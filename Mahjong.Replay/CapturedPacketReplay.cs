using System.Text.Json;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Replay;

/// <summary>Offline diagnostics through the production decoder. Never certifies a protocol or executes game actions.</summary>
public static class CapturedPacketReplay
{
    public static PacketReplayReport Read(string text, MahjongProtocolProfile profile, bool rawCapture)
    {
        var rows = new List<JsonElement>();
        if (rawCapture)
        {
            foreach (string line in text.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                using var doc = JsonDocument.Parse(line);
                rows.Add(doc.RootElement.Clone());
            }
            if (rows.Count < 2 || rows[0].GetProperty("e").GetString() != "capture-start"
                || rows[^1].GetProperty("e").GetString() != "capture-end")
                throw new InvalidDataException("Only sealed captures can be replayed.");
            var environment = rows[0].GetProperty("environment");
            if (environment.GetProperty("game_version").GetString() != profile.GameVersion
                || environment.GetProperty("client_variant").GetString() != profile.Variant)
                throw new InvalidDataException("Capture version/variant does not match candidate profile.");
            // The separate audit retains richer transport diagnostics. Any explicit loss blocks this run.
            foreach (string field in new[] { "dropped", "rejected" })
                if (!rows[^1].TryGetProperty(field, out var value) || value.GetInt64() != 0)
                    throw new InvalidDataException($"Capture has missing/nonzero {field} count; run the capture audit.");
            if (!rows[^1].GetProperty("stream_complete").GetBoolean())
                throw new InvalidDataException("Capture stream was not complete.");
            int recordedCount = rows[^1].GetProperty("packets").GetInt32();
            rows = rows.Where(row => row.GetProperty("e").GetString() == "raw-packet").ToList();
            if (rows.Count != recordedCount) throw new InvalidDataException("Capture packet count mismatch.");
        }
        else
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.GetProperty("variant").GetString() != profile.Variant)
                throw new InvalidDataException("Fixture variant does not match candidate profile.");
            rows = doc.RootElement.GetProperty("packets").EnumerateArray().Select(x => x.Clone()).ToList();
        }
        if (rows.Count == 0) throw new InvalidDataException("Empty packet corpus.");

        var hands = new List<PacketReplayHand>();
        var unmapped = new Dictionary<string, int>();
        var decoder = new MahjongPacketMjaiDecoder();
        var reducer = new PublicStateReducer();
        PacketReplayHand? current = null;
        long previousSequence = -1;
        DateTimeOffset? previousTime = null;
        foreach (var row in rows)
        {
            long sequence = row.GetProperty("sequence").GetInt64();
            var time = DateTimeOffset.Parse(row.GetProperty("t").GetString()!);
            if (sequence <= previousSequence || time < previousTime)
                throw new InvalidDataException("Out-of-order or duplicate packet; do not sort away capture defects.");
            previousSequence = sequence; previousTime = time;
            string opcode = row.GetProperty("opcode").GetString()!;
            if (!profile.TryGet(Convert.ToUInt16(opcode[2..], 16), out var spec))
            {
                unmapped[opcode] = unmapped.GetValueOrDefault(opcode) + 1;
                continue;
            }
            if (row.TryGetProperty("message_id", out var named) && named.GetInt32() != spec!.MessageId)
                throw new InvalidDataException("Fixture message ID conflicts with the candidate opcode map.");
            int message = spec!.MessageId;
            if (message is MahjongPacketMjaiDecoder.MatchStartMessageId or MahjongPacketMjaiDecoder.HandStartMessageId)
            {
                Close(message == MahjongPacketMjaiDecoder.HandStartMessageId ? "next-start-without-result" : "match-reset", sequence);
                decoder = new(); reducer = new();
                if (message == MahjongPacketMjaiDecoder.MatchStartMessageId) continue;
                current = new PacketReplayHand(hands.Count + 1, sequence);
            }
            if (current is null) continue; // Mid-hand capture cannot invent the missing opening.
            current.EndSequence = sequence;
            if (current.Failure is not null) continue; // Quarantine until an observed start, not a timer.
            try
            {
                byte[] payload = Convert.FromHexString(row.GetProperty("payload_hex").GetString()!);
                if (payload.Length != spec.PayloadLength
                    || row.TryGetProperty("payload_length", out var declared) && declared.GetInt32() != payload.Length)
                    throw new InvalidDataException("Mapped packet length mismatch.");
                var events = decoder.Process(message, payload);
                if (events.Count == 0) throw new InvalidDataException("Mapped packet emitted no event.");
                foreach (var evt in events)
                {
                    reducer.Apply(evt, knownCounters: false);
                    if (evt is not MjaiStartGame && !reducer.Complete)
                        throw new InvalidDataException(reducer.Failure ?? "Incomplete public history.");
                    current.Events.Add(new PacketReplayEvent(sequence, time,
                        JsonSerializer.SerializeToElement(evt, evt.GetType())));
                }
                if (events.Any(evt => evt is MjaiEndKyoku)) Close("result", sequence);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
            {
                current!.Failure = ex.Message;
                current.FailureSequence = sequence;
            }
        }
        Close("capture-end-without-result", previousSequence);
        if (hands.Count == 0) throw new InvalidDataException("No observed hand start.");
        return new PacketReplayReport(1, false,
            rawCapture ? "mapped raw-capture packets" : "selected fixture packets",
            ["Offline compatibility diagnostics only; not live protocol approval or win-rate evaluation.",
             "Current decoder assumes start kyotaku=0; this is not observed evidence.",
             "Unmapped packets are inventoried, not proven irrelevant. Missing result boundaries are not synthesized.",
             "Only hands ending in a mapped result without a decoder/reducer fault are sent to the offline model."],
            unmapped, hands);

        void Close(string boundary, long sequence)
        {
            if (current is null) return;
            current.Boundary = boundary;
            current.EndSequence = sequence;
            hands.Add(current);
            current = null;
        }
    }
}

public sealed record PacketReplayEvent(long PacketSequence, DateTimeOffset At, JsonElement Event);
public sealed class PacketReplayHand(int number, long startSequence)
{
    public int Number { get; } = number;
    public long StartSequence { get; } = startSequence;
    public long EndSequence { get; set; }
    public string Boundary { get; set; } = "open";
    public string? Failure { get; set; }
    public long? FailureSequence { get; set; }
    public List<PacketReplayEvent> Events { get; } = [];
    public bool OfflineModelEligible => Failure is null && Boundary == "result";
}
public sealed record PacketReplayReport(int Schema, bool RuntimeEligible, string Scope, string[] Caveats,
    Dictionary<string, int> UnmappedOpcodes, List<PacketReplayHand> Hands);

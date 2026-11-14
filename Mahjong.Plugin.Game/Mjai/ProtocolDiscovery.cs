namespace Mahjong.Plugin.Game.Mjai;

public readonly record struct DiscoveryPacket(ushort Opcode, byte[] Payload);

/// <summary>Lexicographic replay score; higher is better in field order.</summary>
public readonly record struct DiscoveryScore(int CompleteHands, int FailedHands, int DrawDiscardEvents, int Imbalance, int ScoreMismatches)
{
    public int CompareTo(DiscoveryScore other)
    {
        int c = CompleteHands.CompareTo(other.CompleteHands);
        if (c != 0) return c;
        if ((c = other.FailedHands.CompareTo(FailedHands)) != 0) return c;
        if ((c = other.ScoreMismatches.CompareTo(ScoreMismatches)) != 0) return c;
        if ((c = DrawDiscardEvents.CompareTo(other.DrawDiscardEvents)) != 0) return c;
        return other.Imbalance.CompareTo(Imbalance);
    }
}

public sealed record ProtocolDiscoveryResult(bool Success, string Reason,
    IReadOnlyDictionary<string, MahjongPacketSpec> Packets, DiscoveryScore Score, int CombinationsTried);

/// <summary>
/// Recovers the opcode for each Doman message after a client patch reshuffles opcodes. Payload lengths
/// are stable, so every same-length opcode seen at the table is a candidate; each combination is replayed
/// through the production decoder and reducer. Only a unique best combination with enough clean hands is
/// accepted. Optional observed hand-start scores (read independently from the game's own table state)
/// must match every decoded opening.
/// </summary>
public static class ProtocolDiscovery
{
    public static readonly IReadOnlyDictionary<int, int> PayloadLengths = new Dictionary<int, int>
    {
        [MahjongPacketMjaiDecoder.MatchStartMessageId] = 48,
        [MahjongPacketMjaiDecoder.HandStartMessageId] = 104,
        [MahjongPacketMjaiDecoder.DrawOrCallMessageId] = 24,
        [MahjongPacketMjaiDecoder.DiscardMessageId] = 32,
        [MahjongPacketMjaiDecoder.HandResultAMessageId] = 256,
        [MahjongPacketMjaiDecoder.HandResultBMessageId] = 504,
        [MahjongPacketMjaiDecoder.DrawResultMessageId] = 264,
    };

    // Result messages may legitimately be absent from a match (e.g. no exhaustive draw).
    private static readonly HashSet<int> Optional =
    [
        MahjongPacketMjaiDecoder.HandResultAMessageId,
        MahjongPacketMjaiDecoder.HandResultBMessageId,
        MahjongPacketMjaiDecoder.DrawResultMessageId,
    ];

    public const int MinimumCompleteHands = 2;
    public const int MinimumDrawDiscardEvents = 60;
    public const int MaxCombinations = 4096;

    public static bool IsCandidateLength(int length) => PayloadLengths.Values.Contains(length);

    public static ProtocolDiscoveryResult Discover(IReadOnlyList<DiscoveryPacket> packets,
        IReadOnlyDictionary<string, MahjongPacketSpec>? known = null,
        IReadOnlyCollection<int[]>? observedHandScores = null)
    {
        ArgumentNullException.ThrowIfNull(packets);
        var empty = new Dictionary<string, MahjongPacketSpec>();
        var knownByMessage = (known ?? empty).ToDictionary(kv => kv.Value.MessageId, kv => Parse(kv.Key));
        var messages = PayloadLengths.Keys.ToArray();
        var candidates = new List<ushort?[]>();
        long combinations = 1;
        foreach (int message in messages)
        {
            ushort?[] options;
            if (knownByMessage.TryGetValue(message, out ushort fixedOpcode))
                options = [fixedOpcode];
            else
            {
                var seen = packets.Where(p => p.Payload.Length == PayloadLengths[message])
                    .Select(p => (ushort?)p.Opcode).Distinct().OrderBy(o => o).ToList();
                if (Optional.Contains(message)) seen.Add(null);
                if (seen.Count == 0)
                    return Fail($"No {PayloadLengths[message]}-byte packets for message {message}", empty, default, 0);
                options = [.. seen];
            }
            candidates.Add(options);
            combinations *= options.Length;
        }
        if (combinations > MaxCombinations)
            return Fail($"{combinations} candidate combinations exceed the {MaxCombinations} limit", empty, default, 0);

        var scored = new List<(DiscoveryScore Score, Dictionary<string, MahjongPacketSpec> Map)>();
        foreach (var combo in Product(candidates))
        {
            var used = combo.Where(o => o is not null).ToArray();
            if (used.Distinct().Count() != used.Length) continue;
            var map = new Dictionary<string, MahjongPacketSpec>();
            for (int i = 0; i < messages.Length; i++)
                if (combo[i] is ushort opcode)
                    map[$"0x{opcode:X4}"] = new MahjongPacketSpec(messages[i], PayloadLengths[messages[i]]);
            scored.Add((Score(packets, map, observedHandScores), map));
        }
        if (scored.Count == 0) return Fail("No distinct opcode combination", empty, default, 0);
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var best = scored[0];
        if (scored.Count > 1 && scored[1].Score.CompareTo(best.Score) == 0)
            return Fail("Ambiguous: several opcode combinations replay equally well", best.Map, best.Score, scored.Count);
        var s = best.Score;
        if (s.CompleteHands < MinimumCompleteHands)
            return Fail($"Only {s.CompleteHands} clean hands; need {MinimumCompleteHands}", best.Map, s, scored.Count);
        if (s.FailedHands > 0)
            return Fail($"{s.FailedHands} hands failed to decode under the best mapping", best.Map, s, scored.Count);
        if (s.DrawDiscardEvents < MinimumDrawDiscardEvents)
            return Fail($"Only {s.DrawDiscardEvents} draw/discard events; need {MinimumDrawDiscardEvents}", best.Map, s, scored.Count);
        if (s.Imbalance * 10 > s.DrawDiscardEvents)
            return Fail("Draw and discard counts do not balance", best.Map, s, scored.Count);
        if (s.ScoreMismatches > 0)
            return Fail($"{s.ScoreMismatches} decoded openings disagree with the game's table scores", best.Map, s, scored.Count);
        return new ProtocolDiscoveryResult(true, "Unique mapping", best.Map, s, scored.Count);
    }

    internal static DiscoveryScore Score(IReadOnlyList<DiscoveryPacket> packets,
        IReadOnlyDictionary<string, MahjongPacketSpec> map, IReadOnlyCollection<int[]>? observedHandScores)
    {
        var profile = new MahjongProtocolProfile("discovery", "discovery", false, "discovery",
            new Dictionary<string, MahjongPacketSpec>(map));
        var decoder = new MahjongPacketMjaiDecoder();
        var reducer = new PublicStateReducer();
        bool open = false, failed = false;
        int complete = 0, failedHands = 0, draws = 0, discards = 0, calls = 0, mismatches = 0;
        foreach (var packet in packets)
        {
            if (!profile.TryGet(packet.Opcode, out var spec) || spec is null || packet.Payload.Length != spec.PayloadLength)
                continue;
            int message = spec.MessageId;
            if (message is MahjongPacketMjaiDecoder.MatchStartMessageId or MahjongPacketMjaiDecoder.HandStartMessageId)
            {
                if (open && failed) failedHands++;
                open = failed = false;
                decoder = new MahjongPacketMjaiDecoder();
                reducer = new PublicStateReducer();
                if (message == MahjongPacketMjaiDecoder.MatchStartMessageId) continue;
                open = true;
            }
            if (!open || failed) continue;
            try
            {
                var events = decoder.Process(message, packet.Payload);
                if (events.Count == 0) throw new InvalidDataException("Mapped packet emitted no event");
                foreach (var evt in events)
                {
                    reducer.Apply(evt, knownCounters: false);
                    if (evt is not MjaiStartGame && !reducer.Complete)
                        throw new InvalidDataException(reducer.Failure ?? "Incomplete public history");
                    switch (evt)
                    {
                        case MjaiTsumo: draws++; break;
                        case MjaiDahai: discards++; break;
                        case MjaiOpenCall: calls++; break;
                        case MjaiStartKyoku start when observedHandScores is { Count: > 0 }
                            && !observedHandScores.Any(o => SameScores(o, start.Scores)):
                            mismatches++; break;
                    }
                }
                if (events.Any(e => e is MjaiEndKyoku)) { complete++; open = false; }
            }
#pragma warning disable CA1031 // A wrong candidate mapping feeds arbitrary bytes; any decoder fault only lowers its score.
            catch (Exception)
#pragma warning restore CA1031
            {
                failed = true;
            }
        }
        if (open && failed) failedHands++;
        return new DiscoveryScore(complete, failedHands, draws + discards, Math.Abs(discards - draws - calls), mismatches);
    }

    /// <summary>Table state may store raw points or hundreds; seat order may differ from the relative MJAI order.</summary>
    public static bool SameScores(int[] observed, int[] decoded)
    {
        if (observed.Length != 4 || decoded.Length != 4) return false;
        var a = observed.Order().ToArray();
        var b = decoded.Order().ToArray();
        return a.SequenceEqual(b) || a.Select(x => x * 100).SequenceEqual(b);
    }

    private static ProtocolDiscoveryResult Fail(string reason, IReadOnlyDictionary<string, MahjongPacketSpec> map,
        DiscoveryScore score, int tried) => new(false, reason, map, score, tried);

    private static ushort Parse(string key) =>
        Convert.ToUInt16(key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key, 16);

    private static IEnumerable<ushort?[]> Product(List<ushort?[]> lists)
    {
        IEnumerable<ushort?[]> acc = [[]];
        foreach (var list in lists)
            acc = acc.SelectMany(prefix => list.Select(item => (ushort?[])[.. prefix, item]));
        return acc;
    }
}

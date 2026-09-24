using System.Buffers.Binary;
using System.Text.Json;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Replay.Tests;

public class CapturedPacketReplayTests
{
    private static MahjongProtocolProfile Profile() => JsonSerializer.Deserialize<MahjongProtocolProfile>(
        File.ReadAllText(RepoPathResolver.Resolve("data", "protocols", "20260915-international-candidate.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Theory]
    [InlineData("20260924-full-match.json", 5, 5)]
    [InlineData("20260924-added-kan-match.json", 5, 4)]
    [InlineData("20260924-draw-result-match.json", 8, 8)]
    public void Existing_real_corpus_replays_without_hidden_opponent_tiles_or_protocol_approval(string file, int hands, int eligible)
    {
        var result = CapturedPacketReplay.Read(File.ReadAllText(RepoPathResolver.Resolve(
            "tests", "Mahjong.Plugin.Game.Tests", "RegressionFixtures", file)), Profile(), false);
        Assert.Equal(hands, result.Hands.Count);
        Assert.Equal(eligible, result.Hands.Count(h => h.OfflineModelEligible));
        Assert.False(result.RuntimeEligible);
        Assert.Contains(result.Caveats, text => text.Contains("kyotaku=0"));
        foreach (var row in result.Hands.SelectMany(h => h.Events))
        {
            var e = row.Event;
            if (e.GetProperty("type").GetString() == "start_kyoku")
                Assert.All(e.GetProperty("tehais").EnumerateArray().Skip(1).SelectMany(h => h.EnumerateArray()),
                    tile => Assert.Equal("?", tile.GetString()));
            if (e.GetProperty("type").GetString() == "tsumo" && e.GetProperty("actor").GetInt32() != 0)
                Assert.Equal("?", e.GetProperty("pai").GetString());
        }
    }

    [Fact]
    public void Unsupported_event_quarantines_the_hand_until_next_real_start()
    {
        var call = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(call.AsSpan(4), 0x200);
        var report = Fixture(Packet(1, "0x02BB", call), Packet(2, "0x0133", Start()),
            Packet(3, "0x02BB", call), Packet(4, "0x0371", new byte[256]),
            Packet(5, "0x0133", Start()), Packet(6, "0x0371", new byte[256]));
        Assert.Equal(2, report.Hands.Count);
        Assert.False(report.Hands[0].OfflineModelEligible);
        Assert.Equal(3, report.Hands[0].FailureSequence);
        Assert.Contains("0x200", report.Hands[0].Failure);
        Assert.DoesNotContain(report.Hands[0].Events, e => e.PacketSequence >= 3);
        Assert.True(report.Hands[1].OfflineModelEligible);
    }

    [Fact]
    public void Missing_result_is_recorded_not_synthesized_as_a_complete_hand()
    {
        var report = Fixture(Packet(1, "0x0133", Start()), Packet(2, "0xF00D", new byte[264]),
            Packet(3, "0x0133", Start()));
        Assert.Equal("next-start-without-result", report.Hands[0].Boundary);
        Assert.Equal("capture-end-without-result", report.Hands[1].Boundary);
        Assert.All(report.Hands, hand => Assert.False(hand.OfflineModelEligible));
        Assert.Equal(1, report.UnmappedOpcodes["0xF00D"]);
        Assert.DoesNotContain(report.Hands.SelectMany(h => h.Events), e => e.Event.GetProperty("type").GetString() == "end_kyoku");
    }

    [Fact]
    public void Wrong_length_cannot_become_a_model_input()
    {
        var report = Fixture(Packet(1, "0x0133", new byte[103]), Packet(2, "0x0371", new byte[256]));
        Assert.False(report.Hands[0].OfflineModelEligible);
        Assert.Empty(report.Hands[0].Events);
        Assert.Contains("length", report.Hands[0].Failure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Duplicate_or_reordered_records_are_rejected(long secondSequence) =>
        Assert.Throws<InvalidDataException>(() => Fixture(Packet(1, "0x0133", Start()), Packet(secondSequence, "0x0133", Start())));

    [Theory]
    [InlineData(0, true, 1, true)]
    [InlineData(1, true, 1, false)]
    [InlineData(0, false, 1, false)]
    [InlineData(0, true, 2, false)]
    public void Raw_capture_checks_loss_seal_and_counts(int dropped, bool complete, int count, bool accepted)
    {
        string text = string.Join('\n',
            JsonSerializer.Serialize(new { e = "capture-start", environment = new { game_version = Profile().GameVersion, client_variant = "Emj" } }),
            JsonSerializer.Serialize(Packet(1, "0x0133", Start())),
            JsonSerializer.Serialize(new { e = "capture-end", dropped, rejected = 0, stream_complete = complete, packets = count }));
        if (accepted) Assert.Single(CapturedPacketReplay.Read(text, Profile(), true).Hands);
        else Assert.Throws<InvalidDataException>(() => CapturedPacketReplay.Read(text, Profile(), true));
    }

    [Fact]
    public void Empty_corpus_and_variant_mismatch_fail()
    {
        Assert.Throws<InvalidDataException>(() => Fixture());
        Assert.Throws<InvalidDataException>(() => CapturedPacketReplay.Read(
            JsonSerializer.Serialize(new { variant = "EmjL", packets = new[] { Packet(1, "0x0133", Start()) } }), Profile(), false));
    }

    private static PacketReplayReport Fixture(params object[] packets) => CapturedPacketReplay.Read(
        JsonSerializer.Serialize(new { variant = "Emj", packets }), Profile(), false);
    private static object Packet(long sequence, string opcode, byte[] payload) => new
    {
        e = "raw-packet", sequence, t = DateTimeOffset.UnixEpoch.AddSeconds(sequence), opcode,
        payload_hex = Convert.ToHexString(payload), payload_length = payload.Length,
    };
    private static byte[] Start()
    {
        var payload = new byte[104];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(28), 31);
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(32 + i * 4), 250);
        for (int i = 0; i < 13; i++) BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(48 + i * 4), i);
        return payload;
    }
}

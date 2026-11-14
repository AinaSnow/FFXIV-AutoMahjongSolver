using System.Text.Json;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Game.Tests;

public class ProtocolDiscoveryTests
{
    // Pretend a patch reshuffled every opcode: the 2026-09-15 values are replaced with unseen ones.
    private static readonly Dictionary<int, ushort> Patched = new()
    {
        [MahjongPacketMjaiDecoder.MatchStartMessageId] = 0x0301,
        [MahjongPacketMjaiDecoder.HandStartMessageId] = 0x0302,
        [MahjongPacketMjaiDecoder.DrawOrCallMessageId] = 0x0303,
        [MahjongPacketMjaiDecoder.DiscardMessageId] = 0x0304,
        [MahjongPacketMjaiDecoder.HandResultAMessageId] = 0x0305,
        [MahjongPacketMjaiDecoder.HandResultBMessageId] = 0x0306,
        [MahjongPacketMjaiDecoder.DrawResultMessageId] = 0x0307,
    };

    private static List<DiscoveryPacket> Fixture(string file, bool decoys = true)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RegressionFixtures", file)));
        var random = new Random(20261114);
        var packets = new List<DiscoveryPacket>();
        foreach (var row in doc.RootElement.GetProperty("packets").EnumerateArray())
        {
            int message = row.GetProperty("message_id").GetInt32();
            packets.Add(new(Patched[message], Convert.FromHexString(row.GetProperty("payload_hex").GetString()!)));
            if (!decoys) continue;
            // Same-length unrelated traffic, like 0x02CD/0x025F/0x0152 in real captures.
            if (random.Next(10) == 0) packets.Add(new(0x02CD, Bytes(random, 104)));
            if (random.Next(8) == 0) packets.Add(new(0x025F, Bytes(random, 24)));
            if (random.Next(4) == 0) packets.Add(new(0x0152, Bytes(random, 32)));
        }
        return packets;
    }

    private static byte[] Bytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    [Theory]
    [InlineData("20260924-full-match.json")]
    [InlineData("20260924-draw-result-match.json")]
    [InlineData("20260924-added-kan-match.json")]
    public void Recovers_reshuffled_opcodes_among_same_length_decoys(string file)
    {
        var result = ProtocolDiscovery.Discover(Fixture(file));
        Assert.True(result.Success, result.Reason);
        foreach (var (opcode, spec) in result.Packets)
            Assert.Equal(Patched[spec.MessageId], Convert.ToUInt16(opcode[2..], 16));
        foreach (int core in new[] { 636, 637, 638, 641 })
            Assert.Contains(result.Packets.Values, spec => spec.MessageId == core);
        Assert.Equal(0, result.Score.FailedHands);
    }

    [Fact]
    public void Too_little_evidence_is_refused()
    {
        var packets = Fixture("20260924-full-match.json", decoys: false);
        int secondHand = packets.FindIndex(1, p => p.Opcode == Patched[MahjongPacketMjaiDecoder.HandStartMessageId]);
        var result = ProtocolDiscovery.Discover(packets.Take(secondHand + 1).ToList());
        Assert.False(result.Success);
    }

    [Fact]
    public void Table_scores_that_disagree_block_the_mapping()
    {
        var wrong = new[] { new[] { 1, 2, 3, 4 } };
        var result = ProtocolDiscovery.Discover(Fixture("20260924-full-match.json"), observedHandScores: wrong);
        Assert.False(result.Success);
        Assert.Contains("table scores", result.Reason);
    }

    [Fact]
    public void Previously_learned_opcodes_are_kept_and_new_results_can_be_added()
    {
        var known = new Dictionary<string, MahjongPacketSpec>
        {
            ["0x0301"] = new(636, 48), ["0x0302"] = new(637, 104), ["0x0303"] = new(638, 24), ["0x0304"] = new(641, 32),
        };
        var result = ProtocolDiscovery.Discover(Fixture("20260924-draw-result-match.json"), known);
        Assert.True(result.Success, result.Reason);
        Assert.True(result.Packets.ContainsKey("0x0301"));
    }

    [Fact]
    public void Score_comparison_accepts_seat_rotation_and_hundreds()
    {
        Assert.True(ProtocolDiscovery.SameScores([250, 300, 200, 250], [25000, 25000, 30000, 20000]));
        Assert.False(ProtocolDiscovery.SameScores([250, 300, 200, 251], [25000, 25000, 30000, 20000]));
    }
}

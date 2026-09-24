using System.Buffers.Binary;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Game.Tests;

public sealed class MahjongPacketMjaiDecoderTests
{
    [Fact]
    public void Confirmed_packet_sequence_converts_to_player_zero_mjai()
    {
        var decoder = new MahjongPacketMjaiDecoder();
        var actual = new List<IMjaiEvent>();

        actual.AddRange(decoder.Process(636, MatchStart()));
        actual.AddRange(decoder.Process(637, HandStart()));
        actual.AddRange(decoder.Process(638, Draw(0, 0)));
        actual.AddRange(decoder.Process(638, Draw(3, 89)));
        actual.AddRange(decoder.Process(641, Discard(0, 68)));
        actual.AddRange(decoder.Process(638, Call(1, 0x600, 60, 64)));
        actual.AddRange(decoder.Process(641, Discard(3, 89, 0x111)));
        actual.AddRange(decoder.Process(639, new byte[256]));
        actual.AddRange(decoder.Finish());

        Assert.Collection(actual,
            e => Assert.IsType<MjaiStartGame>(e),
            e =>
            {
                var start = Assert.IsType<MjaiStartKyoku>(e);
                Assert.Equal("E", start.Bakaze);
                Assert.Equal(2, start.Kyoku);
                Assert.Equal(1, start.Oya);
                Assert.Equal([22400, 23700, 30200, 23700], start.Scores);
                Assert.Equal(13, start.Tehais[0].Length);
                Assert.All(start.Tehais[1], tile => Assert.Equal("?", tile));
            },
            e => Assert.Equal(new MjaiTsumo(1, "?"), e),
            e => Assert.Equal(new MjaiTsumo(0, "5sr"), e),
            e => Assert.Equal(new MjaiDahai(1, "9p", false), e),
            e =>
            {
                var chi = Assert.IsType<MjaiOpenCall>(e);
                Assert.Equal("chi", chi.Type);
                Assert.Equal(2, chi.Actor);
                Assert.Equal(1, chi.Target);
                Assert.Equal("9p", chi.Pai);
                Assert.Equal(["7p", "8p"], chi.Consumed);
            },
            e => Assert.Equal(new MjaiReach(0), e),
            e => Assert.Equal(new MjaiDahai(0, "5sr", false), e),
            e => Assert.Equal(new MjaiReachAccepted(0), e),
            e => Assert.IsType<MjaiEndKyoku>(e),
            e => Assert.IsType<MjaiEndGame>(e));
    }

    [Theory]
    [InlineData(17, "5mr")]
    [InlineData(16, "5m")]
    [InlineData(53, "5pr")]
    [InlineData(52, "5p")]
    [InlineData(89, "5sr")]
    [InlineData(88, "5s")]
    [InlineData(136, "5mr")]
    [InlineData(140, "5pr")]
    [InlineData(144, "5sr")]
    [InlineData(ushort.MaxValue, "?")]
    public void Physical_tile_decode_preserves_verified_red_five(ushort physical, string expected)
    {
        Assert.Equal(expected, MahjongPacketMjaiDecoder.DecodePhysicalTile(physical));
    }

    [Theory]
    [InlineData(4, "5m")]
    [InlineData(34, "5mr")]
    [InlineData(35, "5pr")]
    [InlineData(36, "5sr")]
    [InlineData(0x104, "5mr")]
    [InlineData(0x10D, "5pr")]
    [InlineData(0x116, "5sr")]
    [InlineData(37, "?")]
    public void Hand_start_tile_kind_decode_preserves_red_fives(int id, string expected)
    {
        Assert.Equal(expected, MahjongPacketMjaiDecoder.DecodeTileKind(id));
    }

    [Fact]
    public void Hand_start_with_red_five_never_sends_unknown_self_tile()
    {
        var decoder = new MahjongPacketMjaiDecoder();
        var payload = HandStart();
        WriteInt32(payload, 48, 34);

        var start = Assert.Single(decoder.Process(637, payload).OfType<MjaiStartKyoku>());

        Assert.Equal("5mr", start.Tehais[0][0]);
        Assert.DoesNotContain(MjaiTile.Unknown, start.Tehais[0]);
    }

    [Fact]
    public void Hand_start_retains_unknown_raw_tile_kind_for_live_diagnostics()
    {
        var decoder = new MahjongPacketMjaiDecoder();
        var payload = HandStart();
        WriteInt32(payload, 48, 53);

        var start = Assert.Single(decoder.Process(637, payload).OfType<MjaiStartKyoku>());

        Assert.Equal(MjaiTile.Unknown, start.Tehais[0][0]);
        Assert.Equal(53, decoder.LastHandStartTileKinds[0]);
        Assert.Equal(22, decoder.LastHandStartDoraKind);
    }

    [Fact]
    public void Unknown_discard_retains_raw_physical_value_for_live_diagnostics()
    {
        var decoder = new MahjongPacketMjaiDecoder();
        decoder.Process(637, HandStart());

        var discard = Assert.Single(decoder.Process(641, Discard(0, ushort.MaxValue)).OfType<MjaiDahai>());

        Assert.Equal(MjaiTile.Unknown, discard.Pai);
        Assert.Equal(ushort.MaxValue, decoder.LastDiscardPhysical);
        Assert.Equal(0x110u, decoder.LastDiscardAction);
    }

    [Fact]
    public void Roster_and_short_packets_are_ignored()
    {
        var decoder = new MahjongPacketMjaiDecoder();
        Assert.Empty(decoder.Process(642, new byte[576]));
        Assert.Empty(decoder.Process(637, new byte[99]));
        Assert.Empty(decoder.Finish());
    }

    [Theory]
    [InlineData(0x130u)]
    [InlineData(0x200u)]
    [InlineData(0x400u)]
    [InlineData(0xDEADu)]
    public void Unsupported_call_or_draw_does_not_silently_leave_an_incomplete_history(uint action)
    {
        var decoder = new MahjongPacketMjaiDecoder();
        decoder.Process(637, HandStart());
        var payload = Draw(0, 20);
        WriteUInt32(payload, 4, action);
        var error = Assert.Throws<InvalidDataException>(() => decoder.Process(638, payload));
        Assert.Contains($"0x{action:X}", error.Message);
    }

    [Theory]
    [InlineData(0x140u)] // Captured added-kan candidate, not a river discard.
    [InlineData(0x210u)] // Captured post-rinshan candidate, flags still unverified.
    [InlineData(0x212u)]
    [InlineData(0xDEADu)]
    public void Unsupported_discard_does_not_invent_a_tedashi_flag(uint action)
    {
        var decoder = new MahjongPacketMjaiDecoder();
        decoder.Process(637, HandStart());
        Assert.Throws<InvalidDataException>(() => decoder.Process(641, Discard(0, 20, action)));
    }

    [Fact]
    public void Call_without_its_claimed_discard_rejects_incomplete_history()
    {
        var decoder = new MahjongPacketMjaiDecoder();
        decoder.Process(637, HandStart());
        Assert.Throws<InvalidDataException>(() => decoder.Process(638, Call(1, 0x500, 20, 20)));
    }

    [Fact]
    public void Combined_riichi_tsumogiri_flags_preserve_both_events()
    {
        var decoder = new MahjongPacketMjaiDecoder();
        decoder.Process(637, HandStart());
        var events = decoder.Process(641, Discard(3, 60, 0x113));
        Assert.Collection(events,
            e => Assert.Equal(new MjaiReach(0), e),
            e => Assert.Equal(new MjaiDahai(0, "7p", true), e),
            e => Assert.Equal(new MjaiReachAccepted(0), e));
    }

    private static byte[] MatchStart() => new byte[48];

    private static byte[] HandStart()
    {
        var payload = new byte[104];
        WriteInt32(payload, 8, 1);
        WriteInt32(payload, 24, 3);
        WriteUInt32(payload, 28, 0x00010116);
        int[] scores = [237, 302, 237, 224];
        for (int i = 0; i < scores.Length; i++)
            WriteInt32(payload, 32 + i * 4, scores[i]);
        int[] hand = [8, 18, 22, 9, 10, 31, 15, 24, 0, 6, 6, 19, 30];
        for (int i = 0; i < hand.Length; i++)
            WriteInt32(payload, 48 + i * 4, hand[i]);
        return payload;
    }

    private static byte[] Draw(int seat, ushort physical)
    {
        var payload = Enumerable.Repeat((byte)0xff, 24).ToArray();
        WriteInt32(payload, 0, seat);
        WriteUInt32(payload, 4, 0x100);
        WriteUInt16(payload, 8, physical);
        WriteInt32(payload, 20, 1);
        return payload;
    }

    private static byte[] Discard(int seat, ushort physical, uint action = 0x110)
    {
        var payload = Enumerable.Repeat((byte)0xff, 32).ToArray();
        WriteInt32(payload, 0, seat);
        WriteUInt32(payload, 4, 1);
        WriteUInt32(payload, 8, action);
        WriteUInt16(payload, 12, physical);
        WriteInt32(payload, 24, 1);
        return payload;
    }

    private static byte[] Call(int seat, uint action, ushort first, ushort second)
    {
        var payload = Enumerable.Repeat((byte)0xff, 24).ToArray();
        WriteInt32(payload, 0, seat);
        WriteUInt32(payload, 4, action);
        WriteUInt16(payload, 12, first);
        WriteUInt16(payload, 14, second);
        WriteInt32(payload, 20, 0);
        return payload;
    }

    private static void WriteUInt16(byte[] target, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset), value);

    private static void WriteInt32(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset), value);

    private static void WriteUInt32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset), value);
}

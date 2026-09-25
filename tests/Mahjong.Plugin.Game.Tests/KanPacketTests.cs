using System.Buffers.Binary;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Game.Tests;

public sealed class KanPacketTests
{
    // Anonymous state-equivalent packets; no copied capture, account or player data.
    [Theory]
    [InlineData(0, 0x130)] [InlineData(1, 0x130)] [InlineData(2, 0x130)] [InlineData(3, 0x130)]
    [InlineData(0, 0x400)] [InlineData(1, 0x400)] [InlineData(2, 0x400)] [InlineData(3, 0x400)]
    [InlineData(0, 0x140)] [InlineData(1, 0x140)] [InlineData(2, 0x140)] [InlineData(3, 0x140)]
    public void Opponent_kans_preserve_red_identity_relative_seat_and_public_dora(int self, int action)
    {
        var decoder = new MahjongPacketMjaiDecoder();
        var state = new PublicStateReducer();
        List<IMjaiEvent> history = [];
        void Send(int id, byte[] bytes)
        {
            foreach (var e in decoder.Process(id, bytes)) { history.Add(e); state.Apply(e); }
            Assert.True(state.Complete, state.Failure);
        }
        Send(637, Start(self));
        int actor = (self + 1) % 4, target = (self + 2) % 4;
        if (action == 0x130)
        {
            Send(638, Draw(actor));
            Send(638, Call(actor, 0x130, 88, 88, 88, 89));
        }
        else
        {
            Send(641, Discard(target, 89));
            Send(638, Call(actor, action == 0x400 ? 0x400u : 0x500u, 88, 88, 88));
            if (action == 0x140)
            {
                Send(641, Discard(actor, 0, 0xA10));
                Send(638, Draw(actor));
                Send(641, Added(actor, 88));
            }
        }
        var before = state.Snapshot(LegalActions.None);
        Assert.Single(before.Seats[1].Melds);
        int eventCount = history.Count;
        Send(638, Replacement(actor, 128));
        Assert.Equal(new MjaiDora("F"), history[eventCount]);
        Assert.Equal(new MjaiTsumo(1, "?"), history[eventCount + 1]);
        Send(641, Discard(actor, 120, 0x212));
        var after = state.Snapshot(LegalActions.None);
        Assert.Equal(before.WallRemaining - 1, after.WallRemaining);
        Assert.Equal(new byte[] { 31, 32 }, after.DoraIndicators.Select(t => t.Id));
        Assert.False(after.Seats[1].DiscardIsTedashi.Last());
        Assert.Equal(MeldKindFor(action), Assert.Single(after.Seats[1].Melds).Kind);
        string[] tiles = action switch
        {
            0x130 => Assert.Single(history.OfType<MjaiAnkan>()).Consumed,
            0x400 => [Assert.Single(history.OfType<MjaiDaiminkan>()).Pai, .. Assert.Single(history.OfType<MjaiDaiminkan>()).Consumed],
            _ => [Assert.Single(history.OfType<MjaiKakan>()).Pai, .. Assert.Single(history.OfType<MjaiKakan>()).Consumed],
        };
        Assert.Equal(4, tiles.Length);
        Assert.Equal(1, tiles.Count(t => t == "5sr"));
        Assert.All(history.OfType<MjaiTsumo>(), draw => Assert.Equal("?", draw.Pai));
    }

    [Fact]
    public void Consecutive_added_kan_uses_new_tile_not_stale_slot_and_keeps_duplicate_markers()
    {
        var d = new MahjongPacketMjaiDecoder(); d.Process(637, Start());
        Pon(d, 1, 104); Pon(d, 1, 116);
        d.Process(638, Draw(1));
        Assert.Equal("9s", Assert.IsType<MjaiKakan>(Assert.Single(d.Process(641, Added(1, 104)))).Pai);
        d.Process(638, Replacement(1, 24));
        var next = Added(1, 116, 0x240); U16(next, 18, 104);
        var kan = Assert.IsType<MjaiKakan>(Assert.Single(d.Process(641, next)));
        Assert.Equal("W", kan.Pai); Assert.Equal(new[] { "W", "W", "W" }, kan.Consumed);
        Assert.Equal(new MjaiDora("7m"), d.Process(638, Replacement(1, 24))[0]);
        Assert.Equal(new MjaiDahai(1, "E", false), Assert.Single(d.Process(641, Discard(1, 108, 0x210))));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Missing_wrong_or_duplicate_replacement_cannot_continue_the_hand(int scenario)
    {
        var d = new MahjongPacketMjaiDecoder(); d.Process(637, Start());
        d.Process(638, Draw(1)); d.Process(638, Call(1, 0x130, 124, 124, 124, 124));
        int id = 638;
        byte[] packet = scenario switch
        {
            0 => Replacement(2, 8), 1 => Replacement(1, ushort.MaxValue),
            2 => Draw(2), 3 => Discard(1, 8, 0x210), _ => Replacement(1, 8),
        };
        if (scenario == 3) id = 641;
        if (scenario == 4) d.Process(638, packet);
        Assert.Throws<InvalidDataException>(() => d.Process(id, packet));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Kan_requires_matching_draw_quad_and_unique_red_tile(int scenario)
    {
        var d = new MahjongPacketMjaiDecoder(); d.Process(637, Start());
        if (scenario != 0) d.Process(638, Draw(scenario == 1 ? 2 : 1));
        var packet = scenario == 2 ? Call(1, 0x130, 88, 88, 88, 84)
            : scenario == 3 ? Call(1, 0x130, 88, 88, 89, 89) : Call(1, 0x130, 88, 88, 88, 89);
        Assert.Throws<InvalidDataException>(() => d.Process(638, packet));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Added_kan_requires_matching_actor_pon_and_draw_phase(int scenario)
    {
        var d = new MahjongPacketMjaiDecoder(); d.Process(637, Start());
        if (scenario != 0) Pon(d, scenario == 1 ? 2 : 1, 124);
        d.Process(638, Draw(1));
        Assert.Throws<InvalidDataException>(() => d.Process(641, Added(1, 124, scenario == 2 ? 0x240u : 0x140u)));
    }

    [Fact]
    public void Robbed_kan_can_end_without_replacement_and_next_hand_forgets_pending_kan()
    {
        var d = new MahjongPacketMjaiDecoder(); d.Process(637, Start());
        Pon(d, 1, 124); d.Process(638, Draw(1)); d.Process(641, Added(1, 124));
        Assert.IsType<MjaiEndKyoku>(Assert.Single(d.Process(639, new byte[256])));
        d.Process(637, Start());
        Assert.Throws<InvalidDataException>(() => d.Process(638, Replacement(1, 8)));
        Assert.IsType<MjaiTsumo>(Assert.Single(d.Process(638, Draw(1))));
    }

    [Fact]
    public void More_than_four_kans_and_duplicate_quad_are_rejected()
    {
        var d = new MahjongPacketMjaiDecoder(); d.Process(637, Start());
        foreach (ushort tile in new ushort[] { 0, 4, 8, 12 })
        {
            d.Process(638, Draw(1)); d.Process(638, Call(1, 0x130, tile, tile, tile, tile));
            d.Process(638, Replacement(1, 124)); d.Process(641, Discard(1, 120, 0x210));
        }
        d.Process(638, Draw(1));
        Assert.Throws<InvalidDataException>(() => d.Process(638, Call(1, 0x130, 20, 20, 20, 20)));
        Assert.Throws<InvalidDataException>(() => d.Process(638, Call(1, 0x130, 0, 0, 0, 0)));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    public void Self_closed_kan_removes_exact_quad_and_replacement_uses_own_tile(int self, bool redQuad)
    {
        var decoder = new MahjongPacketMjaiDecoder();
        var state = new PublicStateReducer();
        List<IMjaiEvent> history = [];
        void Send(int id, byte[] bytes)
        {
            foreach (var e in decoder.Process(id, bytes)) { history.Add(e); state.Apply(e); }
            Assert.True(state.Complete, state.Failure);
        }
        var opening = Start(self);
        int kind = redQuad ? 4 : 20;
        int[] hand = [kind, kind, redQuad ? 34 : kind, 0, 1, 2, 9, 10, 11, 24, 25, 26, 27];
        for (int i = 0; i < hand.Length; i++) I32(opening, 48 + 4 * i, hand[i]);
        Send(637, opening);
        var draw = Draw(self); U16(draw, 8, (ushort)(kind * 4)); Send(638, draw);
        ushort physical = (ushort)(kind * 4);
        Send(638, Call(self, 0x130, physical, physical, physical, (ushort)(physical + (redQuad ? 1 : 0))));
        var before = state.Snapshot(LegalActions.None);
        Assert.Equal(10, before.Hand.Count);
        Assert.Equal(MeldKind.AnKan, Assert.Single(before.OurMelds).Kind);
        var replacement = Replacement(self, 128);
        ushort drawn = redQuad ? (ushort)24 : (ushort)89;
        U16(replacement, 8, drawn);
        U16(replacement, 12, 120); // Opaque slot must not replace the actual draw.
        Send(638, replacement);
        Assert.Equal(new MjaiDora("F"), history[^2]);
        Assert.Equal(new MjaiTsumo(0, redQuad ? "7m" : "5sr"), history[^1]);
        var after = state.Snapshot(LegalActions.None);
        Assert.Equal(11, after.Hand.Count);
        Assert.Equal(before.WallRemaining - 1, after.WallRemaining);
        Assert.Equal(new byte[] { 31, 32 }, after.DoraIndicators.Select(t => t.Id));
        Assert.Equal(!redQuad, after.HandIsRed[^1]);
        Assert.Equal(1, after.AkaDora);
        Send(641, Discard(self, drawn, 0x212));
        Assert.Equal(new MjaiDahai(0, redQuad ? "7m" : "5sr", true), history[^1]);
        Assert.Equal(10, state.Snapshot(LegalActions.None).Hand.Count);
        Assert.Single(history.OfType<MjaiAnkan>());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Self_replacement_tsumogiri_rejects_wrong_kind_or_red_identity(bool wrongRed)
    {
        var d = new MahjongPacketMjaiDecoder(); d.Process(637, Start()); d.Process(638, Draw(0));
        d.Process(638, Call(0, 0x130, 124, 124, 124, 124));
        var replacement = Replacement(0, 128); U16(replacement, 8, 89);
        d.Process(638, replacement);
        Assert.Throws<InvalidDataException>(() => d.Process(641, Discard(0, wrongRed ? (ushort)88 : (ushort)24, 0x212)));
    }

    [Fact]
    public void Unknown_self_replacement_is_rejected_before_publishing_dora()
    {
        var d = new MahjongPacketMjaiDecoder(); d.Process(637, Start()); d.Process(638, Draw(0));
        d.Process(638, Call(0, 0x130, 124, 124, 124, 124));
        var replacement = Replacement(0, 128); U16(replacement, 8, ushort.MaxValue);
        Assert.Throws<InvalidDataException>(() => d.Process(638, replacement));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Self_open_and_added_kan_remain_outside_captured_scope(bool added)
    {
        var d = new MahjongPacketMjaiDecoder(); d.Process(637, Start());
        if (added)
        {
            Pon(d, 0, 124); d.Process(638, Draw(0));
            var ex = Assert.Throws<InvalidDataException>(() => d.Process(641, Added(0, 124)));
            Assert.Contains("self open/added kan", ex.Message);
        }
        else
        {
            d.Process(641, Discard(1, 124));
            var ex = Assert.Throws<InvalidDataException>(() => d.Process(638, Call(0, 0x400, 124, 124, 124)));
            Assert.Contains("self open/added kan", ex.Message);
        }
    }

    private static MeldKind MeldKindFor(int action) => action == 0x130 ? MeldKind.AnKan : action == 0x400 ? MeldKind.MinKan : MeldKind.ShouMinKan;
    private static void Pon(MahjongPacketMjaiDecoder d, int actor, ushort tile)
    {
        d.Process(641, Discard(actor == 1 ? 2 : 1, tile));
        d.Process(638, Call(actor, 0x500, tile, tile));
        d.Process(641, Discard(actor, 120, 0xA10));
    }
    private static byte[] Start(int self = 0)
    {
        var p = new byte[104]; I32(p, 24, self); I32(p, 28, 31);
        for (int i = 0; i < 4; i++) I32(p, 32 + i * 4, 250);
        for (int i = 0; i < 13; i++) I32(p, 48 + i * 4, i);
        return p;
    }
    private static byte[] Draw(int actor)
    {
        var p = new byte[24]; I32(p, 0, actor); I32(p, 4, 0x100); return p;
    }
    private static byte[] Call(int actor, uint action, params ushort[] tiles)
    {
        var p = Draw(actor); U32(p, 4, action);
        for (int i = 0; i < tiles.Length; i++) U16(p, 12 + 2 * i, tiles[i]);
        return p;
    }
    private static byte[] Replacement(int actor, ushort marker)
    {
        var p = Draw(actor); I32(p, 4, 0x200); U16(p, 10, marker); return p;
    }
    private static byte[] Discard(int actor, ushort tile, uint action = 0x110)
    {
        var p = new byte[32]; I32(p, 0, actor); U32(p, 8, action); U16(p, 12, tile); return p;
    }
    private static byte[] Added(int actor, ushort tile, uint action = 0x140)
    {
        var p = Discard(actor, 0, action); U16(p, 16, tile); return p;
    }
    private static void I32(byte[] p, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(offset), value);
    private static void U32(byte[] p, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(offset), value);
    private static void U16(byte[] p, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(offset), value);
}

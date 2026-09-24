using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class CallPromptNormalizationTests
{
    [Fact]
    public void Thirteen_tile_stale_riichi_row_is_not_actionable()
    {
        var scanned = Legal(ActionFlags.Riichi | ActionFlags.Pass);

        var normalized = BaseEmjVariant.NormalizeCallPromptForHandShape(13, scanned);

        Assert.Equal(ActionFlags.None, normalized.Flags);
    }

    [Fact]
    public void Thirteen_tile_reaction_offer_is_preserved()
    {
        var scanned = Legal(ActionFlags.Ron | ActionFlags.Riichi | ActionFlags.Pass);

        var normalized = BaseEmjVariant.NormalizeCallPromptForHandShape(13, scanned);

        Assert.Equal(ActionFlags.Ron | ActionFlags.Pass, normalized.Flags);
    }

    [Fact]
    public void Fourteen_tile_self_turn_offer_is_unchanged()
    {
        var scanned = Legal(ActionFlags.Discard | ActionFlags.Riichi | ActionFlags.Pass);

        var normalized = BaseEmjVariant.NormalizeCallPromptForHandShape(14, scanned);

        Assert.Equal(scanned, normalized);
    }

    [Theory]
    [InlineData(ActionFlags.Ron | ActionFlags.Pass, 1.5)]
    [InlineData(ActionFlags.Chi | ActionFlags.Pass, 5.0)]
    [InlineData(ActionFlags.Discard, 4.0)]
    public void Mortal_timeout_prioritizes_short_lived_windows(ActionFlags flags, double seconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(seconds),
            AutoPlayLoop.MortalDecisionTimeout(flags));
    }

    [Fact]
    public void Mortal_wait_key_ignores_unrelated_table_progress()
    {
        var tile = Tile.FromId(4);
        var legal = new LegalActions(ActionFlags.Pon | ActionFlags.Pass, [], [], [], []);
        var first = StateSnapshot.Empty with
        {
            AddonStateCode = 15,
            Hand = [tile, tile],
            HandIsRed = [false, false],
            Legal = legal,
            WallRemaining = 40,
            TurnIndex = 3,
        };
        var later = first with
        {
            WallRemaining = 37,
            TurnIndex = 6,
            Scores = [24000, 26000, 25000, 25000],
        };

        Assert.Equal(
            AutoPlayLoop.ComputeMortalDecisionKey(first),
            AutoPlayLoop.ComputeMortalDecisionKey(later));
    }

    [Fact]
    public void Transient_tsumo_is_carried_into_same_open_hand_discard_snapshot()
    {
        var hand = Tiles.Parse("67p11z8p").ToArray();
        var pending = StateSnapshot.Empty with
        {
            AddonStateCode = 15,
            Hand = hand,
            HandIsRed = new bool[hand.Length],
            WallRemaining = 7,
            Legal = Legal(ActionFlags.Tsumo | ActionFlags.Pass),
        };
        var current = pending with
        {
            AddonStateCode = 6,
            Legal = Legal(ActionFlags.Discard),
        };

        var carried = AutoPlayLoop.CarryForwardTerminalWin(pending, current);

        Assert.Equal(
            ActionFlags.Discard | ActionFlags.Tsumo | ActionFlags.Pass,
            carried.Legal.Flags);
    }

    [Fact]
    public void Transient_tsumo_is_not_carried_after_wall_changes()
    {
        var hand = Tiles.Parse("67p11z8p").ToArray();
        var pending = StateSnapshot.Empty with
        {
            Hand = hand,
            HandIsRed = new bool[hand.Length],
            WallRemaining = 7,
            Legal = Legal(ActionFlags.Tsumo | ActionFlags.Pass),
        };
        var current = pending with
        {
            WallRemaining = 6,
            Legal = Legal(ActionFlags.Discard),
        };

        var carried = AutoPlayLoop.CarryForwardTerminalWin(pending, current);

        Assert.Equal(ActionFlags.Discard, carried.Legal.Flags);
    }

    [Fact]
    public void Chi_variant_selection_honors_mortal_consumed_tiles()
    {
        var sixSou = Tile.FromId(23);
        var sevenSou = Tile.FromId(24);
        var eightSou = Tile.FromId(25);
        var preferred = new MeldCandidate(MeldKind.Chi, eightSou, [sixSou, sevenSou], 3);
        var snapshot = StateSnapshot.Empty with
        {
            Hand = [Tile.FromId(19), Tile.FromId(20), sixSou, sevenSou],
            HandIsRed = [false, false, false, false],
        };
        IReadOnlyList<int[]> variants =
        [
            [18, 19, 20],
            [23, 24, 25],
        ];

        int selected = AutoPlayLoop.PickBestChiVariantIndex(
            variants, snapshot, preferred, out string note);

        Assert.Equal(1, selected);
        Assert.Equal("Mortal variant 1", note);
    }

    [Theory]
    [InlineData("567m26p45s7777z", 33)]
    [InlineData("77779m778p23788s7z", 6)]
    public void Captured_self_quad_shapes_become_ankan_and_keep_discard(string tiles, int quadId)
    {
        // Exact closed-hand shapes from 2026-09-24 15:06 and 15:31 UTC.
        var hand = Tiles.Parse(tiles).ToArray();
        var legal = BaseEmjVariant.NormalizeSelfKanPrompt(
            Legal(ActionFlags.Discard | ActionFlags.MinKan | ActionFlags.Pass), hand, []);
        Assert.True(legal.Can(ActionFlags.Discard));
        Assert.True(legal.Can(ActionFlags.AnKan));
        Assert.False(legal.Can(ActionFlags.MinKan));
        var candidate = Assert.Single(legal.KanCandidates);
        Assert.Equal(MeldKind.AnKan, candidate.Kind);
        Assert.Equal(4, candidate.HandTiles.Length);
        Assert.All(candidate.HandTiles, tile => Assert.Equal(quadId, tile.Id));
    }

    [Fact]
    public void Added_kan_requires_an_existing_pon_and_a_visible_kan_offer()
    {
        var hand = Tiles.Parse("123m123p123s11z").ToArray();
        var pon = Meld.Pon(Tile.FromId(0), Tile.FromId(0), 2);
        var offered = Legal(ActionFlags.Discard | ActionFlags.MinKan | ActionFlags.Pass);
        var normalized = BaseEmjVariant.NormalizeSelfKanPrompt(offered, hand, [pon]);
        Assert.True(normalized.Can(ActionFlags.ShouMinKan));
        Assert.False(normalized.Can(ActionFlags.MinKan));
        Assert.Single(Assert.Single(normalized.KanCandidates).HandTiles);
        Assert.Empty(BaseEmjVariant.NormalizeSelfKanPrompt(offered, hand, []).KanCandidates);
        var noOffer = Legal(ActionFlags.Discard);
        Assert.Same(noOffer, BaseEmjVariant.NormalizeSelfKanPrompt(noOffer, hand, [pon]));
        var response = Legal(ActionFlags.MinKan | ActionFlags.Pass);
        Assert.Same(response, BaseEmjVariant.NormalizeSelfKanPrompt(response, hand.Take(10).ToArray(), [pon]));
    }

    private static LegalActions Legal(ActionFlags flags) =>
        new(flags, [], [], [], []);
}

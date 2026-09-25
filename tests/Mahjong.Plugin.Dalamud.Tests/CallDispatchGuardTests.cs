using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Game.Mjai;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class CallDispatchGuardTests
{
    private static StateSnapshot Snapshot(ActionFlags flags) => StateSnapshot.Empty with
    {
        HandId = 7, AddonStateCode = 15,
        Hand = Tiles.Parse("234567m123p1112z").ToArray(),
        Legal = new LegalActions(flags, [], [], [], []),
    };

    [Theory]
    [InlineData(ActionFlags.Pon)] [InlineData(ActionFlags.Chi)]
    [InlineData(ActionFlags.Pon | ActionFlags.Chi)]
    public void Pass_uses_fresh_option_order_even_if_old_decision_had_phantom_ron(ActionFlags call)
    {
        var before = Snapshot(call | ActionFlags.Ron | ActionFlags.Pass);
        var observed = before with { Legal = before.Legal with { Flags = call | ActionFlags.Pass } };
        Assert.True(AutoPlayLoop.TryResolveCallOption(before, observed, ActionKind.Pass, out int option));
        Assert.Equal(call == (ActionFlags.Pon | ActionFlags.Chi) ? 2 : 1, option);
        Assert.False(AutoPlayLoop.TryResolveCallOption(before, observed, ActionKind.Ron, out _));
    }

    [Theory]
    [InlineData(ActionFlags.Ron, ActionKind.Ron)]
    [InlineData(ActionFlags.Tsumo, ActionKind.Tsumo)]
    public void Current_win_is_preserved_and_cannot_be_overridden_by_pass(ActionFlags win, ActionKind kind)
    {
        var observed = Snapshot(win | ActionFlags.Pass);
        Assert.Same(observed, AutoPlayLoop.CarryForwardTerminalWin(observed, observed));
        Assert.False(LiveMortalBridge.TryMapDecision(new MortalReaction("none", 0, null, null, []), observed, out _));
        Assert.False(AutoPlayLoop.TryResolveCallOption(observed, observed, ActionKind.Pass, out _));
        Assert.True(AutoPlayLoop.TryResolveCallOption(observed, observed, kind, out int option));
        Assert.Equal(0, option);
    }

    [Fact]
    public void Vanished_ron_is_not_latched_even_when_hand_and_wall_are_unchanged()
    {
        var pending = Snapshot(ActionFlags.Ron | ActionFlags.Pass);
        var current = pending with { Legal = new LegalActions(ActionFlags.Pass, [], [], [], []) };
        Assert.Same(current, AutoPlayLoop.CarryForwardTerminalWin(pending, current));
        Assert.False(AutoPlayLoop.TryResolveCallOption(pending, current, ActionKind.Ron, out _));
    }

    [Fact]
    public void Self_draw_transition_only_allows_the_win_not_a_phantom_pass()
    {
        var pending = Snapshot(ActionFlags.Tsumo | ActionFlags.Pass);
        var current = pending with { AddonStateCode = 6,
            Legal = new LegalActions(ActionFlags.Discard, [], [], [], []) };
        var carried = AutoPlayLoop.CarryForwardTerminalWin(pending, current);
        Assert.True(carried.Legal.Can(ActionFlags.Tsumo));
        Assert.True(AutoPlayLoop.TryResolveCallOption(carried, current, ActionKind.Tsumo, out int option));
        Assert.Equal(0, option);
        Assert.False(AutoPlayLoop.TryResolveCallOption(carried, current, ActionKind.Pass, out _));
        var ordinaryCall = current with { Legal = new LegalActions(ActionFlags.Pon | ActionFlags.Pass, [], [], [], []) };
        Assert.False(AutoPlayLoop.TryResolveCallOption(carried, ordinaryCall, ActionKind.Tsumo, out _));
    }

    [Fact]
    public void New_hand_or_changed_red_identity_cancels_old_call()
    {
        var before = Snapshot(ActionFlags.Pon | ActionFlags.Pass);
        Assert.False(AutoPlayLoop.TryResolveCallOption(before, before with { HandId = 8 }, ActionKind.Pass, out _));
        var reds = new bool[before.Hand.Count]; reds[0] = true;
        Assert.False(AutoPlayLoop.TryResolveCallOption(before, before with { HandIsRed = reds }, ActionKind.Pass, out _));
    }

    [Fact]
    public void Changed_candidate_or_missing_prompt_cannot_accept_old_action()
    {
        var before = Snapshot(ActionFlags.Pon | ActionFlags.Pass);
        var after = before with { Legal = new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [], []) };
        Assert.False(AutoPlayLoop.TryResolveCallOption(before, after, ActionKind.Pon, out _));
        Assert.False(AutoPlayLoop.TryResolveCallOption(before, before with { Legal = LegalActions.None }, ActionKind.Pass, out _));
    }
}

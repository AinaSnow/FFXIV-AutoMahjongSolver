using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class TerminalWinConfirmationTrackerTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 5, 10, 31, DateTimeKind.Utc);

    [Fact]
    public void Reappearing_win_after_prompt_left_is_confirmation()
    {
        var tracker = new TerminalWinConfirmationTracker(TimeSpan.FromSeconds(5));
        var win = Snapshot(ActionFlags.Ron | ActionFlags.Pass);

        tracker.Begin(ActionKind.Ron, win, T0);
        Assert.True(tracker.IsAwaitingTransition(win, T0));

        tracker.Observe(Snapshot(ActionFlags.None), T0.AddMilliseconds(10));

        Assert.True(tracker.TryGetConfirmation(
            win, T0.AddSeconds(1), out var kind));
        Assert.Equal(ActionKind.Ron, kind);
    }

    [Fact]
    public void Same_prompt_without_intermediate_transition_is_not_confirmation()
    {
        var tracker = new TerminalWinConfirmationTracker(TimeSpan.FromSeconds(5));
        var win = Snapshot(ActionFlags.Tsumo | ActionFlags.Pass);

        tracker.Begin(ActionKind.Tsumo, win, T0);

        Assert.False(tracker.TryGetConfirmation(
            win, T0.AddSeconds(1), out _));
        Assert.True(tracker.IsAwaitingTransition(win, T0.AddSeconds(1)));
    }

    [Fact]
    public void Wall_change_cancels_pending_confirmation()
    {
        var tracker = new TerminalWinConfirmationTracker(TimeSpan.FromSeconds(5));
        var win = Snapshot(ActionFlags.Ron | ActionFlags.Pass);
        tracker.Begin(ActionKind.Ron, win, T0);
        tracker.Observe(Snapshot(ActionFlags.None), T0.AddMilliseconds(10));

        var nextTurn = Snapshot(ActionFlags.Ron | ActionFlags.Pass) with
        {
            WallRemaining = win.WallRemaining - 1,
        };

        Assert.False(tracker.TryGetConfirmation(
            nextTurn, T0.AddSeconds(1), out _));
    }

    [Fact]
    public void Confirmation_expires()
    {
        var tracker = new TerminalWinConfirmationTracker(TimeSpan.FromSeconds(5));
        var win = Snapshot(ActionFlags.Ron | ActionFlags.Pass);
        tracker.Begin(ActionKind.Ron, win, T0);
        tracker.Observe(Snapshot(ActionFlags.None), T0.AddMilliseconds(10));

        Assert.False(tracker.TryGetConfirmation(
            win, T0.AddSeconds(6), out _));
    }

    private static StateSnapshot Snapshot(ActionFlags flags)
    {
        var hand = Tiles.Parse("234567m123p1112z").ToArray();
        return StateSnapshot.Empty with
        {
            AddonStateCode = 15,
            Hand = hand,
            HandIsRed = new bool[hand.Length],
            WallRemaining = 17,
            Legal = new LegalActions(flags, [], [], [], []),
        };
    }
}

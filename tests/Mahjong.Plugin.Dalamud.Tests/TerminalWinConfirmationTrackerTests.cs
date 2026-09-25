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

    [Theory]
    [InlineData(ActionKind.Ron, ActionFlags.Ron)]
    [InlineData(ActionKind.Tsumo, ActionFlags.Tsumo)]
    public void Identical_tiles_in_a_new_hand_cannot_confirm_old_win(ActionKind kind, ActionFlags flag)
    {
        var tracker = new TerminalWinConfirmationTracker(TimeSpan.FromSeconds(5));
        var win = Snapshot(flag | ActionFlags.Pass) with { HandId = 1 };
        tracker.Begin(kind, win, T0);
        tracker.Observe(win with { Legal = LegalActions.None }, T0.AddMilliseconds(10));
        Assert.False(tracker.TryGetConfirmation(win with { HandId = 2 }, T0.AddSeconds(1), out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Opponent_progress_without_wall_change_cancels_win_confirmation(bool meldChanged)
    {
        var tracker = new TerminalWinConfirmationTracker(TimeSpan.FromSeconds(5));
        var win = Snapshot(ActionFlags.Ron | ActionFlags.Pass);
        tracker.Begin(ActionKind.Ron, win, T0);
        tracker.Observe(win with { Legal = LegalActions.None }, T0.AddMilliseconds(10));
        var seats = win.Seats.ToArray();
        seats[2] = meldChanged
            ? seats[2] with { Melds = [Meld.Pon(Tile.FromId(0), Tile.FromId(0), 3)] }
            : seats[2] with { DiscardCount = 1, Discards = [Tile.FromId(0)], DiscardIsRed = [false] };
        Assert.False(tracker.TryGetConfirmation(win with { Seats = seats }, T0.AddSeconds(1), out _));
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

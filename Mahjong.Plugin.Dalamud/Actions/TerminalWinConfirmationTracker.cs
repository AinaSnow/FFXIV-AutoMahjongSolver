using Mahjong.Core;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Actions;

internal sealed class TerminalWinConfirmationTracker
{
    private readonly TimeSpan window;
    private PendingWin? pending;

    private sealed record PendingWin(
        ActionKind Kind,
        StateSnapshot Snapshot,
        DateTime StartedAt,
        bool PromptLeft);

    public TerminalWinConfirmationTracker(TimeSpan window)
    {
        this.window = window;
    }

    public void Begin(ActionKind kind, StateSnapshot snapshot, DateTime now)
    {
        if (kind is not (ActionKind.Ron or ActionKind.Tsumo))
            throw new ArgumentOutOfRangeException(nameof(kind));
        pending = new PendingWin(kind, snapshot, now, PromptLeft: false);
    }

    public void Observe(StateSnapshot snapshot, DateTime now)
    {
        if (!TryGetValid(snapshot, now, out var current))
            return;
        if (!snapshot.Legal.Can(FlagFor(current.Kind)))
            pending = current with { PromptLeft = true };
    }

    public bool IsAwaitingTransition(StateSnapshot snapshot, DateTime now)
    {
        return TryGetValid(snapshot, now, out var current)
            && !current.PromptLeft
            && snapshot.Legal.Can(FlagFor(current.Kind));
    }

    public bool TryGetConfirmation(
        StateSnapshot snapshot, DateTime now, out ActionKind kind)
    {
        if (TryGetValid(snapshot, now, out var current)
            && current.PromptLeft
            && snapshot.Legal.Can(FlagFor(current.Kind)))
        {
            kind = current.Kind;
            return true;
        }

        kind = default;
        return false;
    }

    public void Complete() => pending = null;

    public void Reset() => pending = null;

    private bool TryGetValid(
        StateSnapshot snapshot, DateTime now, out PendingWin current)
    {
        if (pending is not { } value
            || now - value.StartedAt > window
            || !HasSameHandContext(value.Snapshot, snapshot))
        {
            pending = null;
            current = null!;
            return false;
        }

        current = value;
        return true;
    }

    private static ActionFlags FlagFor(ActionKind kind) => kind switch
    {
        ActionKind.Ron => ActionFlags.Ron,
        ActionKind.Tsumo => ActionFlags.Tsumo,
        _ => ActionFlags.None,
    };

    internal static bool HasSameHandContext(
        StateSnapshot left, StateSnapshot right)
    {
        if (left.WallRemaining != right.WallRemaining
            || left.Hand.Count != right.Hand.Count
            || left.OurMelds.Count != right.OurMelds.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Hand.Count; i++)
        {
            if (left.Hand[i].Id != right.Hand[i].Id)
                return false;
            bool leftRed = i < left.HandIsRed.Count && left.HandIsRed[i];
            bool rightRed = i < right.HandIsRed.Count && right.HandIsRed[i];
            if (leftRed != rightRed)
                return false;
        }

        for (int i = 0; i < left.OurMelds.Count; i++)
        {
            var leftMeld = left.OurMelds[i];
            var rightMeld = right.OurMelds[i];
            if (leftMeld.Kind != rightMeld.Kind
                || leftMeld.ClaimedFromSeat != rightMeld.ClaimedFromSeat
                || leftMeld.Tiles.Length != rightMeld.Tiles.Length)
            {
                return false;
            }
            for (int j = 0; j < leftMeld.Tiles.Length; j++)
                if (leftMeld.Tiles[j].Id != rightMeld.Tiles[j].Id)
                    return false;
        }
        return true;
    }
}

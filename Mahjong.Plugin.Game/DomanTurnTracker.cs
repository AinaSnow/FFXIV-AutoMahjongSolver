namespace Mahjong.Plugin.Game;

/// <summary>Hand-scoped UI observations shared by hints, Mortal validation and execution.</summary>
public sealed class DomanTurnTracker
{
    private long? handId;
    private int meldCount, discardCount;
    private int? declarationBaseline;
    private bool riichiCommitted;
    private IReadOnlyList<Tile> forbidden = [];

    public void Reset()
    {
        handId = null; declarationBaseline = null; riichiCommitted = false;
        meldCount = discardCount = 0; forbidden = [];
    }

    public void DeclarationDispatched(StateSnapshot state)
    {
        Observe(state);
        if (!riichiCommitted) declarationBaseline ??= CountDiscards(state);
    }

    public void CancelUnconfirmedDeclaration()
    {
        if (!riichiCommitted) declarationBaseline = null;
    }

    public StateSnapshot Observe(StateSnapshot state)
    {
        int count = CountDiscards(state);
        if (handId != state.HandId)
        {
            Reset(); handId = state.HandId; meldCount = state.OurMelds.Count; discardCount = count;
        }
        if (count > discardCount) forbidden = [];
        // Do not infer a call after a missed discard or when attaching in the middle of a hand.
        if (state.OurMelds.Count == meldCount + 1 && count == discardCount)
            forbidden = Kuikae.ForbiddenDiscards(state.OurMelds[^1]);
        if (declarationBaseline is { } baseline && count > baseline) riichiCommitted = true;
        riichiCommitted |= state.OurRiichi;
        meldCount = state.OurMelds.Count; discardCount = count;
        return Apply(state);
    }

    public StateSnapshot Apply(StateSnapshot state)
    {
        if (handId != state.HandId) return state;
        var legal = state.Legal;
        if (forbidden.Count > 0)
            legal = legal with
            {
                DiscardableTiles = state.Hand.Distinct().Where(t => legal.AllowsDiscard(t) && !forbidden.Contains(t)).ToArray(),
                DiscardRestrictionKnown = true,
            };
        if (riichiCommitted)
        {
            // Doman performs subsequent non-winning discards itself. Win/kan prompts stay available.
            legal = legal with { Flags = legal.Flags & ~(ActionFlags.Riichi | ActionFlags.Discard) };
            state = state with { OurRiichi = true };
        }
        return state with { Legal = legal };
    }

    private static int CountDiscards(StateSnapshot state) => state.OurSeat < state.Seats.Count
        ? Math.Max(state.Us.DiscardCount, state.Us.Discards.Count) : 0;
}

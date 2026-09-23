namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>Keep the last playable wall across empty/mid-deal frames. Empty resets are not new hands.</summary>
internal sealed class UiHandBoundaryTracker
{
    private int lastWall = -1;
    private bool pendingEmptyDeal, sawResult;
    public int HandsObserved { get; private set; }
    public static bool ScoresKnown(IReadOnlyList<int> scores) => scores.Count == 4
        && scores.Any(score => score != 0) && scores.All(score => score is >= -200000 and <= 200000);

    public bool Observe(StateSnapshot snapshot)
    {
        sawResult |= HandsObserved > 0 && snapshot.AddonStateCode is 29 or 32;
        if (HandsObserved > 0 && snapshot.Hand.Count == 0 && snapshot.WallRemaining >= 70
            && (lastWall < 70 || sawResult)) pendingEmptyDeal = true;
        int effectiveCount = snapshot.Hand.Count + snapshot.OurMelds.Count * 3;
        if (effectiveCount is not (13 or 14) || snapshot.Hand.Count == 0 || !ScoresKnown(snapshot.Scores)) return false;
        bool first = HandsObserved == 0;
        bool newDeal = snapshot.OurMelds.Count == 0 && snapshot.WallRemaining >= 60
            && (snapshot.WallRemaining > lastWall + 5 || pendingEmptyDeal && snapshot.WallRemaining >= lastWall);
        if (first || newDeal)
        {
            pendingEmptyDeal = false;
            sawResult = false;
            HandsObserved++;
            lastWall = snapshot.WallRemaining;
            return true;
        }
        // Do not consume upward read glitches before a complete deal is observed.
        if (snapshot.WallRemaining < 60) pendingEmptyDeal = false;
        if (snapshot.WallRemaining <= lastWall + 5) lastWall = snapshot.WallRemaining;
        return false;
    }

    public void Reset() { lastWall = -1; HandsObserved = 0; pendingEmptyDeal = false; sawResult = false; }
}

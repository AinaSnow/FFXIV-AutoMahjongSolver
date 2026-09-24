using Mahjong.Engine;

namespace Mahjong.Policy.Placement;

/// <summary>Mahjong rewards rank, not raw score — bias the discard scorer accordingly.</summary>
public sealed class PlacementAdjuster : IPlacementPolicy
{
    private readonly PlacementWeights weights;

    public PlacementAdjuster() : this(PlacementWeights.Default) { }

    public PlacementAdjuster(PlacementWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        this.weights = weights;
    }

    public PlacementMultipliers ComputeFor(StateSnapshot state)
    {
        var rank = RankOf(state, state.OurSeat);
        bool lastHand = IsLastHand(state);
        int scoreGapBelow = ScoreGapToLowerRank(state, rank);

        return rank switch
        {
            1 => lastHand && scoreGapBelow > weights.Rank1HugeLeadGap
                    ? weights.Rank1HugeLead
                    : (lastHand ? weights.Rank1LastHand : weights.Rank1),

            2 or 3 => lastHand ? weights.Rank2Or3LastHand : weights.Rank2Or3,

            4 => lastHand ? weights.Rank4LastHand : weights.Rank4,

            _ => PlacementMultipliers.Neutral,
        };
    }

    /// <summary>1-indexed rank, or zero when tied and the initial order is unknown.</summary>
    public static int RankOf(StateSnapshot state, int seat)
        => SeatRanking.Rank(state.Scores, seat, state.InitialDealerSeat) ?? 0;

    private static int ScoreGapToLowerRank(StateSnapshot state, int ourRank)
    {
        if (ourRank >= 4)
            return int.MaxValue;
        int ourScore = state.Scores[state.OurSeat];
        int minGap = int.MaxValue;
        if (state.Scores.Count(s => s == ourScore) > 1) return 0;
        foreach (var s in state.Scores)
            if (s < ourScore && ourScore - s < minGap)
                minGap = ourScore - s;
        return minGap;
    }

    /// <summary>Approximate; we don't yet have a reliable final-hand flag.</summary>
    public static bool IsLastHand(StateSnapshot state)
        => state.ScheduledRounds is > 0 && state.Kyoku == 4
            && state.RoundWind == state.ScheduledRounds.Value - 1
            && state.Observations.HasFlag(SnapshotObservationFlags.RoundContext);
}

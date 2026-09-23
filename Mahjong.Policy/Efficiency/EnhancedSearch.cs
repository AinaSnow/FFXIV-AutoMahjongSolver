using System.Diagnostics;
using Mahjong.Engine;

namespace Mahjong.Policy.Efficiency;

/// <summary>Experimental two-draw lookahead; never publishes partially searched rankings.</summary>
public static class EnhancedSearch
{
    public static PolicyEvaluation Analyze(StateSnapshot state, PolicyEvaluation baseline,
        TimeSpan budget, CancellationToken cancellationToken = default, IRuleSet? rules = null, RankCalibration? calibration = null)
    {
        var original = baseline;
        long started = Stopwatch.GetTimestamp();
        bool Expired() => cancellationToken.IsCancellationRequested || Stopwatch.GetElapsedTime(started) >= budget;
        if (Expired()) return original;
        rules ??= new RiichiRuleSet();
        if (state.PublicStateConsistent && state.SeatInfoKnown && (state.Legal.Can(ActionFlags.Riichi) || state.Legal.PonCandidates.Count + state.Legal.ChiCandidates.Count + state.Legal.KanCandidates.Count > 0))
        {
            var opponent = new Mahjong.Policy.Opponents.OpponentModel();
            var experimental = new EfficiencyPolicy(opponent, new HeuristicDiscardPolicy(new DefaultWeightProvider(), opponent, new Mahjong.Policy.Placement.PlacementAdjuster(), rules), new EnhancedCallPolicy(rules),
                new EnhancedRiichiPolicy(rules), new HeuristicPushFoldPolicy(), rules);
            baseline = experimental.Analyze(state) with { Source = "enhanced" };
        }
        if (Expired()) return original;
        if (!state.PublicStateConsistent || state.OurRiichi || baseline.Choice.Kind != ActionKind.Discard || baseline.Candidates.Count < 2)
            return baseline;
        // A folding decision is retained, including its explanation.
        if (baseline.Choice.DiscardTile != baseline.Candidates[0].Discard) return baseline;
        if (calibration is not null)
        {
            var calibrated = calibration.Select(state, baseline);
            if (calibrated.Source == "enhanced-calibrated") return Expired() ? original : calibrated;
        }

        var wall = DiscardScorer.BuildVisibleWall(state);
        var ranked = new List<(ScoredDiscard Candidate, double Value)>();
        foreach (var candidate in baseline.Candidates.Where(c => c.ShantenAfter == baseline.Candidates[0].ShantenAfter).Take(4))
        {
            if (Expired()) return original;
            var tiles = state.Hand.ToList(); tiles.Remove(candidate.Discard);
            double total = 0; int weight = 0;
            for (int id = 0; id < Tile.Count34; id++)
            {
                if (Expired()) return original;
                int live = wall.LiveOf(id);
                if (live == 0) continue;
                var draw = Tile.FromId(id); tiles.Add(draw);
                var hand = Hand.FromTiles(tiles, state.OurMelds);
                int shanten = Math.Min(ShantenCalculator.Standard(hand.CloneCounts(), state.OurMelds.Count),
                    state.OurMelds.Count == 0 ? Math.Min(ShantenCalculator.Chiitoitsu(hand.CloneCounts()), ShantenCalculator.Kokushi(hand.CloneCounts())) : 8);
                if (shanten == -1) total += live * 100;
                else
                {
                    // After the first draw, best next discard maximizes second-draw progress.
                    var nextWall = new Wall();
                    // The first discard remains visible; only the newly drawn tile leaves the unseen wall.
                    nextWall.ObserveCounts(wall.Seen.ToArray());
                    nextWall.Observe(draw);
                    var best = UkeireEnumerator.Enumerate(hand, nextWall)
                        .OrderBy(c => c.ShantenAfter).ThenByDescending(c => c.WeightedCount).First();
                    total += live * (-100d * best.ShantenAfter + best.WeightedCount);
                }
                tiles.RemoveAt(tiles.Count - 1); weight += live;
            }
            ranked.Add((candidate, weight == 0 ? double.NegativeInfinity : total / weight));
        }
        if (Expired() || ranked.Count < 2) return original;
        var selected = ranked.OrderByDescending(x => x.Value).ThenBy(x => x.Candidate.DealInCost)
            .ThenByDescending(x => x.Candidate.Score).First();
        var reason = new Reason("two-draw-search", $"experimental two-draw value={selected.Value:F3}; {ranked.Count} complete candidates");
        return baseline with { ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds, Source = "enhanced", Choice = ActionChoice.Discard(selected.Candidate.Discard,
            reason.Display, (baseline.Choice.Steps ?? []).Append(reason).ToArray()) with { DiscardIsRed = selected.Candidate.IsRed } };
    }
}

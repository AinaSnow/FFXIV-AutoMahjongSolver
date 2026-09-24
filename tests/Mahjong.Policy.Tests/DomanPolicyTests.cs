using Mahjong.Policy.Efficiency;
using Mahjong.Rules.Rulesets;

namespace Mahjong.Policy.Tests;
public class DomanPolicyTests
{
    [Fact]
    public void Known_empty_legal_set_produces_no_recommendation()
    {
        var state = Snapshots.Closed14("123456m789p23s55z1z");
        state = state with { Legal = state.Legal with { DiscardRestrictionKnown = true } };
        Assert.Empty(DiscardScorer.Score(state));
        Assert.Equal(ActionKind.Pass, new EfficiencyPolicy(new DomanRuleSet()).Choose(state).Kind);
    }

    [Fact]
    public void Forbidden_tiles_never_appear_in_shared_candidates_or_final_choice()
    {
        var state = Snapshots.Closed14("123456m789p23s55z1z");
        var allowed = Tile.FromId(27);
        state = state with { Legal = state.Legal with { DiscardRestrictionKnown = true, DiscardableTiles = [allowed] } };
        var evaluation = new EfficiencyPolicy(new DomanRuleSet()).Analyze(state);
        Assert.Equal(allowed, evaluation.Choice.DiscardTile);
        Assert.All(evaluation.Candidates, c => Assert.Equal(allowed, c.Discard));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Permanent_furiten_uses_tsumo_value_even_when_discarded_wait_is_exhausted(bool exhausted)
    {
        var state = Snapshots.Closed14("123456m789p23s55z1z");
        var seats = state.Seats.ToArray();
        seats[0] = seats[0] with { Discards = [Tile.FromId(18)] };
        if (exhausted) seats[1] = seats[1] with { Discards = [Tile.FromId(18),Tile.FromId(18),Tile.FromId(18)] };
        state = state with { Seats = seats, Observations = SnapshotObservationFlags.PublicDiscardTiles };
        var discard = DiscardScorer.Score(state).First(c => c.Discard.Id == 27);
        var result = new EnhancedRiichiPolicy(new DomanRuleSet()).Evaluate(state, discard);
        Assert.Contains("self-discard furiten: tsumo-only", result.Reason.Display);
        var unknown = new EnhancedRiichiPolicy(new DomanRuleSet()).Evaluate(state with { Observations = SnapshotObservationFlags.None }, discard);
        Assert.DoesNotContain("self-discard furiten", unknown.Reason.Display);
    }

    [Fact]
    public void Four_copies_in_hand_do_not_create_an_impossible_fifth_tile_during_furiten_search()
    {
        var state = Snapshots.Closed14("111123m456p789s5z1z");
        var discard = DiscardScorer.Score(state).First(c => c.Discard.Id == 27);
        Assert.Equal(0, discard.ShantenAfter);
        var result = new EnhancedRiichiPolicy(new DomanRuleSet()).Evaluate(state, discard);
        Assert.Contains("legal values", result.Reason.Display);
    }

    [Fact]
    public void Confirmed_riichi_cannot_be_declared_again()
    {
        var state = Snapshots.Closed14("123456m789p23s55z1z") with { OurRiichi = true };
        var discard = DiscardScorer.Score(state).First(c => c.Discard.Id == 27);
        Assert.False(new HeuristicRiichiPolicy().Evaluate(state, discard).Accept);
        Assert.False(new EnhancedRiichiPolicy(new DomanRuleSet()).Evaluate(state, discard).Accept);
    }
}

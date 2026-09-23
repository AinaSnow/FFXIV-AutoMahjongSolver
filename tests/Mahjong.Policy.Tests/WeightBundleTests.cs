namespace Mahjong.Policy.Tests;

public class WeightBundleTests
{
    [Fact]
    public void Default_bundle_carries_each_subweight_default()
    {
        var b = WeightBundle.Default;
        Assert.Same(DiscardWeights.Default, b.Discard);
        Assert.Same(OpponentWeights.Default, b.Opponent);
        Assert.Same(PlacementWeights.Default, b.Placement);
    }

    [Fact]
    public void Default_schema_version_matches_current()
    {
        Assert.Equal(WeightBundle.CurrentSchemaVersion, WeightBundle.Default.SchemaVersion);
    }

    [Fact]
    public void Stable_baseline_does_not_reward_isolated_tiles_more_than_progress()
    {
        var d = DiscardWeights.Default;
        Assert.True(d.IsolatedTerminal < d.Shanten);
        Assert.True(d.Dora < d.Shanten);
        Assert.True(d.UkeireWeighted > 0);
    }

    [Fact]
    public void Opponent_defaults_match_pre_phase3_hand_tuned_values()
    {
        var o = OpponentWeights.Default;
        Assert.Equal(-2.0, o.TenpaiIntercept);
        Assert.Equal(0.08, o.TenpaiDiscardCount);
        Assert.Equal(0.35, o.TenpaiMeldCount);
        Assert.Equal(0.02, o.TenpaiTurnsElapsed);
        Assert.Equal(4000.0, o.ExpectedHandValue);
    }

    [Fact]
    public void Placement_defaults_have_neutral_for_rank_2_or_3_mid_hanchan()
    {
        var p = PlacementWeights.Default;
        Assert.Equal(PlacementMultipliers.Neutral, p.Rank2Or3);
        Assert.Equal(8000, p.Rank1HugeLeadGap);
    }

    [Fact]
    public void Bundle_with_record_substitution_replaces_only_targeted_subweights()
    {
        var custom = new DiscardWeights(
            Shanten: 50.0, UkeireKinds: 1.0, UkeireWeighted: 1.0,
            Dora: 1.0, Yakuhai: 1.0, IsolatedTerminal: 1.0, DealInCost: 1.0);
        var b = WeightBundle.Default with { Discard = custom };

        Assert.Same(custom, b.Discard);
        Assert.Same(WeightBundle.Default.Opponent, b.Opponent);
    }
}

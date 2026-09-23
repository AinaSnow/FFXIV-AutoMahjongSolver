using Mahjong.Engine;
using Mahjong.Policy.Efficiency;
namespace Mahjong.Policy.Tests;
public class StabilityRegressionTests
{
    [Theory]
    [InlineData(MeldKind.AnKan)] [InlineData(MeldKind.MinKan)] [InlineData(MeldKind.ShouMinKan)]
    public void Replacement_draw_after_each_kan_can_discard(MeldKind kind)
    {
        var s = Snapshots.Closed14("123m456p789s1122z5p");
        var tile = Tile.FromId(31);
        s = s with { Hand = s.Hand.Take(11).ToArray(), OurMelds = [new Meld(kind, [tile,tile,tile,tile], tile, 1)] };
        Assert.Equal(ActionKind.Discard, new EfficiencyPolicy().Choose(s).Kind);
        Assert.Equal(ActionKind.Pass, new EfficiencyPolicy().Choose(s with { Hand = s.Hand.Take(10).ToArray() }).Kind);
    }
    [Fact]
    public void Multiple_kans_count_as_three_structural_tiles_each()
    {
        var s = Snapshots.Closed14("123m456p789s1122z5p");
        s = s with { Hand = s.Hand.Take(8).ToArray(), OurMelds = [Meld.AnKan(Tile.FromId(31)), Meld.AnKan(Tile.FromId(32))] };
        Assert.Equal(ActionKind.Discard, new EfficiencyPolicy().Choose(s).Kind);
    }
    [Fact]
    public void Dora_does_not_break_tenpai_in_stable_offense()
    {
        var s = Snapshots.Closed14("123m456p789s1122z5p") with { DoraIndicators = [Tile.FromId(12)] };
        var result = new EfficiencyPolicy().Analyze(s);
        Assert.Equal(0, result.Candidates.Single(c => c.Discard == result.Choice.DiscardTile).ShantenAfter);
    }
    [Fact]
    public void Same_kind_prefers_normal_five_and_reports_identity()
    {
        var s = Snapshots.Closed14("123m456p789s1122z5p");
        var red = s.Hand.Select((t,i) => i == s.Hand.Count - 1).ToArray();
        s = s with { HandIsRed = red, AkaDora = 1, Observations = SnapshotObservationFlags.HandRedIdentity,
            Legal = new LegalActions(ActionFlags.Discard, [Tile.FromId(13)], [], [], []) };
        var result = new EfficiencyPolicy().Analyze(s);
        Assert.False(result.Choice.DiscardIsRed);
        Assert.Equal(1, Assert.Single(result.Candidates).DoraRetained);
    }
    [Fact]
    public void Inconsistent_public_state_does_not_dispatch()
    {
        var s = Snapshots.Closed14("123m456p789s1122z5p") with { PublicStateConsistent = false };
        Assert.Equal(ActionKind.Pass, new EfficiencyPolicy().Choose(s).Kind);
    }
    [Fact]
    public void Cancelled_or_zero_budget_search_returns_the_entire_stable_result()
    {
        var s=Snapshots.Closed14("123m456p789s1122z5p");
        var baseline=new EfficiencyPolicy().Analyze(s);
        using var token=new CancellationTokenSource();token.Cancel();
        Assert.Same(baseline,EnhancedSearch.Analyze(s,baseline,TimeSpan.FromSeconds(1),token.Token));
        Assert.Same(baseline,EnhancedSearch.Analyze(s,baseline,TimeSpan.Zero));
    }
    [Fact]
    public void Rank_calibration_rejects_unobserved_buckets()
    {
        var s=Snapshots.Closed14("123m456p789s1122z5p");
        var baseline=new EfficiencyPolicy().Analyze(s);
        var calibration=new RankCalibration(1,"train",new string('0',64),new());
        Assert.Same(baseline,calibration.Select(s,baseline));
    }
}

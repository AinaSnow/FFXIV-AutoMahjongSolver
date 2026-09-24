using System.Text.Json;
using Mahjong.Core;
using Mahjong.Plugin.Game.Mjai;
namespace Mahjong.Plugin.Game.Tests;
public class PublicStateReducerTests
{
    private static string Start(int dealer = 0) => JsonSerializer.Serialize(new {
        type="start_kyoku", oya=dealer, bakaze="S", kyoku=4, honba=2, kyotaku=3,
        scores=new[]{25000,25000,25000,25000}, dora_marker="1m",
        tehais=new[]{new[]{"1m","2m","3m","4p","5pr","6p","7s","8s","9s","E","E","S","S"},new[]{"?"},new[]{"?"},new[]{"?"}}
    });
    [Theory] [InlineData(0,0)] [InlineData(1,3)] [InlineData(2,2)] [InlineData(3,1)]
    public void Relative_self_and_wind_are_independent(int dealer, int wind)
    {
        var r = new PublicStateReducer { ScheduledRounds = 2 }; r.ApplyJson(Start(dealer));
        var s = r.Snapshot(LegalActions.None);
        Assert.Equal((dealer + 1) % 4,s.InitialDealerSeat);
        Assert.Equal(0,s.OurSeat); Assert.Equal(wind,s.SeatWind); Assert.Equal(4,s.Kyoku);
        Assert.Equal(2,s.Honba); Assert.Equal(3,s.RiichiSticks); Assert.Equal(1,s.AkaDora);
        Assert.True(s.PublicStateConsistent);
    }
    [Fact]
    public void Unknown_counters_are_not_claimed_as_observed()
    {
        var r=new PublicStateReducer(); r.ApplyJson(Start(),knownCounters:false);
        var s=r.Snapshot(LegalActions.None);
        Assert.False(s.Observations.HasFlag(SnapshotObservationFlags.Honba));
        Assert.False(s.Observations.HasFlag(SnapshotObservationFlags.RiichiSticks));
    }
    [Fact]
    public void Loss_is_quarantined_until_next_hand_and_red_mismatch_is_rejected()
    {
        var r=new PublicStateReducer(); r.ApplyJson(Start());
        var s=r.Snapshot(LegalActions.None);
        Assert.False(r.Merge(s with { HandIsRed = new bool[13] }).PublicStateConsistent);
        r.Invalidate("dropped packet"); Assert.False(r.Snapshot(LegalActions.None).PublicStateConsistent);
        r.ApplyJson(Start()); Assert.True(r.Snapshot(LegalActions.None).PublicStateConsistent); Assert.Equal(2,r.HandId);
    }
    [Fact]
    public void Riichi_acceptance_is_idempotent_and_dora_appends()
    {
        var r=new PublicStateReducer(); r.ApplyJson(Start());
        r.ApplyJson("{\"type\":\"reach\",\"actor\":2}");
        r.ApplyJson("{\"type\":\"reach_accepted\",\"actor\":2}");
        r.ApplyJson("{\"type\":\"reach_accepted\",\"actor\":2}");
        r.ApplyJson("{\"type\":\"dora\",\"dora_marker\":\"5p\"}");
        var s=r.Snapshot(LegalActions.None);
        Assert.Equal(24000,s.Scores[2]); Assert.Equal(4,s.RiichiSticks); Assert.Equal(2,s.DoraIndicators.Count);
        Assert.True(s.Seats[2].Riichi);
    }
}

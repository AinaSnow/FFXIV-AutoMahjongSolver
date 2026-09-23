using System.Text.Json;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Tests.Stubs;

namespace Mahjong.Plugin.Dalamud.Tests;

public class LiveHandBoundaryTests
{
    private static StateSnapshot Snapshot(int wall, int count, int[]? scores = null, int state = 30) => StateSnapshot.Empty with
    {
        WallRemaining = wall, AddonStateCode = state,
        Hand = Enumerable.Range(0,count).Select(i => Tile.FromId(i % 34)).ToArray(),
        Scores = scores ?? [25000,25000,25000,25000],
    };

    [Fact]
    public async Task Full_live_state_sequence_has_seven_settlements_and_no_phantom_eighth_hand()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory,"RegressionFixtures","live-match-20260923-boundaries.json");
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
        using var temp = new TempDir();
        var config = new DalamudConfigService(_ => {}, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), temp.Path);
        var boundary = new UiHandBoundaryTracker();
        int handId = 0;
        var states = fixture.RootElement.GetProperty("states");
        Assert.True(states.GetArrayLength() > 800); // An empty/reduced fixture cannot silently pass CI.
        foreach (var r in states.EnumerateArray())
        {
            Assert.Equal(0,r.GetProperty("meld_count").GetInt32());
            var snap = Snapshot(r.GetProperty("wall").GetInt32(),r.GetProperty("hand_count").GetInt32(),
                r.GetProperty("scores").EnumerateArray().Select(x=>x.GetInt32()).ToArray(),r.GetProperty("state_code").GetInt32());
            if (boundary.Observe(snap)) handId++;
            logger.OnStateChanged(snap with { HandId = handId });
        }
        logger.CompleteSession();
        logger.CompleteSession(); // Both leave and unload can attempt finalization.
        await logger.FlushAsync();
        Assert.Equal(7, handId);
        var files = logger.SnapshotSessionPaths();
        Assert.Equal(7, files.Count);
        int[] expectedOurDeltas = [3900,12300,0,-5200,-1000,0,13000];
        for (int i=0;i<files.Count;i++)
        {
            var records = File.ReadAllLines(files[i]).Select(line=>JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            var start = Assert.Single(records,x=>x.GetProperty("e").GetString()=="hand-start");
            Assert.Equal(100000,start.GetProperty("scores").EnumerateArray().Sum(x=>x.GetInt32()));
            var end = Assert.Single(records,x=>x.GetProperty("e").GetString()=="hand-end");
            Assert.Equal(expectedOurDeltas[i],end.GetProperty("deltas")[0].GetInt32());
            Assert.Equal(0,end.GetProperty("deltas").EnumerateArray().Sum(x=>x.GetInt32()));
            Assert.True(end.GetProperty("result_inferred").GetBoolean());
        }
        using var archive = new MatchArchiveWriter(temp.Path, new StubPluginLog());
        var stats = new MatchArchiveMortalStats("UI-only",0,0,0,0,0,0,0,0);
        string completed = Assert.IsType<string>(await archive.FinalizeSessionAsync(files,stats));
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(completed,"summary.json")));
        Assert.Equal(7,summary.RootElement.GetProperty("hand_count").GetInt32());
        Assert.Equal(7,summary.RootElement.GetProperty("settled_hands").GetInt32());
        Assert.Equal(48000,summary.RootElement.GetProperty("our_score").GetInt32());
    }

    [Fact]
    public void Empty_or_partial_deal_does_not_consume_the_next_hand_boundary()
    {
        var tracker = new UiHandBoundaryTracker();
        Assert.False(tracker.Observe(Snapshot(70,0,[0,0,0,0])));
        Assert.False(tracker.Observe(Snapshot(70,14,[0,0,0,0])));
        Assert.True(tracker.Observe(Snapshot(70,14)));
        Assert.False(tracker.Observe(Snapshot(20,13)));
        Assert.False(tracker.Observe(Snapshot(70,0)));
        Assert.False(tracker.Observe(Snapshot(70,7)));
        Assert.True(tracker.Observe(Snapshot(70,13)));
        Assert.False(tracker.Observe(Snapshot(70,14)));
        Assert.Equal(2,tracker.HandsObserved);
        tracker.Reset();
        Assert.True(tracker.Observe(Snapshot(70,13)));
        Assert.Equal(1,tracker.HandsObserved);
    }

    [Fact]
    public void First_turn_win_still_advances_hand_id_after_empty_deal()
    {
        var tracker = new UiHandBoundaryTracker();
        Assert.True(tracker.Observe(Snapshot(70,14)));
        Assert.False(tracker.Observe(Snapshot(68,13)));
        Assert.False(tracker.Observe(Snapshot(70,0)));
        Assert.True(tracker.Observe(Snapshot(70,13)));
        Assert.Equal(2,tracker.HandsObserved);
    }

    [Fact]
    public async Task Unloading_during_result_animation_does_not_claim_a_zero_point_draw()
    {
        using var temp = new TempDir();
        using var logger = new GameLogger(new DalamudConfigService(_=>{},new Configuration()), new StubPluginLog(),temp.Path);
        logger.OnStateChanged(Snapshot(70,14));
        logger.OnStateChanged(Snapshot(20,14,state:32)); // UI result, old scores still in memory.
        logger.CompleteSession();
        await logger.FlushAsync();
        var lines = File.ReadAllLines(Assert.Single(logger.SnapshotSessionPaths()));
        Assert.DoesNotContain(lines,x=>x.Contains("hand-end"));
        Assert.Contains(lines,x=>x.Contains("hand-incomplete"));
    }

    [Fact]
    public async Task Leaving_mid_hand_does_not_invent_a_settlement()
    {
        using var temp = new TempDir();
        using var logger = new GameLogger(new DalamudConfigService(_=>{},new Configuration()), new StubPluginLog(),temp.Path);
        logger.OnStateChanged(Snapshot(70,14));
        logger.OnStateChanged(Snapshot(40,13));
        logger.CompleteSession();
        await logger.FlushAsync();
        var lines = File.ReadAllLines(Assert.Single(logger.SnapshotSessionPaths()));
        Assert.DoesNotContain(lines,x=>x.Contains("hand-end"));
        Assert.Contains(lines,x=>x.Contains("hand-incomplete"));
    }
}

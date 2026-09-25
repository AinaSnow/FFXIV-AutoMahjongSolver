using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Game.Mjai;
using System.Text.Json;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class ServerCallOutcomeTrackerTests
{
    [Theory]
    [InlineData("ron", "server-action-mismatch")]
    [InlineData("pon", "server-action-mismatch")]
    [InlineData("chi", "server-confirmed")]
    [InlineData("chi-variant", "server-confirmed")]
    public void Actual_server_call_is_compared_with_requested_action(string requested, string status)
    {
        var tracker = new ServerCallOutcomeTracker(); var at = DateTime.UtcNow;
        tracker.Track(7, requested, "option=1", at);
        var result = Assert.IsType<ServerCallOutcomeTracker.Result>(tracker.Observe(Chi(), at.AddMilliseconds(110)));
        Assert.Equal(7, result.ActionId); Assert.Equal("chi", result.Actual); Assert.Equal(status, result.Status);
        Assert.Null(tracker.Observe(Chi(), at.AddMilliseconds(120))); // No double counting.
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Old_or_cancelled_click_does_not_bind_to_new_hand_or_manual_call(int scenario)
    {
        var tracker = new ServerCallOutcomeTracker(); var at = DateTime.UtcNow;
        tracker.Track(7, "ron", "option=1", at);
        if (scenario == 0) tracker.Reset(); // Manual takeover/configuration/leave.
        if (scenario == 1) tracker.Observe(new MjaiStartGame(), at);
        if (scenario == 2) tracker.Observe(new MjaiEndKyoku(), at);
        Assert.Null(tracker.Observe(Chi(), scenario == 3 ? at.AddSeconds(6) : at));
    }

    [Fact]
    public void Opponent_call_and_result_boundary_do_not_fabricate_our_win_confirmation()
    {
        var tracker = new ServerCallOutcomeTracker(); var at = DateTime.UtcNow;
        tracker.Track(7, "ron", "option=0", at);
        Assert.Null(tracker.Observe(new MjaiOpenCall("chi", 1, 0, "3s", ["1s", "2s"]), at));
        Assert.Null(tracker.Observe(new MjaiEndKyoku(), at));
        Assert.Null(tracker.Observe(Chi(), at));
    }

    [Theory]
    [InlineData("superseded")] [InlineData("state-changed")] [InlineData("timeout")]
    public async Task Server_mismatch_overrides_ui_outcome_and_counts_failed_action_once(string earlier)
    {
        using var temp = new TempDir(); string file = Path.Combine(temp.Path, "game.ndjson");
        await File.WriteAllLinesAsync(file, [
            """{"e":"action","action_id":7,"kind":"Ron","result":"Ok"}""",
            JsonSerializer.Serialize(new { e="action-outcome", action_id=7, status=earlier }),
            """{"e":"action-outcome","action_id":7,"status":"server-action-mismatch"}""",
            """{"e":"action-outcome","action_id":7,"status":"server-action-mismatch"}""",
            """{"e":"action-outcome","action_id":7,"status":"state-changed"}""",
        ]);
        using var writer = new MatchArchiveWriter(temp.Path, new StubPluginLog());
        var stats = new MatchArchiveMortalStats("trial",0,0,0,0,0,0,0,0);
        var dir = Assert.IsType<string>(await writer.FinalizeSessionAsync([file], stats));
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "summary.json")));
        var root = summary.RootElement;
        Assert.Equal(1, root.GetProperty("failed_actions").GetInt32());
        var health = root.GetProperty("execution_health");
        Assert.Equal(0, health.GetProperty("missing_outcomes").GetInt32());
        var outcomes = health.GetProperty("outcomes");
        Assert.Equal(1, outcomes.GetProperty("server-action-mismatch").GetInt32());
        Assert.Single(outcomes.EnumerateObject());
    }

    private static MjaiOpenCall Chi() => new("chi", 0, 3, "3s", ["1s", "2s"]);
}

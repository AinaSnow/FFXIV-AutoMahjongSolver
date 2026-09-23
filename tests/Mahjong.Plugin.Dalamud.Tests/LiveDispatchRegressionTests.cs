using System.Text.Json;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests;

public class LiveDispatchRegressionTests
{
    [Theory]
    [InlineData(6, ActionKind.Tsumo, true)]
    [InlineData(6, ActionKind.Riichi, false)]
    [InlineData(6, ActionKind.AnKan, false)]
    [InlineData(15, ActionKind.Ron, true)]
    [InlineData(28, ActionKind.Riichi, false)]
    [InlineData(30, ActionKind.Tsumo, false)]
    public void Recorded_self_draw_route_does_not_change_riichi_or_other_states(int state, ActionKind kind, bool expected) =>
        Assert.Equal(expected, InputDispatcher.UseClassicCallPath(state, 15, 6, kind, false));

    [Fact]
    public void Automatic_callback_evidence_expires_with_dispatch_scope()
    {
        Assert.False(AutomationInputScope.IsActive);
        using (AutomationInputScope.Enter())
        {
            Assert.False(AutomationInputScope.CallbackObserved);
            AutomationInputScope.ObserveCallback();
            Assert.True(AutomationInputScope.CallbackObserved);
        }
        Assert.False(AutomationInputScope.IsActive);
        AutomationInputScope.ObserveCallback(); // Later manual/game callback cannot inherit the old scope.
        Assert.False(AutomationInputScope.CallbackObserved);
        using (AutomationInputScope.Enter()) Assert.False(AutomationInputScope.CallbackObserved);
    }

    [Fact]
    public void Nested_callbacks_preserve_evidence_and_exceptions_restore_scope()
    {
        Assert.Throws<IOException>((Action)(() =>
        {
            using var outer = AutomationInputScope.Enter();
            using (AutomationInputScope.Enter()) AutomationInputScope.ObserveCallback();
            Assert.True(AutomationInputScope.CallbackObserved);
            throw new IOException("dispatch failed");
        }));
        Assert.False(AutomationInputScope.IsActive);
    }

    [Fact]
    public async Task Two_ok_tsumo_attempts_followed_by_timeout_and_manual_input_are_not_two_successes()
    {
        using var temp = new TempDir();
        string game = Path.Combine(temp.Path, "game.ndjson");
        await File.WriteAllLinesAsync(game, [
            """{"t":"2026-09-23T08:39:14.938Z","e":"action","action_id":1,"kind":"Tsumo","result":"Ok"}""",
            """{"e":"action-outcome","action_id":1,"status":"timeout"}""",
            """{"e":"action","action_id":2,"kind":"Tsumo","result":"Ok"}""",
            """{"e":"action-outcome","action_id":2,"status":"external-input"}""",
            // A duplicate delivery cannot count the old action as successful after manual takeover.
            """{"e":"action-outcome","action_id":2,"status":"state-changed"}""",
        ]);
        using var writer = new MatchArchiveWriter(temp.Path, new StubPluginLog());
        var stats = new MatchArchiveMortalStats("UI-only",0,0,0,0,0,0,0,0);
        string path = Assert.IsType<string>(await writer.FinalizeSessionAsync([game], stats));
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path,"summary.json")));
        var root = summary.RootElement;
        Assert.Equal(DateTimeOffset.Parse("2026-09-23T08:39:14.938Z"), DateTimeOffset.Parse(root.GetProperty("started_at_utc").GetString()!));
        Assert.Equal(2, root.GetProperty("action_count").GetInt32());
        Assert.Equal(1, root.GetProperty("failed_actions").GetInt32());
        var health = root.GetProperty("execution_health");
        Assert.Equal(0, health.GetProperty("missing_outcomes").GetInt32());
        var outcomes = health.GetProperty("outcomes");
        Assert.Equal(1, outcomes.GetProperty("timeout").GetInt32());
        Assert.Equal(1, outcomes.GetProperty("external-input").GetInt32());
        Assert.False(outcomes.TryGetProperty("state-changed",out _));
    }
}

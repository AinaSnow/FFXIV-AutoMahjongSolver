using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Core;
using Mahjong.Plugin.Game;
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

    private static StateSnapshot RiichiKanPrompt => StateSnapshot.Empty with
    {
        HandId = 25, Revision = 13368, AddonStateCode = 6, OurRiichi = true,
        Hand = new[] { 5,5,11,12,13,13,14,15,19,20,24,24,24,24 }.Select(Tile.FromId).ToArray(),
        Legal = new LegalActions(ActionFlags.MinKan | ActionFlags.Pass, [], [], [], []),
    };

    [Fact]
    public void Recorded_riichi_quad_pass_uses_cancel_but_other_prompts_keep_existing_routes()
    {
        var snap = RiichiKanPrompt;
        Assert.True(InputDispatcher.UseRiichiKanCancel(6, 6, ActionKind.Pass, snap));
        Assert.False(InputDispatcher.UseRiichiKanCancel(15, 6, ActionKind.Pass, snap));
        Assert.False(InputDispatcher.UseRiichiKanCancel(6, 6, ActionKind.MinKan, snap));
        Assert.False(InputDispatcher.UseRiichiKanCancel(6, 6, ActionKind.Pass, snap with { OurRiichi = false }));
        Assert.False(InputDispatcher.UseRiichiKanCancel(6, 6, ActionKind.Pass, snap with { Hand = snap.Hand.Take(13).ToArray() }));
        Assert.False(InputDispatcher.UseRiichiKanCancel(6, 6, ActionKind.Pass, snap with { Legal = snap.Legal with { Flags = ActionFlags.Riichi | ActionFlags.Pass } }));
        Assert.False(InputDispatcher.UseRiichiKanCancel(6, 6, ActionKind.Pass, snap with { Legal = snap.Legal with { Flags = snap.Legal.Flags | ActionFlags.Tsumo } }));
    }

    [Fact]
    public void Recorded_no_op_pass_remains_pending_until_timeout_not_next_retry()
    {
        var snap = RiichiKanPrompt;
        var now = new DateTime(2026, 9, 24, 13, 37, 57, DateTimeKind.Utc);
        var pending = new AutoPlayLoop.PendingDispatchOutcome(123, false, "pass", now, snap.HandId, snap.Revision, 6, 14, 0, "call:list-select(option=1)");
        Assert.Null(AutoPlayLoop.ResolveDispatchOutcome(pending, snap, now.AddSeconds(2.45)));
        Assert.Null(AutoPlayLoop.ResolveDispatchOutcome(pending, snap, now.AddSeconds(4.99)));
        Assert.Equal("timeout", AutoPlayLoop.ResolveDispatchOutcome(pending, snap, now.AddSeconds(5)));
        Assert.Equal("cancelled", AutoPlayLoop.ResolveDispatchOutcome(pending, snap with { HandId = 26 }, now.AddSeconds(1)));
        Assert.Equal("superseded", AutoPlayLoop.ResolveDispatchOutcome(pending, snap with { Revision = 13369 }, now.AddSeconds(1)));
        Assert.Equal("unconfirmed-state-change", AutoPlayLoop.ResolveDispatchOutcome(pending, snap with { AddonStateCode = 30 }, now.AddSeconds(1)));
        Assert.Equal("state-changed", AutoPlayLoop.ResolveDispatchOutcome(pending with { CallbackObserved = true }, snap with { AddonStateCode = 30 }, now.AddSeconds(1)));
    }

    [Fact]
    public void Outcome_identity_uses_frozen_dispatch_state_not_unversioned_addon_read()
    {
        using var temp = new TempDir();
        var config = new DalamudConfigService(_ => {}, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), temp.Path);
        var before = RiichiKanPrompt;
        var raw = before with { HandId = 0, Revision = 0 };
        Assert.Null(logger.DispatchSnapshot);
        using (logger.BeginDispatch(before))
        {
            // A synchronous addon callback may publish the next state before
            // RecordAction/EmitDispatchFinding get to observe its result.
            logger.OnStateChanged(before with { Revision = 13369, AddonStateCode = 30 });
            var captured = logger.DispatchSnapshot ?? raw;
            Assert.Same(before, captured);
            var now = DateTime.UtcNow;
            var pending = new AutoPlayLoop.PendingDispatchOutcome(123, true, "pass", now,
                captured.HandId, captured.Revision, captured.AddonStateCode, captured.Hand.Count, captured.OurMelds.Count, "call:opcode-8(riichi-kan-pass)");
            Assert.Null(AutoPlayLoop.ResolveDispatchOutcome(pending, before, now));
            Assert.Equal("state-changed", AutoPlayLoop.ResolveDispatchOutcome(pending, before with { AddonStateCode = 30 }, now));
        }
        Assert.Null(logger.DispatchSnapshot);
    }

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

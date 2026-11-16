using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>Mirrors tools/tests/test_live_hand_labels.py ServerConfirmationTests for the live tracker.</summary>
public sealed class ServerActionOutcomeTrackerTests
{
    private static readonly Tile FiveMan = Tile.FromId(4);

    [Theory]
    [InlineData(true, "server-confirmed")]
    [InlineData(null, "server-confirmed")]
    [InlineData(false, "server-action-mismatch")]
    public void Discard_needs_the_same_tile_and_known_red_identity(bool? dispatchedRed, string status)
    {
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiTsumo(0, "5mr"));
        Assert.Null(tracker.Track(1, "discard", FiveMan, dispatchedRed));
        var outcome = Assert.Single(tracker.Observe(new MjaiDahai(0, "5mr", true)));
        Assert.Equal((1L, status), (outcome.ActionId, outcome.Status));
        Assert.Contains("dahai 5mr", outcome.Server);
        Assert.Empty(tracker.Observe(new MjaiTsumo(1, "?"))); // Resolved once.
    }

    [Fact]
    public void Wrong_tile_is_a_mismatch_and_a_retry_before_the_server_discard_is_superseded()
    {
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiTsumo(0, "1m"));
        tracker.Track(1, "discard", Tile.FromId(0), false);
        tracker.Track(2, "discard", Tile.FromId(0), false);
        var outcomes = tracker.Observe(new MjaiDahai(0, "1m", true));
        Assert.Equal(["server-superseded", "server-confirmed"], outcomes.Select(o => o.Status));

        tracker.Observe(new MjaiTsumo(0, "2m"));
        tracker.Track(3, "discard", Tile.FromId(0), false);
        Assert.Equal("server-action-mismatch", Assert.Single(tracker.Observe(new MjaiDahai(0, "2m", true))).Status);
    }

    [Fact]
    public void Riichi_declaration_and_its_discard_are_both_confirmed_by_reach_then_dahai()
    {
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiTsumo(0, "5m"));
        tracker.Track(1, "riichi");
        tracker.Track(2, "riichi-tsumogiri", FiveMan, false);
        Assert.Equal("server-confirmed", Assert.Single(tracker.Observe(new MjaiReach(0))).Status);
        var discard = Assert.Single(tracker.Observe(new MjaiDahai(0, "5m", true)));
        Assert.Equal((2L, "server-confirmed"), (discard.ActionId, discard.Status));
        Assert.Contains("riichi", discard.Server);
    }

    [Fact]
    public void Riichi_without_server_reach_is_a_mismatch()
    {
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiTsumo(0, "5m"));
        tracker.Track(1, "riichi");
        var outcome = Assert.Single(tracker.Observe(new MjaiDahai(0, "5m", true)));
        Assert.Equal("server-action-mismatch", outcome.Status);
    }

    [Fact]
    public void Pass_is_window_closed_or_no_window_but_never_confirmed()
    {
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiTsumo(1, "?"));
        tracker.Observe(new MjaiDahai(1, "3p", false));
        Assert.Null(tracker.Track(1, "pass"));
        var closed = Assert.Single(tracker.Observe(new MjaiTsumo(2, "?")));
        Assert.Equal("server-window-closed", closed.Status);

        var stale = tracker.Track(2, "pass"); // after a draw: retained or expired row
        Assert.Equal("server-no-window", stale!.Status);

        tracker.Observe(new MjaiDahai(2, "4p", false));
        tracker.Track(3, "pass");
        var mismatch = Assert.Single(tracker.Observe(new MjaiOpenCall("pon", 0, 2, "4p", ["4p", "4p"])));
        Assert.Equal("server-action-mismatch", mismatch.Status);
    }

    [Fact]
    public void Pass_on_a_retained_row_after_our_accepted_pon_is_superseded_and_the_pon_is_not_reported_twice()
    {
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiDahai(1, "3p", false));
        tracker.Track(1, "pon");
        tracker.Track(2, "pass");
        var outcome = Assert.Single(tracker.Observe(new MjaiOpenCall("pon", 0, 1, "3p", ["3p", "3p"])));
        Assert.Equal((2L, "server-superseded"), (outcome.ActionId, outcome.Status));
    }

    [Fact]
    public void Kan_click_without_server_kan_has_no_effect_and_the_following_pass_closes_the_window()
    {
        // Shape of the 7.58 riichi closed-kan click that never reached the server (A3 review #1).
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiTsumo(0, "C"));
        tracker.Track(1, "ankan");
        tracker.Track(2, "pass");
        var outcomes = tracker.Observe(new MjaiDahai(0, "C", true));
        Assert.Equal([(1L, "server-no-effect"), (2L, "server-window-closed")], outcomes.Select(o => (o.ActionId, o.Status)));
    }

    [Fact]
    public void Confirmed_kan_is_left_to_the_call_tracker()
    {
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiTsumo(0, "C"));
        tracker.Track(1, "ankan");
        Assert.Empty(tracker.Observe(new MjaiAnkan(0, ["C", "C", "C", "C"])));
    }

    [Theory]
    [InlineData("ron", "ron", new[] { 0 }, "server-confirmed")]
    [InlineData("ron", "ron", new[] { 2 }, "server-action-mismatch")]
    [InlineData("tsumo", "ron", new[] { 0 }, "server-action-mismatch")]
    [InlineData("ron", "unknown", new int[0], "server-unconfirmed")]
    public void Win_declaration_is_judged_by_the_hand_result(string label, string outcome, int[] winners, string status)
    {
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiDahai(1, "3p", false));
        tracker.Track(1, label);
        tracker.Track(2, label); // terminal-win confirmation click: both steps keep the status
        var result = new ServerHandResult(outcome, winners, outcome == "unknown" ? null : 1, null, null, null, [],
            outcome == "unknown" ? "test" : null);
        var outcomes = tracker.Observe(new MjaiEndKyoku(), result);
        Assert.Equal([status, status], outcomes.Select(o => o.Status));
    }

    [Fact]
    public void Win_click_after_which_the_table_moves_on_has_no_effect()
    {
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiDahai(1, "3p", false));
        tracker.Track(1, "ron");
        Assert.Equal("server-no-effect", Assert.Single(tracker.Observe(new MjaiTsumo(2, "?"))).Status);
    }

    [Fact]
    public void Hand_end_without_a_deciding_event_is_unconfirmed_and_reset_drops_everything()
    {
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiTsumo(0, "1m"));
        tracker.Track(1, "discard", Tile.FromId(0), false);
        var outcome = Assert.Single(tracker.Observe(new MjaiEndKyoku(), null));
        Assert.Equal("server-unconfirmed", outcome.Status);

        tracker.Observe(new MjaiTsumo(0, "1m"));
        tracker.Track(2, "discard", Tile.FromId(0), false);
        tracker.Reset();
        Assert.Empty(tracker.Observe(new MjaiDahai(0, "1m", true)));
    }

    [Fact]
    public void Calls_and_unknown_labels_never_produce_a_server_outcome_record()
    {
        var tracker = new ServerActionOutcomeTracker();
        tracker.Observe(new MjaiDahai(3, "3p", false));
        Assert.Null(tracker.Track(1, "chi"));
        Assert.Null(tracker.Track(2, "chi-variant"));
        Assert.Null(tracker.Track(3, "hand-result-next"));
        Assert.Empty(tracker.Observe(new MjaiOpenCall("chi", 0, 3, "3p", ["1p", "2p"])));
    }
}

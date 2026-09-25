using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Policy.Abstractions;
using Mahjong.Core;
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class MortalLimitedTrialTests
{
    private static MahjongProtocolProfile Profile => new("2026.09.15.0000.0000", "Emj", false, "partial",
        new() { ["0x0133"] = new(637, 104), ["0x02BB"] = new(638, 24),
            ["0x0214"] = new(641, 32), ["0x018D"] = new(643, 264),
            ["0x023F"] = new(636, 48), ["0x0371"] = new(639, 256), ["0x016D"] = new(640, 504) },
        DomanMortalTrialGuard.Scope, "independent opening and public history evidence");

    [Fact]
    public void Trial_requires_explicit_opt_in_exact_build_variant_scope_and_evidence()
    {
        Assert.False(new Configuration().MortalLimitedTrial);
        bool enabled = false;
        string? variant = "Emj";
        using var capture = new MahjongNetworkCapture(Profile.GameVersion, () => variant, [Profile], () => enabled);
        Assert.False(capture.ProtocolAdmitted);
        enabled = true; capture.RefreshProfile();
        Assert.True(capture.ProtocolAdmitted); Assert.False(capture.ProtocolVerified);
        variant = "EmjL"; capture.RefreshProfile(); Assert.False(capture.ProtocolAdmitted);
        Assert.False(Profile.MatchesLimitedTrial("2026.09.16.0000.0000", "Emj"));
        Assert.False((Profile with { LimitedTrialScope = "future" }).MatchesLimitedTrial(Profile.GameVersion, "Emj"));
        Assert.False((Profile with { LimitedTrialEvidence = "" }).MatchesLimitedTrial(Profile.GameVersion, "Emj"));
    }

    [Fact]
    public void Opening_before_addon_appears_reaches_each_consumer_once()
    {
        string? variant = null;
        using var capture = new MahjongNetworkCapture(Profile.GameVersion, () => variant, [Profile], () => true);
        capture.Record(Packet(0x0133, Opening()));
        Assert.False(capture.TryDequeue(out _));
        variant = "Emj"; capture.RefreshProfile();
        capture.CaptureEnabled = true; capture.PublicCaptureEnabled = true;
        Assert.Equal(637, Drain(capture).Single().MessageId);
        Assert.True(capture.TryDequeuePublic(out _)); Assert.False(capture.TryDequeuePublic(out _));
        capture.RefreshProfile(); capture.CaptureEnabled = true;
        Assert.Empty(Drain(capture));
    }

    [Fact]
    public void Old_opening_is_not_replayed_and_bad_lengths_signal_a_gap()
    {
        using var capture = new MahjongNetworkCapture(Profile.GameVersion, () => "Emj", [Profile], () => true);
        capture.Record(Packet(0x0133, Opening()) with { Time = DateTimeOffset.UtcNow.AddSeconds(-10) });
        capture.CaptureEnabled = true; Assert.Empty(Drain(capture));
        capture.Record(Packet(0x0133, new byte[103]));
        Assert.True(capture.DroppedPackets > 0); Assert.Empty(Drain(capture));
    }

    [Theory]
    [InlineData(0x111u, false)] [InlineData(0x113u, true)]
    public void Actual_riichi_discard_is_sent_once_after_virtual_reach_and_replay_is_authoritative(uint action, bool tsumogiri)
    {
        using var h = new Harness(); h.Open(); h.Send(0x02BB, Draw());
        var old = h.Client;
        old.Emit("{\"type\":\"reach\",\"actor\":0}");
        old.Emit("{\"type\":\"dahai\",\"actor\":0,\"pai\":\"5sr\",\"tsumogiri\":false}");
        Assert.True(h.Bridge.TryChoose(h.Snapshot, out var choice));
        Assert.Equal(ActionKind.Riichi, choice.Kind); Assert.True(choice.DiscardIsRed);
        h.Send(0x0214, Discard(action));
        Assert.False(h.Bridge.CurrentHandQuarantined);
        Assert.Equal(24000, h.Snapshot.Scores[0]);
        Assert.Equal(1, h.Snapshot.RiichiSticks);
        Assert.Single(old.Sent.OfType<MjaiReach>());
        Assert.Equal(new MjaiDahai(0, "5sr", tsumogiri), Assert.Single(old.Sent.OfType<MjaiDahai>()));
        Assert.Single(old.Sent.OfType<MjaiReachAccepted>());
        h.Bridge.RecoverUnresponsiveDecision("test timeout"); h.Bridge.Update();
        var replay = h.Client.Replayed.Select(x => JsonDocument.Parse(x).RootElement.GetProperty("type").GetString()!).ToArray();
        Assert.Equal(["start_game", "start_kyoku", "tsumo", "reach", "dahai", "reach_accepted"], replay);
        Assert.Empty(h.Client.Sent); // The pre-roll must not duplicate the replay journal.
    }

    [Fact]
    public void Observed_self_white_draw_declaration_and_slot13_discard_stay_in_one_session()
    {
        // Anonymous state-equivalent reproduction of the captured self 0x113 window.
        using var h = new Harness();
        var opening = Opening();
        BinaryPrimitives.WriteInt32LittleEndian(opening.AsSpan(24), 3);
        int[] hand = [0, 1, 2, 11, 12, 13, 19, 20, 21, 22, 24, 25, 26];
        for (int i = 0; i < hand.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(opening.AsSpan(48 + i * 4), hand[i]);
        h.Send(0x0133, opening);
        var original = h.Client;
        var draw = Draw(); BinaryPrimitives.WriteInt32LittleEndian(draw, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(draw.AsSpan(8), 124);
        h.Send(0x02BB, draw);
        Assert.Equal(31, h.Snapshot.Hand[^1].Id);
        var discard = Discard(0x113); BinaryPrimitives.WriteInt32LittleEndian(discard, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(discard.AsSpan(12), 124);
        h.Send(0x0214, discard);
        Assert.Same(original, h.Client);
        Assert.True(original.IsRunning);
        Assert.False(h.Bridge.CurrentHandQuarantined);
        Assert.Equal(new MjaiDahai(0, "P", true), Assert.Single(original.Sent.OfType<MjaiDahai>()));
        Assert.Single(original.Sent.OfType<MjaiReach>());
        Assert.Single(original.Sent.OfType<MjaiReachAccepted>());
        Assert.Equal(hand, h.Snapshot.Hand.Select(t => (int)t.Id));
        Assert.True(h.Snapshot.OurRiichi);
        Assert.Equal(24000, h.Snapshot.Scores[0]);
        Assert.Equal(1, h.Snapshot.RiichiSticks);
        h.Send(0x018D, new byte[264]);
        Assert.Single(original.Sent.OfType<MjaiEndKyoku>());
        Assert.Equal(0, h.Bridge.DecisionTimeouts);
    }

    [Theory]
    [InlineData(0x140)]
    [InlineData(0x130)]
    [InlineData(0x113)]
    public void Unsupported_or_unsequenced_action_quarantines_then_resumes_at_observed_next_opening(int action)
    {
        using var h = new Harness(); h.Open();
        var old = h.Client; var late = old.ReactionReceivedSnapshot;
        h.Send(0x0214, Discard((uint)action));
        Assert.True(h.Bridge.CurrentHandQuarantined); Assert.False(old.IsRunning);
        long count = h.Bridge.ReactionsReceived;
        late?.Invoke("{\"type\":\"dahai\",\"actor\":0,\"pai\":\"1m\"}");
        Assert.Equal(count, h.Bridge.ReactionsReceived);
        h.Send(0x018D, new byte[264]); h.Open();
        Assert.False(h.Bridge.CurrentHandQuarantined);
        Assert.Single(h.Client.Sent.OfType<MjaiStartKyoku>());
        Assert.False(h.Bridge.TryChoose(h.Snapshot, out _));
    }

    [Fact]
    public void Invalid_opening_pool_is_skipped_and_next_valid_opening_can_resume()
    {
        using var h = new Harness(); h.Open(999);
        Assert.True(h.Bridge.CurrentHandQuarantined);
        Assert.DoesNotContain(h.Client.Sent, x => x is MjaiStartKyoku);
        h.Open(); Assert.False(h.Bridge.CurrentHandQuarantined);
    }

    [Theory]
    [InlineData(990, 1)]
    [InlineData(980, 2)]
    public void Carried_sticks_reach_mortal_and_do_not_skip_the_whole_hand(int hundreds, int sticks)
    {
        using var h = new Harness(); h.Open(hundreds);
        Assert.False(h.Bridge.CurrentHandQuarantined);
        Assert.Equal(sticks, Assert.Single(h.Client.Sent.OfType<MjaiStartKyoku>()).Kyotaku);
        h.Send(0x02BB, Draw());
        h.Client.Emit("""{"type":"dahai","actor":0,"pai":"5sr"}""");
        Assert.True(h.Bridge.TryChoose(h.Snapshot, out var choice));
        Assert.Equal(ActionKind.Discard, choice.Kind); Assert.True(choice.DiscardIsRed);
        h.Bridge.RecoverUnresponsiveDecision("test timeout"); h.Bridge.Update();
        var opening = h.Client.Replayed.Select(x => JsonDocument.Parse(x).RootElement)
            .Single(x => x.GetProperty("type").GetString() == "start_kyoku");
        Assert.Equal(sticks, opening.GetProperty("kyotaku").GetInt32());
    }

    [Fact]
    public void Missing_opening_and_red_identity_conflict_cannot_expose_recommendations()
    {
        using var h = new Harness(); h.Send(0x02BB, Draw());
        Assert.True(h.Bridge.CurrentHandQuarantined);
        h.Open(); h.Send(0x02BB, Draw());
        h.Client.Emit("{\"type\":\"dahai\",\"actor\":0,\"pai\":\"5sr\"}");
        Assert.True(h.Bridge.TryChoose(h.Snapshot, out _));
        var wrong = h.Snapshot with { HandIsRed = new bool[h.Snapshot.Hand.Count] };
        Assert.False(h.Bridge.TryGetRecommendation(wrong, out _, out _));
        Assert.True(h.Bridge.TryGetRecommendation(h.Snapshot, out _, out _));
        h.Config.Update(c => c with { MortalLimitedTrial = false }); h.Capture.RefreshProfile(); h.Bridge.Update();
        Assert.False(h.Bridge.Enabled); Assert.False(h.Client.IsRunning);
        Assert.False(h.Bridge.TryGetRecommendation(h.Snapshot, out _, out _));
    }

    [Fact]
    public void Queue_delivery_failure_isolates_current_hand()
    {
        using var h = new Harness(); h.Open(); h.Client.RejectInput = true;
        h.Send(0x02BB, Draw());
        Assert.True(h.Bridge.CurrentHandQuarantined); Assert.False(h.Client.IsRunning);
    }

    [Fact]
    public void New_hand_does_not_reuse_a_recommendation_for_an_identical_hand()
    {
        using var h = new Harness(); h.Open(); h.Send(0x02BB, Draw());
        h.Client.Emit("{\"type\":\"dahai\",\"actor\":0,\"pai\":\"5sr\"}");
        Assert.True(h.Bridge.TryChoose(h.Snapshot, out _));
        h.Send(0x018D, new byte[264]); h.Open(); h.Send(0x02BB, Draw());
        Assert.False(h.Bridge.TryChoose(h.Snapshot, out _));
    }

    [Fact]
    public void Manual_discard_that_declines_model_riichi_does_not_continue_in_a_false_reach_state()
    {
        using var h = new Harness(); h.Open(); h.Send(0x02BB, Draw());
        h.Client.Emit("{\"type\":\"reach\",\"actor\":0}");
        h.Client.Emit("{\"type\":\"dahai\",\"actor\":0,\"pai\":\"5sr\"}");
        h.Send(0x0214, Discard(0x110));
        Assert.True(h.Bridge.CurrentHandQuarantined);
        Assert.False(h.Client.IsRunning);
    }

    [Fact]
    public void Latest_complete_capture_runs_through_production_trial_bridge_including_both_draw_results()
    {
        using var h = new Harness();
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "RegressionFixtures", "20260924-draw-result-match.json")));
        var initialClient = h.Client;
        foreach (var row in data.RootElement.GetProperty("packets").EnumerateArray())
        {
            ushort opcode = Convert.ToUInt16(row.GetProperty("opcode").GetString()![2..], 16);
            h.Send(opcode, Convert.FromHexString(row.GetProperty("payload_hex").GetString()!));
            Assert.Same(initialClient, h.Client);
            Assert.True(h.Client.IsRunning, $"Quarantined at packet {row.GetProperty("sequence")}: {h.Bridge.Status}");
        }
        Assert.Equal(8, h.Client.Sent.OfType<MjaiStartKyoku>().Count());
        Assert.Equal(8, h.Client.Sent.OfType<MjaiEndKyoku>().Count());
        Assert.Equal(103, h.Client.Sent.OfType<MjaiTsumo>().Count(e => e.Actor == 0));
        Assert.Equal(104, h.Client.Sent.OfType<MjaiDahai>().Count(e => e.Actor == 0));
        Assert.True(h.Bridge.CurrentHandQuarantined); // Last actual result closes the hand.
    }

    [Fact]
    public void Real_layout_switch_trace_preserves_session_including_observed_opponent_kan()
    {
        var observation = AddonEmjObservation.Empty;
        using var h = new Harness(() => observation.ProtocolVariant);
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "RegressionFixtures", "20260924-addon-layout-switch.json")));
        int layoutChanges = 0;
        bool decodedObservedKan = false;
        foreach (var row in data.RootElement.GetProperty("events").EnumerateArray())
        {
            if (row.GetProperty("kind").GetString() == "layout")
            {
                observation = new(true, true, 1, 100, 100, DateTime.UtcNow.Ticks, "poll",
                    row.GetProperty("addon_name").GetString());
                var layout = row.GetProperty("layout").GetString();
                Assert.Equal("Emj", observation.ProtocolVariant);
                Assert.Contains(layout, new[] { "Emj", "EmjL" });
                layoutChanges++;
                var previousClient = h.Client;
                h.Capture.RefreshProfile(); h.Bridge.Update();
                if (previousClient is not null) Assert.Same(previousClient, h.Client);
                Assert.True(h.Client.IsRunning);
            }
            else
            {
                ushort opcode = Convert.ToUInt16(row.GetProperty("opcode").GetString()![2..], 16);
                h.Send(opcode, Convert.FromHexString(row.GetProperty("payload_hex").GetString()!));
                if (row.GetProperty("sequence").GetInt32() == 200)
                {
                    Assert.False(h.Bridge.CurrentHandQuarantined);
                    Assert.Single(h.Client.Sent.OfType<MjaiAnkan>());
                    decodedObservedKan = true;
                }
            }
        }
        Assert.Equal(6, layoutChanges);
        Assert.True(decodedObservedKan);
        Assert.Single(h.Clients); // Opponent kan no longer interrupts the model session.
        Assert.Equal(3, h.Clients.SelectMany(c => c.Sent).OfType<MjaiStartKyoku>().Count());
        Assert.Equal(3, h.Clients.SelectMany(c => c.Sent).OfType<MjaiEndKyoku>().Count());
        Assert.Equal(259, h.Bridge.PacketsProcessed);
        Assert.Contains(h.Client.Sent, e => e is MjaiDora);
        // An actual addon change still stops the session; a layout alias never did.
        observation = observation with { AddonName = "EmjL" };
        h.Capture.RefreshProfile(); h.Bridge.Update();
        Assert.False(h.Bridge.Enabled); Assert.False(h.Client.IsRunning);
    }

    [Fact]
    public void Unloaded_or_unknown_addon_cannot_keep_a_protocol_identity()
    {
        Assert.Null(AddonEmjObservation.Empty.ProtocolVariant);
        var observed = new AddonEmjObservation(true, true, 1, 0, 0, 0, "PostSetup", "Emj");
        Assert.Equal("Emj", observed.ProtocolVariant);
        Assert.Null((observed with { Present = false }).ProtocolVariant);
        Assert.Null((observed with { Address = 0 }).ProtocolVariant);
        Assert.Null((observed with { AddonName = null }).ProtocolVariant);
        var metadata = new MatchArchiveEnvironment(Profile.GameVersion, observed.ProtocolVariant,
            false, "trial", "build", "EmjL");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(metadata));
        Assert.Equal("Emj", json.RootElement.GetProperty("client_variant").GetString());
        Assert.Equal("EmjL", json.RootElement.GetProperty("ui_layout").GetString());
    }

    [Theory]
    [InlineData(ActionFlags.MinKan)] // Legacy generic UI label must not block an already queued discard.
    [InlineData(ActionFlags.AnKan)]
    [InlineData(ActionFlags.ShouMinKan)]
    public void Self_kan_offer_consumes_queued_discard_without_waiting_for_opponent(ActionFlags kan)
    {
        using var h = new Harness(); h.Open(); h.Send(0x02BB, Draw());
        var snapshot = h.Snapshot with { AddonStateCode = 6,
            Legal = new LegalActions(ActionFlags.Discard | kan | ActionFlags.Pass, [], [], [], []) };
        h.Client.Emit("""{"type":"dahai","actor":0,"pai":"5sr"}""");
        Assert.True(h.Bridge.TryChoose(snapshot, out var choice));
        Assert.Equal(ActionKind.Discard, choice.Kind);
        Assert.True(choice.DiscardIsRed);
        Assert.Equal(1, h.Bridge.DecisionsMapped);
        Assert.Equal(0, h.Bridge.DecisionTimeouts);
        Assert.Single(h.Clients);
        Assert.False(h.Bridge.HasExpiredOpponentPrompt(snapshot));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public void Transient_ron_then_real_call_consumes_queued_response_without_restarting(int sample, bool accept)
    {
        // Anonymous hand shapes from the three short Ron -> Pon/Chi transitions.
        int[][] hands =
        [
            [0, 0, 1, 5, 6, 12, 12, 20, 21, 22, 23, 32, 33],
            [1, 7, 7, 9, 10, 12, 13, 13, 21, 24, 30, 30, 32],
            [0, 0, 2, 4, 5, 9, 11, 14, 18, 19, 20, 23, 25],
        ];
        int[] claimed = [0, 30, 19];
        using var h = new Harness();
        var opening = Opening();
        for (int i = 0; i < 13; i++)
            BinaryPrimitives.WriteInt32LittleEndian(opening.AsSpan(48 + i * 4), hands[sample][i]);
        h.Send(0x0133, opening);
        var discard = Discard(0x110);
        BinaryPrimitives.WriteInt32LittleEndian(discard, sample == 2 ? 3 : 2);
        BinaryPrimitives.WriteUInt16LittleEndian(discard.AsSpan(12), (ushort)(claimed[sample] * 4));
        h.Send(0x0214, discard);
        var pending = h.Snapshot with { AddonStateCode = 15,
            Legal = new LegalActions(ActionFlags.Ron | ActionFlags.Pass, [], [], [], []) };
        var observed = pending with { Legal = pending.Legal with {
            Flags = (sample == 2 ? ActionFlags.Chi : ActionFlags.Pon) | ActionFlags.Pass } };
        var current = AutoPlayLoop.CarryForwardTerminalWin(pending, observed);
        Assert.Same(observed, current);
        Assert.Equal(TimeSpan.FromSeconds(5), AutoPlayLoop.MortalDecisionTimeout(current.Legal.Flags));
        string[] calls =
        [
            """{"type":"pon","actor":0,"target":2,"pai":"1m","consumed":["1m","1m"]}""",
            """{"type":"pon","actor":0,"target":2,"pai":"N","consumed":["N","N"]}""",
            """{"type":"chi","actor":0,"target":3,"pai":"2s","consumed":["1s","3s"]}""",
        ];
        h.Client.Emit(accept ? calls[sample] : """{"type":"none"}""");
        // Hint refresh can see the 12-16 ms Ron frame before Auto-play runs.
        h.Bridge.RefreshRecommendation(pending);
        Assert.False(h.Bridge.TryGetRecommendation(pending, out _, out _));
        Assert.False(h.Bridge.TryChoose(pending, out _));
        Assert.True(h.Bridge.TryChoose(current, out var choice));
        Assert.Equal(accept ? (sample == 2 ? ActionKind.Chi : ActionKind.Pon) : ActionKind.Pass, choice.Kind);
        Assert.True(AutoPlayLoop.TryResolveCallOption(current, observed, choice.Kind, out int option));
        Assert.Equal(accept ? 0 : 1, option);
        Assert.Equal(1, h.Bridge.DecisionsMapped);
        Assert.Equal(0, h.Bridge.DecisionTimeouts);
        Assert.Single(h.Clients);
        Assert.Empty(h.Client.Replayed);
    }

    [Fact]
    public void Stale_pass_does_not_hide_a_queued_real_win_response()
    {
        using var h = new Harness(); h.Open();
        var discard = Discard(0x110);
        BinaryPrimitives.WriteInt32LittleEndian(discard, 3);
        h.Send(0x0214, discard);
        var win = h.Snapshot with { AddonStateCode = 15,
            Legal = new LegalActions(ActionFlags.Ron | ActionFlags.Pass, [], [], [], []) };
        h.Client.Emit("""{"type":"none"}""");
        h.Client.Emit("""{"type":"hora","actor":0,"target":3}""");
        Assert.True(h.Bridge.TryChoose(win, out var choice));
        Assert.Equal(ActionKind.Ron, choice.Kind);
        var ordinary = win with { Legal = new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [], []) };
        Assert.False(h.Bridge.TryChoose(ordinary, out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Deferred_win_conflict_cannot_leak_into_next_event_or_restarted_session(bool restart)
    {
        using var h = new Harness(); h.Open();
        var discard = Discard(0x110);
        BinaryPrimitives.WriteInt32LittleEndian(discard, 3);
        h.Send(0x0214, discard);
        var win = h.Snapshot with { AddonStateCode = 15,
            Legal = new LegalActions(ActionFlags.Ron | ActionFlags.Pass, [], [], [], []) };
        h.Client.Emit("""{"type":"none"}""");
        Assert.False(h.Bridge.TryChoose(win, out _));
        if (restart)
        {
            h.Bridge.RecoverUnresponsiveDecision("regression restart");
            h.Bridge.Update();
        }
        else
        {
            var draw = Draw(); BinaryPrimitives.WriteInt32LittleEndian(draw, 2);
            h.Send(0x02BB, draw);
        }
        h.Send(0x0214, discard);
        var prompt = h.Snapshot with { AddonStateCode = 15,
            Legal = new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [], []) };
        Assert.False(h.Bridge.TryChoose(prompt, out _));
        h.Client.Emit("""{"type":"none"}""");
        Assert.True(h.Bridge.TryChoose(prompt, out var choice));
        Assert.Equal(ActionKind.Pass, choice.Kind);
        Assert.Equal(1, h.Bridge.DecisionsMapped);
    }

    [Fact]
    public void Only_confirmed_following_draw_expires_an_opponent_prompt_and_next_discard_reopens_it()
    {
        using var h = new Harness(); h.Open();
        StateSnapshot Prompt() => h.Snapshot with { AddonStateCode = 15,
            Legal = new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [], []) };
        Assert.False(h.Bridge.HasExpiredOpponentPrompt(Prompt())); // Missing history alone cannot cancel.
        var discard = Discard(0x110);
        BinaryPrimitives.WriteInt32LittleEndian(discard, 3);
        h.Send(0x0214, discard);
        Assert.False(h.Bridge.HasExpiredOpponentPrompt(Prompt()));
        var draw = Draw(); BinaryPrimitives.WriteInt32LittleEndian(draw, 1);
        h.Send(0x02BB, draw);
        Assert.True(h.Bridge.HasExpiredOpponentPrompt(Prompt()));
        Assert.False(h.Bridge.HasExpiredOpponentPrompt(Prompt() with { PublicStateConsistent = false }));
        Assert.False(h.Bridge.HasExpiredOpponentPrompt(Prompt() with {
            Legal = new LegalActions(ActionFlags.Ron | ActionFlags.Pass, [], [], [], []) }));
        Assert.False(h.Bridge.HasExpiredOpponentPrompt(Prompt() with { Hand = [] }));
        h.Send(0x0214, discard);
        Assert.False(h.Bridge.HasExpiredOpponentPrompt(Prompt()));
        h.Client.Emit("""{"type":"none"}""");
        Assert.True(h.Bridge.TryChoose(Prompt(), out var choice));
        Assert.Equal(ActionKind.Pass, choice.Kind);
        h.Send(0x018D, new byte[264]);
        Assert.False(h.Bridge.HasExpiredOpponentPrompt(Prompt()));
        Assert.Single(h.Clients); Assert.Equal(0, h.Bridge.DecisionTimeouts);
    }

    [Theory]
    [InlineData(1, 2)] [InlineData(2, 1)] [InlineData(3, 1)] [InlineData(4, 1)]
    public void Captured_carried_pool_and_combined_riichi_hands_are_admitted_until_result(int index, int sticks)
    {
        using var h = new Harness();
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "RegressionFixtures", $"20260925-carried-sticks-{index}.json")));
        var original = h.Client;
        foreach (var row in data.RootElement.GetProperty("packets").EnumerateArray())
        {
            h.Send(Convert.ToUInt16(row.GetProperty("opcode").GetString()![2..], 16),
                Convert.FromHexString(row.GetProperty("payload_hex").GetString()!));
            Assert.Same(original, h.Client);
            Assert.True(h.Client.IsRunning, $"Unexpected isolation at {row.GetProperty("sequence")}: {h.Bridge.Status}");
        }
        Assert.Equal(sticks, Assert.Single(h.Client.Sent.OfType<MjaiStartKyoku>()).Kyotaku);
        Assert.Single(h.Client.Sent.OfType<MjaiEndKyoku>());
        Assert.Single(h.Clients);
        Assert.Equal(0, h.Bridge.DecisionTimeouts);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Confirmed_added_kan_allows_robbed_kan_win_but_rinshan_closes_that_window(bool rinshan)
    {
        using var h = new Harness();
        var opening = Opening();
        // Closed sanshoku, pair E, 4s/6s waiting on 5s; owns none of the kan tile.
        int[] hand = [0, 1, 2, 9, 10, 11, 18, 19, 20, 27, 27, 21, 23];
        BinaryPrimitives.WriteInt32LittleEndian(opening.AsSpan(28), 31);
        for (int i = 0; i < hand.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(opening.AsSpan(48 + 4 * i), hand[i]);
        h.Send(0x0133, opening);
        var discard = Discard(0x110);
        BinaryPrimitives.WriteInt32LittleEndian(discard, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(discard.AsSpan(12), 89);
        h.Send(0x0214, discard);
        var pon = new byte[24]; BinaryPrimitives.WriteInt32LittleEndian(pon, 1);
        BinaryPrimitives.WriteInt32LittleEndian(pon.AsSpan(4), 0x500);
        BinaryPrimitives.WriteUInt16LittleEndian(pon.AsSpan(12), 88);
        BinaryPrimitives.WriteUInt16LittleEndian(pon.AsSpan(14), 88);
        h.Send(0x02BB, pon);
        var afterPon = Discard(0xA10); BinaryPrimitives.WriteInt32LittleEndian(afterPon, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(afterPon.AsSpan(12), 120);
        h.Send(0x0214, afterPon);
        var draw = Draw(); BinaryPrimitives.WriteInt32LittleEndian(draw, 1);
        h.Send(0x02BB, draw);
        var added = Discard(0x140); BinaryPrimitives.WriteInt32LittleEndian(added, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(added.AsSpan(16), 88);
        h.Send(0x0214, added);
        Assert.False(h.Bridge.CurrentHandQuarantined);
        Assert.False(h.Bridge.HasAuthoritativeOpponentDiscard); // A kakan is not a river tile.
        Assert.Single(h.Client.Sent.OfType<MjaiKakan>());
        Assert.Equal(2, h.Client.Sent.OfType<MjaiDahai>().Count());
        if (rinshan)
        {
            BinaryPrimitives.WriteInt32LittleEndian(draw.AsSpan(4), 0x200);
            BinaryPrimitives.WriteUInt16LittleEndian(draw.AsSpan(10), 8);
            h.Send(0x02BB, draw);
            Assert.Equal(new MjaiDora("3m"), h.Client.Sent[^2]);
            Assert.Equal(new MjaiTsumo(1, "?"), h.Client.Sent[^1]);
        }
        var prompt = h.Snapshot with { Legal = new LegalActions(ActionFlags.Ron | ActionFlags.Pass, [], [], [], []) };
        h.Client.Emit("""{"type":"hora","actor":0,"target":2}""");
        Assert.False(h.Bridge.TryChoose(prompt, out _));
        h.Client.Emit("""{"type":"hora","actor":0,"target":1}""");
        Assert.Equal(!rinshan, h.Bridge.TryChoose(prompt, out var choice));
        if (!rinshan) Assert.Equal(ActionKind.Ron, choice.Kind);
        Assert.Single(h.Clients);
        Assert.Empty(h.Client.Replayed);
    }

    [Fact]
    public void Missing_kan_replacement_isolates_until_observed_next_hand()
    {
        using var h = new Harness(); h.Open();
        var draw = Draw(); BinaryPrimitives.WriteInt32LittleEndian(draw, 1);
        h.Send(0x02BB, draw);
        var kan = (byte[])draw.Clone(); BinaryPrimitives.WriteInt32LittleEndian(kan.AsSpan(4), 0x130);
        for (int offset = 12; offset < 20; offset += 2) BinaryPrimitives.WriteUInt16LittleEndian(kan.AsSpan(offset), 124);
        h.Send(0x02BB, kan);
        h.Send(0x02BB, Draw()); // Next ordinary draw cannot skip rinshan/dora.
        Assert.True(h.Bridge.CurrentHandQuarantined);
        Assert.False(h.Client.IsRunning);
        h.Open(); h.Send(0x02BB, Draw());
        Assert.False(h.Bridge.CurrentHandQuarantined);
        Assert.Equal(2, h.Clients.Count);
        h.Client.Emit("""{"type":"dahai","actor":0,"pai":"5sr"}""");
        Assert.True(h.Bridge.TryChoose(h.Snapshot, out _));
        Assert.DoesNotContain(h.Client.Sent, e => e is MjaiAnkan or MjaiDora);
    }

    private static List<CapturedMahjongPacket> Drain(MahjongNetworkCapture capture)
    {
        List<CapturedMahjongPacket> result = [];
        while (capture.TryDequeue(out var packet)) result.Add(packet);
        return result;
    }
    private static RawReceivedPacket Packet(ushort opcode, byte[] bytes) =>
        new(DateTimeOffset.UtcNow, 0, opcode, bytes.Length, bytes, "deucalion");
    private static byte[] Opening(int scoreTotal = 1000)
    {
        var bytes = new byte[104];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 22);
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32 + i * 4), i == 0 ? scoreTotal - 750 : 250);
        int[] hand = [8, 18, 22, 9, 10, 31, 15, 24, 0, 6, 6, 19, 30];
        for (int i = 0; i < 13; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48 + i * 4), hand[i]);
        return bytes;
    }
    private static byte[] Draw()
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 0x100);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 89);
        return bytes;
    }
    private static byte[] Discard(uint action)
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), action);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 89);
        return bytes;
    }
    private sealed class Harness : IDisposable
    {
        public readonly DalamudConfigService Config = new(_ => { }, new Configuration
        { MortalEnabled = true, MortalLimitedTrial = true, MortalWslDistribution = "test", MortalWorkingDirectory = "/test" });
        public readonly MahjongNetworkCapture Capture;
        public readonly LiveMortalBridge Bridge;
        private readonly MahjongPacketMjaiDecoder decoder = new();
        private readonly PublicStateReducer state = new();
        public FakeClient Client = null!;
        public List<FakeClient> Clients = [];
        public StateSnapshot Snapshot => state.Snapshot(new LegalActions(ActionFlags.Discard | ActionFlags.Riichi, [], [], [], []));
        public Harness(Func<string?>? variant = null)
        {
            Capture = new(Profile.GameVersion, variant ?? (() => "Emj"), [Profile], () => Config.Current.MortalLimitedTrial) { TransportReady = true };
            Bridge = new(Capture, DispatchProxy.Create<IFramework, FrameworkStub>(), Config, () => null, new StubPluginLog())
                { ClientFactory = () => { Client = new FakeClient(); Clients.Add(Client); return Client; } };
            Bridge.Update();
        }
        public void Open(int scoreTotal = 1000) => Send(0x0133, Opening(scoreTotal));
        public void Send(ushort opcode, byte[] bytes)
        {
            try { foreach (var evt in decoder.Process(Profile.Packets[$"0x{opcode:X4}"].MessageId, bytes)) state.Apply(evt); }
            catch (InvalidDataException) { }
            Capture.Record(Packet(opcode, bytes)); Bridge.Update();
        }
        public void Dispose() { Bridge.Dispose(); Capture.Dispose(); }
    }
    public class FrameworkStub : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }
    private sealed class FakeClient : IMortalProcessClient
    {
        public event Action<string>? ReactionReceived;
        public event Action<int?>? Exited { add { } remove { } }
        public Action<string>? ReactionReceivedSnapshot => ReactionReceived;
        public bool IsRunning { get; private set; }
        public bool RejectInput;
        public List<IMjaiEvent> Sent = [];
        public List<string> Replayed = [];
        public void Start(MortalProcessSettings settings) => IsRunning = true;
        public void Send(IMjaiEvent evt) { if (RejectInput) throw new IOException("full"); Sent.Add(evt); }
        public void SendReplay(string evt) => Replayed.Add(evt);
        public void Poll() { }
        public void Emit(string json) => ReactionReceived?.Invoke(json);
        public void Dispose() => IsRunning = false;
    }
}

using Mahjong.Plugin.Dalamud.Actions;
using Newtonsoft.Json;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Mahjong.Plugin.Dalamud.Tests;

public class ContinuousCollectionTests
{
    private static readonly CollectionObservation Idle = new()
    {
        LoggedIn = true, SameCharacter = true, AutomationAllowed = true, CanQueue = true,
    };
    private static readonly CollectionObservation Queued = Idle with
    {
        CanQueue = false, OwnQueue = true, QueueState = CollectionQueueState.Queued,
    };
    private static readonly CollectionObservation Pop = Queued with
    {
        QueueState = CollectionQueueState.Ready, OwnPop = true, CanAccept = true,
    };
    private static readonly CollectionObservation Playing = Idle with
    {
        CanQueue = false, InTargetDuty = true, AtTable = true, QueueState = CollectionQueueState.InContent,
    };
    private sealed class Clock : TimeProvider
    {
        private long seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => seconds;
        public void Advance(int amount) => seconds += amount;
    }

    [Fact]
    public void Complete_match_queues_confirms_leaves_and_repeats_without_day_limit()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        Assert.Equal(CollectionAction.Queue, c.Tick(true, Idle));
        Assert.Equal(CollectionAction.None, c.Tick(true, Idle));
        Assert.Equal(CollectionAction.None, c.Tick(true, Queued));
        c.Tick(true, Pop); clock.Advance(1);
        Assert.Equal(CollectionAction.Accept, c.Tick(true, Pop));
        Assert.Equal(CollectionAction.None, c.Tick(true, Pop));
        c.Tick(true, Playing);
        clock.Advance(90000);
        var final = Playing with { FinalResult = true, CanLeave = true };
        Assert.Equal(CollectionAction.None, c.Tick(true, final)); clock.Advance(5);
        Assert.Equal(CollectionAction.Leave, c.Tick(true, final));
        Assert.Equal(CollectionAction.None, c.Tick(true, final));
        c.Tick(true, Idle); Assert.Equal(1, c.CompletedMatches); clock.Advance(10);
        Assert.Equal(CollectionAction.Queue, c.Tick(true, Idle));
    }

    [Fact]
    public void Stop_cancels_immediately_despite_registration_cooldown()
    {
        var c = new ContinuousCollectionController(new Clock()); c.Tick(true, Idle);
        Assert.Equal(CollectionAction.CancelQueue, c.Tick(false, Queued));
        Assert.Equal(CollectionAction.None, c.Tick(false, Pop));
    }

    [Fact]
    public void Stop_before_ack_still_cancels_late_queue()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        c.Tick(true, Idle); c.Tick(false, Idle); clock.Advance(2);
        Assert.Equal(CollectionAction.CancelQueue, c.Tick(false, Queued));
    }

    [Fact]
    public void Stop_finishes_current_match_but_never_requeues()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        c.Tick(true, Playing); Assert.Equal(CollectionAction.None, c.Tick(false, Playing));
        var final = Playing with { FinalResult = true, CanLeave = true };
        c.Tick(false, final); clock.Advance(5);
        Assert.Equal(CollectionAction.Leave, c.Tick(false, final));
        c.Tick(false, Idle); clock.Advance(100);
        Assert.Equal(CollectionAction.None, c.Tick(false, Idle));
    }

    [Fact]
    public void Stop_after_acceptance_finishes_arriving_match_without_withdrawing()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        c.Tick(true, Queued);
        Assert.Equal(CollectionAction.None, c.Tick(false, Queued with { QueueState = CollectionQueueState.Accepted }));
        c.Tick(false, Playing);
        var final = Playing with { FinalResult = true, CanLeave = true };
        c.Tick(false, final); clock.Advance(5);
        Assert.Equal(CollectionAction.Leave, c.Tick(false, final));
    }

    [Fact]
    public void Off_stops_even_final_exit()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        c.Tick(true, Playing);
        var final = Playing with { FinalResult = true, CanLeave = true, AutomationAllowed = false };
        c.Tick(false, final); clock.Advance(100);
        Assert.Equal(CollectionAction.None, c.Tick(false, final));
    }

    [Theory]
    [InlineData(false, true)] [InlineData(true, false)] [InlineData(false, false)]
    public void Unknown_or_foreign_queue_and_pop_never_confirm(bool ownQueue, bool ownPop)
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        var foreign = Pop with { OwnQueue = ownQueue, OwnPop = ownPop };
        Assert.Equal(CollectionAction.None, c.Tick(true, foreign)); clock.Advance(10);
        Assert.Equal(CollectionAction.None, c.Tick(true, foreign)); Assert.NotNull(c.Fault);
    }

    [Fact]
    public void Stopping_does_not_cancel_unowned_queue_or_mismatched_pop()
    {
        var c = new ContinuousCollectionController(new Clock());
        Assert.Equal(CollectionAction.None, c.Tick(false, Queued)); c.Tick(true, Queued);
        Assert.Equal(CollectionAction.None, c.Tick(false, Queued with { OwnQueue = false }));
        Assert.Equal(CollectionAction.None, c.Tick(false, Pop with { OwnPop = false }));
    }

    [Fact]
    public void Reload_adopts_matching_queue_and_confirms_once()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        Assert.Equal(CollectionAction.None, c.Tick(true, Pop)); clock.Advance(1);
        Assert.Equal(CollectionAction.Accept, c.Tick(true, Pop));
        for (int i = 0; i < 100; i++) Assert.Equal(CollectionAction.None, c.Tick(true, Pop));
    }

    [Fact]
    public void Registration_retries_are_bounded()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        for (int i = 0; i < 3; i++) { Assert.Equal(CollectionAction.Queue, c.Tick(true, Idle)); clock.Advance(20); }
        Assert.Equal(CollectionAction.None, c.Tick(true, Idle)); Assert.NotNull(c.Fault);
    }

    [Fact]
    public void Pending_registration_timeout_stops_and_withdraws()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock); c.Tick(true, Idle);
        var pending = Queued with { QueueState = CollectionQueueState.Pending };
        c.Tick(true, pending); clock.Advance(45);
        Assert.Equal(CollectionAction.None, c.Tick(true, pending)); Assert.NotNull(c.Fault);
        Assert.Equal(CollectionAction.CancelQueue, c.Tick(false, pending));
    }

    [Fact]
    public void Missing_or_disabled_commence_button_times_out_without_clicking()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        var pop = Pop with { CanAccept = false }; c.Tick(true, pop); clock.Advance(30);
        Assert.Equal(CollectionAction.None, c.Tick(true, pop)); Assert.NotNull(c.Fault);
        Assert.Equal(CollectionAction.CancelQueue, c.Tick(false, pop));
    }

    [Fact]
    public void Acceptance_retries_are_bounded()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        c.Tick(true, Pop); clock.Advance(1);
        for (int i = 0; i < 3; i++) { Assert.Equal(CollectionAction.Accept, c.Tick(true, Pop)); clock.Advance(5); }
        Assert.Equal(CollectionAction.None, c.Tick(true, Pop)); Assert.NotNull(c.Fault);
    }

    [Fact]
    public void Final_result_must_remain_visible_in_target_duty()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        var final = Playing with { FinalResult = true, CanLeave = true };
        c.Tick(true, final); clock.Advance(4); c.Tick(true, Playing); clock.Advance(5);
        Assert.Equal(CollectionAction.None, c.Tick(true, final)); clock.Advance(4);
        Assert.Equal(CollectionAction.None, c.Tick(true, final)); clock.Advance(1);
        Assert.Equal(CollectionAction.Leave, c.Tick(true, final));
        Assert.Equal(CollectionAction.None, c.Tick(true, final with { InTargetDuty = false, InOtherDuty = true }));
    }

    [Fact]
    public void Cannot_leave_hand_result_or_unconfirmed_final()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock);
        c.Tick(true, Playing); clock.Advance(100);
        Assert.Equal(CollectionAction.None, c.Tick(true, Playing with { CanLeave = true }));
        var final = Playing with { FinalResult = true, CanLeave = false };
        c.Tick(true, final); clock.Advance(100);
        Assert.Equal(CollectionAction.None, c.Tick(true, final));
    }

    [Fact]
    public void Capture_failure_stops_new_queue_but_finishes_match()
    {
        var clock = new Clock(); var c = new ContinuousCollectionController(clock); c.Tick(true, Playing);
        c.Tick(true, Playing with { FatalReason = "capture failed" }); Assert.Equal("capture failed", c.Fault);
        var final = Playing with { FinalResult = true, CanLeave = true };
        c.Tick(false, final); clock.Advance(5); Assert.Equal(CollectionAction.Leave, c.Tick(false, final));
        c.Tick(false, Idle); clock.Advance(100); Assert.Equal(CollectionAction.None, c.Tick(false, Idle));
    }

    [Fact]
    public void Character_change_never_touches_another_characters_queue()
    {
        var c = new ContinuousCollectionController(new Clock()); c.Tick(true, Queued);
        Assert.Equal(CollectionAction.None, c.Tick(true, Pop with { SameCharacter = false }));
        Assert.False(c.NeedsUpdate); Assert.NotNull(c.Fault);
    }

    [Fact]
    public void Logged_out_or_busy_character_waits()
    {
        var c = new ContinuousCollectionController(new Clock());
        Assert.Equal(CollectionAction.None, c.Tick(true, Idle with { LoggedIn = false }));
        Assert.Equal(CollectionAction.None, c.Tick(true, Idle with { CanQueue = false }));
        Assert.Equal(CollectionAction.Queue, c.Tick(true, Idle));
    }

    [Fact]
    public void Rapid_restart_preserves_cooldown()
    {
        var c = new ContinuousCollectionController(new Clock()); c.Tick(true, Idle);
        c.Restart(); Assert.Equal(CollectionAction.None, c.Tick(true, Idle));
    }

    [Fact]
    public void Existing_config_is_opt_out_and_enabled_switch_round_trips()
    {
        var old = JsonConvert.DeserializeObject<Configuration>("{\"Version\":3,\"MortalEnabled\":true}")!;
        Assert.False(old.ContinuousCollection); Assert.Equal(0ul, old.CollectionCharacterId); Assert.True(old.MortalEnabled);
        var enabled = old with { ContinuousCollection = true, CollectionCharacterId = 123 };
        var reload = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(enabled))!;
        Assert.True(reload.ContinuousCollection); Assert.Equal(123ul, reload.CollectionCharacterId); Assert.True(reload.MortalEnabled);
    }
    [Theory]
    [InlineData(766, true)] [InlineData(767, false)] [InlineData(643, false)]
    [InlineData(768, false)] [InlineData(0, false)]
    public unsafe void Native_queue_identity_accepts_only_solo_novice_east(uint id, bool expected)
    {
        ContentsFinderQueueInfo queue = default;
        queue.QueuedEntries[0] = new ContentsId { ContentType = ContentsType.Regular, Id = id };
        Assert.Equal(expected, ContinuousCollectionLoop.IsOnlyTargetQueue(&queue));
    }

    [Fact]
    public unsafe void Empty_roulette_or_multiple_duty_queue_is_rejected()
    {
        ContentsFinderQueueInfo queue = default;
        Assert.False(ContinuousCollectionLoop.IsOnlyTargetQueue(&queue));
        queue.QueuedEntries[0] = new ContentsId { ContentType = ContentsType.Roulette, Id = 766 };
        Assert.False(ContinuousCollectionLoop.IsOnlyTargetQueue(&queue));
        queue.QueuedEntries[0] = new ContentsId { ContentType = ContentsType.Regular, Id = 766 };
        queue.QueuedContentRouletteId = 1;
        Assert.False(ContinuousCollectionLoop.IsOnlyTargetQueue(&queue));
        queue.QueuedContentRouletteId = 0;
        queue.QueuedEntries[1] = new ContentsId { ContentType = ContentsType.Regular, Id = 643 };
        Assert.False(ContinuousCollectionLoop.IsOnlyTargetQueue(&queue));
    }

}

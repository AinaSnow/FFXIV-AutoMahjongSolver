namespace Mahjong.Plugin.Dalamud.Actions;

internal enum CollectionQueueState { None, Pending, Queued, Ready, Accepted, InContent, Unknown }
internal enum CollectionAction { None, Queue, Accept, Leave, CancelQueue }
internal sealed record CollectionObservation
{
    public bool LoggedIn { get; init; }
    public bool SameCharacter { get; init; }
    public bool AutomationAllowed { get; init; }
    public bool CanQueue { get; init; }
    public bool CanAccept { get; init; }
    public bool OwnQueue { get; init; }
    public bool OwnPop { get; init; }
    public CollectionQueueState QueueState { get; init; }
    public bool InTargetDuty { get; init; }
    public bool InOtherDuty { get; init; }
    public bool AtTable { get; init; }
    public bool FinalResult { get; init; }
    public bool CanLeave { get; init; }
    public string? QueueBlockReason { get; init; }
    public string? FatalReason { get; init; }
}

/// <summary>Pure state machine. No delayed game callbacks; the adapter observes and acts on the same frame.</summary>
internal sealed class ContinuousCollectionController(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long nextActionAt;
    private long? finalSince, queueSentAt, readySince;
    private int queueAttempts, acceptAttempts, leaveAttempts, cancelAttempts;
    private bool ownedQueue, finishingMatch;
    private CollectionQueueState previousQueue;
    public string Status { get; private set; } = "Stopped";
    public string? Fault { get; private set; }
    public bool NeedsUpdate => ownedQueue || finishingMatch;
    public int CompletedMatches { get; private set; }

    public void Restart()
    {
        Fault = null;
        queueAttempts = acceptAttempts = leaveAttempts = cancelAttempts = 0;
        finalSince = readySince = null;
        // Keep pending action cooldowns and match ownership across rapid stop/start toggles.
        Status = "Waiting to queue player East-only mahjong";
    }

    public void Fail(string reason) { Fault = reason; Status = reason; }
    private bool Due => clock.GetTimestamp() >= nextActionAt;
    private bool Elapsed(long start, double seconds) => clock.GetElapsedTime(start) >= TimeSpan.FromSeconds(seconds);
    private CollectionAction Send(CollectionAction action, double cooldown, string status)
    {
        nextActionAt = clock.GetTimestamp() + (long)(cooldown * clock.TimestampFrequency);
        Status = status;
        return action;
    }

    public CollectionAction Tick(bool enabled, CollectionObservation s)
    {
        if (!s.LoggedIn) { finalSince = null; Status = "Waiting for login"; return CollectionAction.None; }
        if (!s.SameCharacter)
        {
            finishingMatch = ownedQueue = false;
            Fail("Character changed; start collection again on this character");
            return CollectionAction.None;
        }
        if (enabled && Fault is null)
        {
            if (s.FatalReason is { } reason) Fail(reason);
            else if (!s.AutomationAllowed) Fail("Auto-play disabled; collection stopped");
            else if (s.InOtherDuty) Fail("Another duty is active; collection stopped");
            else if (s.QueueState is CollectionQueueState.Pending or CollectionQueueState.Queued or CollectionQueueState.Ready
                && (!s.OwnQueue || (s.QueueState == CollectionQueueState.Ready && !s.OwnPop)))
                Fail("Another or unknown duty queue is active; collection stopped");
        }
        bool running = enabled && Fault is null && s.AutomationAllowed;
        if (running && s.OwnQueue && s.QueueState != CollectionQueueState.None) ownedQueue = true;
        if ((running || ownedQueue) && s.InTargetDuty && s.AtTable) finishingMatch = true;

        // Stopping only withdraws this mode's queue. Never withdraw an already accepted duty.
        if (!running && ownedQueue && s.OwnQueue && (s.QueueState != CollectionQueueState.Ready || s.OwnPop)
            && s.QueueState is CollectionQueueState.Pending or CollectionQueueState.Queued or CollectionQueueState.Ready)
        {
            if (cancelAttempts > 0 && !Due) return CollectionAction.None;
            if (cancelAttempts >= 3) { Fail("Queue withdrawal did not complete; cancel the mahjong queue manually"); return CollectionAction.None; }
            cancelAttempts++;
            return Send(CollectionAction.CancelQueue, 5, "Stopping: withdrawing mahjong queue");
        }
        if (!running && s.QueueState == CollectionQueueState.None && queueSentAt is { } stopping && Elapsed(stopping, 30))
            queueSentAt = null;
        if (s.QueueState == CollectionQueueState.None && queueSentAt is null) { ownedQueue = false; cancelAttempts = 0; }
        if (s.QueueState != previousQueue)
        {
            if (s.QueueState != CollectionQueueState.Ready) { acceptAttempts = 0; readySince = null; }
            else { readySince = clock.GetTimestamp(); nextActionAt = clock.GetTimestamp() + clock.TimestampFrequency; }
            previousQueue = s.QueueState;
        }
        if (s.QueueState is CollectionQueueState.Queued or CollectionQueueState.Ready or CollectionQueueState.Accepted)
        {
            queueSentAt = null;
            queueAttempts = 0;
        }

        if (s.InTargetDuty && s.AtTable)
        {
            queueSentAt = null;
            if (!s.FinalResult) { finalSince = null; Status = running ? "Playing and recording" : "Finishing current match; no new queue"; return CollectionAction.None; }
            if (!finishingMatch || !s.AutomationAllowed) return CollectionAction.None;
            finalSince ??= clock.GetTimestamp();
            Status = "Final standings; waiting to leave";
            if (!Elapsed(finalSince.Value, 5) || !s.CanLeave || !Due) return CollectionAction.None;
            if (leaveAttempts >= 3) { Fail("Could not leave final standings; leave the finished match manually"); return CollectionAction.None; }
            leaveAttempts++;
            return Send(CollectionAction.Leave, 10, "Leaving completed match");
        }
        finalSince = null;
        if (s.InTargetDuty || s.AtTable) { Status = "Waiting for table transition"; return CollectionAction.None; }
        if (finishingMatch)
        {
            if (leaveAttempts > 0) CompletedMatches++;
            finishingMatch = false;
            leaveAttempts = 0;
            nextActionAt = clock.GetTimestamp() + 10 * clock.TimestampFrequency;
        }
        if (!running) { Status = Fault ?? "Stopped"; return CollectionAction.None; }

        switch (s.QueueState)
        {
            case CollectionQueueState.Ready:
                Status = "Match found; waiting for Commence";
                if (readySince is { } ready && Elapsed(ready, 30))
                { Fail("Commence was not available within 30 seconds; collection stopped"); return CollectionAction.None; }
                if (!s.OwnPop || !s.CanAccept || !Due) return CollectionAction.None;
                if (acceptAttempts >= 3) { Fail("Commence did not complete; collection stopped"); return CollectionAction.None; }
                acceptAttempts++;
                return Send(CollectionAction.Accept, 5, "Confirming player East-only mahjong");
            case CollectionQueueState.Pending:
                Status = "Registering for mahjong";
                if (queueSentAt is { } pending && Elapsed(pending, 45)) Fail("Queue registration timed out; collection stopped");
                return CollectionAction.None;
            case CollectionQueueState.Queued:
                Status = "Queued for player East-only mahjong";
                return CollectionAction.None;
            case CollectionQueueState.Accepted:
            case CollectionQueueState.InContent:
                Status = "Waiting to enter mahjong";
                return CollectionAction.None;
            case CollectionQueueState.Unknown:
                Fail("Unknown matchmaking state; collection stopped");
                return CollectionAction.None;
        }
        if (!s.CanQueue) { Status = s.QueueBlockReason ?? "Waiting until character is free to queue"; return CollectionAction.None; }
        if (!Due || queueSentAt is { } sent && !Elapsed(sent, 20)) return CollectionAction.None;
        if (queueAttempts >= 3) { Fail("Queue registration was not accepted; check the game's error message"); return CollectionAction.None; }
        queueAttempts++;
        ownedQueue = true;
        queueSentAt = clock.GetTimestamp();
        return Send(CollectionAction.Queue, 20, "Registering for player East-only mahjong");
    }
}

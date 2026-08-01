using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using Mahjong.Engine;
using Mahjong.Plugin.Game;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Game.Mjai;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Mortal;

/// <summary>Drains captured packets, advances Mortal, and exposes legal UI decisions.</summary>
public sealed class LiveMortalBridge : IDisposable
{
    private static readonly TimeSpan RestartBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplayValidationWindow = TimeSpan.FromSeconds(10);

    private readonly MahjongNetworkCapture capture;
    private readonly IFramework framework;
    private readonly IConfigService<Configuration> configService;
    private readonly Func<StateSnapshot?> snapshotAccessor;
    private readonly IPluginLog log;
    private readonly ConcurrentQueue<MortalReaction> reactions = new();
    private readonly object reachLock = new();
    private readonly object recommendationLock = new();
    private readonly MjaiEventJournal journal = new();

    private MahjongPacketMjaiDecoder decoder = new();
    private MortalProcessClient? client;
    private MortalProcessSettings? activeSettings;
    private DateTime lastStartAttemptUtc = DateTime.MinValue;
    private bool awaitingReachDiscard;
    private string? optimisticRiichiTile;
    private MortalReaction? pendingKanReaction;
    private ActionChoice? recommendation;
    private bool? recommendationIsRed;
    private int recommendationKey;
    private bool hasRecommendation;
    private bool replayStateOnNextStart;
    private MjaiStartKyoku? pendingStartKyoku;
    private int[] pendingStartTileKinds = [];
    private bool waitingForNextHand;
    private bool quarantinedKyokuEnded;
    private volatile bool replayCrashDetected;
    private long replayValidationDeadlineTicks;
    private long lastModelEvalNanoseconds;
    private CapturedMahjongPacket? lastCapturedPacket;
    private MjaiDahai? lastNetworkDiscard;
    private MjaiDahai? recoveredPendingServerDiscard;
    private int? lastNetworkDrawActor;
    private readonly int[] observedDiscardCounts = [-1, -1, -1, -1];
    private int? latestUiDiscardSeat;
    private int latestUiDiscardCount = -1;
    private int lastCandidateCorrectionKey;
    private bool disposed;

    public LiveMortalBridge(
        MahjongNetworkCapture capture,
        IFramework framework,
        IConfigService<Configuration> configService,
        Func<StateSnapshot?> snapshotAccessor,
        IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(snapshotAccessor);
        ArgumentNullException.ThrowIfNull(log);
        this.capture = capture;
        this.framework = framework;
        this.configService = configService;
        this.snapshotAccessor = snapshotAccessor;
        this.log = log;
        framework.Update += OnFrameworkUpdate;
    }

    public bool Enabled => configService.Current.MortalEnabled;

    public bool IsRunning => client?.IsRunning == true;

    public bool CurrentHandQuarantined => waitingForNextHand;

    public string Status { get; private set; } = "Disabled";

    public long PacketsProcessed { get; private set; }

    public long EventsSent { get; private set; }

    public long ReactionsReceived { get; private set; }

    public long DecisionsMapped { get; private set; }

    public long DecisionTimeouts { get; private set; }

    public long CandidateCorrections { get; private set; }

    public long RecoveredDiscardEvents { get; private set; }

    public double LastModelEvalMilliseconds =>
        Interlocked.Read(ref lastModelEvalNanoseconds) / 1_000_000d;

    public void QuarantineUnresponsiveHand(string reason)
    {
        if (waitingForNextHand)
            return;

        string lastEvent = journal.Snapshot().LastOrDefault() ?? "(none)";
        string lastPacket = lastCapturedPacket is { } packet
            ? $"message={packet.MessageId},opcode=0x{packet.Opcode:X4},payload={Convert.ToHexString(packet.Payload)}"
            : "(none)";
        log.Warning(
            $"[Mortal] {reason}; quarantining current kyoku " +
            $"(events={EventsSent}, reactions={ReactionsReceived}, queued={reactions.Count}, " +
            $"last_mjai={lastEvent}, last_packet={lastPacket}, dropped={capture.DroppedPackets}).");
        QuarantineCurrentHand(reason);
    }

    public void RecoverUnresponsiveDecision(string reason)
    {
        DecisionTimeouts++;
        string lastEvent = journal.Snapshot().LastOrDefault() ?? "(none)";
        log.Warning(
            $"[Mortal] {reason}; using one local fallback and restarting with replay " +
            $"(events={EventsSent}, reactions={ReactionsReceived}, queued={reactions.Count}, " +
            $"last_mjai={lastEvent}, dropped={capture.DroppedPackets}).");

        Stop("Recovering from decision timeout", preserveMjaiState: true);
        capture.CaptureEnabled = true;
        lastStartAttemptUtc = DateTime.MinValue;
    }

    public bool TryChoose(StateSnapshot snapshot, out ActionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (TryGetRecommendation(snapshot, out choice, out _))
            return true;

        TryRecoverMissingDiscard(snapshot);
        if (IsOpponentResponseWindow(snapshot.Legal.Flags)
            && lastNetworkDiscard is not { Actor: > 0 })
        {
            choice = ActionChoice.Pass("waiting for authoritative opponent discard");
            return false;
        }

        var decisionSnapshot = NormalizeCallCandidates(snapshot, lastNetworkDiscard, out bool corrected);
        if (corrected)
            RecordCandidateCorrection(snapshot, decisionSnapshot);

        while (reactions.TryDequeue(out var reaction))
        {
            if (TryMapDecision(reaction, decisionSnapshot, out choice, out bool? isRed))
            {
                DecisionsMapped++;
                PublishRecommendation(snapshot, choice, isRed);
                if (reaction.Type is "ankan" or "kakan" or "daiminkan")
                {
                    lock (reachLock)
                        pendingKanReaction = reaction;
                }
                return true;
            }
            log.Debug($"[Mortal] Ignored stale or illegal reaction type={reaction.Type} pai={reaction.Pai ?? "?"} legal={snapshot.Legal.Flags}.");
        }

        choice = ActionChoice.Pass("waiting for Mortal");
        return false;
    }

    /// <summary>Consumes a pending Mortal reaction for hint mode without dispatching it.</summary>
    public void RefreshRecommendation(StateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enabled || snapshot.Legal.Flags == ActionFlags.None)
            return;
        TryChoose(snapshot, out _);
    }

    public bool TryGetRecommendation(
        StateSnapshot snapshot,
        out ActionChoice choice,
        out bool? discardIsRed)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int key = StateAggregator.ComputeContentHash(snapshot);
        lock (recommendationLock)
        {
            if (hasRecommendation && recommendationKey == key && recommendation is not null)
            {
                choice = recommendation;
                discardIsRed = recommendationIsRed;
                return true;
            }
        }

        choice = ActionChoice.Pass("waiting for Mortal");
        discardIsRed = null;
        return false;
    }

    public bool TryGetRecommendedDiscardRedIdentity(
        StateSnapshot snapshot,
        ActionChoice choice,
        out bool isRed)
    {
        if (TryGetRecommendation(snapshot, out var cached, out bool? cachedIsRed)
            && cached.Kind == choice.Kind
            && cached.DiscardTile == choice.DiscardTile
            && cachedIsRed.HasValue)
        {
            isRed = cachedIsRed.Value;
            return true;
        }

        isRed = false;
        return false;
    }

    /// <summary>
    /// Self/robbed kans are not decoded in the confirmed 638 layouts yet. Once
    /// the UI accepts the click, echo the exact Mortal reaction so its state is
    /// ready for the replacement draw.
    /// </summary>
    public void ConfirmKanDecisionDispatched(ActionChoice choice)
    {
        if (choice.Kind is not (ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan))
            return;

        MortalReaction? reaction;
        lock (reachLock)
        {
            reaction = pendingKanReaction;
            pendingKanReaction = null;
        }
        if (reaction is null || !TryCreateKanEvent(reaction, out var evt))
            return;

        try
        {
            var current = client ?? throw new InvalidOperationException("Mortal process is not running.");
            journal.Append(evt);
            current.Send(evt);
            EventsSent++;
        }
        catch (Exception ex)
        {
            Status = $"Kan confirmation failed: {ex.Message}";
            log.Error(ex, "[Mortal] Failed to confirm a kan decision.");
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        Stop("Disposed");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed)
            return;

        var config = configService.Current;
        if (!config.MortalEnabled)
        {
            if (client is not null || capture.CaptureEnabled)
                Stop("Disabled");
            return;
        }

        var settings = new MortalProcessSettings(
            config.MortalWslDistribution.Trim(),
            config.MortalWorkingDirectory.Trim(),
            string.IsNullOrWhiteSpace(config.MortalPythonExecutable)
                ? "python"
                : config.MortalPythonExecutable.Trim());
        if (!settings.IsValid)
        {
            const string incomplete = "Configure WSL distribution and Mortal directory";
            if (client is not null || capture.CaptureEnabled)
                Stop(incomplete);
            else
                Status = incomplete;
            return;
        }

        if (activeSettings != settings)
        {
            Stop("Restarting after configuration change");
            activeSettings = settings;
            lastStartAttemptUtc = DateTime.MinValue;
        }

        long replayDeadline = Interlocked.Read(ref replayValidationDeadlineTicks);
        if (replayDeadline > 0 && DateTime.UtcNow.Ticks > replayDeadline)
            Interlocked.Exchange(ref replayValidationDeadlineTicks, 0);

        if (client is not null && !client.IsRunning)
        {
            if (replayCrashDetected)
            {
                QuarantineCurrentHand("Replay failed; waiting for the next kyoku");
                log.Error("[Mortal] Replayed state crashed again; quarantined the current kyoku to stop the restart loop.");
            }
            else
            {
                Stop("Restarting after process exit", preserveMjaiState: true);
            }
            activeSettings = settings;
        }

        if (client?.IsRunning != true)
        {
            if (DateTime.UtcNow - lastStartAttemptUtc < RestartBackoff)
                return;
            Start(settings);
        }

        if (client?.IsRunning != true)
            return;

        capture.CaptureEnabled = true;
        if (waitingForNextHand && !TryResumeAtNextHand())
            return;
        if (!TryFlushPendingStart())
            return;

        while (capture.TryDequeue(out var packet))
        {
            PacketsProcessed++;
            if (!ProcessCapturedPacket(packet))
                return;
        }
        ObserveSnapshot(snapshotAccessor());
    }

    private bool ProcessCapturedPacket(CapturedMahjongPacket packet)
    {
        lastCapturedPacket = packet;
        IReadOnlyList<IMjaiEvent> decodedEvents = decoder.Process(packet.MessageId, packet.Payload);
        foreach (var evt in decodedEvents)
        {
            if (evt is MjaiStartKyoku start && !IsSafeStartKyoku(start))
            {
                pendingStartKyoku = start;
                pendingStartTileKinds = decoder.LastHandStartTileKinds.ToArray();
                Status = "Waiting for the UI hand to repair an unknown start tile";
                log.Warning(
                    $"[Mortal] Deferred unsafe start_kyoku; raw dora={FormatRawKind(decoder.LastHandStartDoraKind)}, " +
                    $"raw hand=[{string.Join(", ", pendingStartTileKinds.Select(FormatRawKind))}].");
                return false;
            }

            if (!IsSafeLiveEvent(evt))
            {
                string detail = evt switch
                {
                    MjaiDahai discard =>
                        $"dahai actor={discard.Actor} pai={discard.Pai} physical={FormatRawPhysical(decoder.LastDiscardPhysical)} action={FormatRawAction(decoder.LastDiscardAction)}",
                    _ => evt.Type,
                };
                log.Error(
                    $"[Mortal] Quarantining unsafe MJAI event {detail}; " +
                    $"message={packet.MessageId} opcode=0x{packet.Opcode:X4} payload={Convert.ToHexString(packet.Payload)}.");
                QuarantineCurrentHand("Unsafe MJAI event; waiting for the next kyoku");
                return false;
            }

        }

        if (recoveredPendingServerDiscard is { } recovered
            && decodedEvents.OfType<MjaiDahai>().FirstOrDefault() is { } serverDiscard)
        {
            bool exactDiscard = serverDiscard.Actor == recovered.Actor
                && string.Equals(serverDiscard.Pai, recovered.Pai, StringComparison.Ordinal);
            if (exactDiscard && decodedEvents.Count == 1)
            {
                recoveredPendingServerDiscard = null;
                ObserveNetworkEvent(serverDiscard);
                log.Information(
                    $"[Mortal] Delayed server discard matched recovered event " +
                    $"actor={serverDiscard.Actor} pai={serverDiscard.Pai}; duplicate suppressed.");
                return true;
            }

            if (!journal.TryReplaceLast(recovered, decodedEvents))
            {
                log.Error(
                    $"[Mortal] Could not replace provisional discard with its delayed packet; " +
                    $"recovered={recovered.Actor}:{recovered.Pai}, " +
                    $"server={serverDiscard.Actor}:{serverDiscard.Pai}.");
                QuarantineCurrentHand("Discard correction failed; waiting for the next kyoku");
                return false;
            }

            recoveredPendingServerDiscard = null;
            foreach (var evt in decodedEvents)
                ObserveNetworkEvent(evt);
            log.Warning(
                $"[Mortal] Corrected provisional discard from delayed packet and restarting with replay: " +
                $"recovered={recovered.Actor}:{recovered.Pai}, " +
                $"server={serverDiscard.Actor}:{serverDiscard.Pai}.");
            Stop("Correcting provisional discard", preserveMjaiState: true);
            capture.CaptureEnabled = true;
            lastStartAttemptUtc = DateTime.MinValue;
            return false;
        }

        foreach (var evt in decodedEvents)
        {
            ObserveNetworkEvent(evt);
            if (!TrySendEvent(evt))
                return false;
        }
        return true;
    }

    private bool TryResumeAtNextHand()
    {
        while (capture.TryDequeue(out var packet))
        {
            PacketsProcessed++;
            lastCapturedPacket = packet;
            if (packet.MessageId is MahjongPacketMjaiDecoder.HandResultAMessageId
                or MahjongPacketMjaiDecoder.HandResultBMessageId)
            {
                quarantinedKyokuEnded = true;
                continue;
            }
            if (packet.MessageId != MahjongPacketMjaiDecoder.HandStartMessageId)
                continue;

            waitingForNextHand = false;
            quarantinedKyokuEnded = false;
            decoder = new MahjongPacketMjaiDecoder();
            journal.Clear();
            lastNetworkDiscard = null;
            recoveredPendingServerDiscard = null;
            lastNetworkDrawActor = null;
            ResetObservedDiscardCounts();
            bool resumed = ProcessCapturedPacket(packet);
            if (resumed)
            {
                Status = "Running";
                log.Information("[Mortal] Next hand detected; resumed MJAI from a fresh start_kyoku.");
            }
            return resumed;
        }

        Status = quarantinedKyokuEnded
            ? "Running; quarantined kyoku ended"
            : "Running; current kyoku quarantined, waiting for the next kyoku";
        return false;
    }

    private bool TryFlushPendingStart()
    {
        var pending = pendingStartKyoku;
        if (pending is null)
            return true;

        if (!TryRepairStartKyoku(pending, snapshotAccessor(), out var repaired))
        {
            Status = "Waiting for the UI hand to repair an unknown start tile";
            return false;
        }

        pendingStartKyoku = null;
        pendingStartTileKinds = [];
        log.Information("[Mortal] Repaired start_kyoku from the observed 13-tile UI hand.");
        return TrySendEvent(repaired);
    }

    private bool TrySendEvent(IMjaiEvent evt)
    {
        if (evt is MjaiStartKyoku start && !IsSafeStartKyoku(start))
        {
            Status = "Blocked an unsafe start_kyoku event";
            log.Error("[Mortal] Refused to journal or send an unsafe start_kyoku event.");
            return false;
        }

        if (evt is MjaiStartGame)
            journal.Clear();
        journal.Append(evt);
        if (ShouldSuppressOptimisticRiichiEvent(evt))
            return true;

        try
        {
            var current = client ?? throw new InvalidOperationException("Mortal process is not running.");
            current.Send(evt);
            EventsSent++;
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Send failed: {ex.Message}";
            log.Error(ex, "[Mortal] Failed to send an MJAI event.");
            return false;
        }
    }

    private void Start(MortalProcessSettings settings)
    {
        lastStartAttemptUtc = DateTime.UtcNow;
        try
        {
            var next = new MortalProcessClient(log);
            next.ReactionReceived += OnReactionReceived;
            next.Exited += OnProcessExited;
            next.Start(settings);
            client = next;
            capture.CaptureEnabled = true;
            int replayed = 0;
            if (replayStateOnNextStart)
            {
                foreach (string evt in journal.Snapshot())
                {
                    next.SendReplay(evt);
                    EventsSent++;
                    replayed++;
                }
                replayStateOnNextStart = false;
            }
            Interlocked.Exchange(
                ref replayValidationDeadlineTicks,
                replayed > 0 ? DateTime.UtcNow.Add(ReplayValidationWindow).Ticks : 0);
            Status = replayed == 0
                ? "Running (model may still be loading)"
                : $"Running (replayed {replayed} MJAI events)";
            log.Information($"[Mortal] Started in WSL distribution '{settings.WslDistribution}'.");
            if (replayed > 0)
                log.Information($"[Mortal] Replayed {replayed} MJAI events after process restart.");
        }
        catch (Exception ex)
        {
            capture.CaptureEnabled = false;
            Status = $"Start failed: {ex.Message}";
            log.Error(ex, "[Mortal] Could not start the WSL process.");
        }
    }

    private void Stop(string status, bool preserveMjaiState = false)
    {
        capture.CaptureEnabled = preserveMjaiState;
        var old = client;
        client = null;
        if (old is not null)
        {
            old.ReactionReceived -= OnReactionReceived;
            old.Exited -= OnProcessExited;
            old.Dispose();
        }

        if (!preserveMjaiState)
        {
            while (capture.TryDequeue(out _)) { }
            decoder = new MahjongPacketMjaiDecoder();
            journal.Clear();
            pendingStartKyoku = null;
            pendingStartTileKinds = [];
            waitingForNextHand = false;
            quarantinedKyokuEnded = false;
            lastCapturedPacket = null;
            lastNetworkDiscard = null;
            recoveredPendingServerDiscard = null;
            lastNetworkDrawActor = null;
            ResetObservedDiscardCounts();
            lastCandidateCorrectionKey = 0;
        }
        replayCrashDetected = false;
        if (!preserveMjaiState)
            Interlocked.Exchange(ref replayValidationDeadlineTicks, 0);
        while (reactions.TryDequeue(out _)) { }
        replayStateOnNextStart = preserveMjaiState && journal.Count > 0;
        ClearRecommendation();
        lock (reachLock)
        {
            awaitingReachDiscard = false;
            optimisticRiichiTile = null;
            pendingKanReaction = null;
        }
        Status = status;
    }

    private void OnProcessExited(int? exitCode)
    {
        long replayDeadline = Interlocked.Read(ref replayValidationDeadlineTicks);
        bool failedDuringReplayValidation = replayDeadline > 0 && DateTime.UtcNow.Ticks <= replayDeadline;
        if (failedDuringReplayValidation)
            replayCrashDetected = true;
        Status = failedDuringReplayValidation
            ? $"Exited ({exitCode?.ToString() ?? "unknown"}) during replay; quarantining hand"
            : $"Exited ({exitCode?.ToString() ?? "unknown"}); retrying";
        log.Warning($"[Mortal] Process exited with code {exitCode?.ToString() ?? "unknown"}.");
    }

    private void QuarantineCurrentHand(string status)
    {
        var old = client;
        client = null;
        if (old is not null)
        {
            old.ReactionReceived -= OnReactionReceived;
            old.Exited -= OnProcessExited;
            old.Dispose();
        }

        decoder = new MahjongPacketMjaiDecoder();
        journal.Clear();
        pendingStartKyoku = null;
        pendingStartTileKinds = [];
        waitingForNextHand = true;
        quarantinedKyokuEnded = false;
        lastNetworkDiscard = null;
        recoveredPendingServerDiscard = null;
        lastNetworkDrawActor = null;
        ResetObservedDiscardCounts();
        lastCandidateCorrectionKey = 0;
        replayStateOnNextStart = false;
        replayCrashDetected = false;
        Interlocked.Exchange(ref replayValidationDeadlineTicks, 0);
        lastStartAttemptUtc = DateTime.MinValue;
        while (reactions.TryDequeue(out _)) { }
        ClearRecommendation();
        lock (reachLock)
        {
            awaitingReachDiscard = false;
            optimisticRiichiTile = null;
            pendingKanReaction = null;
        }
        capture.CaptureEnabled = true;
        Status = status;
    }

    private void OnReactionReceived(string json)
    {
        if (!MortalReaction.TryParse(json, out var parsed) || parsed is null)
        {
            log.Warning($"[Mortal] Ignored invalid reaction JSON: {json}");
            return;
        }

        ReactionsReceived++;
        if (parsed.EvalTimeNs is > 0)
            Interlocked.Exchange(ref lastModelEvalNanoseconds, parsed.EvalTimeNs.Value);
        lock (reachLock)
        {
            if (parsed.Type == "reach" && parsed.Actor == 0)
            {
                awaitingReachDiscard = true;
                try
                {
                    var current = client ?? throw new InvalidOperationException("Mortal process is not running.");
                    current.Send(new MjaiReach(0));
                    EventsSent++;
                }
                catch (Exception ex)
                {
                    awaitingReachDiscard = false;
                    Status = $"Reach handshake failed: {ex.Message}";
                    log.Error(ex, "[Mortal] Failed to complete the reach handshake.");
                }
                return;
            }

            if (awaitingReachDiscard && parsed.Type == "dahai" && parsed.Actor == 0)
            {
                awaitingReachDiscard = false;
                optimisticRiichiTile = parsed.Pai;
                reactions.Enqueue(parsed with { Type = "riichi" });
                return;
            }
        }

        reactions.Enqueue(parsed);
    }

    private bool ShouldSuppressOptimisticRiichiEvent(IMjaiEvent evt)
    {
        lock (reachLock)
        {
            if (optimisticRiichiTile is null)
                return false;
            if (evt is MjaiReach { Actor: 0 })
                return true;
            if (evt is MjaiDahai { Actor: 0 } discard)
            {
                if (!string.Equals(discard.Pai, optimisticRiichiTile, StringComparison.Ordinal))
                    log.Warning($"[Mortal] Server riichi discard {discard.Pai} differs from planned {optimisticRiichiTile}.");
                return true;
            }
            if (evt is MjaiReachAccepted { Actor: 0 })
                optimisticRiichiTile = null;
            return false;
        }
    }

    internal static bool TryRepairStartKyoku(
        MjaiStartKyoku start,
        StateSnapshot? snapshot,
        out MjaiStartKyoku repaired)
    {
        repaired = start;
        if (snapshot is null
            || start.Tehais.Length != 4
            || start.Tehais[0].Length != 13
            || snapshot.Hand.Count != 13
            || snapshot.Hand.Count != snapshot.HandIsRed.Count
            || !snapshot.Observations.HasFlag(SnapshotObservationFlags.HandRedIdentity))
        {
            return false;
        }

        // Reject a stale UI snapshot from the preceding hand. Every tile that
        // decoded successfully from the packet must exist in the candidate UI hand.
        var available = snapshot.Hand.GroupBy(tile => tile.Id)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (string value in start.Tehais[0])
        {
            if (value == MjaiTile.Unknown)
                continue;
            if (!MjaiTile.TryParse(value, out var tile, out _)
                || !available.TryGetValue(tile.Id, out int count)
                || count == 0)
            {
                return false;
            }
            available[tile.Id] = count - 1;
        }

        string dora = start.DoraMarker;
        if (dora == MjaiTile.Unknown)
        {
            if (snapshot.DoraIndicators.Count == 0)
                return false;
            dora = MjaiTile.Format(snapshot.DoraIndicators[0]);
        }

        var tehais = start.Tehais.Select(hand => hand.ToArray()).ToArray();
        tehais[0] = MjaiTile.FormatHand(snapshot);
        repaired = start with { DoraMarker = dora, Tehais = tehais };
        return IsSafeStartKyoku(repaired);
    }

    internal static bool IsSafeStartKyoku(MjaiStartKyoku start) =>
        start.Tehais.Length == 4
        && start.Tehais[0].Length == 13
        && start.Tehais[0].All(tile => tile != MjaiTile.Unknown)
        && start.DoraMarker != MjaiTile.Unknown;

    internal static bool IsSafeLiveEvent(IMjaiEvent evt) => evt switch
    {
        MjaiStartKyoku start => IsSafeStartKyoku(start),
        MjaiDahai discard => discard.Pai != MjaiTile.Unknown,
        MjaiTsumo { Actor: 0 } draw => draw.Pai != MjaiTile.Unknown,
        MjaiDora dora => dora.DoraMarker != MjaiTile.Unknown,
        MjaiOpenCall call =>
            call.Pai != MjaiTile.Unknown && call.Consumed.All(tile => tile != MjaiTile.Unknown),
        MjaiAnkan kan => kan.Consumed.All(tile => tile != MjaiTile.Unknown),
        MjaiKakan kan =>
            kan.Pai != MjaiTile.Unknown && kan.Consumed.All(tile => tile != MjaiTile.Unknown),
        MjaiDaiminkan kan =>
            kan.Pai != MjaiTile.Unknown && kan.Consumed.All(tile => tile != MjaiTile.Unknown),
        _ => true,
    };

    internal static StateSnapshot NormalizeCallCandidates(
        StateSnapshot snapshot,
        MjaiDahai? lastDiscard,
        out bool corrected)
    {
        corrected = false;
        if (lastDiscard is not { Actor: > 0 }
            || !MjaiTile.TryParse(lastDiscard.Pai, out var claimed, out _))
        {
            return snapshot;
        }

        var legal = snapshot.Legal;
        const ActionFlags networkCallFlags =
            ActionFlags.Pon | ActionFlags.Chi | ActionFlags.MinKan;
        if ((legal.Flags & networkCallFlags) == 0)
            return snapshot;

        var derived = CallCandidateDeriver.Derive(snapshot.Hand, claimed, lastDiscard.Actor);
        IReadOnlyList<MeldCandidate> pons = legal.Can(ActionFlags.Pon)
            ? derived.Pon
            : legal.PonCandidates;
        IReadOnlyList<MeldCandidate> chis = legal.Can(ActionFlags.Chi)
            ? derived.Chi
            : legal.ChiCandidates;
        IReadOnlyList<MeldCandidate> kans = legal.Can(ActionFlags.MinKan)
            ? [.. legal.KanCandidates.Where(candidate => candidate.Kind != MeldKind.MinKan), .. derived.Kan]
            : legal.KanCandidates;

        corrected = !CandidateListsEqual(legal.PonCandidates, pons)
            || !CandidateListsEqual(legal.ChiCandidates, chis)
            || !CandidateListsEqual(legal.KanCandidates, kans);
        if (!corrected)
            return snapshot;

        return snapshot with
        {
            Legal = legal with
            {
                PonCandidates = pons,
                ChiCandidates = chis,
                KanCandidates = kans,
            },
        };
    }

    private void ObserveNetworkEvent(IMjaiEvent evt)
    {
        switch (evt)
        {
            case MjaiDahai discard:
                lastNetworkDiscard = discard;
                lastNetworkDrawActor = null;
                ClearObservedDiscardEvidence();
                break;
            case MjaiStartKyoku:
            case MjaiEndKyoku:
            case MjaiOpenCall:
                lastNetworkDiscard = null;
                recoveredPendingServerDiscard = null;
                lastNetworkDrawActor = null;
                if (evt is MjaiStartKyoku or MjaiEndKyoku)
                    ResetObservedDiscardCounts();
                break;
            case MjaiTsumo draw:
                lastNetworkDiscard = null;
                recoveredPendingServerDiscard = null;
                lastNetworkDrawActor = draw.Actor;
                ClearObservedDiscardEvidence();
                break;
        }
    }

    private void ObserveSnapshot(StateSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Seats.Count != 4)
            return;

        int changedSeat = -1;
        int changedCount = 0;
        for (int seat = 0; seat < 4; seat++)
        {
            int current = snapshot.Seats[seat].DiscardCount;
            int previous = observedDiscardCounts[seat];
            if (previous >= 0 && current == previous + 1)
            {
                changedSeat = seat;
                changedCount++;
            }
            else if (previous >= 0 && current != previous)
            {
                changedCount += 2;
            }
            observedDiscardCounts[seat] = current;
        }

        if (changedCount == 1
            && changedSeat > 0
            && HasCompleteObservedDiscard(snapshot, changedSeat, observedDiscardCounts[changedSeat]))
        {
            latestUiDiscardSeat = changedSeat;
            latestUiDiscardCount = observedDiscardCounts[changedSeat];
        }
        else if (changedCount > 0)
        {
            ClearObservedDiscardEvidence();
        }
    }

    private bool TryRecoverMissingDiscard(StateSnapshot snapshot)
    {
        const ActionFlags opponentCallFlags =
            ActionFlags.Pon | ActionFlags.Chi | ActionFlags.MinKan | ActionFlags.Ron;
        if ((snapshot.Legal.Flags & opponentCallFlags) == 0 || lastNetworkDiscard is not null)
            return false;

        int? actor = latestUiDiscardSeat is > 0
            && latestUiDiscardSeat == lastNetworkDrawActor
            ? latestUiDiscardSeat
            : null;
        if (actor is null
            || !TryGetObservedDiscard(
                snapshot,
                actor.Value,
                latestUiDiscardCount,
                out var claimed,
                out bool isRed)
            || !IsObservedDiscardCompatibleWithPrompt(snapshot, actor.Value, claimed))
        {
            return false;
        }

        string pai = MjaiTile.Format(claimed, isRed);
        var recovered = new MjaiDahai(actor.Value, pai, false);
        if (!TrySendEvent(recovered))
            return false;

        decoder.RecoverLastDiscard(actor.Value, pai);
        recoveredPendingServerDiscard = recovered;
        ObserveNetworkEvent(recovered);
        RecoveredDiscardEvents++;
        log.Warning(
            $"[Mortal] Recovered missing discard from UI state: " +
            $"actor={actor.Value} pai={pai} legal={snapshot.Legal.Flags}.");
        return true;
    }

    internal static bool TryGetObservedDiscard(
        StateSnapshot snapshot,
        int actor,
        int expectedDiscardCount,
        out Tile claimed,
        out bool isRed)
    {
        const SnapshotObservationFlags required =
            SnapshotObservationFlags.PublicDiscardTiles |
            SnapshotObservationFlags.PublicDiscardRedIdentity;
        if (actor is > 0 and < 4
            && expectedDiscardCount > 0
            && (snapshot.Observations & required) == required)
        {
            var seat = snapshot.Seats[actor];
            if (seat.DiscardCount == expectedDiscardCount
                && seat.Discards.Count == expectedDiscardCount
                && seat.DiscardIsRed.Count == expectedDiscardCount)
            {
                claimed = seat.Discards[^1];
                isRed = seat.DiscardIsRed[^1];
                return true;
            }
        }

        claimed = default;
        isRed = false;
        return false;
    }

    internal StateSnapshot NormalizeForLocalFallback(StateSnapshot snapshot) =>
        NormalizeCallCandidates(snapshot, lastNetworkDiscard, out _);

    internal bool HasAuthoritativeOpponentDiscard =>
        lastNetworkDiscard is { Actor: > 0 };

    private static bool IsOpponentResponseWindow(ActionFlags flags) =>
        (flags & (ActionFlags.Pon | ActionFlags.Chi | ActionFlags.MinKan | ActionFlags.Ron)) != 0;

    private static bool HasCompleteObservedDiscard(
        StateSnapshot snapshot,
        int actor,
        int discardCount) =>
        TryGetObservedDiscard(snapshot, actor, discardCount, out _, out _);

    private static bool IsObservedDiscardCompatibleWithPrompt(
        StateSnapshot snapshot,
        int actor,
        Tile claimed)
    {
        var legal = snapshot.Legal;
        var derived = CallCandidateDeriver.Derive(snapshot.Hand, claimed, actor);
        return legal.Can(ActionFlags.Ron)
            || (legal.Can(ActionFlags.Pon) && derived.Pon.Count > 0)
            || (legal.Can(ActionFlags.Chi) && derived.Chi.Count > 0)
            || (legal.Can(ActionFlags.MinKan) && derived.Kan.Count > 0);
    }

    private void ResetObservedDiscardCounts()
    {
        Array.Fill(observedDiscardCounts, -1);
        ClearObservedDiscardEvidence();
    }

    private void ClearObservedDiscardEvidence()
    {
        latestUiDiscardSeat = null;
        latestUiDiscardCount = -1;
    }

    private void RecordCandidateCorrection(StateSnapshot original, StateSnapshot corrected)
    {
        int key = HashCode.Combine(
            original.AddonStateCode,
            (int)original.Legal.Flags,
            lastNetworkDiscard?.Actor,
            lastNetworkDiscard?.Pai,
            original.WallRemaining);
        if (key == lastCandidateCorrectionKey)
            return;
        lastCandidateCorrectionKey = key;
        CandidateCorrections++;
        log.Warning(
            $"[Mortal] Corrected call candidates from network discard " +
            $"actor={lastNetworkDiscard?.Actor} pai={lastNetworkDiscard?.Pai}: " +
            $"pon {FormatCandidates(original.Legal.PonCandidates)} -> {FormatCandidates(corrected.Legal.PonCandidates)}, " +
            $"chi {FormatCandidates(original.Legal.ChiCandidates)} -> {FormatCandidates(corrected.Legal.ChiCandidates)}.");
    }

    private static bool CandidateListsEqual(
        IReadOnlyList<MeldCandidate> left,
        IReadOnlyList<MeldCandidate> right)
    {
        if (left.Count != right.Count)
            return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (left[i].Kind != right[i].Kind
                || left[i].ClaimedTile != right[i].ClaimedTile
                || left[i].FromSeat != right[i].FromSeat
                || !left[i].HandTiles.SequenceEqual(right[i].HandTiles))
            {
                return false;
            }
        }
        return true;
    }

    private static string FormatCandidates(IReadOnlyList<MeldCandidate> candidates) =>
        candidates.Count == 0
            ? "[]"
            : $"[{string.Join(',', candidates.Select(candidate => $"{candidate.Kind}:{candidate.ClaimedTile.Id}@{candidate.FromSeat}"))}]";

    private static string FormatRawKind(int? value) =>
        value is { } kind ? $"{kind} (0x{unchecked((uint)kind):X8})" : "n/a";

    private static string FormatRawKind(int value) => FormatRawKind((int?)value);

    private static string FormatRawPhysical(ushort? value) =>
        value is { } physical ? $"{physical} (0x{physical:X4})" : "n/a";

    private static string FormatRawAction(uint? value) =>
        value is { } action ? $"0x{action:X8}" : "n/a";

    internal static bool TryMapDecision(MortalReaction reaction, StateSnapshot snapshot, out ActionChoice choice) =>
        TryMapDecision(reaction, snapshot, out choice, out _);

    internal static bool TryMapDecision(
        MortalReaction reaction,
        StateSnapshot snapshot,
        out ActionChoice choice,
        out bool? discardIsRed)
    {
        const string reason = "Mortal";
        discardIsRed = null;
        var legal = snapshot.Legal;
        switch (reaction.Type)
        {
            case "dahai" when legal.Can(ActionFlags.Discard)
                && TryReactionTile(reaction, out var discard, out bool discardRed)
                && snapshot.Hand.Contains(discard):
                choice = ActionChoice.Discard(discard, reason);
                discardIsRed = discardRed;
                return true;

            case "riichi" when legal.Can(ActionFlags.Riichi)
                && TryReactionTile(reaction, out var reachDiscard, out bool reachDiscardRed)
                && snapshot.Hand.Contains(reachDiscard):
                choice = ActionChoice.DeclareRiichi(reachDiscard, reason);
                discardIsRed = reachDiscardRed;
                return true;

            case "hora" when legal.Can(ActionFlags.Tsumo) && reaction.Target == 0:
                choice = ActionChoice.DeclareTsumo(reason);
                return true;

            case "hora" when legal.Can(ActionFlags.Ron):
                choice = ActionChoice.DeclareRon(reason);
                return true;

            case "none" when legal.Can(ActionFlags.Pass)
                || legal.Can(ActionFlags.Pon)
                || legal.Can(ActionFlags.Chi)
                || legal.Can(ActionFlags.MinKan)
                || legal.Can(ActionFlags.Ron):
                choice = ActionChoice.Pass(reason);
                return true;
        }

        if (TryMapCall(reaction, snapshot, out choice))
            return true;

        choice = ActionChoice.Pass(reason);
        return false;
    }

    private static bool TryMapCall(MortalReaction reaction, StateSnapshot snapshot, out ActionChoice choice)
    {
        MeldKind? meldKind = reaction.Type switch
        {
            "chi" => MeldKind.Chi,
            "pon" => MeldKind.Pon,
            "daiminkan" => MeldKind.MinKan,
            "ankan" => MeldKind.AnKan,
            "kakan" => MeldKind.ShouMinKan,
            _ => null,
        };
        if (meldKind is null)
        {
            choice = ActionChoice.Pass();
            return false;
        }

        var candidates = meldKind switch
        {
            MeldKind.Chi => snapshot.Legal.ChiCandidates,
            MeldKind.Pon => snapshot.Legal.PonCandidates,
            _ => snapshot.Legal.KanCandidates,
        };
        var consumed = reaction.Consumed
            .Select(value => MjaiTile.TryParse(value, out var tile, out _) ? (Tile?)tile : null)
            .Where(tile => tile.HasValue)
            .Select(tile => tile!.Value)
            .OrderBy(tile => tile.Id)
            .ToArray();
        Tile? kanTile = null;
        if (meldKind is MeldKind.AnKan && consumed.Length > 0)
            kanTile = consumed[0];
        else if (meldKind is MeldKind.MinKan or MeldKind.ShouMinKan
                 && MjaiTile.TryParse(reaction.Pai, out var parsedKanTile, out _))
            kanTile = parsedKanTile;

        var candidate = candidates.FirstOrDefault(item =>
        {
            if (item.Kind != meldKind.Value)
                return false;
            if (kanTile is { } expected)
                return item.ClaimedTile == expected || item.HandTiles.Any(tile => tile == expected);
            return consumed.Length == 0 || item.HandTiles.OrderBy(tile => tile.Id).SequenceEqual(consumed);
        });
        if (candidate == default)
        {
            choice = ActionChoice.Pass();
            return false;
        }

        ActionKind kind = meldKind.Value switch
        {
            MeldKind.Chi => ActionKind.Chi,
            MeldKind.Pon => ActionKind.Pon,
            MeldKind.MinKan => ActionKind.MinKan,
            MeldKind.AnKan => ActionKind.AnKan,
            MeldKind.ShouMinKan => ActionKind.ShouMinKan,
            _ => ActionKind.Pass,
        };
        choice = new ActionChoice(kind, Call: candidate, Reasoning: "Mortal");
        return true;
    }

    private static bool TryReactionTile(MortalReaction reaction, out Tile tile, out bool isRed) =>
        MjaiTile.TryParse(reaction.Pai, out tile, out isRed);

    private void PublishRecommendation(StateSnapshot snapshot, ActionChoice choice, bool? discardIsRed)
    {
        int key = StateAggregator.ComputeContentHash(snapshot);
        lock (recommendationLock)
        {
            recommendation = choice;
            recommendationIsRed = discardIsRed;
            recommendationKey = key;
            hasRecommendation = true;
        }
    }

    private void ClearRecommendation()
    {
        lock (recommendationLock)
        {
            recommendation = null;
            recommendationIsRed = null;
            recommendationKey = 0;
            hasRecommendation = false;
        }
    }

    private static bool TryCreateKanEvent(MortalReaction reaction, out IMjaiEvent evt)
    {
        evt = null!;
        switch (reaction.Type)
        {
            case "ankan" when reaction.Actor is { } actor && reaction.Consumed.Length == 4:
                evt = new MjaiAnkan(actor, reaction.Consumed);
                return true;
            case "kakan" when reaction.Actor is { } actor && reaction.Pai is { } pai
                && reaction.Consumed.Length == 3:
                evt = new MjaiKakan(actor, pai, reaction.Consumed);
                return true;
            case "daiminkan" when reaction.Actor is { } actor && reaction.Target is { } target
                && reaction.Pai is { } pai && reaction.Consumed.Length == 3:
                evt = new MjaiDaiminkan(actor, target, pai, reaction.Consumed);
                return true;
            default:
                return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Game;
using Mahjong.Policy;

namespace Mahjong.Plugin.Dalamud.Logging;

public sealed class GameLogger : IDisposable
{
    public const int SchemaVersion = 5;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly StateAggregator? aggregator;
    private readonly IConfigService<Configuration> configService;
    private readonly IPluginLog log;
    private readonly string gamesDir;
    private readonly object writerLock = new();
    private readonly Func<IPolicy>? policyAccessor;
    private readonly Func<MeldTrackerStateDto>? meldTrackerAccessor;
    private readonly InputEventLogger? eventLogger;
    private readonly List<string> sessionPaths = [];

    private string? currentPath;
    private int handSeq;
    public long LastActionId { get; private set; }
    private int lastWall = -1;
    private int? lastStateHash;
    private int[]? lastHandStartScores;
    private bool disposed;
    private readonly BackgroundIoWorker io;
    private readonly bool ownsIo;
    private readonly Func<bool>? externalEnabled;
    private int? lastDecisionKey;
    public Task FlushAsync() => io.FlushAsync();

    public string? CurrentPath => currentPath;
    public int HandSeq => handSeq;
    public string GamesDir => gamesDir;

    public GameLogger(
        StateAggregator aggregator,
        IConfigService<Configuration> configService,
        IPluginLog log,
        string pluginConfigDir,
        Func<IPolicy>? policyAccessor = null,
        InputEventLogger? eventLogger = null,
        Func<MeldTrackerStateDto>? meldTrackerAccessor = null,
        BackgroundIoWorker? io = null, Func<bool>? externalEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDir);
        this.aggregator = aggregator;
        this.configService = configService;
        this.log = log;
        this.externalEnabled = externalEnabled;
        this.io = io ?? new BackgroundIoWorker();
        ownsIo = io is null;
        this.policyAccessor = policyAccessor;
        this.eventLogger = eventLogger;
        this.meldTrackerAccessor = meldTrackerAccessor;
        gamesDir = Path.Combine(pluginConfigDir, "games");
        this.io.TryEnqueue(() => Directory.CreateDirectory(gamesDir));

        aggregator.Changed += OnStateChanged;
        aggregator.DecisionPublished += OnDecisionPublished;
        aggregator.ShadowCompared += OnShadowCompared;
        if (eventLogger is not null)
            eventLogger.CallPromptObserved += OnCallPromptObserved;
    }

    /// <summary>Test-only: skips aggregator wiring.</summary>
    internal GameLogger(
        IConfigService<Configuration> configService,
        IPluginLog log,
        string pluginConfigDir)
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDir);
        aggregator = null;
        this.configService = configService;
        this.log = log;
        io = new BackgroundIoWorker();
        ownsIo = true;
        policyAccessor = null;
        eventLogger = null;
        gamesDir = Path.Combine(pluginConfigDir, "games");
        this.io.TryEnqueue(() => Directory.CreateDirectory(gamesDir));

    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (aggregator is not null)
        {
            aggregator.Changed -= OnStateChanged;
            aggregator.DecisionPublished -= OnDecisionPublished;
            aggregator.ShadowCompared -= OnShadowCompared;
        }
        if (eventLogger is not null)
            eventLogger.CallPromptObserved -= OnCallPromptObserved;
        if (ownsIo) io.Dispose();
    }

    internal void ResetSession()
    {
        lock (writerLock)
        {
            currentPath = null;
            sessionPaths.Clear();
            handSeq = 0;
            LastActionId = 0;
            lastWall = -1;
            lastStateHash = null;
            lastDecisionKey = null;
            lastHandStartScores = null;
        }
    }

    internal IReadOnlyList<string> SnapshotSessionPaths()
    {
        lock (writerLock)
            return sessionPaths.ToArray();
    }

    internal void OnStateChanged(StateSnapshot snap)
    {
        if (!configService.Current.EnableGameLogging)
            return;

        // StateAggregator.Changed fires per-frame; dedup by structural hash or one turn yields ~1200 duplicate lines.
        int hash = ComputeContentHash(snap);
        if (lastStateHash == hash)
            return;
        lastStateHash = hash;

        try
        {
            MaybeRollHand(snap);
            WriteLine(JsonSerializer.Serialize(BuildStateEvent(snap), JsonOpts));
            if (aggregator?.LastScorerError is {} error) WriteLine(JsonSerializer.Serialize(new { e="policy-error", reason=error, hand_id=snap.HandId, revision=snap.Revision }, JsonOpts));
        }
        catch (Exception ex)
        {
            log.Error($"GameLogger state-write error: {ex.Message}");
        }
    }

    private void OnShadowCompared(PolicyEvaluation stable, PolicyEvaluation enhanced)
    {
        if (disposed || !configService.Current.EnableGameLogging) return;
        WriteLine(JsonSerializer.Serialize(new { e="shadow-decision", hand_id=stable.HandId, revision=stable.Revision,
            stable_kind=stable.Choice.Kind.ToString(), stable_tile=stable.Choice.DiscardTile?.Id,
            enhanced_kind=enhanced.Choice.Kind.ToString(), enhanced_tile=enhanced.Choice.DiscardTile?.Id,
            reason=enhanced.Choice.Reasoning, elapsed_ms=enhanced.ElapsedMilliseconds }, JsonOpts));
    }

    private void OnDecisionPublished(PolicyEvaluation evaluation)
    {
        if (evaluation.Source is not ("mortal" or "local-fallback" or "terminal-guard") && (externalEnabled?.Invoke() ?? configService.Current.MortalEnabled)) return;
        RecordDecision(evaluation.Choice, evaluation.Source);
    }

    private void OnCallPromptObserved(CallPromptEvent evt)
    {
        if (disposed || !configService.Current.EnableGameLogging)
            return;
        if (currentPath is null)
            return;
        try
        {
            var dto = new CallPromptDto(
                T: evt.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                E: "call-prompt",
                Variant: evt.AddonName,
                StateCode: evt.StateCode,
                Flags: evt.Flags,
                Pon: evt.PonClaimedTileIds,
                Chi: evt.ChiClaimedTileIds,
                Kan: evt.KanClaimedTileIds,
                Av: evt.IntValues);
            WriteLine(JsonSerializer.Serialize(dto, JsonOpts));
        }
        catch (Exception ex)
        {
            log.Error($"GameLogger call-prompt-write error: {ex.Message}");
        }
    }

    public void RecordAction(ActionKind kind, Tile? tile, int? slot, string result, string reasoning)
    {
        if (!configService.Current.EnableGameLogging || disposed)
            return;
        try
        {
            var evt = new ActionEvent(
                T: Now(),
                E: "action",
                ActionId: ++LastActionId,
                HandId: aggregator?.Latest?.HandId,
                Revision: aggregator?.Latest?.Revision,
                Kind: kind.ToString(),
                Tile: tile?.Id,
                Slot: slot,
                Result: result,
                Why: string.IsNullOrEmpty(reasoning) ? null : reasoning);
            WriteLine(JsonSerializer.Serialize(evt, JsonOpts));
        }
        catch (Exception ex)
        {
            log.Error($"GameLogger action-write error: {ex.Message}");
        }
    }

    public void RecordActionOutcome(long actionId, string label, string status, string path)
    {
        if (!configService.Current.EnableGameLogging || disposed) return;
        WriteLine(JsonSerializer.Serialize(new { t = Now(), e = "action-outcome", action_id = actionId,
            label, status, path }, JsonOpts));
    }

    public void RecordDecision(ActionChoice choice, string source)
    {
        int key = HashCode.Combine(aggregator?.Latest?.HandId, aggregator?.Latest?.Revision, choice.Kind, choice.DiscardTile, choice.DiscardIsRed, source, choice.Reasoning);
        if (lastDecisionKey == key) return;
        lastDecisionKey = key;
        if (!configService.Current.EnableGameLogging || disposed)
            return;
        try
        {
            var tracker = meldTrackerAccessor?.Invoke();
            WriteLine(JsonSerializer.Serialize(
                BuildDecisionEvent(choice, tracker, source), JsonOpts));
        }
        catch (Exception ex)
        {
            log.Error($"GameLogger decision-write error: {ex.Message}");
        }
    }

    internal static bool ShouldRecordPolicyDecision(Configuration config) =>
        !config.MortalEnabled || !config.AutomationArmed || config.SuggestionOnly;

    /// <summary>Roll only on wall-jump-up AND hand at deal-shape count (0/13/14); mid-hand jumps are read glitches.</summary>
    private void MaybeRollHand(StateSnapshot snap)
    {
        bool firstRoll = currentPath is null;
        bool wallJumpUp = !firstRoll && snap.WallRemaining > lastWall + 5;
        if (!firstRoll && !wallJumpUp)
        {
            lastWall = snap.WallRemaining;
            return;
        }
        // Wall jumped but hand isn't deal-shape — retain lastWall so the next tick re-attempts the roll.
        if (wallJumpUp && snap.Hand.Count != 0 && snap.Hand.Count != 13 && snap.Hand.Count != 14)
            return;
        lastWall = snap.WallRemaining;

        // Write hand-end into the new file so each next-hand boundary carries the prior settlement.
        var previousStartScores = lastHandStartScores;
        bool emitHandEnd = !firstRoll && previousStartScores is not null;

        RollWriter();

        if (emitHandEnd)
            EmitHandEnd(previousStartScores!, snap.Scores);

        var startScores = snap.Scores.ToArray();
        lastHandStartScores = startScores;
        var start = new HandStartEvent(
            T: Now(),
            E: "hand-start",
            V: SchemaVersion,
            Seat: snap.OurSeat,
            RoundWind: snap.RoundWind,
            Dealer: snap.DealerSeat,
            Honba: snap.Honba,
            RiichiSticks: snap.RiichiSticks,
            SeatInfoKnown: snap.SeatInfoKnown,
            Observations: (int)snap.Observations,
            Scores: startScores);
        WriteLine(JsonSerializer.Serialize(start, JsonOpts));
    }

    private void EmitHandEnd(IReadOnlyList<int> scoresBefore, IReadOnlyList<int> scoresAfter)
    {
        int n = Math.Min(scoresBefore.Count, scoresAfter.Count);
        var deltas = new int[n];
        for (int i = 0; i < n; i++)
            deltas[i] = scoresAfter[i] - scoresBefore[i];
        var (kind, winner, loser) = InferResultKind(deltas);
        var evt = new HandEndEvent(
            T: Now(),
            E: "hand-end",
            Kind: kind,
            Winner: winner,
            Loser: loser,
            Deltas: deltas,
            ScoresAfter: scoresAfter.ToArray());
        try
        { WriteLine(JsonSerializer.Serialize(evt, JsonOpts)); }
        catch (Exception ex) { log.Error($"GameLogger hand-end-write error: {ex.Message}"); }
    }

    internal static (string kind, int? winner, int? loser) InferResultKind(int[] deltas)
    {
        int pos = 0, neg = 0;
        int winnerIdx = -1, loserIdx = -1;
        int maxPos = 0, minNeg = 0;
        for (int i = 0; i < deltas.Length; i++)
        {
            if (deltas[i] > 0)
            {
                pos++;
                if (deltas[i] > maxPos)
                { maxPos = deltas[i]; winnerIdx = i; }
            }
            else if (deltas[i] < 0)
            {
                neg++;
                if (deltas[i] < minNeg)
                { minNeg = deltas[i]; loserIdx = i; }
            }
        }
        if (pos == 1 && neg == 1)
            return ("ron", winnerIdx, loserIdx);
        if (pos == 1 && neg == 3)
            return ("tsumo", winnerIdx, null);
        return ("draw", null, null);
    }

    private void RollWriter()
    {
        lock (writerLock)
        {
            handSeq++;
            var fn = $"game-{DateTime.UtcNow:yyyyMMdd-HHmmss}-hand{handSeq:D2}.ndjson";
            currentPath = Path.Combine(gamesDir, fn);
            sessionPaths.Add(currentPath);
        }
    }

    /// <summary>Open-write-close per line so diagnostics and local archive tooling can read live files.</summary>
    private void WriteLine(string line)
    {
        var path = currentPath;
        if (path is null) return;
        io.TryEnqueue(() => {
            Directory.CreateDirectory(gamesDir);
            using var writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
            writer.WriteLine(line);
        });
    }

    private static int ComputeContentHash(StateSnapshot snap)
    {
        var h = new HashCode();
        h.Add(snap.WallRemaining);
        h.Add(snap.TurnIndex);
        h.Add((int)snap.Legal.Flags);
        h.Add(snap.OurRiichi);
        h.Add(snap.OurIppatsu);
        h.Add(snap.OurSeat);
        h.Add(snap.RoundWind);
        h.Add(snap.DealerSeat);
        h.Add(snap.Honba);
        h.Add(snap.RiichiSticks);
        h.Add((int)snap.Observations);
        for (int i = 0; i < snap.Hand.Count; i++)
        {
            h.Add(snap.Hand[i].Id);
            h.Add(i < snap.HandIsRed.Count && snap.HandIsRed[i]);
        }
        foreach (var m in snap.OurMelds)
        {
            h.Add((int)m.Kind);
            foreach (var t in m.Tiles)
                h.Add(t.Id);
        }
        foreach (var t in snap.DoraIndicators)
            h.Add(t.Id);
        foreach (var s in snap.Scores)
            h.Add(s);
        foreach (var s in snap.Seats)
        {
            h.Add(s.DiscardCount);
            for (int i = 0; i < s.Discards.Count; i++)
            {
                h.Add(s.Discards[i].Id);
                h.Add(i < s.DiscardIsRed.Count && s.DiscardIsRed[i]);
                h.Add(i < s.DiscardIsTedashi.Count && s.DiscardIsTedashi[i]);
            }
            foreach (var m in s.Melds)
            {
                h.Add((int)m.Kind);
                foreach (var t in m.Tiles)
                    h.Add(t.Id);
            }
            h.Add(s.Riichi);
            h.Add(s.RiichiDiscardIndex);
            h.Add(s.Ippatsu);
        }
        return h.ToHashCode();
    }

    private DecisionEvent BuildDecisionEvent(
        ActionChoice choice, MeldTrackerStateDto? tracker, string source) => new(
        T: Now(),
        E: "decision",
        Source: source,
        HandId: aggregator?.Latest?.HandId ?? 0,
        Revision: aggregator?.Latest?.Revision ?? 0,
        IsRed: choice.DiscardIsRed,
        Complete: aggregator?.Latest?.PublicStateConsistent ?? false,
        ElapsedMs: aggregator?.LastEvaluation?.ElapsedMilliseconds,
        Kind: choice.Kind.ToString(),
        Tile: choice.DiscardTile?.Id,
        CallKind: choice.Call?.Kind.ToString(),
        Why: string.IsNullOrEmpty(choice.Reasoning) ? null : choice.Reasoning,
        Steps: choice.Steps is { Count: > 0 } steps
            ? steps.Select(r => new StepDto(K: r.Code, D: r.Display)).ToArray()
            : null,
        Tracker: tracker is { } t
            ? new TrackerDto(
                Melds: t.Melds,
                DeferredTicks: t.DeferredTicks,
                PendingSeat: t.PendingOppDiscardSeat,
                MeldAkadora: t.MeldAkadora)
            : null);

    private static StateEvent BuildStateEvent(StateSnapshot snap) => new(
        T: Now(),
        E: "state",
        StateCode: snap.AddonStateCode,
        Wall: snap.WallRemaining,
        Turn: snap.TurnIndex,
        Observations: (int)snap.Observations,
        Hand: snap.Hand.Select(t => (int)t.Id).ToArray(),
        HandRed: snap.HandIsRed.ToArray(),
        OurMelds: snap.OurMelds.Select(ToMeldDto).ToArray(),
        Dora: snap.DoraIndicators.Select(t => (int)t.Id).ToArray(),
        OurRiichi: snap.OurRiichi,
        OurIppatsu: snap.OurIppatsu,
        Legal: snap.Legal.Flags.ToString(),
        Scores: snap.Scores.ToArray(),
        Seats: snap.Seats.Select(ToSeatDto).ToArray());

    private static SeatDto ToSeatDto(SeatView s) => new(
        Dc: s.DiscardCount,
        D: s.Discards.Select(t => (int)t.Id).ToArray(),
        Dr: s.DiscardIsRed.ToArray(),
        Dt: s.DiscardIsTedashi.ToArray(),
        M: s.Melds.Select(ToMeldDto).ToArray(),
        R: s.Riichi,
        Ri: s.RiichiDiscardIndex,
        Ip: s.Ippatsu);

    private static MeldDto ToMeldDto(Meld m) => new(
        K: m.Kind.ToString(),
        T: m.Tiles.Select(t => (int)t.Id).ToArray(),
        C: m.ClaimedTile?.Id,
        Fs: m.ClaimedFromSeat);

    private static string Now() =>
        DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private sealed record HandStartEvent(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("seat")] int Seat,
        [property: JsonPropertyName("round_wind")] int RoundWind,
        [property: JsonPropertyName("dealer")] int Dealer,
        [property: JsonPropertyName("honba")] int Honba,
        [property: JsonPropertyName("riichi_sticks")] int RiichiSticks,
        [property: JsonPropertyName("seat_info_known")] bool SeatInfoKnown,
        [property: JsonPropertyName("obs")] int Observations,
        [property: JsonPropertyName("scores")] int[] Scores);

    private sealed record HandEndEvent(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("winner")] int? Winner,
        [property: JsonPropertyName("loser")] int? Loser,
        [property: JsonPropertyName("deltas")] int[] Deltas,
        [property: JsonPropertyName("scores_after")] int[] ScoresAfter);

    private sealed record StateEvent(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("state_code")] int StateCode,
        [property: JsonPropertyName("wall")] int Wall,
        [property: JsonPropertyName("turn")] int Turn,
        [property: JsonPropertyName("obs")] int Observations,
        [property: JsonPropertyName("hand")] int[] Hand,
        [property: JsonPropertyName("hand_red")] bool[] HandRed,
        [property: JsonPropertyName("our_melds")] MeldDto[] OurMelds,
        [property: JsonPropertyName("dora")] int[] Dora,
        [property: JsonPropertyName("our_riichi")] bool OurRiichi,
        [property: JsonPropertyName("our_ippatsu")] bool OurIppatsu,
        [property: JsonPropertyName("legal")] string Legal,
        [property: JsonPropertyName("scores")] int[] Scores,
        [property: JsonPropertyName("seats")] SeatDto[] Seats);

    private sealed record ActionEvent(
        [property: JsonPropertyName("action_id")] long ActionId,
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("hand_id")] long? HandId,
        [property: JsonPropertyName("revision")] long? Revision,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("tile")] int? Tile,
        [property: JsonPropertyName("slot")] int? Slot,
        [property: JsonPropertyName("result")] string Result,
        [property: JsonPropertyName("why")] string? Why);

    private sealed record DecisionEvent(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("hand_id")] long HandId,
        [property: JsonPropertyName("revision")] long Revision,
        [property: JsonPropertyName("is_red")] bool? IsRed,
        [property: JsonPropertyName("complete")] bool Complete,
        [property: JsonPropertyName("elapsed_ms")] double? ElapsedMs,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("tile")] int? Tile,
        [property: JsonPropertyName("call_kind")] string? CallKind,
        [property: JsonPropertyName("why")] string? Why,
        [property: JsonPropertyName("steps")] StepDto[]? Steps,
        [property: JsonPropertyName("tracker")] TrackerDto? Tracker);

    private sealed record StepDto(
        [property: JsonPropertyName("k")] string K,
        [property: JsonPropertyName("d")] string D);

    private sealed record TrackerDto(
        [property: JsonPropertyName("melds")] int Melds,
        [property: JsonPropertyName("deferred_ticks")] int DeferredTicks,
        [property: JsonPropertyName("pending_seat")] int PendingSeat,
        [property: JsonPropertyName("meld_akadora")] int MeldAkadora);

    private sealed record SeatDto(
        [property: JsonPropertyName("dc")] int Dc,
        [property: JsonPropertyName("d")] int[] D,
        [property: JsonPropertyName("dr")] bool[] Dr,
        [property: JsonPropertyName("dt")] bool[] Dt,
        [property: JsonPropertyName("m")] MeldDto[] M,
        [property: JsonPropertyName("r")] bool R,
        [property: JsonPropertyName("ri")] int Ri,
        [property: JsonPropertyName("ip")] bool Ip);

    private sealed record MeldDto(
        [property: JsonPropertyName("k")] string K,
        [property: JsonPropertyName("t")] int[] T,
        [property: JsonPropertyName("c")] int? C,
        [property: JsonPropertyName("fs")] int Fs);

    private sealed record CallPromptDto(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("e")] string E,
        [property: JsonPropertyName("variant")] string Variant,
        [property: JsonPropertyName("sc")] int StateCode,
        [property: JsonPropertyName("flags")] int Flags,
        [property: JsonPropertyName("pon")] int[] Pon,
        [property: JsonPropertyName("chi")] int[] Chi,
        [property: JsonPropertyName("kan")] int[] Kan,
        [property: JsonPropertyName("av")] int?[] Av);
}

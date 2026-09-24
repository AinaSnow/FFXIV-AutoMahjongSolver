using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>
/// Keeps one immutable local folder per table session. Raw Mahjong packets are
/// written as they are drained on the framework thread; game logs and a compact
/// summary are added when the table addon closes.
/// </summary>
public sealed class MatchArchiveWriter : IDisposable
{
    private const int SchemaVersion = 4;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions LineJsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IPluginLog log;
    private readonly object sync = new();
    private readonly string rootDir;

    private string? currentDir;
    private DateTimeOffset? startedAtUtc;
    private int packetCount;
    private int packetHandCount;
    private int packetSettledHands;
    private bool packetHandOpen;
    private int[]? lastPacketHandStartScores;
    private bool packetWriteFailed;
    private bool disposed;
    private readonly BackgroundIoWorker io;
    private readonly bool ownsIo;
    private readonly Func<(int Days, long Bytes)> retention;
    private readonly TrainingCorpusWriter? trainingCorpus;
    public Task FlushAsync() => io.FlushAsync();

    public MatchArchiveWriter(string pluginConfigDir, IPluginLog log, BackgroundIoWorker? io = null, Func<(int Days, long Bytes)>? retention = null, TrainingCorpusWriter? trainingCorpus = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDir);
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;
        this.trainingCorpus = trainingCorpus;
        this.io = io ?? new BackgroundIoWorker();
        ownsIo = io is null;
        this.retention = retention ?? (() => (30, 1L << 30));
        rootDir = Path.Combine(pluginConfigDir, "match-archives");
        this.io.TryEnqueue(() => Directory.CreateDirectory(rootDir));

    }

    public string RootDir => rootDir;

    public string? CurrentDirectory => currentDir;

    /// <summary>Called after capture dequeue, never from the receive detour.</summary>
    public void RecordPacket(CapturedMahjongPacket packet)
    {
        if (disposed) return;
        // Capture owns the original immutable payload; copy defensively for callers/tests.
        var copy = packet with { Payload = packet.Payload.ToArray() };
        io.TryEnqueue(() => RecordPacketCore(copy));
    }

    private void RecordPacketCore(CapturedMahjongPacket packet)
    {
        lock (sync)
        {

            TrackPacketHand(packet);
            if (packetWriteFailed)
                return;

            try
            {
                EnsureSession(packet.Timestamp);
                var evt = new PacketArchiveEvent(
                    T: packet.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    MessageId: packet.MessageId,
                    Opcode: $"0x{packet.Opcode:X4}",
                    PayloadHex: Convert.ToHexString(packet.Payload));
                AppendLine(
                    Path.Combine(currentDir!, "packets.ndjson"),
                    JsonSerializer.Serialize(evt, LineJsonOpts));
                packetCount++;
            }
            catch (Exception ex)
            {
                packetWriteFailed = true;
                log.Warning($"[MatchArchive] packet-write failed; current archive will contain game logs only: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Copies this table session's per-hand logs and seals the archive with a summary.
    /// Returns the archive directory, or <see langword="null"/> when no match data existed.
    /// </summary>
    public Task<string?> FinalizeSessionAsync(IReadOnlyList<string> gamePaths, MatchArchiveMortalStats mortalStats, MatchArchiveEnvironment? environment = null, TrainingArchiveContext? training = null)
    {
        var paths = gamePaths.ToArray();
        return io.RunAfterWritesAsync(() => FinalizeSessionCore(paths, mortalStats, environment, training));
    }

    // Synchronous compatibility entry for offline consumers. Runtime uses FinalizeSessionAsync.
    public string? FinalizeSession(IReadOnlyList<string> gamePaths, MatchArchiveMortalStats mortalStats) =>
        FinalizeSessionAsync(gamePaths, mortalStats).GetAwaiter().GetResult();

    private string? FinalizeSessionCore(
        IReadOnlyList<string> gamePaths,
        MatchArchiveMortalStats mortalStats,
        MatchArchiveEnvironment? environment, TrainingArchiveContext? training)
    {
        ArgumentNullException.ThrowIfNull(gamePaths);
        ArgumentNullException.ThrowIfNull(mortalStats);

        lock (sync)
        {

            var existingGames = gamePaths
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (currentDir is null && existingGames.Length == 0 && training?.RawPath is null)
                return null;

            string completedDir = currentDir ?? rootDir;
            try
            {
                EnsureSession(DateTimeOffset.UtcNow);
                completedDir = currentDir!;
                string archivedGamesDir = Path.Combine(completedDir, "games");
                Directory.CreateDirectory(archivedGamesDir);
                var copiedNames = new List<string>(existingGames.Length);
                foreach (string source in existingGames)
                {
                    string name = Path.GetFileName(source);
                    File.Copy(source, Path.Combine(archivedGamesDir, name), overwrite: true);
                    copiedNames.Add(name);
                }

                var metrics = Analyze(existingGames);
                if (metrics.FirstObservedAt is { } first && first < startedAtUtc) startedAtUtc = first;
                int handCount = packetHandCount > 0
                    ? packetHandCount
                    : Math.Max(metrics.HandStarts, metrics.SettledHands);
                int settledHands = Math.Max(packetSettledHands, metrics.SettledHands);
                if (packetHandOpen
                    && metrics.FinalScores is { Length: 4 }
                    && lastPacketHandStartScores is { Length: 4 }
                    && !metrics.FinalScores.SequenceEqual(lastPacketHandStartScores))
                {
                    settledHands++;
                }
                settledHands = Math.Min(handCount, settledHands);
                int? ourScore = metrics.FinalScores is { Length: 4 } ? metrics.FinalScores[0] : null;
                int? ourRank = metrics.FinalScores is { } finalScores
                    ? SeatRanking.Rank(finalScores, 0, metrics.InitialDealerSeat) : null;
                var summary = new MatchArchiveSummary(
                    SchemaVersion: SchemaVersion,
                    StartedAtUtc: startedAtUtc!.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    CompletedAtUtc: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    PacketCount: packetCount,
                    PacketWriteFailed: packetWriteFailed || io.Failures > 0 || metrics.MalformedLines > 0,
                    GameFiles: copiedNames.ToArray(),
                    HandCount: handCount,
                    SettledHands: settledHands,
                    FinalScores: metrics.FinalScores,
                    OurScore: ourScore,
                    OurRank: ourRank,
                    DecisionCount: metrics.DecisionCount,
                    ActionCount: metrics.ActionCount,
                    FailedActions: metrics.FailedActions,
                    TimeoutFallbacks: metrics.TimeoutFallbacks,
                    Mortal: mortalStats,
                    Environment: environment,
                    ExecutionHealth: new { outcomes = metrics.Outcomes, missing_outcomes = Math.Max(0, metrics.ActionCount - metrics.Outcomes.Values.Sum() - (metrics.FailedActions - metrics.Outcomes.GetValueOrDefault("timeout"))), success_requires = "state change with synchronous automated callback; not server confirmation" },
                    DecisionHealth: new { sources = metrics.Sources, mean_ms = metrics.MeanMs, p95_ms = metrics.P95Ms, malformed_lines = metrics.MalformedLines, io_failures = io.Failures });
                File.WriteAllText(
                    Path.Combine(completedDir, "summary.json"),
                    JsonSerializer.Serialize(summary, JsonOpts));
                File.WriteAllText(Path.Combine(completedDir, "managed-complete.json"),
                    JsonSerializer.Serialize(new { version = 1, incomplete = packetWriteFailed || io.Failures > 0 || metrics.MalformedLines > 0 }));
                if (trainingCorpus?.Enabled == true)
                {
                    string pending = Path.Combine(completedDir, "training-pending.json");
                    File.WriteAllText(pending, JsonSerializer.Serialize(new { raw_path = training?.RawPath, reason = "pending-preservation" }));
                    if (trainingCorpus.Preserve(completedDir, training)) File.Delete(pending);
                }
                ApplyRetention(completedDir);
                log.Information(
                    $"[MatchArchive] Saved local match archive: {completedDir} " +
                    $"(games={copiedNames.Count}, packets={packetCount}, decisions={metrics.DecisionCount}).");
                return completedDir;
            }
            catch (Exception ex)
            {
                try { if (currentDir is not null) File.WriteAllText(Path.Combine(currentDir, "incomplete.json"),
                    JsonSerializer.Serialize(new { reason = ex.GetType().Name, message = ex.Message })); } catch { }
                log.Error($"[MatchArchive] finalize failed for {completedDir}: {ex.Message}");
                return completedDir;
            }
            finally
            {
                currentDir = null;
                startedAtUtc = null;
                packetCount = 0;
                packetHandCount = 0;
                packetSettledHands = 0;
                packetHandOpen = false;
                lastPacketHandStartScores = null;
                packetWriteFailed = false;
            }
        }
    }

    public void Dispose()
    {
        disposed = true;
        if (ownsIo) io.Dispose();
        // Queued writes finish after disposal; callers stop producing before closing the queue.
    }

    private void ApplyRetention(string justCompleted)
    {
        var limits = retention();
        var root = new DirectoryInfo(rootDir);
        var archives = root.EnumerateDirectories("match-*")
            .Where(d => d.Parent?.FullName == root.FullName && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .Where(d => File.Exists(Path.Combine(d.FullName, "managed-complete.json")))
            .Where(d => !File.Exists(Path.Combine(d.FullName, "training-pending.json")))
            .Where(d => !d.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(f => f.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            .Select(d => new { Dir = d, End = File.GetLastWriteTimeUtc(Path.Combine(d.FullName, "managed-complete.json")),
                Size = d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) })
            .OrderBy(a => a.End).ToArray();
        long total = archives.Sum(a => a.Size);
        foreach (var archive in archives)
        {
            if (archive.Dir.FullName == justCompleted) continue;
            if (archive.End >= DateTime.UtcNow.AddDays(-Math.Max(1, limits.Days)) && total <= Math.Max(1, limits.Bytes)) continue;
            archive.Dir.Delete(recursive: true);
            total -= archive.Size;
        }
    }

    private void EnsureSession(DateTimeOffset timestamp)
    {
        if (currentDir is not null)
            return;

        startedAtUtc = timestamp.ToUniversalTime();
        string baseName = $"match-{startedAtUtc:yyyyMMdd-HHmmss}Z";
        string candidate = Path.Combine(rootDir, baseName);
        int suffix = 1;
        while (Directory.Exists(candidate))
            candidate = Path.Combine(rootDir, $"{baseName}-{suffix++:D2}");
        Directory.CreateDirectory(candidate);
        currentDir = candidate;
    }

    private static void AppendLine(string path, string line)
    {
        using var writer = new StreamWriter(new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read));
        writer.WriteLine(line);
    }

    private void TrackPacketHand(CapturedMahjongPacket packet)
    {
        switch (packet.MessageId)
        {
            case MahjongPacketMjaiDecoder.HandStartMessageId:
                // A following hand-start proves the previous hand settled even
                // when its result packet was dropped during bridge recovery.
                if (packetHandOpen)
                    packetSettledHands++;
                packetHandCount++;
                packetHandOpen = true;
                lastPacketHandStartScores = TryDecodeRelativeHandStartScores(packet.Payload);
                break;

            case MahjongPacketMjaiDecoder.HandResultAMessageId:
            case MahjongPacketMjaiDecoder.HandResultBMessageId:
            case MahjongPacketMjaiDecoder.DrawResultMessageId:
                if (packetHandOpen)
                {
                    packetSettledHands++;
                    packetHandOpen = false;
                }
                break;
        }
    }

    private static int[]? TryDecodeRelativeHandStartScores(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 48)
            return null;

        int selfSeat = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(24, sizeof(int)));
        if (selfSeat is < 0 or > 3)
            return null;

        var scores = new int[4];
        for (int relativeSeat = 0; relativeSeat < scores.Length; relativeSeat++)
        {
            int absoluteSeat = (relativeSeat + selfSeat) % scores.Length;
            scores[relativeSeat] = BinaryPrimitives.ReadInt32LittleEndian(
                payload.Slice(32 + absoluteSeat * sizeof(int), sizeof(int))) * 100;
        }
        return scores;
    }

    private static ArchiveMetrics Analyze(IReadOnlyList<string> paths)
    {
        DateTimeOffset? firstObservedAt = null;
        int handStarts = 0;
        int settledHands = 0;
        int[]? finalScores = null;
        int? initialDealerSeat = null;
        bool conflictingInitialOrder = false;
        int decisions = 0;
        int actions = 0;
        int failedActions = 0;
        int timeoutFallbacks = 0;
        int malformedLines = 0;
        var outcomes = new Dictionary<string, int>();
        var outcomeIds = new HashSet<long>();
        var sources = new Dictionary<string,int>();
        var times = new List<double>();

        foreach (string path in paths)
        {
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("t", out var time) && time.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var observed)
                        && (firstObservedAt is null || observed < firstObservedAt)) firstObservedAt = observed;
                    if (!root.TryGetProperty("e", out var eventType))
                        continue;
                    switch (eventType.GetString())
                    {
                        case "hand-start":
                            handStarts++;
                            break;

                        case "hand-end":
                            // A hand-end records an observed boundary/final screen. Its
                            // deltas need not sum to zero: riichi sticks can remain on the table.
                            if (!root.TryGetProperty("deltas", out var deltas)
                                || deltas.ValueKind != JsonValueKind.Array || deltas.GetArrayLength() != 4
                                || deltas.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out _))
                                || !root.TryGetProperty("scores_after", out var scores)
                                || TryReadScores(scores) is not { } handEndScores)
                            {
                                malformedLines++;
                                break;
                            }
                            settledHands++;
                            finalScores = handEndScores;
                            break;

                        case "state":
                            if (!conflictingInitialOrder && root.TryGetProperty("initial_dealer", out var initial)
                                && initial.ValueKind == JsonValueKind.Number && initial.TryGetInt32(out int east) && east is >= 0 and < 4)
                            {
                                if (initialDealerSeat is { } previous && previous != east)
                                { initialDealerSeat = null; conflictingInitialOrder = true; }
                                else initialDealerSeat = east;
                            }
                            if (root.TryGetProperty("scores", out var stateScores)
                                && TryReadScores(stateScores) is { } latestScores)
                                finalScores = latestScores;
                            break;

                        case "decision":
                            decisions++;
                            string source = root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString()! : "legacy";
                            sources[source] = sources.GetValueOrDefault(source) + 1;
                            if (root.TryGetProperty("elapsed_ms", out var elapsed) && elapsed.ValueKind == JsonValueKind.Number && elapsed.TryGetDouble(out var ms) && double.IsFinite(ms) && ms >= 0) times.Add(ms);
                            if (root.TryGetProperty("why", out var why)
                                && why.ValueKind == JsonValueKind.String
                                && why.GetString()?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true)
                                timeoutFallbacks++;
                            break;

                        case "action-outcome":
                            if (!root.TryGetProperty("action_id", out var actionId) || !actionId.TryGetInt64(out long id)
                                || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
                                || !outcomeIds.Add(id)) break;
                            string outcome = status.GetString()!;
                            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                            if (outcome == "timeout") failedActions++;
                            break;

                        case "action":
                            actions++;
                            if (root.TryGetProperty("result", out var result)
                                && result.ValueKind == JsonValueKind.String
                                && !string.Equals(result.GetString(), "Ok", StringComparison.OrdinalIgnoreCase))
                                failedActions++;
                            break;
                    }
                }
                catch (JsonException)
                {
                    malformedLines++;
                    // A partially-written final line should not prevent the rest of the match from being archived.
                }
            }
        }

        times.Sort();
        return new ArchiveMetrics(
            handStarts,
            settledHands,
            finalScores,
            decisions,
            actions,
            failedActions,
            timeoutFallbacks, sources, times.Count == 0 ? null : times.Average(),
            times.Count == 0 ? null : times[(int)Math.Ceiling(times.Count * .95) - 1], malformedLines, outcomes, firstObservedAt, initialDealerSeat);
    }

    private static int[]? TryReadScores(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 4)
            return null;

        int[] scores = new int[4];
        for (int i = 0; i < scores.Length; i++)
        {
            if (element[i].ValueKind != JsonValueKind.Number || !element[i].TryGetInt32(out scores[i])
                || scores[i] is < -200000 or > 200000)
                return null;
        }
        return scores.All(score => score == 0) ? null : scores;
    }

    private sealed record PacketArchiveEvent(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("message_id")] int MessageId,
        [property: JsonPropertyName("opcode")] string Opcode,
        [property: JsonPropertyName("payload_hex")] string PayloadHex);

    private sealed record MatchArchiveSummary(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("started_at_utc")] string StartedAtUtc,
        [property: JsonPropertyName("completed_at_utc")] string CompletedAtUtc,
        [property: JsonPropertyName("packet_count")] int PacketCount,
        [property: JsonPropertyName("packet_write_failed")] bool PacketWriteFailed,
        [property: JsonPropertyName("game_files")] string[] GameFiles,
        [property: JsonPropertyName("hand_count")] int HandCount,
        [property: JsonPropertyName("settled_hands")] int SettledHands,
        [property: JsonPropertyName("final_scores")] int[]? FinalScores,
        [property: JsonPropertyName("our_score")] int? OurScore,
        [property: JsonPropertyName("our_rank")] int? OurRank,
        [property: JsonPropertyName("decision_count")] int DecisionCount,
        [property: JsonPropertyName("action_count")] int ActionCount,
        [property: JsonPropertyName("failed_actions")] int FailedActions,
        [property: JsonPropertyName("timeout_fallbacks")] int TimeoutFallbacks,
        [property: JsonPropertyName("mortal")] MatchArchiveMortalStats Mortal,
        [property: JsonPropertyName("environment")] MatchArchiveEnvironment? Environment,
        [property: JsonPropertyName("decision_health")] object DecisionHealth,
        [property: JsonPropertyName("execution_health")] object ExecutionHealth);

    private sealed record ArchiveMetrics(
        int HandStarts,
        int SettledHands,
        int[]? FinalScores,
        int DecisionCount,
        int ActionCount,
        int FailedActions,
        int TimeoutFallbacks, Dictionary<string,int> Sources, double? MeanMs, double? P95Ms, int MalformedLines, Dictionary<string, int> Outcomes, DateTimeOffset? FirstObservedAt, int? InitialDealerSeat);
}

public sealed record MatchArchiveMortalStats(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("packets_processed")] long PacketsProcessed,
    [property: JsonPropertyName("events_sent")] long EventsSent,
    [property: JsonPropertyName("reactions_received")] long ReactionsReceived,
    [property: JsonPropertyName("decisions_mapped")] long DecisionsMapped,
    [property: JsonPropertyName("decision_timeouts")] long DecisionTimeouts,
    [property: JsonPropertyName("candidate_corrections")] long CandidateCorrections,
    [property: JsonPropertyName("recovered_discards")] long RecoveredDiscards,
    [property: JsonPropertyName("last_model_eval_ms")] double LastModelEvalMilliseconds);

public sealed record MatchArchiveEnvironment(
    [property: JsonPropertyName("game_version")] string? GameVersion,
    [property: JsonPropertyName("client_variant")] string? ClientVariant,
    [property: JsonPropertyName("protocol_verified")] bool ProtocolVerified,
    [property: JsonPropertyName("protocol_status")] string ProtocolStatus,
    [property: JsonPropertyName("plugin_build_id")] string PluginBuildId,
    [property: JsonPropertyName("ui_layout")] string? UiLayout = null);

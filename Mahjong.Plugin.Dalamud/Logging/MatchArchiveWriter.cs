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
    private const int SchemaVersion = 2;

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

    public MatchArchiveWriter(string pluginConfigDir, IPluginLog log)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDir);
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;
        rootDir = Path.Combine(pluginConfigDir, "match-archives");
        Directory.CreateDirectory(rootDir);
    }

    public string RootDir => rootDir;

    public string? CurrentDirectory
    {
        get
        {
            lock (sync)
                return currentDir;
        }
    }

    /// <summary>Called after capture dequeue, never from the receive detour.</summary>
    public void RecordPacket(CapturedMahjongPacket packet)
    {
        lock (sync)
        {
            if (disposed)
                return;

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
    public string? FinalizeSession(
        IReadOnlyList<string> gamePaths,
        MatchArchiveMortalStats mortalStats)
    {
        ArgumentNullException.ThrowIfNull(gamePaths);
        ArgumentNullException.ThrowIfNull(mortalStats);

        lock (sync)
        {
            if (disposed)
                return null;

            var existingGames = gamePaths
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (currentDir is null && existingGames.Length == 0)
                return null;

            EnsureSession(DateTimeOffset.UtcNow);
            string completedDir = currentDir!;
            try
            {
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
                int? ourRank = ourScore is not null
                    ? 1 + metrics.FinalScores!.Count(score => score > ourScore.Value)
                    : null;
                var summary = new MatchArchiveSummary(
                    SchemaVersion: SchemaVersion,
                    StartedAtUtc: startedAtUtc!.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    CompletedAtUtc: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    PacketCount: packetCount,
                    PacketWriteFailed: packetWriteFailed,
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
                    Mortal: mortalStats);
                File.WriteAllText(
                    Path.Combine(completedDir, "summary.json"),
                    JsonSerializer.Serialize(summary, JsonOpts));
                log.Information(
                    $"[MatchArchive] Saved local match archive: {completedDir} " +
                    $"(games={copiedNames.Count}, packets={packetCount}, decisions={metrics.DecisionCount}).");
                return completedDir;
            }
            catch (Exception ex)
            {
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
        lock (sync)
            disposed = true;
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
        int handStarts = 0;
        int settledHands = 0;
        int[]? finalScores = null;
        int decisions = 0;
        int actions = 0;
        int failedActions = 0;
        int timeoutFallbacks = 0;

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
                    if (!root.TryGetProperty("e", out var eventType))
                        continue;
                    switch (eventType.GetString())
                    {
                        case "hand-start":
                            handStarts++;
                            break;

                        case "hand-end":
                            if (!root.TryGetProperty("deltas", out var deltas)
                                || deltas.ValueKind != JsonValueKind.Array)
                                break;
                            int sum = deltas.EnumerateArray().Sum(item => item.GetInt32());
                            if (Math.Abs(sum) > 100)
                                break;
                            settledHands++;
                            if (root.TryGetProperty("scores_after", out var scores)
                                && TryReadScores(scores) is { } handEndScores)
                                finalScores = handEndScores;
                            break;

                        case "state":
                            if (root.TryGetProperty("scores", out var stateScores)
                                && TryReadScores(stateScores) is { } latestScores)
                                finalScores = latestScores;
                            break;

                        case "decision":
                            decisions++;
                            if (root.TryGetProperty("why", out var why)
                                && why.ValueKind == JsonValueKind.String
                                && why.GetString()?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true)
                                timeoutFallbacks++;
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
                    // A partially-written final line should not prevent the rest of the match from being archived.
                }
            }
        }

        return new ArchiveMetrics(
            handStarts,
            settledHands,
            finalScores,
            decisions,
            actions,
            failedActions,
            timeoutFallbacks);
    }

    private static int[]? TryReadScores(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return null;

        int[] scores = element.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        if (scores.Length != 4 || scores.All(score => score == 0))
            return null;
        return scores;
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
        [property: JsonPropertyName("mortal")] MatchArchiveMortalStats Mortal);

    private sealed record ArchiveMetrics(
        int HandStarts,
        int SettledHands,
        int[]? FinalScores,
        int DecisionCount,
        int ActionCount,
        int FailedActions,
        int TimeoutFallbacks);
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

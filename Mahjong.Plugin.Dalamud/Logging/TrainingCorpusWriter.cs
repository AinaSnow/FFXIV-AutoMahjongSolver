using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>Called only by the archive disk worker, before retention can remove any source.</summary>
public sealed class TrainingCorpusWriter
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    private readonly Func<bool> enabled;
    private readonly IPluginLog log;
    private string? lastError, lastMatch;
    public string RootDir { get; }
    public bool Enabled => enabled();
    public string? LastError => Volatile.Read(ref lastError);
    public string? LastMatch => Volatile.Read(ref lastMatch);
    public string Status => LastError ?? (LastMatch is { } match ? "Saved " + match : "Waiting for completed match");
    public TrainingCorpusWriter(string configDirectory, Func<bool> enabled, IPluginLog log)
    {
        RootDir = Path.GetFullPath(Path.Combine(configDirectory, "training-data"));
        this.enabled = enabled; this.log = log;
    }
    public void Retry() => Volatile.Write(ref lastError, null);
    internal static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
    internal static string Split(string matchId)
    {
        int bucket = (int)(Convert.ToUInt32(matchId[..8], 16) % 100);
        return bucket < 80 ? "train" : bucket < 90 ? "development" : "acceptance";
    }
    internal static IEnumerable<string> Files(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked dataset directory rejected");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked dataset file rejected");
            if (attrs.HasFlag(FileAttributes.Directory)) { foreach (var file in Files(path)) yield return file; }
            else yield return path;
        }
    }
    public bool Preserve(string archiveDirectory, TrainingArchiveContext? context)
    {
        if (!Enabled) return true;
        try
        {
            Directory.CreateDirectory(RootDir);
            if (File.GetAttributes(RootDir).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked training root rejected");
            string staging = Path.Combine(RootDir, ".pending-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            foreach (var file in Files(archiveDirectory))
            {
                var relative = Path.GetRelativePath(archiveDirectory, file);
                if (relative == "training-pending.json") continue;
                string target = Path.Combine(staging, "archive", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            var flags = new List<string>();
            var captures = context?.Captures ?? [];
            if (captures.Count == 0) flags.Add("raw_capture_missing");
            if (captures.Count > 1) flags.Add("raw_capture_segmented");
            for (int i = 0; i < captures.Count; i++)
            {
                var capture = captures[i];
                // Each segment owns an independent worker; capture restarts must not hide earlier data.
                if (!capture.Completion.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Raw capture seal timed out; source archive retained");
                string relative = i == 0 ? "raw-capture.ndjson" : $"raw-captures/segment-{i + 1:D3}.ndjson";
                string target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(capture.Path, target);
                var last = File.ReadLines(target).LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
                using var footer = JsonDocument.Parse(last ?? "{}");
                if (!footer.RootElement.TryGetProperty("stream_complete", out var complete) || complete.ValueKind != JsonValueKind.True)
                    flags.Add("raw_capture_incomplete");
                if (footer.RootElement.TryGetProperty("reason", out var reason) && reason.GetString() == "disabled")
                    flags.Add("raw_capture_interrupted");
            }
            if (context is not null)
            {
                File.WriteAllText(Path.Combine(staging, "provenance.json"), JsonSerializer.Serialize(context.Provenance, Json));
                File.WriteAllText(Path.Combine(staging, "policy-weights.json"), context.WeightsJson);
                if (context.Provenance.MortalEnabled && context.Provenance.MortalModel is null) flags.Add("mortal_model_unknown");
                if (context.Provenance.CalibrationIdentity.StartsWith("unknown", StringComparison.Ordinal)) flags.Add("calibration_unknown");
            }
            else flags.Add("provenance_unknown");
            string summaryPath = Path.Combine(staging, "archive", "summary.json");
            using var summary = JsonDocument.Parse(File.ReadAllText(summaryPath));
            var data = summary.RootElement;
            if (!data.TryGetProperty("packet_write_failed", out var failed) || failed.ValueKind != JsonValueKind.False) flags.Add("archive_incomplete");
            if (!data.TryGetProperty("environment", out var env) || env.ValueKind != JsonValueKind.Object
                || !env.TryGetProperty("protocol_verified", out var verified) || verified.ValueKind != JsonValueKind.True)
                flags.Add("protocol_not_fully_verified");
            // Summary scores may be a last-known score from an interrupted match. Only the observed final screen certifies a result.
            bool finalObserved = false;
            string games = Path.Combine(staging, "archive", "games");
            if (Directory.Exists(games)) foreach (var file in Files(games)) foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var row = JsonDocument.Parse(line);
                    var v = row.RootElement;
                    finalObserved |= v.TryGetProperty("e", out var e) && e.GetString() == "state"
                        && v.TryGetProperty("state_code", out var state) && state.TryGetInt32(out int code) && code == 27
                        && v.TryGetProperty("hand", out var hand) && hand.ValueKind == JsonValueKind.Array && hand.GetArrayLength() == 0
                        && v.TryGetProperty("scores", out var scores) && scores.ValueKind == JsonValueKind.Array && scores.GetArrayLength() == 4
                        && scores.EnumerateArray().All(s => s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var points) && points is >= -200000 and <= 200000)
                        && scores.EnumerateArray().Any(s => s.GetInt32() != 0);
                }
                catch (JsonException) { flags.Add("malformed_game_log"); }
            }
            if (!finalObserved) flags.Add("final_result_unobserved");
            bool openingObserved = false;
            string packets = Path.Combine(staging, "archive", "packets.ndjson");
            if (File.Exists(packets)) foreach (var line in File.ReadLines(packets))
            {
                try { using var row = JsonDocument.Parse(line); openingObserved |= row.RootElement.TryGetProperty("message_id", out var id) && id.TryGetInt32(out var code) && code == 636; }
                catch (JsonException) { flags.Add("malformed_packet_log"); }
            }
            if (!openingObserved) flags.Add("match_start_unobserved");
            var files = Files(staging).Select(file => new CorpusFile(Path.GetRelativePath(staging, file).Replace('\\', '/'),
                new FileInfo(file).Length, HashFile(file))).ToArray();
            if (!files.Any(f => f.Path is "archive/packets.ndjson" or "raw-capture.ndjson" || f.Path.StartsWith("archive/games/", StringComparison.Ordinal)))
                throw new InvalidDataException("No match events to preserve");
            string identity = files.FirstOrDefault(f => f.Path == "archive/packets.ndjson")?.Sha256
                ?? files.FirstOrDefault(f => f.Path == "raw-capture.ndjson")?.Sha256
                ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", files.Where(f => f.Path.StartsWith("archive/games/", StringComparison.Ordinal)).Select(f => f.Sha256)))));
            string matchId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("ffxiv-match-v1\n" + identity)));
            string split = openingObserved && finalObserved ? Split(matchId) : "unassigned";
            var manifest = new { schema_version = 1, match_id = matchId, split, split_version = "match-sha256-v1",
                data_origin = "ffxiv-live", created_utc = DateTimeOffset.UtcNow, source_archive = Path.GetFileName(archiveDirectory),
                raw_capture = context?.RawPath is { } path ? Path.GetFileName(path) : null,
                raw_captures = captures.Select(c => Path.GetFileName(c.Path)).ToArray(),
                match_start_observed = openingObserved, final_result_observed = finalObserved, training_ready = false, contains_private_raw_data = true,
                quality_flags = flags.Distinct().Order().ToArray(), files };
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
            string destination = Path.Combine(RootDir, "match-" + matchId);
            if (Directory.Exists(destination))
            {
                // Do not silently replace an existing dataset. An exact duplicate can be discarded after verification.
                var storedFiles = Files(destination).Select(f => Path.GetRelativePath(destination, f).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
                if (!storedFiles.SetEquals(files.Select(f => f.Path).Append("manifest.json")))
                    throw new IOException("Unlisted or missing files in existing corpus");
                using var old = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "manifest.json")));
                if (old.RootElement.GetProperty("match_id").GetString() != matchId) throw new IOException("Conflicting corpus identity");
                var existing = old.RootElement.GetProperty("files").EnumerateArray().ToArray();
                if (files.Length != existing.Length || files.Any(f => !existing.Any(e => e.GetProperty("path").GetString() == f.Path && e.GetProperty("sha256").GetString() == f.Sha256)))
                    throw new IOException("Conflicting records for the same match; existing copy preserved");
                foreach (var f in files)
                    if (HashFile(Path.Combine(destination, f.Path)) != f.Sha256) throw new IOException("Existing corpus checksum mismatch");
                // staging is a fresh GUID child of RootDir, created above by this method.
                Directory.Delete(staging, true);
            }
            else Directory.Move(staging, destination);
            Volatile.Write(ref lastMatch, matchId); Volatile.Write(ref lastError, null);
            log.Information($"[TrainingData] Preserved match {matchId}; split={split}; flags={string.Join(',', flags)}");
            return true;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref lastError, "Training data save failed: " + ex.Message);
            log.Warning($"[TrainingData] {LastError}");
            return false;
        }
    }
    private sealed record CorpusFile(string Path, long Bytes, string Sha256);
}

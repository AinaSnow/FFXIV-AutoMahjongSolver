using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Mahjong.Plugin.Dalamud.Telemetry;

public sealed class TelemetryUploader : IDisposable
{
    public static readonly string[] StreamDirs =
        { "games", "errors", "findings", "memdumps", "discards", "inputs", "sigprobes" };

    private const string ShippedMarkerSuffix = ".shipped";
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpTelemetryClient http;
    private readonly EndpointHolder endpoint;
    private readonly IPluginLog log;
    private readonly string configDir;
    private readonly CancellationTokenSource cts = new();
    private readonly Channel<UploadJob> queue;
    private readonly ConcurrentDictionary<string, byte> queuedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Task workerTask;
    private long rateLimitedUntilUtcTicks;
    private bool disposed;

    public TelemetryUploader(
        HttpTelemetryClient http,
        EndpointHolder endpoint,
        IPluginLog log,
        string configDir)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrEmpty(configDir);

        this.http = http;
        this.endpoint = endpoint;
        this.log = log;
        this.configDir = configDir;

        // Files remain on disk for retry; queuedPaths prevents repeated scans from duplicating jobs in memory.
        queue = Channel.CreateUnbounded<UploadJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        EnsureStreamDirs();
        workerTask = Task.Run(() => RunAsync(cts.Token));
    }

    /// <summary>Currently-resolved endpoint config (URL, enabled, version gate).</summary>
    public TelemetryEndpoint CurrentEndpoint => endpoint.Current;

    /// <summary>Count files under streamDirs/* that don't yet have a sibling .shipped marker.</summary>
    public int CountPending()
    {
        int n = 0;
        foreach (var stream in StreamDirs)
        {
            var dir = Path.Combine(configDir, stream);
            if (!Directory.Exists(dir))
                continue;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (f.EndsWith(ShippedMarkerSuffix, StringComparison.Ordinal))
                    continue;
                if (File.Exists(f + ShippedMarkerSuffix))
                    continue;
                n++;
            }
        }
        return n;
    }

    public void Enqueue(string stream, string filePath)
    {
        if (disposed)
            return;
        if (DateTime.UtcNow.Ticks < Volatile.Read(ref rateLimitedUntilUtcTicks))
            return;
        if (string.IsNullOrEmpty(stream) || string.IsNullOrEmpty(filePath))
            return;
        if (!queuedPaths.TryAdd(filePath, 0))
            return;
        if (!queue.Writer.TryWrite(new UploadJob(stream, filePath)))
            queuedPaths.TryRemove(filePath, out _);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        { queue.Writer.TryComplete(); }
        catch { }
        try
        {
            // Hard timeout — Dalamud unloads can't block; pending files retry on next launch.
            if (!workerTask.Wait(DisposeDrainTimeout))
                cts.Cancel();
        }
        catch { }
        cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var lastScan = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastScan >= ScanInterval)
                {
                    EnqueuePendingFiles();
                    lastScan = DateTime.UtcNow;
                }

                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(ScanInterval);
                UploadJob job;
                try
                {
                    job = await queue.Reader.ReadAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                try
                {
                    await ProcessJobAsync(job, ct).ConfigureAwait(false);
                }
                finally
                {
                    queuedPaths.TryRemove(job.FilePath, out _);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.Warning($"[Telemetry] uploader loop error: {ex.Message}");
                try
                { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private async Task ProcessJobAsync(UploadJob job, CancellationToken ct)
    {
        if (!File.Exists(job.FilePath) || IsShipped(job.FilePath))
            return;

        var url = endpoint.Current.UploadUrl;
        if (string.IsNullOrWhiteSpace(url) || !endpoint.Current.Enabled)
            return;
        if (DateTime.UtcNow.Ticks < Volatile.Read(ref rateLimitedUntilUtcTicks))
            return;

        // Exponential backoff with hard cap — scan re-enqueues on failure.
        var delays = new[] { 1, 2, 4, 8, 16 };
        for (int attempt = 0; attempt < delays.Length; attempt++)
        {
            if (ct.IsCancellationRequested)
                return;

            var result = await http.UploadAsync(url, job.Stream, job.FilePath, ct).ConfigureAwait(false);
            if (result == TelemetryUploadResult.Uploaded)
            {
                MarkShipped(job.FilePath);
                return;
            }

            if (result == TelemetryUploadResult.RateLimited)
            {
                var until = NextUtcDay();
                Volatile.Write(ref rateLimitedUntilUtcTicks, until.Ticks);
                log.Info(
                    $"[Telemetry] daily upload quota reached; deferring pending files until {until:O}");
                return;
            }

            try
            { await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), ct).ConfigureAwait(false); }
            catch { return; }
        }
    }

    private void EnqueuePendingFiles()
    {
        foreach (var stream in StreamDirs)
        {
            var dir = Path.Combine(configDir, stream);
            if (!Directory.Exists(dir))
                continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir))
                {
                    if (path.EndsWith(ShippedMarkerSuffix, StringComparison.Ordinal))
                        continue;
                    if (IsShipped(path))
                        continue;
                    Enqueue(stream, path);
                }
            }
            catch (Exception ex)
            {
                log.Warning($"[Telemetry] scan failed for {stream}: {ex.Message}");
            }
        }
    }

    private static bool IsShipped(string filePath) =>
        File.Exists(filePath + ShippedMarkerSuffix);

    private static void MarkShipped(string filePath)
    {
        try
        { File.WriteAllText(filePath + ShippedMarkerSuffix, "1"); }
        catch { }
    }

    private void EnsureStreamDirs()
    {
        foreach (var s in StreamDirs)
        {
            try
            { Directory.CreateDirectory(Path.Combine(configDir, s)); }
            catch { }
        }
    }

    private static DateTime NextUtcDay()
    {
        var now = DateTime.UtcNow;
        return now.Date.AddDays(1);
    }

    private readonly record struct UploadJob(string Stream, string FilePath);
}

public sealed class EndpointHolder
{
    public TelemetryEndpoint Current { get; private set; }

    public EndpointHolder(TelemetryEndpoint initial)
    {
        Current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    public void Set(TelemetryEndpoint next) => Current = next ?? Current;
}

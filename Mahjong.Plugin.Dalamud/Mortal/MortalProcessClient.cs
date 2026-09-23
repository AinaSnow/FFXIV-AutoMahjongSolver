using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Mortal;

public sealed record MortalProcessSettings(
    string WslDistribution,
    string WorkingDirectory,
    string PythonExecutable)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(WslDistribution)
        && !string.IsNullOrWhiteSpace(WorkingDirectory)
        && !string.IsNullOrWhiteSpace(PythonExecutable);
}

/// <summary>Nonblocking ordered transport. Callbacks are drained on the framework thread.</summary>
internal interface IMortalProcessClient : IDisposable
{
    event Action<string>? ReactionReceived;
    event Action<int?>? Exited;
    bool IsRunning { get; }
    void Start(MortalProcessSettings settings);
    void Send(IMjaiEvent evt);
    void SendReplay(string serializedEvent);
    void Poll();
}

internal sealed class MortalProcessClient : IMortalProcessClient
{
    private readonly System.Threading.Channels.Channel<string> input =
        System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(512) { SingleReader = true, FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> notifications = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly IPluginLog log;
    private readonly string runnerDirectory;
    private readonly string session = Guid.NewGuid().ToString("N");
    private long sequence, hand;
    private volatile bool running, disposed;
    private Task? worker;
    private readonly Func<MortalProcessSettings, IManagedJsonProcess> processFactory;
    internal Task Completion => worker ?? Task.CompletedTask;
    public event Action<string>? ReactionReceived;
    public event Action<int?>? Exited;
    public bool IsRunning => running && !disposed;
    public MortalProcessClient(IPluginLog log, string? runnerDirectory = null, Func<MortalProcessSettings, IManagedJsonProcess>? processFactory = null)
    {
        this.log = log; this.runnerDirectory = runnerDirectory ?? AppContext.BaseDirectory;
        this.processFactory = processFactory ?? (settings => new ManagedJsonProcess(CreateStartInfo(settings)));
    }

    public void Start(MortalProcessSettings settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!settings.IsValid) throw new ArgumentException("Mortal WSL settings are incomplete.", nameof(settings));
        if (worker is not null) throw new InvalidOperationException("Already started");
        running = true;
        worker = Task.Run(() => RunAsync(settings));
    }

    public void Poll()
    {
        for (int i = 0; i < 512 && notifications.TryDequeue(out var callback); i++)
            if (!disposed) callback();
    }

    public void Send(IMjaiEvent evt) => SendRaw(JsonSerializer.Serialize(evt, evt.GetType()));
    public void SendReplay(string serializedEvent) => SendRaw(CreateReplayPayload(serializedEvent));
    internal static string CreateReplayPayload(string serializedEvent)
    {
        if (string.IsNullOrWhiteSpace(serializedEvent)) throw new ArgumentException("replay event cannot be empty", nameof(serializedEvent));
        var payload = JsonNode.Parse(serializedEvent) as JsonObject
            ?? throw new ArgumentException("replay event must be a JSON object", nameof(serializedEvent));
        payload["can_act"] = false;
        return payload.ToJsonString();
    }

    private void SendRaw(string json)
    {
        if (!IsRunning) throw new InvalidOperationException("Mortal process is not running");
        var evt = JsonNode.Parse(json)!;
        if (evt["type"]?.GetValue<string>() == "start_kyoku") Interlocked.Increment(ref hand);
        var line = JsonSerializer.Serialize(new { session, hand = Interlocked.Read(ref hand), sequence = Interlocked.Increment(ref sequence), @event = evt });
        if (!input.Writer.TryWrite(line))
        {
            Dispose();
            throw new IOException("Mortal input queue full; replay required");
        }
    }

    private async Task RunAsync(MortalProcessSettings settings)
    {
        IManagedJsonProcess? child = null;
        Task[] streams = [];
        int? exitCode = null;
        try
        {
            child = processFactory(settings);
            child.Start();
            var send = WriteAsync(child);
            var receive = ReadAsync(child);
            var errors = ReadErrorsAsync(child);
            streams = [send, receive, errors];
            var exited = child.WaitForExitAsync(lifetime.Token);
            var first = await Task.WhenAny(send, receive, errors, exited).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (first != exited && !lifetime.IsCancellationRequested)
                throw new IOException("Mortal stream closed unexpectedly");
            if (first == exited) exitCode = await exited.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { log.Warning(ex, "[Mortal] Background transport failed; replay required."); }
        finally
        {
            lifetime.Cancel();
            try { child?.Terminate(); } catch (Exception ex) { log.Warning(ex, "[Mortal] Process termination failed."); }
            try { await Task.WhenAll(streams).ConfigureAwait(false); } catch { /* Original stream failure is logged above. */ }
            child?.Dispose();
            running = false;
            notifications.Enqueue(() => Exited?.Invoke(exitCode));
        }
    }

    private ProcessStartInfo CreateStartInfo(MortalProcessSettings settings)
    {
        var info = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        string runner = Path.Combine(runnerDirectory, "mortal_runner.py");
        if (!File.Exists(runner)) throw new FileNotFoundException("Managed Mortal runner missing", runner);
        string linuxRunner = $"/mnt/{char.ToLowerInvariant(runner[0])}/{runner[3..].Replace('\\', '/')}";
        foreach (var arg in new[] { "-d", settings.WslDistribution, "--cd", settings.WorkingDirectory, "--exec", settings.PythonExecutable, "-u", linuxRunner })
            info.ArgumentList.Add(arg);
        return info;
    }

    private async Task WriteAsync(IManagedJsonProcess child)
    {
        await foreach (string line in input.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
        {
            await child.Input.WriteLineAsync(line.AsMemory(), lifetime.Token).ConfigureAwait(false);
            await child.Input.FlushAsync(lifetime.Token).ConfigureAwait(false);
        }
    }
    private async Task ReadAsync(IManagedJsonProcess child)
    {
        long acknowledged = 0;
        while (await child.Output.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            var result = doc.RootElement;
            if (result.GetProperty("session").GetString() != session) throw new IOException("Mortal session mismatch");
            long seq = result.GetProperty("sequence").GetInt64();
            if (seq != ++acknowledged) throw new IOException("Mortal acknowledgement sequence mismatch");
            long resultHand = result.GetProperty("hand").GetInt64();
            if (result.TryGetProperty("error", out var error)) throw new IOException(error.GetString());
            if (result.TryGetProperty("reaction", out var reaction) && reaction.ValueKind == JsonValueKind.Object)
            {
                string json = reaction.GetRawText();
                if (notifications.Count >= 512) throw new IOException("Mortal output queue full");
                notifications.Enqueue(() => {
                    if (IsRunning && resultHand == Interlocked.Read(ref hand) && seq == Interlocked.Read(ref sequence))
                        ReactionReceived?.Invoke(json);
                });
            }
        }
    }
    private async Task ReadErrorsAsync(IManagedJsonProcess child)
    {
        while (await child.Error.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            log.Warning($"[Mortal stderr] {line}");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        input.Writer.TryComplete();
        lifetime.Cancel();
        // Cleanup and process termination belong to the worker, never the game thread.
    }
}

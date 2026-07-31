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

/// <summary>Owns one Mortal JSONL process launched through WSL.</summary>
internal sealed class MortalProcessClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object inputLock = new();
    private readonly IPluginLog log;
    private Process? process;
    private bool disposed;

    public MortalProcessClient(IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        this.log = log;
    }

    public event Action<string>? ReactionReceived;

    public event Action<int?>? Exited;

    public bool IsRunning => process is { HasExited: false };

    public void Start(MortalProcessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!settings.IsValid)
            throw new ArgumentException("Mortal WSL settings are incomplete.", nameof(settings));
        if (process is not null)
            throw new InvalidOperationException("Mortal process has already been started.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(settings.WslDistribution);
        startInfo.ArgumentList.Add("--cd");
        startInfo.ArgumentList.Add(settings.WorkingDirectory);
        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add(settings.PythonExecutable);
        startInfo.ArgumentList.Add("mortal.py");
        startInfo.ArgumentList.Add("0");

        var child = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        child.Exited += (_, _) => Exited?.Invoke(TryGetExitCode(child));
        if (!child.Start())
            throw new InvalidOperationException("wsl.exe did not start.");

        process = child;
        _ = PumpOutputAsync(child);
        _ = PumpErrorAsync(child);
    }

    public void Send(IMjaiEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        string json = JsonSerializer.Serialize(evt, evt.GetType(), JsonOptions);
        SendRaw(json);
    }

    public void SendReplay(string serializedEvent) =>
        SendRaw(CreateReplayPayload(serializedEvent));

    internal static string CreateReplayPayload(string serializedEvent)
    {
        if (string.IsNullOrWhiteSpace(serializedEvent))
            throw new ArgumentException("replay event cannot be empty", nameof(serializedEvent));

        var payload = JsonNode.Parse(serializedEvent) as JsonObject
            ?? throw new ArgumentException("replay event must be a JSON object", nameof(serializedEvent));
        payload["can_act"] = false;
        return payload.ToJsonString(JsonOptions);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        var child = process;
        process = null;
        if (child is null)
            return;

        try
        {
            lock (inputLock)
            {
                if (!child.HasExited)
                    child.StandardInput.Close();
            }
            if (!child.HasExited && !child.WaitForExit(750))
                child.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Mortal] Failed to stop the managed WSL process cleanly.");
        }
        finally
        {
            child.Dispose();
        }
    }

    private void SendRaw(string json)
    {
        var child = process;
        if (child is null || child.HasExited)
            throw new InvalidOperationException("Mortal process is not running.");

        lock (inputLock)
        {
            child.StandardInput.WriteLine(json);
            child.StandardInput.Flush();
        }
    }

    private async Task PumpOutputAsync(Process child)
    {
        try
        {
            while (await child.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    ReactionReceived?.Invoke(line);
            }
        }
        catch (Exception ex) when (disposed || child.HasExited)
        {
            log.Debug(ex, "[Mortal] stdout reader stopped.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Mortal] stdout reader failed.");
        }
    }

    private async Task PumpErrorAsync(Process child)
    {
        try
        {
            while (await child.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    log.Warning($"[Mortal stderr] {line}");
            }
        }
        catch (Exception ex) when (disposed || child.HasExited)
        {
            log.Debug(ex, "[Mortal] stderr reader stopped.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Mortal] stderr reader failed.");
        }
    }

    private static int? TryGetExitCode(Process child)
    {
        try { return child.ExitCode; }
        catch { return null; }
    }
}

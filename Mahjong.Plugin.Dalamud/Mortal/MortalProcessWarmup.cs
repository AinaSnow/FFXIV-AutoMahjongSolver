using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Mortal;

/// <summary>Framework-owned single-use process preparation. Never sends MJAI or subscribes to reactions.</summary>
internal sealed class MortalProcessWarmup(Func<IMortalProcessClient> factory, MonotonicClock clock) : IDisposable
{
    internal static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);
    private IMortalProcessClient? pending;
    private MortalProcessSettings? settings;
    private DateTime started, retryAfter;
    private bool disposed;
    public bool HasProcess => pending is not null;
    public string Status { get; private set; } = "Prewarm off";

    public void Update(bool allowed, bool prepare, MortalProcessSettings requested)
    {
        if (disposed) return;
        if (!allowed || !requested.IsValid)
        {
            Cancel();
            return;
        }
        if (settings != requested)
        {
            Cancel();
            settings = requested;
        }
        if (pending is not null)
        {
            if (!pending.IsRunning || !pending.IsReady && clock.UtcNow - started >= LoadTimeout)
            {
                pending.Dispose();
                pending = null;
                retryAfter = clock.UtcNow + RetryDelay;
                Status = "Prewarm failed or timed out; backing off";
                return;
            }
            Status = pending.IsReady ? "Prewarm ready; waiting for table" : "Prewarming model; no game inputs";
            return;
        }
        if (!prepare || clock.UtcNow < retryAfter) return;
        IMortalProcessClient? next = null;
        try
        {
            next = factory();
            next.Start(requested);
            pending = next;
            started = clock.UtcNow;
            Status = "Prewarming model; no game inputs";
        }
        catch (Exception ex)
        {
            next?.Dispose();
            retryAfter = clock.UtcNow + RetryDelay;
            Status = $"Prewarm failed: {ex.Message}; backing off";
        }
    }

    // A loading process can also be handed over: it keeps its remaining startup work,
    // while the live bridge queues only verified events and still uses normal deadlines.
    public IMortalProcessClient? Take(MortalProcessSettings requested)
    {
        if (disposed || settings != requested || pending?.IsRunning != true) return null;
        var result = pending;
        pending = null;
        Status = "Prewarm adopted";
        return result;
    }

    public void Cancel()
    {
        pending?.Dispose();
        pending = null;
        settings = null;
        retryAfter = DateTime.MinValue;
        Status = "Prewarm off";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Cancel();
    }
}

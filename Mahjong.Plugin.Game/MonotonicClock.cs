namespace Mahjong.Plugin.Game;

/// <summary>Wall-clock-shaped timestamps with monotonic elapsed time. UTC corrections cannot extend a timeout.</summary>
public sealed class MonotonicClock
{
    private readonly TimeProvider provider;
    private readonly DateTime origin;
    private readonly long started;
    public MonotonicClock(TimeProvider? provider = null)
    {
        this.provider = provider ?? TimeProvider.System;
        origin = this.provider.GetUtcNow().UtcDateTime;
        started = this.provider.GetTimestamp();
    }
    public DateTime UtcNow => origin + provider.GetElapsedTime(started);
}

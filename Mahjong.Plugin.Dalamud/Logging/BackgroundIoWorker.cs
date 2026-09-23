using System.Threading.Channels;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>One ordered, bounded disk queue. Barriers include every preceding write.</summary>
public sealed class BackgroundIoWorker : IDisposable
{
    private readonly Channel<Action> queue;
    private readonly Task pump;
    private long failures;
    private int closing, pendingMarkers;
    public long Failures => Interlocked.Read(ref failures);
    public BackgroundIoWorker(int capacity = 4096)
    {
        queue = Channel.CreateBounded<Action>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        pump = Task.Run(async () => {
            await foreach (var write in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                try { write(); } catch { Interlocked.Increment(ref failures); }
        });
    }
    public bool TryEnqueue(Action write)
    {
        if (Volatile.Read(ref closing) == 0 && queue.Writer.TryWrite(write)) return true;
        Interlocked.Increment(ref failures);
        return false;
    }
    public async Task<T> RunAfterWritesAsync<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref pendingMarkers);
        try
        {
            await queue.Writer.WriteAsync(() => {
                try { done.SetResult(action()); } catch (Exception ex) { Interlocked.Increment(ref failures); done.SetException(ex); }
            }).ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Decrement(ref pendingMarkers) == 0 && Volatile.Read(ref closing) != 0) queue.Writer.TryComplete();
        }
        return await done.Task.ConfigureAwait(false);
    }
    public Task FlushAsync() => Volatile.Read(ref closing) != 0 ? pump : RunAfterWritesAsync(() => true);
    public Task Completion => pump;
    public void Dispose()
    {
        Interlocked.Exchange(ref closing, 1);
        if (Volatile.Read(ref pendingMarkers) == 0) queue.Writer.TryComplete();
    }
}

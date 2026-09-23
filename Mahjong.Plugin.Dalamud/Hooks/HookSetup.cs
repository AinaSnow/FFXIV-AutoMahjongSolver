namespace Mahjong.Plugin.Dalamud.Hooks;

/// <summary>Publish before enabling; release partially initialized hooks on failure.</summary>
internal static class HookSetup
{
    internal static bool TryEnable<T>(ref T? hook, Func<T> create, Action<T> enable, out Exception? failure)
        where T : class, IDisposable
    {
        try
        {
            hook ??= create();
            enable(hook);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            var partial = hook;
            hook = null;
            try { partial?.Dispose(); }
            catch (Exception cleanup) { failure = new AggregateException(ex, cleanup); }
            return false;
        }
    }

    internal static string DescribeFailure(Exception failure)
    {
        bool allocation = IsAllocationFailure(failure);
        if (failure is AggregateException aggregate)
            allocation |= aggregate.Flatten().InnerExceptions.Any(IsAllocationFailure);
        return allocation
            ? "hook-memory-allocation: Dalamud could not allocate a nearby hook buffer. Restarting the game may help; plugin reload alone may not."
            : $"hook-initialization: {failure.GetType().Name}: {failure.Message}";
    }

    private static bool IsAllocationFailure(Exception failure) =>
        failure.Message.Contains("Unable to find memory location to fit MemoryBuffer", StringComparison.Ordinal)
        || failure.InnerException is { } inner && IsAllocationFailure(inner);
}

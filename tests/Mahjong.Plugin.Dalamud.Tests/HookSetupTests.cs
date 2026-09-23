using Mahjong.Plugin.Dalamud.Hooks;

namespace Mahjong.Plugin.Dalamud.Tests;

public class HookSetupTests
{
    private sealed class FakeHook : IDisposable
    {
        public int Disposals;
        public Exception? CleanupFailure;
        public void Dispose() { Disposals++; if (CleanupFailure is not null) throw CleanupFailure; }
    }

    [Fact]
    public void Creation_failure_does_not_enable_or_leave_a_published_hook()
    {
        FakeHook? hook = null;
        var error = new Exception("Unable to find memory location to fit MemoryBuffer of size 32 (65536)");
        Assert.False(HookSetup.TryEnable(ref hook, () => throw error,
            _ => throw new InvalidOperationException("must not enable"), out var failure));
        Assert.Null(hook);
        Assert.Same(error, failure);
        Assert.StartsWith("hook-memory-allocation:", HookSetup.DescribeFailure(failure!));
    }

    [Fact]
    public void Enable_failure_disposes_partially_created_hook_and_allows_clean_retry()
    {
        FakeHook? hook = null;
        var first = new FakeHook();
        Assert.False(HookSetup.TryEnable(ref hook, () => first,
            _ => throw new IOException("enable failed"), out _));
        Assert.Null(hook);
        Assert.Equal(1, first.Disposals);
        var second = new FakeHook();
        Assert.True(HookSetup.TryEnable(ref hook, () => second,
            _ => Assert.Same(second, hook), out var failure));
        Assert.Same(second, hook);
        Assert.Null(failure);
        Assert.Equal(0, second.Disposals);
    }

    [Fact]
    public void Cleanup_failure_preserves_original_cause_without_blocking_plugin_startup()
    {
        var error = new Exception("Unable to find memory location to fit MemoryBuffer");
        var partial = new FakeHook { CleanupFailure = new IOException("cleanup failed") };
        FakeHook? hook = partial;
        Assert.False(HookSetup.TryEnable(ref hook, () => throw new Exception("must reuse"),
            _ => throw error, out var failure));
        Assert.Null(hook);
        Assert.Equal(1, partial.Disposals);
        Assert.Contains(error, Assert.IsType<AggregateException>(failure).InnerExceptions);
        Assert.StartsWith("hook-memory-allocation:", HookSetup.DescribeFailure(failure!));
    }

    [Fact]
    public void Signature_failure_is_not_misreported_as_memory_pressure()
    {
        var message = HookSetup.DescribeFailure(new KeyNotFoundException("signature not found"));
        Assert.StartsWith("hook-initialization:", message);
        Assert.Contains("signature not found", message);
    }
}

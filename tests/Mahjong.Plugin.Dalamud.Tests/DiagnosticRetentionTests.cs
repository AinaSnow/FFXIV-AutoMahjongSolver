using System.Linq;
using Mahjong.Plugin.Dalamud.Logging;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class DiagnosticRetentionTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "mj-retention-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    public DiagnosticRetentionTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private string Make(string name, int bytes, TimeSpan age)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    private string[] Remaining() => Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    [Fact]
    public void Removes_files_older_than_retention_only()
    {
        Make("memdumps-old.ndjson", 10, TimeSpan.FromDays(20));
        Make("memdumps-new.ndjson", 10, TimeSpan.FromDays(2));

        int removed = DiagnosticRetention.Apply(dir, "memdumps-*.ndjson*", days: 14, maxBytes: 1 << 20, nowUtc: Now);

        Assert.Equal(1, removed);
        Assert.Equal(new[] { "memdumps-new.ndjson" }, Remaining());
    }

    [Fact]
    public void Size_cap_removes_oldest_first_and_spares_active_files()
    {
        Make("memdumps-a.ndjson", 100, TimeSpan.FromDays(3));
        Make("memdumps-b.ndjson", 100, TimeSpan.FromDays(2));
        Make("memdumps-c.ndjson", 100, TimeSpan.FromMinutes(1));

        DiagnosticRetention.Apply(dir, "memdumps-*.ndjson*", days: 14, maxBytes: 150, nowUtc: Now);

        // a and b go to meet the cap; c is inside the active grace window and stays even though total still exceeds it.
        Assert.Equal(new[] { "memdumps-c.ndjson" }, Remaining());
    }

    [Fact]
    public void Ignores_non_matching_files_and_missing_directory()
    {
        Make("games-keep.ndjson", 10, TimeSpan.FromDays(90));

        DiagnosticRetention.Apply(dir, "memdumps-*.ndjson*", days: 1, maxBytes: 1, nowUtc: Now);

        Assert.Equal(new[] { "games-keep.ndjson" }, Remaining());
        Assert.Equal(0, DiagnosticRetention.Apply(Path.Combine(dir, "absent"), "*", 1, 1, Now));
    }

    [Fact]
    public void Memory_snapshots_are_off_by_default()
    {
        var cfg = new Configuration();
        Assert.False(cfg.MemoryDumpsEnabled);
        Assert.Equal(14, cfg.DiagnosticRetentionDays);
        Assert.Equal(256L << 20, cfg.DiagnosticMaxBytes);
    }
}

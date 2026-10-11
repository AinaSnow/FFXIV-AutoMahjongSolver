using System;
using System.IO;
using System.Linq;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>
/// Age/size expiry for loose diagnostic files. Only direct-child files matching the
/// pattern are considered; recently written files and reparse points are never removed,
/// so a file still being appended survives.
/// </summary>
public static class DiagnosticRetention
{
    internal static readonly TimeSpan ActiveGrace = TimeSpan.FromMinutes(10);

    public static int Apply(string directory, string pattern, int days, long maxBytes, DateTime? nowUtc = null)
    {
        if (!Directory.Exists(directory))
            return 0;
        var now = nowUtc ?? DateTime.UtcNow;
        var cutoff = now.AddDays(-Math.Max(1, days));
        var files = new DirectoryInfo(directory).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            .Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderBy(f => f.LastWriteTimeUtc).ToArray();
        long total = files.Sum(f => f.Length);
        int removed = 0;
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc >= now - ActiveGrace)
                break;
            if (file.LastWriteTimeUtc >= cutoff && total <= Math.Max(1, maxBytes))
                continue;
            try
            {
                long size = file.Length;
                file.Delete();
                total -= size;
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Mahjong.Plugin.Dalamud.UI.DebugTabs;

/// <summary>Event logger, hook health, local archives, errors/findings tail, overlay debug.</summary>
internal sealed class DiagnosticsTab
{
    private const int TailLines = 5;

    private readonly DevConsoleContext ctx;

    public DiagnosticsTab(DevConsoleContext ctx) => this.ctx = ctx;

    public void Draw()
    {
        DrawEventLoggerCard();
        ImGui.Dummy(new Vector2(0, 4));
        DrawDiscardCaptureCard();
        ImGui.Dummy(new Vector2(0, 4));
        DrawLocalStorageCard();
        ImGui.Dummy(new Vector2(0, 4));
        DrawStreamsCard();
        ImGui.Dummy(new Vector2(0, 4));
        DrawHandOverlayDebugCard();
    }

    private void DrawEventLoggerCard()
    {
        using (Theme.BeginCard("diag-log"))
        {
            Theme.SectionHeader(Loc.T("Event logger", "事件记录"));
            Theme.Subtle($"FireCallback: {ctx.Plugin.EventLogger.CallbackCaptureStatus}");
            Theme.Subtle(Loc.T("Logs observed UI callbacks the mahjong addon receives. Useful for reverse-engineering button indices; heavy when on — turn off when done.", "记录麻将界面收到的 UI 回调，可用于逆向分析按钮序号。开启后开销较大，用完请关闭。"));
            bool enabled = ctx.Plugin.EventLogger.Enabled;
            if (ImGui.Checkbox(Loc.T("Record clicks to emj-events.log", "把点击记录到 emj-events.log"), ref enabled))
            {
                ctx.Plugin.EventLogger.Enabled = enabled;
                if (enabled)
                    ctx.Plugin.EventLogger.OpenLog();
                else
                    ctx.Plugin.EventLogger.CloseLog();
                ctx.LastToast = enabled ? "event logger ON" : "event logger OFF";
            }
            DevHelpers.CopyButton(ctx.Plugin.EventLogger.LogPath, "eventlog");
            ImGui.SameLine(0, 6);
            ImGui.AlignTextToFramePadding();
            Theme.Subtle(ctx.Plugin.EventLogger.LogPath);
        }
    }

    private void DrawDiscardCaptureCard()
    {
        using (Theme.BeginCard("diag-discardhook"))
        {
            Theme.SectionHeader(Loc.T("Discard capture", "打牌捕获"));
            Theme.Subtle(Loc.T("Tracks discards from addon snapshots. Native asm capture remains disabled until its signature is verified.", "通过界面快照跟踪打牌。原生汇编捕获在特征码验证之前保持禁用。"));
            var c = ctx.Plugin.DiscardCapture;
            DevHelpers.KeyValueRow(Loc.T("Strategy", "方式"), c.StrategyName);
            DevHelpers.KeyValueRow(Loc.T("Health", "健康状况"), c.Health.ToString());
            DevHelpers.KeyValueRow(Loc.T("Total captured", "累计捕获"), c.TotalCaptured.ToString());
            DevHelpers.KeyValueRow(Loc.T("Last tile id", "最近的牌 ID"), c.LastTileId.ToString());
            DevHelpers.KeyValueRow(Loc.T("Log", "日志"), ctx.Plugin.DiscardCaptureLogger.LogPath);
            DevHelpers.CopyButton(ctx.Plugin.DiscardCaptureLogger.LogPath, "discardlog");
        }
    }

    private void DrawLocalStorageCard()
    {
        using (Theme.BeginCard("diag-local-storage"))
        {
            Theme.SectionHeader(Loc.T("Local match archives", "本地对局归档"));
            Theme.Subtle(Loc.T("All gameplay, packet, error, finding, and memory-dump data stays on this computer. Nothing is uploaded.", "所有对局、数据包、错误、发现记录和内存转储数据都只保存在本机，不会上传任何内容。"));
            string path = ctx.Plugin.MatchArchive.RootDir;
            int count = Directory.Exists(path)
                ? Directory.GetDirectories(path, "match-*").Length
                : 0;
            DevHelpers.KeyValueRow(Loc.T("Completed matches", "已完成对局"), count.ToString());
            DevHelpers.KeyValueRow(Loc.T("Folder", "文件夹"), path);
            DevHelpers.OpenFolderButton(path, "match-archives", $"Open local match archives:\n{path}");
        }
    }

    private void DrawStreamsCard()
    {
        using (Theme.BeginCard("diag-streams"))
        {
            Theme.SectionHeader(Loc.T("Errors & findings (tail)", "错误与发现记录（末尾）"));
            Theme.Subtle(Loc.T($"Last {TailLines} lines of today's errors and findings NDJSON. Click Open to inspect the full file.", $"今天错误和发现记录 NDJSON 的最后 {TailLines} 行。点“打开”查看完整文件。"));

            string configDir = Plugin.PluginInterface.GetPluginConfigDirectory();
            string errorsPath = Path.Combine(configDir, "errors", $"errors-{DateTime.UtcNow:yyyyMMdd}.ndjson");
            string findingsPath = Path.Combine(configDir, "findings", $"findings-{DateTime.UtcNow:yyyyMMdd}.ndjson");

            DrawTail("Errors", errorsPath);
            ImGui.Dummy(new Vector2(0, 4));
            DrawTail("Findings", findingsPath);
        }
    }

    private void DrawHandOverlayDebugCard()
    {
        using (Theme.BeginCard("diag-handoverlay"))
        {
            Theme.SectionHeader(Loc.T("Hand overlay debug", "手牌高亮调试"));
            Theme.Subtle(Loc.T("Outlines every visible node whose dimensions match a tile, in NodeList order — before the row-finder picks the closed-hand subset. Use to spot tiles the size filter missed or extras the Y-cluster heuristic drops.", "按 NodeList 顺序框出所有尺寸与牌相符的可见节点（在挑选门前手牌之前）。可以用来发现尺寸筛选漏掉的牌，或 Y 坐标聚类丢掉的多余节点。"));
            bool on = ctx.Plugin.HandOverlay.DebugDrawAllRects;
            if (ImGui.Checkbox(Loc.T("Outline all detected tile rects", "框出所有检测到的牌区域"), ref on))
                ctx.Plugin.HandOverlay.DebugDrawAllRects = on;
        }
    }

    private static void DrawTail(string label, string path)
    {
        bool exists = File.Exists(path);
        if (exists)
        {
            string folder = Path.GetDirectoryName(path) ?? path;
            DevHelpers.OpenFolderButton(folder, $"tail-{label}", $"Open folder:\n{folder}");
            ImGui.SameLine(0, 3);
            DevHelpers.CopyButton(path, $"tail-{label}", $"Copy path to clipboard:\n{path}");
            ImGui.SameLine(0, 8);
            ImGui.AlignTextToFramePadding();
        }
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Header);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Faint);
        ImGui.TextUnformatted(exists ? $"({Path.GetFileName(path)})" : "(no file yet)");
        ImGui.PopStyleColor();

        var tail = ReadTail(path, TailLines);
        if (tail.Count == 0)
        {
            Theme.Subtle(Loc.T("  (empty)", "  （空）"));
            return;
        }
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
        foreach (var line in tail)
        {
            string truncated = line.Length > 200 ? line[..200] + "…" : line;
            ImGui.TextWrapped("  " + truncated);
        }
        ImGui.PopStyleColor();
    }

    /// <summary>Read the last <paramref name="n"/> lines of an NDJSON file; tolerant of missing files and other readers.</summary>
    private static List<string> ReadTail(string path, int n)
    {
        var lines = new List<string>(n);
        try
        {
            if (!File.Exists(path))
                return lines;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var ring = new Queue<string>(n);
            while (sr.ReadLine() is { } line)
            {
                if (ring.Count == n)
                    ring.Dequeue();
                ring.Enqueue(line);
            }
            lines.AddRange(ring);
        }
        catch { }
        return lines;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Mahjong.Plugin.Dalamud.UI.DebugTabs;

/// <summary>Snap/autosnap/capture/variant-dump — the bug-report capture surface.</summary>
internal sealed class BugReportTab
{
    private readonly DevConsoleContext ctx;
    private string snapLabel = "report";
    private string captureLabel = "click";

    public BugReportTab(DevConsoleContext ctx) => this.ctx = ctx;

    public void Draw()
    {
        var cmd = ctx.Plugin.MjAutoCommand;
        string configDir = Plugin.PluginInterface.GetPluginConfigDirectory();

        using (Theme.BeginCard("br-snap"))
        {
            Theme.SectionHeader(Loc.T("Snap (one-shot memory dump)", "快照（单次内存转储）"));
            Theme.Subtle(Loc.T("Saves the addon + agent memory to a labeled file. Use this when an issue asks for a one-off snapshot. Label = letters, digits, dash, underscore.", "把界面和 agent 内存保存到带标签的文件。问题需要一次性快照时使用。标签只能包含字母、数字、短横线和下划线。"));

            if (!IsValidLabel(snapLabel))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
                ImGui.TextUnformatted(Loc.T("Label must be [a-zA-Z0-9_-] only.", "标签只能使用 [a-zA-Z0-9_-]。"));
                ImGui.PopStyleColor();
            }

            ImGui.SetNextItemWidth(220);
            ImGui.InputText("##snaplbl", ref snapLabel, 64);
            ImGui.SameLine(0, 8);
            using (DevHelpers.Disable(!IsValidLabel(snapLabel)))
            {
                if (ImGui.Button(Loc.T("Snap", "快照")))
                {
                    var label = snapLabel;
                    cmd.HandleSnap(label);
                    ctx.LastToast = $"snap '{label}' queued → see chat for path";
                }
            }
            Theme.Subtle(Loc.T($"Writes snap-LABEL-ts.txt to plugin config dir.", $"把 snap-标签-时间.txt 写到插件配置目录。"));
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("br-autosnap"))
        {
            Theme.SectionHeader(Loc.T("Autosnap (continuous capture)", "自动快照（持续采集）"));
            Theme.Subtle(Loc.T("Saves a fresh dump every time the game state changes. Use when you can't predict when the bug will trigger. Caps at 500 files; identical states are dropped.", "游戏状态每次变化都保存一份新的转储，适合无法预测问题何时出现的情况。最多 500 个文件，相同的状态会被丢弃。"));
            bool on = cmd.IsAutoSnapOn;

            DevHelpers.KeyValueRow(Loc.T("State", "状态"), on ? "ON" : "OFF");
            DevHelpers.KeyValueRow(Loc.T("Count", "数量"), $"{cmd.AutoSnapCount} / {cmd.AutoSnapMaxCountValue}");

            ImGui.Dummy(new Vector2(0, 2));
            if (on)
            {
                if (ImGui.Button(Loc.T("Stop autosnap", "停止自动快照")))
                {
                    cmd.HandleAutoSnap("off");
                    ctx.LastToast = "autosnap OFF";
                }
            }
            else
            {
                if (ImGui.Button(Loc.T("Start autosnap", "开始自动快照")))
                {
                    cmd.HandleAutoSnap("on");
                    ctx.LastToast = "autosnap ON — files: snap-auto-NNN-<ts>.txt";
                }
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("br-capture"))
        {
            Theme.SectionHeader(Loc.T("Capture (arm-and-click)", "点击采集（预备后点击）"));
            Theme.Subtle(Loc.T("Records the very next click you make in the mahjong UI with a label. Type a name, press Arm, then click in-game. Auto-disarms after one click or 60s.", "带标签记录你在麻将界面里的下一次点击。输入名称，按“预备”，然后在游戏里点击。点击一次或 60 秒后自动取消。"));
            if (!ctx.Plugin.EventLogger.CallbackCaptureAvailable)
                Theme.Subtle(Loc.T($"Unavailable: {ctx.Plugin.EventLogger.CallbackCaptureStatus}", $"不可用：{ctx.Plugin.EventLogger.CallbackCaptureStatus}"));
            var pending = ctx.Plugin.EventLogger.PendingCaptureLabel;

            if (pending is not null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
                ImGui.TextUnformatted(Loc.T($"ARMED: '{pending}' — click the action in-game", $"已预备：'{pending}'，请在游戏里点击对应操作"));
                ImGui.PopStyleColor();
                ImGui.Dummy(new Vector2(0, 2));
                if (ImGui.Button(Loc.T("Disarm", "取消预备")))
                {
                    ctx.Plugin.EventLogger.DisarmCapture();
                    ctx.LastToast = $"capture disarmed (was: {pending})";
                }
            }
            else
            {
                ImGui.SetNextItemWidth(220);
                ImGui.InputText("##caplbl", ref captureLabel, 64);
                ImGui.SameLine(0, 8);
                bool armBlocked = !ctx.Plugin.EventLogger.CallbackCaptureAvailable || !IsValidLabel(captureLabel) || ctx.Plugin.Configuration.AutomationArmed;
                using (DevHelpers.Disable(armBlocked))
                {
                    if (ImGui.Button(Loc.T("Arm", "预备")))
                    {
                        cmd.HandleCapture(captureLabel);
                        ctx.LastToast = $"capture armed: '{captureLabel}' — auto-disarms after one click or 60s";
                    }
                }
                if (ctx.Plugin.Configuration.AutomationArmed)
                    Theme.Subtle(Loc.T("Auto-play is ON — disarm automation first or its dispatch will race your click.", "自动打牌已开启。请先停用自动化，否则它的点击会和你的点击冲突。"));
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("br-variant"))
        {
            Theme.SectionHeader(Loc.T("Variant dump (new-client report)", "变体转储（新客户端报告）"));
            Theme.Subtle(Loc.T("Run this when the plugin can't read your hand or board after a game patch. Writes one file to attach when reporting a new client variant.", "游戏更新后插件读不出手牌或牌桌时运行。会生成一个文件，报告新客户端变体时附上它。"));
            if (ImGui.Button(Loc.T("Dump variant", "转储变体")))
            {
                cmd.DumpVariant();
                ctx.LastToast = "variant dump queued → emj-variant-dump.txt";
            }
            ImGui.SameLine(0, 6);
            DevHelpers.CopyPathButton("emj-variant-dump.txt", "variant");
            Theme.Subtle(Loc.T("Attach emj-variant-dump.txt when reporting a new client variant.", "报告新客户端变体时请附上 emj-variant-dump.txt。"));
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("br-folder"))
        {
            Theme.SectionHeader(Loc.T("Files", "文件"));
            Theme.Subtle(Loc.T("Open the folder containing every dump file. Drag-and-drop into a GitHub issue to attach.", "打开存放所有转储文件的文件夹。拖进 GitHub issue 即可附上。"));

            DrawDirRow(Loc.T("Plugin config", "插件配置"), configDir, "cfgdir");
            string findingsDir = Path.Combine(configDir, "findings");
            if (Directory.Exists(findingsDir))
                DrawDirRow(Loc.T("Findings", "发现记录"),      findingsDir, "findingsdir");
        }

        ImGui.Dummy(new Vector2(0, 4));
        DrawRecentDumpsCard(configDir);
    }

    /// <summary>Icon pair (open + copy) followed by an aligned "label: path" line for a directory.</summary>
    private static void DrawDirRow(string label, string path, string id)
    {
        DevHelpers.OpenFolderButton(path, id, $"Open folder:\n{path}");
        ImGui.SameLine(0, 3);
        DevHelpers.CopyButton(path, id, $"Copy path to clipboard:\n{path}");
        ImGui.SameLine(0, 8);
        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.TextUnformatted($"{label}:");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Faint);
        ImGui.TextWrapped(path);
        ImGui.PopStyleColor();
    }

    /// <summary>Last 10 generated dump files; click to reveal in explorer or copy path.</summary>
    private static void DrawRecentDumpsCard(string configDir)
    {
        using (Theme.BeginCard("br-recent"))
        {
            Theme.SectionHeader(Loc.T("Recent dumps", "最近的转储"));
            Theme.Subtle(Loc.T("Most recently produced dump files. Open reveals the file's folder; Copy puts the full path on your clipboard.", "最近生成的转储文件。“打开”会定位到文件所在文件夹，“复制”会把完整路径放进剪贴板。"));

            var files = RecentDumps(configDir, 10);
            if (files.Count == 0)
            {
                Theme.Subtle(Loc.T("(no dumps yet)", "（还没有转储）"));
                return;
            }

            for (int i = 0; i < files.Count; i++)
            {
                var (path, when) = files[i];
                string name = Path.GetFileName(path);
                string age = FormatAge(DateTime.UtcNow - when);
                string folder = Path.GetDirectoryName(path) ?? configDir;

                DevHelpers.OpenFolderButton(folder, $"rd{i}", $"Reveal in folder:\n{folder}");
                ImGui.SameLine(0, 3);
                DevHelpers.CopyButton(path, $"rd{i}", $"Copy path to clipboard:\n{path}");
                ImGui.SameLine(0, 8);

                ImGui.AlignTextToFramePadding();
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
                ImGui.TextUnformatted(name);
                ImGui.PopStyleColor();
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Faint);
                ImGui.TextUnformatted($"  {age} ago");
                ImGui.PopStyleColor();
            }
        }
    }

    private static List<(string Path, DateTime WrittenUtc)> RecentDumps(string configDir, int max)
    {
        var result = new List<(string, DateTime)>();
        try
        {
            if (!Directory.Exists(configDir))
                return result;
            string[] patterns = { "snap-*.txt", "emj-*.txt" };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pat in patterns)
            {
                foreach (var f in Directory.EnumerateFiles(configDir, pat, SearchOption.TopDirectoryOnly))
                {
                    if (!seen.Add(f))
                        continue;
                    result.Add((f, File.GetLastWriteTimeUtc(f)));
                }
            }
            result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            if (result.Count > max)
                result.RemoveRange(max, result.Count - max);
        }
        catch { }
        return result;
    }

    private static string FormatAge(TimeSpan d)
    {
        if (d.TotalSeconds < 60)
            return $"{(int)d.TotalSeconds}s";
        if (d.TotalMinutes < 60)
            return $"{(int)d.TotalMinutes}m";
        if (d.TotalHours < 24)
            return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }

    private static bool IsValidLabel(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        foreach (var c in s)
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        return true;
    }
}

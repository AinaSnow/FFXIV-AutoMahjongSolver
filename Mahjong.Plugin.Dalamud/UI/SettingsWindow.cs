using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Mahjong.Plugin.Dalamud.UI;

/// <summary>Settings, grouped into Auto-play / Appearance / Developer cards.</summary>
public sealed class SettingsWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    /// <summary>Which color the preview row renders — purely UI state, not persisted.</summary>
    private bool previewShowsTsumogiri;

    public SettingsWindow(Plugin plugin)
        : base("Doman Mahjong Solver — Settings###domanmahjong-settings")
    {
        ArgumentNullException.ThrowIfNull(plugin);
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(500, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(800, 1200),
        };
    }

    public void Dispose() { }

    private static string StyleLabel(HighlightStyle s) => s switch
    {
        HighlightStyle.Arrow => Loc.T("Big arrow + label", "大箭头 + 文字标签"),
        _ => Loc.T("Neon glow + corner brackets", "霓虹光晕 + 角框"),
    };

    private void DrawHighlightPreview(Configuration cfg)
    {
        // Radio above the preview to flip which color is being shown.
        bool showDiscard = !previewShowsTsumogiri;
        if (ImGui.RadioButton(Loc.T("Preview: Discard", "预览：手切") + "##prev-discard", showDiscard))
            previewShowsTsumogiri = false;
        ImGui.SameLine(0, 14);
        if (ImGui.RadioButton(Loc.T("Tsumogiri", "摸切") + "##prev-tsumogiri", !showDiscard))
            previewShowsTsumogiri = true;

        // Reserve a canvas wide enough for 5 fake tiles + headroom for the arrow/label.
        const int tileCount = 5;
        const float tileW = 34f;
        const float tileH = 50f;
        const float tileGap = 6f;
        const float topPad = 56f;   // room above for the arrow/pill
        const float botPad = 12f;

        float canvasW = ImGui.GetContentRegionAvail().X;
        float canvasH = topPad + tileH + botPad;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(canvasW, canvasH));

        var dl = ImGui.GetWindowDrawList();

        // Backing panel so the preview reads as its own surface.
        dl.AddRectFilled(origin, origin + new Vector2(canvasW, canvasH), Theme.Pack(Theme.SurfaceAlt), 6f);
        dl.AddRect(origin, origin + new Vector2(canvasW, canvasH), Theme.Pack(Theme.Border), 6f, ImDrawFlags.None, 1f);

        // Lay out 5 fake tiles centered horizontally.
        float rowW = tileCount * tileW + (tileCount - 1) * tileGap;
        float startX = origin.X + (canvasW - rowW) * 0.5f;
        float tileY = origin.Y + topPad;
        var rects = new List<(Vector2 Pos, Vector2 Size)>(tileCount);
        for (int i = 0; i < tileCount; i++)
        {
            var pos = new Vector2(startX + i * (tileW + tileGap), tileY);
            var size = new Vector2(tileW, tileH);
            rects.Add((pos, size));
            // Shadow + tile face + border, same recipe as Theme.DrawTile but without an item dummy.
            dl.AddRectFilled(pos + new Vector2(1, 2), pos + size + new Vector2(1, 2), Theme.Pack(Theme.TileShadow), 4f);
            dl.AddRectFilled(pos, pos + size, Theme.Pack(Theme.TileFace), 4f);
            dl.AddRect(pos, pos + size, Theme.Pack(Theme.TileBorder), 4f, ImDrawFlags.None, 1.5f);
        }

        int pickSlot = tileCount / 2;
        bool isDrawnTile = previewShowsTsumogiri;
        var color = (isDrawnTile ? cfg.HighlightColorTsumogiri : cfg.HighlightColorDiscard).ToVector3();
        float intensity = Math.Clamp(cfg.HighlightIntensity, 0.4f, 1.6f);

        // Clip to the preview panel so the arrow/glow can't escape it.
        dl.PushClipRect(origin, origin + new Vector2(canvasW, canvasH), true);
        switch (cfg.HighlightStyle)
        {
            case HighlightStyle.Arrow:
                HandOverlay.DrawHighlightArrow(dl, rects[pickSlot], color, intensity, isDrawnTile);
                break;
            default:
                HandOverlay.DrawHighlightNeonGlow(dl, rects[pickSlot], color, intensity);
                break;
        }
        dl.PopClipRect();
    }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        WindowName = Loc.T("Doman Mahjong Solver — Settings", "多玛麻将助手 — 设置") + "###domanmahjong-settings";
        using var _s = Theme.PushWindowStyle();

        using (Theme.BeginCard("settings-play"))
        {
            Theme.SectionHeader(Loc.T("Auto-play", "自动打牌"));

            int delay = cfg.HumanizedDelayMs;
            ImGui.SetNextItemWidth(300);
            if (ImGui.SliderInt(Loc.T("Click speed", "点击速度") + "##click-speed", ref delay, 400, 3000, "%d ms"))
                plugin.ConfigService.Update(c => c with { HumanizedDelayMs = delay });
            Theme.Subtle(Loc.T("Average delay before each auto-play click.", "每次自动点击前的平均延迟。"));

            ImGui.Dummy(new Vector2(0, 4));

            bool autoAdvance = cfg.AutoAdvanceAfterHand;
            if (ImGui.Checkbox(Loc.T("Auto-advance after each hand", "每局结束后自动继续") + "##auto-advance", ref autoAdvance))
                plugin.ConfigService.Update(c => c with { AutoAdvanceAfterHand = autoAdvance });
            Theme.Subtle(Loc.T("Click \"Next\" on the win/draw results screen so the next hand starts automatically.", "在和牌/流局结算画面自动点击“下一步”，直接开始下一局。"));
        }

        ImGui.Dummy(new Vector2(0, 4));

        using (Theme.BeginCard("settings-strategy"))
        {
            Theme.SectionHeader(Loc.T("Experimental strategy", "实验性策略"));
            bool enhanced = cfg.EnhancedStrategy;
            if (ImGui.Checkbox(Loc.T("Enable two-draw search", "启用两巡搜索") + "##two-draw", ref enhanced))
                plugin.ConfigService.Update(c => c with { EnhancedStrategy = enhanced });
            bool shadow = cfg.EnhancedShadowOnly;
            if (ImGui.Checkbox(Loc.T("Shadow comparison only", "仅影子对比（不替换动作）") + "##shadow", ref shadow))
                plugin.ConfigService.Update(c => c with { EnhancedShadowOnly = shadow });
            Theme.Subtle(Loc.T("Experimental; no verified ranking improvement yet. Stable remains the default.", "实验功能，尚无已验证的名次提升。默认仍使用稳定策略。"));
            int days = cfg.ArchiveRetentionDays;
            if (ImGui.InputInt(Loc.T("Archive retention (days)", "对局归档保留天数") + "##archive-days", ref days))
                plugin.ConfigService.Update(c => c with { ArchiveRetentionDays = Math.Clamp(days, 1, 3650) });
            int mib = (int)(cfg.ArchiveMaxBytes / (1L << 20));
            if (ImGui.InputInt(Loc.T("Archive limit (MiB)", "对局归档容量上限 (MiB)") + "##archive-mib", ref mib))
                plugin.ConfigService.Update(c => c with { ArchiveMaxBytes = (long)Math.Clamp(mib, 1, 1048576) << 20 });
        }

        using (Theme.BeginCard("settings-training-data"))
        {
            Theme.SectionHeader(Loc.T("Training data archive", "训练数据存档"));
            bool retain = cfg.RetainTrainingData;
            if (ImGui.Checkbox(Loc.T("Keep completed matches for future training", "保留已完成的对局供以后训练") + "##retain", ref retain))
                plugin.ConfigService.Update(c => c with { RetainTrainingData = retain });
            ImGui.TextWrapped(Loc.T("Keeps a separate local copy of game logs and sealed raw packets. No automatic expiry, upload or training. Stop collection if storage fails.", "单独在本地保存一份对局日志和封存的原始数据包。不会自动过期、上传或训练。存储失败时会停止采集。"));
            Theme.Subtle(Loc.Status(plugin.TrainingCorpus.Status));
            if (ImGui.SmallButton(Loc.T("Copy training data folder", "复制训练数据文件夹路径") + "##copy-training")) ImGui.SetClipboardText(plugin.TrainingCorpus.RootDir);
        }

        using (Theme.BeginCard("settings-mortal"))
        {
            Theme.SectionHeader(Loc.T("Mortal AI", "Mortal AI"));

            bool enabled = cfg.MortalEnabled;
            if (ImGui.Checkbox(Loc.T("Use Mortal for live decisions", "实战中使用 Mortal 决策") + "##mortal", ref enabled))
                plugin.ConfigService.Update(c => c with { MortalEnabled = enabled });

            bool eastProgress = cfg.MortalEastProgress;
            if (ImGui.Checkbox(Loc.T("East-only match mode (East 4 = last hand)", "东风战模式（东 4 局为终局）") + "##east-progress", ref eastProgress))
                plugin.ConfigService.Update(c => c with { MortalEastProgress = eastProgress });
            Theme.Subtle(Loc.T("Turn on when queuing East-only matches, off for full matches. Offline acceptance: average rank -0.032, 4th place -2.1 pts vs the unmodified model.", "排东风战时打开，排半庄时关闭。离线验收：相比原版模型，平均名次 -0.032，四位率 -2.1 个百分点。"));

            bool trial = cfg.MortalLimitedTrial;
            if (ImGui.Checkbox(Loc.T("Mortal limited trial (Emj)", "Mortal 限定试用 (Emj)") + "##trial", ref trial))
                plugin.ConfigService.Update(c => c with { MortalLimitedTrial = trial });
            ImGui.TextWrapped(Loc.T("Optional trial for observed East-round hands with no carried riichi deposits. Kans or incomplete state switch the rest of that hand to the local strategy. Next hand is checked again. Match-end ranking differences remain experimental.", "可选试用，适用于已观测、且没有遗留立直棒的东场手局。出现杠或局面不完整时，该手剩余部分改用本地策略，下一手重新检查。终局名次方面的差异仍属实验性质。"));

            if (enabled)
                ImGui.BeginDisabled();

            string distribution = cfg.MortalWslDistribution;
            ImGui.SetNextItemWidth(300);
            if (ImGui.InputText(Loc.T("WSL distribution", "WSL 发行版") + "##wsl", ref distribution, 128))
                plugin.ConfigService.Update(c => c with { MortalWslDistribution = distribution });

            string directory = cfg.MortalWorkingDirectory;
            ImGui.SetNextItemWidth(420);
            if (ImGui.InputText(Loc.T("Mortal directory", "Mortal 目录") + "##mortal-dir", ref directory, 512))
                plugin.ConfigService.Update(c => c with { MortalWorkingDirectory = directory });

            string python = cfg.MortalPythonExecutable;
            ImGui.SetNextItemWidth(180);
            if (ImGui.InputText(Loc.T("Python executable", "Python 可执行文件") + "##python", ref python, 128))
                plugin.ConfigService.Update(c => c with { MortalPythonExecutable = python });

            if (enabled)
                ImGui.EndDisabled();

            Theme.Subtle($"{Loc.T("Status", "状态")}: {Loc.Status(plugin.MortalBridge.Status)}");
            Theme.Subtle($"{Loc.T("Network", "网络")}: {Loc.Status(plugin.NetworkCapture.ProtocolStatus)}");
            if (plugin.NetworkCapture.LearningWanted || plugin.NetworkCapture.LearningStatus != "Not needed")
                Theme.Subtle($"{Loc.T("Protocol learning", "协议学习")}: {Loc.Status(plugin.NetworkCapture.LearningStatus)}");
            if (!plugin.NetworkCapture.ProtocolAdmitted)
                ImGui.TextWrapped(Loc.T("Network inference is unavailable for this version/variant or the limited trial is off. Existing recordings can be checked offline; additional random matches are not required.", "当前客户端版本/变体无法使用网络推理，或限定试用未开启。已有录包可以离线检查，无需再随机排局。"));
            else if (plugin.NetworkCapture.LimitedTrialActive)
                ImGui.TextWrapped(Loc.T("Limited trial is active. This is not full protocol verification; unsupported hands use the local strategy.", "限定试用已启用。这不是完整的协议验证，不支持的手局会改用本地策略。"));
            Theme.Subtle($"{Loc.T("Packets", "数据包")} {plugin.MortalBridge.PacketsProcessed}  ·  MJAI {plugin.MortalBridge.EventsSent}  ·  {Loc.T("Decisions", "决策")} {plugin.MortalBridge.ReactionsReceived}");
            Theme.Subtle($"{Loc.T("Mapped", "已映射")} {plugin.MortalBridge.DecisionsMapped}  ·  {Loc.T("Timeouts", "超时")} {plugin.MortalBridge.DecisionTimeouts}  ·  {Loc.T("Corrected", "已修正")} {plugin.MortalBridge.CandidateCorrections}  ·  {Loc.T("Recovered", "已恢复")} {plugin.MortalBridge.RecoveredDiscardEvents}  ·  {Loc.T("Model", "模型")} {plugin.MortalBridge.LastModelEvalMilliseconds:0.0} ms");
        }

        ImGui.Dummy(new Vector2(0, 4));

        using (Theme.BeginCard("settings-packet-debug"))
        {
            Theme.SectionHeader(Loc.T("Debug packet logger", "调试录包"));
            bool capture = cfg.DebugAutoPacketLogging;
            if (ImGui.Checkbox(Loc.T("Automatically record packets at mahjong tables", "在麻将桌自动录制数据包") + "##packet-capture", ref capture))
                plugin.ConfigService.Update(c => c with { DebugAutoPacketLogging = capture });
            ImGui.TextWrapped(Loc.T("Saves local received packets through bundled Deucalion, with a 2-second pre-roll. May contain player information. No upload. Decision input follows the separately selected verified or limited-trial protocol scope.", "通过内置的 Deucalion 在本地保存收到的数据包，带 2 秒预缓冲。可能包含玩家信息，不会上传。决策输入仍按另行选择的已验证或限定试用协议范围处理。"));
            Theme.Subtle($"{Loc.T("Status", "状态")}: {Loc.Status(plugin.DebugPackets.Status)}");
            Theme.Subtle($"{Loc.T("Saved", "已保存")} {plugin.DebugPackets.Packets}  |  {Loc.T("Queue drops", "队列丢弃")} {plugin.DebugPackets.Dropped}  |  {Loc.T("Read rejects", "读取拒绝")} {plugin.DebugPackets.Rejected}");
            Theme.Subtle(Loc.T("64 MiB per capture; automatically stops on leaving the table. Files remain until you remove them.", "每份录包上限 64 MiB，离桌自动停止。文件会一直保留，直到你手动删除。"));
            if (ImGui.SmallButton(Loc.T("Copy packet log folder", "复制录包文件夹路径") + "##copy-packets")) ImGui.SetClipboardText(plugin.DebugPackets.DirectoryPath);
            if (plugin.DebugPackets.CurrentPath is {} capturePath) ImGui.TextWrapped(capturePath);
        }

        using (Theme.BeginCard("settings-memdumps"))
        {
            Theme.SectionHeader(Loc.T("Memory snapshots", "内存快照"));
            bool dumps = cfg.MemoryDumpsEnabled;
            if (ImGui.Checkbox(Loc.T("Record mahjong window memory snapshots", "记录麻将窗口内存快照") + "##memdumps", ref dumps))
                plugin.ConfigService.Update(c => c with { MemoryDumpsEnabled = dumps });
            ImGui.TextWrapped(Loc.T("Only needed to re-locate memory offsets after a game patch breaks hand reading. Writes about 1 MiB every few seconds while seated.", "仅在游戏更新导致读不出手牌、需要重新定位内存偏移时使用。在桌期间每几秒写入约 1 MiB。"));
            int days = cfg.DiagnosticRetentionDays;
            if (ImGui.InputInt(Loc.T("Diagnostics retention (days)", "诊断文件保留天数") + "##diag-days", ref days))
                plugin.ConfigService.Update(c => c with { DiagnosticRetentionDays = Math.Clamp(days, 1, 3650) });
            int mib = (int)(cfg.DiagnosticMaxBytes / (1L << 20));
            if (ImGui.InputInt(Loc.T("Diagnostics limit per folder (MiB)", "每个诊断目录容量上限 (MiB)") + "##diag-mib", ref mib))
                plugin.ConfigService.Update(c => c with { DiagnosticMaxBytes = (long)Math.Clamp(mib, 16, 1048576) << 20 });
            Theme.Subtle(Loc.T("Applies to memdumps, inputs and discards. Game logs, packet captures, match archives and training data are not affected.", "作用于 memdumps、inputs、discards 三个目录。对局日志、录包、对局归档和训练数据不受影响。"));
            if (ImGui.SmallButton(Loc.T("Copy memory snapshot folder", "复制内存快照文件夹路径") + "##copy-memdumps")) ImGui.SetClipboardText(plugin.MemoryDumpRecorder.MemdumpsDir);
        }

        using (Theme.BeginCard("settings-appearance"))
        {
            Theme.SectionHeader(Loc.T("Appearance", "外观"));

            bool highlight = cfg.ShowInGameHighlight;
            if (ImGui.Checkbox(Loc.T("Highlight suggested tile in the mahjong window", "在麻将窗口中高亮推荐打出的牌") + "##highlight", ref highlight))
                plugin.ConfigService.Update(c => c with { ShowInGameHighlight = highlight });
            Theme.Subtle(Loc.T("A pulsing outline on the discard to make. Shown in Hints mode.", "在推荐打出的牌上显示闪烁轮廓，仅在提示模式下显示。"));

            ImGui.Dummy(new Vector2(0, 4));

            if (!highlight)
                ImGui.BeginDisabled();

            var style = cfg.HighlightStyle;
            ImGui.SetNextItemWidth(300);
            if (ImGui.BeginCombo(Loc.T("Highlight style", "高亮样式") + "##highlight-style", StyleLabel(style)))
            {
                foreach (var opt in new[] { HighlightStyle.NeonGlow, HighlightStyle.Arrow })
                {
                    bool selected = opt == style;
                    if (ImGui.Selectable(StyleLabel(opt), selected) && opt != style)
                        plugin.ConfigService.Update(c => c with { HighlightStyle = opt });
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            Theme.Subtle(style switch
            {
                HighlightStyle.Arrow => Loc.T("Large arrow with a DISCARD / TSUMOGIRI label. Keeps the tile art uncovered.", "大箭头加“手切 / 摸切”标签，不遮挡牌面。"),
                _ => Loc.T("Neon glow halo, L-shaped corner brackets, and a bouncing arrow above the tile.", "霓虹光晕、L 形角框，牌上方有跳动的箭头。"),
            });

            ImGui.Dummy(new Vector2(0, 6));

            DrawHighlightPreview(cfg);

            ImGui.Dummy(new Vector2(0, 6));

            // Discard color.
            var discardVec = cfg.HighlightColorDiscard.ToVector3();
            if (ImGui.ColorEdit3(Loc.T("Discard color", "手切颜色") + "##discard-color", ref discardVec,
                    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                plugin.ConfigService.Update(c => c with { HighlightColorDiscard = RgbColor.From(discardVec) });

            // Tsumogiri color.
            var tsuVec = cfg.HighlightColorTsumogiri.ToVector3();
            if (ImGui.ColorEdit3(Loc.T("Tsumogiri color", "摸切颜色") + "##tsumogiri-color", ref tsuVec,
                    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                plugin.ConfigService.Update(c => c with { HighlightColorTsumogiri = RgbColor.From(tsuVec) });

            // Intensity slider.
            float intensity = cfg.HighlightIntensity;
            ImGui.SetNextItemWidth(300);
            if (ImGui.SliderFloat(Loc.T("Intensity", "强度") + "##intensity", ref intensity, 0.4f, 1.6f, "%.2fx"))
                plugin.ConfigService.Update(c => c with { HighlightIntensity = intensity });

            // Reset button.
            if (ImGui.SmallButton(Loc.T("Reset overlay colors & intensity", "重置高亮颜色和强度") + "##reset-overlay"))
                plugin.ConfigService.Update(c => c with
                {
                    HighlightColorDiscard = RgbColor.Defaults.Discard,
                    HighlightColorTsumogiri = RgbColor.Defaults.Tsumogiri,
                    HighlightIntensity = 1.0f,
                });

            if (!highlight)
                ImGui.EndDisabled();

            ImGui.Dummy(new Vector2(0, 4));

            bool details = cfg.ShowSuggestionDetails;
            if (ImGui.Checkbox(Loc.T("Show analysis details under best move", "在最佳打法下方显示分析详情") + "##details", ref details))
                plugin.ConfigService.Update(c => c with { ShowSuggestionDetails = details });
            Theme.Subtle(Loc.T("Adds a shanten / ukeire breakdown of the top discards in the main window.", "在主窗口中显示候选打牌的向听数 / 进张数明细。"));
        }

        ImGui.Dummy(new Vector2(0, 4));

        using (Theme.BeginCard("settings-dev"))
        {
            Theme.SectionHeader(Loc.T("Developer", "开发者"));

            bool dev = cfg.DevMode;
            if (ImGui.Checkbox(Loc.T("Enable developer tools", "启用开发者工具") + "##devmode", ref dev))
            {
                plugin.ConfigService.Update(c => c with { DevMode = dev });
                if (dev)
                    plugin.DebugOverlay.IsOpen = true;
            }
            Theme.Subtle(Loc.T("Adds a debug button to the main window toolbar.", "在主窗口工具栏添加调试按钮。"));
        }
    }
}

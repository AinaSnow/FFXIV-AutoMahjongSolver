using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Policy;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.UI;

/// <summary>End-user window: toolbar, status, mode, live game. Settings and debug live in their own windows.</summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("Doman Mahjong Solver###domanmahjong-main")
    {
        this.plugin = plugin;
        Size = new Vector2(520, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 420),
            MaximumSize = new Vector2(900, 2000),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        WindowName = Loc.T("Doman Mahjong Solver", "多玛麻将助手") + "###domanmahjong-main";

        using var _s = Theme.PushWindowStyle();

        if (!cfg.TosAccepted)
        {
            DrawTosGate(cfg);
            return;
        }

        DrawModeCard(cfg);
        ImGui.Dummy(new Vector2(0, 4));
        DrawCollectionCard(cfg);
        ImGui.Dummy(new Vector2(0, 4));
        DrawLiveCard();

        DrawAutoPlayConfirmModal(cfg);
    }

    private void DrawTosGate(Configuration cfg)
    {
        using (Theme.BeginCard("tos"))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Header);
            ImGui.TextUnformatted(Loc.T("Welcome to Doman Mahjong Solver", "欢迎使用多玛麻将助手"));
            ImGui.PopStyleColor();
            ImGui.Dummy(new Vector2(0, 6));

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
            ImGui.TextWrapped(Loc.T(
                "This plugin can play Doman Mahjong for you by reading the game and clicking " +
                "on your behalf. Before turning that on, please read:",
                "本插件可以读取游戏画面并代你点击，替你打多玛麻将。开启之前请先阅读："));
            ImGui.PopStyleColor();
            ImGui.Dummy(new Vector2(0, 4));

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
            ImGui.BulletText(Loc.T("Third-party automation is against the FFXIV Terms of Service.", "第三方自动化违反 FF14 的服务条款。"));
            ImGui.BulletText(Loc.T("Use at your own risk — your account may be sanctioned.", "使用风险自负，账号可能会受到处罚。"));
            ImGui.PopStyleColor();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.BulletText(Loc.T("\"Hints\" mode only shows advice — never clicks for you.", "“提示”模式只显示建议，不会替你点击。"));
            ImGui.TextWrapped(Loc.T(
                "  • Gameplay logs, Mahjong packets, errors, and memory diagnostics " +
                "stay on this computer. Completed matches are grouped into local " +
                "archives for later review; this build does not upload them.",
                "  • 对局日志、麻将数据包、错误和内存诊断都只保存在本机。已完成的对局会整理成本地归档，方便以后回顾；本版本不会上传。"));
            ImGui.PopStyleColor();

            ImGui.Dummy(new Vector2(0, 10));

            ImGui.PushStyleColor(ImGuiCol.Button, Theme.Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(Theme.Accent.X * 1.15f, Theme.Accent.Y * 1.15f, Theme.Accent.Z * 1.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(Theme.Accent.X * 0.85f, Theme.Accent.Y * 0.85f, Theme.Accent.Z * 0.85f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.05f, 0.10f, 0.08f, 1f));
            float btnW = ImGui.GetContentRegionAvail().X;
            if (ImGui.Button(Loc.T("I understand — continue", "我已了解，继续") + "##tos-accept", new Vector2(btnW, 36)))
                plugin.ConfigService.Update(c => c with { TosAccepted = true });
            ImGui.PopStyleColor(4);
        }
    }

    private void DrawModeCard(Configuration cfg)
    {
        using (Theme.BeginCard("mode"))
        {
            // Header row: "Mode" label + right-aligned icon actions.
            ImGui.AlignTextToFramePadding();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Header);
            ImGui.TextUnformatted(Loc.T("Mode", "模式"));
            ImGui.PopStyleColor();

            DrawHeaderIcons(cfg);

            var dl = ImGui.GetWindowDrawList();
            var p = ImGui.GetCursorScreenPos();
            float rw = ImGui.GetContentRegionAvail().X;
            dl.AddLine(p + new Vector2(0, 2), new Vector2(p.X + rw, p.Y + 2), Theme.Pack(Theme.Divider), 1f);
            ImGui.Dummy(new Vector2(0, 6));

            int current = !cfg.AutomationArmed ? 0 : (cfg.SuggestionOnly ? 1 : 2);
            float avail = ImGui.GetContentRegionAvail().X;
            float gap = 6f;
            float w = (avail - gap * 2) / 3f;
            var size = new Vector2(w, 50);

            if (ModePill("off", Loc.T("Off", "关闭"), Loc.T("Do nothing", "不做任何事"), Theme.Muted, current == 0, size))
                RequestMode(0, cfg);
            ImGui.SameLine(0, gap);
            if (ModePill("hints", Loc.T("Hints", "提示"), Loc.T("Highlight best move", "高亮最佳打法"), Theme.Warn, current == 1, size))
                RequestMode(1, cfg);
            ImGui.SameLine(0, gap);
            if (ModePill("auto", Loc.T("Auto-play", "自动打牌"), Loc.T("Click for you", "替你点击"), Theme.Accent, current == 2, size))
                RequestMode(2, cfg);
        }
    }

    private void DrawHeaderIcons(Configuration cfg)
    {
        var bugLabel = FontAwesomeIcon.Bug.ToIconString();
        var infoLabel = FontAwesomeIcon.InfoCircle.ToIconString();
        var gearLabel = FontAwesomeIcon.Cog.ToIconString();

        bool bugClicked = false, infoClicked, gearClicked;
        // Tooltips render outside the icon-font scope — they would use icon glyphs otherwise.
        int hovered = -1;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var framePadX = ImGui.GetStyle().FramePadding.X;
            var spacingX = ImGui.GetStyle().ItemSpacing.X;
            var btnW = ImGui.CalcTextSize(gearLabel).X + framePadX * 2;
            int slots = cfg.DevMode ? 3 : 2;
            float totalW = btnW * slots + spacingX * (slots - 1);
            Theme.RightAlign(totalW, Theme.CardPadX);

            if (cfg.DevMode)
            {
                bugClicked = ImGui.Button(bugLabel + "##debug");
                if (ImGui.IsItemHovered()) hovered = 0;
                ImGui.SameLine();
            }
            infoClicked = ImGui.Button(infoLabel + "##about");
            if (ImGui.IsItemHovered()) hovered = 1;
            ImGui.SameLine();
            gearClicked = ImGui.Button(gearLabel + "##settings");
            if (ImGui.IsItemHovered()) hovered = 2;
        }

        switch (hovered)
        {
            case 0: ImGui.SetTooltip(Loc.T("Developer console", "开发者控制台")); break;
            case 1: ImGui.SetTooltip(Loc.T("About", "关于")); break;
            case 2: ImGui.SetTooltip(Loc.T("Settings", "设置")); break;
        }

        if (bugClicked) plugin.ToggleDebugOverlay();
        if (infoClicked) plugin.ToggleAboutWindow();
        if (gearClicked) plugin.ToggleSettingsWindow();
    }

    private static bool ModePill(string id, string title, string sub, Vector4 tint, bool selected, Vector2 size)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;

        bool clicked = ImGui.InvisibleButton($"##mode-{id}", size);
        bool hovered = ImGui.IsItemHovered();

        Vector4 bg = selected ? Theme.Fade(tint, 0.30f)
                     : hovered ? Theme.Fade(tint, 0.15f)
                               : Theme.Fade(tint, 0.07f);
        Vector4 border = selected ? tint : Theme.Fade(tint, 0.40f);

        dl.AddRectFilled(min, max, Theme.Pack(bg), 6f);
        dl.AddRect(min, max, Theme.Pack(border), 6f, ImDrawFlags.None, selected ? 2f : 1f);

        var titleSize = ImGui.CalcTextSize(title);
        var subSize = ImGui.CalcTextSize(sub);
        Vector4 titleColor = selected ? new Vector4(1f, 1f, 1f, 1f) : tint;
        Vector4 subColor = selected ? new Vector4(1f, 1f, 1f, 0.75f) : Theme.Fade(tint, 0.65f);
        var titlePos = min + new Vector2((size.X - titleSize.X) * 0.5f, 8);
        var subPos = min + new Vector2((size.X - subSize.X) * 0.5f, size.Y - subSize.Y - 6);
        dl.AddText(titlePos, Theme.Pack(titleColor), title);
        dl.AddText(subPos, Theme.Pack(subColor), sub);

        return clicked;
    }

    private bool autoPlayConfirmPending;

    private void RequestMode(int mode, Configuration cfg)
    {
        if (mode == 2 && !cfg.AutoPlayConfirmed)
        {
            autoPlayConfirmPending = true;
            return;
        }
        ApplyMode(mode);
    }

    private void ApplyMode(int mode) =>
        plugin.ConfigService.Update(c => c with
        {
            AutomationArmed = mode > 0,
            SuggestionOnly = mode == 1,
            ContinuousCollection = mode == 2 && c.ContinuousCollection,
        });

    private void DrawAutoPlayConfirmModal(Configuration cfg)
    {
        string id = Loc.T("Enable Auto-play?", "启用自动打牌？") + "###autoplay-confirm";
        if (autoPlayConfirmPending)
        {
            ImGui.OpenPopup(id);
            autoPlayConfirmPending = false;
        }

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(id, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
            return;

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
        ImGui.TextWrapped(Loc.T("Auto-play will click for you with humanized timing.", "自动打牌会以拟人化的节奏替你点击。"));
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
        ImGui.PushTextWrapPos(360);
        ImGui.TextUnformatted(Loc.T(
            "Switch Mode to Off or Hints any time to stop. Third-party automation is " +
            "against the FFXIV Terms of Service — use at your own risk.",
            "随时把模式切到“关闭”或“提示”即可停止。第三方自动化违反 FF14 服务条款，使用风险自负。"));
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 10));

        if (ImGui.Button(Loc.T("Enable Auto-play", "启用自动打牌") + "##autoplay-enable", new Vector2(160, 28)))
        {
            plugin.ConfigService.Update(c => c with { AutoPlayConfirmed = true });
            ApplyMode(2);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Cancel", "取消") + "##autoplay-cancel", new Vector2(100, 28)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private void DrawCollectionCard(Configuration cfg)
    {
        using (Theme.BeginCard("collection"))
        {
            Theme.SectionHeader(Loc.T("Continuous collection · Player East-only", "连续采集 · 玩家东风战"));
            bool enabled = cfg.ContinuousCollection;
            if (!cfg.AutoPlayConfirmed) ImGui.BeginDisabled();
            if (ImGui.Checkbox(Loc.T("Auto-queue, commence and repeat until stopped", "自动排队、开局并循环，直到手动停止") + "##collection", ref enabled))
            {
                if (enabled) Plugin.ChatGui.Print("[MjAuto] " + Loc.Status(plugin.Collection.Start()));
                else plugin.Collection.Stop();
            }
            if (!cfg.AutoPlayConfirmed) ImGui.EndDisabled();
            ImGui.TextWrapped(Loc.Status(plugin.Collection.Status));
            Theme.Subtle(Loc.T($"Completed this load: {plugin.Collection.CompletedMatches}. No time limit; resumes after reload.", $"本次加载已完成：{plugin.Collection.CompletedMatches} 场。没有时间限制，重载后会继续。"));
            Theme.Subtle(Loc.T("Stop withdraws the queue or finishes the current match. Off stops all play.", "停止会撤销排队，或打完当前这一场。切到“关闭”会停止一切操作。"));
        }
    }

    private void DrawLiveCard()
    {
        using (Theme.BeginCard("live"))
        {
            Theme.SectionHeader(Loc.T("Live game", "当前对局"));

            var snap = plugin.Aggregator.Latest;
            if (snap is null)
            {
                DrawEmptyLive();
                return;
            }

            var evaluation = plugin.Aggregator.PresentedEvaluation(plugin.MortalBridge.Enabled,
                plugin.MortalBridge.TryGetRecommendation(snap, out _, out _),
            plugin.MortalBridge.Enabled && plugin.MortalBridge.CurrentHandQuarantined);
            bool usesMortal = evaluation?.Source == "mortal" || (evaluation is null && plugin.MortalBridge.Enabled && !plugin.MortalBridge.CurrentHandQuarantined);
            ScoredDiscard[]? scored = evaluation?.Candidates.ToArray();
            var choice = evaluation?.Choice;
            bool? choiceIsRed = choice?.DiscardIsRed;
            string? scorerError = plugin.Aggregator.LastScorerError;
            int highlightSlot = -1;
            if (choice?.DiscardTile is { } t)
                highlightSlot = InputDispatcher.FindSlotOfTile(t, snap.Hand, snap.HandIsRed, choiceIsRed);

            DrawSeatRow(snap);
            ImGui.Dummy(new Vector2(0, 10));
            DrawHandRow(snap, highlightSlot);
            ImGui.Dummy(new Vector2(0, 10));
            DrawSuggestion(snap, scored, choice, scorerError, usesMortal);
        }
    }

    private void DrawEmptyLive()
    {
        var cfg = plugin.Configuration;
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
        ImGui.TextWrapped(Loc.T("Waiting for a Doman Mahjong table.", "等待进入多玛麻将桌。"));
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 4));

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.TextWrapped(Loc.T("When a match starts, the plugin will:", "对局开始后，插件会："));
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 2));

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
        ImGui.BulletText(Loc.T("Hints — outline the best discard in the mahjong window.", "提示：在麻将窗口中框出最佳打牌。"));
        ImGui.PopStyleColor();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        ImGui.BulletText(Loc.T("Auto-play — click the best discard for you with humanized timing.", "自动打牌：以拟人化的节奏替你打出最佳的牌。"));
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));
        string modeHint = !cfg.AutomationArmed
            ? Loc.T("Mode is currently Off. Pick Hints or Auto-play above to arm the plugin.", "当前模式为“关闭”。在上方选择“提示”或“自动打牌”来启用插件。")
            : cfg.SuggestionOnly
                ? Loc.T("Hints mode is active — open a match and watch for the outline.", "提示模式已启用，开局后留意牌上的高亮框。")
                : Loc.T("Auto-play is active — open a match and the plugin will take over.", "自动打牌已启用，开局后插件会接管。");
        Theme.Subtle(modeHint);
    }

    private void DrawSeatRow(StateSnapshot snap)
    {
        string[] labels = { Loc.T("You", "自家"), Loc.T("Right", "下家"), Loc.T("Across", "对家"), Loc.T("Left", "上家") };
        float avail = ImGui.GetContentRegionAvail().X;
        float gap = 6f;
        float pillW = (avail - gap * 3) / 4f;
        for (int i = 0; i < 4; i++)
        {
            DrawSeatPill(labels[i], snap.Scores[i], isYou: i == 0, new Vector2(pillW, 40));
            if (i < 3)
                ImGui.SameLine(0, gap);
        }
    }

    private static void DrawSeatPill(string label, int score, bool isYou, Vector2 size)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        Vector4 tint = isYou ? Theme.Accent : Theme.Muted;
        Vector4 bg = Theme.Fade(tint, isYou ? 0.18f : 0.08f);

        dl.AddRectFilled(min, max, Theme.Pack(bg), 6f);
        dl.AddRect(min, max, Theme.Pack(tint, isYou ? 0.85f : 0.45f), 6f, ImDrawFlags.None, 1f);

        var labelSize = ImGui.CalcTextSize(label);
        var labelPos = min + new Vector2((size.X - labelSize.X) * 0.5f, 5);
        dl.AddText(labelPos, Theme.Pack(tint, 0.8f), label);

        string scoreStr = score.ToString();
        var scoreSize = ImGui.CalcTextSize(scoreStr);
        var scorePos = min + new Vector2((size.X - scoreSize.X) * 0.5f, size.Y - scoreSize.Y - 5);
        Vector4 scoreColor = isYou ? Theme.Header : Theme.Body;
        dl.AddText(scorePos, Theme.Pack(scoreColor), scoreStr);

        ImGui.Dummy(size);
    }

    private static void DrawHandRow(StateSnapshot snap, int highlightSlot)
    {
        Theme.Caption(Loc.T($"Hand · {snap.Hand.Count} tiles", $"手牌 · {snap.Hand.Count} 张"));
        ImGui.Dummy(new Vector2(0, 3));
        Theme.DrawHand(snap.Hand, highlightSlot);
    }

    private void DrawSuggestion(
        StateSnapshot snap,
        ScoredDiscard[]? scored,
        ActionChoice? choice,
        string? scorerError,
        bool usesMortal)
    {
        var cfg = plugin.Configuration;

        Theme.Caption(Loc.T("Best move", "最佳打法"));
        ImGui.Dummy(new Vector2(0, 3));

        if (!snap.Legal.Can(ActionFlags.Discard))
        {
            Theme.Subtle(Loc.T($"Waiting for your turn ({snap.Hand.Count} tiles in hand).", $"等待轮到你（手牌 {snap.Hand.Count} 张）。"));
            return;
        }

        if (scorerError != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Danger);
            ImGui.TextWrapped($"{Loc.T("scorer error", "评分出错")}: {scorerError}");
            ImGui.PopStyleColor();
            return;
        }

        if (choice is null)
        {
            if (usesMortal)
                Theme.Subtle(Loc.T("Waiting for Mortal's decision.", "等待 Mortal 的决策。"));
            return;
        }

        string verb = FriendlyActionVerb(choice.Kind);

        float startY = ImGui.GetCursorPosY();
        float bigH = Theme.BigTileH;
        float textH = ImGui.CalcTextSize("X").Y;
        float textY = startY + (bigH - textH) * 0.5f;

        ImGui.SetCursorPosY(textY);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        ImGui.TextUnformatted(verb);
        ImGui.PopStyleColor();

        if (choice.DiscardTile is { } t)
        {
            ImGui.SameLine(0, 12);
            ImGui.SetCursorPosY(startY);
            Theme.DrawTile(t, new Vector2(Theme.BigTileW, Theme.BigTileH), Theme.Pulse(1.4f, 0.55f, 1.0f));

            ImGui.SameLine(0, 12);
            ImGui.SetCursorPosY(textY);
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.TextUnformatted(FriendlyTileName(t));
            ImGui.PopStyleColor();
        }

        if (choice.DiscardTile is not null)
            ImGui.SetCursorPosY(startY + bigH + 4);

        string why = !string.IsNullOrWhiteSpace(choice.Reasoning) ? choice.Reasoning
            : usesMortal ? Loc.T("Selected by Mortal.", "由 Mortal 选择。") : ExplainChoice(choice, scored ?? []);
        if (!string.IsNullOrEmpty(why))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
            ImGui.TextWrapped(why);
            ImGui.PopStyleColor();
        }

        if (cfg.ShowInGameHighlight && cfg.SuggestionOnly)
        {
            Theme.Subtle(Loc.T("The tile is outlined in the mahjong window.", "这张牌已在麻将窗口中框出。"));
        }

        if (cfg.ShowSuggestionDetails && usesMortal) Theme.Subtle(Loc.T("Mortal did not provide candidate scores.", "Mortal 没有提供候选打牌的评分。"));
        if (cfg.ShowSuggestionDetails && scored is not null)
        {
            ImGui.Dummy(new Vector2(0, 6));
            Theme.Subtle(Loc.T(
                "shanten = turns away from ready.  ukeire = tiles that complete your wait — " +
                "counted as distinct kinds and total copies remaining in the live wall.",
                "向听 = 离听牌还差几步。进张 = 能推进手牌的牌，分别按种类数和牌山中剩余的总张数计算。"));
            ImGui.Dummy(new Vector2(0, 4));
            int show = Math.Min(3, scored.Length);
            for (int i = 0; i < show; i++)
                DrawScoredPickRow(i, scored[i]);
        }

        if (plugin.AutoPlay.LastActionDescription != "(none)")
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.TextWrapped($"{Loc.T("Last action", "上一步操作")}: {plugin.AutoPlay.LastActionDescription}");
            ImGui.PopStyleColor();
        }
    }

    private static void DrawScoredPickRow(int rank, ScoredDiscard s)
    {
        float rowStart = ImGui.GetCursorPosY();
        float tileH = Theme.SmallTileH;
        float textH = ImGui.CalcTextSize("X").Y;
        float textY = rowStart + (tileH - textH) * 0.5f;

        ImGui.SetCursorPosY(textY);
        Vector4 rankColor = rank == 0 ? Theme.Accent : Theme.Muted;
        ImGui.PushStyleColor(ImGuiCol.Text, rankColor);
        ImGui.TextUnformatted($"{rank + 1}.");
        ImGui.PopStyleColor();

        ImGui.SameLine(0, 8);
        ImGui.SetCursorPosY(rowStart);
        Theme.DrawTile(s.Discard, new Vector2(Theme.SmallTileW, Theme.SmallTileH));

        ImGui.SameLine(0, 10);
        ImGui.SetCursorPosY(textY);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
        ImGui.TextUnformatted(Loc.T($"shanten {s.ShantenAfter}    ukeire {s.UkeireKinds} kinds · {s.UkeireWeighted} tiles", $"向听 {s.ShantenAfter}    进张 {s.UkeireKinds} 种 · {s.UkeireWeighted} 张"));
        ImGui.PopStyleColor();

        ImGui.SetCursorPosY(rowStart + tileH + 3);
    }

    private static string FriendlyActionVerb(ActionKind kind) => kind switch
    {
        ActionKind.Discard => Loc.T("Discard", "打出"),
        ActionKind.Riichi => Loc.T("Riichi on", "立直并打出"),
        ActionKind.Tsumo => Loc.T("Win (tsumo)", "和牌（自摸）"),
        ActionKind.Ron => Loc.T("Win (ron)", "和牌（荣和）"),
        ActionKind.Pon => Loc.T("Pon", "碰"),
        ActionKind.Chi => Loc.T("Chi", "吃"),
        ActionKind.AnKan => Loc.T("Kan", "杠"),
        ActionKind.MinKan => Loc.T("Kan", "杠"),
        ActionKind.ShouMinKan => Loc.T("Kan", "杠"),
        _ => kind.ToString(),
    };

    private static string FriendlyTileName(Tile tile) => Theme.TileFriendlyName(tile);

    private static string ExplainChoice(ActionChoice choice, ScoredDiscard[] scored)
    {
        switch (choice.Kind)
        {
            case ActionKind.Tsumo:
            case ActionKind.Ron:
                return Loc.T("Winning hand — close it out.", "已经和牌，直接收下。");
            case ActionKind.Riichi:
                return Loc.T("One tile from winning — declare riichi.", "已听牌，宣告立直。");
            case ActionKind.Pon:
            case ActionKind.Chi:
            case ActionKind.AnKan:
            case ActionKind.MinKan:
            case ActionKind.ShouMinKan:
                return Loc.T("Calling completes a set faster than drawing for it.", "鸣牌比自己摸更快凑成一组。");
        }

        if (scored.Length == 0) return "";
        var top = scored[0];
        string kindNoun = top.UkeireKinds == 1 ? "kind" : "kinds";
        string tileNoun = top.UkeireWeighted == 1 ? "tile" : "tiles";

        if (top.ShantenAfter < 0)
            return Loc.T("Discarding this still leaves a winning hand.", "打出这张后仍然是和牌型。");
        if (top.ShantenAfter == 0)
            return Loc.T($"Keeps you ready — {top.UkeireKinds} {kindNoun} ({top.UkeireWeighted} {tileNoun} live) complete the hand.", $"保持听牌：共 {top.UkeireKinds} 种牌（剩余 {top.UkeireWeighted} 张）可以和牌。");
        if (top.ShantenAfter == 1)
            return Loc.T($"One step from ready, with {top.UkeireKinds} useful {kindNoun} to draw.", $"一向听，有 {top.UkeireKinds} 种有效进张。");
        return Loc.T($"{top.ShantenAfter} steps from ready — keeps the most useful draws available.", $"{top.ShantenAfter} 向听，保留最多的有效进张。");
    }
}

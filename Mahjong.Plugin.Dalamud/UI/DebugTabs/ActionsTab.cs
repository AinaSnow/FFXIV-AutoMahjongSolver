using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.UI.DebugTabs;

/// <summary>Manual dispatch surface: auto-discard, test slot, test call option.</summary>
internal sealed class ActionsTab
{
    private readonly DevConsoleContext ctx;
    private int testDiscardSlot = 13;
    private int testCallOption;

    public ActionsTab(DevConsoleContext ctx) => this.ctx = ctx;

    public void Draw()
    {
        var snap = ctx.Plugin.Aggregator.Latest;
        bool addonPresent = snap is not null;
        bool ourTurn = addonPresent && snap!.Legal.Can(ActionFlags.Discard);

        using (Theme.BeginCard("actions-auto"))
        {
            Theme.SectionHeader(Loc.T("Auto-discard", "自动打牌"));
            Theme.Subtle(Loc.T("Asks the policy what to discard and dispatches the click. Only works on your turn.", "询问策略该打哪张并执行点击，仅在轮到你时有效。"));
            using (DevHelpers.Disable(!ourTurn))
            {
                float w = ImGui.GetContentRegionAvail().X;
                if (ImGui.Button(Loc.T("Run policy pick", "执行策略选择"), new Vector2(w, 34)))
                    ctx.Framework.RunOnFrameworkThread(TriggerAutoDiscard);
            }
            if (!ourTurn)
                Theme.Subtle(addonPresent ? "Not our turn." : "No snapshot — open a match first.");
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("actions-testslot"))
        {
            Theme.SectionHeader(Loc.T("Test discard slot", "测试打出指定位置"));
            Theme.Subtle(Loc.T("Discards the tile at a specific hand slot regardless of policy. 0 = leftmost, 13 = just-drawn.", "无视策略，直接打出指定位置的牌。0 = 最左，13 = 刚摸的牌。"));

            ImGui.SetNextItemWidth(90);
            ImGui.InputInt("##slot", ref testDiscardSlot);
            testDiscardSlot = Math.Clamp(testDiscardSlot, 0, 13);
            ImGui.SameLine(0, 8);
            using (DevHelpers.Disable(!ourTurn))
            {
                if (ImGui.Button(Loc.T($"Dispatch slot {testDiscardSlot}", $"打出位置 {testDiscardSlot}")))
                {
                    int slot = testDiscardSlot;
                    ctx.Framework.RunOnFrameworkThread(() =>
                    {
                        var r = ctx.Plugin.Dispatcher.DispatchDiscard(slot);
                        ctx.LastToast = $"discard slot={slot} → {r}";
                    });
                }
            }

            ImGui.Dummy(new Vector2(0, 3));
            if (ImGui.SmallButton(Loc.T("drawn (13)", "摸牌 (13)")))
                testDiscardSlot = 13;
            ImGui.SameLine(0, 4);
            if (ImGui.SmallButton(Loc.T("leftmost (0)", "最左 (0)")))
                testDiscardSlot = 0;
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("actions-testcall"))
        {
            Theme.SectionHeader(Loc.T("Test call option", "测试鸣牌选项"));
            Theme.Subtle(Loc.T("Send a raw call-prompt button index. Enable event-logger in Diagnostics to see which button each index hits.", "发送鸣牌提示的原始按钮序号。在“诊断”里开启事件记录，可以看到每个序号对应哪个按钮。"));

            ImGui.SetNextItemWidth(90);
            ImGui.InputInt("##opt", ref testCallOption);
            testCallOption = Math.Clamp(testCallOption, 0, 5);
            ImGui.SameLine(0, 8);
            if (ImGui.Button(Loc.T($"Dispatch opt {testCallOption}", $"发送选项 {testCallOption}")))
            {
                int opt = testCallOption;
                ctx.Framework.RunOnFrameworkThread(() =>
                {
                    var r = ctx.Plugin.Dispatcher.DispatchCallOption(opt);
                    ctx.LastToast = $"call opt={opt} → {r}";
                });
            }

            ImGui.Dummy(new Vector2(0, 3));
            for (int i = 0; i < 3; i++)
            {
                if (i > 0)
                    ImGui.SameLine(0, 4);
                int v = i;
                if (ImGui.SmallButton(v.ToString()))
                    testCallOption = v;
            }

            ImGui.Dummy(new Vector2(0, 3));
            Theme.Subtle(Loc.T("pon/pass: 0=Pass, 1=Pon.  chi/pass: likely 0=Chi, 1=Pass.  Verify by logging.", "碰/跳过：0=跳过，1=碰。吃/跳过：可能是 0=吃，1=跳过。请用事件记录确认。"));
        }
    }

    private void TriggerAutoDiscard()
    {
        var snap = ctx.Plugin.AddonReader.TryBuildSnapshot();
        if (snap is null || !snap.Legal.Can(ActionFlags.Discard))
        {
            ctx.LastToast = "auto-discard: not our turn";
            return;
        }

        var choice = ctx.Plugin.Policy.Choose(snap);
        if (choice.Kind != ActionKind.Discard || choice.DiscardTile is null)
        {
            ctx.LastToast = $"auto-discard: policy returned {choice.Kind}";
            return;
        }

        var tile = choice.DiscardTile.Value;
        int slot = ctx.Plugin.AddonReader.FindAddonSlotOfTile(tile);
        if (slot < 0)
        {
            ctx.LastToast = $"auto-discard: tile {tile} not found in hand";
            return;
        }

        var delay = HumanTiming.RandomDelay();
        ctx.LastToast = $"auto-discarding {tile} slot {slot} in {delay.TotalMilliseconds:F0}ms";

        _ = ctx.Framework.RunOnTick(() =>
        {
            var r = ctx.Plugin.Dispatcher.DispatchDiscard(slot);
            ctx.LastToast = $"auto-discarded {tile} → {r}";
        }, delay);
    }
}

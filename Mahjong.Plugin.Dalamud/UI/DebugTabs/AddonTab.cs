using System.Numerics;
using Dalamud.Bindings.ImGui;
using Mahjong.Plugin.Dalamud.GameState;

namespace Mahjong.Plugin.Dalamud.UI.DebugTabs;

/// <summary>AddonEmj lifecycle, plus the active variant and layout profile.</summary>
internal sealed class AddonTab
{
    private readonly DevConsoleContext ctx;

    public AddonTab(DevConsoleContext ctx) => this.ctx = ctx;

    public void Draw()
    {
        using (Theme.BeginCard("addon"))
        {
            Theme.SectionHeader("AddonEmj");
            Theme.Subtle(Loc.T("In-memory address and visibility of the mahjong UI. Empty until you seat at a table.", "麻将界面的内存地址和可见状态。上桌之前为空。"));
            var obs = ctx.Plugin.AddonReader.Poll();
            if (!obs.Present)
            {
                var candidates = string.Join("\" or \"", ctx.Addon.KnownAddonNames);
                Theme.Subtle(Loc.T($"Addon \"{candidates}\" not found. Open a Doman Mahjong match in-game.", $"找不到界面 \"{candidates}\"。请在游戏里进入一场多玛麻将。"));
                if (obs.LastLifecycleEvent is not null)
                {
                    ImGui.Dummy(new Vector2(0, 4));
                    DevHelpers.KeyValueRow(Loc.T("Last event", "最近事件"), obs.LastLifecycleEvent);
                }
                return;
            }

            DevHelpers.KeyValueRow(Loc.T("Address", "地址"), $"0x{obs.Address:X}");
            DevHelpers.KeyValueRow(Loc.T("Visible", "可见"), obs.IsVisible.ToString());
            DevHelpers.KeyValueRow(Loc.T("Last event", "最近事件"), obs.LastLifecycleEvent ?? "(none)");
        }

        ImGui.Dummy(new Vector2(0, 4));
        DrawVariantCard();
        ImGui.Dummy(new Vector2(0, 4));
        DrawLayoutCard();
    }

    private void DrawVariantCard()
    {
        using (Theme.BeginCard("addon-variants"))
        {
            Theme.SectionHeader(Loc.T("Variant probes", "客户端变体探测"));
            Theme.Subtle(Loc.T("Which variant strategy matched on the current addon. Miss on all = run Bug report → Dump variant.", "当前界面匹配到了哪个变体策略。全部不匹配时，请到“问题报告 → 转储变体”。"));

            var selector = ctx.Plugin.AddonReader.Selector;
            if (selector.Variants.Count == 0)
            {
                Theme.Subtle(Loc.T("(no variants registered)", "（没有注册任何变体）"));
                return;
            }

            foreach (var v in selector.Variants)
            {
                string status = ProbeStatus(v);
                DevHelpers.KeyValueRow(v.Name, status);
            }
        }
    }

    private unsafe string ProbeStatus(GameState.Variants.IEmjVariant v)
    {
        if (!ctx.Addon.TryGet(out var unit, out _))
            return "no addon";
        try
        {
            return v.Probe(unit) ? "MATCH" : "miss";
        }
        catch (System.Exception ex)
        {
            return $"threw: {ex.GetType().Name}";
        }
    }

    private void DrawLayoutCard()
    {
        using (Theme.BeginCard("addon-layout"))
        {
            Theme.SectionHeader(Loc.T("Active layout profile", "当前布局配置"));
            Theme.Subtle(Loc.T("Read-offsets the variant uses to extract your hand, scores, and dora from addon memory.", "该变体从界面内存中读取手牌、分数和宝牌所用的偏移。"));

            var layout = ctx.Plugin.AddonReader.ActiveLayout;
            if (layout is null)
            {
                Theme.Subtle(Loc.T("(no profile active — addon hasn't been parsed yet)", "（没有生效的配置：界面尚未解析）"));
                return;
            }

            DevHelpers.KeyValueRow(Loc.T("Name", "名称"), layout.Name);
            DevHelpers.KeyValueRow(Loc.T("Addon name", "界面名"), layout.AddonName);
            DevHelpers.KeyValueRow(Loc.T("Tile tex base", "牌贴图基址"), layout.TileTextureBase.ToString());
            DevHelpers.KeyValueRow(Loc.T("Hand array start", "手牌数组起点"), $"0x{layout.Offsets.HandArrayStart:X}");
            DevHelpers.KeyValueRow(Loc.T("Self score", "自家分数"), $"0x{layout.Offsets.SelfScore:X}");
            DevHelpers.KeyValueRow(Loc.T("Dora indicator", "宝牌指示牌"), $"0x{layout.Offsets.DoraIndicator:X}");
            DevHelpers.KeyValueRow(Loc.T("Hand size limit", "手牌数量上限"), layout.Limits.HandSize.ToString());
        }
    }
}

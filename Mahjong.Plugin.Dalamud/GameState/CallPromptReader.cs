using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.Game.Variants;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.GameState;

internal readonly record struct CallPromptOption(int Index, string Label, bool Enabled = true);

/// <summary>Read the live list's logical indexes; never infer button order from action flags.</summary>
internal static class CallPromptReader
{
    internal static unsafe IReadOnlyList<CallPromptOption> Read(AtkUnitBase* unit, uint hostId, uint shellId)
    {
        if (unit == null || !unit->IsVisible) return [];
        var host = unit->GetNodeById(hostId);
        if (host == null || (int)host->Type < 1000 || !Visible(host)) return [];
        var hostComponent = ((AtkComponentNode*)host)->Component;
        if (hostComponent == null) return [];
        var shell = hostComponent->GetNodeById(shellId);
        if (shell == null || (int)shell->Type != 1030 || !Visible(shell)) return [];
        return ReadRows((AtkComponentList*)((AtkComponentNode*)shell)->Component);
    }

    internal static unsafe IReadOnlyList<CallPromptOption> ReadRows(AtkComponentList* list)
    {
        if (list == null || list->ListLength is <= 0 or > 16 || list->ItemRendererList == null
            || list->AllocatedItemRendererListLength < list->ListLength
            || list->IsUpdatePending || list->IsScrollRefreshPending) return [];
        var result = new List<CallPromptOption>();
        for (int i = 0; i < list->ListLength; i++)
        {
            var item = list->ItemRendererList[i];
            var renderer = item.AtkComponentListItemRenderer;
            if (renderer == null || renderer->ListItemIndex != i) continue;
            var button = (AtkComponentButton*)renderer;
            var component = (AtkComponentBase*)renderer;
            if (!Visible((AtkResNode*)component->OwnerNode)) continue;
            var text = button->ButtonTextNode;
            if (text == null || !Visible((AtkResNode*)text)) continue;
            result.Add(new(i, text->NodeText.ToString(), !item.IsDisabled && button->IsEnabled));
        }
        return result;
    }

    private static unsafe bool Visible(AtkResNode* node)
    {
        // Include ancestors: pooled renderers retain text after their parent is hidden.
        for (int depth = 0; node != null && depth < 32; depth++, node = node->ParentNode)
            if (!node->NodeFlags.HasFlag(NodeFlags.Visible)) return false;
        return node == null;
    }

    internal static bool TryResolve(IReadOnlyList<CallPromptOption> options, LayoutActionLabels labels,
        ActionKind kind, out int index)
    {
        index = -1;
        foreach (var option in options)
        {
            bool matches = kind switch
            {
                ActionKind.Ron => LayoutActionLabelMatcher.IsRon(option.Label, labels),
                ActionKind.Tsumo => LayoutActionLabelMatcher.IsTsumo(option.Label, labels),
                ActionKind.Riichi => LayoutActionLabelMatcher.IsRiichi(option.Label, labels),
                ActionKind.Pon => LayoutActionLabelMatcher.IsPon(option.Label, labels),
                ActionKind.Chi => LayoutActionLabelMatcher.IsChi(option.Label, labels),
                ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan => LayoutActionLabelMatcher.IsKan(option.Label, labels),
                ActionKind.Pass => LayoutActionLabelMatcher.IsPass(option.Label, labels),
                _ => false,
            };
            if (!matches) continue;
            if (!option.Enabled || option.Index < 0 || index >= 0) { index = -1; return false; }
            index = option.Index;
        }
        return index >= 0;
    }
}

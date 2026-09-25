using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Game.Variants;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class CallPromptReaderTests
{
    [Theory]
    [InlineData("Ron", "Chi", ActionKind.Ron, 0)]
    [InlineData("Ron", "Chi", ActionKind.Chi, 1)]
    [InlineData("Chi", "Ron", ActionKind.Ron, 1)]
    [InlineData("Pon", "Ron", ActionKind.Ron, 1)]
    [InlineData("Ron", "Pon", ActionKind.Ron, 0)]
    [InlineData("Ron", "Chi", ActionKind.Pass, 2)]
    [InlineData("立直", "槓", ActionKind.AnKan, 1)]
    public void Action_uses_observed_index_in_either_order(string first, string second, ActionKind kind, int expected)
    {
        CallPromptOption[] options = [new(0, first), new(1, second), new(2, "Pass")];
        Assert.True(CallPromptReader.TryResolve(options, LayoutActionLabels.Default, kind, out int index));
        Assert.Equal(expected, index);
    }

    [Fact]
    public void Unknown_rows_keep_their_slot_and_missing_or_duplicate_win_is_not_guessed()
    {
        CallPromptOption[] options = [new(0, "unrecognized"), new(3, "Ron"), new(4, "Pass")];
        Assert.True(CallPromptReader.TryResolve(options, LayoutActionLabels.Default, ActionKind.Ron, out int slot));
        Assert.Equal(3, slot);
        Assert.False(CallPromptReader.TryResolve([new(1, "Chi"), new(2, "Pass")], LayoutActionLabels.Default, ActionKind.Ron, out _));
        Assert.False(CallPromptReader.TryResolve([new(0, "Ron"), new(1, "Ron")], LayoutActionLabels.Default, ActionKind.Ron, out _));
        Assert.False(CallPromptReader.TryResolve([new(0, "Ron", false)], LayoutActionLabels.Default, ActionKind.Ron, out _));
        Assert.False(CallPromptReader.TryResolve([], LayoutActionLabels.Default, ActionKind.Ron, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public unsafe void Native_rows_retain_logical_indexes_and_reject_hidden_or_unsettled_renderers(int scenario)
    {
        // Owned synthetic UI memory. No game pointers or game functions are used.
        AtkComponentList list = default;
        AtkComponentListItemRenderer renderer = default;
        AtkComponentNode owner = default;
        AtkResNode parent = default;
        AtkTextNode text = default;
        var items = stackalloc AtkComponentList.ListItem[3];
        new Span<AtkComponentList.ListItem>(items, 3).Clear();
        list.ItemRendererList = items; list.ListLength = 3; list.AllocatedItemRendererListLength = 3;
        renderer.ListItemIndex = 2;
        items[2].AtkComponentListItemRenderer = &renderer;
        ((AtkComponentBase*)&renderer)->OwnerNode = &owner;
        ((AtkComponentButton*)&renderer)->ButtonTextNode = &text;
        owner.AtkResNode.NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;
        owner.AtkResNode.ParentNode = &parent; parent.NodeFlags = NodeFlags.Visible;
        text.AtkResNode.NodeFlags = NodeFlags.Visible;
        text.AtkResNode.ParentNode = (AtkResNode*)&owner;
        byte[] bytes = Encoding.UTF8.GetBytes("Ron\0");
        fixed (byte* ptr = bytes)
        {
            text.NodeText.StringPtr = ptr; text.NodeText.BufUsed = bytes.Length;
            if (scenario == 1) parent.NodeFlags = 0; // Visible child under hidden parent.
            if (scenario == 2) list.IsUpdatePending = true;
            if (scenario == 3) renderer.ListItemIndex = 1; // Recycled stale renderer.
            if (scenario == 4) items[2].IsDisabled = true;
            if (scenario == 5) list.AllocatedItemRendererListLength = 2;
            var rows = CallPromptReader.ReadRows(&list);
            Assert.Equal(scenario == 0, CallPromptReader.TryResolve(rows, LayoutActionLabels.Default, ActionKind.Ron, out int index));
            if (scenario == 0) Assert.Equal(2, index); // Hidden/absent slots were never compressed.
        }
    }
}

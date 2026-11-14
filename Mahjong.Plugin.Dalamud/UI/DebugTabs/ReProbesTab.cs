using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Mahjong.Plugin.Dalamud.UI.DebugTabs;

/// <summary>RE primitives: walknodes, findtiles, poolslots/icons, hexdump, dumpmem, atkvalues, addons.</summary>
internal sealed class ReProbesTab
{
    private readonly DevConsoleContext ctx;
    private string hexStart = "0x0C00";
    private string hexEnd = "0x1400";
    private bool hexAgent;
    private string memOffset = "0x0238";
    private string memLength = "0x0400";
    private string addonsFilter = "";

    public ReProbesTab(DevConsoleContext ctx) => this.ctx = ctx;

    public void Draw()
    {
        var cmd = ctx.Plugin.MjAutoCommand;

        using (Theme.BeginCard("re-tree"))
        {
            Theme.SectionHeader(Loc.T("Addon tree & memory scan", "界面节点树与内存扫描"));
            Theme.Subtle(Loc.T("walknodes: every UI node in the addon. findtiles: scan memory for tile-encoded values. atkvalues: the addon's parameter array.", "walknodes：界面里的所有 UI 节点。findtiles：在内存里扫描牌编码。atkvalues：界面的参数数组。"));
            if (ImGui.Button("Walk Nodes"))
            {
                cmd.HandleWalkNodes();
                ctx.LastToast = "walknodes queued → emj-nodes.txt";
            }
            ImGui.SameLine(0, 3);
            DevHelpers.CopyPathButton("emj-nodes.txt", "nodes");
            ImGui.SameLine(0, 10);
            if (ImGui.Button("Find Tiles"))
            {
                cmd.HandleFindTiles();
                ctx.LastToast = "findtiles queued → emj-findtiles.txt";
            }
            ImGui.SameLine(0, 3);
            DevHelpers.CopyPathButton("emj-findtiles.txt", "findtiles");
            ImGui.SameLine(0, 10);
            if (ImGui.Button("Atk Values"))
            {
                cmd.DumpAtkValues();
                ctx.LastToast = "atkvalues → emj-atkvalues.txt";
            }
            ImGui.SameLine(0, 3);
            DevHelpers.CopyPathButton("emj-atkvalues.txt", "atkvalues");
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("re-pool"))
        {
            Theme.SectionHeader(Loc.T("Discard-pool slot diff", "河牌位置对比"));
            Theme.Subtle(Loc.T("Finds which byte inside each pool-tile slot encodes the tile id, by comparing slots with different tiles. Used to locate fields when a client patch moves them.", "通过对比放着不同牌的位置，找出河牌位置中哪个字节记录牌 ID。客户端更新移动了字段时用来重新定位。"));
            if (ImGui.Button("Pool Slots"))
            {
                cmd.HandlePoolSlots();
                ctx.LastToast = "poolslots queued → emj-poolslots.txt";
            }
            ImGui.SameLine(0, 3);
            DevHelpers.CopyPathButton("emj-poolslots.txt", "poolslots");
            ImGui.SameLine(0, 10);
            if (ImGui.Button("Pool Icons"))
            {
                cmd.HandlePoolIcons();
                ctx.LastToast = "poolicons queued → emj-poolicons.txt";
            }
            ImGui.SameLine(0, 3);
            DevHelpers.CopyPathButton("emj-poolicons.txt", "poolicons");
            Theme.Subtle(Loc.T("Walks types 1021..1024 and diffs candidate tile bytes across visible slots.", "遍历类型 1021..1024，对比可见位置中的候选牌字节。"));
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("re-hex"))
        {
            Theme.SectionHeader(Loc.T("Hex dump", "十六进制转储"));
            Theme.Subtle(Loc.T("Raw bytes of the addon (or AgentEmj) over a chosen range. Defaults target the current best-guess region.", "转储界面（或 AgentEmj）指定范围的原始字节。默认范围是目前最可能的区域。"));
            ImGui.Checkbox(Loc.T("Agent (vs addon)", "Agent（而非界面）"), ref hexAgent);
            ImGui.SetNextItemWidth(120);
            ImGui.InputText("start##hex", ref hexStart, 16);
            ImGui.SameLine(0, 8);
            ImGui.SetNextItemWidth(120);
            ImGui.InputText("end##hex", ref hexEnd, 16);

            bool startOk = DevHelpers.TryParseHex(hexStart, out _);
            bool endOk = DevHelpers.TryParseHex(hexEnd, out _);
            using (DevHelpers.Disable(!(startOk && endOk)))
            {
                if (ImGui.Button("Hex Dump"))
                {
                    var args = (hexAgent ? "agent " : "") + $"{hexStart} {hexEnd}";
                    cmd.HandleHexDump(args);
                    ctx.LastToast = $"hexdump {args.Trim()} → emj-hexdump{(hexAgent ? "-agent" : "")}.txt";
                }
            }
            ImGui.SameLine(0, 6);
            DevHelpers.CopyPathButton(hexAgent ? "emj-hexdump-agent.txt" : "emj-hexdump.txt", "hex");
            if (!startOk || !endOk)
                Theme.Subtle(Loc.T("Use hex with optional 0x prefix.", "使用十六进制，可带 0x 前缀。"));
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("re-mem"))
        {
            Theme.SectionHeader(Loc.T("Addon memory (offset + length)", "界面内存（偏移 + 长度）"));
            Theme.Subtle(Loc.T("Same idea as Hex dump but addon-only and written to emj-dump.txt. Convenient for fixed-offset structures.", "和十六进制转储类似，但只针对界面，结果写入 emj-dump.txt。适合固定偏移的结构。"));
            ImGui.SetNextItemWidth(120);
            ImGui.InputText("offset##mem", ref memOffset, 16);
            ImGui.SameLine(0, 8);
            ImGui.SetNextItemWidth(120);
            ImGui.InputText("length##mem", ref memLength, 16);

            bool offOk = DevHelpers.TryParseHex(memOffset, out _);
            bool lenOk = DevHelpers.TryParseHex(memLength, out _);
            using (DevHelpers.Disable(!(offOk && lenOk)))
            {
                if (ImGui.Button("Dump Memory"))
                {
                    cmd.DumpMemory($"{memOffset} {memLength}");
                    ctx.LastToast = $"dumpmem +{memOffset} len {memLength} → emj-dump.txt";
                }
            }
            ImGui.SameLine(0, 6);
            DevHelpers.CopyPathButton("emj-dump.txt", "dump");
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("re-addons"))
        {
            Theme.SectionHeader(Loc.T("List loaded addons", "列出已加载的界面"));
            Theme.Subtle(Loc.T("Print every game UI addon currently loaded. Substring filter; empty = all. Used to confirm the mahjong addon name on your client.", "列出当前加载的所有游戏界面。可按子串过滤，留空表示全部。用来确认你的客户端上麻将界面的名字。"));
            ImGui.SetNextItemWidth(220);
            ImGui.InputText("filter##addons", ref addonsFilter, 64);
            ImGui.SameLine(0, 8);
            if (ImGui.Button(Loc.T("List Addons", "列出界面")))
            {
                cmd.DumpAddons(addonsFilter);
                ctx.LastToast = "addons → chat";
            }
        }
    }
}

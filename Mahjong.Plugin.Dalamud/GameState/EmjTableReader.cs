using FFXIVClientStructs.FFXIV.Client.Game.Emj;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>
/// Reads the client's own mahjong table through FFXIVClientStructs (community-maintained offsets that ship
/// with each Dalamud update). Used only as independent ground truth when learning opcodes after a patch.
/// </summary>
public static unsafe class EmjTableReader
{
    public static int[]? ReadPoints()
    {
        var module = AgentModule.Instance();
        if (module == null) return null;
        var agent = (AgentEmj*)module->GetAgentByInternalId(AgentId.Emj);
        if (agent == null || agent->Game == null) return null;
        var engine = agent->Game->Engine;
        if (engine == null || engine->Core == null || engine->Core->Table == null) return null;
        var board = ((Table*)engine->Core->Table)->Board;
        if (board == null) return null;
        var points = board->RoundState.Points;
        if (points.Length != 4) return null;
        var copy = points.ToArray();
        // An uninitialised table reads as all zero; it is not evidence.
        return copy.All(p => p == 0) ? null : copy;
    }
}

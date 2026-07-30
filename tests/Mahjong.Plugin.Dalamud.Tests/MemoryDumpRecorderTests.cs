using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Mahjong.Plugin.Dalamud.Telemetry;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class MemoryDumpRecorderTests
{
    [Fact]
    public void Agent_capture_targets_named_mahjong_agent()
    {
        Assert.Equal(AgentId.Emj, MemoryDumpRecorder.MahjongAgentId);
        Assert.NotEqual((AgentId)5, MemoryDumpRecorder.MahjongAgentId);
        Assert.Equal(
            new[] { AgentId.Unk329, AgentId.Unk330 },
            MemoryDumpRecorder.MahjongCandidateAgentIds);
        Assert.Equal(4, MemoryDumpRecorder.SchemaVersion);
    }
}

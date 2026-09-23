using System.Text.Json;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Tests;

public class MatchArchiveWriterTests
{
    private static readonly MatchArchiveMortalStats Stats = new(
        Status: "Running",
        PacketsProcessed: 12,
        EventsSent: 18,
        ReactionsReceived: 4,
        DecisionsMapped: 3,
        DecisionTimeouts: 1,
        CandidateCorrections: 2,
        RecoveredDiscards: 1,
        LastModelEvalMilliseconds: 8.5);

    [Fact]
    public async Task Truncated_log_is_marked_incomplete_and_decision_health_survives()
    {
        using var tmp = new TempDir();
        string game = Path.Combine(tmp.Path, "game.ndjson");
        File.WriteAllLines(game, [
            """{"e":"decision","source":"mortal","elapsed_ms":12}""",
            """{"e":"decision","source":"local-fallback","elapsed_ms":24}""",
            """{"e":"action""" ]);
        using var writer = new MatchArchiveWriter(tmp.Path, new StubPluginLog());
        string archive = Assert.IsType<string>(await writer.FinalizeSessionAsync([game], Stats));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(archive,"summary.json")));
        Assert.True(doc.RootElement.GetProperty("packet_write_failed").GetBoolean());
        var health = doc.RootElement.GetProperty("decision_health");
        Assert.Equal(18, health.GetProperty("mean_ms").GetDouble());
        Assert.Equal(24, health.GetProperty("p95_ms").GetDouble());
        Assert.Equal(1, health.GetProperty("malformed_lines").GetInt32());
        Assert.Equal(1, health.GetProperty("sources").GetProperty("mortal").GetInt32());
    }

    [Fact]
    public void Finalize_copies_game_logs_and_writes_packet_and_summary_data()
    {
        using var tmp = new TempDir();
        string gamesDir = Path.Combine(tmp.Path, "games");
        Directory.CreateDirectory(gamesDir);
        string game = Path.Combine(gamesDir, "game-20260802-010203-hand01.ndjson");
        File.WriteAllLines(game,
        [
            "{\"e\":\"hand-start\",\"scores\":[25000,25000,25000,25000]}",
            "{\"e\":\"decision\",\"kind\":\"Pass\",\"why\":\"Mortal timeout; local fallback\"}",
            "{\"e\":\"action\",\"kind\":\"Pass\",\"result\":\"Ok\"}",
            "{\"e\":\"hand-end\",\"kind\":\"ron\",\"deltas\":[8000,-8000,0,0],\"scores_after\":[33000,17000,25000,25000]}",
        ]);

        using var writer = new MatchArchiveWriter(tmp.Path, new StubPluginLog());
        writer.RecordPacket(new CapturedMahjongPacket(
            MessageId: 637,
            Opcode: 0x018e,
            Timestamp: new DateTimeOffset(2026, 8, 2, 1, 2, 3, TimeSpan.Zero),
            Payload: [0x01, 0xAB, 0xFF]));

        string archive = Assert.IsType<string>(writer.FinalizeSession([game], Stats));

        string packetLine = Assert.Single(File.ReadAllLines(Path.Combine(archive, "packets.ndjson")));
        Assert.Contains("\"message_id\":637", packetLine);
        Assert.Contains("\"opcode\":\"0x018E\"", packetLine);
        Assert.Contains("\"payload_hex\":\"01ABFF\"", packetLine);
        Assert.True(File.Exists(Path.Combine(archive, "games", Path.GetFileName(game))));

        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(archive, "summary.json")));
        var root = summary.RootElement;
        Assert.Equal(3, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(1, root.GetProperty("packet_count").GetInt32());
        Assert.Equal(1, root.GetProperty("hand_count").GetInt32());
        Assert.Equal(1, root.GetProperty("settled_hands").GetInt32());
        Assert.Equal(1, root.GetProperty("decision_count").GetInt32());
        Assert.Equal(1, root.GetProperty("action_count").GetInt32());
        Assert.Equal(1, root.GetProperty("timeout_fallbacks").GetInt32());
        Assert.Equal(
            [33000, 17000, 25000, 25000],
            root.GetProperty("final_scores").EnumerateArray().Select(item => item.GetInt32()).ToArray());
        Assert.Equal(33000, root.GetProperty("our_score").GetInt32());
        Assert.Equal(1, root.GetProperty("our_rank").GetInt32());
        Assert.Equal(12, root.GetProperty("mortal").GetProperty("packets_processed").GetInt64());
        Assert.Null(writer.CurrentDirectory);
    }

    [Fact]
    public void Finalize_uses_latest_state_scores_and_packet_hand_boundaries()
    {
        using var tmp = new TempDir();
        string gamesDir = Path.Combine(tmp.Path, "games");
        Directory.CreateDirectory(gamesDir);
        string game = Path.Combine(gamesDir, "game-20260802-010203-hand01.ndjson");
        File.WriteAllLines(game,
        [
            "{\"e\":\"hand-start\",\"scores\":[25000,25000,25000,25000]}",
            "{\"e\":\"hand-end\",\"kind\":\"ron\",\"deltas\":[8000,-8000,0,0],\"scores_after\":[33000,17000,25000,25000]}",
            "{\"e\":\"hand-start\",\"scores\":[33000,17000,25000,25000]}",
            "{\"e\":\"state\",\"scores\":[18000,39000,23000,20000]}",
        ]);

        using var writer = new MatchArchiveWriter(tmp.Path, new StubPluginLog());
        writer.RecordPacket(HandStartPacket(selfSeat: 0, [25000, 25000, 25000, 25000]));
        writer.RecordPacket(HandStartPacket(selfSeat: 3, [33000, 17000, 25000, 25000]));

        string archive = Assert.IsType<string>(writer.FinalizeSession([game], Stats));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(archive, "summary.json")));
        var root = summary.RootElement;

        Assert.Equal(2, root.GetProperty("hand_count").GetInt32());
        Assert.Equal(2, root.GetProperty("settled_hands").GetInt32());
        Assert.Equal(
            [18000, 39000, 23000, 20000],
            root.GetProperty("final_scores").EnumerateArray().Select(item => item.GetInt32()).ToArray());
        Assert.Equal(18000, root.GetProperty("our_score").GetInt32());
        Assert.Equal(4, root.GetProperty("our_rank").GetInt32());
    }

    [Fact]
    public void Packet_result_settles_a_hand_when_score_delta_contains_a_riichi_stick()
    {
        using var tmp = new TempDir();
        string gamesDir = Path.Combine(tmp.Path, "games");
        Directory.CreateDirectory(gamesDir);
        string game = Path.Combine(gamesDir, "game-20260802-010203-hand01.ndjson");
        File.WriteAllLines(game,
        [
            "{\"e\":\"hand-start\",\"scores\":[25000,25000,25000,25000]}",
            "{\"e\":\"hand-end\",\"kind\":\"ron\",\"deltas\":[8000,-8000,-1000,0],\"scores_after\":[33000,17000,24000,25000]}",
            "{\"e\":\"state\",\"scores\":[33000,17000,24000,25000]}",
        ]);

        using var writer = new MatchArchiveWriter(tmp.Path, new StubPluginLog());
        writer.RecordPacket(HandStartPacket(selfSeat: 0, [25000, 25000, 25000, 25000]));
        writer.RecordPacket(new CapturedMahjongPacket(
            MahjongPacketMjaiDecoder.HandResultAMessageId,
            0x1234,
            DateTimeOffset.UtcNow,
            [0x01]));

        string archive = Assert.IsType<string>(writer.FinalizeSession([game], Stats));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(archive, "summary.json")));

        Assert.Equal(1, summary.RootElement.GetProperty("hand_count").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("settled_hands").GetInt32());
    }

    [Fact]
    public void Empty_session_does_not_create_an_archive()
    {
        using var tmp = new TempDir();
        using var writer = new MatchArchiveWriter(tmp.Path, new StubPluginLog());

        Assert.Null(writer.FinalizeSession([], Stats));
        Assert.Empty(Directory.GetDirectories(writer.RootDir));
    }

    [Fact]
    public void Sessions_with_the_same_start_second_get_distinct_directories()
    {
        using var tmp = new TempDir();
        using var writer = new MatchArchiveWriter(tmp.Path, new StubPluginLog());
        var timestamp = new DateTimeOffset(2026, 8, 2, 1, 2, 3, TimeSpan.Zero);
        var packet = new CapturedMahjongPacket(637, 0x018e, timestamp, [0x01]);

        writer.RecordPacket(packet);
        string first = Assert.IsType<string>(writer.FinalizeSession([], Stats));
        writer.RecordPacket(packet);
        string second = Assert.IsType<string>(writer.FinalizeSession([], Stats));

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetDirectories(writer.RootDir).Length);
        Assert.Equal(1, ReadSummary(first).GetProperty("hand_count").GetInt32());
        Assert.Equal(1, ReadSummary(second).GetProperty("hand_count").GetInt32());
    }

    [Fact]
    public async Task Retention_preserves_legacy_and_active_directories()
    {
        using var tmp = new TempDir();
        using var writer = new MatchArchiveWriter(tmp.Path, new StubPluginLog(), retention: () => (30, 1));
        await writer.FlushAsync();
        string legacy=Path.Combine(writer.RootDir,"match-legacy"), active=Path.Combine(writer.RootDir,"match-active");
        Directory.CreateDirectory(legacy); Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(legacy,"summary.json"),"{}");
        writer.RecordPacket(HandStartPacket(0,[25000,25000,25000,25000]));
        string first=(await writer.FinalizeSessionAsync([],Stats))!;
        writer.RecordPacket(HandStartPacket(0,[25000,25000,25000,25000]));
        string second=(await writer.FinalizeSessionAsync([],Stats))!;
        Assert.False(Directory.Exists(first)); Assert.True(Directory.Exists(second));
        Assert.True(Directory.Exists(legacy)); Assert.True(Directory.Exists(active));
    }

    [Fact]
    public async Task Unwritable_archive_does_not_throw_into_the_game_thread()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path,"match-archives"),"occupied by file");
        using var writer = new MatchArchiveWriter(tmp.Path,new StubPluginLog());
        writer.RecordPacket(HandStartPacket(0,[25000,25000,25000,25000]));
        await writer.FinalizeSessionAsync([],Stats);
        await writer.FlushAsync();
    }

    private static CapturedMahjongPacket HandStartPacket(int selfSeat, int[] relativeScores)
    {
        var payload = new byte[48];
        BitConverter.GetBytes(selfSeat).CopyTo(payload, 24);
        for (int relativeSeat = 0; relativeSeat < relativeScores.Length; relativeSeat++)
        {
            int absoluteSeat = (relativeSeat + selfSeat) % relativeScores.Length;
            BitConverter.GetBytes(relativeScores[relativeSeat] / 100).CopyTo(payload, 32 + absoluteSeat * 4);
        }
        return new CapturedMahjongPacket(
            MahjongPacketMjaiDecoder.HandStartMessageId,
            0x018e,
            DateTimeOffset.UtcNow,
            payload);
    }

    private static JsonElement ReadSummary(string archive)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(archive, "summary.json")));
        return document.RootElement.Clone();
    }
}

using System.Text.Json;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game.Variants;

namespace Mahjong.Plugin.Dalamud.Tests.Replay;

public class NegativeScoreSettlementTests
{
    [Fact]
    public async Task Captured_final_screen_survives_reader_logger_and_archive_with_outstanding_riichi()
    {
        using var tmp = new TempDir();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "RegressionFixtures", "20260924-negative-score-settlement.json")));
        var profile = JsonLayoutProfileLoader.Load(Path.Combine(TestPaths.LayoutsDir, "emj.json"));
        var variant = new BaseEmjVariant(profile, new StubPluginLog(), tmp.Path);
        var ctx = new VariantReadContext(new MeldTracker(), EventLogger: null);
        using var logger = new GameLogger(new DalamudConfigService(_ => { }, new Configuration()),
            new StubPluginLog(), tmp.Path);
        DateTimeOffset? previous = null;
        foreach (var row in fixture.RootElement.GetProperty("states").EnumerateArray())
        {
            var time = DateTimeOffset.Parse(row.GetProperty("t").GetString()!);
            Assert.True(previous is null || time > previous);
            previous = time;
            var scores = Ints(row.GetProperty("scores"));
            var counts = Ints(row.GetProperty("discard_counts"));
            var memory = new AddonMemoryBuilder(profile)
                .WithScores(scores[0], scores[1], scores[2], scores[3])
                .WithDiscardCounts(counts[0], counts[1], counts[2], counts[3])
                .WithHand(string.Concat(Ints(row.GetProperty("hand")).Select(id => Tile.FromId(id).ShortName)))
                .Build();
            var snapshot = variant.BuildSnapshotFromMemory(memory,
                [AtkValueRecord.OfInt(row.GetProperty("state_code").GetInt32())], ctx, false);
            Assert.NotNull(snapshot);
            logger.OnStateChanged(snapshot);
        }
        logger.CompleteSession();
        logger.CompleteSession(); // Leaving twice cannot settle twice.
        await logger.FlushAsync();
        var paths = logger.SnapshotSessionPaths();
        var path = Assert.Single(paths);
        var lines = File.ReadAllLines(path);
        Assert.DoesNotContain(lines, line => line.Contains("hand-incomplete"));
        using var end = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"e\":\"hand-end\"")));
        Assert.Equal(new[] { -1000, 2000, -1000, -1000 }, Ints(end.RootElement.GetProperty("deltas")));
        Assert.Equal("unknown", end.RootElement.GetProperty("kind").GetString());
        Assert.True(end.RootElement.GetProperty("result_inferred").GetBoolean());
        using var writer = new MatchArchiveWriter(tmp.Path, new StubPluginLog());
        var archive = Assert.IsType<string>(await writer.FinalizeSessionAsync(paths,
            new MatchArchiveMortalStats("UI-only", 0, 0, 0, 0, 0, 0, 0, 0)));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(archive, "summary.json")));
        Assert.Equal(1, summary.RootElement.GetProperty("hand_count").GetInt32());
        Assert.Equal(1, summary.RootElement.GetProperty("settled_hands").GetInt32());
        Assert.Equal(new[] { -800, 37000, 39400, 23400 }, Ints(summary.RootElement.GetProperty("final_scores")));
        Assert.Equal(4, summary.RootElement.GetProperty("our_rank").GetInt32());
        Assert.False(summary.RootElement.GetProperty("packet_write_failed").GetBoolean());
    }

    private static int[] Ints(JsonElement array) => array.EnumerateArray().Select(x => x.GetInt32()).ToArray();
}

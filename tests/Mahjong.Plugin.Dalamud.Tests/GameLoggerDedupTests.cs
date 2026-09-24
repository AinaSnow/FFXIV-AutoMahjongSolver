using Mahjong.Core;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests;

public class GameLoggerDedupTests
{
    private static readonly int[] StartScores = [25000, 25000, 25000, 25000];

    private static StateSnapshot SampleSnap(
        int wallRemaining,
        int handCount = 14,
        int[]? scores = null,
        bool firstTileRed = false)
    {
        var seats = new SeatView[4];
        for (int i = 0; i < 4; i++)
            seats[i] = new SeatView(
                Discards: Array.Empty<Tile>(),
                DiscardIsTedashi: Array.Empty<bool>(),
                Melds: Array.Empty<Meld>(),
                Riichi: false,
                RiichiDiscardIndex: -1,
                Ippatsu: false,
                IsTenpaiCalled: false);
        var hand = new Tile[handCount];
        var handIsRed = new bool[handCount];
        for (int i = 0; i < handCount; i++)
            hand[i] = Tile.FromId((i + 4) % Tile.Count34);
        if (handCount > 0)
            handIsRed[0] = firstTileRed;
        return StateSnapshot.Empty with
        {
            WallRemaining = wallRemaining,
            Seats = seats,
            Hand = hand,
            HandIsRed = handIsRed,
            Observations = handCount > 0
                ? SnapshotObservationFlags.HandRedIdentity
                : SnapshotObservationFlags.None,
            Scores = scores ?? StartScores,
        };
    }

    [Fact]
    public async Task Identical_snapshots_collapse_to_a_single_state_line()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        var snap = SampleSnap(70);
        for (int i = 0; i < 1000; i++)
            logger.OnStateChanged(snap);

        await logger.FlushAsync();
        var files = Directory.GetFiles(logger.GamesDir, "game-*.ndjson");
        Assert.Single(files);
        var lines = File.ReadAllLines(files[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"e\":\"hand-start\"", lines[0]);
        Assert.Contains("\"e\":\"state\"", lines[1]);
    }

    [Fact]
    public async Task Distinct_snapshots_emit_distinct_state_lines()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        for (int w = 70; w >= 66; w--)
            logger.OnStateChanged(SampleSnap(w));

        await logger.FlushAsync();
        var files = Directory.GetFiles(logger.GamesDir, "game-*.ndjson");
        Assert.Single(files);
        var lines = File.ReadAllLines(files[0]);
        Assert.Equal(6, lines.Length);
    }

    [Fact]
    public async Task Red_identity_change_emits_a_distinct_state_line()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        logger.OnStateChanged(SampleSnap(70, handCount: 14));
        logger.OnStateChanged(SampleSnap(70, handCount: 14, firstTileRed: true));

        await logger.FlushAsync();
        var file = Assert.Single(Directory.GetFiles(logger.GamesDir, "game-*.ndjson"));
        var lines = File.ReadAllLines(file);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"obs\":2", lines[^1]);
        Assert.Contains("\"hand_red\":[true,false", lines[^1]);
    }

    // Regression: MaybeRollHand must reject upward wall jumps at mid-hand counts so transient reads don't produce truncated files.
    [Fact]
    public async Task Wall_jump_with_mid_hand_count_does_not_roll_new_file()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        logger.OnStateChanged(SampleSnap(70, handCount: 14));
        logger.OnStateChanged(SampleSnap(40, handCount: 14));
        logger.OnStateChanged(SampleSnap(34, handCount: 6));
        logger.OnStateChanged(SampleSnap(40, handCount: 6));
        logger.OnStateChanged(SampleSnap(70, handCount: 14));

        await logger.FlushAsync();
        var files = Directory.GetFiles(logger.GamesDir, "game-*.ndjson");
        Assert.Equal(2, files.Length);
    }

    // Settlement belongs to the prior hand; empty transition frames cannot open a new file.
    [Fact]
    public async Task Hand_roll_settles_prior_file_before_starting_next_hand()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        logger.OnStateChanged(SampleSnap(70, handCount: 14, scores: [25000, 25000, 25000, 25000]));
        logger.OnStateChanged(SampleSnap(40, handCount: 14, scores: [25000, 25000, 25000, 25000]));
        logger.OnStateChanged(SampleSnap(70, handCount: 14, scores: [33000, 23000, 21000, 23000]));

        await logger.FlushAsync();
        var files = Directory.GetFiles(logger.GamesDir, "game-*.ndjson").OrderBy(p => p).ToArray();
        Assert.Equal(2, files.Length);

        var hand1 = File.ReadAllLines(files[0]);
        Assert.Contains("\"e\":\"hand-start\"", hand1[0]);
        var end = hand1[^1];
        Assert.Contains("\"e\":\"hand-end\"", end);

        var hand2 = File.ReadAllLines(files[1]);
        Assert.Contains("\"e\":\"hand-end\"", end);
        Assert.Contains("\"kind\":\"tsumo\"", end);
        Assert.Contains("\"winner\":0", end);
        Assert.Contains("\"deltas\":[8000,-2000,-4000,-2000]", end);
        Assert.Contains("\"scores_after\":[33000,23000,21000,23000]", end);
        Assert.Contains("\"e\":\"hand-start\"", hand2[0]);
    }

    [Fact]
    public async Task Session_reset_restarts_hand_sequence_without_cross_game_hand_end()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        logger.OnStateChanged(SampleSnap(
            70, handCount: 14, scores: [28900, 21400, 29900, 19800]));
        logger.OnStateChanged(SampleSnap(
            40, handCount: 14, scores: [28900, 21400, 29900, 19800]));

        Assert.Single(logger.SnapshotSessionPaths());

        logger.ResetSession();

        Assert.Equal(0, logger.HandSeq);
        Assert.Null(logger.CurrentPath);
        Assert.Empty(logger.SnapshotSessionPaths());

        logger.OnStateChanged(SampleSnap(
            70, handCount: 14, scores: [25000, 25000, 25000, 25000]));

        Assert.Equal(1, logger.HandSeq);
        await logger.FlushAsync();
        var lines = Directory.GetFiles(logger.GamesDir, "game-*.ndjson")
            .SelectMany(File.ReadAllLines)
            .ToArray();
        Assert.Equal(2, lines.Count(line => line.Contains("\"e\":\"hand-start\"")));
        Assert.DoesNotContain(lines, line => line.Contains("\"e\":\"hand-end\""));
    }

    // Regression: a deferred roll must not consume the wall-jump signal, so the next deal-shape tick still triggers the roll.
    [Fact]
    public async Task Hand_roll_defers_when_wall_jumps_but_hand_is_mid_deal()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        logger.OnStateChanged(SampleSnap(70, handCount: 14));
        logger.OnStateChanged(SampleSnap(40, handCount: 14));
        logger.OnStateChanged(SampleSnap(70, handCount: 7));
        logger.OnStateChanged(SampleSnap(70, handCount: 14));

        await logger.FlushAsync();
        var files = Directory.GetFiles(logger.GamesDir, "game-*.ndjson");
        Assert.Equal(2, files.Length);
    }

    [Theory]
    [InlineData(new[] { 8000, -2000, -4000, -2000 }, "tsumo", 0, (object?)null)]
    [InlineData(new[] { 0, 5200, 0, -5200 }, "ron", 1, 3)]
    [InlineData(new[] { 1500, -1500, 1500, -1500 }, "draw", (object?)null, (object?)null)]
    [InlineData(new[] { 0, 0, 0, 0 }, "draw", (object?)null, (object?)null)]
    [InlineData(new[] { -1000, -3000, 4000, 0 }, "unknown", (object?)null, (object?)null)]
    public void InferResultKind_classifies_delta_shapes(int[] deltas, string expectedKind, object? expectedWinner, object? expectedLoser)
    {
        var (kind, winner, loser) = GameLogger.InferResultKind(deltas);
        Assert.Equal(expectedKind, kind);
        Assert.Equal((int?)expectedWinner, winner);
        Assert.Equal((int?)expectedLoser, loser);
    }

    [Fact]
    public async Task EnableGameLogging_off_drops_all_writes()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration { EnableGameLogging = false });
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);

        for (int i = 0; i < 50; i++)
            logger.OnStateChanged(SampleSnap(70 - i));

        await logger.FlushAsync();
        var files = Directory.GetFiles(logger.GamesDir, "game-*.ndjson");
        Assert.Empty(files);
    }

    [Fact]
    public void Mortal_auto_mode_suppresses_preliminary_local_policy_decision()
    {
        var config = new Configuration
        {
            MortalEnabled = true,
            AutomationArmed = true,
            SuggestionOnly = false,
        };

        Assert.False(GameLogger.ShouldRecordPolicyDecision(config));
    }

    [Fact]
    public async Task RecordDecision_writes_final_source_and_choice()
    {
        using var tmp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration());
        using var logger = new GameLogger(config, new StubPluginLog(), tmp.Path);
        logger.OnStateChanged(SampleSnap(70, handCount: 14));

        logger.RecordDecision(
            ActionChoice.Discard(Tile.FromId(8), "Mortal"),
            source: "discard");

        await logger.FlushAsync();
        var file = Assert.Single(Directory.GetFiles(logger.GamesDir, "game-*.ndjson"));
        var decision = Assert.Single(File.ReadAllLines(file),
            line => line.Contains("\"e\":\"decision\""));
        Assert.Contains("\"source\":\"discard\"", decision);
        Assert.Contains("\"kind\":\"Discard\"", decision);
        Assert.Contains("\"tile\":8", decision);
        Assert.Contains("\"why\":\"Mortal\"", decision);
    }
}

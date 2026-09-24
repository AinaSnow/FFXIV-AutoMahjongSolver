using System.Text.Json;

namespace Mahjong.Plugin.Game.Tests;

public class LiveDomanRiichiTests
{
    [Fact]
    public void Captured_declarations_keep_initial_discard_and_handover_subsequent_draws()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "RegressionFixtures", "20260924-riichi-ui.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        var tracker = new DomanTurnTracker();
        StateSnapshot? latest = null;
        int declarations = 0, automaticTurns = 0, winPrompts = 0;
        int? baseline = null; long hand = -1, at = -1; bool committed = false;
        foreach (var row in fixture.RootElement.GetProperty("events").EnumerateArray())
        {
            long nextHand = row.GetProperty("hand_id").GetInt64();
            if (nextHand != hand) { hand = nextHand; at = -1; baseline = null; committed = false; }
            long time = row.GetProperty("at_ms").GetInt64();
            Assert.True(time >= at); at = time;
            if (row.GetProperty("type").GetString() == "declaration")
            {
                Assert.NotNull(latest);
                tracker.DeclarationDispatched(latest);
                baseline = latest.Us.DiscardCount;
                Assert.False(tracker.Apply(latest).OurRiichi);
                declarations++;
                continue;
            }
            int count = row.GetProperty("discards").GetInt32();
            var seats = StateSnapshot.Empty.Seats.ToArray();
            seats[0] = seats[0] with { DiscardCount = count };
            var flags = Enum.Parse<ActionFlags>(row.GetProperty("legal").GetString()!);
            latest = StateSnapshot.Empty with
            {
                HandId = hand, Seats = seats,
                Hand = row.GetProperty("hand").EnumerateArray().Select(t => Tile.FromId(t.GetInt32())).ToArray(),
                Legal = new(flags, [], [], [], []),
            };
            var result = tracker.Observe(latest);
            committed |= baseline is { } start && count > start;
            Assert.Equal(committed, result.OurRiichi);
            if (committed)
            {
                Assert.False(result.Legal.Can(ActionFlags.Riichi | ActionFlags.Discard));
                if (flags.HasFlag(ActionFlags.Discard)) automaticTurns++;
            }
            else Assert.Equal(flags, result.Legal.Flags);
            var wins = flags & (ActionFlags.Tsumo | ActionFlags.Ron);
            Assert.Equal(wins, result.Legal.Flags & (ActionFlags.Tsumo | ActionFlags.Ron));
            if (wins != 0) winPrompts++;
        }
        Assert.Equal(3, declarations);
        Assert.True(automaticTurns > 0);
        Assert.True(winPrompts > 0);
    }
}

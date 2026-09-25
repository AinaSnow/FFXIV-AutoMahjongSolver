using System.Text.Json;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class RiichiDiscardResolverTests
{
    private const int Base = 76001;

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Draw_slot_stays_thirteen_with_any_number_of_closed_kans(int kans)
    {
        var raw = new int[14];
        int count = 14 - 3 * kans;
        for (int i = 0; i < count - 1; i++) raw[i] = Base + i;
        raw[13] = Base + 35; // red 5p in the draw slot, past empty slots after kan.
        var state = Snapshot(raw, kans);
        Assert.True(RiichiDiscardResolver.TryResolve(state, raw, Base, null, null,
            out var tile, out int slot, out bool red, out var reason), reason);
        Assert.Equal(Tile.FromId(13), tile); Assert.True(red); Assert.Equal(13, slot);
        Assert.Equal(count, state.Hand.Count);
    }

    [Theory]
    [InlineData(false, 3)] [InlineData(true, 4)]
    public void Eleven_tile_riichi_retains_the_latched_five_identity(bool red, int expectedSlot)
    {
        // Anonymous equivalent of the observed 11-tile / 1-kan declaration.
        int[] raw = [Base+6, Base+7, Base+8, Base+13, Base+35, Base+14,
            Base+23, Base+24, Base+28, Base+28, 0, 0, 0, Base+25];
        var state = Snapshot(raw, 1);
        Assert.True(RiichiDiscardResolver.TryResolve(state, raw, Base, Tile.FromId(13), red,
            out var tile, out int slot, out bool actualRed, out var reason), reason);
        Assert.Equal(expectedSlot, slot); Assert.Equal(red, actualRed); Assert.Equal(Tile.FromId(13), tile);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void Missing_draw_stale_red_forbidden_tile_or_changed_state_prevents_input(int scenario)
    {
        int[] raw = [Base, Base+1, Base+2, Base+13, Base+35, Base+14,
            Base+23, Base+24, Base+28, Base+28, 0, 0, 0, Base+25];
        var state = Snapshot(raw, 1);
        Tile? target = Tile.FromId(13); bool? red = false;
        if (scenario == 0) { raw[13] = 0; state = Snapshot(raw, 1); } // supplementary draw has not arrived
        if (scenario == 1) raw[3] = Base + 35; // normal five has changed to red
        if (scenario == 2) state = state with { Legal = state.Legal with { DiscardRestrictionKnown = true } };
        if (scenario == 3) state = state with { OurRiichi = true };
        if (scenario == 4) state = state with { OurMelds = [Meld.Pon(Tile.FromId(20), Tile.FromId(20), 1)] };
        if (scenario == 5) state = state with { PublicStateConsistent = false };
        if (scenario == 6) state = state with { Legal = LegalActions.None };
        if (scenario == 7) { raw[10] = raw[13]; raw[13] = 0; target = null; red = null; } // no observed draw slot
        Assert.False(RiichiDiscardResolver.TryResolve(state, raw, Base, target, red, out _, out _, out _, out _));
    }

    [Fact]
    public async Task Rejected_follow_up_is_logged_and_counted_once_in_archive()
    {
        using var temp = new TempDir();
        var config = new DalamudConfigService(_ => { }, new Configuration { EnableGameLogging = true });
        using var logger = new GameLogger(config, new StubPluginLog(), temp.Path);
        int[] raw = [Base, Base+1, Base+2, Base+13, Base+35, Base+14,
            Base+23, Base+24, Base+28, Base+28, 0, 0, 0, Base+25];
        var state = Snapshot(raw, 1) with { HandId = 5, Revision = 12 };
        logger.OnStateChanged(state);
        using (logger.BeginDispatch(state))
            logger.RecordActionAborted(ActionKind.Discard, Tile.FromId(13), "riichi-tsumogiri", "stale red identity", false);
        await logger.FlushAsync();
        string path = Assert.IsType<string>(logger.CurrentPath);
        using var writer = new MatchArchiveWriter(temp.Path, new StubPluginLog());
        var stats = new MatchArchiveMortalStats("test",0,0,0,0,0,0,0,0);
        string archive = Assert.IsType<string>(await writer.FinalizeSessionAsync([path], stats));
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(archive, "summary.json")));
        Assert.Equal(1, report.RootElement.GetProperty("failed_actions").GetInt32());
        var health = report.RootElement.GetProperty("execution_health");
        Assert.Equal(1, health.GetProperty("outcomes").GetProperty("aborted").GetInt32());
        Assert.Equal(0, health.GetProperty("missing_outcomes").GetInt32());
        using var action = JsonDocument.Parse(File.ReadLines(path).Single(line => line.Contains("\"e\":\"action\"")));
        Assert.Equal(5, action.RootElement.GetProperty("hand_id").GetInt64());
        Assert.False(action.RootElement.GetProperty("is_red").GetBoolean());
    }

    private static StateSnapshot Snapshot(int[] raw, int kans)
    {
        var decoded = HandArrayDecoder.ReadHandDetailed(raw, Base);
        return StateSnapshot.Empty with
        {
            Hand = decoded.Tiles, HandIsRed = decoded.IsRed, Observations = SnapshotObservationFlags.HandRedIdentity,
            OurMelds = Enumerable.Range(0, kans).Select(i => Meld.AnKan(Tile.FromId(20+i))).ToArray(),
            Legal = new LegalActions(ActionFlags.Discard | ActionFlags.Riichi | ActionFlags.Pass, [], [], [], []),
            AddonStateCode = 6,
        };
    }
}

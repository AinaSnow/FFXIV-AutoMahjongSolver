using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.Mortal;
using Mahjong.Plugin.Game.Mjai;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.Dalamud.Tests;

public sealed class MortalBridgeTests
{
    [Theory]
    [InlineData(0x0129, 636, 48)]
    [InlineData(0x018e, 637, 104)]
    [InlineData(0x00b0, 638, 24)]
    [InlineData(0x0273, 639, 256)]
    [InlineData(0x039c, 640, 504)]
    [InlineData(0x0156, 641, 32)]
    public void Capture_maps_only_confirmed_payloads(ushort opcode, int messageId, int payloadLength)
    {
        Assert.True(MahjongNetworkCapture.TryGetPacketSpec(opcode, out int actualId, out int actualLength));
        Assert.Equal(messageId, actualId);
        Assert.Equal(payloadLength, actualLength);
    }

    [Fact]
    public void Capture_does_not_map_roster_opcode()
    {
        Assert.False(MahjongNetworkCapture.TryGetPacketSpec(0x01eb, out _, out _));
    }

    [Fact]
    public void Replay_payload_disables_actions_without_changing_the_event()
    {
        const string source = "{\"type\":\"tsumo\",\"actor\":0,\"pai\":\"5mr\"}";

        string replay = MortalProcessClient.CreateReplayPayload(source);

        Assert.Equal(
            "{\"type\":\"tsumo\",\"actor\":0,\"pai\":\"5mr\",\"can_act\":false}",
            replay);
    }

    [Fact]
    public void Dahai_maps_to_a_legal_discard()
    {
        var fiveSou = Tile.FromId(22);
        var snapshot = Snapshot(
            hand: [fiveSou],
            legal: new LegalActions(ActionFlags.Discard, [fiveSou], [], [], []));
        var reaction = new MortalReaction("dahai", 0, null, "5sr", []);

        Assert.True(LiveMortalBridge.TryMapDecision(reaction, snapshot, out var choice));
        Assert.Equal(ActionKind.Discard, choice.Kind);
        Assert.Equal(fiveSou, choice.DiscardTile);
    }

    [Fact]
    public void Dahai_preserves_red_identity_for_slot_selection()
    {
        var fiveMan = Tile.FromId(4);
        var snapshot = Snapshot(
            hand: [fiveMan, fiveMan],
            legal: new LegalActions(ActionFlags.Discard, [fiveMan], [], [], [])) with
        {
            HandIsRed = [true, false],
        };
        var reaction = new MortalReaction("dahai", 0, null, "5mr", []);

        Assert.True(LiveMortalBridge.TryMapDecision(
            reaction, snapshot, out var choice, out bool? isRed));
        Assert.True(isRed);
        Assert.Equal(
            0,
            InputDispatcher.FindSlotOfTile(
                choice.DiscardTile!.Value,
                snapshot.Hand,
                snapshot.HandIsRed,
                isRed));
        Assert.Equal(
            1,
            InputDispatcher.FindSlotOfTile(
                choice.DiscardTile.Value,
                snapshot.Hand,
                snapshot.HandIsRed,
                targetIsRed: false));
    }

    [Fact]
    public void Reach_maps_to_the_discard_selected_by_the_second_reaction()
    {
        var onePin = Tile.FromId(9);
        var snapshot = Snapshot(
            hand: [onePin],
            legal: new LegalActions(ActionFlags.Discard | ActionFlags.Riichi, [onePin], [], [], []));
        var reaction = new MortalReaction("riichi", 0, null, "1p", []);

        Assert.True(LiveMortalBridge.TryMapDecision(reaction, snapshot, out var choice));
        Assert.Equal(ActionKind.Riichi, choice.Kind);
        Assert.Equal(onePin, choice.DiscardTile);
    }

    [Fact]
    public void Chi_uses_the_matching_ui_candidate()
    {
        var four = Tile.FromId(21);
        var five = Tile.FromId(22);
        var six = Tile.FromId(23);
        var candidate = new MeldCandidate(MeldKind.Chi, four, [five, six], 3);
        var snapshot = Snapshot(
            hand: [five, six],
            legal: new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [candidate], []));
        var reaction = new MortalReaction("chi", 0, 3, "4s", ["5sr", "6s"]);

        Assert.True(LiveMortalBridge.TryMapDecision(reaction, snapshot, out var choice));
        Assert.Equal(ActionKind.Chi, choice.Kind);
        Assert.Equal(candidate, choice.Call);
    }

    [Fact]
    public void Illegal_or_stale_discard_is_rejected()
    {
        var snapshot = Snapshot(
            hand: [Tile.FromId(0)],
            legal: new LegalActions(ActionFlags.Discard, [Tile.FromId(0)], [], [], []));
        var reaction = new MortalReaction("dahai", 0, null, "9m", []);

        Assert.False(LiveMortalBridge.TryMapDecision(reaction, snapshot, out _));
    }

    [Fact]
    public void Kakan_matches_the_single_added_tile_ui_candidate()
    {
        var east = Tile.FromId(27);
        var candidate = new MeldCandidate(MeldKind.ShouMinKan, east, [east], -1);
        var snapshot = Snapshot(
            hand: [east],
            legal: new LegalActions(ActionFlags.ShouMinKan, [], [], [], [candidate]));
        var reaction = new MortalReaction("kakan", 0, null, "E", ["E", "E", "E"]);

        Assert.True(LiveMortalBridge.TryMapDecision(reaction, snapshot, out var choice));
        Assert.Equal(ActionKind.ShouMinKan, choice.Kind);
        Assert.Equal(candidate, choice.Call);
    }

    [Fact]
    public void Unknown_start_tile_is_repaired_from_matching_ui_hand_with_red_identity()
    {
        string[] packetHand =
            ["2m", "3m", "?", "3p", "3s", "7s", "8s", "9s", "W", "P", "F", "C", "2s"];
        var start = new MjaiStartKyoku(
            "E", "7m", 1, 0, 0, 0,
            [25000, 25000, 25000, 25000],
            [packetHand, UnknownHand(), UnknownHand(), UnknownHand()]);
        var hand = new[]
        {
            Tile.FromId(1), Tile.FromId(2), Tile.FromId(13), Tile.FromId(11),
            Tile.FromId(20), Tile.FromId(24), Tile.FromId(25), Tile.FromId(26),
            Tile.FromId(29), Tile.FromId(31), Tile.FromId(32), Tile.FromId(33),
            Tile.FromId(19),
        };
        var snapshot = Snapshot(hand, LegalActions.None) with
        {
            HandIsRed = [false, false, true, false, false, false, false, false, false, false, false, false, false],
            Observations = SnapshotObservationFlags.HandRedIdentity,
        };

        Assert.True(LiveMortalBridge.TryRepairStartKyoku(start, snapshot, out var repaired));
        Assert.Contains("5pr", repaired.Tehais[0]);
        Assert.True(LiveMortalBridge.IsSafeStartKyoku(repaired));
    }

    [Fact]
    public void Unknown_start_tile_rejects_stale_ui_hand()
    {
        var start = new MjaiStartKyoku(
            "E", "7m", 1, 0, 0, 0,
            [25000, 25000, 25000, 25000],
            [["1m", "?", "3m", "4m", "5m", "6m", "7m", "8m", "9m", "1p", "2p", "3p", "4p"],
             UnknownHand(), UnknownHand(), UnknownHand()]);
        var stale = Snapshot(Enumerable.Repeat(Tile.FromId(33), 13).ToArray(), LegalActions.None) with
        {
            Observations = SnapshotObservationFlags.HandRedIdentity,
        };

        Assert.False(LiveMortalBridge.TryRepairStartKyoku(start, stale, out _));
    }

    [Fact]
    public void Unknown_public_discard_is_blocked_before_journaling()
    {
        Assert.False(LiveMortalBridge.IsSafeLiveEvent(new MjaiDahai(2, MjaiTile.Unknown, false)));
    }

    [Fact]
    public void Concealed_opponent_draw_remains_a_safe_unknown()
    {
        Assert.True(LiveMortalBridge.IsSafeLiveEvent(new MjaiTsumo(2, MjaiTile.Unknown)));
    }

    [Fact]
    public void Open_call_with_an_unknown_consumed_tile_is_blocked()
    {
        Assert.False(LiveMortalBridge.IsSafeLiveEvent(
            new MjaiOpenCall("pon", 0, 2, "5p", ["5p", MjaiTile.Unknown])));
    }

    [Fact]
    public void Network_discard_replaces_a_wrong_chi_candidate()
    {
        var oneSou = Tile.FromId(18);
        var twoSou = Tile.FromId(19);
        var threeSou = Tile.FromId(20);
        var sixSou = Tile.FromId(23);
        var sevenSou = Tile.FromId(24);
        var eightSou = Tile.FromId(25);
        var wrong = new MeldCandidate(MeldKind.Chi, oneSou, [twoSou, threeSou], 3);
        var snapshot = Snapshot(
            [twoSou, threeSou, sixSou, sevenSou],
            new LegalActions(ActionFlags.Chi | ActionFlags.Pass, [], [], [wrong], []));

        var normalized = LiveMortalBridge.NormalizeCallCandidates(
            snapshot,
            new MjaiDahai(3, "8s", false),
            out bool corrected);

        Assert.True(corrected);
        var candidate = Assert.Single(normalized.Legal.ChiCandidates);
        Assert.Equal(eightSou, candidate.ClaimedTile);
        Assert.Equal([sixSou, sevenSou], candidate.HandTiles);
        Assert.Equal(3, candidate.FromSeat);
    }

    [Fact]
    public void Network_discard_corrects_pon_source_seat()
    {
        var eightMan = Tile.FromId(7);
        var wrong = new MeldCandidate(MeldKind.Pon, eightMan, [eightMan, eightMan], 1);
        var snapshot = Snapshot(
            [eightMan, eightMan],
            new LegalActions(ActionFlags.Pon | ActionFlags.Pass, [], [wrong], [], []));

        var normalized = LiveMortalBridge.NormalizeCallCandidates(
            snapshot,
            new MjaiDahai(2, "8m", false),
            out bool corrected);

        Assert.True(corrected);
        Assert.Equal(2, Assert.Single(normalized.Legal.PonCandidates).FromSeat);
    }

    [Fact]
    public void Missing_discard_requires_complete_observed_river_data()
    {
        var eightMan = Tile.FromId(7);
        var incomplete = Snapshot(
            [eightMan, eightMan],
            new LegalActions(ActionFlags.Pon | ActionFlags.Pass, [], [], [], []));
        Assert.False(LiveMortalBridge.TryGetObservedDiscard(
            incomplete, actor: 2, expectedDiscardCount: 1, out _, out _));

        var seats = incomplete.Seats.ToArray();
        seats[2] = seats[2] with
        {
            Discards = [eightMan],
            DiscardIsRed = [true],
            DiscardCount = 1,
        };
        var observed = incomplete with
        {
            Seats = seats,
            Observations = SnapshotObservationFlags.PublicDiscardTiles |
                SnapshotObservationFlags.PublicDiscardRedIdentity,
        };

        Assert.True(LiveMortalBridge.TryGetObservedDiscard(
            observed, actor: 2, expectedDiscardCount: 1, out var claimed, out bool isRed));
        Assert.Equal(eightMan, claimed);
        Assert.True(isRed);

        Assert.False(LiveMortalBridge.TryGetObservedDiscard(
            observed, actor: 2, expectedDiscardCount: 2, out _, out _));
    }

    private static StateSnapshot Snapshot(IReadOnlyList<Tile> hand, LegalActions legal) =>
        StateSnapshot.Empty with
        {
            Hand = hand,
            HandIsRed = new bool[hand.Count],
            Legal = legal,
        };

    private static string[] UnknownHand() => Enumerable.Repeat(MjaiTile.Unknown, 13).ToArray();
}

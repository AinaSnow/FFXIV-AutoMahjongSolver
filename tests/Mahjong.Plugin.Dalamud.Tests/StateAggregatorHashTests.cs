using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;

namespace Mahjong.Plugin.Dalamud.Tests;

public class StateAggregatorHashTests
{
    [Fact]
    public void Candidate_identity_changes_content_hash()
    {
        var first = SnapshotWithPon(claimedTileId: 4);
        var second = SnapshotWithPon(claimedTileId: 5);

        Assert.NotEqual(
            StateAggregator.ComputeContentHash(first),
            StateAggregator.ComputeContentHash(second));
    }

    private static StateSnapshot SnapshotWithPon(int claimedTileId)
    {
        var claimed = Tile.FromId(claimedTileId);
        var candidate = new MeldCandidate(
            MeldKind.Pon,
            claimed,
            [claimed, claimed],
            FromSeat: 1);

        return StateSnapshot.Empty with
        {
            Hand = Tiles.Parse("55m123p456s11234z"),
            Legal = new LegalActions(
                ActionFlags.Pon | ActionFlags.Pass,
                [],
                [candidate],
                [],
                []),
        };
    }
}

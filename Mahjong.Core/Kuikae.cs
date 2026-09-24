namespace Mahjong.Core;

/// <summary>Doman forbids replacing the claimed tile or swapping the ends of a chi.</summary>
public static class Kuikae
{
    public static IReadOnlyList<Tile> ForbiddenDiscards(MeldCandidate call)
    {
        if (call.Kind == MeldKind.Pon) return [call.ClaimedTile];
        if (call.Kind != MeldKind.Chi) return [];
        var result = new HashSet<Tile> { call.ClaimedTile };
        var consumed = call.HandTiles.OrderBy(t => t.Id).ToArray();
        if (consumed.Length == 2 && consumed[0].Suit != TileSuit.Honor
            && consumed[0].Suit == consumed[1].Suit && consumed[1].Id == consumed[0].Id + 1)
        {
            // Both possible ends are forbidden, independent of which end was claimed.
            if (consumed[0].Id % 9 > 0) result.Add(Tile.FromId(consumed[0].Id - 1));
            if (consumed[1].Id % 9 < 8) result.Add(Tile.FromId(consumed[1].Id + 1));
        }
        return result.OrderBy(t => t.Id).ToArray();
    }

    public static IReadOnlyList<Tile> ForbiddenDiscards(Meld meld)
    {
        if (meld.ClaimedTile is not { } claimed) return [];
        var consumed = meld.Tiles.ToList();
        if (!consumed.Remove(claimed)) return [];
        return ForbiddenDiscards(new MeldCandidate(meld.Kind, claimed, consumed.ToArray(), meld.ClaimedFromSeat));
    }
}

using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState.Variants;

namespace Mahjong.Plugin.Dalamud.Actions;

/// <summary>Validates the current hand and resolves the declaration discard to an actual addon slot.</summary>
internal static class RiichiDiscardResolver
{
    internal static bool TryResolve(StateSnapshot? snapshot, ReadOnlySpan<int> rawSlots, int textureBase,
        Tile? latchedTile, bool? latchedRed, out Tile tile, out int slot, out bool isRed, out string reason)
    {
        tile = default; slot = -1; isRed = false;
        reason = "waiting for a complete closed hand after draw/kan";
        if (snapshot is null || !snapshot.PublicStateConsistent || snapshot.OurRiichi
            || snapshot.OurMelds.Count > 4 || snapshot.OurMelds.Any(m => m.Kind != MeldKind.AnKan)
            || snapshot.Hand.Count + snapshot.OurMelds.Count * 3 != 14)
            return false;
        reason = "declaration discard is no longer offered";
        if (!snapshot.Legal.Can(ActionFlags.Discard | ActionFlags.Riichi)
            || snapshot.Legal.Can(ActionFlags.Ron | ActionFlags.Tsumo))
            return false;

        int effectiveBase = HandArrayDecoder.ResolveTextureBase(rawSlots, textureBase, out _);
        var decoded = HandArrayDecoder.ReadHandDetailed(rawSlots, effectiveBase);
        reason = "addon slots no longer match the observed hand/red identity";
        // Compare full arrays before resolving a slot. A transient empty/shifted
        // slot must not turn the chosen ordinary five into its red counterpart.
        if (!decoded.Tiles.OrderBy(t => t.Id).SequenceEqual(snapshot.Hand.OrderBy(t => t.Id)))
            return false;
        if (snapshot.Observations.HasFlag(SnapshotObservationFlags.HandRedIdentity)
            && (snapshot.HandIsRed.Count != snapshot.Hand.Count
                || !decoded.Tiles.Select((t, i) => (t.Id, decoded.IsRed[i])).Order()
                    .SequenceEqual(snapshot.Hand.Select((t, i) => (t.Id, snapshot.HandIsRed[i])).Order())))
            return false;

        if (latchedTile is { } target)
        {
            slot = HandArrayDecoder.FindAddonSlot(rawSlots, effectiveBase, target.Id, latchedRed);
            reason = "latched declaration tile/red identity is not in the current addon hand";
            if (slot < 0) return false;
        }
        else
        {
            // The UI draw slot stays 13 even with 11/8/5/2 concealed tiles.
            // Compact snapshot index 13 does not exist after a kan.
            reason = "drawn tile slot is absent";
            if (rawSlots.Length <= 13 || HandArrayDecoder.DecodeTileId(rawSlots[13], effectiveBase, out _) < 0)
                return false;
            slot = 13;
        }
        int id = HandArrayDecoder.DecodeTileId(rawSlots[slot], effectiveBase, out isRed);
        tile = Tile.FromId(id);
        reason = "selected declaration discard is forbidden";
        if (!snapshot.Legal.AllowsDiscard(tile)) return false;
        reason = "";
        return true;
    }
}

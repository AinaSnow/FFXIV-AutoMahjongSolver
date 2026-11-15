namespace Mahjong.Plugin.Dalamud.Actions;

/// <summary>ContentFinderCondition columns; <see cref="Name"/> is localized and only logged.</summary>
internal readonly record struct CollectionDutyRow(uint RowId, uint TerritoryType, uint Content, uint ContentType, string? Name = null);

/// <summary>Pure gates for continuous collection; the adapter supplies sheet and table facts.</summary>
internal static class CollectionGate
{
    // ContentType 19 is the Gold Saucer content type read from the 2026.09.15 sheet (docs/continuous-collection.md).
    internal const uint ContentType = 19;

    /// <summary>
    /// The duty row must still link the target territory, content and content type. These are numeric
    /// sheet columns, identical in every client language; the localized name is never compared, so a
    /// shifted or reused row id fails closed regardless of language.
    /// </summary>
    internal static bool IsTargetDutyRow(CollectionDutyRow? row) =>
        row is { } r && r.RowId == ContinuousCollectionLoop.DutyId && r.TerritoryType == ContinuousCollectionLoop.TerritoryId
        && r.Content == ContinuousCollectionLoop.ContentId && r.ContentType == ContentType;

    /// <summary>
    /// The table's addon variant must be the one the loaded layout resolved for this table, and this build
    /// must have a bundled or learned protocol profile for exactly that variant.
    /// </summary>
    internal static bool IsCollectableVariant(string? variant, string? activeLayoutAddonName, bool variantHasProfile) =>
        !string.IsNullOrEmpty(variant) && activeLayoutAddonName == variant && variantHasProfile;
}

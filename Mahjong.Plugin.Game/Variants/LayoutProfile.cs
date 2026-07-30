namespace Mahjong.Plugin.Game.Variants;

/// <summary>
/// Variant constants loaded from <c>data/layouts/*.json</c>. Adding a new variant is one JSON
/// file — never a code change.
/// </summary>
public sealed record LayoutProfile(
    string Name,
    string AddonName,
    int TileTextureBase,
    LayoutOffsets Offsets,
    LayoutNodeIds NodeIds,
    LayoutAtkValueIndices AtkValues,
    LayoutStateCodes StateCodes,
    LayoutSanityLimits Limits,
    string[]? ClientLanguages = null,
    LayoutActionLabels? ActionLabels = null);

/// <summary>
/// Seat stride ~0x2E0. Per-seat discard array offsets are optional — null leaves
/// <see cref="Mahjong.Core.SeatView.Discards"/> empty.
/// </summary>
public sealed record LayoutOffsets(
    int SelfScore,
    int ShimochaScore,
    int ToimenScore,
    int KamichaScore,
    int SelfDiscardCountByte,
    int ShimochaDiscardCountByte,
    int ToimenDiscardCountByte,
    int KamichaDiscardCountByte,
    int HandArrayStart,
    int DoraIndicator,
    int? SelfDiscardArray = null,
    int? ShimochaDiscardArray = null,
    int? ToimenDiscardArray = null,
    int? KamichaDiscardArray = null,
    int DiscardArrayMaxLen = 24);

public sealed record LayoutNodeIds(
    uint CallModalHost,
    uint CallModalShell,
    uint MeldContainer = 0,
    uint NextButton = 97,
    uint NextButtonCollision = 4,
    int NextButtonEventParam = 7);

/// <summary>Scan-window fields are per-variant because EmjL places claim slots differently than Emj.</summary>
public sealed record LayoutAtkValueIndices(
    int StateCode,
    int WallCount,
    int ChiClaimedTile,
    int PonClaimScanLo = 16,
    int PonClaimScanHi = 21,
    int ChiFallbackScanLimit = 30,
    int ButtonLabelScanLimit = 20);

public sealed record LayoutStateCodes(
    int OurTurnDiscard,
    int CallPrompt,
    int CallPromptList,
    int SelfDeclareList,
    int PostDrawIdle,
    int ChiVariantSelect = 25,
    int HandResult = 29);

public sealed record LayoutSanityLimits(
    int HandSize,
    int WallInitial,
    int ScoreSanityMax,
    int DiscardCountSanityMax,
    int MaxAkadoraSlots);

/// <summary>
/// Localized action labels exposed by the addon. Profiles can override these
/// lists when a client uses text that is not covered by the built-in aliases.
/// </summary>
public sealed record LayoutActionLabels
{
    public string[] Pon { get; init; } = ["Pon", "ポン", "碰"];
    public string[] Chi { get; init; } = ["Chi", "チー", "吃"];
    public string[] Kan { get; init; } = ["Kan", "カン", "杠", "槓"];
    public string[] Ron { get; init; } = ["Ron", "ロン", "荣和", "榮和"];
    public string[] Riichi { get; init; } = ["Riichi", "リーチ", "立直"];
    public string[] Tsumo { get; init; } = ["Tsumo", "ツモ", "自摸"];
    public string[] Pass { get; init; } = ["Pass", "パス", "过", "過"];

    public static LayoutActionLabels Default { get; } = new();
}

public static class LayoutActionLabelMatcher
{
    public static bool IsPon(string value, LayoutActionLabels labels) => Matches(value, labels.Pon);
    public static bool IsChi(string value, LayoutActionLabels labels) => Matches(value, labels.Chi);
    public static bool IsKan(string value, LayoutActionLabels labels) => Matches(value, labels.Kan);
    public static bool IsRon(string value, LayoutActionLabels labels) => Matches(value, labels.Ron);
    public static bool IsRiichi(string value, LayoutActionLabels labels) => Matches(value, labels.Riichi);
    public static bool IsTsumo(string value, LayoutActionLabels labels) => Matches(value, labels.Tsumo);
    public static bool IsPass(string value, LayoutActionLabels labels) => Matches(value, labels.Pass);

    public static bool IsAcceptAction(string value, LayoutActionLabels labels) =>
        IsPon(value, labels) || IsChi(value, labels) || IsKan(value, labels) ||
        IsRon(value, labels) || IsRiichi(value, labels) || IsTsumo(value, labels);

    private static bool Matches(string value, IReadOnlyList<string>? aliases)
    {
        if (aliases is null)
            return false;
        var normalized = value.Trim();
        foreach (var alias in aliases)
        {
            if (string.Equals(normalized, alias, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

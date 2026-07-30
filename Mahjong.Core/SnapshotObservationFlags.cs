namespace Mahjong.Core;

/// <summary>
/// Declares which optional public-state fields were observed rather than synthesized.
/// Consumers such as an mjai bridge must require the fields they need instead of treating
/// placeholder defaults as real table state.
/// </summary>
[Flags]
public enum SnapshotObservationFlags : ushort
{
    None = 0,
    SeatInfo = 1 << 0,
    HandRedIdentity = 1 << 1,
    PublicDiscardTiles = 1 << 2,
    PublicDiscardRedIdentity = 1 << 3,
    PublicTedashi = 1 << 4,
    OpponentMelds = 1 << 5,
    OpponentRiichi = 1 << 6,
}

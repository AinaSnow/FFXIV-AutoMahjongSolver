namespace Mahjong.Plugin.Dalamud.GameState;

public sealed record AddonEmjObservation(
    bool Present,
    bool IsVisible,
    nint Address,
    ushort Width,
    ushort Height,
    long LastSeenUtcTicks,
    string? LastLifecycleEvent,
    string? AddonName = null)
{
    // Protocol identity follows the actual addon, not the memory-layout alias
    // selected while its hand arrays are being populated.
    public string? ProtocolVariant => Present && Address != 0 ? AddonName : null;

    public static AddonEmjObservation Empty { get; } =
        new(false, false, 0, 0, 0, 0, null);
}

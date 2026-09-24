using Mahjong.Core;

namespace Mahjong.Plugin.Game;

/// <summary>
/// Pure state for the auto-play tick loop. Invariants: <see cref="BeginDispatch"/> must
/// be paired with <see cref="CompleteDispatch"/> in a finally; <see cref="LatchRiichiConfirm"/>
/// is one-shot and self-clears on hand transition.
/// </summary>
public sealed class ActionStateMachine
{
    private readonly TimeSpan dispatchTimeout;
    private readonly TimeSpan retryCooldown;

    private bool inFlight;
    private DateTime dispatchStartedAt;
    private DateTime lastActionAt;
    private DispatchContext? lastContext;
    private bool riichiConfirmLatched;
    private Tile? riichiConfirmTile;
    private bool? riichiConfirmTileIsRed;
    private int lastObservedWall = -1;
    private int? riichiDiscardBaseline;
    private bool riichiDiscardCommitted;

    public ActionStateMachine(TimeSpan dispatchTimeout, TimeSpan retryCooldown)
    {
        this.dispatchTimeout = dispatchTimeout;
        this.retryCooldown = retryCooldown;
    }

    public bool IsDispatchInFlight => inFlight;

    /// <summary>True when the riichi-accept popup may still be visible from a previous dispatch.</summary>
    public bool IsRiichiConfirmPending => riichiConfirmLatched;

    /// <summary>
    /// Tile chosen for the declaration discard; cleared when our river count advances.
    /// Null also covers probe-accept without a tile; later draws use the drawn tile.
    /// </summary>
    public Tile? RiichiConfirmTile => riichiConfirmTile;

    /// <summary>Red identity paired with <see cref="RiichiConfirmTile"/> when the decision source distinguishes tile copies.</summary>
    public bool? RiichiConfirmTileIsRed => riichiConfirmTileIsRed;

    public bool TryRecoverFromStuckDispatch(DateTime now)
    {
        if (!inFlight)
            return false;
        if (now - dispatchStartedAt <= dispatchTimeout)
            return false;
        inFlight = false;
        return true;
    }

    public void BeginDispatch(DateTime now, DispatchContext context)
    {
        inFlight = true;
        dispatchStartedAt = now;
        lastActionAt = now;
        lastContext = context;
    }

    public void CompleteDispatch() => inFlight = false;

    public bool ShouldSuppressForContext(DispatchContext context, DateTime now)
        => lastContext.HasValue
           && lastContext.Value.Equals(context)
           && now - lastActionAt < retryCooldown;

    public void ClearContext() => lastContext = null;

    /// <summary>A null <paramref name="target"/> preserves any previously-latched tile.</summary>
    public void LatchRiichiConfirm(Tile? target = null, bool? targetIsRed = null, int? ownDiscardCount = null)
    {
        riichiConfirmLatched = true;
        if (riichiDiscardCommitted) return;
        riichiDiscardBaseline ??= ownDiscardCount;
        if (target is not null)
        {
            riichiConfirmTile = target;
            riichiConfirmTileIsRed = targetIsRed;
        }
    }

    /// <summary>After the declaration discard lands, future draws must not reuse its tile choice.</summary>
    public void ObserveOwnDiscardCount(int count)
    {
        if (!riichiConfirmLatched || riichiDiscardBaseline is not { } baseline || count <= baseline) return;
        riichiDiscardCommitted = true;
        riichiConfirmTile = null;
        riichiConfirmTileIsRed = null;
        // Keep the hand-scoped latch: a repeated popup must not redeclare riichi.
    }

    public void ClearRiichiConfirm()
    {
        riichiConfirmLatched = false;
        riichiDiscardBaseline = null;
        riichiDiscardCommitted = false;
        riichiConfirmTile = null;
        riichiConfirmTileIsRed = null;
    }

    /// <summary>
    /// Sharp upward wall jump = new hand dealt → clear per-hand state. Tolerance 5 absorbs
    /// transient wall-read glitches.
    /// </summary>
    public void ObserveWall(int wall)
    {
        if (lastObservedWall >= 0 && wall > lastObservedWall + 5)
        {
            ClearRiichiConfirm();
            lastContext = null;
        }
        lastObservedWall = wall;
    }
}

public readonly record struct DispatchContext(int State, int Hand);

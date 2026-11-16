using Mahjong.Plugin.Game.Mjai;

namespace Mahjong.Plugin.Dalamud.Actions;

/// <summary>
/// Server-level outcome of our own discard, riichi, win and pass dispatches, written as separate
/// <c>server-outcome</c> log records. Purely observational: it never changes dispatch, retries,
/// timing or the UI outcome (<c>action-outcome</c>); calls and kans keep being reported by
/// <see cref="ServerCallOutcomeTracker"/>. The offline mirror is <c>confirm_actions</c> in
/// <c>tools/live_hand_labels.py</c>, with the same status vocabulary:
/// <list type="bullet">
/// <item><c>server-confirmed</c>: the server attributes exactly this action to us.</item>
/// <item><c>server-action-mismatch</c>: our next server action at that decision point differs.</item>
/// <item><c>server-window-closed</c>: pass only. The server moved past the offer without a call or
/// win by us. The server never acknowledges a pass, so this is consistency, not confirmation.</item>
/// <item><c>server-no-window</c>: pass clicked while no offer was open on the server (retained row).</item>
/// <item><c>server-no-effect</c>: a win, call or kan click after which the decision point closed
/// without it (confirmed and mismatched calls stay with the call tracker).</item>
/// <item><c>server-superseded</c>: another dispatch of ours resolved the same decision point.</item>
/// <item><c>server-unconfirmed</c>: the hand ended, or its result was unknown, before a deciding event.</item>
/// </list>
/// </summary>
internal sealed class ServerActionOutcomeTracker
{
    internal sealed record Outcome(long ActionId, string Label, string Status, string? Reason, string? Server);

    private enum Group { Discard, Riichi, Hora, Offer, Self }

    private sealed record Pending(long ActionId, string Label, Group Group, Tile? Tile, bool? IsRed, bool Silent);

    private readonly List<Pending> pending = [];
    // The latest window-relevant server event; it decides what a pass can mean.
    private (string Type, int Actor)? context;
    private bool reachThisTurn;

    internal void Reset()
    {
        pending.Clear();
        context = null;
        reachThisTurn = false;
    }

    /// <summary>
    /// Registers a successful dispatch. <paramref name="tile"/>/<paramref name="isRed"/> describe the
    /// tile a discard was meant to play (red identity null when it was not resolved at dispatch).
    /// Returns an immediate outcome when the server state already shows the click cannot be a
    /// server action.
    /// </summary>
    internal Outcome? Track(long actionId, string label, Tile? tile = null, bool? isRed = null)
    {
        switch (label)
        {
            case "discard" or "riichi-tsumogiri" or "oos-tsumogiri":
                pending.Add(new(actionId, label, Group.Discard, tile, isRed, false));
                return null;
            case "riichi" or "riichi-confirm":
                pending.Add(new(actionId, label, Group.Riichi, null, null, false));
                return null;
            case "ron" or "tsumo":
                pending.Add(new(actionId, label, Group.Hora, null, null, false));
                return null;
            case "pass":
                if (OpponentOffer) { pending.Add(new(actionId, label, Group.Offer, null, null, false)); return null; }
                if (OwnDraw) { pending.Add(new(actionId, label, Group.Self, null, null, false)); return null; }
                return new(actionId, label, "server-no-window",
                    $"no open offer on the server (last event: {context?.Type ?? "none"})", null);
            case "pon" or "chi" or "chi-variant" or "minkan":
                // Reported by ServerCallOutcomeTracker. Tracked silently so that a later pass on a
                // retained row is attributed as superseded rather than as a mismatch.
                if (OpponentOffer) pending.Add(new(actionId, label, Group.Offer, null, null, true));
                return null;
            case "ankan" or "shouminkan":
                if (OwnDraw) pending.Add(new(actionId, label, Group.Self, null, null, true));
                return null;
            default:
                return null;
        }
    }

    private bool OpponentOffer => context is { Type: "dahai" or "kakan", Actor: not 0 };
    private bool OwnDraw => context is { Type: "tsumo", Actor: 0 };

    /// <summary>Feeds one decoded server event; <paramref name="result"/> accompanies a result-packet end_kyoku.</summary>
    internal IReadOnlyList<Outcome> Observe(IMjaiEvent evt, ServerHandResult? result = null)
    {
        var output = new List<Outcome>();
        switch (evt)
        {
            case MjaiStartGame or MjaiStartKyoku or MjaiEndGame:
                Flush(output, "hand boundary before a deciding server event");
                context = null;
                reachThisTurn = false;
                break;
            case MjaiReach { Actor: 0 }:
                reachThisTurn = true;
                Resolve(output, Group.Riichi, "server-confirmed", null, "reach", _ => true);
                break;
            case MjaiDahai dahai:
            {
                string server = $"dahai {dahai.Pai}{(dahai.Tsumogiri ? " tsumogiri" : "")}{(dahai.Actor == 0 && reachThisTurn ? " riichi" : "")}";
                if (dahai.Actor == 0)
                {
                    Resolve(output, Group.Discard, "server-action-mismatch", "server discarded a different tile", server,
                        p => SameTile(p, dahai.Pai));
                    Resolve(output, Group.Riichi, "server-action-mismatch", "server discarded without riichi", server, _ => false);
                    Resolve(output, Group.Self, reachThisTurn ? "server-action-mismatch" : "server-window-closed",
                        reachThisTurn ? "server shows our riichi" : null, server, _ => false);
                }
                Continued(output, server, "dahai", dahai.Actor);
                reachThisTurn = false;
                context = ("dahai", dahai.Actor);
                break;
            }
            case MjaiTsumo tsumo:
                Continued(output, null, "tsumo", tsumo.Actor);
                context = ("tsumo", tsumo.Actor);
                break;
            case MjaiOpenCall or MjaiDaiminkan:
            {
                int actor = evt is MjaiOpenCall open ? open.Actor : ((MjaiDaiminkan)evt).Actor;
                string type = evt.Type;
                if (actor == 0)
                {
                    string label = type == "daiminkan" ? "minkan" : type;
                    Resolve(output, Group.Offer, "server-action-mismatch", $"server shows our {type}", type,
                        p => p.Label == label || label == "chi" && p.Label == "chi-variant");
                    Resolve(output, Group.Discard, "server-action-mismatch", $"next server action of ours is {type}", type, _ => false);
                }
                Continued(output, $"{type} by seat {actor}", type, actor);
                context = (type, actor);
                break;
            }
            case MjaiAnkan or MjaiKakan:
            {
                int actor = evt is MjaiAnkan ankan ? ankan.Actor : ((MjaiKakan)evt).Actor;
                string type = evt.Type;
                if (actor == 0)
                {
                    Resolve(output, Group.Discard, "server-action-mismatch", $"next server action of ours is {type}", type, _ => false);
                    Resolve(output, Group.Riichi, "server-action-mismatch", $"next server action of ours is {type}", type, _ => false);
                    Resolve(output, Group.Self, "server-action-mismatch", $"server shows our {type}", type,
                        p => p.Label == (type == "ankan" ? "ankan" : "shouminkan"));
                }
                Continued(output, $"{type} by seat {actor}", type, actor);
                context = (type, actor);
                break;
            }
            case MjaiEndKyoku:
                EndHand(output, result);
                context = null;
                reachThisTurn = false;
                break;
        }
        return output;
    }

    /// <summary>
    /// Any table-advancing event closes an opponent offer and resolves a pending win declaration:
    /// our own different action is a mismatch, the table simply moving on means no effect.
    /// </summary>
    private void Continued(List<Outcome> output, string? server, string type, int actor)
    {
        Resolve(output, Group.Offer, "server-window-closed", null, server ?? type, _ => false);
        Resolve(output, Group.Hora, actor == 0 ? "server-action-mismatch" : "server-no-effect",
            $"server continued with {type}", server ?? type, _ => false);
    }

    private void EndHand(List<Outcome> output, ServerHandResult? result)
    {
        bool known = result is not null && result.Outcome != "unknown";
        string server = known ? $"result {result!.Outcome} winners=[{string.Join(",", result.Winners)}]" : "result unknown";
        Resolve(output, Group.Hora, known ? "server-action-mismatch" : "server-unconfirmed",
            known ? $"result is {result!.Outcome}" : "hand result unknown", server,
            p => known && result!.WeWon && result.Outcome == p.Label);
        bool weWon = known && result!.WeWon;
        foreach (var group in new[] { Group.Offer, Group.Self })
            Resolve(output, group, !known ? "server-unconfirmed" : weWon ? "server-action-mismatch" : "server-window-closed",
                !known ? "hand result unknown" : weWon ? "server shows our win" : null, server, _ => false);
        Flush(output, "hand ended before a deciding server event");
    }

    private static bool SameTile(Pending entry, string pai)
    {
        if (entry.Tile is not { } tile || !MjaiTile.TryParse(pai, out var served, out bool servedRed))
            return false;
        // Red identity unknown at dispatch: only the kind can be compared.
        return served.Id == tile.Id && (entry.IsRed is not { } red || red == servedRed);
    }

    /// <summary>
    /// Resolves every pending entry of <paramref name="group"/>: an entry the event confirms is
    /// "server-confirmed", the rest get <paramref name="status"/>. For discards, calls and passes one
    /// server event decides one decision point: the latest confirmed entry keeps the attribution
    /// (with the chi list step that preceded a confirmed chi variant), or, when none is confirmed,
    /// only earlier repeats of the same label are superseded. Riichi and win declarations are
    /// multi-step flows (declare + confirm), so all their entries keep their own status.
    /// </summary>
    private void Resolve(List<Outcome> output, Group group, string status, string? reason, string? server,
        Func<Pending, bool> confirms)
    {
        var entries = pending.Where(p => p.Group == group).ToList();
        if (entries.Count == 0) return;
        pending.RemoveAll(p => p.Group == group);
        bool multiStep = group is Group.Riichi or Group.Hora;
        var keep = entries.LastOrDefault(confirms);
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            bool confirmed = confirms(entry);
            string entryStatus = confirmed ? "server-confirmed" : status;
            string? entryReason = confirmed ? null : reason;
            if (confirmed && group == Group.Discard && entry.IsRed is null)
                entryReason = "red identity not recorded at dispatch";
            bool chiStep = keep is { Label: "chi-variant" } && entry.Label == "chi" && confirmed;
            bool superseded = !multiStep && (keep is not null
                ? !ReferenceEquals(entry, keep) && !chiStep
                : entries.Skip(i + 1).Any(later => later.Label == entry.Label));
            if (superseded)
            {
                entryStatus = "server-superseded";
                entryReason = "another dispatch resolved the same decision point";
            }
            if (!entry.Silent)
                output.Add(new(entry.ActionId, entry.Label, entryStatus, entryReason, server));
            else if (entryStatus == "server-window-closed")
                // A call/kan click after which the decision point closed without it: the existing
                // call tracker only times out silently, so this is the one call status reported here.
                output.Add(new(entry.ActionId, entry.Label, "server-no-effect",
                    "the decision point closed without this call", server));
        }
    }

    private void Flush(List<Outcome> output, string reason)
    {
        foreach (var entry in pending.Where(p => !p.Silent))
            output.Add(new(entry.ActionId, entry.Label, "server-unconfirmed", reason, null));
        pending.Clear();
    }
}

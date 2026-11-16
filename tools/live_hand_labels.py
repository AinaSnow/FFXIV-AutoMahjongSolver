"""Hand results and our own server-observed actions from archived Doman Mahjong packets.

Offline evidence only. Reads the decoder-level message IDs that an archive's
packets.ndjson already carries (636 match start, 637 hand start, 638 draw/call,
639 tsumo result, 640 ron result, 641 discard, 643 draw result). It never
replaces the production C# decoder, never sends anything to a model, and every
field it cannot cross-check stays None/"unknown" instead of being guessed.

Seats in the output are relative to us (0 = self, 1 = shimocha, ...), the same
convention as MJAI and the UI game log. Points are full points (x100 of wire).

Result layout (2026.11.03 / 7.58, established in docs/reviews/20261116-c1-live-data-loop.md):
a winner block is 63 little-endian int32 words
  w0 winning tile kind (+0x100 red), w1 revealed concealed count, w2 limit tier,
  w3 han | riichi sticks awarded << 8 | honba << 16, w4 winner seat (4 = empty block),
  w5 hand value, w6/w7 tsumo payment by dealer / by each non-dealer (negative, 0 for ron),
  w8 fu, w9..w12 scores after this block, w13..w25 revealed tiles, w26..w41 yaku ids,
  w42..w57 yaku values, w58..w62 ura indicators.
639 (256 bytes) = one block + one zero word: tsumo. 640 (504 bytes) = two blocks: ron;
the second block is empty (w4 = 4) unless two players won on the same discard, and its
scores are cumulative. The discarder is not a field: it is the actor of the last discard
(or added kan, for robbing a kan), accepted only when the payment arithmetic agrees.
643 (264 bytes): w0 0 = exhaustive draw / 1 = abortive draw, w1, then four 16-word blocks
[score after, revealed tile count (0 = not tenpai), tiles...].
"""
from datetime import datetime, timezone
import re
import struct

MATCH_START, HAND_START, DRAW_CALL, RESULT_TSUMO, RESULT_RON, DISCARD, DRAW_RESULT = 636, 637, 638, 639, 640, 641, 643
RESULT_MESSAGES = (RESULT_TSUMO, RESULT_RON, DRAW_RESULT)
BLOCK_WORDS = 63
LABEL_VERSION = "c1-result-v1"


def physical_tile(value):
    """Return (kind, red) for a physical tile id or None for hidden/unknown."""
    if value == 0xFFFF:
        return None
    kind = value >> 2
    if not 0 <= kind < 34:
        return None
    return kind, kind in (4, 13, 22) and value & 3 == 1


def result_tile(value):
    kind = value & 0xFF
    if not 0 <= kind < 34 or value & ~0x1FF or value & 0x100 and kind not in (4, 13, 22):
        return None
    return kind, bool(value & 0x100)


def _words(payload):
    return struct.unpack("<" + "i" * (len(payload) // 4), payload)


def decode_winner_block(words):
    """Decode one 63-word winner block; None for the empty second ron block."""
    if len(words) != BLOCK_WORDS:
        raise ValueError("winner block length")
    winner = words[4]
    if winner == 4:
        if words[0] != 255 or any(t != 255 for t in words[13:26]):
            raise ValueError("empty winner block carries tiles")
        return None
    if not 0 <= winner < 4:
        raise ValueError("winner seat outside 0..3")
    tile = result_tile(words[0])
    if tile is None or not 0 <= words[1] <= 13 or words[1] % 3 != 1:
        raise ValueError("winning tile or revealed count")
    packed = words[3]
    if packed < 0 or packed >> 24:
        raise ValueError("packed han/sticks/honba")
    return dict(
        winner=winner, win_tile=tile[0], win_tile_red=tile[1], limit=words[2],
        han=packed & 0xFF, sticks=packed >> 8 & 0xFF, honba=packed >> 16 & 0xFF,
        value=words[5] * 100, dealer_pays=-words[6] * 100, other_pays=-words[7] * 100,
        fu=words[8], scores_after=[s * 100 for s in words[9:13]],
        yaku=[(k, v) for k, v in zip(words[26:42], words[42:58]) if k or v])


def decode_result(message, payload):
    """Decode a result packet. Raises ValueError on any shape this layout does not cover."""
    w = _words(payload)
    if message == RESULT_TSUMO:
        if len(payload) != 256 or w[63] != 0:
            raise ValueError("639 length/trailer")
        block = decode_winner_block(w[:63])
        if block is None or block["dealer_pays"] < 0 or block["other_pays"] <= 0:
            raise ValueError("639 without a tsumo payment")
        return dict(outcome="tsumo", blocks=[block], scores_after=block["scores_after"])
    if message == RESULT_RON:
        if len(payload) != 504:
            raise ValueError("640 length")
        blocks = [decode_winner_block(w[:63]), decode_winner_block(w[63:126])]
        if blocks[0] is None:
            raise ValueError("640 without a first winner")
        blocks = [b for b in blocks if b is not None]
        if any(b["dealer_pays"] or b["other_pays"] for b in blocks):
            raise ValueError("640 carries tsumo payments")
        if len({b["winner"] for b in blocks}) != len(blocks):
            raise ValueError("640 repeats a winner")
        return dict(outcome="ron", blocks=blocks, scores_after=blocks[-1]["scores_after"])
    if message == DRAW_RESULT:
        if len(payload) != 264 or w[0] not in (0, 1):
            raise ValueError("643 length/draw type")
        seats = [w[2 + 16 * k:18 + 16 * k] for k in range(4)]
        if any(not 0 <= s[1] <= 13 for s in seats):
            raise ValueError("643 revealed count")
        return dict(outcome="exhaustive_draw" if w[0] == 0 else "abortive_draw",
                    draw_type=w[0], draw_aux=w[1], tenpai=[s[1] > 0 for s in seats],
                    scores_after=[s[0] * 100 for s in seats], blocks=[])
    raise ValueError("not a result message")


class _Hand:
    def __init__(self, t, words):
        self.start_t = t
        self.round_wind, self.kyoku, self.honba = words[1], words[2], words[3]
        self.self_seat = words[6]
        self.start_abs = [s * 100 for s in words[8:12]]
        self.events = []
        self.riichi = set()
        self.last_turn = None  # (kind, abs actor, tile or None)
        self.last_riichi_discard = None  # abs actor when the latest event is a riichi discard
        self.result = None
        self.result_t = None
        self.result_error = None
        self.decode_error = None
        self.next_start = None

    def rel(self, seat):
        return (seat - self.self_seat) % 4


def _parse_rows(rows):
    hands = []
    current = None
    for row in rows:
        message = row.get("message_id")
        payload_hex = row.get("payload_hex")
        if message not in (MATCH_START, HAND_START, DRAW_CALL, DISCARD, *RESULT_MESSAGES):
            continue
        try:
            payload = bytes.fromhex(payload_hex)
        except (TypeError, ValueError):
            if current is not None and current.decode_error is None:
                current.decode_error = "malformed payload"
            continue
        t = row.get("t")
        if message == MATCH_START:
            current = None
            continue
        if message == HAND_START:
            if len(payload) < 100:
                continue
            w = _words(payload[:100])
            if not 0 <= w[6] < 4:
                continue
            hand = _Hand(t, w)
            if current is not None and current.result is not None:
                # Next-start scores in our relative seats, for the previous hand's continuity check.
                current.next_start = [hand.start_abs[(r + hand.self_seat) % 4] for r in range(4)]
            hands.append(hand)
            current = hand
            continue
        if current is None or current.result is not None or current.result_error is not None:
            continue
        if message in RESULT_MESSAGES:
            current.result_t = t
            try:
                current.result = decode_result(message, payload)
            except (ValueError, struct.error) as error:
                current.result_error = str(error)
            continue
        if len(payload) < 24:
            current.decode_error = current.decode_error or "short payload"
            continue
        seat = struct.unpack_from("<i", payload, 0)[0]
        if not 0 <= seat < 4:
            current.decode_error = current.decode_error or "seat outside 0..3"
            continue
        actor = current.rel(seat)
        if message == DRAW_CALL:
            action = struct.unpack_from("<I", payload, 4)[0]
            tiles = [physical_tile(v) for v in struct.unpack_from("<6H", payload, 8)]
            if action in (0x100, 0x200):
                tile = tiles[0] if actor == 0 else None
                current.events.append(dict(t=t, type="tsumo", actor=actor, tile=tile, rinshan=action == 0x200))
                current.last_turn = ("tsumo", seat, tile)
                current.last_riichi_discard = None
            elif action in (0x400, 0x500, 0x600):
                kind = {0x400: "daiminkan", 0x500: "pon", 0x600: "chi"}[action]
                consumed = tiles[2:5] if action == 0x400 else tiles[2:4]
                target = current.last_turn[1] if current.last_turn and current.last_turn[0] == "dahai" else None
                current.events.append(dict(t=t, type=kind, actor=actor, consumed=consumed,
                                           target=None if target is None else current.rel(target)))
                current.last_turn = ("call", seat, None)
                current.last_riichi_discard = None
            elif action == 0x130:
                current.events.append(dict(t=t, type="ankan", actor=actor, consumed=tiles[2:6]))
                current.last_turn = ("ankan", seat, None)
                current.last_riichi_discard = None
            else:
                current.decode_error = current.decode_error or f"draw/call 0x{action:X}"
                current.events.append(dict(t=t, type="unknown", actor=actor, code=action))
        else:
            action = struct.unpack_from("<I", payload, 8)[0]
            tiles = [physical_tile(v) for v in struct.unpack_from("<6H", payload, 12)]
            if action in (0x140, 0x240):
                current.events.append(dict(t=t, type="kakan", actor=actor, tile=tiles[2]))
                current.last_turn = ("kakan", seat, tiles[2])
                current.last_riichi_discard = None
            elif action in (0x110, 0x111, 0x112, 0x113, 0x210, 0x211, 0x212, 0x213, 0xA10):
                riichi = action in (0x111, 0x113, 0x211, 0x213)
                if riichi:
                    current.riichi.add(actor)
                current.events.append(dict(t=t, type="dahai", actor=actor, tile=tiles[0],
                                           tsumogiri=action in (0x112, 0x113, 0x212, 0x213),
                                           riichi=riichi, code=action))
                current.last_turn = ("dahai", seat, tiles[0])
                current.last_riichi_discard = seat if riichi else None
            else:
                current.decode_error = current.decode_error or f"discard 0x{action:X}"
                current.events.append(dict(t=t, type="unknown", actor=actor, code=action))
    return hands


def _deposits_abs(hand):
    seats = {(r + hand.self_seat) % 4 for r in hand.riichi}
    if hand.result and hand.result["outcome"] == "ron" and hand.last_riichi_discard is not None:
        seats.discard(hand.last_riichi_discard)
    return seats


def _expected_win_deltas(hand, blocks, discarder_abs):
    """Full-point per-absolute-seat deltas implied by the winner blocks, or None."""
    deltas = [0] * 4
    winners = {block["winner"] for block in blocks}
    # A winner's own deposit is returned and is not counted in the awarded sticks;
    # a riichi discard that is itself ronned never becomes a deposit.
    for seat in _deposits_abs(hand) - winners:
        deltas[seat] -= 1000
    for index, block in enumerate(blocks):
        winner = block["winner"]
        honba = block["honba"] * 300
        deltas[winner] += block["value"] + honba + block["sticks"] * 1000
        if discarder_abs is None:
            for payer in range(4):
                if payer != winner:
                    each = block["dealer_pays"] if payer == 0 else block["other_pays"]
                    deltas[payer] -= each + block["honba"] * 100
        else:
            deltas[discarder_abs] -= block["value"] + honba
    return deltas


def _count(hand, types):
    counts = [0] * 4
    for event in hand.events:
        if event["type"] in types:
            counts[event["actor"]] += 1
    return counts


def label_hand(hand):
    """One per-hand result label. Never guesses: any disagreement yields outcome 'unknown'."""
    label = dict(label_version=LABEL_VERSION, outcome="unknown", winners=[], discarder=None,
                 multi_winner=False, deltas=None, tenpai=None, riichi=sorted(hand.riichi),
                 honba=hand.honba, kyoku=hand.kyoku + 1, round_wind=hand.round_wind,
                 dealer=hand.rel(0), winner_details=[], checks={}, unknown_reason=None,
                 open_calls=_count(hand, ("chi", "pon", "daiminkan")), closed_kans=_count(hand, ("ankan", "kakan")),
                 discards=_count(hand, ("dahai",)))
    if hand.result is None:
        label["unknown_reason"] = hand.result_error or "no result packet"
        return label
    result = hand.result
    start = hand.start_abs
    after = result["scores_after"]
    deltas_abs = [after[s] - start[s] for s in range(4)]
    label["deltas"] = [deltas_abs[(r + hand.self_seat) % 4] for r in range(4)]
    checks = label["checks"]
    checks["next_start_scores"] = None if hand.next_start is None else \
        hand.next_start == [after[(r + hand.self_seat) % 4] for r in range(4)]
    reasons = []
    if hand.decode_error:
        reasons.append("hand history: " + hand.decode_error)
    if checks["next_start_scores"] is False:
        reasons.append("scores after result disagree with the next hand start")
    if result["outcome"] in ("exhaustive_draw", "abortive_draw"):
        label["tenpai"] = [result["tenpai"][(r + hand.self_seat) % 4] for r in range(4)]
        expected = [0] * 4
        if result["outcome"] == "exhaustive_draw":
            count = sum(result["tenpai"])
            if count in (1, 2, 3):
                for seat in range(4):
                    expected[seat] = 3000 // count if result["tenpai"][seat] else -3000 // (4 - count)
        for r in hand.riichi:
            expected[(r + hand.self_seat) % 4] -= 1000
        checks["payment_arithmetic"] = expected == deltas_abs
        if result["outcome"] == "exhaustive_draw":
            checks["riichi_players_tenpai"] = all(result["tenpai"][(r + hand.self_seat) % 4] for r in hand.riichi)
            if not checks["riichi_players_tenpai"]:
                reasons.append("a riichi player is shown not tenpai")
        if not checks["payment_arithmetic"]:
            reasons.append("draw payments disagree with tenpai flags (nagashi or unknown draw rule)")
        if result["outcome"] == "abortive_draw":
            # The only 7.58 sample reveals just the player who had drawn (consistent with
            # nine terminals); the abortive kind itself is not a field and stays unknown.
            revealed = {seat for seat in range(4) if result["tenpai"][seat]}
            last_drawer = {hand.last_turn[1]} if hand.last_turn and hand.last_turn[0] == "tsumo" else set()
            checks["revealed_only_by_last_drawer"] = revealed <= last_drawer
            if not checks["revealed_only_by_last_drawer"]:
                reasons.append("abortive draw reveals a hand other than the last drawer's")
            label["tenpai"] = None
        if not reasons:
            label["outcome"] = result["outcome"]
        else:
            label["unknown_reason"] = "; ".join(reasons)
        return label
    blocks = result["blocks"]
    label["winners"] = [hand.rel(b["winner"]) for b in blocks]
    label["multi_winner"] = len(blocks) > 1
    label["winner_details"] = [dict(seat=hand.rel(b["winner"]), han=b["han"], fu=b["fu"], limit=b["limit"],
                                    value=b["value"], win_tile=b["win_tile"], win_tile_red=b["win_tile_red"],
                                    honba=b["honba"], sticks=b["sticks"]) for b in blocks]
    last = hand.last_turn
    checks["honba"] = all(b["honba"] == hand.honba for b in blocks[:1])
    if result["outcome"] == "tsumo":
        block = blocks[0]
        checks["winner_is_last_drawer"] = bool(last and last[0] == "tsumo" and last[1] == block["winner"])
        if last and last[0] == "tsumo" and last[2] is not None:
            checks["win_tile_is_drawn_tile"] = last[2] == (block["win_tile"], block["win_tile_red"])
        expected = _expected_win_deltas(hand, blocks, None)
        discarder_abs = None
    else:
        discarded = last and last[0] in ("dahai", "kakan")
        discarder_abs = last[1] if discarded else None
        checks["discarder_from_last_discard"] = discarder_abs is not None and all(
            b["winner"] != discarder_abs for b in blocks)
        if discarded and last[2] is not None:
            checks["win_tile_is_discarded_tile"] = all(
                last[2] == (b["win_tile"], b["win_tile_red"]) for b in blocks)
        expected = _expected_win_deltas(hand, blocks, discarder_abs) if discarder_abs is not None else None
    checks["payment_arithmetic"] = expected == deltas_abs
    if not checks["honba"]:
        reasons.append("result honba disagrees with hand start")
    for name in ("winner_is_last_drawer", "discarder_from_last_discard", "win_tile_is_drawn_tile",
                 "win_tile_is_discarded_tile", "payment_arithmetic"):
        if checks.get(name) is False:
            reasons.append(name.replace("_", " ") + " failed")
    if reasons:
        label["unknown_reason"] = "; ".join(reasons)
        label["winners"], label["winner_details"], label["multi_winner"] = [], [], False
        return label
    label["outcome"] = result["outcome"]
    label["discarder"] = None if discarder_abs is None else hand.rel(discarder_abs)
    return label


def decode_hands(rows):
    """Split archived packet rows into hands with server events and conservative result labels."""
    hands = _parse_rows(rows)
    output = []
    for hand in hands:
        output.append(dict(start_t=hand.start_t, result_t=hand.result_t, self_seat=hand.self_seat,
                           start_scores=[hand.start_abs[(r + hand.self_seat) % 4] for r in range(4)],
                           events=hand.events, label=label_hand(hand)))
    return output


# ---------------------------------------------------------------------------
# Server-level outcome of our own dispatched actions (offline, from archived packets).
#
# server-confirmed       the server attributes exactly this action to us (dahai with the same
#                        tile kind and red identity, reach, the same call/kan type, a result
#                        that names us as the winner of the same kind)
# server-action-mismatch the server's next action of ours at that decision point differs
# server-window-closed   pass only: the server moved past the offer without a call/win by us.
#                        The server never acknowledges a pass, so this is consistency, not
#                        confirmation (a higher-priority call or the table timer looks the same)
# server-no-window       pass/call clicked while no offer was open on the server (retained or
#                        expired UI row); the click could not be a server action
# server-no-effect       call/kan/win click; the decision point closed without that action happening
# server-superseded      another dispatch of ours resolved the same decision point
# server-unconfirmed     packets exist but no deciding server event (hand ended first, result unknown)
# no-server-evidence     the match has no archived packets
# not-applicable         not a game action (result-screen "next") or the dispatch itself failed
SERVER_STATUSES = ("server-confirmed", "server-action-mismatch", "server-window-closed", "server-no-window",
                   "server-no-effect", "server-superseded", "server-unconfirmed", "no-server-evidence",
                   "not-applicable")
DISCARD_LABELS = ("discard", "riichi-tsumogiri", "oos-tsumogiri")
RIICHI_LABELS = ("riichi", "riichi-confirm")
CALL_TYPES = {"Pon": "pon", "Chi": "chi", "MinKan": "daiminkan", "AnKan": "ankan", "ShouMinKan": "kakan"}
SELF_TURN_TYPES = ("dahai", "ankan", "kakan", "pon", "chi", "daiminkan")
_TIME = re.compile(r"(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)")


def ticks(value):
    """UTC ISO-8601 (at most 7 fractional digits, Z or +00:00) to comparable 100 ns ticks, or None."""
    match = _TIME.fullmatch(str(value or ""))
    if not match:
        return None
    whole = datetime(*map(int, match.groups()[:6]), tzinfo=timezone.utc)
    return int(whole.timestamp()) * 10_000_000 + int((match[7] or "").ljust(7, "0"))


def _server_event(event):
    if event["type"] == "result":
        label = event["label"]
        return dict(type="result", outcome=label["outcome"], winners=label["winners"], discarder=label["discarder"])
    out = dict(type=event["type"], actor=event["actor"])
    tile = event.get("tile")
    if event["type"] == "ankan" and event.get("consumed"):
        tile = event["consumed"][0]
    if tile is not None:
        out.update(tile=tile[0], red=tile[1])
    for key in ("tsumogiri", "riichi"):
        if key in event:
            out[key] = event[key]
    return out


def _timeline(hand):
    events = [dict(e, _t=ticks(e["t"])) for e in hand["events"]]
    if hand["result_t"] is not None:
        events.append(dict(type="result", actor=None, label=hand["label"], _t=ticks(hand["result_t"])))
    return events


def _first(events, start, predicate):
    return next((index for index in range(start, len(events)) if predicate(events[index])), None)


def _ours(event):
    return event["type"] == "result" or event["actor"] == 0 and event["type"] in SELF_TURN_TYPES


def _we_won(event):
    return event["type"] == "result" and 0 in event["label"]["winners"]


def _unknown_result(event):
    return event["type"] == "result" and event["label"]["outcome"] == "unknown"


def _evaluate(action, events, after):
    """Return (status, reason, resolving event index or None, retry group) for one dispatched action."""
    kind, label = action["kind"], action.get("label")
    context = events[after - 1] if after > 0 else None
    opponent_offer = context is not None and context["type"] in ("dahai", "kakan") and context["actor"] != 0
    own_draw = context is not None and context["type"] == "tsumo" and context["actor"] == 0
    if label in DISCARD_LABELS or kind == "Discard":
        index = _first(events, after, _ours)
        if index is None:
            return "server-unconfirmed", "no later server event of ours", None, "discard"
        event = events[index]
        if event["type"] != "dahai":
            return ("server-unconfirmed" if event["type"] == "result" else "server-action-mismatch",
                    "next server action of ours is " + event["type"], index, "discard")
        if event["tile"] is None:
            return "server-unconfirmed", "server discard tile unknown", index, "discard"
        tile_ok = event["tile"][0] == action.get("tile")
        red_known = type(action.get("is_red")) is bool
        if tile_ok and (not red_known or event["tile"][1] == action["is_red"]):
            return "server-confirmed", None if red_known else "red identity not recorded at dispatch", index, "discard"
        return ("server-action-mismatch", "server discarded a different tile" if not tile_ok
                else "server discarded the other red identity", index, "discard")
    if kind == "Riichi" or label in RIICHI_LABELS:
        index = _first(events, after, _ours)
        if index is None or events[index]["type"] == "result":
            return "server-unconfirmed", "no later server discard of ours", index, "riichi"
        event = events[index]
        if event["type"] == "dahai" and event["riichi"]:
            return "server-confirmed", None, index, "riichi"
        return "server-action-mismatch", "next server action of ours is a non-riichi " + event["type"], index, "riichi"
    if kind in ("Ron", "Tsumo"):
        index = _first(events, after, lambda e: True)
        if index is None:
            return "server-unconfirmed", "no later server event", None, "hora"
        event = events[index]
        if event["type"] != "result":
            # Our own different action is a mismatch; the table simply moving on means the
            # declaration had no effect (e.g. a click on a stale win label).
            return ("server-action-mismatch" if event["actor"] == 0 else "server-no-effect",
                    "server continued with " + event["type"], index, "hora")
        if _unknown_result(event):
            return "server-unconfirmed", "hand result unknown", index, "hora"
        if _we_won(event) and event["label"]["outcome"] == kind.lower():
            return "server-confirmed", None, index, "hora"
        return "server-action-mismatch", "result is " + event["label"]["outcome"], index, "hora"
    if kind == "Pass":
        if opponent_offer:
            index = _first(events, after, lambda e: True)
            if index is None:
                return "server-unconfirmed", "no later server event", None, "response"
            event = events[index]
            if event["type"] in ("pon", "chi", "daiminkan") and event["actor"] == 0 or _we_won(event):
                return "server-action-mismatch", "server shows our " + event["type"], index, "response"
            if _unknown_result(event):
                return "server-unconfirmed", "hand result unknown", index, "response"
            return "server-window-closed", None, index, "response"
        if own_draw:
            index = _first(events, after, _ours)
            if index is None:
                return "server-unconfirmed", "no later server event of ours", None, "self"
            event = events[index]
            if _unknown_result(event):
                return "server-unconfirmed", "hand result unknown", index, "self"
            if event["type"] == "dahai" and not event["riichi"] or event["type"] == "result" and not _we_won(event):
                return "server-window-closed", None, index, "self"
            return ("server-action-mismatch", "server shows our " +
                    ("riichi" if event["type"] == "dahai" else event["type"]), index, "self")
        return ("server-no-window", "no open offer on the server (last event: " +
                (context["type"] if context else "hand start") + ")", None, "response")
    if kind in CALL_TYPES:
        expected = CALL_TYPES[kind]
        if expected in ("ankan", "kakan"):
            if not own_draw:
                return "server-no-window", "no own draw open on the server", None, "self"
            index = _first(events, after, _ours)
            if index is None:
                return "server-unconfirmed", "no later server event of ours", None, "self"
            event = events[index]
            if event["type"] == expected:
                consumed = event.get("consumed") or [None]
                if expected == "ankan" and action.get("tile") is not None and consumed[0] \
                        and consumed[0][0] != action["tile"]:
                    return "server-action-mismatch", "server kan of a different tile", index, "self"
                return "server-confirmed", None, index, "self"
            if event["type"] == "dahai":
                return "server-no-effect", "server shows a discard instead of the kan", index, "self"
            return "server-action-mismatch", "server shows " + event["type"], index, "self"
        if not opponent_offer:
            return "server-no-window", "no open offer on the server", None, "response"
        index = _first(events, after, lambda e: True)
        if index is None:
            return "server-unconfirmed", "no later server event", None, "response"
        event = events[index]
        if event["actor"] == 0 and event["type"] == expected:
            return "server-confirmed", None, index, "response"
        if event["actor"] == 0 and event["type"] in ("pon", "chi", "daiminkan") or _we_won(event):
            return "server-action-mismatch", "server shows our " + event["type"], index, "response"
        if _unknown_result(event):
            return "server-unconfirmed", "hand result unknown", index, "response"
        return "server-no-effect", "offer closed by " + event["type"], index, "response"
    return "server-unconfirmed", "unsupported action kind " + str(kind), None, "other"


def hand_index_for(hands, t):
    """Index of the server hand whose start precedes UTC time t (the hand an action belongs to), or None."""
    value, owner = ticks(t), None
    for number, hand in enumerate(hands):
        start = ticks(hand["start_t"])
        if value is not None and start is not None and start <= value:
            owner = number
    return owner


def confirm_actions(hands, actions):
    """Match dispatched actions to decoded server events. Returns {action_id: outcome dict}.

    actions: dicts with action_id, t, kind, tile, is_red, result and the first outcome label.
    hands: decode_hands() output (empty when the match has no archived packets).
    """
    results = {}
    timelines = [(_timeline(h), ticks(h["start_t"]), ticks(h["result_t"]) if h["result_t"] else None) for h in hands]
    evaluated = []
    for action in actions:
        aid = action["action_id"]
        if action.get("label") == "hand-result-next" or action.get("result") != "Ok":
            reason = "not a game action" if action.get("result") == "Ok" else "dispatch returned " + str(action.get("result"))
            results[aid] = dict(status="not-applicable", reason=reason, server_event=None)
            continue
        if not hands:
            results[aid] = dict(status="no-server-evidence", reason="no archived packets", server_event=None)
            continue
        t = ticks(action.get("t"))
        owner = hand_index_for(hands, action.get("t"))
        if owner is None:
            results[aid] = dict(status="server-unconfirmed", reason="before the first server hand", server_event=None)
            continue
        events, _, end = timelines[owner]
        if end is not None and t > end:
            status = "server-no-window" if action["kind"] == "Pass" else "server-unconfirmed"
            results[aid] = dict(status=status, reason="after the hand result", server_event=None)
            continue
        after = sum(1 for e in events if e["_t"] is not None and e["_t"] <= t)
        status, reason, index, group = _evaluate(action, events, after)
        results[aid] = dict(status=status, reason=reason,
                            server_event=_server_event(events[index]) if index is not None else None)
        if index is not None:
            evaluated.append((owner, index, group, t, aid))
    # Retries: one server event resolves a decision point once. When it confirms one of
    # several discard/call/pass dispatches, the latest confirmed one keeps the attribution and
    # the others are superseded (clicks that did not decide the outcome, e.g. a pass on a
    # retained row after our accepted pon). When it confirms none, only earlier repeats of the
    # same kind are superseded; a different kind keeps its own failure status (e.g. a kan click
    # that had no effect followed by a pass). Riichi and win declarations are multi-step flows
    # (declare + confirm), so every step keeps its own status.
    by_event = {}
    for owner, index, group, t, aid in evaluated:
        by_event.setdefault((owner, index, group), []).append((t, aid))
    kinds = {action["action_id"]: action["kind"] for action in actions}
    labels = {action["action_id"]: action.get("label") for action in actions}
    for (_, _, group), entries in by_event.items():
        if group in ("riichi", "hora") or len(entries) < 2:
            continue
        ordered = [aid for _, aid in sorted(entries)]
        confirmed = [aid for aid in ordered if results[aid]["status"] == "server-confirmed"]
        if confirmed:
            keep = {confirmed[-1]}
            if labels.get(confirmed[-1]) == "chi-variant":
                # Opening the chi variant list and choosing the variant are two steps of one call.
                keep.update(aid for aid in confirmed if labels.get(aid) == "chi")
            superseded = [aid for aid in ordered if aid not in keep]
        else:
            superseded = [aid for position, aid in enumerate(ordered)
                          if any(kinds[later] == kinds[aid] for later in ordered[position + 1:])]
        for aid in superseded:
            results[aid] = dict(results[aid], status="server-superseded",
                                reason="another dispatch resolved the same decision point")
    return results

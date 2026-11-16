"""Read-only per-seat outcome readout from live hand labels (self vs the three opponents pooled).

A sanity check that the C1 hand labels are usable for C2 opponent-pool calibration; it is not a
calibration and changes no evaluation config. Input is either an export's hands.jsonl
(only rows with opponent_stats_eligible) or a directory of match archives (match-*/packets.ndjson,
decoded with live_hand_labels). Seats are relative to us; rates are per seat-hand, so opponent
rates pool three seats per hand and are not independent samples.
"""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import statistics
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from live_hand_labels import decode_hands  # noqa: E402


def wilson(successes, trials, z=1.96):
    if not trials:
        return None
    p = successes / trials
    centre = (p + z * z / (2 * trials)) / (1 + z * z / trials)
    half = z * math.sqrt(p * (1 - p) / trials + z * z / (4 * trials * trials)) / (1 + z * z / trials)
    return [round(centre - half, 4), round(centre + half, 4)]


def hands_from_export(path):
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        row = json.loads(line)
        if row.get("opponent_stats_eligible") and row.get("result"):
            yield dict(result=row["result"], riichi=row["riichi"], open_calls=row["open_calls"],
                       discards=row["discards"])


def hands_from_archives(root):
    for directory in sorted(Path(root).glob("match-*")):
        packets = directory / "packets.ndjson"
        if not packets.is_file():
            continue
        rows = [json.loads(line) for line in packets.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
        for hand in decode_hands(rows):
            label = hand["label"]
            if label["outcome"] != "unknown":
                yield dict(result=label, riichi=label["riichi"], open_calls=label["open_calls"],
                           discards=label["discards"])


def summarize(hands):
    hands = list(hands)
    seats = {"self": [0], "opponents": [1, 2, 3]}
    outcomes = Counter(h["result"]["outcome"] for h in hands)
    table = {}
    for name, members in seats.items():
        n = len(hands) * len(members)
        c = Counter()
        values, deal_in_values, win_turns = [], [], []
        for h in hands:
            result = h["result"]
            for seat in members:
                won = seat in result["winners"]
                c["wins"] += won
                if won:
                    c[result["outcome"]] += 1
                    detail = next(d for d in result["winner_details"] if d["seat"] == seat)
                    values.append(detail["value"])
                    win_turns.append(h["discards"][seat])
                if result["outcome"] == "ron" and result["discarder"] == seat:
                    c["deal_ins"] += 1
                    c["deal_ins_into_self"] += 0 in result["winners"]
                    deal_in_values.append(sum(d["value"] for d in result["winner_details"]))
                c["riichi"] += seat in h["riichi"]
                c["called"] += h["open_calls"][seat] > 0
        rate = lambda k: dict(count=c[k], rate=round(c[k] / n, 4) if n else None, ci95=wilson(c[k], n))
        table[name] = dict(
            seat_hands=n, win=rate("wins"), tsumo_wins=c["tsumo"], ron_wins=c["ron"],
            tsumo_share_of_wins=round(c["tsumo"] / c["wins"], 4) if c["wins"] else None,
            deal_in=rate("deal_ins"), riichi=rate("riichi"), open_call=rate("called"),
            mean_win_value=round(statistics.mean(values)) if values else None,
            median_winner_discards_before_win=statistics.median(win_turns) if win_turns else None,
            mean_deal_in_value=round(statistics.mean(deal_in_values)) if deal_in_values else None)
        if name == "opponents":
            table[name]["deal_ins_into_self"] = c["deal_ins_into_self"]
    total = len(hands)
    return dict(hands=total, outcomes=dict(outcomes),
                exhaustive_draw=dict(count=outcomes["exhaustive_draw"], rate=round(outcomes["exhaustive_draw"] / total, 4)
                                     if total else None, ci95=wilson(outcomes["exhaustive_draw"], total)),
                abortive_draw=outcomes["abortive_draw"],
                multi_winner_hands=sum(h["result"]["multi_winner"] for h in hands),
                seats=table,
                caveats=["Seat-hand rates; the three opponents per hand are pooled and correlated.",
                         "Only labelled hands are counted; unknown outcomes are excluded, never guessed.",
                         "East-only quick ranked matches from one account and queue; not a population estimate.",
                         "Riichi counts declarations, including a declaration discard that was ronned."])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--hands", type=Path, help="hands.jsonl from tools/training_dataset.py")
    source.add_argument("--archives", type=Path, help="directory of match-* archives with packets.ndjson")
    args = parser.parse_args()
    hands = hands_from_export(args.hands) if args.hands else hands_from_archives(args.archives)
    print(json.dumps(summarize(hands), indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()

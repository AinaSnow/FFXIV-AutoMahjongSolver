"""Independently compare captured dora markers with revealed result bonus counts.

Offline evidence only: does not decode private opponent draws, change profiles or
send result hands to a model. Uses only the first winner block of 639/640. The
production decoder/real-model replay must be checked separately for stream safety.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct


def physical(value):
    kind = value >> 2
    if not 0 <= kind < 34:
        raise ValueError("Unknown physical tile in evidence")
    return kind, int(kind in (4, 13, 22) and value & 3 == 1)


def bonus_count(tiles, indicators):
    def successor(kind):
        if kind < 27:
            return kind // 9 * 9 + (kind % 9 + 1) % 9
        return 27 + (kind - 27 + 1) % 4 if kind < 31 else 31 + (kind - 31 + 1) % 3
    return sum(sum(tile[0] == successor(marker) for tile in tiles) for marker in indicators)


def audit_packets(rows):
    markers = []
    melds = [[] for _ in range(4)]
    last = None
    results = []
    skipped_results = []
    actions = {}
    kan_count = 0
    start = None
    for row in rows:
        data = bytes.fromhex(row["payload_hex"])
        message = row["message_id"]
        words = struct.unpack("<" + "I" * (len(data) // 4), data)
        def short(offset):
            return struct.unpack_from("<H", data, offset)[0]
        if message == 637:
            markers = [words[7] & 255]
            melds = [[] for _ in range(4)]
            last = None
            kan_count = 0
            start = row.get("t")
        elif not markers:
            continue
        elif message == 638:
            actor, action = words[:2]
            if action == 0x200:
                markers.append(physical(short(10))[0])
            elif action in (0x400, 0x500, 0x600):
                if last is None:
                    raise ValueError("Missing claimed discard in evidence")
                tiles = [last[1]] + [physical(short(o)) for o in range(12, 18 if action == 0x400 else 16, 2)]
                melds[actor].append([action, tiles])
                last = None
            elif action == 0x130:
                melds[actor].append([action, [physical(short(o)) for o in range(12, 20, 2)]])
            if action in (0x400, 0x130):
                kan_count += 1
                actions[hex(action)] = actions.get(hex(action), 0) + 1
        elif message == 641:
            actor, action = words[0], words[2]
            if action in (0x140, 0x240):
                added = physical(short(16))
                pon = [m for m in melds[actor] if m[0] == 0x500 and m[1][0][0] == added[0]]
                if len(pon) != 1:
                    raise ValueError("Added kan without one matching pon in evidence")
                pon[0][0] = action
                pon[0][1].append(added)
                kan_count += 1
                actions[hex(action)] = actions.get(hex(action), 0) + 1
                last = None
            else:
                last = actor, physical(short(12))
        elif message in (639, 640):
            if words[0] == 255 and words[1] == 0:
                skipped_results.append(dict(start=start, reason="result has no revealed winning hand"))
                markers = []
                continue
            if words[1] not in (1, 4, 7, 10, 13) or words[4] >= 4:
                raise ValueError("Unrecognized first winner block")
            tiles = [(x & 255, int(bool(x & 256))) for x in [words[0], *words[13:13 + words[1]]]]
            tiles += [tile for meld in melds[words[4]] for tile in meld[1]]
            if any(tile[0] >= 34 for tile in tiles):
                raise ValueError("Unrecognized revealed tile")
            # High bits carry flags; indicators use the same low-byte kind as
            # hand start. 0xff is empty. Never treat flags as extra markers.
            ura = [x & 255 for x in words[58:63] if x & 255 < 34]
            normal, hidden, red = bonus_count(tiles, markers), bonus_count(tiles, ura), sum(t[1] for t in tiles)
            values = [v for k, v in zip(words[26:42], words[42:58]) if k == 23]
            if len(values) > 1:
                raise ValueError("Duplicate result bonus entry")
            packed = values[0] if values else 0
            actual = [packed & 255, packed >> 8 & 255, packed >> 16 & 255]
            expected = [normal + hidden + red, hidden, red]
            results.append(dict(start=start, kans=kan_count, marker_count=len(markers),
                                added_dora_bonus=normal - bonus_count(tiles, markers[:1]),
                                expected=expected, actual=actual, matches=expected == actual))
            markers = []
        elif message in (636, 643):
            markers = []
    return dict(results=results, skipped_results=skipped_results, kan_actions=actions)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archives", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    reports = []
    for path in sorted(args.archives.glob("*/packets.ndjson")):
        seal = path.parent / "managed-complete.json"
        if not seal.exists() or json.loads(seal.read_text(encoding="utf-8-sig")).get("incomplete") is not False:
            continue
        content = path.read_bytes()
        audit = audit_packets(json.loads(line) for line in content.decode("utf-8-sig").splitlines() if line.strip())
        reports.append(dict(source=path.parent.name, sha256=hashlib.sha256(content).hexdigest(), **audit))
    results = [r for report in reports for r in report["results"]]
    summary = dict(checked=len(results), matched=sum(r["matches"] for r in results),
                   skipped_results=sum(len(r["skipped_results"]) for r in reports),
                   kan_results=sum(r["kans"] > 0 for r in results),
                   nonzero_added_bonus_results=sum(r["kans"] > 0 and r["added_dora_bonus"] > 0 for r in results))
    if not results:
        raise ValueError("No sealed result evidence; empty corpus is not a pass")
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(dict(summary=summary, archives=reports), output, ensure_ascii=False, indent=2)
    print(json.dumps(summary))
    return 0 if summary["checked"] == summary["matched"] else 1


if __name__ == "__main__":
    raise SystemExit(main())

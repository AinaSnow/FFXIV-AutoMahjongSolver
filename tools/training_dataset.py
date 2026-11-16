"""Preserve/import live match archives and export auditable, perspective-limited samples.

This is a dataset preparation tool, not Mortal's training log format. Unknown outcomes
stay null; local UI progress is not server confirmation or an optimal-action label.
"""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import tempfile

from live_hand_labels import confirm_actions, decode_hands, hand_index_for, LABEL_VERSION

SCHEMA = 1
EXPORT_SCHEMA = 2  # 2026-11-16: hand-result labels, server action outcomes, eligibility flags, hands.jsonl
SPLIT_VERSION = "match-sha256-v1"
PARTITIONS = ("train", "development", "acceptance", "unassigned")
STATE_FIELDS = ("state_code", "wall", "turn", "obs", "hand", "hand_red", "our_melds", "dora",
                "our_riichi", "our_ippatsu", "legal", "scores", "seats", "discard_restriction_known",
                "discardable_tiles", "initial_dealer", "our_seat", "seat_wind", "seat_info_known",
                "round_wind", "dealer", "honba", "riichi_sticks", "kyoku", "scheduled_rounds", "complete")
# Exact key allow-lists for an exported sample; anything else fails the pii_whitelist_intact check.
SAMPLE_KEYS = {"schema_version", "data_origin", "match_id", "split", "sample_id", "hand_id", "revision", "observed_at",
               "observation", "recommendation", "dispatched_action", "execution_status", "provenance", "labels",
               "eligibility", "training_eligible", "quality_flags"}
PROVENANCE_KEYS = ("policy_weights_sha256", "policy_build_id", "rules_build_id", "rule_set", "strategy",
                   "mortal_enabled", "mortal_limited_trial", "search_budget_ms", "calibration_identity", "mortal_model")
PACKET_LOSS_FLAGS = {"raw_capture_missing", "raw_capture_incomplete", "raw_capture_interrupted", "raw_capture_segmented",
                     "raw_capture_packet_loss", "raw_archive_packets_missing", "archive_incomplete",
                     "malformed_packet_log", "malformed_raw_capture", "packet_history_rejected"}
# A pass is never acknowledged by the server; "window closed without our call" is the
# strongest evidence available and is accepted as consistent, never as confirmed.
CONSISTENT_SERVER_STATUSES = ("server-confirmed", "server-window-closed")


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def split_for(match_id):
    if not re.fullmatch(r"[0-9a-f]{64}", match_id):
        raise ValueError("Invalid match identity")
    bucket = int(match_id[:8], 16) % 100
    return "train" if bucket < 80 else "development" if bucket < 90 else "acceptance"


def identity_for(files):
    by_path = {f["path"]: f["sha256"] for f in files}
    identity = by_path.get("archive/packets.ndjson") or by_path.get("raw-capture.ndjson")
    if identity is None:
        hashes = [f["sha256"] for f in sorted(files, key=lambda f: f["path"]) if f["path"].startswith("archive/games/")]
        if not hashes:
            raise ValueError("No match events")
        identity = hashlib.sha256("\n".join(hashes).encode()).hexdigest()
    return hashlib.sha256(("ffxiv-match-v1\n" + identity).encode()).hexdigest()


def is_link(path):
    return path.is_symlink() or getattr(path, "is_junction", lambda: False)()


def safe_file(root, relative):
    part = PurePosixPath(relative)
    if not part.parts or part.is_absolute() or any(p in ("..", ".") or ":" in p or "\\" in p for p in part.parts):
        raise ValueError("Unsafe dataset path")
    path = root
    if is_link(root):
        raise ValueError("Linked dataset root")
    for component in part.parts:
        path = path / component
        if is_link(path):
            raise ValueError("Linked dataset path")
    if not path.resolve().is_relative_to(root.resolve()):
        raise ValueError("Dataset path escapes root")
    return path


def files_under(root):
    if is_link(root):
        raise ValueError("Linked source directory")
    for directory, directories, files in os.walk(root, followlinks=False):
        for name in directories + files:
            if is_link(Path(directory) / name):
                raise ValueError("Linked source entry")
        for name in files:
            yield Path(directory) / name


def inventory(root):
    return [{"path": p.relative_to(root).as_posix(), "bytes": p.stat().st_size, "sha256": digest(p)}
            for p in sorted(files_under(root)) if p.relative_to(root).as_posix() != "manifest.json"]


def read_rows(path):
    with path.open(encoding="utf-8-sig") as source:
        for number, line in enumerate(source, 1):
            if not line.strip():
                continue
            try:
                row = json.loads(line)
                if not isinstance(row, dict):
                    raise ValueError("Expected an event object")
                yield number, row, None
            except (json.JSONDecodeError, ValueError) as error:
                yield number, None, str(error)


def packet_identity(row):
    # .NET serializers may trim trailing fractional zeros; retain all seven digits.
    t = re.fullmatch(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)", str(row.get("t", "")))
    opcode, payload = row.get("opcode"), row.get("payload_hex")
    if not t or not isinstance(opcode, str) or not isinstance(payload, str):
        return None
    return (t[1], (t[2] or "").ljust(7, "0"), opcode.upper(), payload.upper())


def match_quality(directory, files):
    flags = set()
    events = []
    opening = False
    archived_packets, raw_packets = set(), set()
    packet_rows = []
    for item in files:
        name = item["path"]
        if name.startswith("archive/games/") and name.endswith(".ndjson"):
            for line, row, error in read_rows(safe_file(directory, name)):
                if error:
                    flags.add("malformed_game_log")
                else:
                    events.append((name, line, row))
        elif name == "archive/packets.ndjson":
            for _, row, error in read_rows(safe_file(directory, name)):
                if error:
                    flags.add("malformed_packet_log")
                else:
                    packet_rows.append(row)
                    if row.get("message_id") == 636:
                        opening = True
                    if (identity := packet_identity(row)) is not None:
                        archived_packets.add(identity)
    if any(r.get("e") == "input-callback" and r.get("automated") is False for _, _, r in events):
        flags.add("external_input_observed")
    events.sort(key=lambda item: (item[0], item[1]))
    final = None
    for _, _, row in events:
        kind = row.get("e")
        if kind in ("hand-start", "hand-incomplete") or (kind == "state" and isinstance(row.get("hand"), list) and row["hand"]):
            final = None
        elif kind == "state" and row.get("state_code") == 27:
            scores = row.get("scores")
            final = row if row.get("hand") == [] and isinstance(scores, list) and len(scores) == 4 \
                and all(type(s) is int and -200000 <= s <= 200000 for s in scores) and any(scores) else None
    if "malformed_game_log" in flags:
        final = None
    if not opening:
        flags.add("match_start_unobserved")
    if final is None:
        flags.add("final_result_unobserved")
    raw_files = [f["path"] for f in files if f["path"] == "raw-capture.ndjson" or f["path"].startswith("raw-captures/")]
    if not raw_files:
        flags.add("raw_capture_missing")
    if len(raw_files) > 1:
        flags.add("raw_capture_segmented")
    for raw_name in raw_files:
        last = None
        for _, row, error in read_rows(safe_file(directory, raw_name)):
            if error:
                flags.add("malformed_raw_capture")
            else:
                last = row
                if row.get("e") == "raw-packet" and (identity := packet_identity(row)) is not None:
                    raw_packets.add(identity)
        if not last or last.get("e") != "capture-end" or last.get("stream_complete") is not True:
            flags.add("raw_capture_incomplete")
        elif any(last.get(field) not in (None, 0) for field in ("dropped", "rejected")):
            flags.add("raw_capture_packet_loss")
        if last and last.get("reason") == "disabled":
            flags.add("raw_capture_interrupted")
    if raw_files and archived_packets - raw_packets:
        flags.add("raw_archive_packets_missing")
    summary = json.loads((directory / "archive/summary.json").read_text(encoding="utf-8-sig"))
    if summary.get("packet_write_failed") is not False:
        flags.add("archive_incomplete")
    if (summary.get("environment") or {}).get("protocol_verified") is not True:
        flags.add("protocol_not_fully_verified")
    if (summary.get("execution_health") or {}).get("missing_outcomes", 0):
        flags.add("missing_action_outcomes")
    hands = decode_hands(packet_rows)
    if any(h["label"]["unknown_reason"] and h["label"]["unknown_reason"].startswith("hand history") for h in hands):
        flags.add("packet_history_rejected")
    return flags, events, opening, final, summary, hands


def read_manifest(directory):
    if is_link(directory) or is_link(directory / "manifest.json"):
        raise ValueError("Linked corpus rejected")
    m = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    if m.get("schema_version") != SCHEMA or m.get("split_version") != SPLIT_VERSION or m.get("data_origin") != "ffxiv-live":
        raise ValueError("Unsupported corpus schema")
    files = m["files"]
    if len({f["path"] for f in files}) != len(files):
        raise ValueError("Duplicate inventory entries")
    actual = {p.relative_to(directory).as_posix() for p in files_under(directory)} - {"manifest.json"}
    if actual != {f["path"] for f in files}:
        raise ValueError("Unlisted or missing corpus files")
    for f in files:
        path = safe_file(directory, f["path"])
        if path.stat().st_size != f["bytes"] or digest(path) != f["sha256"]:
            raise ValueError("Checksum mismatch: " + f["path"])
    if m["match_id"] != identity_for(files):
        raise ValueError("Match identity mismatch")
    expected = split_for(m["match_id"]) if m.get("match_start_observed") and m.get("final_result_observed") else "unassigned"
    if m.get("split") != expected:
        raise ValueError("Split changed: match-level partition is immutable")
    return m


def import_archive(archive, corpus, raw=None):
    """Backfill a sealed old archive; never guess which separate raw capture belongs to it."""
    archive, corpus = Path(archive), Path(corpus)
    if not (archive / "summary.json").is_file() or not (archive / "managed-complete.json").is_file():
        raise ValueError("Only sealed managed archives can be imported")
    corpus.mkdir(parents=True, exist_ok=True)
    if is_link(corpus):
        raise ValueError("Linked corpus rejected")
    staging = Path(tempfile.mkdtemp(prefix=".pending-import-", dir=corpus))
    # A failed import stays visibly incomplete; no existing corpus or source files are removed.
    for source in files_under(archive):
        relative = source.relative_to(archive)
        if relative.as_posix() == "training-pending.json":
            continue
        destination = staging / "archive" / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, destination)
    if raw is not None:
        raw = Path(raw)
        if is_link(raw):
            raise ValueError("Linked raw file rejected")
        shutil.copyfile(raw, staging / "raw-capture.ndjson")
    files = inventory(staging)
    flags, _, opening, final, _, _ = match_quality(staging, files)
    flags.update(("legacy_import", "provenance_unknown"))
    match_id = identity_for(files)
    manifest = dict(schema_version=SCHEMA, match_id=match_id, split_version=SPLIT_VERSION,
                    split=split_for(match_id) if opening and final is not None else "unassigned",
                    data_origin="ffxiv-live", created_utc=datetime.now(timezone.utc).isoformat(),
                    source_archive=archive.name, raw_capture=raw.name if raw else None,
                    match_start_observed=opening, final_result_observed=final is not None,
                    training_ready=False, contains_private_raw_data=True, quality_flags=sorted(flags), files=files)
    (staging / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    destination = corpus / ("match-" + match_id)
    if destination.exists():
        existing = read_manifest(destination)
        known = {f["path"]: f["sha256"] for f in existing["files"]}
        if any(known.get(f["path"]) != f["sha256"] for f in files):
            raise ValueError("Conflicting duplicate match; existing corpus preserved")
        # Only remove the fresh, verified staging child created above.
        if staging.resolve().parent != corpus.resolve() or not staging.name.startswith(".pending-import-"):
            raise ValueError("Invalid staging path")
        shutil.rmtree(staging)
        return destination
    staging.rename(destination)
    return destination


def key(row):
    a, b = row.get("hand_id"), row.get("revision")
    return (a, b) if type(a) is int and type(b) is int and a > 0 and b > 0 else None


def action_matches(decision, action):
    return all(decision.get(k) == action.get(k) for k in ("kind", "tile", "is_red"))


def export_match(directory, m):
    flags, events, opening, final, summary, hands = match_quality(directory, m["files"])
    flags.update(m.get("quality_flags", []))
    # Legacy manifests used a per-decision flag for missing identity at archive
    # close (e.g. a quarantined process). Keep that fact in the match report;
    # each sample must use its own decision's model identity.
    if "mortal_model_unknown" in flags:
        flags.remove("mortal_model_unknown")
        flags.add("mortal_model_at_close_unknown")
    if bool(m.get("match_start_observed")) != opening or bool(m.get("final_result_observed")) != (final is not None):
        raise ValueError("Boundary evidence disagrees with manifest")
    states, decisions, outcomes = {}, defaultdict(list), defaultdict(list)
    actions = []
    for order, (name, line, row) in enumerate(events):
        k = key(row)
        if row.get("e") == "state" and k:
            if k in states and states[k][1] != row:
                flags.add("conflicting_state_identity")
            states[k] = (order, row)
        elif row.get("e") == "decision":
            decisions[k].append((order, row))
        elif row.get("e") == "action":
            actions.append((order, name, line, row))
        elif row.get("e") == "action-outcome":
            outcomes[row.get("action_id")].append((order, row))
    ids = Counter(row.get("action_id") for _, _, _, row in actions)
    rank = None
    if final is not None:
        east = final.get("initial_dealer")
        if final["scores"].count(final["scores"][0]) == 1:
            rank = 1 + sum(score > final["scores"][0] for score in final["scores"][1:])
        elif type(east) is int and 0 <= east < 4:
            order = sorted(range(4), key=lambda i: (-final["scores"][i], (i - east) % 4))
            rank = order.index(0) + 1
    first_labels = {aid: entries[0][1].get("label") for aid, entries in outcomes.items() if entries}
    server = confirm_actions(hands, [dict(row, label=first_labels.get(row.get("action_id")))
                                     for _, _, _, row in actions if ids[row.get("action_id")] == 1])
    statuses = Counter(entry["status"] for entry in server.values())
    match_eligibility = dict(
        opening_observed=opening,
        final_result_observed=final is not None,
        hand_results_labelled=bool(hands) and all(h["label"]["outcome"] != "unknown" for h in hands),
        server_actions_consistent=bool(hands) and statuses["server-action-mismatch"] == 0
        and statuses["no-server-evidence"] == 0,
        no_packet_loss=not (flags & PACKET_LOSS_FLAGS),
        protocol_verified="protocol_not_fully_verified" not in flags)
    rows = []
    for order, name, line, action in actions:
        quality = flags - {"mortal_model_at_close_unknown"}
        if action.get("dispatch_context") != "before-input":
            quality.add("action_context_unverified")
        k = key(action)
        state_entry = states.get(k)
        state = state_entry[1] if state_entry and state_entry[0] < order else None
        if state is None:
            quality.add("state_correlation_missing")
        elif state.get("complete") is not True:
            quality.add("state_incomplete")
        candidates = [row for at, row in decisions.get(k, []) if k and at < order and action_matches(row, action)]
        recommendation = candidates[-1] if candidates else None
        if recommendation is None:
            quality.add("recommendation_correlation_missing")
        outcome_rows = [row for at, row in outcomes.get(action.get("action_id"), []) if at > order]
        outcome = outcome_rows[0] if len(outcome_rows) == 1 and ids[action.get("action_id")] == 1 else None
        if outcome is None:
            quality.add("execution_outcome_missing_or_ambiguous")
        elif outcome.get("status") != "state-changed" or action.get("result") != "Ok":
            quality.add("execution_not_observed")
        if action.get("kind") == "Discard" and type(action.get("is_red")) is not bool:
            quality.add("discard_red_identity_unknown")
        provenance = recommendation.get("provenance") if recommendation else None
        if not provenance:
            quality.add("decision_provenance_unknown")
        if recommendation and recommendation.get("source") == "mortal" and not (provenance or {}).get("mortal_model"):
            quality.add("mortal_model_unknown")
        if rank is None:
            quality.add("final_rank_unknown")
        confirmation = server.get(action.get("action_id")) if ids[action.get("action_id")] == 1 else None
        if confirmation is None or confirmation["status"] == "no-server-evidence":
            confirmation = None
            quality.add("server_action_not_verified")
        elif confirmation["status"] == "server-window-closed":
            quality.add("server_pass_not_acknowledged")
        elif confirmation["status"] != "server-confirmed":
            quality.add("server_action_not_verified")
        hand_index = hand_index_for(hands, action.get("t")) if hands else None
        hand_label = hands[hand_index]["label"] if hand_index is not None else None
        known = hand_label is not None and hand_label["outcome"] != "unknown"
        if not known:
            quality.add("hand_result_unknown")
        # Explicit allow-list: no player names, actor IDs, raw payloads, local paths or free-text reasoning.
        features = {field: state[field] for field in STATE_FIELDS if state and field in state}
        provenance = {k: v for k, v in (provenance or {}).items() if k in PROVENANCE_KEYS}
        eligibility = dict(match_eligibility,
                           hand_result_labelled=known,
                           server_action_consistent=confirmation is not None
                           and confirmation["status"] in CONSISTENT_SERVER_STATUSES,
                           sample_observation_complete=state is not None and state.get("complete") is True
                           and action.get("dispatch_context") == "before-input")
        row = dict(schema_version=EXPORT_SCHEMA, data_origin="ffxiv-live", match_id=m["match_id"], split=m["split"],
            sample_id=hashlib.sha256(f"{m['match_id']}:{name}:{line}".encode()).hexdigest(),
            hand_id=action.get("hand_id"), revision=action.get("revision"), observed_at=action.get("t"),
            observation=features, recommendation={k: recommendation.get(k) for k in ("source", "kind", "tile", "is_red")} if recommendation else None,
            dispatched_action={k: action.get(k) for k in ("kind", "tile", "is_red", "result")},
            execution_status=outcome.get("status") if outcome else None, provenance=provenance,
            labels=dict(final_scores=final["scores"] if final else None, final_rank=rank,
                        won=(0 in hand_label["winners"]) if known else None,
                        deal_in=(hand_label["discarder"] == 0) if known else None,
                        hand_result=public_hand_result(hand_label) if known else None,
                        optimal_action=None, server_confirmed_action=confirmation),
            eligibility=eligibility, training_eligible=False, quality_flags=sorted(quality))
        eligibility["pii_whitelist_intact"] = whitelist_intact(row)
        # Every gate, including the independent protocol gate, must hold. No gate is waived here.
        row["training_eligible"] = all(eligibility.values())
        rows.append(row)
    hand_rows = []
    for number, hand in enumerate(hands):
        label = hand["label"]
        hand_flags = sorted(flags & (PACKET_LOSS_FLAGS | {"protocol_not_fully_verified", "match_start_unobserved"}))
        hand_rows.append(dict(schema_version=EXPORT_SCHEMA, label_version=LABEL_VERSION, data_origin="ffxiv-live",
                              match_id=m["match_id"], split=m["split"], hand_number=number + 1,
                              result=public_hand_result(label) if label["outcome"] != "unknown" else None,
                              round_wind=label["round_wind"], kyoku=label["kyoku"], honba=label["honba"],
                              dealer=label["dealer"], riichi=label["riichi"], open_calls=label["open_calls"],
                              closed_kans=label["closed_kans"], discards=label["discards"],
                              unknown_reason=label["unknown_reason"],
                              # Weaker tier for C2 opponent statistics only; never a training gate and
                              # deliberately independent of the protocol gate (see training-data.md).
                              opponent_stats_eligible=label["outcome"] != "unknown" and opening
                              and not (flags & PACKET_LOSS_FLAGS),
                              quality_flags=hand_flags))
    return rows, dict(match_id=m["match_id"], split=m["split"], samples=len(rows), quality_flags=sorted(flags),
                      final_result_observed=final is not None, decision_sources=(summary.get("decision_health") or {}).get("sources", {}),
                      hands=len(hands), hands_labelled=sum(h["label"]["outcome"] != "unknown" for h in hands),
                      eligibility=match_eligibility, server_action_statuses=dict(sorted(statuses.items())),
                      hand_rows=hand_rows)


def public_hand_result(label):
    """Seat-relative hand outcome without the internal evidence checks."""
    return {k: label[k] for k in ("outcome", "winners", "discarder", "multi_winner", "deltas", "tenpai",
                                  "winner_details")}


def whitelist_intact(row):
    return (set(row) <= SAMPLE_KEYS and set(row["observation"]) <= set(STATE_FIELDS)
            and set(row["provenance"]) <= set(PROVENANCE_KEYS)
            and (row["recommendation"] is None or set(row["recommendation"]) <= {"source", "kind", "tile", "is_red"})
            and set(row["dispatched_action"]) <= {"kind", "tile", "is_red", "result"})


def export_corpus(corpus, output):
    corpus, output = Path(corpus), Path(output)
    matches = sorted(p for p in corpus.glob("match-*") if p.is_dir())
    if not matches:
        raise ValueError("No sealed match corpus found")
    if output.resolve().is_relative_to(corpus.resolve()):
        raise ValueError("Export output must be outside the retained corpus")
    output.mkdir(parents=True, exist_ok=False)
    totals, reports, seen = Counter(), [], {}
    rejected = []
    staging = Path(tempfile.mkdtemp(prefix=".pending-export-", dir=output))
    targets = {part: (staging / (part + ".jsonl")).open("w", encoding="utf-8", newline="\n") for part in PARTITIONS}
    targets["hands"] = (staging / "hands.jsonl").open("w", encoding="utf-8", newline="\n")
    eligibility, hands = Counter(), Counter()
    try:
        for directory in matches:
            try:
                m = read_manifest(directory)
                fingerprint = json.dumps(m["files"], sort_keys=True)
                if m["match_id"] in seen:
                    if seen[m["match_id"]] != fingerprint:
                        raise ValueError("Conflicting duplicate match")
                    continue
                seen[m["match_id"]] = fingerprint
                rows, report = export_match(directory, m)
                for row in rows:
                    targets[m["split"]].write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
                    totals[m["split"]] += 1
                    gates = row["eligibility"]
                    eligibility["training_eligible"] += row["training_eligible"]
                    eligibility["eligible_except_protocol_gate"] += all(
                        value for gate, value in gates.items() if gate != "protocol_verified")
                    for gate, value in gates.items():
                        eligibility["failing_" + gate] += not value
                for hand in report.pop("hand_rows"):
                    targets["hands"].write(json.dumps(hand, ensure_ascii=False, separators=(",", ":")) + "\n")
                    hands["hands"] += 1
                    hands["labelled"] += hand["result"] is not None
                    hands["opponent_stats_eligible"] += hand["opponent_stats_eligible"]
                reports.append(report)
            except (ValueError, KeyError, TypeError, OSError) as error:
                rejected.append(dict(directory=directory.name, reason=str(error)))
    finally:
        for target in targets.values():
            target.close()
    if rejected:
        (output / "rejected.json").write_text(json.dumps(rejected, indent=2), encoding="utf-8")
        raise ValueError(f"{len(rejected)} corpus entries failed validation; no training split files published")
    for part in (*PARTITIONS, "hands"):
        (staging / (part + ".jsonl")).rename(output / (part + ".jsonl"))
    staging.rmdir()
    match_gates = Counter(gate for r in reports for gate, value in r["eligibility"].items() if value)
    report = dict(schema_version=EXPORT_SCHEMA, data_origin="ffxiv-live", split_version=SPLIT_VERSION,
                  label_version=LABEL_VERSION, matches=reports, totals={part: totals[part] for part in PARTITIONS},
                  training_eligible=eligibility["training_eligible"],
                  eligibility=dict(sorted(eligibility.items())),
                  match_gates_passed=dict(sorted(match_gates.items())),
                  matches_eligible_except_protocol_gate=sum(
                      all(v for g, v in r["eligibility"].items() if g != "protocol_verified") for r in reports),
                  hands=dict(hands),
                  notice="Prepared observations only. training_eligible requires every gate including the "
                         "independent protocol gate; opponent_stats_eligible hands are a weaker tier for "
                         "opponent statistics, not training. No optimal-action labels are inferred.")
    report["outputs"] = {part: digest(output / (part + ".jsonl")) for part in (*PARTITIONS, "hands")}
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--import-archive", type=Path)
    parser.add_argument("--import-archives", type=Path)
    parser.add_argument("--raw-capture", type=Path, help="Explicit capture for --import-archive only; never matched by guessed timestamps")
    args = parser.parse_args()
    if args.raw_capture and not args.import_archive:
        parser.error("--raw-capture requires --import-archive")
    if not (args.output or args.import_archive or args.import_archives):
        parser.error("Choose an import or output operation")
    if args.import_archive:
        print(import_archive(args.import_archive, args.corpus, args.raw_capture))
    if args.import_archives:
        for archive in sorted(args.import_archives.glob("match-*")):
            if (archive / "managed-complete.json").is_file():
                print(import_archive(archive, args.corpus))
    if args.output:
        report = export_corpus(args.corpus, args.output)
        print(json.dumps({"matches": len(report["matches"]), "samples": report["totals"],
                          "training_eligible": report["training_eligible"],
                          "eligible_except_protocol_gate": report["eligibility"].get("eligible_except_protocol_gate", 0),
                          "hands": report["hands"]}))


if __name__ == "__main__":
    main()

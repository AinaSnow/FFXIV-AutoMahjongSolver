"""Audit completed self-play exports and reject overlapping datasets before training."""
import argparse
from collections import Counter
import json
from pathlib import Path

from self_play import canonical_identity, inspect_log, seeds_for, SEED_KEY
from rule_profile import rule_profile
from doman_engine import DATASET_REVISION, validate_match_events
from self_play_dataset import file_hash, hand_contexts, validate_arrays, validate_outcome


def checked_path(root, relative, expected_hash=None):
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()) or not path.is_file():
        raise ValueError("Missing file or path outside dataset")
    if expected_hash is not None and file_hash(path) != expected_hash:
        raise ValueError("Dataset file hash mismatch")
    return path


def verify_dataset(root, seen_matches=None, seen_seeds=None):
    import numpy as np
    root = Path(root).resolve()
    manifest = json.loads((root/"manifest.json").read_text(encoding="utf-8"))
    if manifest.get("schema_version") != 2 or manifest.get("status") != "complete":
        raise ValueError("Only completed schema-2 datasets can be consumed")
    if manifest.get("oracle") is not False or not manifest.get("non_oracle_loader_verified"):
        raise ValueError("Unverified non-oracle dataset")
    mode = manifest["rules"]["match_mode"]
    rules = rule_profile(mode, allow_experimental=True)
    if manifest.get("scheduled_hands") != (4 if mode == "doman-quick" else 8) or manifest.get("seed_key") != SEED_KEY:
        raise ValueError("Invalid match length or seed key")
    if manifest["rules"].get("rule_set") != rules["rule_set"]:
        raise ValueError("Unknown rule set")
    if mode != "libriichi-hanchan" and (manifest.get("native_build") or {}).get("dataset_revision") != DATASET_REVISION:
        raise ValueError("Doman dataset adapter identity is missing")
    split = manifest["split"]
    from self_play import SPLITS
    seeds = seeds_for(split, manifest["seed_start"]-SPLITS[split],
                      manifest["seed_end_exclusive"]-manifest["seed_start"])
    if manifest["completed_seed_groups"] != len(seeds):
        raise ValueError("Missing completed seed groups")
    if manifest.get("perspective_glob") != ("players/*/*.npz" if split == "train" else None):
        raise ValueError("Training split marker mismatch")
    if manifest.get("training_glob") != ("unique/*.json.gz" if split == "train" and mode == "libriichi-hanchan" else None):
        raise ValueError("Invalid training log entrypoint")
    if manifest.get("experimental_training_glob") != ("unique/*.json.gz" if split == "train" and mode != "libriichi-hanchan" else None):
        raise ValueError("Invalid experimental training log entrypoint")
    all_matches = seen_matches if seen_matches is not None else set()
    all_seeds = seen_seeds if seen_seeds is not None else set()
    if any(seed in all_seeds for seed in seeds):
        raise ValueError("Repeated seeds across datasets")
    index = checked_path(root, "matches.jsonl", manifest["matches_sha256"])
    rows = [json.loads(line) for line in index.read_text(encoding="utf-8").splitlines()]
    if Counter(row["seed"] for row in rows) != Counter({seed: 4 for seed in seeds}):
        raise ValueError("Missing or duplicated seat rotations")
    unique = set()
    expected_arrays = set()
    expected_raw = {f"raw/{seed}/{seed}_42_{letter}.json.gz" for seed in seeds for letter in "abcd"}
    if {row["path"] for row in rows} != expected_raw or len(rows) != len(expected_raw):
        raise ValueError("Incorrect raw seat log inventory")
    if {p.relative_to(root).as_posix() for p in (root/"raw").rglob("*.json.gz")} != expected_raw:
        raise ValueError("Unindexed raw log")
    decisions = 0
    for row in rows:
        if row["split"] != split or row["match_mode"] != mode or row["scheduled_hands"] != manifest["scheduled_hands"]:
            raise ValueError("Mixed dataset split or match mode")
        raw_path = checked_path(root, row["path"], row["sha256"])
        _, events = inspect_log(raw_path, row["seed"])
        validate_match_events(events, mode)
        identity = canonical_identity(events)
        hands, scores = hand_contexts(events)
        if identity != row["match_id"] or hands != row["hands"]:
            raise ValueError("Match identity or public hand metadata mismatch")
        validate_outcome(row["final_scores"], row["ranks"], scores)
        if row["duplicate"] != (identity in unique):
            raise ValueError("Invalid duplicate flag")
        if row["duplicate"]:
            if row["views"]:
                raise ValueError("Duplicate game has repeated training views")
            continue
        if identity in all_matches:
            raise ValueError("Duplicate gameplay across datasets/splits")
        unique.add(identity)
        if row["unique_path"] != f"unique/{identity}.json.gz":
            raise ValueError("Unexpected unique log path")
        checked_path(root, row["unique_path"], row["sha256"])
        if sorted(view["player_id"] for view in row["views"]) != [0,1,2,3]:
            raise ValueError("Missing player perspectives")
        match_samples = 0
        for view in row["views"]:
            seat = view["player_id"]
            if view["path"] != f"players/{identity}/player-{seat}.npz" or view["labels_path"] != f"players/{identity}/player-{seat}.labels.json":
                raise ValueError("Unexpected player export path")
            path = checked_path(root, view["path"], view["sha256"])
            expected_arrays.add(view["path"])
            labels = json.loads(checked_path(root, view["labels_path"], view["labels_sha256"]).read_text(encoding="utf-8"))
            expected_labels = dict(match_id=identity, seed=row["seed"], split=split, match_mode=mode,
                scheduled_hands=manifest["scheduled_hands"], player_id=seat, hands=hands,
                final_rank=row["ranks"][seat], final_score=row["final_scores"][seat],
                score_delta=row["final_scores"][seat]-25000, oracle=False, labels_are_model_inputs=False,
                feature_fields=["obs", "masks"], observation_transform="native",
                architecture_version=manifest["model"]["architecture_version"], player_decisions=view["player_decisions"])
            if labels != expected_labels:
                raise ValueError("Player labels disagree with source match")
            with np.load(path, allow_pickle=False) as arrays:
                count = validate_arrays(arrays, labels["architecture_version"], len(hands))
            if count != view["player_decisions"]:
                raise ValueError("Player decision count mismatch")
            match_samples += count
        if match_samples != row["player_samples"]:
            raise ValueError("Match decision count mismatch")
        decisions += match_samples
    if {p.relative_to(root).as_posix() for p in (root/"players").rglob("*.npz")} != expected_arrays:
        raise ValueError("Unindexed player observations")
    if {p.stem.removesuffix(".json") for p in (root/"unique").glob("*.json.gz")} != unique:
        raise ValueError("Unindexed unique games")
    if (manifest["generated_matches"], manifest["unique_matches"], manifest["duplicate_matches"], manifest["player_decisions"]) != (len(rows), len(unique), len(rows)-len(unique), decisions) or not decisions:
        raise ValueError("Manifest totals disagree with verified data")
    all_seeds.update(seeds)
    all_matches.update(unique)
    return dict(split=split, match_mode=mode, unique_matches=len(unique), player_decisions=decisions, status="verified")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("datasets", type=Path, nargs="+")
    args = parser.parse_args()
    matches, seeds = set(), set()
    results = [verify_dataset(path, matches, seeds) for path in args.datasets]
    print(json.dumps(results))


if __name__ == "__main__":
    main()

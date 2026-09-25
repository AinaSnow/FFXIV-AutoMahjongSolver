"""Generate traceable Mortal self-play logs and validate non-oracle training inputs."""
import argparse
from collections import Counter
import gzip
import hashlib
import json
import os
from pathlib import Path
import random
import subprocess
import sys
import time

from rule_profile import rule_profile

SPLITS = {"train": 100_000, "development": 1_000_000, "acceptance": 10_000_000}
SEED_KEY = 42


def seeds_for(split, offset, groups):
    if split not in SPLITS or not 1 <= groups <= 100000 or not 0 <= offset < 100000 or offset + groups > 100000:
        raise ValueError("groups/offset must stay within the split's disjoint 100000-seed range")
    return range(SPLITS[split] + offset, SPLITS[split] + offset + groups)


def canonical_identity(events):
    # Model names and inference timing are not different games. All four seat
    # rotations of an identical greedy model can otherwise duplicate the data.
    cleaned = [{k: v for k, v in e.items() if k not in ("meta", "names", "seed")} for e in events]
    return hashlib.sha256(json.dumps(cleaned, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def inspect_log(path, seed):
    with gzip.open(path, "rt", encoding="utf-8") as source:
        raw = source.read()
    events = [json.loads(line) for line in raw.splitlines() if line.strip()]
    if not events or events[0].get("type") != "start_game" or events[-1].get("type") != "end_game":
        raise ValueError("Incomplete match log")
    if events[0].get("seed") != [seed, SEED_KEY]:
        raise ValueError("Unexpected seed identity")
    active = False
    for event in events:
        if event["type"] == "start_kyoku":
            if active:
                raise ValueError("New hand without an end marker")
            active = True
        elif event["type"] == "end_kyoku":
            if not active:
                raise ValueError("Hand end without an opening")
            active = False
    if active:
        raise ValueError("Unfinished final hand")
    return raw, events


def validate_training_view(loader, raw):
    games = loader.load_log(raw)
    if sorted(game.take_player_id() for game in games) != [0, 1, 2, 3]:
        raise ValueError("Missing player perspectives")
    samples = 0
    for game in games:
        if game.take_invisible_obs():
            raise ValueError("Oracle observations must not enter ordinary model training")
        actions, masks = game.take_actions(), game.take_masks()
        if len(actions) != len(masks) or not actions:
            raise ValueError("Missing actions or legal masks")
        if any(not 0 <= action < len(mask) or not mask[action] for action, mask in zip(actions, masks)):
            raise ValueError("Illegal action in training sample")
        samples += len(actions)
    return samples


def write_json(path, value):
    # A failed run remains marked running; never publish a complete manifest early.
    temporary = path.with_suffix(".pending.json")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    temporary.replace(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mortal-dir", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--split", choices=SPLITS, default="train")
    parser.add_argument("--groups", type=int, default=2, help="Four seat rotations per seed, deduplicated before training")
    parser.add_argument("--offset", type=int, default=100)
    parser.add_argument("--epsilon", type=float, default=0.02, help="Training-only exploration; evaluation splits are greedy")
    args = parser.parse_args()
    try:
        seeds = seeds_for(args.split, args.offset, args.groups)
        if not 0 <= args.epsilon <= 1:
            raise ValueError("epsilon must be between 0 and 1")
        if args.output.exists():
            raise ValueError("Output exists; select a fresh run directory")
    except ValueError as error:
        parser.error(str(error))
    mortal_dir, output = args.mortal_dir.resolve(), args.output.resolve()
    os.chdir(mortal_dir)
    sys.path.insert(0, str(mortal_dir))
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
    import torch
    import numpy as np
    import mortal_runner
    from libriichi.arena import OneVsThree
    from libriichi.dataset import GameplayLoader, Grp
    torch.set_num_threads(2)
    engine = mortal_runner.create_engine()
    engine.name = "selfplay"
    engine.boltzmann_epsilon = args.epsilon if args.split == "train" else 0
    engine.boltzmann_temp = 0.05
    engine.top_p = 1
    loader = GameplayLoader(version=engine.version, oracle=False, trust_seed=False)
    output.mkdir(parents=True, exist_ok=False)
    manifest = dict(schema_version=1, data_origin="libriichi-selfplay", status="running", split=args.split,
                    seed_start=seeds.start, seed_end_exclusive=seeds.stop, seed_key=SEED_KEY,
                    rules=rule_profile(), model=mortal_runner._model_identity,
                    engine_commit=subprocess.check_output(["git", "-C", str(mortal_dir), "rev-parse", "HEAD"], text=True).strip(),
                    source_hashes={name: hashlib.sha256((mortal_dir/name).read_bytes()).hexdigest()
                                   for name in ("engine.py", "model.py", "config.toml")},
                    generator_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                    torch_version=torch.__version__, device=str(engine.device),
                    epsilon=engine.boltzmann_epsilon, temperature=engine.boltzmann_temp,
                    oracle=False, live_game_statistics=False, training_weights_modified=False)
    write_json(output/"manifest.json", manifest)
    seen = set()
    total_samples = generated = duplicates = 0
    event_counts = Counter()
    started = time.monotonic()
    with (output/"matches.jsonl").open("w", encoding="utf-8") as index:
        for seed in seeds:
            random.seed(seed); np.random.seed(seed); torch.manual_seed(seed)
            directory = output/"raw"/str(seed)
            OneVsThree(disable_progress_bar=True, log_dir=str(directory)).py_vs_py(engine, engine, (seed, SEED_KEY), 1)
            logs = sorted(directory.glob("*.json.gz"))
            if len(logs) != 4:
                raise ValueError("Expected four completed seat rotations")
            for path in logs:
                raw, events = inspect_log(path, seed)
                samples = validate_training_view(loader, raw)
                identity = canonical_identity(events)
                duplicate = identity in seen
                generated += 1
                if duplicate:
                    duplicates += 1
                else:
                    seen.add(identity)
                    destination = output/"unique"/(identity+".json.gz")
                    destination.parent.mkdir(exist_ok=True)
                    destination.write_bytes(path.read_bytes())
                    total_samples += samples
                    event_counts.update(e["type"] for e in events)
                outcome = Grp.load_log(raw)
                index.write(json.dumps(dict(match_id=identity, seed=seed, split=args.split,
                    path=path.relative_to(output).as_posix(), sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                    duplicate=duplicate, player_samples=samples, final_scores=list(outcome.take_final_scores()),
                    ranks=[int(rank)+1 for rank in outcome.take_rank_by_player()]))+"\n")
                index.flush()
            print(f"seed {seed}: {generated} generated, {len(seen)} unique, {total_samples} player decisions", flush=True)
    manifest.update(status="complete", generated_matches=generated, unique_matches=len(seen),
                    duplicate_matches=duplicates, player_decisions=total_samples,
                    event_counts=dict(event_counts), elapsed_seconds=time.monotonic()-started,
                    non_oracle_loader_verified=True,
                    training_glob="unique/*.json.gz" if args.split == "train" else None,
                    training_note="Logs validated; full Mortal training additionally requires a GRP model and isolated training configuration")
    write_json(output/"manifest.json", manifest)
    print(json.dumps({k: manifest[k] for k in ("unique_matches", "player_decisions", "elapsed_seconds", "event_counts")}), flush=True)


if __name__ == "__main__":
    main()

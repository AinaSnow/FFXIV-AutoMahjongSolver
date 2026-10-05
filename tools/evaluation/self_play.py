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

from rule_profile import rule_profile, REQUESTED_MODES
from doman_engine import load_doman_dataset, validate_match_events
from self_play_dataset import hand_contexts, validate_outcome, export_player_views, file_hash

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
    parser.add_argument("--match-mode", choices=REQUESTED_MODES, default="libriichi-hanchan")
    parser.add_argument("--doman-engine", type=Path, help="Doman requires the verified dataset-capable isolated build")
    parser.add_argument("--device", help="Offline device override; live config is unchanged")
    args = parser.parse_args()
    try:
        rules = rule_profile(args.match_mode, allow_experimental=True)
        seeds = seeds_for(args.split, args.offset, args.groups)
        if not 0 <= args.epsilon <= 1:
            raise ValueError("epsilon must be between 0 and 1")
        if args.output.exists():
            raise ValueError("Output exists; select a fresh run directory")
    except ValueError as error:
        parser.error(str(error))
    native_build = None
    if args.match_mode != "libriichi-hanchan":
        try:
            OneVsThree, GameplayLoader, Grp, native_build = load_doman_dataset(args.doman_engine)
        except (ValueError, OSError, ImportError) as error:
            parser.error(str(error))
    mortal_dir, output = args.mortal_dir.resolve(), args.output.resolve()
    os.chdir(mortal_dir)
    sys.path.insert(0, str(mortal_dir))
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
    import torch
    import numpy as np
    import mortal_runner
    if native_build is None:
        from libriichi.arena import OneVsThree
        from libriichi.dataset import GameplayLoader, Grp
    torch.set_num_threads(2)
    torch.backends.cudnn.benchmark = False
    torch.backends.cudnn.deterministic = True
    engine = mortal_runner.create_engine(device_name=args.device, enable_amp=False)
    if engine.is_oracle:
        raise ValueError("Self-play training export forbids oracle engines")
    engine.name = "selfplay"
    engine.boltzmann_epsilon = args.epsilon if args.split == "train" else 0
    engine.boltzmann_temp = 0.05
    engine.top_p = 1
    mode_options = dict(match_mode=args.match_mode) if native_build else {}
    loader = GameplayLoader(version=engine.version, oracle=False, trust_seed=False, **mode_options)
    output.mkdir(parents=True, exist_ok=False)
    manifest = dict(schema_version=2, data_origin="doman-selfplay" if native_build else "libriichi-selfplay", status="running", split=args.split,
                    seed_start=seeds.start, seed_end_exclusive=seeds.stop, seed_key=SEED_KEY,
                    rules=rules, model=engine.model_identity.copy(), native_build=native_build,
                    scheduled_hands=4 if args.match_mode == "doman-quick" else 8, observation_transform="native",
                    loader=dict(oracle=False, trust_seed=False, augmented=False, match_mode=args.match_mode),
                    engine_commit=subprocess.check_output(["git", "-C", str(mortal_dir), "rev-parse", "HEAD"], text=True).strip(),
                    source_hashes={name: hashlib.sha256((mortal_dir/name).read_bytes()).hexdigest()
                                   for name in ("engine.py", "model.py", "config.toml")},
                    generator_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                    torch_version=torch.__version__, device=str(engine.device),
                    inference=dict(amp=False, cudnn_benchmark=False, cudnn_deterministic=True,
                        quick_eval=engine.enable_quick_eval, rule_based_agari_guard=engine.enable_rule_based_agari_guard),
                    exporter_sha256=file_hash(Path(__file__).with_name("self_play_dataset.py")),
                    completed_seed_groups=0, training_glob=None, perspective_glob=None,
                    epsilon=engine.boltzmann_epsilon, temperature=engine.boltzmann_temp,
                    oracle=False, live_game_statistics=False, training_weights_modified=False)
    write_json(output/"manifest.json", manifest)
    seen = {}
    total_samples = generated = duplicates = 0
    event_counts = Counter()
    started = time.monotonic()
    try:
        with (output/"matches.jsonl").open("w", encoding="utf-8") as index:
            for seed in seeds:
                random.seed(seed); np.random.seed(seed); torch.manual_seed(seed)
                directory = output/"raw"/str(seed)
                OneVsThree(disable_progress_bar=True, log_dir=str(directory), **mode_options).py_vs_py(engine, engine, (seed, SEED_KEY), 1)
                logs = [directory/f"{seed}_{SEED_KEY}_{letter}.json.gz" for letter in "abcd"]
                if set(directory.glob("*.json.gz")) != set(logs):
                    raise ValueError("Expected exactly four completed seat rotations")
                for path in logs:
                    raw, events = inspect_log(path, seed)
                    validate_match_events(events, args.match_mode)
                    if events[0].get("names") != ["selfplay"]*4:
                        raise ValueError("Unexpected self-play model names")
                    hands, reconstructed = hand_contexts(events)
                    outcome = Grp.load_log(raw)
                    scores = list(outcome.take_final_scores())
                    ranks = [int(rank)+1 for rank in outcome.take_rank_by_player()]
                    validate_outcome(scores, ranks, reconstructed)
                    identity = canonical_identity(events)
                    duplicate = identity in seen
                    views = []
                    generated += 1
                    if duplicate:
                        duplicates += 1
                        samples = seen[identity]
                    else:
                        destination = output/"unique"/(identity+".json.gz")
                        destination.parent.mkdir(exist_ok=True)
                        destination.write_bytes(path.read_bytes())
                        metadata = dict(match_id=identity, seed=seed, split=args.split,
                            match_mode=args.match_mode, scheduled_hands=manifest["scheduled_hands"])
                        views = export_player_views(loader, raw, output/"players"/identity,
                            metadata, hands, scores, ranks, engine.version)
                        for view in views:
                            for field in ("path", "labels_path"):
                                view[field] = f"players/{identity}/{view[field]}"
                        samples = sum(view["player_decisions"] for view in views)
                        seen[identity] = samples
                        total_samples += samples
                        event_counts.update(e["type"] for e in events)
                    index.write(json.dumps(dict(match_id=identity, seed=seed, split=args.split,
                        match_mode=args.match_mode, scheduled_hands=manifest["scheduled_hands"],
                        path=path.relative_to(output).as_posix(), sha256=file_hash(path),
                        unique_path=f"unique/{identity}.json.gz", duplicate=duplicate,
                        player_samples=samples, views=views, hands=hands, final_scores=scores, ranks=ranks))+"\n")
                    index.flush()
                manifest.update(completed_seed_groups=manifest["completed_seed_groups"]+1,
                    generated_matches=generated, unique_matches=len(seen), player_decisions=total_samples)
                write_json(output/"manifest.json", manifest)
                print(f"seed {seed}: {generated} generated, {len(seen)} unique, {total_samples} player decisions", flush=True)
        manifest.update(status="complete", generated_matches=generated, unique_matches=len(seen),
                        duplicate_matches=duplicates, player_decisions=total_samples,
                        event_counts=dict(event_counts), elapsed_seconds=time.monotonic()-started,
                        non_oracle_loader_verified=True, matches_sha256=file_hash(output/"matches.jsonl"),
                        training_glob="unique/*.json.gz" if args.split == "train" and not native_build else None,
                        experimental_training_glob="unique/*.json.gz" if args.split == "train" and native_build else None,
                        perspective_glob="players/*/*.npz" if args.split == "train" else None,
                        training_note="Validated experimental data; Doman needs the matching dataset loader, "
                            "an isolated training configuration and calibrated GRP/reward. No trained weights produced.")
        write_json(output/"manifest.json", manifest)
        print(json.dumps({k: manifest[k] for k in ("unique_matches", "player_decisions", "elapsed_seconds", "event_counts")}), flush=True)
    except BaseException as error:
        manifest.update(status="failed", error=f"{type(error).__name__}: {error}",
            elapsed_seconds=time.monotonic()-started, training_glob=None, experimental_training_glob=None, perspective_glob=None)
        write_json(output/"manifest.json", manifest)
        raise


if __name__ == "__main__":
    main()

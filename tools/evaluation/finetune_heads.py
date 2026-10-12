"""Bounded Doman v4 output-head experiment; no plugin or live model writes.

Uses complete-match Monte Carlo utility, not an uncalibrated hanchan GRP.
Frozen visible-state embeddings are shared by both reward candidates.
"""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import random
import sys
import time

from doman_engine import load_doman_dataset, validate_match_events
from self_play import canonical_identity, inspect_log, seeds_for, SEED_KEY, write_json
from self_play_dataset import hand_contexts, validate_outcome, validate_arrays, file_hash


def load_runtime(cfg):
    arena, loader, grp, build = load_doman_dataset(cfg["doman_engine"])
    mortal_dir = Path(cfg["mortal_dir"]).resolve()
    os.chdir(mortal_dir)
    sys.path[:0] = [str(mortal_dir), str(Path(__file__).resolve().parents[1])]
    import torch
    from mortal_runner import create_engine
    torch.set_num_threads(2)
    torch.backends.cudnn.benchmark = False
    torch.backends.cudnn.deterministic = True
    engine = create_engine(checkpoint_path=cfg["baseline"], device_name="cuda:0", enable_amp=False, name="trainee")
    if engine.version != 4 or engine.is_oracle or engine.model_identity["checkpoint_sha256"] != cfg["baseline_sha256"]:
        raise ValueError("Expected the frozen non-oracle v4 baseline")
    engine.brain.requires_grad_(False).eval().freeze_bn(True)
    hardware = dict(gpu=torch.cuda.get_device_name(0), vram_bytes=torch.cuda.get_device_properties(0).total_memory,
                    torch=str(torch.__version__), python=sys.version, amp=False, threads=2)
    return arena, loader, grp, build, engine, hardware


def encode_game(game, brain, batch_size):
    import numpy as np
    import torch
    if game.take_invisible_obs():
        raise ValueError("Oracle inputs cannot be used")
    arrays = dict(obs=np.asarray(game.take_obs(), dtype=np.float32), masks=np.asarray(game.take_masks(), dtype=np.bool_),
                  actions=np.asarray(game.take_actions(), dtype=np.int64), hand_index=np.asarray(list(game.take_at_kyoku()), dtype=np.int64),
                  dones=np.asarray(game.take_dones(), dtype=np.bool_), apply_gamma=np.asarray(game.take_apply_gamma(), dtype=np.bool_))
    if not len(arrays["actions"]):
        return None
    validate_arrays(arrays, 4, int(arrays["hand_index"].max())+1)
    features = []
    with torch.inference_mode():
        for start in range(0, len(arrays["actions"]), batch_size):
            features.append(brain(torch.from_numpy(arrays["obs"][start:start+batch_size]).cuda()).cpu())
    return dict(phi=torch.cat(features), actions=torch.from_numpy(arrays["actions"]), masks=torch.from_numpy(arrays["masks"]))


def generate(cfg, output, *, pilot=False):
    import numpy as np
    import torch
    from mortal_runner import create_engine
    arena, loader_type, grp, build, engine, hardware = load_runtime(cfg)
    torch.cuda.reset_peak_memory_stats()
    engine.boltzmann_epsilon = cfg["epsilon"]
    engine.boltzmann_temp = cfg["temperature"]
    engine.top_p = 1
    loader = loader_type(version=4, oracle=False, trust_seed=False, match_mode="doman-quick")
    groups = cfg["pilot_groups"] if pilot else cfg["train_groups"]
    offset = cfg["pilot_offset"] if pilot else cfg["train_offset"]
    seeds = seeds_for("train", offset, groups)
    manifest = dict(status="running", stage="pilot" if pilot else "training-data", hardware=hardware,
                    config=cfg, native_build=build, baseline=engine.model_identity, seed_start=seeds.start,
                    seed_end_exclusive=seeds.stop, script_sha256=file_hash(__file__), oracle=False,
                    observation_transform="native", rule_based_agari_guard=True, quick_eval=True,
                    reward="complete-match terminal utility; gamma=1; no GRP or intermediate rank shaping",
                    unique_matches=0, player_decisions=0, completed_groups=0)
    write_json(output/"data-manifest.json", manifest)
    chunks, seen, opponents = [], set(), {}
    generated = 0
    arena_seconds = encoding_seconds = 0.0
    started = time.monotonic()
    try:
        with (output/"matches.jsonl").open("w", encoding="utf-8") as sink:
            for batch_index, first in enumerate(range(seeds.start, seeds.stop, cfg["batch_groups"])):
                if time.monotonic()-started > cfg["max_data_seconds"]:
                    raise TimeoutError("Predeclared data time budget exhausted")
                count = min(cfg["batch_groups"], seeds.stop-first)
                opponent_path = cfg["opponents"][batch_index % len(cfg["opponents"])]
                if opponent_path not in opponents:
                    opponents[opponent_path] = create_engine(checkpoint_path=opponent_path, name="opponent", device_name="cuda:0", enable_amp=False)
                opponent = opponents[opponent_path]
                directory = output/"raw"/str(first)
                random.seed(first); np.random.seed(first); torch.manual_seed(first)
                before = time.monotonic()
                arena(disable_progress_bar=True, log_dir=str(directory), match_mode="doman-quick").py_vs_py(
                    engine, opponent, (first, SEED_KEY), count)
                arena_seconds += time.monotonic()-before
                print(f"generated seeds {first}..{first+count-1}; encoding frozen visible features", flush=True)
                before = time.monotonic()
                expected = {directory/f"{seed}_{SEED_KEY}_{letter}.json.gz" for seed in range(first, first+count) for letter in "abcd"}
                if set(directory.glob("*.json.gz")) != expected:
                    raise ValueError("Incomplete arena batch")
                for seed in range(first, first+count):
                    for seat, letter in enumerate("abcd"):
                        path = directory/f"{seed}_{SEED_KEY}_{letter}.json.gz"
                        raw, events = inspect_log(path, seed)
                        validate_match_events(events, "doman-quick")
                        names = ["opponent"]*4; names[seat] = "trainee"
                        if events[0]["names"] != names:
                            raise ValueError("Unexpected player assignment")
                        hands, reconstructed = hand_contexts(events)
                        outcome = grp.load_log(raw)
                        scores = list(outcome.take_final_scores())
                        ranks = [int(rank)+1 for rank in outcome.take_rank_by_player()]
                        validate_outcome(scores, ranks, reconstructed)
                        identity = canonical_identity(events)
                        duplicate = identity in seen
                        generated += 1
                        row = dict(seed=seed, seat=seat, match_id=identity, duplicate=duplicate,
                                   path=path.relative_to(output).as_posix(), sha256=file_hash(path),
                                   opponent=opponent.model_identity, scores=scores, ranks=ranks, hands=len(hands))
                        if not duplicate:
                            seen.add(identity)
                            games = loader.load_log(raw)
                            if sorted(g.take_player_id() for g in games) != [0, 1, 2, 3]:
                                raise ValueError("Missing player views")
                            for game in games:
                                player = game.take_player_id()
                                encoded = encode_game(game, engine.brain, cfg["encode_batch_size"])
                                if encoded is None:
                                    continue
                                n = len(encoded["actions"])
                                encoded["ranks"] = torch.full((n,), ranks[player]-1, dtype=torch.long)
                                # Every complete-match player perspective has equal expected weight.
                                encoded["weights"] = torch.full((n,), 1/n, dtype=torch.float64)
                                chunks.append(encoded)
                                manifest["player_decisions"] += n
                        sink.write(json.dumps(row)+"\n")
                sink.flush()
                encoding_seconds += time.monotonic()-before
                manifest.update(completed_groups=first+count-seeds.start, unique_matches=len(seen), generated_matches=generated,
                                arena_seconds=arena_seconds, encoding_seconds=encoding_seconds,
                                elapsed_seconds=time.monotonic()-started, peak_vram_bytes=torch.cuda.max_memory_allocated())
                write_json(output/"data-manifest.json", manifest)
                print(f"data {manifest['completed_groups']}/{groups} groups, {len(seen)} unique matches, {manifest['player_decisions']} decisions", flush=True)
        data = {key: torch.cat([chunk[key] for chunk in chunks]) for key in chunks[0]}
        torch.save(data, output/"features.pt")
        manifest.update(status="complete", features_sha256=file_hash(output/"features.pt"), matches_sha256=file_hash(output/"matches.jsonl"),
                        elapsed_seconds=time.monotonic()-started, simulated_matches_per_hour=generated/arena_seconds*3600,
                        encoded_decisions_per_second=manifest["player_decisions"]/encoding_seconds)
        write_json(output/"data-manifest.json", manifest)
        return data, engine, manifest
    except BaseException as exc:
        manifest.update(status="failed", error=f"{type(exc).__name__}: {exc}", elapsed_seconds=time.monotonic()-started)
        write_json(output/"data-manifest.json", manifest)
        raise


def fit(cfg, data, engine, output, *, pilot=False):
    import torch
    from model import DQN
    from mortal_runner import load_checkpoint
    from torch.nn import functional as F
    state, identity = load_checkpoint(torch, cfg["baseline"])
    if identity["checkpoint_sha256"] != cfg["baseline_sha256"]:
        raise ValueError("Baseline changed")
    if any(not torch.equal(value.cpu(), state["mortal"][key]) for key, value in engine.brain.state_dict().items()):
        raise ValueError("Frozen backbone or BatchNorm changed during encoding")
    phi, masks, actions, ranks = (data[key].cuda() for key in ("phi", "masks", "actions", "ranks"))
    results = {}
    objectives = {"A": [3.0, 1.0, -1.0, -3.0], "B": [1.0, 1.0, 0.0, -0.25]}
    steps = cfg["pilot_steps"] if pilot else cfg["steps"]
    for name, utility in objectives.items():
        head = DQN(version=4).cuda()
        head.load_state_dict(state["current_dqn"])
        optimizer = torch.optim.AdamW(head.parameters(), lr=cfg["learning_rate"], weight_decay=cfg["weight_decay"])
        targets = torch.tensor(utility, device="cuda")[ranks]
        generator = torch.Generator().manual_seed(cfg["training_seed"])
        # A and B get identical samples in identical order, with independent optimizers.
        indices = torch.multinomial(data["weights"], steps*cfg["training_batch_size"], replacement=True, generator=generator)
        torch.cuda.synchronize(); started = time.monotonic()
        torch.cuda.reset_peak_memory_stats()
        final_loss = {}
        with (output/f"{name}-loss.jsonl").open("w", encoding="utf-8") as sink:
            for step, selection in enumerate(indices.split(cfg["training_batch_size"]), 1):
                if time.monotonic()-started > cfg["max_fit_seconds"]:
                    raise TimeoutError("Predeclared head training budget exhausted")
                idx = selection.cuda()
                q = head(phi[idx], masks[idx])
                taken = q.gather(1, actions[idx, None]).squeeze(1)
                regression = 0.5 * F.mse_loss(taken, targets[idx])
                conservative = (q.logsumexp(-1)-taken).mean()
                loss = regression + cfg["cql_weight"]*conservative
                if not torch.isfinite(loss):
                    raise ValueError("Non-finite head loss")
                optimizer.zero_grad(set_to_none=True)
                loss.backward()
                torch.nn.utils.clip_grad_norm_(head.parameters(), cfg["max_grad_norm"])
                optimizer.step()
                if step == 1 or step % 100 == 0 or step == steps:
                    final_loss = dict(step=step, regression=regression.item(), cql=conservative.item(), total=loss.item())
                    sink.write(json.dumps(final_loss)+"\n"); sink.flush()
        torch.cuda.synchronize(); seconds = time.monotonic()-started
        changed = sum(not torch.equal(value.cpu(), state["current_dqn"][key]) for key, value in head.state_dict().items())
        if changed == 0:
            raise ValueError("Candidate did not update the action head")
        candidate = dict(mortal=state["mortal"], current_dqn={k: v.detach().cpu() for k,v in head.state_dict().items()},
                         config=copy.deepcopy(state["config"]))
        candidate["config"]["env"] = dict(candidate["config"].get("env", {}), pts=utility, gamma=1)
        candidate["finetune"] = dict(config=cfg, objective=name, terminal_utility=utility, steps=steps,
                                      baseline_sha256=cfg["baseline_sha256"], backbone_and_bn_frozen=True,
                                      original_optimizer_resumed=False, grp_used=False, auxiliary_rank_head_used=False)
        path = output/f"candidate-{name}.pth"
        torch.save(candidate, path)
        torch.save(dict(optimizer=optimizer.state_dict(), steps=steps, sampling_generator=generator.get_state(),
                        config=cfg, objective=name), output/f"optimizer-{name}.pth")
        # Check the deployable checkpoint, including every BatchNorm buffer.
        saved = torch.load(path, map_location="cpu", weights_only=True)
        assert all(torch.equal(v, saved["mortal"][k]) for k,v in state["mortal"].items())
        results[name] = dict(path=path.name, sha256=file_hash(path), steps=steps, seconds=seconds,
                             sampled_decisions_per_second=steps*cfg["training_batch_size"]/seconds,
                             peak_vram_bytes=torch.cuda.max_memory_allocated(), changed_head_tensors=changed,
                             backbone_and_bn_bitwise_equal=True, terminal_utility=utility, final_loss=final_loss)
        print(f"head {name}: {steps} steps in {seconds:.2f}s; checkpoint {results[name]['sha256']}", flush=True)
    write_json(output/"training-report.json", results)
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--pilot", action="store_true")
    args = parser.parse_args()
    cfg = json.loads(args.config.read_text(encoding="utf-8"))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    write_json(output/"experiment-config.json", cfg)
    # mortal_runner can be imported before load_runtime changes the working directory.
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
    data, engine, manifest = generate(cfg, output, pilot=args.pilot)
    fit(cfg, data, engine, output, pilot=args.pilot)
    print(json.dumps({key: manifest[key] for key in ("hardware", "elapsed_seconds", "simulated_matches_per_hour", "encoded_decisions_per_second", "peak_vram_bytes")}), flush=True)


if __name__ == "__main__":
    main()

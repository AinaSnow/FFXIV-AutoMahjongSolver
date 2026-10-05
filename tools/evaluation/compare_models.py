"""Paired Mortal checkpoint comparison in standard or experimental Doman arenas."""
import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import random
import subprocess
import sys
import time

from progress_observation import MODES, NATIVE, apply_candidate_transform, describe_ablation
from arena import report
from doman_engine import load_doman, validate_match_events
from rule_profile import rule_profile, REQUESTED_MODES
from self_play import canonical_identity, inspect_log, seeds_for, SEED_KEY, write_json


def match_outcome(events, seat, outcome):
    scores = list(outcome.take_final_scores())
    ranks = [int(rank) + 1 for rank in outcome.take_rank_by_player()]
    if len(scores) != 4 or sorted(ranks) != [1, 2, 3, 4]:
        raise ValueError("Missing final scores or distinct final placements")
    hands = wins = deal_ins = 0
    won = dealt = False
    for event in events:
        if event["type"] == "start_kyoku":
            won = dealt = False
        elif event["type"] == "hora":
            won |= event["actor"] == seat
            dealt |= event["target"] == seat and event["actor"] != seat
        elif event["type"] == "end_kyoku":
            hands += 1
            wins += int(won)
            deal_ins += int(dealt)
    if not hands:
        raise ValueError("Match contains no completed hands")
    return dict(seat=seat, rank=ranks[seat], score=int(scores[seat]),
                score_delta=int(scores[seat])-25000, hands=hands, wins=wins,
                deal_ins=deal_ins, gameplay_sha256=canonical_identity(events))


def read_results(directory, seed, role, grp, match_mode="libriichi-hanchan"):
    expected = [directory / f"{seed}_{SEED_KEY}_{letter}.json.gz" for letter in "abcd"]
    if set(directory.glob(f"{seed}_{SEED_KEY}_*.json.gz")) != set(expected):
        raise ValueError("Expected exactly four logs, one per rotated seat")
    results = []
    for seat, path in enumerate(expected):
        raw, events = inspect_log(path, seed)
        validate_match_events(events, match_mode)
        names = ["opponent"]*4
        names[seat] = role
        if events[0].get("names") != names:
            raise ValueError("Log model names do not match the expected seat assignment")
        result = match_outcome(events, seat, grp.load_log(raw))
        result.update(log=path.name, log_sha256=hashlib.sha256(path.read_bytes()).hexdigest())
        results.append(result)
    return results



def read_batch_results(directory, seed_start, seed_count, role, grp, rankings, match_mode="libriichi-hanchan"):
    expected = {directory/f"{seed}_{SEED_KEY}_{letter}.json.gz"
                for seed in range(seed_start, seed_start+seed_count) for letter in "abcd"}
    if set(directory.glob("*.json.gz")) != expected:
        raise ValueError("Batch must contain exactly four logs per expected seed")
    matches = {seed: read_results(directory, seed, role, grp, match_mode)
               for seed in range(seed_start, seed_start+seed_count)}
    counts = Counter(match["rank"] for group in matches.values() for match in group)
    if list(rankings) != [counts[rank] for rank in range(1, 5)]:
        raise ValueError("Arena rankings disagree with logged outcomes")
    return matches


def paired_report(groups, split, identities, match_mode="libriichi-hanchan"):
    for group in groups:
        for role in ("candidate", "baseline"):
            if len(group[role]) != 4 or sorted(m["seat"] for m in group[role]) != [0, 1, 2, 3]:
                raise ValueError("Incomplete paired seed group")
    transforms = {role: identity.get("observation_transform", NATIVE) for role, identity in identities.items()}
    ablation = any(value != NATIVE for value in transforms.values())
    same_model = (identities["candidate"]["checkpoint_sha256"] == identities["baseline"]["checkpoint_sha256"]
                  and transforms["candidate"] == transforms["baseline"]
                  and identities["candidate"].get("observation_transform_sha256") == identities["baseline"].get("observation_transform_sha256"))
    if same_model:
        for group in groups:
            left = {m["seat"]: m for m in group["candidate"]}
            right = {m["seat"]: m for m in group["baseline"]}
            fields = ("gameplay_sha256", "rank", "score", "hands", "wins", "deal_ins")
            if any(left[seat][field] != right[seat][field] for seat in range(4) for field in fields):
                raise ValueError("Identical-checkpoint control produced different paired outcomes")
    result = report(groups, split, "mortal", match_mode)
    result.update(comparison="mortal-observation-ablation" if ablation else "mortal-checkpoints", same_model_control=same_model,
                  control_passed=True if same_model else None, models=identities,
                  live_weights_modified=False, ffxiv_rating_gain=None,
                  sample_unit="paired seed group; four seats are correlated",
                  conclusion="same-model control" if same_model else
                             "acceptance statistics only" if result["promotion_statistics_pass"] else
                             "exploratory; insufficient evidence to replace the live model")
    if ablation:
        describe_ablation(result)
        result["doman_promotion_eligible"] = False
    for role in ("candidate", "baseline"):
        counts = Counter(m["rank"] for group in groups for m in group[role])
        result[role]["placement_counts"] = [counts[rank] for rank in range(1, 5)]
    return result


def markdown_report(result):
    title = {"libriichi-hanchan": "标准半庄", "doman-quick": "FF14 东风实验", "doman-full": "FF14 半庄实验"}[result["match_mode"]]
    lines = [f"# Mortal {title}模型对照", "",
             f"独立种子组：{result['groups']}；物理对局：{result['matches']}。同组四座有关联，置信区间按种子组计算。", "",
             "| 指标 | 候选 | 基线 |", "|---|---:|---:|"]
    for label, key, percent in (("平均名次", "average_rank", False), ("前二率", "top_two_rate", True),
                                ("一位率", "first_rate", True), ("四位率", "fourth_rate", True),
                                ("每手和牌率", "win_rate", True), ("每手放铳率", "deal_in_rate", True),
                                ("平均牌桌得失分", "average_score_delta", False)):
        values = [result[role][key] for role in ("candidate", "baseline")]
        display = ["未知" if value is None else f"{value:.1%}" if percent else f"{value:.3f}" for value in values]
        lines.append(f"| {label} | {display[0]} | {display[1]} |")
    lines += ["", f"平均名次差（候选−基线，负数更好）：{result['rank_delta']:.4f}；95% 区间：{result['rank_delta_95_ci']}。",
              f"四位率差：{result['fourth_delta']:.4f}；95% 区间：{result['fourth_delta_95_ci']}。", "",
              "同模型对照检查通过。" if result["same_model_control"] else "探索性结果不构成更换线上模型的依据。",
              f"模式：{result['match_mode']}。FF14 规则尚未完全对齐；牌桌得失分不是 Rating 收益。模型未自动替换。", "",
              "## 权重身份", ""]
    lines += [f"- {role}: `{result['models'][role]['checkpoint_sha256']}`; observation={result['models'][role].get('observation_transform', NATIVE)}" for role in ("candidate", "baseline", "opponent")]
    lines += ["", "## 规则边界", ""] + [f"- {difference}" for difference in result["rule_differences"]]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mortal-dir", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True, help="Checkpoint path relative to mortal-dir or absolute")
    parser.add_argument("--baseline", type=Path, help="Default: configured live checkpoint, read only")
    parser.add_argument("--opponent", type=Path, help="Default: baseline checkpoint for all three opponents")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--split", choices=("development", "acceptance"), default="development")
    parser.add_argument("--groups", type=int, default=2)
    parser.add_argument("--offset", type=int, default=200)
    parser.add_argument("--threads", type=int, default=2)
    parser.add_argument("--batch-groups", type=int, default=1, help="Seed groups per GPU batch; statistics remain paired by seed")
    parser.add_argument("--device", help="Default: configured device; no online configuration is modified")
    parser.add_argument("--match-mode", choices=REQUESTED_MODES, default="libriichi-hanchan")
    parser.add_argument("--doman-engine", type=Path, help="Isolated build directory; required for Doman modes")
    parser.add_argument("--candidate-observation", choices=MODES, default=NATIVE,
                        help="Offline progress ablation; same candidate/baseline weights, doman-quick only")
    args = parser.parse_args()
    try:
        rules = rule_profile(args.match_mode, allow_experimental=True)
        if args.candidate_observation != NATIVE and args.match_mode != "doman-quick":
            raise ValueError("Progress ablation requires doman-quick")
        if args.candidate_observation != NATIVE:
            describe_ablation(rules)
        seeds = seeds_for(args.split, args.offset, args.groups)
        if not 1 <= args.batch_groups <= 256:
            raise ValueError("batch-groups must be between 1 and 256")
        if args.threads < 1:
            raise ValueError("threads must be positive")
        if args.output.exists():
            raise ValueError("Output exists; choose a fresh run directory")
    except ValueError as error:
        parser.error(str(error))
    native_build = None
    if args.match_mode != "libriichi-hanchan":
        try:
            OneVsThree, Grp, native_build = load_doman(args.doman_engine)
        except (ValueError, OSError, ImportError) as error:
            parser.error(str(error))
    mortal_dir, output = args.mortal_dir.resolve(), args.output.resolve()
    os.chdir(mortal_dir)
    sys.path[:0] = [str(Path(__file__).resolve().parents[1]), str(mortal_dir)]
    import torch
    import numpy as np
    from config import config
    from mortal_runner import create_engine
    if native_build is None:
        from libriichi.arena import OneVsThree
        from libriichi.dataset import Grp
    torch.set_num_threads(args.threads)
    torch.backends.cudnn.benchmark = False
    torch.backends.cudnn.deterministic = True
    paths = dict(candidate=args.candidate, baseline=args.baseline or Path(config["control"]["state_file"]))
    paths["opponent"] = args.opponent or paths["baseline"]
    engines = {role: create_engine(checkpoint_path=path, name=role, device_name=args.device, enable_amp=False)
               for role, path in paths.items()}
    apply_candidate_transform(engines, args.candidate_observation, args.match_mode)
    identities = {role: engine.model_identity.copy() for role, engine in engines.items()}
    engine_commit = subprocess.check_output(["git", "-C", str(mortal_dir), "rev-parse", "HEAD"], text=True).strip()
    manifest = dict(schema_version=1, status="running", comparison="mortal-checkpoints" if args.candidate_observation == NATIVE else "mortal-observation-ablation", rules=rules,
                    native_build=native_build, models=identities, model_paths={role: str(path.resolve()) for role, path in paths.items()},
                    engine_commit=engine_commit, source_hashes={name: hashlib.sha256((mortal_dir/name).read_bytes()).hexdigest()
                        for name in ("engine.py", "model.py", "config.toml")},
                    evaluator_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                    torch_version=torch.__version__, devices={role: str(engine.device) for role, engine in engines.items()},
                    threads=args.threads, inference=dict(oracle=False, epsilon=0, stochastic_latent=False,
                        amp=False, quick_eval=True, rule_based_agari_guard=True, cudnn_benchmark=False, cudnn_deterministic=True),
                    split=args.split, seed_start=seeds.start, seed_end_exclusive=seeds.stop, seed_key=SEED_KEY,
                    requested_groups=args.groups, batch_groups=args.batch_groups, completed_groups=0, live_weights_modified=False,
                    model_provenance_verified=False, ffxiv_rating_gain=None)
    output.mkdir(parents=True, exist_ok=False)
    write_json(output/"manifest.json", manifest)
    groups = []
    started = time.monotonic()
    try:
        with (output/"results.jsonl").open("w", encoding="utf-8") as sink:
            for index, seed_start in enumerate(range(seeds.start, seeds.stop, args.batch_groups)):
                seed_count = min(args.batch_groups, seeds.stop-seed_start)
                batch = {}
                # Alternate batches; both roles always use the same seed range and batch size.
                order = ("candidate", "baseline") if index % 2 == 0 else ("baseline", "candidate")
                for role in order:
                    random.seed(seed_start); np.random.seed(seed_start); torch.manual_seed(seed_start)
                    directory = output/f"batch-{seed_start}"/role
                    arena_options = dict(match_mode=args.match_mode) if native_build else {}
                    rankings = OneVsThree(disable_progress_bar=True, log_dir=str(directory), **arena_options).py_vs_py(
                        engines[role], engines["opponent"], (seed_start, SEED_KEY), seed_count)
                    batch[role] = read_batch_results(directory, seed_start, seed_count, role, Grp, rankings, args.match_mode)
                    for matches in batch[role].values():
                        for match in matches:
                            match["log"] = (directory/match["log"]).relative_to(output).as_posix()
                    print(f"seeds {seed_start}..{seed_start+seed_count-1}: {role} {seed_count*4} seats complete", flush=True)
                complete_groups = [dict(seed=seed, key=SEED_KEY, candidate=batch["candidate"][seed], baseline=batch["baseline"][seed])
                                   for seed in range(seed_start, seed_start+seed_count)]
                # Validate every pair before publishing the batch. Keep seeds as the statistical unit.
                paired_report(complete_groups, args.split, identities, args.match_mode)
                for group in complete_groups:
                    sink.write(json.dumps(group)+"\n")
                sink.flush()
                groups.extend(complete_groups)
                manifest.update(completed_groups=len(groups), elapsed_seconds=time.monotonic()-started)
                write_json(output/"manifest.json", manifest)
                print(f"completed paired seed groups {len(groups)}/{args.groups}", flush=True)
        result = paired_report(groups, args.split, identities, args.match_mode)
        write_json(output/"report.json", result)
        (output/"report.md").write_text(markdown_report(result), encoding="utf-8")
        manifest.update(status="complete", elapsed_seconds=time.monotonic()-started,
                        candidate_adapted_observations=getattr(engines["candidate"], "adapted_observations", 0))
        write_json(output/"manifest.json", manifest)
        print(json.dumps({key: result[key] for key in ("groups", "matches", "rank_delta", "rank_delta_95_ci", "fourth_delta", "fourth_delta_95_ci", "conclusion")}), flush=True)
    except BaseException as error:
        manifest.update(status="failed", error=f"{type(error).__name__}: {error}", elapsed_seconds=time.monotonic()-started)
        write_json(output/"manifest.json", manifest)
        raise


if __name__ == "__main__":
    main()

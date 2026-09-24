"""Paired one-versus-three evaluation. Each seed is four rotated seats per version."""
import argparse
import hashlib
import gzip
import importlib
import json
import math
from pathlib import Path
import random
import shlex
import statistics
import subprocess
import sys
from mjai_adapter import PolicyEngine
from rule_profile import rule_profile, REQUESTED_MODES

SPLITS = {"train": 100_000, "development": 1_000_000, "acceptance": 10_000_000}


def paired_interval(values, replicates=10000):
    if len(values) < 2:
        return None
    rng = random.Random(90731)
    means = sorted(statistics.mean(rng.choices(values, k=len(values))) for _ in range(replicates))
    return [means[int(replicates * .025)], means[int(replicates * .975)]]


def report(groups, split, opponent="mortal", match_mode="libriichi-hanchan"):
    rules = rule_profile(match_mode)
    rank_delta = [statistics.mean(m["rank"] for m in g["candidate"]) - statistics.mean(m["rank"] for m in g["baseline"]) for g in groups]
    fourth_delta = [statistics.mean(m["rank"] == 4 for m in g["candidate"]) - statistics.mean(m["rank"] == 4 for m in g["baseline"]) for g in groups]
    rank_ci, fourth_ci = paired_interval(rank_delta), paired_interval(fourth_delta)
    summary = dict(groups=len(groups), matches=len(groups)*8, split=split,
                   rank_delta=statistics.mean(rank_delta), rank_delta_95_ci=rank_ci,
                   fourth_delta=statistics.mean(fourth_delta), fourth_delta_95_ci=fourth_ci)
    for variant in ("candidate", "baseline"):
        matches = [m for group in groups for m in group[variant]]
        summary[variant] = dict(average_rank=statistics.mean(m["rank"] for m in matches),
                               first_rate=statistics.mean(m["rank"]==1 for m in matches),
                               fourth_rate=statistics.mean(m["rank"]==4 for m in matches),
                               average_score=statistics.mean(m["score"] for m in matches),
                               average_score_delta=statistics.mean(m["score"]-25000 for m in matches),
                               win_rate=(sum(m.get("wins",0) for m in matches)/sum(m.get("hands",0) for m in matches)) if sum(m.get("hands",0) for m in matches) else None,
                               deal_in_rate=(sum(m.get("deal_ins",0) for m in matches)/sum(m.get("hands",0) for m in matches)) if sum(m.get("hands",0) for m in matches) else None)
    summary["promotion_statistics_pass"] = bool(opponent=="mortal" and split=="acceptance" and len(groups)>=1000 and rank_ci and fourth_ci
        and summary["rank_delta"]<=-.03 and rank_ci[1]<0 and fourth_ci[1]<=.01)
    summary["promotion_requires_clean_protocol_and_action_regressions"] = True
    summary["scope"] = "libriichi strategy results; not live-game win rate or execution reliability"
    summary["match_mode"] = rules["match_mode"]
    summary["scheduled_rounds"] = rules["scheduled_rounds"]
    summary["rule_differences"] = rules["rule_differences"]
    # Passing standard-arena statistics is insufficient to change the live Doman default.
    summary["doman_promotion_eligible"] = False
    return summary


def add_outcomes(engine, directory, seed, split):
    labelled=[]
    for match in engine.results:
        seat=match["seat"]
        path=next(directory.glob(f"*_{chr(97+seat)}.json.gz"))
        outcomes={}; current=None
        for line in gzip.open(path,"rt"):
            event=json.loads(line)
            if event["type"]=="start_kyoku":
                current=f"{event['bakaze']}:{event['kyoku']}:{event['honba']}"
                outcomes[current]=dict(won=0,deal_in=0,points=0)
            elif current and event["type"] in ("hora","ryukyoku"):
                if event["type"]=="hora":
                    outcomes[current]["won"] |= int(event["actor"]==seat)
                    outcomes[current]["deal_in"] |= int(event["target"]==seat and event["actor"]!=seat)
                outcomes[current]["points"]+=event.get("deltas",[0]*4)[seat]
        match.update(hands=len(outcomes),wins=sum(o["won"] for o in outcomes.values()),
                     deal_ins=sum(o["deal_in"] for o in outcomes.values()),score_delta=match["score"]-25000)
        for sample in engine.samples:
            if sample["game_index"]==match["index"]:
                labelled.append(dict(**sample,**outcomes[sample["hand_key"]],rank=match["rank"],seed=seed,split=split))
    return labelled


def main():
    p=argparse.ArgumentParser()
    p.add_argument("--policy-command",required=True,help="JSON array of executable and arguments")
    p.add_argument("--mortal-dir",type=Path,required=True)
    p.add_argument("--opponent",default="mortal",choices=("mortal","tsumogiri"))
    p.add_argument("--split",choices=SPLITS,default="development")
    p.add_argument("--groups",type=int,default=1000)
    p.add_argument("--offset",type=int,default=0)
    p.add_argument("--output",type=Path,required=True)
    p.add_argument("--calibration-path",help="Path understood by the C# process; trained artifact only")
    p.add_argument("--match-mode", default="libriichi-hanchan", choices=REQUESTED_MODES)
    args=p.parse_args()
    try:
        rules = rule_profile(args.match_mode)
    except ValueError as ex:
        p.error(str(ex))
    if not 1<=args.groups<=100000 or not 0<=args.offset<100000 or args.offset+args.groups>100000:
        p.error("groups/offset must stay within the split's disjoint 100000-seed range")
    if args.output.exists():
        p.error("output already exists; choose a new run directory")
    args.output.mkdir(parents=True)
    sys.path.insert(0,str(args.mortal_dir.resolve()))
    from libriichi.arena import OneVsThree
    if args.opponent=="mortal":
        sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
        from mortal_runner import create_engine
        opponent = create_engine()
        from config import config
        model = Path(config["control"]["state_file"])
        model_hash = hashlib.sha256(model.read_bytes()).hexdigest()
    else:
        from engine import ExampleMjaiLogEngine
        opponent = ExampleMjaiLogEngine("tsumogiri-smoke")
        model_hash = None
    engine_commit = subprocess.check_output(["git","-C",str(args.mortal_dir),"rev-parse","HEAD"],text=True).strip()
    manifest = dict(schema=1,engine="libriichi",engine_commit=engine_commit,model_sha256=model_hash,
                    policy_command=json.loads(args.policy_command),split=args.split,seed_start=SPLITS[args.split]+args.offset,
                    groups=args.groups,opponent=args.opponent,rule_set=rules["rule_set"],rules=rules,candidate="enhanced",baseline="stable",
                    policy_build=json.loads(subprocess.check_output(json.loads(args.policy_command)+["--describe"],text=True)),
                    calibration_path=args.calibration_path)
    (args.output/"manifest.json").write_text(json.dumps(manifest,indent=2))
    groups=[]
    for index in range(args.groups):
        seed=SPLITS[args.split]+args.offset+index
        group=dict(seed=seed,key=42)
        for name in ("candidate","baseline"):
            engine = PolicyEngine(manifest["policy_command"],name,enhanced=name=="candidate",calibration_path=args.calibration_path if name=="candidate" else None,match_mode=args.match_mode)
            try:
                arena=OneVsThree(disable_progress_bar=True,log_dir=str(args.output/f"seed-{seed}"/name))
                arena.py_vs_py(engine,opponent,(seed,42),1)
                if len(engine.results)!=4 or sorted(m["seat"] for m in engine.results)!=[0,1,2,3]:
                    raise RuntimeError("arena did not complete all four candidate seats")
                samples=add_outcomes(engine,args.output/f"seed-{seed}"/name,seed,args.split)
                if args.split=="train":
                    with (args.output/"training-decisions.jsonl").open("a") as f:
                        for sample in samples: f.write(json.dumps(sample)+"\n")
                group[name]=engine.results
            finally:
                engine.close()
        groups.append(group)
        with (args.output/"results.jsonl").open("a") as f:
            f.write(json.dumps(group)+"\n")
        print(f"completed seed group {index+1}/{args.groups}",flush=True)
    (args.output/"report.json").write_text(json.dumps(report(groups,args.split,args.opponent,args.match_mode),indent=2))


if __name__=="__main__":
    main()

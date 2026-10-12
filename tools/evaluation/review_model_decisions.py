"""Review first gameplay divergence in completed paired model comparisons."""
import argparse
from collections import Counter, defaultdict
import gzip
import json
from pathlib import Path


def events(path):
    with gzip.open(path, "rt", encoding="utf-8") as stream:
        return [{k:v for k,v in json.loads(line).items() if k not in ("meta", "names", "seed")} for line in stream if line.strip()]


def review(directory):
    pairs = [json.loads(line) for line in (directory/"results.jsonl").read_text().splitlines()]
    rows=[]
    for pair in pairs:
        for candidate, baseline in zip(pair["candidate"],pair["baseline"]):
            assert candidate["seat"]==baseline["seat"]
            seat=candidate["seat"]
            left,right=events(directory/candidate["log"]),events(directory/baseline["log"])
            current=None; scores=None; hand_index=-1; turn=0
            row=dict(seed=pair["seed"],seat=seat,rank_delta=candidate["rank"]-baseline["rank"],
                     top_two_delta=int(candidate["rank"]<=2)-int(baseline["rank"]<=2),
                     fourth_delta=int(candidate["rank"]==4)-int(baseline["rank"]==4))
            for i,(a,b) in enumerate(zip(left,right)):
                if a!=b:
                    order=sorted(range(4),key=lambda player:(-scores[player],player))
                    own=lambda ev: ev.get("actor")==seat and ev["type"] in ("dahai","reach","chi","pon","ankan","kakan","daiminkan","hora")
                    row.update(identical=False,event_index=i,hand_index=hand_index,kyoku=current["kyoku"],honba=current["honba"],
                               current_rank=order.index(seat)+1,score=scores[seat],turn=turn,
                               score_position=("tied" if scores.count(scores[seat])>1 else "top-two" if order.index(seat)<2 else "bottom-two"),
                               candidate_event=a,baseline_event=b,own_decision=own(a) or own(b))
                    break
                if a["type"]=="start_kyoku":
                    current=a; scores=a["scores"].copy(); hand_index+=1; turn=0
                elif a["type"]=="reach_accepted": scores[a["actor"]]-=1000
                elif a["type"]=="dahai" and a["actor"]==seat: turn+=1
                elif a["type"] in ("hora","ryukyoku"):
                    scores=[v+d for v,d in zip(scores,a["deltas"])]
            else:
                assert len(left)==len(right)
                row.update(identical=True)
            rows.append(row)
    different=[r for r in rows if not r["identical"]]
    grouped={}
    for field in ("kyoku","current_rank","score_position","own_decision"):
        result={}
        for value in sorted({r[field] for r in different}):
            selected=[r for r in different if r[field]==value]
            result[str(value)]=dict(matches=len(selected),rank_delta_sum=sum(r["rank_delta"] for r in selected),
                                    top_two_delta_sum=sum(r["top_two_delta"] for r in selected),
                                    fourth_delta_sum=sum(r["fourth_delta"] for r in selected))
        grouped[field]=result
    transitions=Counter(f"{r['baseline_event']['type']} -> {r['candidate_event']['type']}" for r in different)
    return dict(matches=len(rows),identical=len(rows)-len(different),different=len(different),
                first_divergence_transitions=dict(transitions),groups=grouped,
                interpretation="First divergence descriptive audit; later outcomes are whole-policy comparisons, not causal labels for this one action"),rows


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory",type=Path)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    args.output.mkdir(parents=True,exist_ok=False)
    summary,rows=review(args.directory)
    (args.output/"summary.json").write_text(json.dumps(summary,indent=2),encoding="utf-8")
    (args.output/"first-divergences.jsonl").write_text("".join(json.dumps(row)+"\n" for row in rows),encoding="utf-8")
    print(json.dumps(summary,indent=2))


if __name__=="__main__": main()

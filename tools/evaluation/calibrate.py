"""Fit empirical rank/win/deal-in buckets from labelled training decisions only."""
import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path

def fit(path):
    raw=path.read_bytes(); buckets=defaultdict(list)
    for line in raw.decode().splitlines():
        row=json.loads(line)
        if row.get("split")!="train" or not 100000<=row.get("seed",-1)<200000:
            raise ValueError("Calibration accepts training seeds only; development/acceptance leakage rejected")
        if not 1<=row["rank"]<=4 or row["won"] not in (0,1) or row["deal_in"] not in (0,1):
            raise ValueError("Invalid labelled outcome")
        buckets[row["key"]].append(row)
    if not buckets: raise ValueError("Empty training dataset")
    return dict(SchemaVersion=1,Split="train",DatasetSha256=hashlib.sha256(raw).hexdigest(),Buckets={
        key:dict(Samples=len(rows),ExpectedRank=sum(r["rank"] for r in rows)/len(rows),
                 WinProbability=sum(r["won"] for r in rows)/len(rows),
                 DealInProbability=sum(r["deal_in"] for r in rows)/len(rows),
                 ExpectedPoints=sum(r["points"] for r in rows)/len(rows))
        for key,rows in buckets.items()})

if __name__=="__main__":
    p=argparse.ArgumentParser();p.add_argument("decisions",type=Path);p.add_argument("output",type=Path);a=p.parse_args()
    a.output.write_text(json.dumps(fit(a.decisions),indent=2))

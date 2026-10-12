"""Cross-fitted East-match rank value, followed by one frozen-head candidate C."""
import argparse
import copy
from itertools import permutations
import json
import math
import os
from pathlib import Path
import sys
import time

import numpy as np
import torch
from torch import nn
from torch.nn import functional as F

from doman_engine import load_doman_dataset
from self_play import inspect_log, canonical_identity, write_json
from self_play_dataset import file_hash

PERMS = torch.tensor(list(permutations(range(4))), dtype=torch.long)
UTILITY = torch.tensor([1., 1., 0., -.25], dtype=torch.float64)


class ScoreRankModel(nn.Module):
    """Plackett-Luce rank distribution with four fitted East-round scales."""
    def __init__(self):
        super().__init__()
        scales = torch.tensor([2., math.sqrt(3), math.sqrt(2), 1.], dtype=torch.float64)
        self.raw_scales = nn.Parameter(torch.log(torch.expm1(scales-.05)))
        self.register_buffer("order", PERMS.argsort(-1))

    def forward(self, prefixes):
        current = torch.stack([prefix[-1] for prefix in prefixes])
        scale = F.softplus(self.raw_scales)[current[:, 0].long()] + .05
        strengths = current[:, 3:7] / scale[:, None]
        ordered = strengths[:, self.order]
        denominators = ordered.flip(-1).logcumsumexp(-1).flip(-1)
        return (ordered-denominators).sum(-1)


def rank_probabilities(logits):
    matrix = F.one_hot(PERMS.to(logits.device), 4).to(logits.dtype)
    return torch.einsum("nc,cpr->npr", logits.softmax(-1), matrix)


def hand_rewards(values, ranks):
    final = UTILITY.to(values.device)[ranks]
    return torch.cat((values[1:], final[None])) - values


def load_matches(cfg, output, loader_type, grp_type):
    run = Path(cfg["training_run"])
    manifest = json.loads((run/"data-manifest.json").read_text())
    assert file_hash(run/"features.pt") == manifest["features_sha256"]
    assert file_hash(run/"matches.jsonl") == manifest["matches_sha256"]
    data = torch.load(run/"features.pt", map_location="cpu", weights_only=True)
    loader = loader_type(version=4, oracle=False, trust_seed=False, match_mode="doman-quick")
    matches, mapping = [], []
    offset = 0
    for row in map(json.loads, (run/"matches.jsonl").read_text().splitlines()):
        if row["duplicate"]:
            continue
        path = run/row["path"]
        assert file_hash(path) == row["sha256"]
        raw, events = inspect_log(path, row["seed"])
        assert canonical_identity(events) == row["match_id"]
        grp = grp_type.load_log(raw)
        features = torch.as_tensor(np.asarray(grp.take_feature()), dtype=torch.float64)
        ranks = torch.tensor(list(grp.take_rank_by_player()), dtype=torch.long)
        matches.append(dict(seed=row["seed"], identity=row["match_id"], features=features, ranks=ranks))
        for game in loader.load_log(raw):
            player = game.take_player_id()
            actions = torch.tensor(game.take_actions(), dtype=torch.long)
            n = len(actions)
            if not n:
                continue
            at_hand = torch.tensor(list(game.take_at_kyoku()), dtype=torch.long)
            assert torch.equal(data["actions"][offset:offset+n], actions)
            assert (data["ranks"][offset:offset+n] == ranks[player]).all()
            assert torch.equal(data["masks"][offset:offset+n], torch.as_tensor(np.asarray(game.take_masks())))
            mapping.append(dict(match=len(matches)-1, player=player, offset=offset, n=n, at_hand=at_hand))
            offset += n
        if len(matches) % 50 == 0:
            print(f"aligned {len(matches)} training matches / {offset} decisions", flush=True)
    assert offset == len(data["actions"])
    # Baseline-only existing development games: deterministic seat duplicates count once.
    directory = Path(cfg["calibration_directory"])
    held_out, seen = [], set()
    for pair in map(json.loads, (directory/"results.jsonl").read_text().splitlines()):
        for result in pair["baseline"]:
            raw, events = inspect_log(directory/result["log"], pair["seed"])
            identity = canonical_identity(events)
            if identity in seen:
                continue
            seen.add(identity)
            grp = grp_type.load_log(raw)
            held_out.append(dict(seed=pair["seed"], identity=identity,
                                 features=torch.as_tensor(np.asarray(grp.take_feature()), dtype=torch.float64),
                                 ranks=torch.tensor(list(grp.take_rank_by_player()), dtype=torch.long)))
    assert {m["seed"] for m in matches}.isdisjoint(m["seed"] for m in held_out)
    assert {m["identity"] for m in matches}.isdisjoint(seen)
    torch.save(dict(matches=matches, held_out=held_out, mapping=mapping), output/"rank-data.pt")
    return data, matches, held_out, mapping


def prefixes(matches, device):
    seq, labels, weights, owners = [], [], [], []
    for match_index, match in enumerate(matches):
        sequence = match["features"].to(device)
        label = (PERMS == match["ranks"]).all(-1).nonzero().item()
        for end in range(1, len(sequence)+1):
            seq.append(sequence[:end]); labels.append(label)
            weights.append(1/len(sequence)); owners.append(match_index)
    return seq, torch.tensor(labels, device=device), torch.tensor(weights, dtype=torch.float64), owners


def train_predictor(family, matches, cfg, output, suffix):
    from model import GRP
    torch.manual_seed(cfg["rank_seed"])
    model = (GRP(hidden_size=32, num_layers=1) if family == "gru" else ScoreRankModel()).cuda()
    seq, labels, weights, _ = prefixes(matches, "cuda")
    generator = torch.Generator().manual_seed(cfg["rank_seed"])
    samples = torch.multinomial(weights, cfg["rank_steps"]*cfg["rank_batch_size"], replacement=True, generator=generator)
    optimizer = torch.optim.Adam(model.parameters(), lr=cfg["gru_lr"] if family == "gru" else cfg["score_lr"])
    started = time.monotonic()
    for selection in samples.split(cfg["rank_batch_size"]):
        logits = model([seq[i] for i in selection.tolist()])
        loss = F.cross_entropy(logits, labels[selection.cuda()])
        assert torch.isfinite(loss)
        optimizer.zero_grad(set_to_none=True); loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), 5.)
        optimizer.step()
        if time.monotonic()-started > cfg["max_rank_fit_seconds"]:
            raise TimeoutError("Rank predictor budget exhausted")
    model.eval()
    torch.save(dict(model={k:v.cpu() for k,v in model.state_dict().items()}, family=family,
                    training_seeds=sorted({m["seed"] for m in matches}), config=cfg), output/f"rank-{family}-{suffix}.pth")
    print(f"{family} {suffix}: {len(matches)} matches, {cfg['rank_steps']} steps, {time.monotonic()-started:.1f}s", flush=True)
    return model


def measure(model, matches, *, uniform=False):
    seq, labels, _, owners = prefixes(matches, "cuda")
    parts = []
    with torch.inference_mode():
        for start in range(0, len(seq), 128):
            parts.append(torch.zeros((len(seq[start:start+128]),24), device="cuda", dtype=torch.float64)
                         if uniform else model(seq[start:start+128]))
        logits = torch.cat(parts)
        nll = F.cross_entropy(logits, labels, reduction="none").cpu()
        prob = rank_probabilities(logits).cpu()
        true = F.one_hot(PERMS[labels.cpu()],4).double()
        utility_mse = ((prob @ UTILITY - true @ UTILITY)**2).mean(-1)
        brier = ((prob[:,:,:2].sum(-1)-true[:,:,:2].sum(-1))**2).mean(-1)
    per_match = []
    owners = torch.tensor(owners)
    for i, match in enumerate(matches):
        mask = owners == i
        per_match.append(dict(seed=match["seed"], nll=nll[mask].mean().item(),
                              utility_mse=utility_mse[mask].mean().item(), top_two_brier=brier[mask].mean().item()))
    return dict(**{key:sum(row[key] for row in per_match)/len(per_match) for key in ("nll","utility_mse","top_two_brier")}, per_match=per_match)


def fit_policy(cfg, data, targets, output, family):
    from model import DQN
    from mortal_runner import load_checkpoint
    original = json.loads((Path(cfg["training_run"])/"experiment-config.json").read_text())
    state, identity = load_checkpoint(torch, original["baseline"])
    assert identity["checkpoint_sha256"] == original["baseline_sha256"]
    head = DQN(version=4).cuda(); head.load_state_dict(state["current_dqn"])
    phi, masks, actions = (data[key].cuda() for key in ("phi","masks","actions"))
    targets = targets.float().cuda()
    optimizer = torch.optim.AdamW(head.parameters(), lr=original["learning_rate"], weight_decay=original["weight_decay"])
    generator = torch.Generator().manual_seed(original["training_seed"])
    samples = torch.multinomial(data["weights"], original["steps"]*original["training_batch_size"], replacement=True, generator=generator)
    started = time.monotonic(); losses=[]
    for step, selection in enumerate(samples.split(original["training_batch_size"]),1):
        idx = selection.cuda(); q = head(phi[idx], masks[idx]); taken = q.gather(1,actions[idx,None]).squeeze(1)
        regression = .5*F.mse_loss(taken, targets[idx]); cql = (q.logsumexp(-1)-taken).mean()
        loss = regression+original["cql_weight"]*cql
        assert torch.isfinite(loss)
        optimizer.zero_grad(set_to_none=True); loss.backward()
        torch.nn.utils.clip_grad_norm_(head.parameters(),original["max_grad_norm"]); optimizer.step()
        if step == 1 or step%100 == 0:
            losses.append(dict(step=step,regression=regression.item(),cql=cql.item()))
    torch.cuda.synchronize()
    candidate = dict(mortal=state["mortal"], current_dqn={k:v.detach().cpu() for k,v in head.state_dict().items()}, config=copy.deepcopy(state["config"]))
    candidate["config"]["env"] = dict(candidate["config"].get("env",{}),pts=UTILITY.tolist(),gamma=1)
    candidate["finetune"] = dict(objective="C",reward="cross-fitted next-hand expected terminal utility delta",config=cfg,
                                  baseline_sha256=identity["checkpoint_sha256"],rank_model_family=family,steps=original["steps"],
                                  backbone_and_bn_frozen=True, original_optimizer_resumed=False)
    path = output/"candidate-C.pth"; torch.save(candidate,path)
    saved = torch.load(path,map_location="cpu",weights_only=True)
    assert all(torch.equal(v,saved["mortal"][k]) for k,v in state["mortal"].items())
    torch.save(dict(optimizer=optimizer.state_dict(),config=cfg,steps=original["steps"]),output/"optimizer-C.pth")
    report = dict(sha256=file_hash(path),baseline_sha256=identity["checkpoint_sha256"],steps=original["steps"],
                  backbone_and_bn_bitwise_equal=True,seconds=time.monotonic()-started,losses=losses,
                  reward_mean=targets.mean().item(),reward_std=targets.std().item())
    write_json(output/"training-report.json", report)
    print(json.dumps(report),flush=True)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config",type=Path,required=True); parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args(); cfg=json.loads(args.config.read_text()); output=args.output.resolve()
    output.mkdir(parents=True,exist_ok=False); write_json(output/"experiment-config.json",cfg)
    _,loader_type,grp_type,build=load_doman_dataset(cfg["doman_engine"])
    os.chdir(cfg["mortal_dir"]); sys.path[:0]=[cfg["mortal_dir"],str(Path(__file__).resolve().parents[1])]
    torch.set_num_threads(2); torch.backends.cudnn.benchmark=False; torch.backends.cudnn.deterministic=True
    started=time.monotonic()
    data,matches,held_out,mapping=load_matches(cfg,output,loader_type,grp_type)
    reference=ScoreRankModel().cuda().eval()
    metrics=dict(uniform=measure(None,held_out,uniform=True),fixed_score=measure(reference,held_out))
    models={}
    for family in ("score","gru"):
        models[family]=train_predictor(family,matches,cfg,output,"full")
        metrics[family]=measure(models[family],held_out)
    family=min(("score","gru"),key=lambda name:metrics[name]["nll"])
    chosen,reference=metrics[family],metrics["fixed_score"]
    passed=(chosen["nll"] <= reference["nll"]*.99
            and chosen["utility_mse"] <= reference["utility_mse"]
            and chosen["top_two_brier"] <= reference["top_two_brier"])
    calibration=dict(chosen=family,passed=passed,metrics=metrics,
                     selection="lowest held-out permutation NLL; at least 1% better than fixed score model, utility MSE and top-two Brier no worse",
                     training_matches=len(matches),development_matches=len(held_out),live_weights_modified=False)
    write_json(output/"calibration-report.json",calibration)
    print(json.dumps({k:v for k,v in calibration.items() if k!="metrics"}),flush=True)
    if not passed:
        write_json(output/"result.json",dict(status="calibration-rejected",elapsed_seconds=time.monotonic()-started))
        return
    rewards={}; folds=[]
    for fold in range(cfg["folds"]):
        train=[m for m in matches if m["seed"]%cfg["folds"]!=fold]
        validation=[(i,m) for i,m in enumerate(matches) if m["seed"]%cfg["folds"]==fold]
        train_seeds={m["seed"] for m in train}; test_seeds={m["seed"] for _,m in validation}
        assert train_seeds.isdisjoint(test_seeds)
        model=train_predictor(family,train,cfg,output,f"fold-{fold}")
        with torch.inference_mode():
            for index,match in validation:
                feature=match["features"].cuda()
                logits=model([feature[:end] for end in range(1,len(feature)+1)])
                values=(rank_probabilities(logits) @ UTILITY.cuda()).cpu()
                delta=hand_rewards(values,match["ranks"])
                assert torch.allclose(delta.sum(0),UTILITY[match["ranks"]]-values[0],atol=1e-8)
                rewards[index]=delta
        folds.append(dict(fold=fold,train_seeds=sorted(train_seeds),predicted_seeds=sorted(test_seeds)))
    targets=torch.empty(len(data["actions"]),dtype=torch.float64)
    for entry in mapping:
        targets[entry["offset"]:entry["offset"]+entry["n"]]=rewards[entry["match"]][entry["at_hand"],entry["player"]]
    assert torch.isfinite(targets).all()
    torch.save(dict(targets=targets,hand_rewards=rewards,folds=folds),output/"cross-fitted-rewards.pt")
    write_json(output/"cross-fit-seeds.json",folds)
    fit_policy(cfg,data,targets,output,family)
    write_json(output/"result.json",dict(status="candidate-ready",elapsed_seconds=time.monotonic()-started,
                                         family=family,peak_vram_bytes=torch.cuda.max_memory_allocated(),native_build=build))


if __name__=="__main__": main()

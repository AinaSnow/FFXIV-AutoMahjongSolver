"""Full-network Doman East fine-tune of the v4 Mortal checkpoint.

Two modes:
  gen    trainee (v4 + small exploration) plays rotated seats against an opponent
         pool (greedy v4, greedy 298k, FF14-calibrated noisy v4); raw logs only.
  train  Mortal-style offline Q regression (kyoku reward from the Doman East rank
         model, CQL) on all layers except BatchNorm, starting from v4.

Noisy-opponent views are never trained on: CQL raises the Q of logged actions,
so exploratory opponent play would be reinforced. No live weights are touched.
"""
import argparse
import gzip
import json
import os
from pathlib import Path
import random
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))

import numpy as np
import torch
from torch import nn
from torch.nn import functional as F

from doman_engine import load_doman_dataset
from finetune_credit import ScoreRankModel, rank_probabilities
from self_play import seeds_for, SEED_KEY, write_json


def runtime(mortal_dir):
    os.chdir(mortal_dir)
    sys.path[:0] = [str(mortal_dir), str(Path(__file__).resolve().parents[1])]
    torch.set_num_threads(4)


def generate(args):
    arena, _, _, _ = load_doman_dataset(args.doman_engine)
    runtime(args.mortal_dir)
    from mortal_runner import create_engine
    trainee = create_engine(checkpoint_path=args.trainee, name="trainee", device_name="cuda:0", enable_amp=False)
    trainee.boltzmann_epsilon, trainee.boltzmann_temp, trainee.top_p = args.trainee_epsilon, args.trainee_temp, 1
    pool = []
    for spec in args.pool:
        path, eps, temp, tag = spec.split(",")
        engine = create_engine(checkpoint_path=path, name=tag, device_name="cuda:0", enable_amp=False)
        engine.boltzmann_epsilon, engine.boltzmann_temp, engine.top_p = float(eps), float(temp), 1
        pool.append((tag, engine))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    seeds = seeds_for("train", args.offset, args.groups)
    started = time.monotonic()
    with (output/"batches.jsonl").open("a", encoding="utf-8") as sink:
        for index, first in enumerate(range(seeds.start, seeds.stop, args.batch_groups)):
            count = min(args.batch_groups, seeds.stop-first)
            directory = output/"raw"/str(first)
            if directory.exists():
                continue
            tag, opponent = pool[index % len(pool)]
            random.seed(first); np.random.seed(first); torch.manual_seed(first)
            arena(disable_progress_bar=True, log_dir=str(directory), match_mode="doman-quick").py_vs_py(
                trainee, opponent, (first, SEED_KEY), count)
            sink.write(json.dumps(dict(first=first, count=count, opponent=tag,
                                       greedy_opponent=opponent.boltzmann_epsilon == 0))+"\n")
            sink.flush()
            print(f"[{time.monotonic()-started:7.0f}s] seeds {first}+{count} vs {tag}", flush=True)


class Stream(torch.utils.data.IterableDataset):
    """Shuffled buffer over log files; each DataLoader worker owns a file shard
    and yields whole float16 batches."""
    def __init__(self, data_dirs, loader_type, rank_model, pts, buffer_size, seed, batch_size):
        super().__init__()
        self.batch_size = batch_size
        self.files = []
        for data_dir in data_dirs:
            for row in map(json.loads, (data_dir/"batches.jsonl").read_text().splitlines()):
                names = {"trainee"} | ({"opponent"} if row["greedy_opponent"] else set())
                for path in sorted((data_dir/"raw"/str(row["first"])).glob("*.json.gz")):
                    self.files.append((path, names))
        self.loader = loader_type(version=4, oracle=False, trust_seed=False, match_mode="doman-quick")
        self.rank_model, self.pts = rank_model, pts
        self.buffer_size, self.rng = buffer_size, random.Random(seed)

    def rewards(self, grp, player):
        features = torch.as_tensor(np.asarray(grp.take_feature()), dtype=torch.float64)
        ranks = torch.tensor(list(grp.take_rank_by_player()), dtype=torch.long)
        with torch.inference_mode():
            values = rank_probabilities(self.rank_model([features[:end] for end in range(1, len(features)+1)])) @ self.pts
        final = self.pts[ranks]
        return (torch.cat((values[1:], final[None])) - values)[:, player].numpy()

    def samples_of(self, path, names):
        raw = gzip.open(path, "rt", encoding="utf-8").read()
        seat_names = json.loads(raw.split("\n", 1)[0])["names"]
        for game in self.loader.load_log(raw):
            player = game.take_player_id()
            if seat_names[player] not in names:
                continue
            obs = np.asarray(game.take_obs(), dtype=np.float16)
            if not len(obs):
                continue
            actions = np.asarray(game.take_actions(), dtype=np.int64)
            masks = np.asarray(game.take_masks(), dtype=np.bool_)
            at_kyoku = np.asarray(list(game.take_at_kyoku()), dtype=np.int64)
            rewards = self.rewards(game.take_grp(), player)
            assert len(rewards) >= at_kyoku[-1]+1
            for i in range(len(obs)):
                # Copy: a view would pin the whole game's obs array inside the shuffle buffer.
                yield obs[i].copy(), actions[i], masks[i].copy(), np.float32(rewards[at_kyoku[i]])

    def samples(self):
        info = torch.utils.data.get_worker_info()
        files = self.files if info is None else self.files[info.id::info.num_workers]
        rng = self.rng if info is None else random.Random(self.rng.random() + info.id)
        while True:
            rng.shuffle(files)
            buffer = []
            for path, names in files:
                buffer.extend(self.samples_of(path, names))
                if len(buffer) >= self.buffer_size:
                    rng.shuffle(buffer)
                    keep = self.buffer_size // 2
                    yield from buffer[keep:]
                    del buffer[keep:]
            rng.shuffle(buffer)
            yield from buffer

    def __iter__(self):
        yield from batches(self.samples(), self.batch_size)


def batches(stream, batch_size):
    obs, actions, masks, rewards = [], [], [], []
    for o, a, m, r in stream:
        obs.append(o); actions.append(a); masks.append(m); rewards.append(r)
        if len(obs) == batch_size:
            yield (torch.from_numpy(np.stack(obs)), torch.from_numpy(np.stack(actions)),
                   torch.from_numpy(np.stack(masks)), torch.from_numpy(np.stack(rewards)))
            obs, actions, masks, rewards = [], [], [], []


def train(args):
    _, loader_type, _, _ = load_doman_dataset(args.doman_engine)
    runtime(args.mortal_dir)
    from model import Brain, DQN
    from mortal_runner import load_checkpoint
    state, identity = load_checkpoint(torch, args.trainee)
    cfg = state["config"]
    brain = Brain(version=4, **{k: cfg["resnet"][k] for k in ("num_blocks", "conv_channels")}).cuda()
    dqn = DQN(version=4).cuda()
    brain.load_state_dict(state["mortal"]); dqn.load_state_dict(state["current_dqn"])
    brain.train(); dqn.train(); brain.freeze_bn(True)
    for module in brain.modules():
        if isinstance(module, nn.BatchNorm1d):
            module.requires_grad_(False)
    anchor = {name: p.detach().clone() for name, p in [*brain.named_parameters(), *dqn.named_parameters()] if p.requires_grad}
    params = [p for p in [*brain.parameters(), *dqn.parameters()] if p.requires_grad]
    optimizer = torch.optim.AdamW(params, lr=args.lr, weight_decay=0)
    warmup = max(1, args.steps // 20)
    scheduler = torch.optim.lr_scheduler.LambdaLR(optimizer, lambda s: min(1, (s+1)/warmup) * 0.5*(1+np.cos(np.pi*min(s, args.steps)/args.steps)))
    saved = torch.load(args.rank_model, map_location="cpu", weights_only=True)
    rank_model = ScoreRankModel().eval(); rank_model.load_state_dict(saved["model"])
    pts = torch.tensor(args.pts, dtype=torch.float64)
    stream = Stream(args.data, loader_type, rank_model, pts, args.buffer // max(1, args.workers), args.seed, args.batch_size)
    print(f"files={len(stream.files)} trainable={sum(p.numel() for p in params):,}", flush=True)
    loader = torch.utils.data.DataLoader(stream, batch_size=None, num_workers=args.workers, pin_memory=True,
                                         prefetch_factor=4 if args.workers else None, persistent_workers=bool(args.workers))
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=True)
    write_json(output/"train-config.json", json.loads(json.dumps(vars(args), default=str)))
    torch.manual_seed(args.seed)
    started = time.monotonic(); log = (output/"train-log.jsonl").open("w")
    totals = dict(dqn=0., cql=0., anchor=0., q=0., target=0.)
    for step, (obs, actions, masks, rewards) in enumerate(loader, 1):
        obs, actions = obs.cuda(non_blocking=True).float(), actions.cuda()
        masks, rewards = masks.cuda(), rewards.cuda()
        q_out = dqn(brain(obs), masks)
        q = q_out[torch.arange(len(actions), device=actions.device), actions]
        dqn_loss = 0.5 * F.mse_loss(q, rewards)
        cql_loss = q_out.logsumexp(-1).mean() - q.mean()
        anchor_loss = torch.zeros((), device=q.device) if not args.anchor else sum(((p - anchor[n])**2).sum() for n, p in [*brain.named_parameters(), *dqn.named_parameters()] if p.requires_grad)
        loss = dqn_loss + args.cql * cql_loss + args.anchor * anchor_loss
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        nn.utils.clip_grad_norm_(params, 1.0)
        optimizer.step(); scheduler.step()
        for key, value in (("dqn", dqn_loss), ("cql", cql_loss), ("anchor", anchor_loss), ("q", q.mean()), ("target", rewards.mean())):
            totals[key] += value.item()
        if step % args.log_every == 0:
            row = dict(step=step, seconds=round(time.monotonic()-started), lr=scheduler.get_last_lr()[0],
                       **{k: v/args.log_every for k, v in totals.items()})
            log.write(json.dumps(row)+"\n"); log.flush(); print(json.dumps(row), flush=True)
            totals = dict.fromkeys(totals, 0.)
        if step % args.save_every == 0 or step == args.steps:
            torch.save(dict(mortal={k: v.detach().cpu() for k, v in brain.state_dict().items()},
                            current_dqn={k: v.detach().cpu() for k, v in dqn.state_dict().items()},
                            config=state["config"], finetune=dict(script="finetune_full.py", step=step,
                            base_sha256=identity["checkpoint_sha256"], args=json.loads((output/"train-config.json").read_text()))),
                       output/f"candidate-{step}.pth")
        if step == args.steps:
            break


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("gen", "train"))
    parser.add_argument("--mortal-dir", type=Path, required=True)
    parser.add_argument("--doman-engine", type=Path, required=True)
    parser.add_argument("--trainee", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    # gen
    parser.add_argument("--pool", nargs="+", default=[], help="path,epsilon,temp,tag per opponent engine")
    parser.add_argument("--groups", type=int, default=256)
    parser.add_argument("--offset", type=int, default=40000)
    parser.add_argument("--batch-groups", type=int, default=32)
    parser.add_argument("--trainee-epsilon", type=float, default=0.05)
    parser.add_argument("--trainee-temp", type=float, default=0.05)
    # train
    parser.add_argument("--data", type=Path, nargs="+")
    parser.add_argument("--rank-model", type=Path)
    parser.add_argument("--pts", type=float, nargs=4, default=[3., 1., -1., -3.])
    parser.add_argument("--steps", type=int, default=20000)
    parser.add_argument("--batch-size", type=int, default=512)
    parser.add_argument("--lr", type=float, default=3e-6)
    parser.add_argument("--cql", type=float, default=1.0)
    parser.add_argument("--anchor", type=float, default=0.0)
    parser.add_argument("--buffer", type=int, default=60000, help="Total shuffle buffer across workers")
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--seed", type=int, default=20261011)
    parser.add_argument("--log-every", type=int, default=200)
    parser.add_argument("--save-every", type=int, default=5000)
    args = parser.parse_args()
    generate(args) if args.mode == "gen" else train(args)


if __name__ == "__main__":
    main()

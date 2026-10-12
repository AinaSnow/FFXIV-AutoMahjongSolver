"""Bounded opponent-behaviour sensitivity audit of the existing 512 worlds.

Scores are a diagnostic change of measure of the old structural proposal,
not calibrated posterior probabilities and never training labels.
"""
import argparse
import copy
import gzip
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import sys
import time

import numpy as np

from riichi_feedback import digest, sample_world, validate_world, visible


MODEL_SHA = '0a88ddad649804d085491b5397d895f596b0e55f30632c549ea145bb44786563'
LIB_SHA = '8396c4313643977f810c7ef4eae42f80204ab70eee6b441a6adb52ce88c563dd'


def read(path):
    return json.loads(path.read_text(encoding='utf-8'))


def save(path, obj):
    path.write_text(json.dumps(obj, indent=2, allow_nan=False)+'\n', encoding='utf-8')


def event_identity(event):
    return {k: v for k, v in event.items() if k != 'meta'}


def sampled_prefix(root, seed):
    world = sample_world(root, seed)
    validate_world(root, world)
    prefix = copy.deepcopy(root['prefix'])
    prefix[0]['tehais'] = world['haipai']
    wall = world['yama'].copy()
    for event in prefix[1:]:
        if event['type'] == 'tsumo':
            event['pai'] = wall.pop()
    assert visible(prefix, root['focal']) == root['prefix']
    return prefix


def control_prefix(repo, root):
    """True private cards are restricted to positive controls, never weights."""
    prefix = []
    with gzip.open(repo/root['source_log'], 'rt') as stream:
        for line in stream:
            event = event_identity(json.loads(line))
            if event['type'] == 'start_kyoku':
                prefix = [event]
            elif prefix:
                prefix.append(event)
            if len(prefix) == len(root['prefix']) and visible(prefix, root['focal']) == root['prefix']:
                return prefix
    raise AssertionError('original control prefix missing')


def tile_action(tile):
    if tile.endswith('r'):
        return 34+'mps'.index(tile[1])
    if len(tile) == 2:
        return 'mps'.index(tile[1])*9+int(tile[0])-1
    return 27+['E', 'S', 'W', 'N', 'P', 'F', 'C'].index(tile)


def score_prefix(module, engine, root, prefix):
    """Use the existing native Bot, including its agari guard and quick eval."""
    focal = root['focal']
    opponents = [p for p in range(4) if p != focal]
    bots = {p: module.mjai.Bot(engine, p) for p in opponents}
    states = {p: module.state.PlayerState(p) for p in opponents}
    records = []
    for p in opponents:
        bots[p].react(json.dumps(dict(type='start_game', names=['control']*4)), can_act=False)
    for index, event in enumerate(prefix):
        for player in opponents:
            private_event = visible([event], player)[0]
            encoded = json.dumps(private_event)
            state = states[player]
            cans = state.update(encoded)
            observed = None
            if event['type'] == 'tsumo' and event['actor'] == player:
                observed = prefix[index+1]
                assert observed['type'] == 'dahai' and observed['actor'] == player
            elif event['type'] == 'dahai' and event['actor'] != player:
                observed = dict(type='none')
            reaction = bots[player].react(encoded, can_act=observed is not None)
            if observed is None:
                continue
            if not cans.can_act:
                assert reaction is None and observed['type'] == 'none'
                continue  # Forced pass has probability one in every model.
            state.validate_reaction(json.dumps(observed))
            predicted = json.loads(reaction)
            meta = predicted.get('meta', {})
            mask_bits = meta.get('mask_bits')
            observed_action = tile_action(observed['pai']) if observed['type'] == 'dahai' else 45
            if mask_bits is None:
                assert event_identity(predicted) == observed
                count = 1  # Native quick evaluation: only one legal action.
            else:
                assert mask_bits & (1 << observed_action)
                count = mask_bits.bit_count()
            # Mortal's action space always emits tsumogiri if the chosen tile
            # equals the draw; this public flag can rule out a sampled world.
            flag_compatible = observed['type'] != 'dahai' or observed['tsumogiri'] == (state.last_self_tsumo() == observed['pai'])
            records.append(dict(event_index=index, actor=player,
                kind='discard' if observed['type']=='dahai' else 'pass',
                observed=observed, predicted=event_identity(predicted),
                legal_actions=count, flag_compatible=flag_compatible,
                greedy_match=event_identity(predicted)==observed))
    return dict(decisions=records, shanten=[states[p].shanten for p in opponents])


def log_weight(records, epsilon, evidence):
    selected = [r for r in records if evidence == 'all' or r['kind']=='discard']
    if not all(r['flag_compatible'] for r in selected):
        return None  # Zero mass, represented without non-standard JSON -Infinity.
    return sum(math.log((1-epsilon)*r['greedy_match']+epsilon/r['legal_actions']) for r in selected)


def weights(logs):
    values = np.asarray([-np.inf if v is None else v for v in logs], dtype=float)
    if not np.isfinite(values).any():
        return None
    result = np.exp(values-np.max(values))
    return result/result.sum()


def effective_size(w):
    return float(1/np.square(w).sum())


def model_setup(repo):
    import torch
    build = repo/'artifacts/doman-feedback-probe-20261005'
    assert digest(build/'libriichi_doman.so') == LIB_SHA
    spec = importlib.util.spec_from_file_location('libriichi_doman', build/'libriichi_doman.so')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    mortal = repo/'.loacl/Mortal/mortal'
    os.chdir(mortal)
    sys.path[:0] = [str(mortal), str(repo/'tools')]
    from mortal_runner import create_engine
    torch.set_num_threads(2)
    torch.backends.cudnn.benchmark = False
    torch.backends.cudnn.deterministic = True
    engine = create_engine(checkpoint_path=mortal/'patch/model_v4_20240308_best_min.pth',
        name='frozen-belief-audit', device_name='cuda:0', enable_amp=False)
    assert engine.model_identity['checkpoint_sha256'] == MODEL_SHA
    assert not engine.is_oracle and engine.boltzmann_epsilon == 0
    return module, engine


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--mode', choices=['prepare','preflight','score','summarize'], required=True)
    args = parser.parse_args()
    repo = args.repo.resolve()
    previous = repo/'artifacts/decision-feedback/riichi-probe-20261005'
    output = repo/'artifacts/decision-feedback/belief-audit-20261006'
    roots = read(previous/'roots.json')
    old_config = read(previous/'experiment-config.json')
    if args.mode == 'prepare':
        output.mkdir(parents=True, exist_ok=False)
        config = dict(experiment='opponent-history-sensitivity-20261006',
            source_sha256=digest(__file__), sampler_sha256=digest(Path(__file__).with_name('riichi_feedback.py')),
            roots_sha256=digest(previous/'roots.json'), previous_config_sha256=digest(previous/'experiment-config.json'),
            previous_pairs_sha256=digest(previous/'measure/pairs.jsonl'),
            epsilons=[0.05,0.20], evidence=['discard','all'], pairs_per_block=64,
            ess_minimum=32, maximum_weight=0.10, bootstrap_replicates=10000,
            interval_alpha=0.05/32, scoring_seconds_cap=1200,
            additional_continuations=0, training_labels_admitted=False,
            interpretation='Sensitivity relative to a structural proposal, not a calibrated posterior')
        save(output/'config.json', config)
        print(json.dumps(config), flush=True)
        return
    cfg = read(output/'config.json')
    assert digest(__file__) == cfg['source_sha256']
    assert digest(Path(__file__).with_name('riichi_feedback.py')) == cfg['sampler_sha256']
    assert digest(previous/'roots.json') == cfg['roots_sha256']
    assert digest(previous/'experiment-config.json') == cfg['previous_config_sha256']
    if args.mode == 'summarize':
        summarize(repo, previous, output, roots, cfg)
        return
    module, engine = model_setup(repo)
    started = time.monotonic()
    if args.mode == 'preflight':
        assert not (output/'preflight.json').exists()
        controls = []
        for root in roots:
            result = score_prefix(module, engine, root, control_prefix(repo, root))
            assert all(r['greedy_match'] and r['flag_compatible'] for r in result['decisions']), root['id']
            controls.append(dict(id=root['id'], **result))
        root = roots[0]
        sample = sampled_prefix(root, 32_000_000)
        a = score_prefix(module, engine, root, sample)
        b = score_prefix(module, engine, root, sample)
        assert a == b
        # Each actor sees only their own sampled private cards. Corrupting all
        # other hidden fields must leave that actor's encoded input unchanged.
        for player in range(4):
            mutated = copy.deepcopy(sample)
            for event in mutated:
                if event['type']=='start_kyoku':
                    for p in range(4):
                        if p != player: event['tehais'][p] = ['C']*13
                elif event['type']=='tsumo' and event['actor']!=player:
                    event['pai'] = 'C'
            assert visible(mutated, player) == visible(sample, player)
        assert np.allclose(weights([0]*64), np.full(64, 1/64))
        assert effective_size(weights([0]*64)) == 64
        assert effective_size(weights([0]+[-100]*63)) < 1.01
        assert weights([None]*64) is None
        save(output/'preflight.json', dict(status='passed', true_world_controls=controls,
            repeat_identical=True, actor_visibility_independence=True,
            normalization_and_concentration_checks=True, elapsed_seconds=time.monotonic()-started))
        print(json.dumps(dict(status='passed', controls=[dict(id=r['id'],decisions=len(r['decisions'])) for r in controls],
                              elapsed_seconds=time.monotonic()-started)), flush=True)
        return
    assert read(output/'preflight.json')['status']=='passed'
    assert not (output/'world-scores.jsonl').exists()
    manifest = dict(status='running', worlds_completed=0, source_sha256=digest(__file__),
        config_sha256=digest(output/'config.json'), model=engine.model_identity,
        library_sha256=LIB_SHA, additional_continuations=0, training_started=False)
    save(output/'manifest.json', manifest)
    try:
        for block, base in enumerate(old_config['world_seed_bases']):
            for root_index, root in enumerate(roots):
                for offset in range(64):
                    assert time.monotonic()-started < cfg['scoring_seconds_cap']
                    seed = base+root_index*1000+offset
                    result = score_prefix(module, engine, root, sampled_prefix(root, seed))
                    row = dict(id=root['id'], block=block, seed=seed, **result)
                    row['log_weights'] = {f'{evidence}-eps{epsilon}':log_weight(result['decisions'],epsilon,evidence)
                        for evidence in cfg['evidence'] for epsilon in cfg['epsilons']}
                    with (output/'world-scores.jsonl').open('a') as stream:
                        stream.write(json.dumps(row, allow_nan=False)+'\n')
                    manifest['worlds_completed'] += 1
                    if manifest['worlds_completed'] % 16 == 0:
                        manifest['elapsed_seconds'] = time.monotonic()-started
                        save(output/'manifest.json', manifest)
                        print(f"scored {manifest['worlds_completed']}/512 histories, {manifest['elapsed_seconds']:.1f}s",flush=True)
        manifest.update(status='complete',elapsed_seconds=time.monotonic()-started,
                        scores_sha256=digest(output/'world-scores.jsonl'))
        save(output/'manifest.json', manifest)
    except BaseException as error:
        manifest.update(status='failed',error=f'{type(error).__name__}: {error}',elapsed_seconds=time.monotonic()-started)
        save(output/'manifest.json', manifest)
        raise


def summarize(repo, previous, output, roots, cfg):
    manifest = read(output/'manifest.json')
    assert manifest['status']=='complete' and manifest['worlds_completed']==512
    assert digest(output/'world-scores.jsonl')==manifest['scores_sha256']
    assert digest(previous/'measure/pairs.jsonl')==cfg['previous_pairs_sha256']
    rows = [json.loads(line) for line in (output/'world-scores.jsonl').read_text().splitlines()]
    pairs = {r['seed']:r for r in map(json.loads,(previous/'measure/pairs.jsonl').read_text().splitlines())}
    assert len(rows)==len(pairs)==len({r['seed'] for r in rows})==512
    utilities = np.array([1,1,0,-0.25])
    rng = np.random.default_rng(916106)
    results = []
    for root in roots:
        for block in range(2):
            group = [r for r in rows if r['id']==root['id'] and r['block']==block]
            assert len(group)==64
            differences = []
            for row in group:
                pair = pairs[row['seed']]
                assert pair['id']==root['id'] and pair['block']==block
                # Validate that scored histories are exactly the previous worlds.
                with gzip.open(previous/'measure'/pair['no_declaration']['log'], 'rt') as stream:
                    assert json.loads(next(stream))['type']=='start_game'
                    actual = [event_identity(json.loads(next(stream))) for _ in root['prefix']]
                assert actual == sampled_prefix(root, row['seed'])
                a,b = pair['declare'], pair['no_declaration']
                ar,br = a['ranks'][root['focal']], b['ranks'][root['focal']]
                differences.append([utilities[ar]-utilities[br], ar-br, int(ar<2)-int(br<2), int(ar==3)-int(br==3)])
            values = np.asarray(differences)
            all_decisions = [d for row in group for d in row['decisions']]
            entry = dict(id=root['id'],block=block,worlds=64,unweighted_delta=values.mean(axis=0).tolist(),
                full_greedy_consistent=sum(all(d['greedy_match'] for d in row['decisions']) for row in group),
                discard_greedy_consistent=sum(all(d['greedy_match'] for d in row['decisions'] if d['kind']=='discard') for row in group),
                flag_incompatible_worlds=sum(any(not d['flag_compatible'] for d in row['decisions']) for row in group),
                match_rates={kind:dict(matches=sum(d['greedy_match'] for d in all_decisions if d['kind']==kind),
                    decisions=sum(d['kind']==kind for d in all_decisions)) for kind in ['discard','pass']},models={})
            for name in group[0]['log_weights']:
                w = weights([row['log_weights'][name] for row in group])
                if w is None:
                    entry['models'][name] = dict(support_pass=False,ess=0,reason='zero mass')
                    continue
                ess = effective_size(w)
                support = ess >= cfg['ess_minimum'] and w.max() <= cfg['maximum_weight']
                detail = dict(ess=ess,maximum_weight=float(w.max()),nonzero_worlds=int((w>0).sum()),
                    support_pass=bool(support),weighted_delta=(w@values).tolist(),
                    weighted_mean_opponent_shanten=float(w@np.asarray([np.mean(r['shanten']) for r in group])),
                    unweighted_mean_opponent_shanten=float(np.mean([r['shanten'] for r in group])))
                if support:
                    indices = rng.integers(0,64,size=(cfg['bootstrap_replicates'],64))
                    sampled_weights = w[indices]
                    numerator = (sampled_weights*values[indices,0]).sum(axis=1)
                    denominator = sampled_weights.sum(axis=1)
                    assert (denominator>0).all()
                    detail['utility_interval'] = np.quantile(numerator/denominator,
                        [cfg['interval_alpha']/2,1-cfg['interval_alpha']/2]).tolist()
                else:
                    detail['utility_interval'] = None
                    detail['reason'] = 'Insufficient effective support; weighted mean is descriptive only'
                entry['models'][name] = detail
            results.append(entry)
    report = dict(scope=cfg['interpretation'], roots=results, worlds=512,
        full_greedy_consistent=sum(r['full_greedy_consistent'] for r in results),
        support_passes=sum(m['support_pass'] for r in results for m in r['models'].values()),
        support_comparisons=32, scorer_input_excludes_outcomes=True,
        scored_prefixes_equal_original_continuation_prefixes=True,
        training_labels_admitted=False, additional_continuations=0,
        original_probe_library_unchanged=digest(repo/'artifacts/doman-feedback-probe-20261005/libriichi_doman.so')==LIB_SHA,
        original_model_unchanged=digest(repo/'.loacl/Mortal/mortal/patch/model_v4_20240308_best_min.pth')==MODEL_SHA)
    assert report['original_probe_library_unchanged'] and report['original_model_unchanged']
    save(output/'report.json', report)
    print(json.dumps(report, indent=2), flush=True)


if __name__ == '__main__':
    main()

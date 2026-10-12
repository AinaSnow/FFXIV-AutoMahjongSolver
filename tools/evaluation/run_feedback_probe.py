"""Execute only the preregistered four-state riichi feedback feasibility probe."""
import argparse
import copy
import gzip
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import time

import numpy as np
import torch

from riichi_feedback import sample_world, validate_world, digest
from self_play import canonical_identity


def save(path, data):
    path.write_text(json.dumps(data,indent=2)+'\n',encoding='utf-8')


def execute(module, engine, root, seed, choices, directory, key):
    world=sample_world(root,seed); validate_world(root,world)
    cases=[dict(world,declare=choice) for choice in choices]
    raw=module.arena.probe_branches(engine,[json.dumps(case) for case in cases])
    rows=[]
    for idx,text in enumerate(raw):
        row=json.loads(text)
        events=list(map(json.loads,row.pop('log').splitlines()))
        with gzip.open(directory/f'{key}-{idx}.json.gz','wt') as stream:
            stream.write('\n'.join(map(json.dumps,events))+'\n')
        row.update(seed=seed,gameplay_sha256=canonical_identity(events))
        assert sum(row['scores'])==sum(world['scores'])+1000*world['kyotaku']
        assert sorted(row['ranks'])==[0,1,2,3]
        rows.append(row)
    assert len({r['observation_sha3_256'] for r in rows})==1
    return rows


def interval(rows, alpha):
    matrix=np.asarray(rows,dtype=np.float64)
    rng=np.random.default_rng(90731)
    sampled=rng.integers(0,len(matrix),size=(10000,len(matrix)))
    means=matrix[sampled].mean(axis=1)
    limits=np.quantile(means,[alpha/2,1-alpha/2],axis=0)
    return matrix.mean(axis=0).tolist(), limits.T.tolist()


def summarize(rows, roots, cfg):
    utility=np.asarray(cfg['utility'])
    names=['utility','average_rank','top_two','fourth','score']
    result=[]
    for root in roots:
        blocks=[]
        for block in range(2):
            chosen=[r for r in rows if r['id']==root['id'] and r['block']==block]
            assert len(chosen)==cfg['pairs_per_root_per_block']
            values=[]
            for pair in chosen:
                a,b=pair['declare'],pair['no_declaration']; seat=root['focal']
                ar,br=a['ranks'][seat],b['ranks'][seat]
                values.append([utility[ar]-utility[br],ar-br,int(ar<2)-int(br<2),int(ar==3)-int(br==3),a['scores'][seat]-b['scores'][seat]])
            means,cis=interval(values,cfg['interval_alpha'])
            blocks.append(dict(block=block,pairs=len(chosen),delta=dict(zip(names,means)),
                               ci_99_375=dict(zip(names,cis))))
        directions=[1 if b['ci_99_375']['utility'][0]>0 else -1 if b['ci_99_375']['utility'][1]<0 else 0 for b in blocks]
        result.append(dict(id=root['id'],group=root['group'],focal=root['focal'],blocks=blocks,
                           conditional_stability_pass=directions[0]!=0 and directions[0]==directions[1]))
    return dict(scope='Conditional feedback under an uncalibrated structural hidden-tile prior; not policy or live-game strength evidence',
                roots=result,stable_roots=sum(r['conditional_stability_pass'] for r in result),
                pairs=len(rows),continuations=len(rows)*2,training_labels_admitted=False,
                candidate_training_started=False,live_weights_modified=False)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo',type=Path,required=True)
    parser.add_argument('--mode',choices=['preflight','measure'],required=True)
    args=parser.parse_args(); repo=args.repo.resolve()
    run=repo/'artifacts/decision-feedback/riichi-probe-20261005'
    build=repo/'artifacts/doman-feedback-probe-20261005'
    cfg=json.loads((run/'experiment-config.json').read_text())
    roots=json.loads((run/'roots.json').read_text())
    build_info=json.loads((build/'build-manifest.json').read_text())
    assert digest(run/'roots.json')==cfg['roots_sha256']
    assert digest(Path(__file__).with_name('riichi_feedback.py'))==cfg['sampler_sha256']
    assert digest(build/'libriichi_doman.so')==build_info['library_sha256']
    assert digest(Path(__file__).with_name('feedback_probe.rs'))==build_info['probe_sha256']
    spec=importlib.util.spec_from_file_location('libriichi_doman',build/'libriichi_doman.so')
    module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    mortal=repo/'.loacl/Mortal/mortal'; os.chdir(mortal)
    sys.path[:0]=[str(mortal),str(repo/'tools')]
    from mortal_runner import create_engine
    torch.set_num_threads(2); torch.backends.cudnn.benchmark=False; torch.backends.cudnn.deterministic=True
    engine=create_engine(checkpoint_path=mortal/'patch/model_v4_20240308_best_min.pth',name='frozen-baseline',device_name='cuda:0',enable_amp=False)
    assert engine.model_identity['checkpoint_sha256']=='0a88ddad649804d085491b5397d895f596b0e55f30632c549ea145bb44786563'
    assert not engine.is_oracle and engine.boltzmann_epsilon==0
    directory=run/args.mode; directory.mkdir(exist_ok=False)
    started=time.monotonic()
    manifest=dict(status='running',mode=args.mode,model=engine.model_identity,build=build_info,
                  runner_sha256=digest(__file__),config_sha256=digest(run/'experiment-config.json'),
                  sampler_sha256=cfg['sampler_sha256'],root_hashes={},live_weights_modified=False)
    save(directory/'manifest.json',manifest)
    try:
        if args.mode=='preflight':
            controls=[]
            for i in range(2):
                pair=execute(module,engine,roots[0],28_000_000+i,[False,False],directory,f'control-{i}')
                assert pair[0]==pair[1]
                controls.append(pair)
            for i,root in enumerate(roots):
                pair=execute(module,engine,root,28_000_100+i,[False,True],directory,f'branch-{i}')
                manifest['root_hashes'][root['id']]=pair[0]['observation_sha3_256']
            assert all(row['observation_sha3_256']==manifest['root_hashes'][roots[0]['id']] for pair in controls for row in pair)
            manifest.update(status='passed',same_action_controls=2,branch_pairs=4,continuations=12,
                            elapsed_seconds=time.monotonic()-started)
            save(directory/'manifest.json',manifest)
            print(json.dumps(manifest),flush=True)
            return
        preflight=json.loads((run/'preflight/manifest.json').read_text())
        assert preflight['status']=='passed'
        assert preflight['build']['library_sha256']==build_info['library_sha256']
        all_rows=[]
        for block,base in enumerate(cfg['world_seed_bases']):
            for root_index,root in enumerate(roots):
                for start in range(0,64,16):
                    assert time.monotonic()-started<cfg['simulation_seconds_cap']
                    cases=[]
                    for i in range(start,start+16):
                        world=sample_world(root,base+root_index*1000+i)
                        validate_world(root,world)
                        cases.extend([dict(world,declare=False),dict(world,declare=True)])
                    raw=module.arena.probe_branches(engine,[json.dumps(case) for case in cases])
                    outputs=[]
                    for i,text in enumerate(raw):
                        row=json.loads(text); events=list(map(json.loads,row.pop('log').splitlines()))
                        filename=f'b{block}-{root_index}-{start+i//2}-{i%2}.json.gz'
                        with gzip.open(directory/filename,'wt') as sink:
                            sink.write('\n'.join(map(json.dumps,events))+'\n')
                        row['log']=filename
                        row['gameplay_sha256']=canonical_identity(events)
                        assert row['observation_sha3_256']==preflight['root_hashes'][root['id']]
                        assert sum(row['scores'])==sum(root['prefix'][0]['scores'])+1000*root['prefix'][0]['kyotaku']
                        assert sorted(row['ranks'])==[0,1,2,3]
                        outputs.append(row)
                    for i in range(16):
                        a,b=outputs[i*2:i*2+2]
                        assert a['declare'] is False and b['declare'] is True
                        pair=dict(id=root['id'],block=block,seed=base+root_index*1000+start+i,no_declaration=a,declare=b)
                        all_rows.append(pair)
                        with (directory/'pairs.jsonl').open('a') as sink: sink.write(json.dumps(pair)+'\n')
                    manifest.update(pairs_completed=len(all_rows),elapsed_seconds=time.monotonic()-started)
                    save(directory/'manifest.json',manifest)
                    print(f'completed {len(all_rows)}/512 paired worlds ({len(all_rows)*2}/1024 continuations)',flush=True)
        result=summarize(all_rows,roots,cfg)
        save(run/'feedback-report.json',result)
        manifest.update(status='complete',elapsed_seconds=time.monotonic()-started,root_hashes=preflight['root_hashes'])
        save(directory/'manifest.json',manifest)
        print(json.dumps(result),flush=True)
    except BaseException as error:
        manifest.update(status='failed',error=f'{type(error).__name__}: {error}',elapsed_seconds=time.monotonic()-started)
        save(directory/'manifest.json',manifest); raise


if __name__=='__main__': main()

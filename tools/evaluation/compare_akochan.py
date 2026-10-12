"""Bounded paired comparison of pinned Akochan against the original Mortal."""
import argparse, atexit, collections, hashlib, importlib.util, json, os, pathlib, shutil, sys, tempfile, time
R=pathlib.Path('/mnt/e/SapphireServer/FFXIV-AutoMahjongSolver'); O=R/'artifacts/akochan-20261006'
sys.path[:0]=[str(R/'tools/evaluation'),str(R/'tools')]
from doman_engine import sha256, load_doman
from compare_models import read_batch_results
from arena import report, paired_interval
from self_play import write_json, inspect_log

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--output',type=pathlib.Path,required=True)
    p.add_argument('--seed',type=int,required=True)
    p.add_argument('--groups',type=int,required=True)
    p.add_argument('--reference-only',action='store_true')
    a=p.parse_args(); a.output.mkdir(parents=True,exist_ok=False)
    os.environ.update(AKOCHAN_DIR=str(O/'upstream'),AKOCHAN_TACTICS='setup_mjai.json',OMP_NUM_THREADS='2',RAYON_NUM_THREADS='2',
        LD_LIBRARY_PATH=str(O/'upstream')+':'+str(O/'deps/root/usr/lib/x86_64-linux-gnu')+':'+os.environ.get('LD_LIBRARY_PATH',''))
    manifest=json.loads((O/'build-manifest.json').read_text())
    for path,key in ((O/'native/libriichi_doman.so','library_sha256'),(O/'upstream/system.exe','ako_binary_sha256'),
                     (O/'upstream/libai.so','ako_library_sha256'),(O/'upstream/setup_mjai.json','tactics_sha256')):
        assert sha256(path)==manifest[key]
    for path,digest in manifest['parameters'].items(): assert sha256(O/'upstream'/path)==digest
    # Akochan repeatedly opens parameter files. Use identical local copies to
    # avoid measuring Windows-mounted filesystem overhead as decision cost.
    runtime=pathlib.Path(tempfile.mkdtemp(prefix='ffxiv-akochan-'))
    atexit.register(shutil.rmtree,runtime)
    shutil.copytree(O/'upstream/params',runtime/'params')
    for name in ('system.exe','libai.so','setup_mjai.json'): shutil.copy2(O/'upstream'/name,runtime/name)
    for path,digest in manifest['parameters'].items(): assert sha256(runtime/path)==digest
    os.environ['AKOCHAN_DIR']=str(runtime)
    os.environ['LD_LIBRARY_PATH']=str(runtime)+':'+os.environ['LD_LIBRARY_PATH']
    if a.reference_only:
        Arena,Grp,_=load_doman(R/'artifacts/doman-engine-v2-dataset')
    else:
        spec=importlib.util.spec_from_file_location('libriichi_doman',O/'native/libriichi_doman.so')
        native=importlib.util.module_from_spec(spec); spec.loader.exec_module(native)
        Arena=native.arena.OneVsThree; Grp=native.dataset.Grp
    mortal=R/'.loacl/Mortal/mortal'; os.chdir(mortal); sys.path.insert(0,str(mortal))
    import torch
    from mortal_runner import create_engine
    torch.set_num_threads(2); torch.backends.cudnn.benchmark=False; torch.backends.cudnn.deterministic=True
    checkpoint=mortal/'patch/model_v4_20240308_best_min.pth'
    assert sha256(checkpoint)=='0a88ddad649804d085491b5397d895f596b0e55f30632c549ea145bb44786563'
    opponent=create_engine(checkpoint_path=checkpoint,name='opponent',device_name='cuda',enable_amp=False)
    baseline=create_engine(checkpoint_path=checkpoint,name='baseline',device_name='cuda',enable_amp=False)
    started=time.monotonic(); groups=[]; times=[]; actions=collections.Counter()
    status=dict(status='running',seed_start=a.seed,groups_requested=a.groups,completed_groups=0,seed_key=42,
                reference_only=a.reference_only,akochan=manifest,mortal=baseline.model_identity,threads=2,
                evaluator_sha256=sha256(__file__),live_modified=False,match_mode='doman-quick')
    write_json(a.output/'manifest.json',status)
    try:
        with (a.output/'results.jsonl').open('w') as sink:
            for index,seed in enumerate(range(a.seed,a.seed+a.groups)):
                group=dict(seed=seed,key=42)
                roles=['baseline'] if a.reference_only else (['candidate','baseline'] if index%2==0 else ['baseline','candidate'])
                for role in roles:
                    directory=a.output/f'seed-{seed}'/role
                    arena=Arena(disable_progress_bar=True,log_dir=str(directory),match_mode='doman-quick')
                    before=time.monotonic()
                    ranks=arena.ako_vs_py(opponent,(seed,42),1) if role=='candidate' else arena.py_vs_py(baseline,opponent,(seed,42),1)
                    group[role]=read_batch_results(directory,seed,1,'akochan' if role=='candidate' else role,Grp,ranks,'doman-quick')[seed]
                    for match in group[role]:
                        path=directory/match['log']; match['log']=str(path.relative_to(a.output))
                        if role=='candidate':
                            _,events=inspect_log(path,seed)
                            for e in events:
                                if e.get('actor')==match['seat']:
                                    actions[e['type']]+=1
                                    ns=e.get('meta',{}).get('eval_time_ns')
                                    if ns is not None: times.append(ns/1e9)
                    print(json.dumps(dict(seed=seed,role=role,ranks=list(ranks),seconds=time.monotonic()-before)),flush=True)
                groups.append(group); sink.write(json.dumps(group)+'\n'); sink.flush()
                status.update(completed_groups=len(groups),elapsed_seconds=time.monotonic()-started)
                write_json(a.output/'manifest.json',status)
        if not a.reference_only:
            result=report(groups,'development','mortal','doman-quick')
            delta=[sum(m['rank']<=2 for m in g['candidate'])/4-sum(m['rank']<=2 for m in g['baseline'])/4 for g in groups]
            times.sort()
            result.update(top_two_delta=sum(delta)/len(delta),top_two_delta_95_ci=paired_interval(delta),
                candidate_description='Akochan official defaults, tonpuu',candidate_action_counts=dict(actions),
                candidate_timing=dict(count=len(times),total_seconds=sum(times),max_seconds=max(times,default=0),
                  median_seconds=times[len(times)//2] if times else None,p95_seconds=times[min(len(times)-1,int(len(times)*.95))] if times else None),
                elapsed_seconds=time.monotonic()-started,live_modified=False,
                limitation='Akochan terminal value model retains Tenhou extension/30000 assumptions; arena uses Doman rules.')
            for role in ('candidate','baseline'):
                c=collections.Counter(m['rank'] for g in groups for m in g[role]); result[role]['placements']=[c[i] for i in range(1,5)]
            write_json(a.output/'report.json',result)
        status.update(status='complete',elapsed_seconds=time.monotonic()-started)
        write_json(a.output/'manifest.json',status)
        print(json.dumps({k:v for k,v in status.items() if k not in ('akochan','mortal')}),flush=True)
    except BaseException as e:
        status.update(status='failed',error=repr(e),elapsed_seconds=time.monotonic()-started)
        write_json(a.output/'manifest.json',status); raise
if __name__=='__main__': main()

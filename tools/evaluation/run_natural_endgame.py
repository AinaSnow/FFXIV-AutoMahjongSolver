"""Preregistered finite natural-root action intervention, never training labels."""
import argparse,collections,gzip,hashlib,importlib.util,json,os,pathlib,select,shutil,subprocess,sys,tempfile,time
R=pathlib.Path('/mnt/e/SapphireServer/FFXIV-AutoMahjongSolver')
O=R/'artifacts/decision-feedback/natural-endgame-20261006'; A=R/'artifacts/akochan-20261006'; D=R/'artifacts/decision-feedback/endgame-audit-20261006'
sys.path[:0]=[str(R/'tools/evaluation'),str(R/'tools')]
from doman_engine import sha256,load_doman_module
from self_play import canonical_identity
def read(p):return json.loads(p.read_text())
def save(p,x):p.write_text(json.dumps(x,ensure_ascii=False,indent=2)+'\n')
def append(p,x):
 with p.open('a') as f:f.write(json.dumps(x)+'\n')

def summarize(rows,cfg):
 import numpy as np
 names=['average_rank','top_two','first','fourth','score']
 def val(r,i):
  f=r['root']['focal']; v=r['results'][i]; rank=v['ranks'][f]
  return [rank+1,int(rank<2),int(rank==0),int(rank==3),v['scores'][f]]
 out=dict(roots=len(rows),blocks=[sum(r['block']==b for r in rows) for b in range(2)],comparisons={},placements={})
 for label,i in [('mortal',0),('original',1),('terminal',2)]:
  values=np.asarray([val(r,i) for r in rows]);out['placements'][label]=dict(zip(names,values.mean(axis=0).tolist())) if rows else {}
 for label,j in [('terminal_minus_original',1),('terminal_minus_mortal',0)]:
  if not rows:continue
  x=np.asarray([np.asarray(val(r,2))-np.asarray(val(r,j)) for r in rows],dtype=float)
  rng=np.random.default_rng(cfg['bootstrap_seed']);means=x[rng.integers(len(x),size=(cfg['bootstrap_samples'],len(x)))].mean(axis=1)
  lo,hi=np.quantile(means,[.025,.975],axis=0)
  block=[x[[r['block']==b for r in rows]].mean(axis=0).tolist() if any(r['block']==b for r in rows) else None for b in range(2)]
  strata={str(k):dict(n=sum(r['root']['rank']==k for r in rows),delta=x[[r['root']['rank']==k for r in rows]].mean(axis=0).tolist()) for k in range(1,5) if any(r['root']['rank']==k for r in rows)}
  out['comparisons'][label]=dict(delta=dict(zip(names,x.mean(axis=0).tolist())),ci95={n:[float(l),float(h)] for n,l,h in zip(names,lo,hi)},block_delta=block,strata=strata)
 if rows:
  p=out['comparisons']['terminal_minus_original'];g=cfg['gates']
  out['gates']=dict(enough_roots=min(out['blocks'])>=g['minimum_roots_per_block'],
   rank_effect=p['delta']['average_rank']<=g['mean_rank_delta_max'],rank_interval=p['ci95']['average_rank'][1]<0,
   block_direction=all(b is not None and b[0]<0 for b in p['block_delta']),
   top_two_guard=p['ci95']['top_two'][0]>=g['top_two_ci_low_min'],fourth_guard=p['ci95']['fourth'][1]<=g['fourth_ci_high_max'])
  out['passed']=all(out['gates'].values())
 out.update(no_training_labels=True,no_live_changes=True)
 return out

def main():
 p=argparse.ArgumentParser();p.add_argument('mode',choices=['reference','preflight','measure']);a=p.parse_args();cfg=read(O/'config.json')
 os.environ.update(OMP_NUM_THREADS='2',RAYON_NUM_THREADS='2')
 if a.mode=='reference':mod,_=load_doman_module(R/'artifacts/doman-engine-v2-dataset')
 else:
  info=read(O/'build.json');assert sha256(O/'libriichi_doman.so')==info['library_sha256']
  spec=importlib.util.spec_from_file_location('libriichi_doman',O/'libriichi_doman.so');mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod)
 mortal=R/'.loacl/Mortal/mortal';os.chdir(mortal);sys.path.insert(0,str(mortal))
 import torch
 from mortal_runner import create_engine
 torch.set_num_threads(2);torch.backends.cudnn.benchmark=False;torch.backends.cudnn.deterministic=True
 engine=create_engine(checkpoint_path=mortal/'patch/model_v4_20240308_best_min.pth',device_name='cuda',enable_amp=False)
 assert engine.model_identity['checkpoint_sha256']=='0a88ddad649804d085491b5397d895f596b0e55f30632c549ea145bb44786563'
 assert not engine.is_oracle and engine.boltzmann_epsilon==0
 if a.mode=='reference':
  out=O/'reference';assert not out.exists()
  arena=mod.arena.OneVsThree(disable_progress_bar=True,log_dir=str(out),match_mode='doman-quick')
  arena.py_vs_py(engine,engine,(1019000,42),1)
  logs=list(out.glob('*.json.gz'));assert len(logs)==4
  identities=[canonical_identity([json.loads(s) for s in gzip.open(f,'rt')]) for f in logs]
  assert len(set(identities))==1
  save(O/'reference.json',dict(seed=1019000,gameplay_sha256=identities[0]));print('Original-library reference complete',flush=True);return
 if a.mode=='measure':
  pre=read(O/'preflight/manifest.json');assert pre['status']=='passed' and pre['runner_sha256']==sha256(__file__)
 out=O/a.mode;out.mkdir(exist_ok=False);(out/'logs').mkdir()
 started=time.monotonic();manifest=dict(status='running',mode=a.mode,runner_sha256=sha256(__file__),config_sha256=sha256(O/'config.json'),
  build=read(O/'build.json'),games=0,roots=0,elapsed_seconds=0,no_training=True,no_live_changes=True)
 save(out/'manifest.json',manifest);rows=[];procs={};handles=[];cached=[]
 with tempfile.TemporaryDirectory(prefix='natural-endgame-') as temp:
  tmp=pathlib.Path(temp);shutil.copytree(A/'upstream/params',tmp/'params');shutil.copy2(A/'upstream/setup_mjai.json',tmp/'setup_mjai.json')
  env={**os.environ,'LD_LIBRARY_PATH':str(A/'upstream')}
  try:
   build=read(D/'build.json')
   for label in ('original','terminal'):
    assert sha256(D/label)==build[label+'_binary']
    err=(out/(label+'-stderr.log')).open('w');handles.append(err)
    procs[label]=subprocess.Popen([str(D/label)],cwd=tmp,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=err,text=True,bufsize=1)
   def query(label,q):
    proc=procs[label];proc.stdin.write(json.dumps(q)+'\n');proc.stdin.flush()
    if not select.select([proc.stdout],[],[],cfg['query_timeout_seconds'])[0]:raise TimeoutError(label+' query timeout')
    text=proc.stdout.readline()
    if not text:raise RuntimeError(label+' exited')
    return json.loads(text)
   for case in read(D/'value-controls.json'):
    for label in procs:assert query(label,case['input'])==case[label]
   def callback(encoded):
    root=json.loads(encoded);f=root['focal']
    for e in root['prefix']:
     assert 'seed' not in e and 'meta' not in e
     if e['type']=='start_kyoku':assert all(p==f or h==['?']*13 for p,h in enumerate(e['tehais']))
     if e['type']=='tsumo' and e['actor']!=f:assert e['pai']=='?'
    q=dict(kind='policy',focal=f,record=[dict(type='start_game',kyoku_first=4,aka_flag=True)]+root['prefix'])
    choices={label:query(label,q) for label in procs}
    assert all(choices.values());cached.append(choices)
    return json.dumps([choices[label][0]['moves'] for label in ('original','terminal')])
   blocks=[cfg['preflight_seeds']] if a.mode=='preflight' else cfg['seed_blocks']
   root_cap=cfg['max_preflight_roots'] if a.mode=='preflight' else cfg['max_roots_per_block']
   timed_out=False
   for block,(start,end) in enumerate(blocks):
    block_roots=0
    for seed in range(start,end+1):
     if time.monotonic()-started>=cfg['wall_seconds']:timed_out=True;break
     cached.clear()
     result=json.loads(mod.arena.natural_endgame(engine,callback,seed,cfg['key'],True,a.mode=='preflight'))
     result['block']=block;has_root=result['root'] is not None
     matches=result['results'] if has_root else [result['baseline']]
     for i,match in enumerate(matches):
      raw=match.pop('log');events=[json.loads(s) for s in raw.splitlines()]
      assert events[0]['type']=='start_game' and events[-1]['type']=='end_game'
      assert sorted(match['ranks'])==[0,1,2,3] and sum(match['scores'])==100000
      file=f'{seed}-{i}.json.gz'
      with gzip.open(out/'logs'/file,'wt') as sink:sink.write(raw)
      match.update(log='logs/'+file,gameplay_sha256=canonical_identity(events))
     if a.mode=='preflight' and seed==1019000:assert matches[0]['gameplay_sha256']==read(O/'reference.json')['gameplay_sha256']
     if has_root:
      assert len(cached)==1 and len(matches)==3
      result['choices']=cached[0];root=result['root'];f=root['focal'];root['rank']=sorted(range(4),key=lambda p:(-root['scores'][p],p)).index(f)+1
      if a.mode=='preflight':assert len({m['gameplay_sha256'] for m in matches})==1
      else:
       assert result['actions'][1:]==[cached[0][label][0]['moves'] for label in ('original','terminal')]
      rows.append(result);block_roots+=1;append(out/'pairs.jsonl',result)
     append(out/'games.jsonl',dict(seed=seed,block=block,selected=has_root,baseline=matches[0]))
     manifest.update(games=manifest['games']+1,roots=len(rows),elapsed_seconds=time.monotonic()-started)
     save(out/'manifest.json',manifest)
     if has_root or manifest['games']%16==0:print(json.dumps(dict(block=block,seed=seed,games=manifest['games'],roots=len(rows),elapsed=round(manifest['elapsed_seconds'],1))),flush=True)
     if block_roots>=root_cap:break
    if timed_out:break
   if a.mode=='preflight':assert len(rows)>=1;manifest['status']='passed';manifest['same_action_controls']=2*len(rows)
   else:
    save(O/'report.json',summarize(rows,cfg));manifest['status']='time_cap' if timed_out else 'complete'
   manifest['elapsed_seconds']=time.monotonic()-started;save(out/'manifest.json',manifest);print(json.dumps(manifest),flush=True)
  except BaseException as err:
   manifest.update(status='failed',error=repr(err),elapsed_seconds=time.monotonic()-started);save(out/'manifest.json',manifest);raise
  finally:
   for proc in procs.values():
    if proc.poll() is None:proc.terminate()
    try:proc.wait(timeout=5)
    except subprocess.TimeoutExpired:proc.kill();proc.wait()
    proc.stdin.close();proc.stdout.close()
   for h in handles:h.close()
if __name__=='__main__':main()

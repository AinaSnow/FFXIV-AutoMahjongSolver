"""Bounded natural endgame diagnosis; no training or rollout labels."""
import argparse, collections, copy, gzip, hashlib, importlib.util, json, os, pathlib, select, shutil, subprocess, sys, tempfile, time
R=pathlib.Path('/mnt/e/SapphireServer/FFXIV-AutoMahjongSolver'); A=R/'artifacts/akochan-20261006'; O=R/'artifacts/decision-feedback/endgame-audit-20261006'
sys.path[:0]=[str(R/'tools/evaluation'),str(R/'tools')]
from riichi_feedback import visible, digest
from doman_engine import load_doman_module
def save(p,x): p.write_text(json.dumps(x,ensure_ascii=False,indent=2)+'\n')
def read(p): return json.loads(p.read_text())
def event(e): return {k:v for k,v in e.items() if k!='meta'}
def rows(p): return [json.loads(s) for s in p.read_text().splitlines()]

def prepare():
    O.mkdir(parents=True,exist_ok=True)
    assert not (O/'roots.json').exists(), 'Preserve existing selected roots'
    config=dict(purpose='diagnostic, no proven action loss or training label',sources=['doman-tail-H-dev200-20261006 baseline','akochan development-parallel baseline and candidate'],
        selection='first eligible root per unique game, source/rank quota 3; East4 own draw, all current scores<30000, shanten<=1, no own riichi, cannot win immediately, fewer than four kans',
        max_roots=24,query_timeout_seconds=40,score_budget_seconds=900,no_training=True,no_continuations=True)
    save(O/'config.json',config)
    mod,_=load_doman_module(R/'artifacts/doman-engine-v2-dataset')
    games=[]; seen=set()
    for directory,roles in [(R/'artifacts/evaluation/doman-tail-H-dev200-20261006',['baseline']),(A/'development-parallel',['baseline','candidate'])]:
        for g in rows(directory/'results.jsonl'):
            for role in roles:
                for m in g[role]:
                    if m['gameplay_sha256'] in seen: continue
                    seen.add(m['gameplay_sha256'])
                    games.append(dict(seed=g['seed'],source='ako' if role=='candidate' else 'mortal',focal=m['seat'] if role=='candidate' else None,path=directory/m['log']))
    counts=collections.Counter(); root_counts=collections.Counter(); roots=[]; terminal=[]; eligibility=[]
    for game in sorted(games,key=lambda x:(x['seed'],x['source'],x['focal'] if x['focal'] is not None else -1)):
        raw=gzip.open(game['path'],'rt').read(); events=list(map(json.loads,raw.splitlines())); src=game['source']; counts[src+'_games']+=1
        reached=False; selected=False; prefix=[]; current=None; scores=None; states=None; kan_count=0
        for i,e in enumerate(events):
            if e['type']=='start_kyoku':
                current=e; prefix=[]; scores=e['scores'].copy(); kan_count=0
                states=[mod.state.PlayerState(p) for p in range(4)] if e['bakaze']=='E' and e['kyoku']==4 else None
                if states is not None and not reached: counts[src+'_east4_games']+=1; reached=True
            if states is None: continue
            prefix.append(event(e))
            if e['type']=='reach_accepted': scores[e['actor']]-=1000
            if e['type'] in ('hora','ryukyoku') and 'deltas' in e: scores=[x+y for x,y in zip(scores,e['deltas'])]
            if e['type'] in ('ankan','kakan','daiminkan'): kan_count+=1
            for p,state in enumerate(states): state.update(json.dumps(visible([e],p)[0]))
            if e['type']!='tsumo': continue
            focal=e['actor']; state=states[focal]; cans=state.last_cans
            if src=='ako' and focal!=game['focal']: continue
            counts[src+'_east4_self_draws']+=1
            if max(scores)>=30000 or state.shanten>1 or state.self_riichi_declared or cans.can_tsumo_agari or not cans.can_discard or kan_count>=4: continue
            rank=sorted(range(4),key=lambda p:(-scores[p],p)).index(focal)+1
            counts[src+'_eligible_roots']+=1
            eligibility.append(dict(seed=game['seed'],source=src,index=i,focal=focal,rank=rank,can_riichi=cans.can_riichi))
            key=(src,rank)
            if selected or root_counts[key]>=3: continue
            clean=visible(prefix,focal)
            mutated=copy.deepcopy(prefix)
            for ev in mutated:
                if ev['type']=='start_kyoku':
                    for p in range(4):
                        if p!=focal: ev['tehais'][p]=['C']*13
                elif ev['type']=='tsumo' and ev['actor']!=focal: ev['pai']='C'
            assert clean==visible(mutated,focal)
            actual=event(events[i+1]); assert actual.get('actor')==focal
            root=dict(id=f'{game["seed"]}-{src}-p{focal}-e{i}',seed=game['seed'],source=src,focal=focal,rank=rank,
                scores=scores.copy(),shanten=state.shanten,turn=state.at_turn,can_riichi=cans.can_riichi,
                hand=list(state.tehai),red=list(state.akas_in_hand),source_log=str(game['path'].relative_to(R)),source_sha256=digest(game['path']),
                event_index=i,prefix=clean,logged_action=actual)
            roots.append(root); selected=True; root_counts[key]+=1
        last_start=next(e for e in reversed(events) if e['type']=='start_kyoku')
        final_scores=list(mod.dataset.Grp.load_log(raw).take_final_scores())
        counts[src+'_terminal_below30000']+=int(max(final_scores)<30000)
        if max(final_scores)<30000:
            terminal.append(dict(seed=game['seed'],source=src,focal=game['focal'],scores=final_scores,
                last_start={k:last_start[k] for k in ('bakaze','kyoku','honba','oya','scores')},
                endings=[event(e) for e in events[events.index(last_start):] if e['type'] in ('hora','ryukyoku','end_game')],source_log=str(game['path'].relative_to(R))))
    save(O/'roots.json',roots); save(O/'terminal-cases.json',terminal)
    save(O/'census.json',dict(counts=counts,root_counts={str(k):v for k,v in root_counts.items()},unique_games=len(games),selected_roots=len(roots),eligible_roots=eligibility))
    config.update(roots_sha256=digest(O/'roots.json'),census_sha256=digest(O/'census.json')); save(O/'config.json',config)
    print(json.dumps(dict(counts=counts,selected=len(roots),root_counts={str(k):v for k,v in root_counts.items()})),flush=True)

def build():
    upstream=A/'upstream'; source=(upstream/'ai_src/jun_calc.cpp').read_text()
    marker='\tconst int oya_first = (12 + oya - kyoku) % 4;'
    assert source.count(marker)==1
    patch='''
    // Diagnostic override: terminal outcome value only, not an arena rule change.
    if (kyoku >= 8 && !is_renchan) return calc_jun_prob_end(ten, oya_first);
    if (kyoku >= 7 && is_renchan) {
        const auto final_rank = calc_jun_prob_end(ten, oya_first);
        if (final_rank[oya][0] == 1.0f) return final_rank;
    }
'''
    (O/'jun_calc_terminal.cpp').write_text(source.replace(marker,marker+patch))
    includes=[str(upstream),str(upstream/'ai_src'),str(A/'deps/root/usr/include')]
    for name,extra in [('original',[]),('terminal',[str(O/'jun_calc_terminal.cpp')])]:
        cmd=['g++','-include','cstdint','-O3','-std=c++11','-fopenmp','-pthread','-Wl,--export-dynamic']+['-I'+p for p in includes]+[str(O/'probe.cpp')]+extra+['-L'+str(upstream),'-lai','-o',str(O/name)]
        with (O/f'build-{name}.log').open('w') as log: subprocess.run(cmd,check=True,stdout=log,stderr=subprocess.STDOUT,timeout=180)
    save(O/'build.json',dict(original_binary=digest(O/'original'),terminal_binary=digest(O/'terminal'),source=digest(O/'jun_calc_terminal.cpp'),
        original_library=digest(upstream/'libai.so'),probe_source=digest(O/'probe.cpp')))
    print('Both diagnostic probes compiled',flush=True)

def score():
    cfg=read(O/'config.json'); roots=read(O/'roots.json'); assert digest(O/'roots.json')==cfg['roots_sha256']
    mod,_=load_doman_module(R/'artifacts/doman-engine-v2-dataset')
    mortal=R/'.loacl/Mortal/mortal'; os.chdir(mortal); sys.path.insert(0,str(mortal))
    import torch
    from mortal_runner import create_engine
    torch.set_num_threads(2); torch.backends.cudnn.benchmark=False; torch.backends.cudnn.deterministic=True
    engine=create_engine(checkpoint_path=mortal/'patch/model_v4_20240308_best_min.pth',device_name='cuda',enable_amp=False)
    started=time.monotonic(); results=[]; procs={}; handles=[]
    with tempfile.TemporaryDirectory(prefix='ako-endgame-') as temp:
        tmp=pathlib.Path(temp)
        shutil.copytree(A/'upstream/params',tmp/'params'); shutil.copy2(A/'upstream/setup_mjai.json',tmp/'setup_mjai.json')
        env={**os.environ,'LD_LIBRARY_PATH':str(A/'upstream'),'OMP_NUM_THREADS':'2'}
        try:
            for name in ('original','terminal'):
                err=(O/(name+'-stderr.log')).open('w'); handles.append(err)
                procs[name]=subprocess.Popen([str(O/name)],cwd=tmp,env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=err,text=True,bufsize=1)
            def query(name,obj):
                p=procs[name]; p.stdin.write(json.dumps(obj)+'\n'); p.stdin.flush()
                if not select.select([p.stdout],[],[],cfg['query_timeout_seconds'])[0]: raise TimeoutError(name+' query timeout')
                line=p.stdout.readline()
                if not line: raise RuntimeError(name+' exited '+str(p.poll()))
                return json.loads(line)
            checks=[]
            for kyoku,scores,oya,renchan in [(8,[26000,25500,25000,23500],0,False),(7,[24000,24000,24000,28000],3,True),(6,[26000,25500,25000,23500],2,False),(8,[32000,24000,23000,21000],0,False)]:
                q=dict(kind='value',kyoku=kyoku,scores=scores,oya=oya,renchan=renchan)
                checks.append(dict(input=q,original=query('original',q),terminal=query('terminal',q)))
            assert checks[0]['original']!=checks[0]['terminal'] and checks[1]['original']!=checks[1]['terminal']
            assert checks[2]['original']==checks[2]['terminal'] and checks[3]['original']==checks[3]['terminal']
            save(O/'value-controls.json',checks)
            terminal_values=[]
            for case in read(O/'terminal-cases.json'):
                if case['last_start']['kyoku']!=4: continue
                q=dict(kind='value',kyoku=8,scores=case['scores'],oya=0,renchan=False)
                terminal_values.append(dict(**case,original=query('original',q),terminal=query('terminal',q),scope='valuation of terminal score vector; not a counterfactual action return'))
            save(O/'terminal-values.json',terminal_values)
            with (O/'decisions.jsonl').open('w') as sink:
                for root in roots:
                    assert time.monotonic()-started<cfg['score_budget_seconds']
                    prefix=root['prefix']; focal=root['focal']; state=mod.state.PlayerState(focal); bot=mod.mjai.Bot(engine,focal)
                    bot.react(json.dumps(dict(type='start_game',names=['audit']*4)),can_act=False)
                    for i,ev in enumerate(prefix):
                        line=json.dumps(ev); state.update(line); pred=bot.react(line,can_act=i==len(prefix)-1)
                    mortal_action=event(json.loads(pred))
                    q=dict(kind='policy',focal=focal,record=[dict(type='start_game',kyoku_first=4,aka_flag=True)]+prefix)
                    choices={name:query(name,q) for name in ('original','terminal')}
                    for name in choices:
                        assert choices[name]
                        state.validate_reaction(json.dumps(choices[name][0]['moves'][0]))
                    actual=root['logged_action']
                    control=(mortal_action if root['source']=='mortal' else choices['original'][0]['moves'][0])==actual
                    row=dict(id=root['id'],source=root['source'],rank=root['rank'],mortal=mortal_action,logged_action=actual,replay_control_match=control,
                        changed=choices['original'][0]['moves']!=choices['terminal'][0]['moves'],choices=choices)
                    results.append(row); sink.write(json.dumps(row)+'\n'); sink.flush()
                    print(json.dumps({k:v for k,v in row.items() if k!='choices'}),flush=True)
            save(O/'score-summary.json',dict(count=len(results),changed=sum(r['changed'] for r in results),controls_matched=sum(r['replay_control_match'] for r in results),elapsed_seconds=time.monotonic()-started,
                decision_source_sha256=digest(__file__),no_rollouts=True,no_training_labels=True))
        finally:
            for p in procs.values():
                if p.poll() is None: p.terminate()
                try: p.wait(timeout=5)
                except subprocess.TimeoutExpired: p.kill(); p.wait()
            for h in handles: h.close()

if __name__=='__main__':
    p=argparse.ArgumentParser(); p.add_argument('mode',choices=['prepare','build','score']); a=p.parse_args()
    globals()[a.mode]()

"""Small riichi/no-declaration feedback probe using sanitized public prefixes."""
import argparse
from collections import Counter
import copy
import gzip
import hashlib
import json
from pathlib import Path
import random


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def inventory():
    tiles = [f'{n}{s}' for s in 'mps' for n in range(1, 10) for _ in range(3 if n == 5 else 4)]
    return tiles + [f'5{s}r' for s in 'mps'] + [v for v in ('E','S','W','N','P','F','C') for _ in range(4)]


def visible(events, focal):
    result = []
    for raw in events:
        event = {k: copy.deepcopy(v) for k,v in raw.items() if k not in ('meta','names','seed')}
        if event['type'] == 'start_kyoku':
            event['tehais'] = [hand if i == focal else ['?']*13 for i,hand in enumerate(event['tehais'])]
        elif event['type'] == 'tsumo' and event['actor'] != focal:
            event['pai'] = '?'
        result.append(event)
    return result


def select_roots(root):
    directory = root/'artifacts/evaluation/doman-advantage-D-dev100-20261005'
    audit = root/'artifacts/finetune/doman-advantage-D-20261005/decision-review/first-divergences.jsonl'
    pairs = {row['seed']:row for row in map(json.loads,(directory/'results.jsonl').read_text().splitlines())}
    selected, counts = [], {'early':0,'last':0}
    for row in sorted(map(json.loads,audit.read_text().splitlines()),key=lambda r:(r['seed'],r['seat'])):
        if row['identical'] or row['candidate_event']['type'] != 'reach':
            continue
        path = directory/pairs[row['seed']]['candidate'][row['seat']]['log']
        with gzip.open(path,'rt') as stream:
            events = list(map(json.loads,stream))
        index = row['event_index']
        start = max(i for i,e in enumerate(events[:index]) if e['type']=='start_kyoku')
        prefix = events[start:index]
        if any(e['type'] not in ('start_kyoku','tsumo','dahai') for e in prefix):
            continue
        group = 'last' if row['kyoku']==4 else 'early'
        if counts[group] == 2:
            continue
        # Use only the immediate riichi discard, never subsequent outcomes.
        discard = events[index+1]
        assert discard['type']=='dahai' and discard['actor']==row['seat']
        clean_discard = {k:v for k,v in discard.items() if k!='meta'}
        public = visible(prefix,row['seat'])
        assert public[-1]['type']=='tsumo' and public[-1]['actor']==row['seat']
        selected.append(dict(id=f"{row['seed']}-seat{row['seat']}",source_seed=row['seed'],
            focal=row['seat'],group=group,prefix=public,discard=clean_discard,
            source_log=path.relative_to(root).as_posix(),source_sha256=digest(path)))
        counts[group]+=1
        if sum(counts.values())==4:
            break
    assert counts=={'early':2,'last':2}
    return selected


def sample_world(root, seed):
    """Condition tile slots on visible draws/discards, without true hidden tiles.

    Opponent non-tsumogiri discards are assigned to a uniform old-hand slot.
    This structural prior does NOT use likelihoods of the opponents' policy.
    """
    rng=random.Random(seed)
    prefix=root['prefix']; start=prefix[0]; focal=root['focal']
    slots=[None]*136
    hands=[list(range(i*13,(i+1)*13)) for i in range(4)]
    slots[focal*13:(focal+1)*13]=start['tehais'][focal]
    # Same layout as Board.init_from_seed: 52 hands,4 rinshan,5 dora,5 ura,70 wall.
    slots[60]=start['dora_marker']
    draw_slot=None; draw_count=0
    for event in prefix[1:]:
        actor=event['actor']
        if event['type']=='tsumo':
            draw_slot=135-draw_count; draw_count+=1
            assert actor==(start['oya']+draw_count-1)%4
            if actor==focal:
                slots[draw_slot]=event['pai']
        else:
            assert event['type']=='dahai' and draw_slot is not None
            if event['tsumogiri']:
                assert slots[draw_slot] in (None,event['pai'])
                slots[draw_slot]=event['pai']
            else:
                if actor==focal:
                    chosen=next(i for i in hands[actor] if slots[i]==event['pai'])
                else:
                    chosen=rng.choice(hands[actor])
                    assert slots[chosen] is None
                    slots[chosen]=event['pai']
                hands[actor].remove(chosen)
                hands[actor].append(draw_slot)
            draw_slot=None
    known=Counter(tile for tile in slots if tile is not None)
    full=Counter(inventory())
    assert all(count<=full[tile] for tile,count in known.items())
    rest=list((full-known).elements()); rng.shuffle(rest)
    for i,tile in enumerate(slots):
        if tile is None: slots[i]=rest.pop()
    assert not rest and Counter(slots)==full
    return dict(id=root['id'],focal=focal,kyoku=start['kyoku']-1,honba=start['honba'],
        kyotaku=start['kyotaku'],scores=start['scores'],haipai=[slots[i*13:(i+1)*13] for i in range(4)],
        rinshan=slots[52:56],dora_indicators=slots[56:61],ura_indicators=slots[61:66],yama=slots[66:],
        discards=[e for e in prefix if e['type']=='dahai'],expected_prefix=prefix,
        discard=root['discard'],future_seed=seed)


def validate_world(root, world):
    tiles=sum(world['haipai'],[])+world['rinshan']+world['dora_indicators']+world['ura_indicators']+world['yama']
    assert Counter(tiles)==Counter(inventory())
    hands=copy.deepcopy(world['haipai']); wall=world['yama'].copy()
    focal=root['focal']; draw=None
    assert hands[focal]==root['prefix'][0]['tehais'][focal]
    for event in root['prefix'][1:]:
        actor=event['actor']
        if event['type']=='tsumo':
            draw=wall.pop()
            if actor==focal: assert draw==event['pai']
        elif event['tsumogiri']:
            assert draw==event['pai']
        else:
            hands[actor].remove(event['pai']); hands[actor].append(draw)
    assert world['dora_indicators'][-1]==root['prefix'][0]['dora_marker']


def prepare(repo, output):
    output.mkdir(parents=True,exist_ok=False)
    roots=select_roots(repo)
    (output/'roots.json').write_text(json.dumps(roots,indent=2)+'\n',encoding='utf-8')
    diagnostics=[]
    for idx,root in enumerate(roots):
        assignments=set(); walls=set()
        for i in range(128):
            world=sample_world(root,29_000_000+idx*1000+i)
            validate_world(root,world)
            assignments.add(json.dumps([h for p,h in enumerate(world['haipai']) if p!=root['focal']]))
            walls.add(json.dumps(world['yama']))
        assert len(assignments)==128 and len(walls)==128
        diagnostics.append(dict(id=root['id'],structurally_valid_worlds=128,
                                distinct_hidden_hands=len(assignments),distinct_walls=len(walls)))
    # Hidden-data independence: arbitrary corruption of excluded fields has no
    # effect on sanitation; the sampler only receives already-sanitized roots.
    for root in roots:
        altered=copy.deepcopy(root['prefix'])
        for event in altered:
            if event['type']=='start_kyoku':
                for p in range(4):
                    if p!=root['focal']: event['tehais'][p]=['C']*13
            elif event['type']=='tsumo' and event['actor']!=root['focal']:
                event['pai']='C'
        assert visible(altered,root['focal'])==root['prefix']
    result=dict(roots=diagnostics,hidden_sanitation_invariance=True,
                scope='Structural tile/history validity only; no calibrated opponent belief or policy improvement claim')
    (output/'sampler-checks.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(result,indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    prepare(args.repo,args.output)

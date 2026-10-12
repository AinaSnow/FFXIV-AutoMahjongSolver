"""Matched fresh-data control: action head versus last two residual blocks."""
import argparse
import copy
import json
import os
from pathlib import Path
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
import torch
from torch import nn

import finetune_heads as generation
from finetune_advantage import action_loss, reward_weights
from finetune_credit import ScoreRankModel, rank_probabilities, hand_rewards, UTILITY
from self_play import write_json, inspect_log
from self_play_dataset import file_hash, validate_arrays
from doman_engine import load_doman_dataset


def read(path):
    return json.loads(Path(path).read_text())


def encode_dataset(cfg, output, pilot=False):
    """Cache the frozen prefix in float32; never cache across trainable layers."""
    views = []
    cache_path = output/'prefix-f32.bin'
    checks = dict(split_encoding_equal=True, samples_checked=0, oracle=False)
    old_encoder = generation.encode_game
    count = 0
    with cache_path.open('wb') as cache:
        def encode(game, brain, batch_size):
            nonlocal count
            assert not game.take_invisible_obs()
            arrays = dict(obs=np.asarray(game.take_obs(), dtype=np.float32),
                masks=np.asarray(game.take_masks(), dtype=np.bool_),
                actions=np.asarray(game.take_actions(), dtype=np.int64),
                hand_index=np.asarray(list(game.take_at_kyoku()), dtype=np.int64),
                dones=np.asarray(game.take_dones(), dtype=np.bool_),
                apply_gamma=np.asarray(game.take_apply_gamma(), dtype=np.bool_))
            n = len(arrays['actions'])
            views.append(dict(player=game.take_player_id(), offset=count, n=n))
            if not n:
                return None
            validate_arrays(arrays, 4, int(arrays['hand_index'].max())+1)
            assert count+n <= cfg['max_decisions']
            layers = list(brain.encoder.net.children())
            stem = nn.Sequential(*layers[:cfg['cut_index']]).eval()
            suffix = nn.Sequential(*layers[cfg['cut_index']:], brain.actv).eval()
            phi = []
            with torch.inference_mode():
                for start in range(0,n,batch_size):
                    obs = torch.from_numpy(arrays['obs'][start:start+batch_size]).cuda()
                    prefix = stem(obs)
                    assert tuple(prefix.shape[1:]) == (256,34)
                    result = suffix(prefix)
                    if checks['samples_checked'] < 256:
                        assert torch.equal(brain(obs), result)
                        checks['samples_checked'] += len(obs)
                    cache.write(prefix.cpu().contiguous().numpy().tobytes())
                    phi.append(result.cpu())
            count += n
            return dict(phi=torch.cat(phi), masks=torch.from_numpy(arrays['masks']),
                actions=torch.from_numpy(arrays['actions']), hand_index=torch.from_numpy(arrays['hand_index']))
        generation.encode_game = encode
        try:
            data, engine, manifest = generation.generate(cfg, output, pilot=pilot)
        finally:
            generation.encode_game = old_encoder
    assert count == len(data['actions']) and cache_path.stat().st_size == count*256*34*4
    write_json(output/'player-views.json', views)
    write_json(output/'prefix-manifest.json', dict(shape=[count,256,34],dtype='float32',
        sha256=file_hash(cache_path), player_views_sha256=file_hash(output/'player-views.json'), checks=checks))
    return data, engine, manifest


def rewards_for_new_data(cfg, output, data, grp_type):
    """The old fixed rank-value model has never seen these new train seeds."""
    saved = torch.load(cfg['rank_model'],map_location='cpu',weights_only=True)
    assert saved['family']=='score'
    model = ScoreRankModel().eval()
    model.load_state_dict(saved['model'])
    rows = [r for r in map(json.loads,(output/'matches.jsonl').read_text().splitlines()) if not r['duplicate']]
    assert set(saved['training_seeds']).isdisjoint(r['seed'] for r in rows)
    views = read(output/'player-views.json')
    assert len(views)==4*len(rows)
    targets = torch.empty(len(data['actions']),dtype=torch.float64)
    hand_targets = []
    with torch.inference_mode():
        for index,row in enumerate(rows):
            path=output/row['path']
            assert file_hash(path)==row['sha256']
            raw,_ = inspect_log(path,row['seed'])
            match=grp_type.load_log(raw)
            features=torch.as_tensor(np.asarray(match.take_feature()),dtype=torch.float64)
            ranks=torch.tensor(list(match.take_rank_by_player()),dtype=torch.long)
            logits=model([features[:end] for end in range(1,len(features)+1)])
            values=rank_probabilities(logits) @ UTILITY
            delta=hand_rewards(values,ranks)
            assert torch.allclose(delta.sum(0),UTILITY[ranks]-values[0],atol=1e-8)
            group=views[index*4:index*4+4]
            assert sorted(v['player'] for v in group)==[0,1,2,3]
            for view in group:
                start,n,player=view['offset'],view['n'],view['player']
                assert (data['ranks'][start:start+n]==ranks[player]).all()
                targets[start:start+n]=delta[data['hand_index'][start:start+n],player]
            hand_targets.append(delta)
    assert torch.isfinite(targets).all()
    torch.save(dict(targets=targets,hand_rewards=hand_targets,rank_model_sha256=file_hash(cfg['rank_model'])),output/'rewards.pt')
    return targets


def trained_brain_keys(brain, cfg):
    keys=[]
    for index in cfg['train_blocks']:
        block=brain.encoder.net[index]
        for name,param in block.named_parameters():
            keys.append(f'encoder.net.{index}.{name}')
            param.requires_grad_(True)
        for module in block.modules():
            if isinstance(module,nn.BatchNorm1d):
                module.requires_grad_(False)
    return {name for name,param in brain.named_parameters() if param.requires_grad}


def fit(cfg, output, data, rewards, engine, *, pilot=False):
    from model import Brain, DQN
    from mortal_runner import load_checkpoint
    state,identity=load_checkpoint(torch,cfg['baseline'])
    assert identity['checkpoint_sha256']==cfg['baseline_sha256']
    meta=read(output/'prefix-manifest.json')
    assert file_hash(output/'prefix-f32.bin')==meta['sha256']
    cache=np.memmap(output/'prefix-f32.bin',dtype=np.float32,mode='r',shape=tuple(meta['shape']))
    weights,weighting=reward_weights(rewards,data['weights'],cfg['reward_temperature_floor'],cfg['raw_weight_lower'],cfg['raw_weight_upper'])
    write_json(output/'weighting-report.json',weighting)
    phi,masks,actions=[data[name].cuda() for name in ('phi','masks','actions')]
    weights=weights.cuda()
    reference_weight=state['current_dqn']['net.weight'][1:].cuda()
    reference_bias=state['current_dqn']['net.bias'][1:].cuda()
    steps=cfg['pilot_steps'] if pilot else cfg['steps']
    generator=torch.Generator().manual_seed(cfg['training_seed'])
    indices=torch.multinomial(data['weights'],steps*cfg['training_batch_size'],replacement=True,generator=generator)
    torch.save(indices,output/'sampling-indices.pt')
    reports={}
    for label in ['H','T']:
        brain=copy.deepcopy(engine.brain).eval().requires_grad_(False).freeze_bn(True)
        allowed=trained_brain_keys(brain,cfg) if label=='T' else set()
        suffix=nn.Sequential(*list(brain.encoder.net.children())[cfg['cut_index']:],brain.actv).eval()
        head=nn.Linear(1024,46).cuda()
        with torch.no_grad():
            head.weight.copy_(reference_weight); head.bias.copy_(reference_bias)
        groups=[dict(params=list(head.parameters()),lr=cfg['learning_rate'])]
        tail_params=[p for p in brain.parameters() if p.requires_grad]
        if tail_params: groups.append(dict(params=tail_params,lr=cfg['tail_learning_rate']))
        parameters=list(head.parameters())+tail_params
        optimizer=torch.optim.AdamW(groups,weight_decay=cfg['weight_decay'])
        losses=[]
        torch.cuda.reset_peak_memory_stats()
        torch.cuda.synchronize(); started=time.monotonic()
        for step,batch in enumerate(indices.split(cfg['training_batch_size']),1):
            idx=batch.cuda()
            if label=='H':
                features=phi[idx]
            else:
                prefix=torch.from_numpy(np.array(cache[batch.numpy()],copy=True)).cuda()
                features=suffix(prefix)
            logits=head(features)
            with torch.no_grad(): prior=nn.functional.linear(phi[idx],reference_weight,reference_bias)
            loss,nll,kl=action_loss(logits,prior,masks[idx],actions[idx],weights[idx],cfg['policy_temperature'],cfg['kl_weight'])
            assert torch.isfinite(loss)
            optimizer.zero_grad(set_to_none=True); loss.backward()
            assert all(p.grad is None for name,p in brain.named_parameters() if name not in allowed)
            if label=='T' and step==1:
                assert any(p.grad is not None and p.grad.abs().max()>0 for p in tail_params)
            grad_norm=nn.utils.clip_grad_norm_(parameters,cfg['max_grad_norm'])
            optimizer.step()
            if step==1 or step%100==0 or step==steps:
                row=dict(step=step,weighted_nll=nll.item(),kl=kl.item(),loss=loss.item(),grad_norm=grad_norm.item())
                losses.append(row)
                print(json.dumps(dict(candidate=label,**row,seconds=time.monotonic()-started)),flush=True)
            assert time.monotonic()-started<cfg['max_fit_seconds']
        torch.cuda.synchronize(); fit_seconds=time.monotonic()-started
        brain_state={k:v.detach().cpu() for k,v in brain.state_dict().items()}
        changed={key for key,value in brain_state.items() if not torch.equal(value,state['mortal'][key])}
        assert changed<=allowed
        if label=='T': assert changed and all(any(key.startswith(f'encoder.net.{i}.') for key in changed) for i in cfg['train_blocks'])
        else: assert not changed
        head_state={k:v.clone() for k,v in state['current_dqn'].items()}
        head_state['net.weight'][1:]=head.weight.detach().cpu()
        head_state['net.bias'][1:]=head.bias.detach().cpu()
        assert all(torch.equal(state['current_dqn'][k][0],v[0]) for k,v in head_state.items())
        candidate=dict(mortal=brain_state,current_dqn=head_state,config=copy.deepcopy(state['config']),
            finetune=dict(experiment=cfg['experiment'],role=label,config=cfg,steps=steps,
                baseline_sha256=cfg['baseline_sha256'],trainable_brain_keys=sorted(allowed),
                reward='Fixed old rank-model next-hand terminal-utility delta; not counterfactual action advantage',
                all_bn_affine_and_statistics_frozen=True,value_row_frozen=True))
        path=output/f'candidate-{label}.pth'
        torch.save(candidate,path)
        torch.save(dict(optimizer=optimizer.state_dict(),steps=steps,config=cfg),output/f'optimizer-{label}.pth')
        saved=torch.load(path,map_location='cpu',weights_only=True)
        reload=Brain(version=4,**state['config']['resnet']).cuda().eval()
        reload.load_state_dict(saved['mortal'])
        dqn=DQN(version=4).cuda().eval(); dqn.load_state_dict(saved['current_dqn'])
        reload_suffix=nn.Sequential(*list(reload.encoder.net.children())[cfg['cut_index']:],reload.actv)
        chosen_indices=torch.randperm(len(actions),generator=torch.Generator().manual_seed(810610))[:8192]
        action_changes=0; kl_total=0.
        with torch.inference_mode():
            for batch in chosen_indices.split(256):
                idx=batch.cuda(); prefix=torch.from_numpy(np.array(cache[batch.numpy()],copy=True)).cuda()
                live_features=suffix(prefix)
                logits=head(live_features)
                decision=logits.masked_fill(~masks[idx],-torch.inf).argmax(-1)
                assert torch.equal(decision,dqn(reload_suffix(prefix),masks[idx]).argmax(-1))
                prior=nn.functional.linear(phi[idx],reference_weight,reference_bias)
                action_changes+=(decision!=prior.masked_fill(~masks[idx],-torch.inf).argmax(-1)).sum().item()
                _,_,kl=action_loss(logits,prior,masks[idx],actions[idx],weights[idx],cfg['policy_temperature'],cfg['kl_weight'])
                kl_total+=kl.item()*len(batch)
        reports[label]=dict(sha256=file_hash(path),steps=steps,fit_seconds=fit_seconds,
            trainable_parameters=sum(p.numel() for p in parameters),trainable_brain_keys=sorted(allowed),
            changed_brain_keys=sorted(changed),all_other_brain_tensors_bitwise_equal=True,
            bn_and_value_row_bitwise_equal=True,losses=losses,
            diagnostic_decisions=len(chosen_indices),diagnostic_action_changes=action_changes,
            diagnostic_mean_kl=kl_total/len(chosen_indices),saved_checkpoint_choices_equal=True,
            peak_vram_bytes=torch.cuda.max_memory_allocated())
        write_json(output/'training-report.json',reports)
        del brain,suffix,head,optimizer,reload,reload_suffix,dqn
    return reports


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--mode',choices=['pilot','data','fit'],required=True)
    args=parser.parse_args(); cfg=read(args.config); output=args.output.resolve()
    for path,expected in cfg['input_sha256'].items(): assert file_hash(path)==expected,path
    if args.mode!='fit':
        output.mkdir(parents=True,exist_ok=False)
        write_json(output/'experiment-config.json',cfg)
        write_json(output/'provenance.json',dict(script_sha256=file_hash(__file__),config_sha256=file_hash(args.config),inputs=cfg['input_sha256']))
    else:
        assert read(output/'experiment-config.json')==cfg
        assert read(output/'provenance.json')['script_sha256']==file_hash(__file__)
        assert read(Path(cfg['pilot_output'])/'result.json')['status']=='pilot-passed'
    started=time.monotonic()
    try:
        if args.mode in ('pilot','data'):
            data,engine,manifest=encode_dataset(cfg,output,pilot=args.mode=='pilot')
            _,_,grp_type,_=load_doman_dataset(cfg['doman_engine'])
            rewards=rewards_for_new_data(cfg,output,data,grp_type)
            manifest['generator_rank_label']=manifest.pop('reward')
            manifest['reward']='Fixed independent rank model: next-hand expected terminal utility delta'
            manifest['rank_model_sha256']=file_hash(cfg['rank_model'])
            manifest['rewards_sha256']=file_hash(output/'rewards.pt')
            write_json(output/'data-manifest.json',manifest)
        else:
            manifest=read(output/'data-manifest.json')
            assert manifest['status']=='complete'
            assert file_hash(output/'features.pt')==manifest['features_sha256']
            assert file_hash(output/'rewards.pt')==manifest['rewards_sha256']
            data=torch.load(output/'features.pt',map_location='cpu',weights_only=True)
            rewards=torch.load(output/'rewards.pt',map_location='cpu',weights_only=True)['targets']
            _,_,_,_,engine,_=generation.load_runtime(cfg)
        if args.mode!='data':
            fit(cfg,output,data,rewards,engine,pilot=args.mode=='pilot')
        write_json(output/'result.json',dict(status='pilot-passed' if args.mode=='pilot' else 'data-ready' if args.mode=='data' else 'candidates-ready',
            elapsed_seconds=time.monotonic()-started,live_weights_modified=False))
    except BaseException as error:
        write_json(output/'result.json',dict(status='failed',error=f'{type(error).__name__}: {error}',elapsed_seconds=time.monotonic()-started))
        raise


if __name__=='__main__': main()

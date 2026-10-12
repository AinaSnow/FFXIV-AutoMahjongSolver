"""One bounded, frozen-backbone reward-weighted action-learning candidate D."""
import argparse
import copy
import json
import math
import os
from pathlib import Path
import sys
import time

import torch
from torch.nn import functional as F

from doman_engine import load_doman_dataset
from self_play import write_json
from self_play_dataset import file_hash


def reward_weights(rewards, sampling_weights, floor, lower, upper):
    """Use only the training distribution to fix one reward temperature."""
    probability = sampling_weights.double() / sampling_weights.sum()
    rewards = rewards.double()
    mean = (probability * rewards).sum()
    std = ((probability * (rewards - mean).square()).sum()).sqrt()
    temperature = max(std.item(), floor)
    raw = (rewards / temperature).clamp(math.log(lower), math.log(upper)).exp()
    normalizer = (probability * raw).sum()
    return (raw / normalizer).float(), dict(
        reward_mean=mean.item(), reward_std=std.item(), temperature=temperature,
        raw_lower=lower, raw_upper=upper, normalizer=normalizer.item(),
        minimum=(raw / normalizer).min().item(), maximum=(raw / normalizer).max().item(),
        weighted_lower_clip_fraction=probability[rewards / temperature <= math.log(lower)].sum().item(),
        weighted_upper_clip_fraction=probability[rewards / temperature >= math.log(upper)].sum().item())


def masked_log_probabilities(logits, masks, temperature):
    # Illegal entries become zero only after normalization over legal actions.
    return F.log_softmax((logits / temperature).masked_fill(~masks, -torch.inf), dim=-1).masked_fill(~masks, 0.)


def action_loss(logits, reference_logits, masks, actions, weights, temperature, kl_weight):
    logp = masked_log_probabilities(logits, masks, temperature)
    with torch.no_grad():
        reference_logp = masked_log_probabilities(reference_logits, masks, temperature)
        reference_probability = reference_logp.exp().masked_fill(~masks, 0.)
    nll = -logp.gather(1, actions[:, None]).squeeze(1)
    weighted_nll = (weights * nll).mean()
    kl = (reference_probability * (reference_logp - logp)).sum(-1).mean()
    return weighted_nll + kl_weight * kl, weighted_nll, kl


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    cfg = json.loads(args.config.read_text())
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    write_json(output / 'experiment-config.json', cfg)
    started = time.monotonic()
    provenance = dict(trainer_sha256=file_hash(__file__), config_sha256=file_hash(args.config),
                      inputs=cfg['input_sha256'], live_weights_modified=False)
    write_json(output / 'provenance.json', provenance)
    try:
        for path, expected in cfg['input_sha256'].items():
            assert file_hash(path) == expected, path
        _, _, _, build = load_doman_dataset(cfg['doman_engine'])
        os.chdir(cfg['mortal_dir'])
        sys.path[:0] = [cfg['mortal_dir'], str(Path(__file__).resolve().parents[1])]
        from model import DQN
        from mortal_runner import load_checkpoint

        torch.set_num_threads(2)
        torch.backends.cudnn.benchmark = False
        torch.backends.cudnn.deterministic = True
        torch.cuda.reset_peak_memory_stats()
        run, credit = Path(cfg['training_run']), Path(cfg['credit_run'])
        data = torch.load(run / 'features.pt', map_location='cpu', weights_only=True)
        rewards = torch.load(credit / 'cross-fitted-rewards.pt', map_location='cpu', weights_only=True)
        folds = json.loads((credit / 'cross-fit-seeds.json').read_text())
        assert folds == rewards['folds']
        expected_seeds = set(range(106000, 106128))
        seen = set()
        for fold in folds:
            train, held = set(fold['train_seeds']), set(fold['predicted_seeds'])
            assert train.isdisjoint(held) and train | held == expected_seeds
            assert seen.isdisjoint(held)
            seen |= held
        assert seen == expected_seeds
        targets = rewards['targets']
        assert len(targets) == len(data['actions']) == 141863
        assert torch.isfinite(targets).all()
        assert data['masks'].gather(1, data['actions'][:, None]).all()
        weights, weighting = reward_weights(targets, data['weights'], cfg['reward_temperature_floor'],
                                            cfg['raw_weight_lower'], cfg['raw_weight_upper'])
        write_json(output / 'weighting-report.json', weighting)

        original = json.loads((run / 'experiment-config.json').read_text())
        state, identity = load_checkpoint(torch, original['baseline'])
        assert identity['checkpoint_sha256'] == original['baseline_sha256']
        # The v4 public state-value row cancels in a softmax. Train only advantages.
        action_head = torch.nn.Linear(1024, 46).cuda()
        with torch.no_grad():
            action_head.weight.copy_(state['current_dqn']['net.weight'][1:])
            action_head.bias.copy_(state['current_dqn']['net.bias'][1:])
        reference = copy.deepcopy(action_head).requires_grad_(False).eval()
        phi, masks, actions = (data[key].cuda() for key in ('phi', 'masks', 'actions'))
        weights = weights.cuda()
        optimizer = torch.optim.AdamW(action_head.parameters(), lr=original['learning_rate'],
                                      weight_decay=original['weight_decay'])
        generator = torch.Generator().manual_seed(original['training_seed'])
        indices = torch.multinomial(data['weights'], original['steps'] * original['training_batch_size'],
                                    replacement=True, generator=generator)
        losses = []
        fit_started = time.monotonic()
        for step, batch in enumerate(indices.split(original['training_batch_size']), 1):
            idx = batch.cuda()
            logits = action_head(phi[idx])
            with torch.no_grad():
                reference_logits = reference(phi[idx])
            loss, nll, kl = action_loss(logits, reference_logits, masks[idx], actions[idx], weights[idx],
                                       cfg['policy_temperature'], cfg['kl_weight'])
            assert torch.isfinite(loss)
            optimizer.zero_grad(set_to_none=True)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(action_head.parameters(), original['max_grad_norm'])
            optimizer.step()
            if step == 1 or step % 100 == 0:
                losses.append(dict(step=step, weighted_nll=nll.item(), reference_kl=kl.item(), total=loss.item()))
            if time.monotonic() - fit_started > cfg['max_fit_seconds']:
                raise TimeoutError('Predeclared policy fit budget exhausted')
        torch.cuda.synchronize()
        fit_seconds = time.monotonic() - fit_started
        head_state = {key: value.clone() for key, value in state['current_dqn'].items()}
        head_state['net.weight'][1:] = action_head.weight.detach().cpu()
        head_state['net.bias'][1:] = action_head.bias.detach().cpu()
        candidate = dict(mortal=state['mortal'], current_dqn=head_state, config=copy.deepcopy(state['config']))
        candidate['finetune'] = dict(objective='D',
            objective_description='Clipped reward-weighted legal-action cross entropy plus KL(original || candidate)',
            config=cfg, weighting=weighting, baseline_sha256=identity['checkpoint_sha256'],
            backbone_bn_and_value_row_frozen=True, original_optimizer_resumed=False, steps=original['steps'],
            reward_proxy='C cross-fitted next-hand expected terminal utility delta; not action-level counterfactual advantage')
        path = output / 'candidate-D.pth'
        torch.save(candidate, path)
        saved = torch.load(path, map_location='cpu', weights_only=True)
        assert all(torch.equal(value, saved['mortal'][key]) for key, value in state['mortal'].items())
        assert all(torch.equal(state['current_dqn'][key][0], saved['current_dqn'][key][0]) for key in head_state)
        torch.save(dict(optimizer=optimizer.state_dict(), config=cfg, weighting=weighting, steps=original['steps']),
                   output / 'optimizer-D.pth')

        # Verify saved runner-compatible head choices equal the trained action logits.
        dqn = DQN(version=4).cuda().eval()
        dqn.load_state_dict(saved['current_dqn'])
        cstate = torch.load(credit / 'candidate-C.pth', map_location='cpu', weights_only=True)
        chead = DQN(version=4).cuda().eval()
        chead.load_state_dict(cstate['current_dqn'])
        changes = dict(D_vs_baseline=0, D_vs_C=0)
        margins, kl_sum = [], 0.
        with torch.inference_mode():
            for start in range(0, len(actions), 1024):
                ph, mask = phi[start:start+1024], masks[start:start+1024]
                logits, prior = action_head(ph), reference(ph)
                chosen = logits.masked_fill(~mask, -torch.inf).argmax(-1)
                assert torch.equal(chosen, dqn(ph, mask).argmax(-1))
                assert mask.gather(1, chosen[:, None]).all()
                changes['D_vs_baseline'] += (chosen != prior.masked_fill(~mask, -torch.inf).argmax(-1)).sum().item()
                changes['D_vs_C'] += (chosen != chead(ph, mask).argmax(-1)).sum().item()
                logp = masked_log_probabilities(logits, mask, cfg['policy_temperature'])
                log0 = masked_log_probabilities(prior, mask, cfg['policy_temperature'])
                p0 = log0.exp().masked_fill(~mask, 0.)
                kl_sum += (p0 * (log0-logp)).sum().item()
        write_json(output / 'action-change-report.json', dict(scope='Training-state diagnostic, not strength evidence',
            decisions=len(actions), changes=changes, mean_reference_kl=kl_sum/len(actions),
            saved_head_choices_equal=True, all_choices_legal=True))
        report = dict(sha256=file_hash(path), baseline_sha256=identity['checkpoint_sha256'],
            C_sha256=file_hash(credit/'candidate-C.pth'), steps=original['steps'], training_batch_size=original['training_batch_size'],
            trainable_parameters=sum(p.numel() for p in action_head.parameters()),
            backbone_bn_and_value_row_bitwise_equal=True, fit_seconds=fit_seconds, losses=losses,
            hardware=dict(gpu=torch.cuda.get_device_name(0), vram_bytes=torch.cuda.get_device_properties(0).total_memory,
                          torch=str(torch.__version__), python=sys.version, threads=2),
            weighting=weighting, inference='Unchanged native v4 greedy Mortal runner; training softmax is not used online')
        write_json(output / 'training-report.json', report)
        write_json(output / 'result.json', dict(status='candidate-ready', elapsed_seconds=time.monotonic()-started,
            peak_vram_bytes=torch.cuda.max_memory_allocated(), native_build=build, live_weights_modified=False))
        print(json.dumps(dict(status='candidate-ready', sha256=report['sha256'], weighting=weighting,
                              changes=changes, fit_seconds=fit_seconds)), flush=True)
    except BaseException as error:
        write_json(output / 'result.json', dict(status='failed', error=f'{type(error).__name__}: {error}',
                                              elapsed_seconds=time.monotonic()-started, live_weights_modified=False))
        raise


if __name__ == '__main__':
    main()

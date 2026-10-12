import sys
from pathlib import Path
import unittest

import torch

sys.path.insert(0, str(Path(__file__).resolve().parent))
from finetune_advantage import action_loss, reward_weights


class ActionLearningTests(unittest.TestCase):
    def test_better_return_in_identical_states_increases_that_action_preference(self):
        rewards = torch.tensor([-.3, .3])
        weights, _ = reward_weights(rewards, torch.ones(2), .05, .25, 4.)
        logits = torch.zeros(2, requires_grad=True)
        loss, _, _ = action_loss(logits.expand(2, -1), torch.zeros(2, 2),
            torch.ones(2, 2, dtype=torch.bool), torch.tensor([0, 1]), weights, .5, 1.)
        loss.backward()
        after = logits.detach() - .01 * logits.grad
        self.assertGreater(after[1].item(), after[0].item())

    def test_illegal_actions_have_no_gradient_and_do_not_affect_loss(self):
        logits = torch.tensor([[0., 1., 1000.]], requires_grad=True)
        prior = torch.tensor([[1., 0., -1000.]])
        mask = torch.tensor([[True, True, False]])
        loss, _, kl = action_loss(logits, prior, mask, torch.tensor([1]), torch.ones(1), .5, 1.)
        cropped, _, _ = action_loss(logits[:, :2], prior[:, :2], mask[:, :2], torch.tensor([1]), torch.ones(1), .5, 1.)
        self.assertTrue(torch.isfinite(loss))
        self.assertTrue(torch.allclose(loss, cropped))
        self.assertGreater(kl.item(), 0)
        loss.backward()
        self.assertEqual(logits.grad[0, 2].item(), 0)

    def test_common_value_shift_cancels_and_forced_actions_have_zero_loss(self):
        logits = torch.tensor([[.1, .3], [3., 4.]], dtype=torch.float64)
        masks = torch.tensor([[True, True], [True, False]])
        args = (masks, torch.tensor([1, 0]), torch.ones(2), .5, 1.)
        loss, _, kl = action_loss(logits, logits, *args)
        shifted, _, _ = action_loss(logits+100, logits-50, *args)
        self.assertTrue(torch.allclose(loss, shifted))
        self.assertAlmostEqual(kl.item(), 0)
        forced, _, _ = action_loss(logits[1:], logits[1:], masks[1:], torch.tensor([0]), torch.ones(1), .5, 1.)
        self.assertEqual(forced.item(), 0)

    def test_weight_clipping_bounds_influence_and_preserves_sampling_mean(self):
        rewards = torch.tensor([-100., 0., 100.])
        sampling = torch.tensor([1., 1000., 1.])
        weights, report = reward_weights(rewards, sampling, .05, .25, 4.)
        self.assertTrue(torch.isfinite(weights).all())
        self.assertAlmostEqual((weights*sampling).sum().item()/sampling.sum().item(), 1., places=6)
        self.assertLessEqual((weights.max()/weights.min()).item(), 16.0001)
        self.assertGreater(report['weighted_lower_clip_fraction'], 0)
        self.assertGreater(report['weighted_upper_clip_fraction'], 0)


if __name__ == '__main__':
    unittest.main()

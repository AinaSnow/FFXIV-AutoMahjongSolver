import sys
from pathlib import Path
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parent))
import torch
from finetune_credit import ScoreRankModel, rank_probabilities, hand_rewards, UTILITY


class CreditRewardTests(unittest.TestCase):
    def test_rank_distribution_is_doubly_stochastic(self):
        model=ScoreRankModel()
        for scores in ([2.5]*4,[4.,3.,2.,1.]):
            logits=model([torch.tensor([[2.,0.,0.,*scores]],dtype=torch.float64)])
            matrix=rank_probabilities(logits)
            self.assertTrue(torch.allclose(matrix.sum(-1),torch.ones(1,4,dtype=torch.float64)))
            self.assertTrue(torch.allclose(matrix.sum(-2),torch.ones(1,4,dtype=torch.float64)))
            if len(set(scores))==1:
                self.assertTrue(torch.allclose(matrix,torch.full_like(matrix,.25)))
            else:
                self.assertGreater(matrix[0,0,0].item(),matrix[0,3,0].item())

    def test_hand_rewards_telescope_and_conserve_total_rank_utility(self):
        logits=torch.tensor([[0.]*24,list(range(24)),list(reversed(range(24)))],dtype=torch.float64)
        values=rank_probabilities(logits) @ UTILITY
        ranks=torch.tensor([2,0,3,1])
        rewards=hand_rewards(values,ranks)
        self.assertTrue(torch.allclose(rewards.sum(0),UTILITY[ranks]-values[0]))
        self.assertTrue(torch.allclose(rewards.sum(-1),torch.zeros(3,dtype=torch.float64),atol=1e-10))
        self.assertTrue(torch.allclose(rewards[-1],UTILITY[ranks]-values[-1]))

    def test_terminal_label_affects_reward_without_affecting_prediction(self):
        model=ScoreRankModel()
        prefix=torch.tensor([[0.,0.,0.,2.5,2.5,2.5,2.5]],dtype=torch.float64)
        values=rank_probabilities(model([prefix])) @ UTILITY
        good=hand_rewards(values,torch.tensor([0,1,2,3]))
        bad=hand_rewards(values,torch.tensor([3,1,2,0]))
        self.assertGreater(good[0,0].item(),bad[0,0].item())
        self.assertTrue(torch.equal(values,rank_probabilities(model([prefix])) @ UTILITY))


if __name__=="__main__": unittest.main()

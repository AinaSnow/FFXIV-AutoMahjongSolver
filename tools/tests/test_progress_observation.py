import copy
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "evaluation"))
from progress_observation import adapt_east_progress, apply_candidate_transform, EastProgressEngine, EAST_PROGRESS
from compare_models import paired_report


class Matrix(list):
    # Minimal row-addressable matrix keeps the logic gate independent of numpy/torch.
    shape = (1012, 34)
    def copy(self):
        return copy.deepcopy(self)


def sample(kyoku=0):
    obs = Matrix([[0.0] * 34 for _ in range(1012)])
    obs[19+kyoku] = [1.0]*34
    obs[25][27] = 1.0
    obs[26][30] = 1.0
    obs[27] = [kyoku/7]*34
    obs[0][4] = obs[4][0] = obs[29][5] = 1.0
    return obs


def engine():
    return SimpleNamespace(version=4, is_oracle=False, name="candidate",
        model_identity={"checkpoint_sha256": "same"}, react_batch=Mock(return_value="result"))


class ProgressObservationTests(unittest.TestCase):
    def test_only_progress_changes_for_each_east_hand_and_input_remains_intact(self):
        for kyoku in range(4):
            obs = sample(kyoku); before = copy.deepcopy(obs)
            adapted = adapt_east_progress(obs)
            self.assertEqual(before, obs)
            self.assertEqual([27], [i for i in range(1012) if adapted[i] != obs[i]])
            self.assertEqual([(kyoku+4)/7]*34, adapted[27])

    def test_unknown_layout_wind_kyoku_and_double_adaptation_fail_closed(self):
        bad = []
        obs = sample(); obs.shape = (934, 34); bad.append(obs)
        obs = sample(); obs[25][27] = 0; obs[25][28] = 1; bad.append(obs)
        obs = sample(); obs[20] = [1]*34; bad.append(obs)
        obs = sample(); obs[19][0] = .5; bad.append(obs)
        obs = sample(); obs[27][0] = float("nan"); bad.append(obs)
        bad.append(adapt_east_progress(sample()))
        for obs in bad:
            with self.assertRaises(ValueError): adapt_east_progress(obs)

    def test_mask_identity_hidden_information_and_base_engine_are_preserved(self):
        base = engine(); wrapped = EastProgressEngine(base)
        obs, masks = [sample()], [[True]*46]
        self.assertEqual("result", wrapped.react_batch(obs, masks, None))
        args = base.react_batch.call_args.args
        self.assertIs(masks, args[1]); self.assertIsNone(args[2])
        self.assertIsNot(obs[0], args[0][0]); self.assertEqual(1, wrapped.adapted_observations)
        self.assertEqual("candidate", wrapped.name)
        self.assertNotIn("observation_transform", base.model_identity)
        with self.assertRaises(ValueError): wrapped.react_batch(obs, masks, [sample()])
        for overrides in ({"version": 3}, {"is_oracle": True}):
            base = engine(); base.__dict__.update(overrides)
            with self.assertRaises(ValueError): EastProgressEngine(base)

    def test_ablation_requires_same_weights_and_quick_mode_and_only_wraps_candidate(self):
        engines = {role: engine() for role in ("candidate", "baseline", "opponent")}
        original = engines.copy()
        with self.assertRaises(ValueError): apply_candidate_transform(engines, EAST_PROGRESS, "doman-full")
        engines["candidate"].model_identity["checkpoint_sha256"] = "other"
        with self.assertRaises(ValueError): apply_candidate_transform(engines, EAST_PROGRESS, "doman-quick")
        engines["candidate"].model_identity["checkpoint_sha256"] = "same"
        apply_candidate_transform(engines, EAST_PROGRESS, "doman-quick")
        self.assertIs(original["candidate"], engines["candidate"].engine)
        self.assertIs(original["baseline"], engines["baseline"])
        self.assertIs(original["opponent"], engines["opponent"])

    def test_same_checkpoint_with_changed_features_is_not_same_policy_control(self):
        models = {role: engine().model_identity for role in ("candidate", "baseline", "opponent")}
        models["candidate"] = EastProgressEngine(engine()).model_identity
        matches = [dict(seat=i, rank=i+1, score=25000, hands=4, wins=0, deal_ins=0,
                        gameplay_sha256=str(i)) for i in range(4)]
        group = dict(candidate=copy.deepcopy(matches), baseline=matches)
        group["candidate"][0]["gameplay_sha256"] = "different"
        result = paired_report([group], "development", models, "doman-quick")
        self.assertFalse(result["same_model_control"])
        self.assertIsNone(result["control_passed"])
        self.assertFalse(result["doman_promotion_eligible"])
        self.assertEqual("mortal-observation-ablation", result["comparison"])
        self.assertTrue(any("distribution shift" in item for item in result["rule_differences"]))


if __name__ == "__main__":
    unittest.main()

import copy
import gzip
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

sys.path[:0] = [str(Path(__file__).resolve().parents[1]), str(Path(__file__).resolve().parents[1]/"evaluation")]
from compare_models import main, match_outcome, paired_report, read_results
import mortal_runner


class Outcome:
    def take_final_scores(self):
        return [40000, 30000, 20000, 10000]

    def take_rank_by_player(self):
        return [0, 1, 2, 3]


class ModelComparisonTests(unittest.TestCase):
    def test_multiple_winners_count_only_one_dealt_in_hand_and_tsumo_is_not_deal_in(self):
        events = [dict(type="start_kyoku"), dict(type="hora", actor=1, target=0),
                  dict(type="hora", actor=2, target=0), dict(type="end_kyoku"),
                  dict(type="start_kyoku"), dict(type="hora", actor=0, target=0), dict(type="end_kyoku")]
        result = match_outcome(events, 0, Outcome())
        self.assertEqual((2, 1, 1), (result["hands"], result["wins"], result["deal_ins"]))
        self.assertEqual((1, 40000), (result["rank"], result["score"]))
        with self.assertRaisesRegex(ValueError, "no completed hands"):
            match_outcome([], 0, Outcome())

    def test_wrong_seat_assignment_and_missing_log_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            for seat, letter in enumerate("abcd"):
                names = ["opponent"]*4
                names[seat] = "candidate"
                rows = [dict(type="start_game", seed=[1000200, 42], names=names),
                        dict(type="start_kyoku"), dict(type="end_kyoku"), dict(type="end_game")]
                with gzip.open(directory/f"1000200_42_{letter}.json.gz", "wt", encoding="utf-8") as sink:
                    sink.write("\n".join(map(json.dumps, rows)))
            grp = SimpleNamespace(load_log=lambda raw: Outcome())
            results = read_results(directory, 1000200, "candidate", grp)
            self.assertEqual([0, 1, 2, 3], [row["seat"] for row in results])
            self.assertEqual([1, 2, 3, 4], [row["rank"] for row in results])
            with self.assertRaisesRegex(ValueError, "seat assignment"):
                read_results(directory, 1000200, "baseline", grp)
            (directory/"1000200_42_a.json.gz").unlink()
            with self.assertRaisesRegex(ValueError, "exactly four"):
                read_results(directory, 1000200, "candidate", grp)

    def group(self):
        matches = [dict(seat=seat, rank=seat+1, score=40000-seat*10000,
                        hands=8, wins=2, deal_ins=1, gameplay_sha256=f"game-{seat}") for seat in range(4)]
        return dict(candidate=copy.deepcopy(matches), baseline=copy.deepcopy(matches))

    def test_same_model_control_detects_divergence_and_incomplete_pairs(self):
        models = {role: dict(checkpoint_sha256="same") for role in ("candidate", "baseline", "opponent")}
        group = self.group()
        result = paired_report([group], "development", models)
        self.assertTrue(result["control_passed"])
        self.assertIsNone(result["rank_delta_95_ci"])
        self.assertFalse(result["promotion_statistics_pass"])
        self.assertFalse(result["doman_promotion_eligible"])
        self.assertEqual([1, 1, 1, 1], result["candidate"]["placement_counts"])
        self.assertEqual(.5, result["candidate"]["top_two_rate"])
        group["candidate"][0]["gameplay_sha256"] = "different"
        with self.assertRaisesRegex(ValueError, "Identical-checkpoint"):
            paired_report([group], "development", models)
        group["candidate"].pop()
        with self.assertRaisesRegex(ValueError, "Incomplete paired"):
            paired_report([group], "development", models)

    def test_distinct_model_small_sample_never_promotes(self):
        models = {role: dict(checkpoint_sha256=role) for role in ("candidate", "baseline", "opponent")}
        group = self.group()
        for row in group["candidate"]:
            row["rank"] = 1
        result = paired_report([group], "acceptance", models)
        self.assertFalse(result["same_model_control"])
        self.assertFalse(result["promotion_statistics_pass"])
        self.assertEqual(-1.5, result["rank_delta"])
        self.assertEqual(1, result["candidate"]["top_two_rate"])
        self.assertIsNone(result["ffxiv_rating_gain"])

    def test_failed_second_pair_preserves_only_completed_group_and_marks_run_failed(self):
        calls = []
        class Arena:
            def __init__(self, **kwargs):
                self.directory = Path(kwargs["log_dir"])

            def py_vs_py(self, challenger, opponent, seed, count):
                calls.append((challenger.name, opponent, seed))
                if len(calls) == 3:
                    raise RuntimeError("injected engine failure")
                self.directory.mkdir(parents=True)
                for seat, letter in enumerate("abcd"):
                    names = [opponent.name]*4
                    names[seat] = challenger.name
                    rows = [dict(type="start_game", seed=list(seed), names=names),
                            dict(type="start_kyoku"), dict(type="end_kyoku"), dict(type="end_game")]
                    with gzip.open(self.directory/f"{seed[0]}_{seed[1]}_{letter}.json.gz", "wt", encoding="utf-8") as sink:
                        sink.write("\n".join(map(json.dumps, rows)))
                return [1, 1, 1, 1]
        torch = SimpleNamespace(set_num_threads=Mock(), manual_seed=Mock(), __version__="test",
                                backends=SimpleNamespace(cudnn=SimpleNamespace()))
        modules = {"torch": torch, "numpy": SimpleNamespace(random=SimpleNamespace(seed=Mock())),
                   "config": SimpleNamespace(config={"control": {"state_file": "live.pth"}}),
                   "libriichi": SimpleNamespace(), "libriichi.arena": SimpleNamespace(OneVsThree=Arena),
                   "libriichi.dataset": SimpleNamespace(Grp=SimpleNamespace(load_log=lambda raw: Outcome()))}
        def engine(**kwargs):
            return SimpleNamespace(name=kwargs["name"], device="cpu",
                                   model_identity=dict(checkpoint_sha256=str(kwargs["checkpoint_path"])))
        original_directory, original_path = Path.cwd(), sys.path[:]
        try:
            with tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                for name in ("engine.py", "model.py", "config.toml"):
                    (root/name).write_text("test", encoding="utf-8")
                output = root/"run"
                argv = ["compare_models.py", "--mortal-dir", str(root), "--candidate", "candidate.pth",
                        "--output", str(output), "--groups", "2"]
                with patch.dict(sys.modules, modules), patch.object(sys, "argv", argv), \
                     patch.object(mortal_runner, "create_engine", side_effect=engine), \
                     patch("compare_models.subprocess.check_output", return_value="commit"), patch("builtins.print"):
                    with self.assertRaisesRegex(RuntimeError, "injected"):
                        main()
                manifest = json.loads((output/"manifest.json").read_text(encoding="utf-8"))
                self.assertEqual("failed", manifest["status"])
                self.assertEqual(1, manifest["completed_groups"])
                self.assertEqual(1, len((output/"results.jsonl").read_text(encoding="utf-8").splitlines()))
                self.assertFalse((output/"report.json").exists())
                self.assertIs(calls[0][1], calls[1][1])
                self.assertEqual(calls[0][2], calls[1][2])
                self.assertEqual(["candidate", "baseline", "baseline"], [call[0] for call in calls])
                os.chdir(original_directory)
        finally:
            os.chdir(original_directory)
            sys.path[:] = original_path

    def test_checkpoint_overrides_keep_live_config_and_each_engine_identity(self):
        cfg = {"control": {"state_file": "live.pth", "device": "cuda:0", "enable_amp": True}}
        state = {"config": {"control": {"version": 4}, "resnet": {"num_blocks": 40, "conv_channels": 192}},
                 "mortal": {}, "current_dqn": {}}
        fake_torch = SimpleNamespace(device=lambda value: SimpleNamespace(type=value.split(":")[0]),
                                     cuda=SimpleNamespace(is_available=lambda: True))
        modules = {"prelude": SimpleNamespace(), "torch": fake_torch, "config": SimpleNamespace(config=cfg),
                   "model": SimpleNamespace(Brain=Mock(), DQN=Mock()),
                   "engine": SimpleNamespace(MortalEngine=lambda *args, **kwargs: SimpleNamespace(**kwargs))}
        def load(torch_module, path):
            return state, dict(checkpoint_sha256=str(path))
        original_identity = mortal_runner._model_identity
        try:
            with patch.dict(sys.modules, modules), patch.object(mortal_runner, "load_checkpoint", side_effect=load):
                live = mortal_runner.create_engine()
                candidate = mortal_runner.create_engine(checkpoint_path="candidate.pth", name="candidate", device_name="cpu", enable_amp=False)
            self.assertEqual("live.pth", cfg["control"]["state_file"])
            self.assertTrue(cfg["control"]["enable_amp"])
            self.assertEqual("live.pth", live.model_identity["checkpoint_sha256"])
            self.assertEqual("candidate.pth", candidate.model_identity["checkpoint_sha256"])
            self.assertTrue(live.enable_amp)
            self.assertFalse(candidate.enable_amp)
            self.assertFalse(candidate.is_oracle)
            self.assertEqual("candidate", candidate.name)
        finally:
            mortal_runner._model_identity = original_identity


if __name__ == "__main__":
    unittest.main()

import gzip
import json
from pathlib import Path
import sys
import tempfile
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "evaluation"))
from self_play import canonical_identity, inspect_log, seeds_for


class SelfPlayTests(unittest.TestCase):
    def test_seed_partitions_are_disjoint_and_bounded(self):
        ranges = [seeds_for(split, 0, 100000) for split in ("train", "development", "acceptance")]
        self.assertLess(ranges[0].stop, ranges[1].start)
        self.assertLess(ranges[1].stop, ranges[2].start)
        for args in (("train", 99999, 2), ("train", -1, 1), ("train", 0, 0)):
            with self.assertRaises(ValueError):
                seeds_for(*args)

    def test_same_game_with_different_model_names_and_timing_is_not_new_training_data(self):
        left = [dict(type="start_game", names=["A"]*4, seed=[1, 42]), dict(type="dahai", actor=0, pai="5mr", meta={"elapsed":1})]
        right = [dict(type="start_game", names=["B"]*4, seed=[2, 42]), dict(type="dahai", actor=0, pai="5mr", meta={"elapsed":20})]
        self.assertEqual(canonical_identity(left), canonical_identity(right))
        right[-1]["pai"] = "5m"
        self.assertNotEqual(canonical_identity(left), canonical_identity(right))

    def test_truncated_or_wrong_seed_game_cannot_be_marked_complete(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "game.gz"
            complete = [dict(type="start_game", seed=[100100,42]), dict(type="start_kyoku"), dict(type="end_kyoku"), dict(type="end_game")]
            def write(rows):
                with gzip.open(path, "wt", encoding="utf-8") as sink:
                    sink.write("\n".join(json.dumps(row) for row in rows))
            write(complete)
            self.assertEqual(4, len(inspect_log(path, 100100)[1]))
            with self.assertRaises(ValueError): inspect_log(path, 100101)
            write(complete[:-1])
            with self.assertRaises(ValueError): inspect_log(path, 100100)
            write([complete[0], complete[1], complete[-1]])
            with self.assertRaises(ValueError): inspect_log(path, 100100)

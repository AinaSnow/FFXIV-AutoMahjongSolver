import copy
import io
import json
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

sys.path[:0] = [str(Path(__file__).resolve().parents[1]), str(Path(__file__).resolve().parents[1] / "evaluation")]
from mortal_runner import east_progress_or_none, LiveObservationEngine, serve, EAST_PROGRESS, NATIVE
from progress_observation import adapt_east_progress
from test_progress_observation import sample


class Bot:
    def __init__(self, engine):
        self.engine = engine

    def react(self, event):
        self.engine.react_batch([sample(3)], [[True]], None)
        return json.dumps({"type": "none"})


def request(sequence, **extra):
    return json.dumps(dict(session="s", hand=1, sequence=sequence, event={"type": "tsumo", "can_act": True}, **extra))


class RunnerObservationTests(unittest.TestCase):
    def test_live_remap_matches_the_accepted_offline_transform(self):
        for kyoku in range(4):
            self.assertEqual(adapt_east_progress(sample(kyoku)), east_progress_or_none(sample(kyoku)))

    def test_non_east_or_unexpected_layout_falls_back_instead_of_raising(self):
        south = sample(0); south[25][27] = 0.0; south[25][28] = 1.0
        adapted_twice = adapt_east_progress(sample(1))
        for obs in (south, adapted_twice):
            self.assertIsNone(east_progress_or_none(obs))

    def test_engine_passes_native_inputs_through_untouched(self):
        inner = SimpleNamespace(react_batch=Mock(return_value="r"), name="mortal")
        engine = LiveObservationEngine(inner)
        obs = [sample(2)]
        engine.react_batch(obs, ["m"], None)
        self.assertIs(inner.react_batch.call_args[0][0], obs)
        self.assertEqual("mortal", engine.name)

    def test_engine_remaps_east_and_keeps_south_native(self):
        inner = SimpleNamespace(react_batch=Mock(return_value="r"))
        engine = LiveObservationEngine(inner); engine.mode = EAST_PROGRESS
        south = sample(0); south[25][27] = 0.0; south[25][28] = 1.0
        engine.react_batch([sample(3), south], ["a", "b"], None)
        sent = inner.react_batch.call_args[0][0]
        self.assertEqual([1.0]*34, sent[0][27])
        self.assertIs(south, sent[1])
        self.assertEqual((1, 1), (engine.adapted, engine.passthrough))

    def test_serve_applies_per_request_mode_and_rejects_unknown_modes(self):
        inner = SimpleNamespace(react_batch=Mock(return_value="r"))
        engine = LiveObservationEngine(inner)
        sink = io.StringIO()
        serve(Bot(engine), [request(1), request(2, observation=EAST_PROGRESS), request(3, observation="bogus")], sink, engine=engine)
        lines = [json.loads(line) for line in sink.getvalue().splitlines()]
        self.assertNotIn("observation", lines[0])
        self.assertEqual(EAST_PROGRESS, lines[1]["observation"])
        self.assertIn("unsupported observation mode", lines[2]["error"])
        self.assertEqual([3/7]*34, inner.react_batch.call_args_list[0][0][0][0][27])
        self.assertEqual([1.0]*34, inner.react_batch.call_args_list[1][0][0][0][27])

    def test_serve_without_live_engine_rejects_non_native_mode(self):
        sink = io.StringIO()
        serve(Bot(LiveObservationEngine(SimpleNamespace(react_batch=Mock()))), [request(1, observation=EAST_PROGRESS)], sink)
        self.assertIn("requires a live observation engine", json.loads(sink.getvalue())["error"])


if __name__ == "__main__":
    unittest.main()

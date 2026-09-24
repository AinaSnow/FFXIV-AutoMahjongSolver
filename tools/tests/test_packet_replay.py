import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from replay_mortal import Runner, replay_hand


class ReplayTests(unittest.TestCase):
    def test_recommendation_does_not_replace_recorded_actions(self):
        requests = []
        class Client:
            def send(self, request):
                requests.append(request)
                return dict(reaction=dict(type="dahai", pai="9m"))
        hand = dict(number=2, start_sequence=100, events=[
            dict(packet_sequence=101, event=dict(type="tsumo", actor=0, pai="1p")),
            dict(packet_sequence=102, event=dict(type="dahai", actor=0, pai="1m", tsumogiri=False))])
        result = replay_hand(hand, Client(), io.StringIO(), "offline")
        self.assertEqual(result["status"], "passed")
        self.assertEqual(requests[1]["event"]["pai"], "1m")
        self.assertFalse(requests[-1]["event"]["can_act"])
        self.assertEqual([r["sequence"] for r in requests], [1, 2, 3])

    def test_model_error_reports_exact_packet_and_stops_the_hand(self):
        class Client:
            def send(self, request):
                raise RuntimeError("bad event")
        result = replay_hand(dict(number=1, start_sequence=10, events=[
            dict(packet_sequence=12, event=dict(type="tsumo"))]), Client(), io.StringIO(), "test")
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["failed_packet"], 12)
        self.assertEqual(result["events"], 0)

    def test_subprocess_correlation_is_checked(self):
        script = "import sys,json; r=json.loads(sys.stdin.readline()); r['sequence']+=1; print(json.dumps(r),flush=True)"
        with tempfile.TemporaryDirectory() as tmp, open(Path(tmp)/"stderr.log", "w") as stderr:
            client = Runner([sys.executable, "-c", script], tmp, stderr, 3)
            try:
                with self.assertRaisesRegex(ValueError, "correlation"):
                    client.send(dict(session="test", hand=1, sequence=1, event=dict(type="none")))
            finally:
                client.close()
            self.assertIsNotNone(client.process.poll())

    def test_timeout_terminates_a_stuck_child(self):
        with tempfile.TemporaryDirectory() as tmp, open(Path(tmp)/"stderr.log", "w") as stderr:
            client = Runner([sys.executable, "-c", "import time; time.sleep(30)"], tmp, stderr, .05)
            try:
                with self.assertRaises(TimeoutError):
                    client.send(dict(session="test", hand=1, sequence=1, event=dict(type="none")))
            finally:
                client.close()
            self.assertIsNotNone(client.process.poll())


if __name__ == "__main__":
    unittest.main()

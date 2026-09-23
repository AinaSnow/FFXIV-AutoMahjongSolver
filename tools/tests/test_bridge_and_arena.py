import io
import json
from pathlib import Path
import sys
import unittest
import tempfile
sys.path[:0]=[str(Path(__file__).resolve().parents[1]),str(Path(__file__).resolve().parents[1]/"evaluation")]
from calibrate import fit
from mortal_runner import serve
from mjai_adapter import public_events
from arena import paired_interval, report, SPLITS

class Bot:
    def react(self,line):
        return '{"type":"none"}'

class BridgeTests(unittest.TestCase):
    def test_every_input_including_replay_is_acknowledged(self):
        requests=[dict(session="a",hand=1,sequence=i+1,event=dict(type="none",can_act=i==0)) for i in range(2)]
        sink=io.StringIO();serve(Bot(),[json.dumps(r) for r in requests],sink)
        responses=[json.loads(line) for line in sink.getvalue().splitlines()]
        self.assertEqual([r["sequence"] for r in responses],[1,2])
        self.assertIsNone(responses[1]["reaction"])
    def test_out_of_order_sequence_fails_closed(self):
        sink=io.StringIO();serve(Bot(),[json.dumps(dict(session="a",hand=1,sequence=2,event=dict(type="none")))],sink)
        self.assertIn("error",json.loads(sink.getvalue()))
    def test_only_our_concealed_tiles_cross_process_boundary(self):
        original=[dict(type="start_kyoku",oya=1,tehais=[[f"secret{i}"] for i in range(4)],scores=[1,2,3,4]),dict(type="tsumo",actor=0,pai="secret-draw")]
        for seat in range(4):
            visible=public_events(original,seat)
            self.assertEqual(visible[0]["tehais"][0],[f"secret{seat}"])
            self.assertTrue(all(t=="?" for h in visible[0]["tehais"][1:] for t in h))
            self.assertEqual(visible[0]["oya"],(1-seat)%4)
            if seat!=0: self.assertEqual(visible[1]["pai"],"?")
        self.assertEqual(original[0]["oya"],1)
    def test_intervals_cluster_by_seed_and_small_samples_cannot_promote(self):
        self.assertIsNone(paired_interval([0]))
        self.assertEqual(paired_interval([-0.5]*4,100),[-0.5,-0.5])
        group=dict(candidate=[dict(rank=1,score=40000)]*4,baseline=[dict(rank=4,score=10000)]*4)
        self.assertFalse(report([group],"acceptance")["promotion_statistics_pass"])
        self.assertEqual(len(set(SPLITS.values())),3)

class CalibrationTests(unittest.TestCase):
    def test_validation_and_acceptance_rows_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            p=Path(directory)/"rows.jsonl"
            p.write_text(json.dumps(dict(split="acceptance",seed=10000000,key="x",rank=1,won=1,deal_in=0,points=8000)))
            with self.assertRaises(ValueError): fit(p)
            p.write_text(json.dumps(dict(split="train",seed=100000,key="x",rank=2,won=1,deal_in=0,points=8000)))
            result=fit(p)
            self.assertEqual(result["Buckets"]["x"]["ExpectedRank"],2)
            self.assertEqual(len(result["DatasetSha256"]),64)


if __name__=="__main__": unittest.main()

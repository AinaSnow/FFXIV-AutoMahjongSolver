import hashlib
import io
import json
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from training_dataset import export_corpus, export_match, import_archive, read_manifest, safe_file, split_for
from mortal_runner import load_checkpoint, serve


class DatasetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.archive = self.root / "source"; (self.archive / "games").mkdir(parents=True)
        self.corpus = self.root / "corpus"
        self.state = dict(e="state", hand_id=9, revision=10, state_code=30, hand=[4, 13], hand_red=[True, False],
                          scores=[25000]*4, complete=True, our_seat=0, initial_dealer=0, player_name="PRIVATE")
        self.decision = dict(e="decision", hand_id=9, revision=10, kind="Discard", tile=4, is_red=True,
                             source="mortal", provenance={"policy_weights_sha256":"weights", "mortal_model":{"model":{"checkpoint_sha256":"model"}}})
        self.action = dict(e="action", action_id=1, hand_id=9, revision=10, kind="Discard", tile=4, is_red=True, result="Ok", why="PRIVATE")
        self.final = dict(e="state", hand_id=9, revision=99, state_code=27, hand=[], scores=[30000,27000,26000,17000], initial_dealer=0)
        self.write([self.state, self.decision, self.action, dict(e="action-outcome", action_id=1, status="state-changed"), self.final])
        (self.archive / "summary.json").write_text(json.dumps(dict(packet_write_failed=False, environment={"protocol_verified":False}, final_scores=[99999]*4)), encoding="utf-8")
        (self.archive / "managed-complete.json").write_text("{}", encoding="utf-8")
        (self.archive / "packets.ndjson").write_text('{"message_id":636,"payload_hex":"PRIVATE"}\n', encoding="utf-8")

    def write(self, rows):
        (self.archive / "games/hand.ndjson").write_text("\n".join(json.dumps(r) for r in rows)+"\n", encoding="utf-8")

    def imported(self):
        return import_archive(self.archive, self.corpus)

    def test_actions_and_results_stay_distinct_and_private_raw_is_not_exported(self):
        d=self.imported(); m=read_manifest(d)
        rows, _=export_match(d,m); row=rows[0]
        self.assertEqual(row["observation"]["hand_red"], [True,False])
        self.assertEqual(row["labels"]["final_scores"], self.final["scores"])
        self.assertEqual(row["labels"]["final_rank"],1)
        self.assertIsNone(row["labels"]["won"])
        self.assertIsNone(row["labels"]["optimal_action"])
        self.assertIsNone(row["labels"]["server_confirmed_action"])
        self.assertFalse(row["training_eligible"])
        self.assertIn("server_action_not_verified",row["quality_flags"])
        self.assertIn("protocol_not_fully_verified",row["quality_flags"])
        self.assertNotIn("PRIVATE",json.dumps(row))

    def test_entire_match_split_is_stable_and_duplicate_import_is_idempotent(self):
        d=self.imported(); m=read_manifest(d)
        self.assertEqual(d,self.imported())
        self.assertEqual(m["split"],split_for(m["match_id"]))
        shutil.copytree(d,self.corpus/"match-alias")
        report=export_corpus(self.corpus,self.root/"export")
        self.assertEqual(len(report["matches"]),1)
        self.assertEqual(sum(report["totals"].values()),1)

    def test_modified_split_is_rejected(self):
        d=self.imported(); m=read_manifest(d); m["split"]="wrong"
        (d/"manifest.json").write_text(json.dumps(m),encoding="utf-8")
        with self.assertRaisesRegex(ValueError,"Split changed"):read_manifest(d)

    def test_corruption_fails_before_publishing_splits(self):
        d=self.imported(); (d/"archive/games/hand.ndjson").write_text("tampered",encoding="utf-8")
        with self.assertRaises(ValueError):export_corpus(self.corpus,self.root/"export")
        self.assertFalse((self.root/"export/train.jsonl").exists())
        self.assertTrue((self.root/"export/rejected.json").exists())

    def test_missing_opening_or_final_is_unassigned_and_has_no_fabricated_result(self):
        self.write([self.state,self.decision,self.action])
        (self.archive/"packets.ndjson").write_text('{"message_id":635}\n',encoding="utf-8")
        d=self.imported(); m=read_manifest(d); rows,_=export_match(d,m)
        self.assertEqual(m["split"],"unassigned")
        self.assertIsNone(rows[0]["labels"]["final_scores"])
        self.assertIsNone(rows[0]["labels"]["final_rank"])

    def test_legacy_state_or_wrong_red_tile_does_not_bind_recommendation(self):
        self.state.pop("revision"); self.action["is_red"]=False
        self.write([self.state,self.decision,self.action,self.final])
        d=self.imported();rows,_=export_match(d,read_manifest(d))
        self.assertEqual(rows[0]["observation"],{})
        self.assertIsNone(rows[0]["recommendation"])
        self.assertIn("execution_outcome_missing_or_ambiguous",rows[0]["quality_flags"])

    def test_inferred_hand_end_cannot_be_used_as_match_end(self):
        self.write([self.state,self.decision,self.action,dict(e="hand-end",kind="ron",result_inferred=True,scores_after=self.final["scores"])])
        d=self.imported();rows,_=export_match(d,read_manifest(d))
        self.assertIsNone(rows[0]["labels"]["final_scores"])
        self.assertIsNone(rows[0]["labels"]["deal_in"])

    def test_incomplete_capture_is_preserved_but_flagged(self):
        raw=self.root/"raw.ndjson";raw.write_text('{"e":"capture-end","stream_complete":false}\n',encoding="utf-8")
        d=import_archive(self.archive,self.corpus,raw)
        self.assertEqual((d/"raw-capture.ndjson").read_bytes(),raw.read_bytes())
        self.assertIn("raw_capture_incomplete",read_manifest(d)["quality_flags"])

    def test_unknown_and_changed_files_cannot_bypass_inventory(self):
        d=self.imported(); (d/"unexpected.txt").write_text("data",encoding="utf-8")
        with self.assertRaisesRegex(ValueError,"Unlisted"):read_manifest(d)
        for name in ("../secret", "C:/secret", "/secret", "..\\secret"):
            with self.assertRaises(ValueError):safe_file(d,name)

    def test_ordinary_export_refuses_overwrite_or_empty_corpus(self):
        with self.assertRaises(ValueError):export_corpus(self.corpus,self.root/"empty")
        self.imported();export_corpus(self.corpus,self.root/"export")
        with self.assertRaises(FileExistsError):export_corpus(self.corpus,self.root/"export")

    def test_conflicting_duplicate_keeps_original_copy(self):
        d=self.imported(); before=(d/"archive/games/hand.ndjson").read_bytes()
        self.action["tile"]=12;self.write([self.state,self.action,self.final])
        with self.assertRaisesRegex(ValueError,"Conflicting duplicate"):self.imported()
        self.assertEqual((d/"archive/games/hand.ndjson").read_bytes(),before)


class ModelIdentityTests(unittest.TestCase):
    def test_hash_describes_exact_loaded_bytes_even_if_checkpoint_is_replaced(self):
        with tempfile.TemporaryDirectory() as directory:
            p=Path(directory)/"weights.pth";p.write_bytes(b"original model")
            class Torch:
                @staticmethod
                def load(stream,**kwargs):
                    p.write_bytes(b"replacement")
                    return stream.read()
            loaded,identity=load_checkpoint(Torch,p)
            self.assertEqual(loaded,b"original model")
            self.assertEqual(identity["checkpoint_sha256"],hashlib.sha256(loaded).hexdigest())

    def test_identity_is_sent_on_first_correlated_ack_only(self):
        class Bot:
            def react(self,line):return '{"type":"none"}'
        req=[dict(session="a",sequence=n,hand=1,event=dict(type="none")) for n in (1,2)]
        target=io.StringIO();serve(Bot(),map(json.dumps,req),target,{"checkpoint_sha256":"sha"})
        first,second=map(json.loads,target.getvalue().splitlines())
        self.assertEqual(first["model"]["checkpoint_sha256"],"sha")
        self.assertNotIn("model",second)


if __name__=="__main__":unittest.main()

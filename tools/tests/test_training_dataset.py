import hashlib
import io
import json
from pathlib import Path
import shutil
import struct
import sys
import tempfile
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from training_dataset import export_corpus, export_match, import_archive, read_manifest, safe_file, split_for, inventory
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

    def test_final_score_is_invalidated_by_later_hand_across_files(self):
        for event in (dict(e="hand-start"), dict(e="hand-incomplete"), dict(e="state", hand=[1, 2, 3])):
            with self.subTest(event=event):
                (self.archive / "games/next.ndjson").write_text(json.dumps(event), encoding="utf-8")
                destination = import_archive(self.archive, self.root / ("case-" + event["e"]))
                manifest = read_manifest(destination)
                self.assertFalse(manifest["final_result_observed"])
                self.assertEqual("unassigned", manifest["split"])
                rows, _ = export_match(destination, manifest)
                self.assertIsNone(rows[0]["labels"]["final_scores"])

    def test_later_real_settlement_replaces_intermediate_result(self):
        latest = dict(self.final, scores=[17000, 36000, 27000, 20000])
        self.write([self.state, self.action, self.final, dict(e="hand-start"), self.state, latest])
        destination = self.imported()
        rows, _ = export_match(destination, read_manifest(destination))
        self.assertEqual(latest["scores"], rows[0]["labels"]["final_scores"])
        self.assertEqual(4, rows[0]["labels"]["final_rank"])

    def test_model_at_close_flag_does_not_override_exact_decision_provenance(self):
        d = self.imported()
        for flag in ("mortal_model_unknown", "mortal_model_at_close_unknown"):
            m = read_manifest(d)
            m["quality_flags"].append(flag)
            rows, report = export_match(d, m)
            self.assertIn("mortal_model_at_close_unknown", report["quality_flags"])
            self.assertNotIn("mortal_model_unknown", rows[0]["quality_flags"])
            self.assertNotIn("mortal_model_at_close_unknown", rows[0]["quality_flags"])
            self.assertEqual(rows[0]["provenance"]["mortal_model"], self.decision["provenance"]["mortal_model"])

    def test_mortal_decision_without_model_remains_unknown_even_with_close_identity(self):
        self.decision["provenance"].pop("mortal_model")
        self.write([self.state, self.decision, self.action, self.final])
        d = self.imported()
        rows, _ = export_match(d, read_manifest(d))
        self.assertIn("mortal_model_unknown", rows[0]["quality_flags"])
        self.assertNotIn("mortal_model", rows[0]["provenance"])

    def test_mortal_decision_with_no_provenance_cannot_lose_model_unknown_flag(self):
        self.decision.pop("provenance")
        self.write([self.state, self.decision, self.action, self.final])
        d = self.imported()
        rows, _ = export_match(d, read_manifest(d))
        self.assertIn("mortal_model_unknown", rows[0]["quality_flags"])
        self.assertIn("decision_provenance_unknown", rows[0]["quality_flags"])

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

    def test_rank_needs_starting_dealer_only_for_our_score_tie(self):
        self.final.pop("initial_dealer")
        self.write([self.state, self.action, self.final])
        d=self.imported(); rows,_=export_match(d,read_manifest(d))
        self.assertEqual(rows[0]["labels"]["final_rank"],1)
        self.final["scores"]=[26000,26000,28000,20000]
        self.write([self.state,self.action,self.final])
        (self.archive/"packets.ndjson").write_text('{"message_id":636,"match":2}',encoding="utf-8")
        d=self.imported(); rows,_=export_match(d,read_manifest(d))
        self.assertIsNone(rows[0]["labels"]["final_rank"])

    def test_all_capture_segments_are_checked_and_old_dispatch_context_is_flagged(self):
        d=self.imported(); m=read_manifest(d)
        (d/"raw-capture.ndjson").write_text('{"e":"capture-end","stream_complete":true,"reason":"disabled"}',encoding="utf-8")
        (d/"raw-captures").mkdir()
        (d/"raw-captures/segment-002.ndjson").write_text('{"e":"capture-end","stream_complete":false}',encoding="utf-8")
        m["files"]=inventory(d)
        (d/"manifest.json").write_text(json.dumps(m),encoding="utf-8")
        rows,_=export_match(d,read_manifest(d))
        for flag in ("raw_capture_segmented","raw_capture_interrupted","raw_capture_incomplete","action_context_unverified"):
            self.assertIn(flag,rows[0]["quality_flags"])

    def test_complete_footer_does_not_hide_a_missing_main_capture(self):
        packet=dict(t="2026-09-24T11:50:43.5056310+00:00",opcode="0x0214",payload_hex="AABB",message_id=636)
        (self.archive/"packets.ndjson").write_text(json.dumps(packet),encoding="utf-8")
        raw=self.root/"raw.ndjson"
        raw.write_text('{"e":"capture-end","stream_complete":true}',encoding="utf-8")
        d=import_archive(self.archive,self.corpus,raw)
        rows,_=export_match(d,read_manifest(d))
        self.assertIn("raw_archive_packets_missing",rows[0]["quality_flags"])
        packet.update(e="raw-packet",t="2026-09-24T11:50:43.505631+00:00")
        raw.write_text(json.dumps(packet)+'\n'+ '{"e":"capture-end","stream_complete":true}',encoding="utf-8")
        d=import_archive(self.archive,self.root/"complete-corpus",raw)
        rows,_=export_match(d,read_manifest(d))
        self.assertNotIn("raw_archive_packets_missing",rows[0]["quality_flags"])


FIXTURE = Path(__file__).resolve().parents[2] / "tests/Mahjong.Plugin.Game.Tests/RegressionFixtures/20261116-758-hand-results.json"


class ServerLabelExportTests(unittest.TestCase):
    """A sanitized 7.58 self-ron hand exported with one dispatched discard of ours."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.archive = self.root / "source"; (self.archive / "games").mkdir(parents=True)
        hand = next(h for h in json.loads(FIXTURE.read_text(encoding="utf-8"))["hands"] if h["label"] == "self-ron")
        result_at = next(i for i, p in enumerate(hand["packets"]) if p["message_id"] == 640)
        packets = [dict(t=hand["packets"][0]["t"], opcode="0x020F", message_id=636, payload_hex="00" * 48)]
        packets += [{k: p[k] for k in ("t", "opcode", "message_id", "payload_hex")} for p in hand["packets"][:result_at + 1]]
        self.packets = packets
        ours = next(p for p in packets if p["message_id"] == 641 and p["payload_hex"].startswith(self.self_seat(packets)))
        physical = struct.unpack_from("<H", bytes.fromhex(ours["payload_hex"]), 12)[0]
        self.tile, self.red = physical >> 2, physical >> 2 in (4, 13, 22) and physical & 3 == 1
        dispatched = ours["t"][:20] + "0000000Z"  # same second, before the server packet's fraction
        state = dict(e="state", hand_id=9, revision=10, state_code=30, hand=[self.tile], hand_red=[self.red],
                     scores=[25000]*4, complete=True)
        final = dict(e="state", hand_id=9, revision=99, state_code=27, hand=[], scores=[30000, 27000, 26000, 17000], initial_dealer=0)
        self.rows = [state, dict(e="action", action_id=1, t=dispatched, hand_id=9, revision=10, kind="Discard",
                                 tile=self.tile, is_red=self.red, result="Ok", dispatch_context="before-input"),
                     dict(e="action-outcome", action_id=1, label="discard", status="state-changed"), final]
        (self.archive / "games/hand.ndjson").write_text("\n".join(json.dumps(r) for r in self.rows) + "\n", encoding="utf-8")
        (self.archive / "packets.ndjson").write_text("\n".join(json.dumps(p) for p in packets) + "\n", encoding="utf-8")
        (self.archive / "managed-complete.json").write_text("{}", encoding="utf-8")
        self.raw = self.root / "raw.ndjson"
        self.raw.write_text("\n".join([json.dumps(dict(p, e="raw-packet")) for p in packets]
                                      + ['{"e":"capture-end","stream_complete":true,"dropped":0,"rejected":0}']), encoding="utf-8")

    @staticmethod
    def self_seat(packets):
        start = next(p for p in packets if p["message_id"] == 637)
        seat = struct.unpack_from("<i", bytes.fromhex(start["payload_hex"]), 24)[0]
        return struct.pack("<i", seat).hex().upper()

    def export(self, verified):
        (self.archive / "summary.json").write_text(json.dumps(dict(packet_write_failed=False,
            environment={"protocol_verified": verified})), encoding="utf-8")
        d = import_archive(self.archive, self.root / f"corpus-{verified}", self.raw)
        return export_match(d, read_manifest(d))

    def test_labels_and_server_confirmation_are_filled_but_protocol_gate_stays_independent(self):
        rows, report = self.export(False)
        row = rows[0]
        self.assertTrue(row["labels"]["won"])
        self.assertFalse(row["labels"]["deal_in"])
        self.assertEqual("ron", row["labels"]["hand_result"]["outcome"])
        self.assertEqual([0], row["labels"]["hand_result"]["winners"])
        confirmation = row["labels"]["server_confirmed_action"]
        self.assertEqual("server-confirmed", confirmation["status"])
        self.assertEqual(dict(type="dahai", actor=0, tile=self.tile, red=self.red), {k: confirmation["server_event"][k] for k in ("type", "actor", "tile", "red")})
        self.assertNotIn("server_action_not_verified", row["quality_flags"])
        gates = row["eligibility"]
        self.assertFalse(gates.pop("protocol_verified"))
        self.assertTrue(all(gates.values()), gates)
        self.assertFalse(row["training_eligible"])
        hand = report["hand_rows"][0]
        self.assertTrue(hand["opponent_stats_eligible"])
        self.assertIn("protocol_not_fully_verified", hand["quality_flags"])

    def test_every_gate_including_protocol_is_required(self):
        rows, _ = self.export(True)
        self.assertTrue(rows[0]["training_eligible"])

    def test_wrong_tile_or_lost_packets_close_the_gates(self):
        self.rows[1]["tile"] = (self.tile + 1) % 34
        (self.archive / "games/hand.ndjson").write_text("\n".join(json.dumps(r) for r in self.rows) + "\n", encoding="utf-8")
        self.raw.write_text(self.raw.read_text(encoding="utf-8").replace('"dropped":0', '"dropped":3'), encoding="utf-8")
        rows, report = self.export(True)
        row = rows[0]
        self.assertEqual("server-action-mismatch", row["labels"]["server_confirmed_action"]["status"])
        self.assertFalse(row["eligibility"]["server_actions_consistent"])
        self.assertFalse(row["eligibility"]["no_packet_loss"])
        self.assertIn("raw_capture_packet_loss", row["quality_flags"])
        self.assertFalse(row["training_eligible"])
        self.assertFalse(report["hand_rows"][0]["opponent_stats_eligible"])

    def test_export_publishes_hands_file_and_eligibility_counts(self):
        self.export(False)
        report = export_corpus(self.root / "corpus-False", self.root / "export")
        self.assertEqual(0, report["training_eligible"])
        self.assertEqual(1, report["eligibility"]["eligible_except_protocol_gate"])
        self.assertEqual(1, report["hands"]["opponent_stats_eligible"])
        hands = (self.root / "export/hands.jsonl").read_text(encoding="utf-8").splitlines()
        self.assertEqual(1, len(hands))
        self.assertIn("hands", report["outputs"])


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

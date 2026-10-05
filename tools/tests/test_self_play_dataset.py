import copy
import importlib.util
import gzip
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/"evaluation"))
from self_play_dataset import hand_contexts, validate_outcome, validate_arrays, export_player_views
from verify_self_play import checked_path, verify_dataset
from doman_engine import DATASET_PATCH, DATASET_REVISION, BASE_COMMIT, PATCH, REVISION, sha256, verify_build, load_doman_dataset
from self_play import main, canonical_identity
from self_play_dataset import file_hash


def opening():
    return dict(type="start_kyoku", bakaze="E", kyoku=1, honba=0, kyotaku=0, oya=0, scores=[25000]*4)


class DatasetLogicTests(unittest.TestCase):
    def test_hand_labels_reconstruct_riichi_multiple_winners_and_score_continuity(self):
        rows = [opening(), dict(type="reach_accepted", actor=0),
            dict(type="hora", deltas=[-8000,9000,0,0]), dict(type="hora", deltas=[-1000,0,1000,0]),
            dict(type="end_kyoku"), dict(opening(), kyoku=2, scores=[15000,34000,26000,25000]),
            dict(type="ryukyoku", deltas=[0]*4), dict(type="end_kyoku")]
        hands, scores = hand_contexts(rows)
        self.assertEqual(2, len(hands)); self.assertEqual([15000,34000,26000,25000], scores)
        validate_outcome(scores, [4,1,2,3], scores)
        bad = copy.deepcopy(rows); bad[5]["scores"][0] += 100
        with self.assertRaisesRegex(ValueError, "previous result"): hand_contexts(bad)
        for broken in ([opening(), dict(type="end_kyoku")], [opening()], [],
                       [opening(), dict(type="hora"), dict(type="end_kyoku")]):
            with self.assertRaises(ValueError): hand_contexts(broken)

    def test_final_labels_validate_stick_award_ties_and_reject_wrong_rank(self):
        validate_outcome([24000,26000,25000,25000], [4,1,2,3], [24000,25000,25000,25000])
        validate_outcome([25000]*4, [1,2,3,4], [25000]*4)
        with self.assertRaises(ValueError): validate_outcome([25000]*4, [4,3,2,1], [25000]*4)
        with self.assertRaises(ValueError): validate_outcome([25000]*4, [1,2,3,4], [25001]*4)

    def test_untrusted_paths_hashes_and_old_dataset_build_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp); file=root/"x"; file.write_text("x")
            self.assertEqual(file, checked_path(root,"x",sha256(file)))
            with self.assertRaises(ValueError): checked_path(root,"../outside")
            with self.assertRaises(ValueError): checked_path(root,"x","wrong")
            library=root/"libriichi_doman.so"; library.write_bytes(b"not loaded")
            manifest=dict(status="validated",base_commit=BASE_COMMIT,rules_revision=REVISION,
                patch_sha256=sha256(PATCH),library_sha256=sha256(library))
            path=root/"build-manifest.json"; path.write_text(json.dumps(manifest))
            verify_build(root) # old arena remains valid for historical evaluation
            with self.assertRaisesRegex(ValueError,"dataset adapter"): load_doman_dataset(root)
            manifest.update(dataset_revision=DATASET_REVISION,dataset_patch_sha256=sha256(DATASET_PATCH))
            path.write_text(json.dumps(manifest)); verify_build(root)
            manifest["dataset_patch_sha256"]="wrong"; path.write_text(json.dumps(manifest))
            with self.assertRaises(ValueError): verify_build(root)

    def test_failed_generation_is_not_published_and_doman_loader_receives_mode(self):
        original_dir, original_path = Path.cwd(), sys.path[:]
        torch=SimpleNamespace(set_num_threads=Mock(),manual_seed=Mock(),__version__="test",
            backends=SimpleNamespace(cudnn=SimpleNamespace()))
        modules={"torch":torch,"numpy":SimpleNamespace(random=SimpleNamespace(seed=Mock()))}
        engine=SimpleNamespace(is_oracle=False,version=4,device="cpu",enable_quick_eval=True,
            enable_rule_based_agari_guard=True,model_identity={"architecture_version":4})
        factory=Mock(return_value=engine); loader=Mock()
        arena=Mock(return_value=SimpleNamespace(py_vs_py=Mock(side_effect=RuntimeError("injected"))))
        try:
            with tempfile.TemporaryDirectory() as temp:
                root=Path(temp)
                for name in ("engine.py","model.py","config.toml"): (root/name).write_text("test")
                output=root/"output"
                argv=["self_play.py","--mortal-dir",str(root),"--output",str(output),"--match-mode","doman-quick"]
                with patch.dict(sys.modules, dict(modules,mortal_runner=SimpleNamespace(create_engine=factory))), \
                     patch.object(sys,"argv",argv), patch("self_play.load_doman_dataset",return_value=(arena,loader,None,{"dataset_revision":1})), \
                     patch("self_play.subprocess.check_output",return_value="commit"):
                    with self.assertRaisesRegex(RuntimeError,"injected"): main()
                manifest=json.loads((output/"manifest.json").read_text())
                self.assertEqual("failed",manifest["status"])
                self.assertIsNone(manifest["training_glob"]); self.assertIsNone(manifest["perspective_glob"])
                self.assertEqual("doman-quick",loader.call_args.kwargs["match_mode"])
                self.assertFalse(loader.call_args.kwargs["oracle"])
                self.assertFalse(factory.call_args.kwargs["enable_amp"])
                os.chdir(original_dir)
        finally:
            os.chdir(original_dir); sys.path[:]=original_path


@unittest.skipUnless(importlib.util.find_spec("numpy"), "NumPy export tests require the offline evaluation runtime")
class DatasetArrayTests(unittest.TestCase):
    def arrays(self):
        import numpy as np
        return dict(obs=np.zeros((2,1012,34),dtype=np.float32), masks=np.ones((2,46),dtype=np.bool_),
            actions=np.array([1,2],dtype=np.int64),hand_index=np.array([0,1],dtype=np.int64),
            dones=np.array([True,True]),apply_gamma=np.array([True,True]))

    def test_illegal_hidden_nan_and_misaligned_inputs_are_rejected(self):
        import numpy as np
        arrays=self.arrays(); self.assertEqual(2,validate_arrays(arrays,4,2))
        bad=copy.deepcopy(arrays); bad["masks"][0,1]=False
        bad2=copy.deepcopy(arrays); bad2["obs"][0,0,0]=np.nan
        bad3=copy.deepcopy(arrays); bad3["invisible_obs"]=bad3["obs"]
        bad4=copy.deepcopy(arrays); bad4["dones"][0]=False
        bad5=copy.deepcopy(arrays); bad5["hand_index"][1]=2
        for value in (bad,bad2,bad3,bad4,bad5):
            with self.assertRaises(ValueError): validate_arrays(value,4,2)

    def test_empty_player_perspective_has_no_fabricated_decisions(self):
        arrays={key:value[:0] for key,value in self.arrays().items()}
        self.assertEqual(0,validate_arrays(arrays,4,1))

    def test_export_handles_native_u8_bytes_and_keeps_outcomes_in_separate_labels(self):
        arrays=self.arrays()
        def game(seat):
            return SimpleNamespace(take_player_id=lambda:seat,take_invisible_obs=lambda:[],
                take_obs=lambda:arrays["obs"],take_masks=lambda:arrays["masks"],
                take_actions=lambda:[1,2],take_at_kyoku=lambda:b"\x00\x01",
                take_dones=lambda:[True,True],take_apply_gamma=lambda:[True,True])
        loader=SimpleNamespace(load_log=lambda raw:[game(seat) for seat in range(4)])
        with tempfile.TemporaryDirectory() as temp:
            records=export_player_views(loader,"raw",Path(temp)/"players",dict(match_id="id"),
                [opening(),dict(opening(),kyoku=2)],[25000]*4,[1,2,3,4],4)
            self.assertEqual(4,len(records))
            self.assertTrue(all(row["player_decisions"]==2 for row in records))
            labels=json.loads((Path(temp)/"players/player-0.labels.json").read_text())
            self.assertEqual(1,labels["final_rank"])
            self.assertFalse(labels["labels_are_model_inputs"])
            self.assertEqual(["obs","masks"],labels["feature_fields"])

    def test_complete_inventory_rejects_reuse_tampering_and_extra_samples(self):
        import numpy as np
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp); seed=100500
            events=[dict(type="start_game",seed=[seed,42],names=["selfplay"]*4), opening(),
                dict(type="ryukyoku",deltas=[0]*4),dict(type="end_kyoku"),dict(type="end_game")]
            raw="\n".join(json.dumps(row) for row in events)
            identity=canonical_identity(events); hands,_=hand_contexts(events)
            arrays=self.arrays(); arrays["hand_index"][:]=0; arrays["dones"][0]=False
            def game(seat):
                return SimpleNamespace(take_player_id=lambda:seat,take_invisible_obs=lambda:[],
                    take_obs=lambda:arrays["obs"],take_masks=lambda:arrays["masks"],
                    take_actions=lambda:[1,2],take_at_kyoku=lambda:b"\x00\x00",
                    take_dones=lambda:[False,True],take_apply_gamma=lambda:[True,True])
            metadata=dict(match_id=identity,seed=seed,split="train",match_mode="doman-quick",scheduled_hands=4)
            views=export_player_views(SimpleNamespace(load_log=lambda raw:[game(i) for i in range(4)]),raw,
                root/"players"/identity,metadata,hands,[25000]*4,[1,2,3,4],4)
            for view in views:
                for field in ("path","labels_path"): view[field]=f"players/{identity}/{view[field]}"
            rows=[]; (root/"raw"/str(seed)).mkdir(parents=True); (root/"unique").mkdir()
            for i,letter in enumerate("abcd"):
                path=root/"raw"/str(seed)/f"{seed}_42_{letter}.json.gz"
                path.write_bytes(gzip.compress(raw.encode()))
                rows.append(dict(**metadata,path=path.relative_to(root).as_posix(),sha256=file_hash(path),
                    unique_path=f"unique/{identity}.json.gz",duplicate=i!=0,views=views if i==0 else [],
                    player_samples=8,hands=hands,final_scores=[25000]*4,ranks=[1,2,3,4]))
                if i==0: (root/"unique"/f"{identity}.json.gz").write_bytes(path.read_bytes())
            index=root/"matches.jsonl"; index.write_text("\n".join(json.dumps(row) for row in rows))
            manifest=dict(schema_version=2,status="complete",oracle=False,non_oracle_loader_verified=True,
                native_build=dict(dataset_revision=DATASET_REVISION),rules=dict(match_mode="doman-quick",rule_set="doman-experimental-v1"),seed_key=42,experimental_training_glob="unique/*.json.gz",
                model=dict(architecture_version=4),split="train",seed_start=seed,seed_end_exclusive=seed+1,
                completed_seed_groups=1,perspective_glob="players/*/*.npz",scheduled_hands=4,
                matches_sha256=file_hash(index),generated_matches=4,unique_matches=1,duplicate_matches=3,player_decisions=8)
            (root/"manifest.json").write_text(json.dumps(manifest))
            matches,seeds=set(),set()
            self.assertEqual(8,verify_dataset(root,matches,seeds)["player_decisions"])
            with self.assertRaisesRegex(ValueError,"Repeated seeds"): verify_dataset(root,matches,seeds)
            with self.assertRaisesRegex(ValueError,"Duplicate gameplay"): verify_dataset(root,matches,set())
            extra=root/"players"/identity/"unexpected.npz"; extra.write_bytes(b"unexpected")
            with self.assertRaisesRegex(ValueError,"Unindexed"): verify_dataset(root)
            extra.unlink()
            target=root/views[0]["path"]; target.write_bytes(target.read_bytes()+b"modified")
            with self.assertRaisesRegex(ValueError,"hash mismatch"): verify_dataset(root)

    def test_incomplete_dataset_and_heldout_training_marker_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            (root/"manifest.json").write_text(json.dumps(dict(schema_version=2,status="failed")))
            with self.assertRaisesRegex(ValueError,"completed"): verify_dataset(root)
            manifest=dict(schema_version=2,status="complete",oracle=False,non_oracle_loader_verified=True,
                rules=dict(match_mode="libriichi-hanchan",rule_set="libriichi"),seed_key=42,scheduled_hands=8,split="development",seed_start=1000000,
                seed_end_exclusive=1000001,completed_seed_groups=1,perspective_glob="players/*/*.npz")
            (root/"manifest.json").write_text(json.dumps(manifest))
            with self.assertRaisesRegex(ValueError,"split marker"): verify_dataset(root)


if __name__ == "__main__": unittest.main()

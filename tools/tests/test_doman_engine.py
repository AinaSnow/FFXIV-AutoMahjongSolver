import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/"evaluation"))
from doman_engine import BASE_COMMIT, PATCH, REVISION, load_doman, sha256, validate_match_events, verify_build
from rule_profile import rule_profile
from compare_models import markdown_report, paired_report


class DomanTests(unittest.TestCase):
    def test_standard_entry_remains_fail_closed(self):
        for mode in ("doman-quick", "doman-full"):
            with self.assertRaises(ValueError):
                rule_profile(mode)
        with self.assertRaisesRegex(ValueError, "requires --doman-engine"):
            load_doman(None)

    def test_explicit_experimental_profiles_do_not_claim_parity(self):
        for mode, rounds in (("doman-quick", 1), ("doman-full", 2)):
            rules = rule_profile(mode, allow_experimental=True)
            self.assertEqual(rounds, rules["scheduled_rounds"])
            self.assertFalse(rules["doman_compatible"])
            self.assertTrue(rules["rule_differences"])
        with self.assertRaises(ValueError):
            rule_profile("unknown", allow_experimental=True)

    def test_native_build_rejects_modified_binary_patch_revision_and_unfinished_tests(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            library = root / "libriichi_doman.so"
            library.write_bytes(b"fake-library-not-loaded")
            manifest = dict(status="validated", base_commit=BASE_COMMIT, rules_revision=REVISION,
                            library_sha256=sha256(library), patch_sha256=sha256(PATCH))
            path = root/"build-manifest.json"
            path.write_text(json.dumps(manifest), encoding="utf-8")
            self.assertEqual(verify_build(root)[1], manifest)
            for key in manifest:
                path.write_text(json.dumps({**manifest, key: "wrong"}), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "stale, untested or altered"):
                    verify_build(root)
            path.write_text(json.dumps(manifest), encoding="utf-8")
            library.write_bytes(b"changed")
            with self.assertRaises(ValueError):
                verify_build(root)

    def test_match_logs_reject_extension_but_allow_east_four_renchan(self):
        def hand(wind, n): return dict(type="start_kyoku", bakaze=wind, kyoku=n)
        rows = [hand("E", i) for i in range(1, 5)] + [hand("E", 4)]
        validate_match_events(rows, "doman-quick")
        for invalid in (rows+[hand("S", 1)], [hand("E", 2)], [hand("E", 1), hand("E", 5)], []):
            with self.assertRaises(ValueError): validate_match_events(invalid, "doman-quick")
        validate_match_events(rows + [hand("S", 1)], "doman-full")
        with self.assertRaises(ValueError): validate_match_events(rows + [hand("W", 1)], "doman-full")

    def test_quick_report_is_labelled_experimental_and_cannot_promote(self):
        matches = [dict(seat=i, rank=i+1, score=25000, hands=4, wins=1, deal_ins=0,
                        gameplay_sha256=str(i)) for i in range(4)]
        identities = {role: dict(checkpoint_sha256="same") for role in ("candidate", "baseline", "opponent")}
        result = paired_report([dict(seed=1, candidate=matches, baseline=matches)], "development", identities, "doman-quick")
        self.assertEqual(result["scheduled_rounds"], 1)
        self.assertFalse(result["doman_promotion_eligible"])
        self.assertTrue(result["control_passed"])
        self.assertIn("FF14 东风实验", markdown_report(result))
        self.assertNotIn("当前是标准 libriichi 半庄", markdown_report(result))


if __name__ == "__main__": unittest.main()

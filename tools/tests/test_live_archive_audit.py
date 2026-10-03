import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from audit_live_archives import run_audit


class ArchiveAuditTests(unittest.TestCase):
    def fixture(self, root):
        path = root / "match-test" / "summary.json"
        path.parent.mkdir()
        data = dict(schema_version=5, decision_count=10, failed_actions=0, timeout_fallbacks=1,
                    packet_write_failed=False, mortal=dict(decision_timeouts=1),
                    decision_health=dict(sources={"mortal": 7, "local-fallback": 1, "ui-recovery": 2},
                                         malformed_lines=0, io_failures=0),
                    execution_health=dict(outcomes={"state-changed": 9}, missing_outcomes=0))
        path.write_text(json.dumps(data), encoding="utf-8")
        return path, data

    def test_sources_are_separate_and_counterfactual_loss_remains_unknown(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path, _ = self.fixture(root)
            before = path.read_bytes()
            report = run_audit(root, root / "output")
            self.assertEqual(report["totals"]["mortal_coverage"], .7)
            self.assertEqual(report["totals"]["local_fallback_rate"], .1)
            self.assertIsNone(report["archives"][0]["estimated_rank_loss"])
            self.assertEqual(path.read_bytes(), before)
            with self.assertRaises(FileExistsError): run_audit(root, root / "output")

    def test_corrupt_and_legacy_summaries_cannot_silently_pass(self):
        for mode in ("corrupt", "legacy", "mismatched-total"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                path, data = self.fixture(root)
                if mode == "legacy": data["schema_version"] = 1
                if mode == "mismatched-total": data["decision_count"] = 11
                path.write_text("{" if mode == "corrupt" else json.dumps(data), encoding="utf-8")
                report = run_audit(root, root / "output")
                self.assertEqual(report["status"], "incomplete")
                self.assertEqual(report["totals"]["audited_archives"], 0)

    def test_v4_counters_are_supported_but_missing_final_result_is_unknown(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path, data = self.fixture(root)
            data["schema_version"] = 4
            path.write_text(json.dumps(data), encoding="utf-8")
            (root / "match-unfinished").mkdir()
            report = run_audit(root, root / "output")
            self.assertEqual(report["totals"]["selected_archives"], 2)
            self.assertEqual(report["totals"]["audited_archives"], 1)
            self.assertIsNone(report["archives"][0]["final_result_claimed"])
            self.assertEqual(len(report["errors"]), 1)
            self.assertEqual(report["status"], "incomplete")

    def test_empty_input_and_missing_captures_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(ValueError): run_audit(root, root / "output")
            self.fixture(root)
            with self.assertRaises(ValueError): run_audit(root, root / "output", captures=root)

    def test_capture_changed_between_inventory_and_decode_is_rejected(self):
        import subprocess
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root)
            (root / "capture-test.ndjson").write_text("{}", encoding="utf-8")
            def changed_export(command, **kwargs):
                Path(command[-1]).write_text(json.dumps(dict(source_sha256="changed")), encoding="utf-8")
                return subprocess.CompletedProcess(command, 0)
            with patch("audit_live_archives.subprocess.run", side_effect=changed_export):
                report = run_audit(root, root / "output", root, root / "profile", root / "decoder")
            self.assertEqual(report["captures"][0]["status"], "failed")
            self.assertIn("changed during audit", report["errors"][0]["reason"])

    def test_capture_timeout_is_reported_and_later_captures_are_attempted(self):
        import subprocess
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root)
            for i in range(2): (root / f"capture-{i}.ndjson").write_text("{}", encoding="utf-8")
            with patch("audit_live_archives.subprocess.run", side_effect=subprocess.TimeoutExpired("dotnet", 1)) as run:
                report = run_audit(root, root / "output", root, root / "profile", root / "decoder")
            self.assertEqual(run.call_count, 2)
            self.assertEqual(len(report["errors"]), 2)
            self.assertEqual(report["status"], "incomplete")

"""Unit tests for the unprivileged, filesystem-only SkillSpector bridge.

All fixtures are synthetic. No real scanner, network, secrets or malicious execution.
"""
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

SOURCE = Path(__file__).resolve().parents[1] / "workers" / "skillspector" / "worker.py"
spec = importlib.util.spec_from_file_location("packory_skillspector_worker", SOURCE)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class SkillSpectorBridgeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.input = Path(self.temp.name) / "in"
        self.output = Path(self.temp.name) / "out"
        self.input.mkdir()
        self.output.mkdir()
        self.original_input = worker.INPUT
        self.original_output = worker.OUTPUT
        worker.INPUT = self.input
        worker.OUTPUT = self.output
        self.addCleanup(self.restore)

    def restore(self):
        worker.INPUT = self.original_input
        worker.OUTPUT = self.original_output

    def job(self, data=b"synthetic archive content"):
        identity = "a" * 64
        digest = hashlib.sha256(data).hexdigest()
        (self.input / (identity + ".zip")).write_bytes(data)
        manifest = self.input / (identity + ".json")
        manifest.write_text(json.dumps({"id": identity, "sha256": digest}), encoding="utf-8")
        return manifest, identity, digest

    def test_completed_scan_outputs_matching_digest_and_offline_cli(self):
        manifest, identity, digest = self.job()
        report = json.dumps({"execution_successful": True,
                             "analysis_completeness": {"is_complete": True},
                             "risk_assessment": {"score": 0}})
        completed = subprocess.CompletedProcess(["skillspector"], 0, report, "")
        def fake_scan(_command, **kwargs):
            kwargs["stdout"].write(report.encode("utf-8"))
            return completed

        with mock.patch.object(worker.subprocess, "run", side_effect=fake_scan) as called:
            worker.process(manifest)
        payload = json.loads((self.output / (identity + ".json")).read_text())
        self.assertEqual(digest, payload["sha256"])
        self.assertEqual(0, payload["exit_code"])
        self.assertEqual(report and json.loads(report), payload["report"])
        cmd = called.call_args.args[0]
        self.assertIn("--no-llm", cmd)
        self.assertIn("--fail-on-incomplete", cmd)
        self.assertEqual("json", cmd[cmd.index("--format") + 1])

    def test_digest_mismatch_rejects_before_scanner(self):
        manifest, identity, digest = self.job()
        manifest.write_text(json.dumps({"id": identity, "sha256": "b" * 64}), encoding="utf-8")
        with mock.patch.object(worker.subprocess, "run") as called:
            worker.process(manifest)
        called.assert_not_called()
        payload = json.loads((self.output / (identity + ".json")).read_text())
        self.assertEqual(2, payload["exit_code"])


    def test_duplicate_manifest_fields_or_invalid_shape_never_scan(self):
        manifest, identity, digest = self.job()
        for content in (
            '{"id":"' + identity + '","sha256":"' + digest +
            '","sha256":"' + digest + '"}',
            '["not a queue object"]',
            '{"id":"' + identity + '","sha256":"' + digest + '","extra":true}',
            '{"id":"' + identity + '","sha256":"' + digest + '","extra":NaN}',
        ):
            with self.subTest(payload=content):
                (self.output / (identity + ".json")).unlink(missing_ok=True)
                manifest.write_text(content, encoding="utf-8")
                with mock.patch.object(worker.subprocess, "run") as called:
                    worker.process(manifest)
                called.assert_not_called()
                data = json.loads((self.output / (identity + ".json")).read_text())
                self.assertEqual(2, data["exit_code"])
                self.assertIsNone(data["report"])

    def test_duplicate_scanner_findings_and_oversized_stdout_fail_closed(self):
        manifest, identity, digest = self.job()
        for content in (
            b'{"execution_successful":true,"risk_assessment":{"score":0,"score":100}}',
            b'{"execution_successful":true,"risk_assessment":{"score":NaN}}',
            b"X" * (worker.MAX_REPORT + 1),
        ):
            with self.subTest(bytes=len(content)):
                (self.output / (identity + ".json")).unlink(missing_ok=True)

                def fake_scan(_command, **kwargs):
                    kwargs["stdout"].write(content)
                    return subprocess.CompletedProcess(["skillspector"], 0)

                with mock.patch.object(worker.subprocess, "run",
                                       side_effect=fake_scan):
                    worker.process(manifest)
                data = json.loads((self.output / (identity + ".json")).read_text())
                self.assertEqual(digest, data["sha256"])
                self.assertEqual(2, data["exit_code"])
                self.assertIsNone(data["report"])

    def test_timeout_is_a_failure_not_an_approval(self):
        manifest, identity, _ = self.job()
        with mock.patch.object(worker.subprocess, "run",
                               side_effect=subprocess.TimeoutExpired("skillspector", 1)):
            worker.process(manifest)
        payload = json.loads((self.output / (identity + ".json")).read_text())
        self.assertEqual(2, payload["exit_code"])
        self.assertIsNone(payload["report"])


if __name__ == "__main__":
    unittest.main()

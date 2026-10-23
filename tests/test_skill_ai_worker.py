"""Synthetic offline bridge tests. No vendor account or external inference is used."""
import hashlib
import importlib.util
import json
import pathlib
import sys
import tempfile
import time
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "workers" / "skillspector"))
import ai_worker as ai


class AiWorkerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name)
        self.input = self.root / "in"
        self.output = self.root / "out"
        self.input.mkdir()
        self.output.mkdir()
        self.patch = mock.patch.multiple(ai, INPUT=self.input, OUTPUT=self.output)
        self.patch.start()
        self.job_id = "a" * 32
        self.archive = b"synthetic bytes; scanner is mocked, no execution"
        self.job = {"id": self.job_id, "sha256": hashlib.sha256(self.archive).hexdigest(), "revision": 1,
                    "deadline": int(time.time() * 1000) + 60000, "provider": "openai", "model": "fixture",
                    "outputLanguage": "English", "maxPromptBytes": 32768, "maxOutputTokens": 512, "maxRequests": 4}
        self.manifest = self.input / (self.job_id + ".job.json")
        self.zip = self.input / (self.job_id + ".zip")
        self.result = self.output / (self.job_id + ".result.json")
        self.write()

    def tearDown(self):
        self.patch.stop()
        self.temp.cleanup()

    def write(self):
        self.manifest.write_text(json.dumps(self.job))
        self.zip.write_bytes(self.archive)

    def test_manifest_rejects_unknown_commands_endpoints_and_credentials(self):
        for field in ("command", "apiKey", "endpoint"):
            with self.subTest(field=field):
                self.write()
                body = dict(self.job, **{field: "untrusted"})
                self.manifest.write_text(json.dumps(body))
                with self.assertRaises(ValueError):
                    ai.validate_manifest(self.manifest)

    def test_duplicate_and_nonfinite_json_rejected(self):
        for raw in ('{"id":1,"id":2}', '{"x":NaN}', '{"x":Infinity}'):
            with self.assertRaises(ValueError):
                ai.decode_json(raw)

    def test_bounds_and_expiry(self):
        for field, value in (("deadline", 0), ("maxRequests", 65), ("maxPromptBytes", 200000),
                             ("maxOutputTokens", 20000), ("revision", True), ("provider", "codex_cli")):
            with self.subTest(field=field):
                original = self.job[field]
                self.job[field] = value
                self.write()
                with self.assertRaises(ValueError):
                    ai.validate_manifest(self.manifest)
                self.job[field] = original

    def test_digest_mismatch_never_invokes_scanner(self):
        self.zip.write_bytes(b"modified")
        with mock.patch.object(ai.importlib.metadata, "version", return_value=ai.SCANNER_VERSION), mock.patch.object(ai, "run_scanner") as run:
            ai.process(self.manifest)
        run.assert_not_called()
        self.assertEqual(2, json.loads(self.result.read_text())["exit_code"])

    def test_unsupported_scanner_version_fails_closed(self):
        with mock.patch.object(ai.importlib.metadata, "version", return_value="unknown"), mock.patch.object(ai, "run_scanner") as run:
            ai.process(self.manifest)
        run.assert_not_called()
        self.assertIsNone(json.loads(self.result.read_text())["report"])

    def test_symlink_and_oversized_exchange_are_rejected(self):
        path = self.root / "link"
        path.symlink_to(self.manifest)
        with self.assertRaises(ValueError):
            ai.bounded_read(path, 4096)
        with self.assertRaises(ValueError):
            ai.bounded_read(self.manifest, 2)

    def test_invalid_manifest_name_does_not_write_anything(self):
        ai.process(self.input / "../outside.job.json")
        self.assertFalse(list(self.output.iterdir()))

    def test_completed_synthetic_result_is_bound_to_digest_revision_and_pin(self):
        class FakeRelay:
            def __init__(self, *_): pass
            def __enter__(self): return self
            def __exit__(self, *_): pass
            def serve_forever(self): pass
            def shutdown(self): pass
        with mock.patch.object(ai.importlib.metadata, "version", return_value=ai.SCANNER_VERSION), \
                mock.patch.object(ai, "Relay", FakeRelay), \
                mock.patch.object(ai, "run_scanner", return_value=(0, {"synthetic": True})):
            ai.process(self.manifest)
        result = json.loads(self.result.read_text())
        self.assertEqual(self.job["sha256"], result["sha256"])
        self.assertEqual(1, result["revision"])
        self.assertEqual(ai.SCANNER_VERSION, result["scanner_version"])
        self.assertEqual({"synthetic": True}, result["report"])

    def test_cancelled_manifest_does_not_publish_late_result(self):
        class FakeRelay:
            def __init__(self, *_): pass
            def __enter__(self): return self
            def __exit__(self, *_): pass
            def serve_forever(self): pass
            def shutdown(self): pass
        def run(*_):
            self.manifest.unlink()
            return 0, {"late": True}
        with mock.patch.object(ai.importlib.metadata, "version", return_value=ai.SCANNER_VERSION), \
                mock.patch.object(ai, "Relay", FakeRelay), mock.patch.object(ai, "run_scanner", side_effect=run):
            ai.process(self.manifest)
        self.assertFalse(self.result.exists())


if __name__ == "__main__":
    unittest.main()

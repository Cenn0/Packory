"""Adversarial tests for the self-contained, source-only CI evidence verifier."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts/verify_source_evidence.py"
SPEC = importlib.util.spec_from_file_location("verify_source_evidence", SCRIPT)
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)
SHA = "a" * 40


class SourceEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.payload = b"synthetic public-only source snapshot\n"
        (self.root / "source.tar.gz").write_bytes(self.payload)
        self.record = {
            "schema": "packory.source-evidence/1",
            "sourceSha": SHA,
            "runId": 42,
            "runAttempt": 1,
            "qualification": "source-only",
            "productionRecommended": False,
            "archive": {
                "name": "source.tar.gz",
                "sha256": hashlib.sha256(self.payload).hexdigest(),
                "bytes": len(self.payload),
            },
        }
        self.save()

    def save(self):
        (self.root / "manifest.json").write_text(
            json.dumps(self.record, separators=(",", ":")), encoding="utf-8")

    def assert_rejected(self):
        with self.assertRaises(VERIFIER.Rejected):
            VERIFIER.validate(self.root, SHA)

    def test_valid_source_only_scope(self):
        actual = VERIFIER.validate(self.root, SHA)
        self.assertEqual(actual["qualification"], "source-only")
        self.assertIs(actual["productionRecommended"], False)

    def test_rejects_altered_source_bytes(self):
        (self.root / "source.tar.gz").write_bytes(b"x" * len(self.payload))
        self.assert_rejected()

    def test_rejects_archive_symlink(self):
        other = self.root / "other.tar.gz"
        other.write_bytes(self.payload)
        (self.root / "source.tar.gz").unlink()
        (self.root / "source.tar.gz").symlink_to(other)
        self.assert_rejected()

    def test_rejects_boolean_numeric_identity(self):
        self.record["runId"] = True
        self.save()
        self.assert_rejected()

    def test_rejects_duplicate_json_keys(self):
        path = self.root / "manifest.json"
        path.write_text(path.read_text(encoding="utf-8").replace(
            '"sourceSha":', '"sourceSha":"b", "sourceSha":'), encoding="utf-8")
        self.assert_rejected()

    def test_rejects_invalid_archive_size_and_name(self):
        for key, value in (("bytes", -1), ("name", "../private.txt")):
            with self.subTest(key=key):
                original = self.record["archive"][key]
                self.record["archive"][key] = value
                self.save()
                self.assert_rejected()
                self.record["archive"][key] = original

    def test_rejects_new_or_missing_fields(self):
        self.record["unexpected"] = "not allowed"
        self.save()
        self.assert_rejected()

    def test_rejects_prod_or_unqualified_claims(self):
        for key, value in (("productionRecommended", True), ("qualification", "production")):
            with self.subTest(key=key):
                self.record[key] = value
                self.save()
                self.assert_rejected()
                self.record[key] = False if key == "productionRecommended" else "source-only"

    def test_rejects_wrong_commit(self):
        with self.assertRaises(VERIFIER.Rejected):
            VERIFIER.validate(self.root, "b" * 40)


if __name__ == "__main__":
    unittest.main()

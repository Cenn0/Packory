"""Offline pilot readback regressions; fixtures never run a skill or product."""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "development_resource_check", ROOT / "scripts/check_development_resources.py")
checker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(checker)


class DevelopmentResourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "checkout"
        self.root.mkdir()
        records = {}
        for destination, (kind, name, files) in checker.EXPECTED.items():
            hashes = {}
            for relative in files:
                path = self.root / destination / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                raw = ("synthetic fixture: " + relative + "\n").encode()
                path.write_bytes(raw)
                hashes[relative] = hashlib.sha256(raw).hexdigest()
            records[destination] = {"kind": kind, "name": name, "files": hashes}
        self.value = {"schema_version": 1, "records": records,
                      "provenance": {"git_dirty": False,
                                     "git_revision": checker.SOURCE_REVISION,
                                     "selected_source_sha256": "1" * 64}}
        # A fixture's reviewed fingerprint is injected only by the unit test.
        # The command-line checker offers no pin override or repair mode.
        pin = mock.patch.object(checker, "MANIFEST_SHA256", "")
        pin.start()
        self.addCleanup(pin.stop)
        self.write_manifest()
        self.sample = self.root / ".agents/policy/evidence.md"

    def write_raw(self, raw, update_test_pin=True):
        (self.root / checker.MANIFEST).write_bytes(raw)
        if update_test_pin:
            checker.MANIFEST_SHA256 = hashlib.sha256(raw).hexdigest()

    def write_manifest(self, update_test_pin=True):
        self.write_raw((json.dumps(self.value, sort_keys=True, indent=2) + "\n").encode(),
                       update_test_pin)

    def fails(self, code):
        with self.assertRaises(checker.CheckFailure) as caught:
            checker.check(self.root)
        self.assertEqual(code, str(caught.exception))
        self.assertNotIn(str(self.root), str(caught.exception))

    def test_exact_synthetic_installation_has_readback_only_result(self):
        first = checker.check(self.root)
        second = checker.check(self.root)
        self.assertEqual(first, second)
        self.assertEqual(("VERIFIED", 7, 21),
                         (first["status"], first["resources"], first["files"]))
        self.assertEqual("NOT_RUN", first["agent_load"])
        self.assertEqual("NOT_RUN", first["runtime_smoke"])
        self.assertEqual("NOT_ESTABLISHED", first["publisher_authentication"])

    def test_changed_file_fails_without_repair(self):
        changed = b"synthetic changed bytes"
        self.sample.write_bytes(changed)
        self.fails("RESOURCE_HASH_DRIFT")
        self.assertEqual(changed, self.sample.read_bytes())

    def test_missing_or_unexpected_file_is_drift(self):
        original = self.sample.read_bytes()
        self.sample.unlink()
        self.fails("RESOURCE_TREE_DRIFT")
        self.sample.write_bytes(original)
        (self.sample.parent / "extra.txt").write_text("synthetic")
        self.fails("RESOURCE_TREE_DRIFT")

    def test_manifest_and_payload_rewrite_cannot_replace_reviewed_pin(self):
        self.sample.write_bytes(b"synthetic replacement")
        self.value["records"][".agents/policy"]["files"]["evidence.md"] = hashlib.sha256(
            self.sample.read_bytes()).hexdigest()
        self.write_manifest(update_test_pin=False)
        self.fails("MANIFEST_PIN_MISMATCH")

    def test_wrong_revision_and_dirty_source_are_rejected(self):
        self.value["provenance"]["git_revision"] = "0" * 40
        self.write_manifest()
        self.fails("INVALID_PROVENANCE")
        self.value["provenance"]["git_revision"] = checker.SOURCE_REVISION
        self.value["provenance"]["git_dirty"] = True
        self.write_manifest()
        self.fails("INVALID_PROVENANCE")

    def test_strict_manifest_types_and_unknown_fields(self):
        original = json.loads(json.dumps(self.value))
        for field, value in (("schema_version", True), ("records", []),
                             ("provenance", []), ("extra", "untrusted")):
            with self.subTest(field=field):
                self.value = json.loads(json.dumps(original))
                self.value[field] = value
                self.write_manifest()
                with self.assertRaises(checker.CheckFailure):
                    checker.check(self.root)

    def test_duplicate_json_keys_and_invalid_numbers_are_rejected(self):
        self.write_raw(b'{"schema_version":1,"schema_version":1}')
        self.fails("INVALID_MANIFEST")
        self.write_raw(b'{"schema_version":NaN}')
        self.fails("INVALID_MANIFEST")

    def test_invalid_utf8_and_excessive_json_depth_are_rejected(self):
        self.write_raw(bytes([255]))
        self.fails("INVALID_MANIFEST")
        self.write_raw(b"[" * 1200 + b"]" * 1200)
        self.fails("INVALID_MANIFEST")

    def test_manifest_cannot_add_or_traverse_paths(self):
        record = self.value["records"][".agents/policy"]
        record["files"]["../../outside.txt"] = "0" * 64
        self.write_manifest()
        self.fails("INVALID_RESOURCE_RECORD")
        del record["files"]["../../outside.txt"]
        self.value["records"]["../outside"] = record
        self.write_manifest()
        self.fails("UNEXPECTED_MANIFEST_SCOPE")

    def test_wrong_resource_kind_name_or_digest_is_rejected(self):
        original = json.loads(json.dumps(self.value))
        for field, value in (("kind", "agent"), ("name", "other-policy"),
                             ("files", {"evidence.md": "not-a-digest"})):
            with self.subTest(field=field):
                self.value = json.loads(json.dumps(original))
                self.value["records"][".agents/policy"][field] = value
                self.write_manifest()
                self.fails("INVALID_RESOURCE_RECORD")

    def test_manifest_byte_budget(self):
        self.write_raw(b" " * (checker.MAX_MANIFEST_BYTES + 1))
        self.fails("BYTE_BUDGET_EXCEEDED")

    def test_individual_resource_byte_budget(self):
        with self.sample.open("wb") as stream:
            stream.truncate(checker.MAX_FILE_BYTES + 1)
        self.fails("BYTE_BUDGET_EXCEEDED")

    def test_combined_resource_byte_budget(self):
        raw = b"x" * 100001
        digest = hashlib.sha256(raw).hexdigest()
        for destination, record in self.value["records"].items():
            for relative in record["files"]:
                (self.root / destination / relative).write_bytes(raw)
                record["files"][relative] = digest
        self.write_manifest()
        self.fails("BYTE_BUDGET_EXCEEDED")

    def test_directory_entry_budget_is_enforced_before_unbounded_enumeration(self):
        for number in range(checker.MAX_ENTRIES + 1):
            (self.root / ".agents" / ("extra-" + str(number))).write_bytes(b"")
        self.fails("ENTRY_BUDGET_EXCEEDED")

    def test_file_symlink_and_manifest_symlink_are_rejected(self):
        outside = Path(self.temp.name) / "outside.txt"
        outside.write_text("synthetic private marker")
        self.sample.unlink()
        self.sample.symlink_to(outside)
        self.fails("UNSAFE_FILE_TYPE")
        manifest = self.root / checker.MANIFEST
        saved = Path(self.temp.name) / "saved-manifest"
        manifest.rename(saved)
        manifest.symlink_to(saved)
        self.fails("UNSAFE_FILE_TYPE")

    def test_directory_symlink_and_root_symlink_are_rejected(self):
        directory = self.root / ".agents/skills"
        saved = Path(self.temp.name) / "saved-skills"
        directory.rename(saved)
        directory.symlink_to(saved, target_is_directory=True)
        self.fails("UNSAFE_DIRECTORY_TYPE")
        alias = Path(self.temp.name) / "checkout-link"
        alias.symlink_to(self.root, target_is_directory=True)
        with self.assertRaises(checker.CheckFailure):
            checker.check(alias)

    def test_hardlink_and_fifo_are_rejected_before_reading(self):
        outside = Path(self.temp.name) / "outside.txt"
        outside.write_text("synthetic shared file")
        self.sample.unlink()
        os.link(outside, self.sample)
        self.fails("UNSAFE_FILE_TYPE")
        self.sample.unlink()
        os.mkfifo(self.sample)
        self.fails("UNSAFE_FILE_TYPE")

    def test_replacement_during_read_is_detected_even_with_identical_bytes(self):
        original = os.read
        expected_inode = self.sample.stat().st_ino
        replaced = False

        def replace_after_read(descriptor, length):
            nonlocal replaced
            data = original(descriptor, length)
            if not replaced and os.fstat(descriptor).st_ino == expected_inode:
                replaced = True
                self.sample.unlink()
                self.sample.write_bytes(data)
            return data

        with mock.patch.object(checker.os, "read", side_effect=replace_after_read):
            self.fails("RESOURCE_CHANGED_DURING_READ")
        self.assertTrue(replaced)

    def test_unsupported_safe_primitives_fail_closed(self):
        with mock.patch.object(checker.os, "supports_dir_fd", set()):
            self.fails("UNSUPPORTED_SAFE_READ_PRIMITIVES")

    def test_diagnostic_never_echoes_file_contents_or_untrusted_names(self):
        marker = "synthetic" + "-private-marker"
        self.sample.write_text(marker)
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            result = checker.main(["--root", str(self.root)])
        self.assertEqual(1, result)
        self.assertNotIn(marker, output.getvalue())
        self.assertNotIn(str(self.root), output.getvalue())
        self.assertEqual("RESOURCE_HASH_DRIFT", json.loads(output.getvalue())["reason"])


class CheckedInPilotTests(unittest.TestCase):
    def test_committed_pilot_matches_its_reviewed_fingerprint(self):
        result = checker.check(ROOT)
        self.assertEqual("VERIFIED", result["status"])
        self.assertEqual(21, result["files"])


if __name__ == "__main__":
    unittest.main()

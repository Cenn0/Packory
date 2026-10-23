"""Synthetic contract tests only; no host, registry or secret access."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "single_node", Path(__file__).resolve().parents[1] / "scripts" /
    "single_node_acceptance.py")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class SingleNodeEvidenceTests(unittest.TestCase):
    def qualified_fixture(self):
        evidence = mod.template()
        evidence["source"]["sha"] = "b" * 40
        evidence["source"]["image_revisions"] = {"app": "b" * 40, "scanner": "b" * 40}
        evidence["source"]["image_digests"] = {"app": "sha256:" + "a" * 64,
                                                "scanner": "sha256:" + "c" * 64}
        evidence["clients"] = {name: "1.2.3" for name in mod.CLIENTS}
        evidence["boot_scope"] = "independent_daemon_restart_equivalent"
        evidence["measurements"] = {name: 4 for name in mod.METRICS}
        evidence["cases"] = [{"id": name, "status": "passed", "reason": None} for name in mod.CASES]
        return evidence

    def test_unexecuted_template_cannot_pass_validation(self):
        with self.assertRaises(mod.EvidenceError):
            mod.validate(mod.template())

    def test_complete_synthetic_fixture_is_structurally_accepted_only(self):
        counts, complete = mod.validate(self.qualified_fixture())
        self.assertTrue(complete)
        self.assertEqual(len(mod.CASES), counts["passed"])

    def test_skipped_failed_and_not_run_are_distinct_and_block_release(self):
        for state, reason in (("failed", "assertion_failed"),
                              ("skipped", "operator_not_authorized"),
                              ("not_run", "not_executed")):
            fixture = self.qualified_fixture()
            fixture["cases"][0] = {"id": fixture["cases"][0]["id"], "status": state,
                                   "reason": reason}
            counts, complete = mod.validate(fixture)
            self.assertFalse(complete)
            self.assertEqual(1, counts[state])

    def test_rejects_status_reason_mismatches(self):
        for status, reason in (
            ("failed", "not_executed"),
            ("failed", "missing_tool"),
            ("skipped", "assertion_failed"),
            ("skipped", "not_executed"),
            ("not_run", "timeout"),
            ("not_run", "operator_not_authorized"),
        ):
            with self.subTest(status=status, reason=reason):
                fixture = self.qualified_fixture()
                fixture["cases"][0] = {
                    "id": fixture["cases"][0]["id"],
                    "status": status, "reason": reason,
                }
                with self.assertRaises(mod.EvidenceError):
                    mod.validate(fixture)

    def test_accepted_reasons_still_block_completion(self):
        for status, reason in (
            ("failed", "assertion_failed"),
            ("failed", "timeout"),
            ("skipped", "missing_tool"),
            ("skipped", "unavailable_provider"),
            ("skipped", "unsafe_to_run"),
            ("skipped", "operator_not_authorized"),
            ("not_run", "not_executed"),
        ):
            with self.subTest(status=status, reason=reason):
                fixture = self.qualified_fixture()
                fixture["cases"][0] = {
                    "id": fixture["cases"][0]["id"],
                    "status": status, "reason": reason,
                }
                counts, complete = mod.validate(fixture)
                self.assertFalse(complete)
                self.assertEqual(1, counts[status])

    def test_rejects_identical_image_digests(self):
        fixture = self.qualified_fixture()
        fixture["source"]["image_digests"]["scanner"] = fixture["source"]["image_digests"]["app"]
        with self.assertRaises(mod.EvidenceError):
            mod.validate(fixture)

    def test_rejects_unknown_fields_and_duplicate_case(self):
        fixture = self.qualified_fixture()
        fixture["operator_email"] = "redacted"
        with self.assertRaises(mod.EvidenceError):
            mod.validate(fixture)
        fixture.pop("operator_email")
        fixture["cases"][1] = fixture["cases"][0].copy()
        with self.assertRaises(mod.EvidenceError):
            mod.validate(fixture)

    def test_rejects_missing_revision_or_image_integrity(self):
        for field, value in (("sha", "0" * 40), ("sha", "not-a-sha")):
            fixture = self.qualified_fixture()
            fixture["source"][field] = value
            with self.assertRaises(mod.EvidenceError):
                mod.validate(fixture)
        fixture = self.qualified_fixture()
        fixture["source"]["image_digests"]["scanner"] = "main"
        with self.assertRaises(mod.EvidenceError):
            mod.validate(fixture)

    def test_rejects_false_measurement_claims_and_unscoped_reboot(self):
        fixture = self.qualified_fixture()
        fixture["measurements"]["concurrent_cpu_percent"] = None
        with self.assertRaises(mod.EvidenceError):
            mod.validate(fixture)
        fixture = self.qualified_fixture()
        fixture["boot_scope"] = "not_tested"
        with self.assertRaises(mod.EvidenceError):
            mod.validate(fixture)
        fixture = self.qualified_fixture()
        fixture["workload"]["duration_seconds"] = True
        with self.assertRaises(mod.EvidenceError):
            mod.validate(fixture)

    def test_rejects_duplicate_json_keys_and_oversized_input(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "acceptance.json"
            path.write_text('{"same":1,"same":2}', encoding="utf-8")
            with self.assertRaises(mod.EvidenceError):
                mod.read(path)
            path.write_bytes(b"x" * 65537)
            with self.assertRaises(mod.EvidenceError):
                mod.read(path)

    def test_error_messages_never_reveal_input_paths_or_values(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "synthetic-hidden-file"
            try:
                mod.read(path)
            except mod.EvidenceError as error:
                self.assertNotIn(str(path), str(error))
            else:
                self.fail("Missing file was not rejected")


if __name__ == "__main__":
    unittest.main()

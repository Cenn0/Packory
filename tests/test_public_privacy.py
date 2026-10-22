"""Security scanner must reject private addresses and credentials without echoing them."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "privacy_guard", Path(__file__).resolve().parents[1] / "scripts" / "check_public_privacy.py"
)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class PrivacyGuardTests(unittest.TestCase):
    def test_contact_email_redacted(self):
        personal = "worker" + "@" + "private.example"
        detected = mod.scan_text("synthetic.txt", "contact=" + personal)
        self.assertEqual(1, len(detected))
        self.assertNotIn(personal, detected[0])

    def test_noreply_and_placeholder_allowed(self):
        self.assertEqual([], mod.scan_text("x.md",
            "contact=1234+account@users.noreply.github.com\n"
            "example=person@example.test\n"
            "service=noreply@github.com"))

    def test_credential_categories_never_echo_secrets(self):
        key = "AKIA" + "A" * 16
        problems = mod.scan_text("synthetic.txt", "value=" + key)
        self.assertEqual(1, len(problems))
        self.assertNotIn(key, problems[0])

    def test_rejects_sensitive_tracked_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in (".env", "production.key", "secrets.p12", ".npmrc"):
                (root / name).write_text("synthetic")
            problems = mod.check_paths(root, [".env", "production.key", "secrets.p12", ".npmrc"])
            self.assertEqual(4, len(problems))

    def test_native_node_module_sources_are_inspected_and_findings_redacted(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            personal = "synthetic" + "@" + "private.example"
            credential = "AKIA" + "B" * 16
            for extension in (".cjs", ".mjs"):
                name = "module" + extension
                (root / name).write_text(personal + "\n" + credential, encoding="utf-8")
                problems = mod.check_paths(root, [name])
                self.assertEqual(2, len(problems))
                self.assertNotIn(personal, str(problems))
                self.assertNotIn(credential, str(problems))


if __name__ == "__main__":
    unittest.main()

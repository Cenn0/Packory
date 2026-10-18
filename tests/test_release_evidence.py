"""Synthetic signed-release acceptance regressions. Never touches production."""
import datetime as dt
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import tempfile
import unittest

MODULE = pathlib.Path(__file__).resolve().parents[1] / "scripts" / "verify_release_evidence.py"
spec = importlib.util.spec_from_file_location("release_evidence", MODULE)
assert spec and spec.loader
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
SHA = "a" * 40


class EvidenceGateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="packory-release-evidence-")
        self.addCleanup(self.tmp.cleanup)
        self.root = pathlib.Path(self.tmp.name)
        self.private = self.root / "test-only-key.pem"
        self.public = self.root / "trusted-public.pem"
        subprocess.run(["openssl", "genpkey", "-algorithm", "RSA",
                        "-pkeyopt", "rsa_keygen_bits:2048", "-out",
                        str(self.private)], check=True, capture_output=True)
        subprocess.run(["openssl", "pkey", "-in", str(self.private),
                        "-pubout", "-out", str(self.public)],
                       check=True, capture_output=True)
        now = dt.datetime.now(dt.timezone.utc)
        self.doc = {
            "schema_version": 1,
            "revision": SHA,
            "audience": "packory-production-acceptance",
            "environment": "isolated-production-like",
            "verifier_key_id": "external-reviewer",
            "issued_at": (now - dt.timedelta(minutes=2)).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "expires_at": (now + dt.timedelta(hours=1)).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "gates": {}
        }
        for gate in release.GATES:
            report = gate + ".json"
            content = json.dumps({"synthetic": gate}, sort_keys=True).encode()
            (self.root / report).write_bytes(content)
            self.doc["gates"][gate] = {
                "status": "pass", "report": report,
                "sha256": hashlib.sha256(content).hexdigest()
            }
        self.publish()

    def publish(self):
        (self.root / "release-manifest.json").write_text(
            json.dumps(self.doc, sort_keys=True), encoding="utf-8"
        )
        subprocess.run(
            ["openssl", "dgst", "-sha256", "-sign", str(self.private),
             "-sigopt", "rsa_padding_mode:pss", "-sigopt", "rsa_pss_saltlen:-1",
             "-out", str(self.root / "release-manifest.sig"),
             str(self.root / "release-manifest.json")],
            check=True, capture_output=True,
        )

    def verify(self):
        return release.validate(self.root, SHA, self.public, "external-reviewer")

    def test_all_synthetic_gates_are_signed_and_bound(self):
        self.assertEqual(self.verify(), len(release.GATES))

    def test_missing_and_failed_gate_rejected_even_when_signed(self):
        del self.doc["gates"]["real_s3_provider"]
        self.publish()
        with self.assertRaises(release.Rejected):
            self.verify()
        self.doc["gates"]["real_s3_provider"] = {
            "status": "failed", "report": "real_s3_provider.json",
            "sha256": "0" * 64
        }
        self.publish()
        with self.assertRaises(release.Rejected):
            self.verify()

    def test_signed_gate_with_missing_or_changed_report_fails(self):
        path = self.root / "pg_s3_restore.json"
        path.write_text("tampered")
        with self.assertRaises(release.Rejected):
            self.verify()
        path.unlink()
        with self.assertRaises(release.Rejected):
            self.verify()

    def test_signed_report_must_be_unambiguous_valid_json_object(self):
        gate = "real_s3_provider"
        path = self.root / (gate + ".json")
        for payload in (
            b"not JSON", b"", b"[]", b"null",
            b'{"duplicate":1,"duplicate":2}', b'{"result": NaN}',
            b'{"result": Infinity}', b'{"message": "\\xff"}',
        ):
            with self.subTest(payload=payload):
                path.write_bytes(payload)
                self.doc["gates"][gate]["sha256"] = hashlib.sha256(payload).hexdigest()
                self.publish()  # Signature and digest genuinely match bad bytes.
                with self.assertRaises(release.Rejected):
                    self.verify()

    def test_signature_authenticates_the_bytes_parsed_by_verifier(self):
        # Valid signed fixture survives the verifier's stdin-based OpenSSL
        # invocation. The signature must authenticate exactly `raw`, not
        # a second, potentially different file read.
        self.assertEqual(self.verify(), len(release.GATES))

    def test_manifest_tampering_and_invalid_signature_fail(self):
        path = self.root / "release-manifest.json"
        path.write_bytes(path.read_bytes() + b" ")
        with self.assertRaises(release.Rejected):
            self.verify()
        self.publish()
        (self.root / "release-manifest.sig").write_bytes(b"bad-signature")
        with self.assertRaises(release.Rejected):
            self.verify()

    def test_expired_or_mismatched_revision_rejected(self):
        now = dt.datetime.now(dt.timezone.utc)
        self.doc["expires_at"] = (now - dt.timedelta(minutes=1)).strftime("%Y-%m-%dT%H:%M:%SZ")
        self.publish()
        with self.assertRaises(release.Rejected):
            self.verify()
        self.doc["expires_at"] = (now + dt.timedelta(hours=1)).strftime("%Y-%m-%dT%H:%M:%SZ")
        self.doc["revision"] = "b" * 40
        self.publish()
        with self.assertRaises(release.Rejected):
            self.verify()

    def test_signed_path_traversal_and_duplicate_report_fail(self):
        self.doc["gates"]["real_s3_provider"]["report"] = "../elsewhere.json"
        self.publish()
        with self.assertRaises(release.Rejected):
            self.verify()
        self.doc["gates"]["real_s3_provider"]["report"] = "pg_s3_restore.json"
        self.doc["gates"]["real_s3_provider"]["sha256"] = self.doc["gates"]["pg_s3_restore"]["sha256"]
        self.publish()
        with self.assertRaises(release.Rejected):
            self.verify()

    def test_duplicate_manifest_key_and_unsigned_extra_field_fail(self):
        data = (self.root / "release-manifest.json").read_text()
        data = data.replace('"schema_version": 1', '"schema_version": 1, "schema_version": 1')
        (self.root / "release-manifest.json").write_text(data)
        with self.assertRaises(release.Rejected):
            self.verify()
        self.doc["private_hostname"] = "not-allowed"
        self.publish()
        with self.assertRaises(release.Rejected):
            self.verify()

    def test_invalid_key_or_unsigned_provider_cannot_pass(self):
        self.doc["verifier_key_id"] = "other"
        self.publish()
        with self.assertRaises(release.Rejected):
            self.verify()
        self.doc["verifier_key_id"] = "external-reviewer"
        self.publish()
        with self.assertRaises(release.Rejected):
            release.validate(self.root, SHA, self.public, "not-approved")


if __name__ == "__main__":
    unittest.main()

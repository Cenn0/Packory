"""Real boto3/Moto S3 version API regression for offline snapshot tooling.

Uses disposable random test buckets on the local emulator. Does NOT validate
a production S3 provider or PostgreSQL restore and must not be reported as such.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
import uuid

spec = importlib.util.spec_from_file_location(
    "pg_s3_snapshot", Path(__file__).resolve().parents[1] /
    "scripts" / "pg_s3_snapshot.py")
snapshot = importlib.util.module_from_spec(spec)
spec.loader.exec_module(snapshot)


@unittest.skipUnless(os.getenv("PACKORY_TEST_S3"), "local S3 emulator required")
class VersionedSnapshotApiTests(unittest.TestCase):
    def test_captures_every_version_and_reconstructs_only_live_keys(self):
        import boto3
        from botocore.config import Config
        endpoint = os.environ["PACKORY_TEST_S3"]
        client = boto3.client("s3", region_name="us-east-1", endpoint_url=endpoint,
                              aws_access_key_id="test", aws_secret_access_key="test",
                              config=Config(s3={"addressing_style": "path"}))
        original = "packory-snapshot-test-" + uuid.uuid4().hex[:16]
        restored = "packory-restore-test-" + uuid.uuid4().hex[:16]
        for bucket in (original, restored):
            client.create_bucket(Bucket=bucket)
            client.put_bucket_versioning(
                Bucket=bucket, VersioningConfiguration={"Status": "Enabled"})
            self.assertTrue(snapshot.versioning_enabled(client, bucket))

        prefix = "packory-test/"
        live_key = prefix + "nuget/demo/1.0.0.nupkg"
        retired_key = prefix + "retired"
        evidence_key = prefix + "recovery/quarantine/evidence"

        client.put_object(Bucket=original, Key=live_key, Body=b"generation-1")
        client.put_object(Bucket=original, Key=live_key, Body=b"generation-2")
        client.put_object(Bucket=original, Key=retired_key, Body=b"deleted-version")
        client.delete_object(Bucket=original, Key=retired_key)
        client.put_object(Bucket=original, Key=evidence_key, Body=b"quarantine-evidence")

        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            pg = root / "metadata.dump"
            pg.write_bytes(b"synthetic PG fixture only, not a real database dump")
            entries, markers, total = snapshot.capture_object_versions(
                client, original, prefix, root, 1024 * 1024)
            self.assertEqual(4, len(entries))
            self.assertEqual(1, len(markers))
            self.assertTrue(markers[0]["is_latest"])
            self.assertTrue(any("quarantine" in e["key"] for e in entries))
            self.assertGreater(total, 10)

            manifest = {
                "schema": 1, "profile": "postgresql-s3-offline",
                "source": {"bucket": original, "prefix": prefix, "database": "synthetic"},
                "postgres": {"path": "metadata.dump", "size": pg.stat().st_size,
                             "sha256": snapshot.checksum(pg), "table_counts": {}},
                "objects": entries, "delete_markers": markers,
            }
            (root / "manifest.json").write_text(json.dumps(manifest))
            snapshot.verify(root)
            self.assertEqual(2, snapshot.restore_current_objects(
                client, restored, manifest, root))

            body = client.get_object(Bucket=restored, Key=live_key)["Body"].read()
            self.assertEqual(b"generation-2", body)
            archived = client.get_object(Bucket=restored, Key=evidence_key)["Body"].read()
            self.assertEqual(b"quarantine-evidence", archived)
            with self.assertRaises(Exception):
                client.get_object(Bucket=restored, Key=retired_key)

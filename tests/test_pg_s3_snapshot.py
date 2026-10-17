"""Offline snapshot proof tests; fake S3 is not real provider certification."""
import importlib.util
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "pg_s3_snapshot", Path(__file__).resolve().parents[1] /
    "scripts" / "pg_s3_snapshot.py")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class Body(io.BytesIO):
    pass


class Paginator:
    def __init__(self, parent):
        self.parent = parent

    def paginate(self, Bucket, Prefix):
        yield {"Versions": [
            {"Key": key, "VersionId": version, "IsLatest": True}
            for key, version in self.parent.data if key.startswith(Prefix)
        ]}


class FakeS3:
    def __init__(self):
        self.data = {("packory/nuget/alpha/file.nupkg", "v1"): b"hello",
                     ("packory/recovery/quarantine/orphan", "v1"): b"evidence"}

    def get_bucket_versioning(self, Bucket):
        return {"Status": "Enabled"}

    def get_paginator(self, name):
        if name != "list_object_versions":
            raise ValueError(name)
        return Paginator(self)

    def get_object(self, Bucket, Key, VersionId=None):
        if VersionId is None:
            VersionId = "v1"
        payload = self.data[(Key, VersionId)]
        return {"Body": Body(payload), "ContentLength": len(payload)}

    def put_object(self, Bucket, Key, Body, IfNoneMatch, ContentType):
        if (Key, "v1") in self.data:
            raise RuntimeError("Would overwrite")
        self.data[(Key, "v1")] = Body.read()


class SnapshotTests(unittest.TestCase):
    def test_capture_and_verify_archive_versions_including_quarantine(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "metadata.dump").write_bytes(b"synthetic-postgres-dump")
            db_bytes = (root / "metadata.dump").read_bytes()
            s3 = FakeS3()
            entries, markers, total = mod.capture_object_versions(
                s3, "bucket", "packory/", root, 2048)
            self.assertEqual(2, len(entries))
            self.assertEqual([], markers)
            self.assertEqual(len(b"hello") + len(b"evidence"), total)
            manifest = {
                "schema": 1, "profile": "postgresql-s3-offline",
                "source": {"bucket": "bucket", "prefix": "packory/", "database": "origin"},
                "postgres": {"path": "metadata.dump", "size": len(db_bytes),
                             "sha256": hashlib.sha256(db_bytes).hexdigest(), "table_counts": {}},
                "objects": entries, "delete_markers": []
            }
            (root / "manifest.json").write_text(json.dumps(manifest))
            self.assertEqual(2, len(mod.verify(root)["objects"]))
            (root / entries[0]["path"]).write_bytes(b"tampered")
            with self.assertRaises(mod.SnapshotError):
                mod.verify(root)

    def test_rejects_traversal_and_untracked_snapshot_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "metadata.dump").write_bytes(b"pg")
            manifest = {
                "schema": 1, "profile": "postgresql-s3-offline",
                "source": {"prefix": "packory/"},
                "postgres": {"path": "metadata.dump", "size": 2,
                             "sha256": hashlib.sha256(b"pg").hexdigest()},
                "objects": [{"key": "packory/key", "version": "v1",
                             "path": "../outside", "size": 0,
                             "sha256": hashlib.sha256(b"").hexdigest()}],
                "delete_markers": []
            }
            (root / "manifest.json").write_text(json.dumps(manifest))
            with self.assertRaises(mod.SnapshotError):
                mod.verify(root)

    def test_metadata_fingerprints_catch_same_count_value_corruption(self):
        class Rows:
            def __init__(self, data):
                self.data = data

            def execute(self, sql):
                self.last_sql = sql
                return [(text,) for text in sorted(self.data)]

        data = Rows(['{"id":1,"name":"alpha"}', '{"id":2,"name":"beta"}'])
        original = mod.metadata_fingerprints(data, ("packages",))
        self.assertEqual(2, original["packages"]["count"])
        self.assertEqual(64, len(original["packages"]["sha256"]))
        self.assertIn('COLLATE "C"', data.last_sql)
        data.data = ['{"id":1,"name":"alpha"}', '{"id":2,"name":"changed"}']
        changed = mod.metadata_fingerprints(data, ("packages",))
        self.assertEqual(original["packages"]["count"], changed["packages"]["count"])
        self.assertNotEqual(original["packages"]["sha256"], changed["packages"]["sha256"])

    def test_version_two_snapshot_must_include_feed_metadata_fingerprints(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "metadata.dump").write_bytes(b"synthetic")
            fingerprints = {name: {"count": 0, "sha256": hashlib.sha256(b"").hexdigest()}
                            for name in mod.CORE_TABLES}
            fingerprints.update({name: {"count": 0, "sha256": hashlib.sha256(b"").hexdigest()}
                                 for name in ("packory_schema_version", *mod.FEED_TABLES)})
            manifest = {
                "schema": 2, "profile": "postgresql-s3-offline",
                "source": {"bucket": "test-bucket", "prefix": "test/", "database": "source"},
                "postgres": {"path": "metadata.dump", "size": 9,
                             "sha256": hashlib.sha256(b"synthetic").hexdigest(),
                             "table_counts": {name: 0 for name in fingerprints},
                             "table_fingerprints": fingerprints},
                "objects": [], "delete_markers": []
            }
            (root / "manifest.json").write_text(json.dumps(manifest))
            self.assertEqual(2, mod.verify(root)["schema"])
            del fingerprints["feeds"]
            manifest["postgres"]["table_counts"].pop("feeds")
            (root / "manifest.json").write_text(json.dumps(manifest))
            with self.assertRaises(mod.SnapshotError):
                mod.verify(root)

    def test_endpoint_guards(self):
        with self.assertRaises(mod.SnapshotError):
            mod.validate_endpoint("http://s3.example.test")
        self.assertEqual("http://127.0.0.1:5000",
                         mod.validate_endpoint("http://127.0.0.1:5000", True))
        with self.assertRaises(mod.SnapshotError):
            mod.validate_endpoint("https://user:pass" + "@" + "s3.example.test")

    def test_retains_old_versions_but_restores_current_only(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            entries, _, _ = mod.capture_object_versions(
                FakeS3(), "bucket", "packory/", root, 4096)
            restored = FakeS3()
            restored.data.clear()
            count = mod.restore_current_objects(restored, "new-test-bucket",
                                                {"objects": entries, "delete_markers": []}, root)
            self.assertEqual(2, count)
            self.assertEqual(b"hello",
                             restored.get_object(Bucket="target",
                             Key="packory/nuget/alpha/file.nupkg")["Body"].read())


if __name__ == "__main__":
    unittest.main()

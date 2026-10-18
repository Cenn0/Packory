"""Deterministic hostile-manifest/fault tests; no real-provider certification."""
import copy
from contextlib import nullcontext
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location(
    "pg_s3_snapshot_safety_target", Path(__file__).resolve().parents[1] / "scripts" / "pg_s3_snapshot.py")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class SnapshotFixture:
    def __init__(self, root):
        self.root = root
        self.manifest = {
            "schema": 2, "profile": "postgresql-s3-offline",
            "source": {"prefix": "packory/", "bucket": "fixture-source", "database": "fixture-source"},
            "postgres": {"path": "metadata.dump", "size": 2,
                         "sha256": hashlib.sha256(b"pg").hexdigest(),
                         "database_schema_version": 2,
                         "table_counts": {name: 0 for name in (*mod.CORE_TABLES, "packory_schema_version", *mod.FEED_TABLES)},
                         "table_fingerprints": {name: {"count": 0, "sha256": hashlib.sha256(b"").hexdigest()}
                                                for name in (*mod.CORE_TABLES, "packory_schema_version", *mod.FEED_TABLES)}},
            "objects": [], "delete_markers": []
        }
        (root / "metadata.dump").write_bytes(b"pg")
        (root / "objects").mkdir()
        self.add("packory/nuget/demo/file.nupkg", "v1", b"old", False)
        self.add("packory/nuget/demo/file.nupkg", "v2", b"new", True)
        self.add("packory/recovery/quarantine/file", "q1", b"evidence", True)
        self.add("packory/deleted/file", "d1", b"deleted", False)
        self.manifest["delete_markers"].append({"key": "packory/deleted/file", "version": "d2", "is_latest": True})
        self.save()

    def add(self, key, version, body, latest):
        path = "objects/" + mod.unique_blob(key, version) + ".bin"
        (self.root / path).write_bytes(body)
        self.manifest["objects"].append({"key": key, "version": version, "path": path,
                                         "is_latest": latest, "size": len(body),
                                         "sha256": hashlib.sha256(body).hexdigest()})

    def save(self):
        (self.root / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")


class Target:
    def __init__(self):
        self.writes = []
        self.data = {}

    def put_object(self, Bucket, Key, Body, IfNoneMatch, ContentType):
        if IfNoneMatch != "*" or Key in self.data:
            raise AssertionError("Unexpected overwrite")
        self.writes.append(Key)
        self.data[Key] = Body.read()

    def get_object(self, Bucket, Key, VersionId=None):
        return {"ContentLength": len(self.data[Key]), "Body": io.BytesIO(self.data[Key])}


class ManifestSafetyTests(unittest.TestCase):
    def test_valid_versions_preserve_evidence_and_restore_only_live_versions(self):
        with tempfile.TemporaryDirectory() as temp:
            f = SnapshotFixture(Path(temp))
            self.assertEqual(4, len(mod.verify(f.root)["objects"]))
            target = Target()
            self.assertEqual(2, mod.restore_current_objects(target, "isolated", f.manifest, f.root))
            self.assertEqual(b"new", target.data["packory/nuget/demo/file.nupkg"])
            self.assertEqual(b"evidence", target.data["packory/recovery/quarantine/file"])
            self.assertNotIn("packory/deleted/file", target.data)

    def test_invalid_object_or_marker_metadata_prevents_all_restore_writes(self):
        cases = [
            ("objects", 0, "is_latest", "false"),
            ("objects", 0, "is_latest", 0),
            ("objects", 0, "is_latest", None),
            ("objects", 0, "is_latest", True),
            ("objects", 1, "is_latest", False),
            ("objects", 0, "size", True),
            ("objects", 0, "size", -1),
            ("objects", 0, "size", 3.0),
            ("objects", 0, "version", ""),
            ("objects", 0, "version", None),
            ("objects", 0, "sha256", "0" * 64 + "\n"),
            ("objects", 0, "key", "outside/key"),
            ("objects", 0, "path", "../metadata.dump"),
            ("delete_markers", 0, "is_latest", "false"),
            ("delete_markers", 0, "is_latest", False),
            ("delete_markers", 0, "version", ""),
            ("delete_markers", 0, "key", "outside/deleted"),
            ("delete_markers", 0, "key", "packory/../deleted"),
            ("delete_markers", 0, "key", "packory/deleted\n"),
        ]
        with tempfile.TemporaryDirectory() as temp:
            f = SnapshotFixture(Path(temp))
            original = copy.deepcopy(f.manifest)
            for group, index, field, value in cases:
                with self.subTest(group=group, field=field, value=value):
                    f.manifest = copy.deepcopy(original)
                    f.manifest[group][index][field] = value
                    f.save()
                    with self.assertRaises(mod.SnapshotError):
                        mod.verify(f.root)
                    target = Target()
                    with self.assertRaises(mod.SnapshotError):
                        mod.restore_current_objects(target, "isolated", f.manifest, f.root)
                    self.assertEqual([], target.writes)

    def test_missing_fields_and_duplicate_version_identities_fail_closed(self):
        with tempfile.TemporaryDirectory() as temp:
            f = SnapshotFixture(Path(temp))
            original = copy.deepcopy(f.manifest)
            for mutation in (lambda m: m["objects"][0].pop("is_latest"),
                             lambda m: m["delete_markers"][0].pop("version"),
                             lambda m: m["delete_markers"].append(copy.deepcopy(m["delete_markers"][0])),
                             lambda m: m["delete_markers"].append({"key": m["objects"][1]["key"],
                                                                  "version": m["objects"][1]["version"], "is_latest": False}),
                             lambda m: m["objects"].append(copy.deepcopy(m["objects"][0]))):
                f.manifest = copy.deepcopy(original)
                mutation(f.manifest)
                f.save()
                with self.assertRaises(mod.SnapshotError):
                    mod.verify(f.root)

    def test_postgres_fields_use_exact_integer_and_digest_types(self):
        with tempfile.TemporaryDirectory() as temp:
            f = SnapshotFixture(Path(temp))
            original = copy.deepcopy(f.manifest)
            changes = [("size", True), ("size", "2"), ("database_schema_version", True),
                       ("sha256", None), ("table_counts", {"packages": True})]
            for key, value in changes:
                f.manifest = copy.deepcopy(original)
                f.manifest["postgres"][key] = value
                f.save()
                with self.subTest(key=key, value=value), self.assertRaises(mod.SnapshotError):
                    mod.verify(f.root)
            f.manifest = copy.deepcopy(original)
            f.manifest["snapshot_bytes"] = True
            f.save()
            with self.assertRaises(mod.SnapshotError):
                mod.verify(f.root)

    def test_duplicate_json_fields_and_nonfinite_constants_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            f = SnapshotFixture(Path(temp))
            raw = json.dumps(f.manifest)
            for extra in ('"schema":2,', '"extra":NaN,', '"extra":Infinity,'):
                (f.root / "manifest.json").write_text("{" + extra + raw[1:])
                with self.assertRaises(mod.SnapshotError):
                    mod.verify(f.root)

    def test_legacy_format_keeps_strict_current_version_selection(self):
        with tempfile.TemporaryDirectory() as temp:
            f = SnapshotFixture(Path(temp))
            f.manifest["schema"] = 1
            f.manifest["postgres"].pop("table_fingerprints")
            f.save()
            self.assertEqual(1, mod.verify(f.root)["schema"])
            f.manifest["delete_markers"][0]["is_latest"] = "true"
            f.save()
            with self.assertRaises(mod.SnapshotError):
                mod.verify(f.root)

    def test_symlinks_hardlinks_and_special_files_are_rejected_before_open(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            f = SnapshotFixture(root)
            for kind in ("symlink", "hardlink", "fifo"):
                extra = root / "extra"
                if kind == "symlink":
                    extra.symlink_to(root / "metadata.dump")
                elif kind == "hardlink":
                    os.link(root / "metadata.dump", extra)
                elif hasattr(os, "mkfifo"):
                    os.mkfifo(extra)
                else:
                    continue
                with self.subTest(kind=kind), self.assertRaises(mod.SnapshotError):
                    mod.verify(f.root)
                extra.unlink()

    def test_restore_response_is_size_bounded_and_stream_always_closed(self):
        class WrongTarget(Target):
            def get_object(self, **kwargs):
                self.body = io.BytesIO(b"excessive bytes")
                return {"ContentLength": 3, "Body": self.body}
        with tempfile.TemporaryDirectory() as temp:
            f = SnapshotFixture(Path(temp))
            target = WrongTarget()
            with self.assertRaises(mod.SnapshotError):
                mod.restore_current_objects(target, "isolated", f.manifest, f.root)
            self.assertTrue(target.body.closed)

    def test_cli_redacts_unexpected_sdk_failures(self):
        secret_marker = "do-not-print-operator-detail"
        args = ["snapshot", "backup", "--snapshot", "unused", "--bucket", "isolated",
                "--prefix", "packory/", "--writes-stopped-confirmed"]
        with patch.object(mod.sys, "argv", args), patch.object(mod, "environment"), \
             patch.object(mod, "client", side_effect=RuntimeError(secret_marker)), \
             patch.object(mod.sys, "stderr", new_callable=io.StringIO) as output:
            self.assertEqual(1, mod.main())
            self.assertNotIn(secret_marker, output.getvalue())
            self.assertNotIn("Traceback", output.getvalue())


class SourceInventoryTests(unittest.TestCase):
    def test_inventory_rejects_ambiguous_current_objects_and_outside_keys(self):
        class Source:
            def __init__(self, records):
                self.records = records
            def get_paginator(self, _name):
                return self
            def paginate(self, **_kwargs):
                yield {"Versions": self.records}
        record = {"Key": "packory/one", "VersionId": "v1", "IsLatest": True}
        for bad in (dict(record, Key="elsewhere/one"), dict(record, IsLatest="true"),
                    dict(record, VersionId=""), dict(record, IsLatest=False),
                    dict(record, Size=True)):
            with self.subTest(bad=bad), self.assertRaises(mod.SnapshotError):
                mod.inventory(Source([bad]), "fixture", "packory/")
        with self.assertRaises(mod.SnapshotError):
            mod.inventory(Source([record, dict(record, VersionId="v2")]), "fixture", "packory/")

    def test_offline_backup_refuses_source_inventory_changes_and_removes_only_staging(self):
        class Source:
            scans = 0
            def get_bucket_versioning(self, **_kwargs):
                return {"Status": "Enabled"}
            def get_paginator(self, _name):
                return self
            def paginate(self, **_kwargs):
                self.scans += 1
                yield {"Versions": [{"Key": "packory/blob", "VersionId": "v" + str(self.scans),
                                     "IsLatest": True, "Size": 3, "ETag": "fixture-etag"}]}
            def get_object(self, **_kwargs):
                return {"ContentLength": 3, "Body": io.BytesIO(b"abc")}
        fingerprints = {name: {"count": 0, "sha256": hashlib.sha256(b"").hexdigest()}
                        for name in mod.CORE_TABLES}
        with tempfile.TemporaryDirectory() as temp:
            destination = Path(temp) / "snapshot"
            sentinel = Path(temp) / "retain"
            sentinel.write_bytes(b"unrelated")
            with patch.object(mod, "postgres", return_value=nullcontext(object())), \
                 patch.object(mod, "metadata_tables", return_value=mod.CORE_TABLES), \
                 patch.object(mod, "metadata_fingerprints", return_value=fingerprints), \
                 patch.object(mod, "pg_command", side_effect=lambda _op, path: path.write_bytes(b"pg")), \
                 patch.dict(os.environ, {"PGDATABASE": "fixture-source"}):
                with self.assertRaisesRegex(mod.SnapshotError, "S3 inventory changed"):
                    mod.backup(destination, Source(), "fixture-source", "packory/", 1024)
            self.assertFalse(destination.exists())
            self.assertEqual(["retain"], sorted(path.name for path in Path(temp).iterdir()))
            self.assertEqual(b"unrelated", sentinel.read_bytes())


if __name__ == "__main__":
    unittest.main()

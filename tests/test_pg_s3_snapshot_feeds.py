"""V10 private-feed snapshot graph and bundle evidence; synthetic, offline only."""
import base64
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
import warnings
import zipfile

spec = importlib.util.spec_from_file_location(
    "pg_s3_snapshot_feeds_target", Path(__file__).resolve().parents[1] / "scripts" / "pg_s3_snapshot.py")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)
STAMP = "2026-01-01T00:00:00.0000000+00:00"


def sha(body):
    return hashlib.sha256(body).hexdigest()


def bundle(members, compression=zipfile.ZIP_STORED):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=compression) as archive, warnings.catch_warnings():
        warnings.filterwarnings("ignore", message="Duplicate name:.*", category=UserWarning)
        for name, content in members:
            archive.writestr(name, content)
    return output.getvalue()


def asset(feed, kind, name, version, path, media, body):
    return {"feed_id": feed, "kind": kind, "name": name, "version": version,
            "path": path, "media_type": media, "sha256": sha(body), "size": len(body), "created_at": STAMP}


def package(feed, kind, payload=b"synthetic original package bytes", change=None, extra=None, compression=zipfile.ZIP_STORED):
    name = "@scope/package" if kind == "npm" else "synthetic-package"
    version = "1.0.0+fixture" if kind == "cargo" else "1.0.0"
    filename = "synthetic_package-1.0.0-py3-none-any.whl"
    path = name + "/" + (filename if kind == "pypi" else version.split("+")[0])
    if kind == "cargo":
        metadata = {"name": name, "vers": version, "cksum": sha(payload)}
    elif kind == "pypi":
        metadata = {"name": name, "version": version, "filename": filename,
                    "sha256": sha(payload), "size": len(payload), "requiresPython": None}
    else:
        metadata = {"manifest": {"name": name, "version": version, "dist": {
            "tarball": "https://registry.example.test/feeds/" + feed + "/npm/%40scope%2Fpackage/-/1.0.0.tgz",
            "shasum": hashlib.sha1(payload).hexdigest(),
            "integrity": "sha512-" + base64.b64encode(hashlib.sha512(payload).digest()).decode()}},
            "tags": {"latest": version}}
    envelope = {"schemaVersion": 1, "feedId": feed, "kind": kind, "path": path,
                "name": name, "version": version, "payloadSha256": sha(payload),
                "payloadSize": len(payload), "metadata": metadata}
    if change:
        change(envelope)
    members = [("metadata.json", json.dumps(envelope).encode()), ("payload", payload)]
    if extra:
        members.append(extra)
    body = bundle(members, compression)
    return asset(feed, kind, name, version, path, "application/vnd.packory." + kind + "-package+zip", body), body


class Fixture:
    def __init__(self, root):
        self.root = root
        (root / "objects").mkdir()
        (root / "metadata.dump").write_bytes(b"synthetic dump")
        tables = (*mod.CORE_TABLES, "packory_schema_version", *mod.FEED_TABLES, *mod.DIGEST_TABLES,
                  *mod.ISOLATED_FEED_TABLES, *mod.RAW_TABLES, *mod.MAVEN_TABLES, *mod.GO_TABLES,
                  *mod.FEED_ASSET_TABLES, *mod.FEED_OCI_TABLES, *mod.SECURITY_REGISTRY_TABLES)
        self.manifest = {
            "schema": 3, "profile": "postgresql-s3-offline",
            "source": {"prefix": "synthetic/", "bucket": "synthetic-source", "database": "synthetic-source"},
            "postgres": {"path": "metadata.dump", "size": len(b"synthetic dump"), "sha256": sha(b"synthetic dump"),
                         "database_schema_version": 10, "table_counts": {name: 0 for name in tables},
                         "table_fingerprints": {name: {"count": 0, "sha256": sha(b"")} for name in tables}},
            "feed_references": {"assets": [], "oci_tags": []}, "objects": [], "delete_markers": []}

    def add(self, item, body):
        key = "synthetic/" + mod.feed_asset_key(item)
        path = "objects/" + mod.unique_blob(key, "synthetic-v1") + ".bin"
        (self.root / path).write_bytes(body)
        self.manifest["objects"].append({"key": key, "version": "synthetic-v1", "path": path,
                                         "is_latest": True, "size": len(body), "sha256": sha(body)})
        self.manifest["feed_references"]["assets"].append(item)
        return item

    def oci(self, feed, repo, body, manifest=False):
        digest = sha(body)
        item = asset(feed, "oci", repo, "sha256:" + digest,
                     ("manifest/" if manifest else "blob/") + repo + "/" + digest,
                     "application/vnd.oci.image.manifest.v1+json" if manifest else "application/octet-stream", body)
        return self.add(item, body)

    def tag(self, item, tag="latest"):
        self.manifest["feed_references"]["oci_tags"].append({"feed_id": item["feed_id"], "name": item["name"],
            "tag": tag, "digest": item["version"], "updated_at": STAMP})

    def save(self):
        for field, table in (("assets", "feed_assets"), ("oci_tags", "feed_oci_tags")):
            count = len(self.manifest["feed_references"][field]) if "feed_references" in self.manifest else 0
            self.manifest["postgres"]["table_counts"][table] = count
            if table in self.manifest["postgres"]["table_fingerprints"]:
                self.manifest["postgres"]["table_fingerprints"][table]["count"] = count
        (self.root / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")


def image(config, subject=None):
    value = {"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json",
             "config": {"digest": config["version"], "size": config["size"], "mediaType": "application/vnd.oci.image.config.v1+json"},
             "layers": []}
    if subject:
        value["subject"] = subject
    return json.dumps(value).encode()


class FeedSnapshotTests(unittest.TestCase):
    def test_v15_deprecations_require_exact_feed_revision_and_bounded_messages(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = Fixture(Path(directory))
            item, body = package("alpha-npm", "npm")
            fixture.add(item, body)
            fixture.save()
            manifest = fixture.manifest
            manifest["postgres"]["database_schema_version"] = 15
            manifest["postgres"]["table_counts"].update(feed_npm_tags=0, feed_cargo_yanks=0, feed_npm_deprecations=1)
            manifest["feed_references"].update(npm_tags=[], cargo_yanks=[])
            pointer = {"feed_id": item["feed_id"], "name": item["name"], "version": item["version"],
                       "message": "Use the next release", "updated_at": STAMP}
            for message in ("Use the next release", ""):
                manifest["feed_references"]["npm_deprecations"] = [pointer | {"message": message}]
                mod.verify_feed_references(manifest, fixture.root)
            for change in ({"feed_id": "foreign"}, {"version": "9.9.9"}, {"message": None},
                           {"message": "x" * 2049}, {"message": "line\ncontrol"}, {"updated_at": ""}):
                manifest["feed_references"]["npm_deprecations"] = [pointer | change]
                with self.subTest(change=change), self.assertRaises(mod.SnapshotError):
                    mod.verify_feed_references(manifest, fixture.root)
            manifest["feed_references"]["npm_deprecations"] = [pointer, pointer]
            manifest["postgres"]["table_counts"]["feed_npm_deprecations"] = 2
            with self.assertRaises(mod.SnapshotError):
                mod.verify_feed_references(manifest, fixture.root)

    def test_v14_cargo_yank_inventory_is_bound_to_exact_committed_feed_asset(self):
        with tempfile.TemporaryDirectory() as directory:
            f = Fixture(Path(directory))
            item, body = package("alpha-cargo", "cargo")
            f.add(item, body)
            f.save()
            manifest = f.manifest
            manifest["postgres"]["database_schema_version"] = 14
            manifest["postgres"]["table_counts"]["feed_npm_tags"] = 0
            manifest["postgres"]["table_counts"]["feed_cargo_yanks"] = 1
            manifest["feed_references"]["npm_tags"] = []
            pointer = {
                "feed_id": item["feed_id"], "name": item["name"],
                "version": item["version"], "yanked": 1, "updated_at": STAMP,
            }
            manifest["feed_references"]["cargo_yanks"] = [pointer]
            mod.verify_feed_references(manifest, f.root)
            for change in (
                {"feed_id": "foreign-feed"},
                {"version": "9.9.9"},
                {"yanked": 2},
                {"yanked": True},
                {"updated_at": ""},
            ):
                with self.subTest(change=change):
                    manifest["feed_references"]["cargo_yanks"] = [pointer | change]
                    with self.assertRaises(mod.SnapshotError):
                        mod.verify_feed_references(manifest, f.root)
            manifest["feed_references"]["cargo_yanks"] = [pointer, pointer]
            manifest["postgres"]["table_counts"]["feed_cargo_yanks"] = 2
            with self.assertRaises(mod.SnapshotError):
                mod.verify_feed_references(manifest, f.root)

    def test_same_name_revision_two_feed_bundles_are_independent(self):
        with tempfile.TemporaryDirectory() as directory:
            f = Fixture(Path(directory))
            for kind in ("npm", "cargo", "pypi"):
                for feed in ("alpha-feed", "beta-feed"):
                    f.add(*package(feed, kind, (kind + feed).encode()))
            f.save()
            self.assertEqual(6, len(mod.verify(f.root)["feed_references"]["assets"]))
            self.assertEqual(6, len({item["key"] for item in f.manifest["objects"]}))

    def test_bundle_identity_metadata_and_payload_mismatch_rejected_even_with_new_outer_hash(self):
        changes = (
            lambda e: e.update(feedId="beta-feed"), lambda e: e.update(name="another-package"),
            lambda e: e.update(version="2.0.0"), lambda e: e.update(path="another/path"),
            lambda e: e.update(kind="npm"), lambda e: e.update(payloadSize=True),
            lambda e: e.update(payloadSize=1), lambda e: e.update(payloadSha256="0" * 64),
            lambda e: e["metadata"].update(cksum="0" * 64), lambda e: e.update(schemaVersion=True))
        for change in changes:
            with self.subTest(change=changes.index(change)), tempfile.TemporaryDirectory() as directory:
                f = Fixture(Path(directory))
                f.add(*package("alpha-feed", "cargo", change=change))
                f.save()
                with self.assertRaises(mod.SnapshotError):
                    mod.verify(f.root)

    def test_bundle_has_exact_stored_members_and_no_duplicate_json(self):
        cases = ({"extra": ("../outside", b"ignored")}, {"extra": ("metadata.json", b"{}")},
                 {"compression": zipfile.ZIP_DEFLATED})
        for options in cases:
            with self.subTest(options=options), tempfile.TemporaryDirectory() as directory:
                f = Fixture(Path(directory))
                f.add(*package("alpha-feed", "npm", **options))
                f.save()
                with self.assertRaises(mod.SnapshotError):
                    mod.verify(f.root)
        with tempfile.TemporaryDirectory() as directory:
            f = Fixture(Path(directory))
            item, original = package("alpha-feed", "cargo")
            with zipfile.ZipFile(io.BytesIO(original)) as archive:
                envelope = b'{"feedId":"alpha-feed",' + archive.read("metadata.json")[1:]
                body = bundle([("metadata.json", envelope), ("payload", archive.read("payload"))])
            item.update(size=len(body), sha256=sha(body))
            f.add(item, body)
            f.save()
            with self.assertRaises(mod.SnapshotError):
                mod.verify(f.root)

    def test_adapter_metadata_retains_its_native_field_types(self):
        cases = (("npm", lambda e: e["metadata"].update(tags={"bad/tag": "1.0.0"})),
                 ("npm", lambda e: e["metadata"]["manifest"]["dist"].update(integrity="sha512-invalid")),
                 ("pypi", lambda e: e["metadata"].update(requiresPython=True)),
                 ("pypi", lambda e: e["metadata"].update(size=True)))
        for kind, change in cases:
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as directory:
                f = Fixture(Path(directory))
                f.add(*package("alpha-feed", kind, change=change))
                f.save()
                with self.assertRaises(mod.SnapshotError):
                    mod.verify(f.root)

    def test_go_and_prior_raw_maven_keys_remain_verifiable(self):
        with tempfile.TemporaryDirectory() as directory:
            f = Fixture(Path(directory))
            raw = b"old immutable raw bytes"
            f.add(asset("alpha-raw", "raw", "same.name", "1.0.0", "same.name/1.0.0", "application/octet-stream", raw), raw)
            maven = b"synthetic maven bytes"
            f.add(asset("alpha-maven", "maven", "org.example:demo", "1.0.0",
                        "org/example/demo/1.0.0/demo-1.0.0-sources.jar", "application/java-archive", maven), maven)
            go = bundle([("info", json.dumps({"Version": "v1.0.0", "Time": STAMP}).encode()),
                         ("mod", b"module example.test/Hello\n"), ("zip", b"synthetic zip bytes")])
            f.add(asset("alpha-go", "go", "example.test/Hello", "v1.0.0", "example.test/!hello/@v/v1.0.0.zip",
                        "application/vnd.packory.go-module-bundle+zip", go), go)
            f.save()
            self.assertEqual(3, len(mod.verify(f.root)["feed_references"]["assets"]))

    def test_live_object_must_match_ledger_and_historic_or_deleted_object_is_insufficient(self):
        for mutation in ("deleted", "different-bytes", "foreign-key", "missing"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                f = Fixture(Path(directory))
                f.add(*package("alpha-feed", "cargo"))
                stored = f.manifest["objects"][0]
                if mutation == "deleted":
                    stored["is_latest"] = False
                    f.manifest["delete_markers"].append({"key": stored["key"], "version": "synthetic-v2", "is_latest": True})
                elif mutation == "different-bytes":
                    (f.root / stored["path"]).write_bytes(b"changed")
                    stored.update(size=7, sha256=sha(b"changed"))
                elif mutation == "foreign-key":
                    old = f.root / stored["path"]
                    stored["key"] = stored["key"].replace("alpha-feed", "beta-feed")
                    stored["path"] = "objects/" + mod.unique_blob(stored["key"], stored["version"]) + ".bin"
                    old.rename(f.root / stored["path"])
                else:
                    (f.root / stored["path"]).unlink()
                    f.manifest["objects"].clear()
                f.save()
                with self.assertRaises(mod.SnapshotError):
                    mod.verify(f.root)

    def test_oci_tags_and_graph_use_exact_feed_and_repository_including_empty_blob(self):
        with tempfile.TemporaryDirectory() as directory:
            f = Fixture(Path(directory))
            for feed in ("alpha-oci", "beta-oci"):
                config = f.oci(feed, "team/image", b"")
                subject = {"digest": "sha256:" + "a" * 64, "size": 1, "mediaType": "application/vnd.oci.image.manifest.v1+json"}
                manifest = f.oci(feed, "team/image", image(config, subject), manifest=True)
                f.tag(manifest)
            f.save()
            self.assertEqual(2, len(mod.verify(f.root)["feed_references"]["oci_tags"]))
            f.manifest["feed_references"]["oci_tags"][0]["name"] = "another/image"
            f.save()
            with self.assertRaises(mod.SnapshotError):
                mod.verify(f.root)

    def test_oci_cannot_borrow_blob_from_other_feed_or_repo_or_alias_tag_to_blob(self):
        for mode in ("other-feed", "other-repo", "blob-tag", "size-mismatch"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as directory:
                f = Fixture(Path(directory))
                config = f.oci("alpha-oci", "team/image", b"synthetic config")
                feed = "beta-oci" if mode == "other-feed" else "alpha-oci"
                repo = "other/image" if mode == "other-repo" else "team/image"
                if mode == "size-mismatch":
                    config = dict(config, size=1)
                manifest = f.oci(feed, repo, image(config), manifest=True)
                f.tag(config if mode == "blob-tag" else manifest)
                f.save()
                with self.assertRaises(mod.SnapshotError):
                    mod.verify(f.root)

    def test_oci_external_urls_and_mismatched_inline_data_cannot_enter_restored_graph(self):
        cases = (("urls", ["https://outside.example.test/synthetic-content"]),
                 ("urls", []), ("urls", None), ("data", "W10="), ("data", "bG9uZ2Vy"),
                 ("data", "e31="), ("data", "e30=\n"), ("data", 42), ("data", None))
        for field, value in cases:
            with self.subTest(field=field, value=value), tempfile.TemporaryDirectory() as directory:
                f = Fixture(Path(directory))
                config = f.oci("alpha-oci", "team/image", b"{}")
                document = json.loads(image(config))
                document["config"][field] = value
                f.oci("alpha-oci", "team/image", json.dumps(document).encode(), manifest=True)
                f.save()
                with self.assertRaises(mod.SnapshotError):
                    mod.verify(f.root)
        with tempfile.TemporaryDirectory() as directory:
            f = Fixture(Path(directory))
            config = f.oci("alpha-oci", "team/image", b"{}")
            document = json.loads(image(config))
            document["config"]["data"] = "e30="
            f.oci("alpha-oci", "team/image", json.dumps(document).encode(), manifest=True)
            f.save()
            self.assertEqual(2, len(mod.verify(f.root)["feed_references"]["assets"]))

    def test_v10_requires_tag_fingerprint_and_reference_inventory(self):
        for mutation in ("tag-fingerprint", "inventory", "old-profile", "legacy-profile", "unknown-schema"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                f = Fixture(Path(directory))
                if mutation == "tag-fingerprint":
                    del f.manifest["postgres"]["table_fingerprints"]["feed_oci_tags"]
                elif mutation == "inventory":
                    del f.manifest["feed_references"]
                elif mutation == "old-profile":
                    f.manifest["schema"] = 2
                elif mutation == "legacy-profile":
                    f.manifest["schema"] = 1
                else:
                    f.manifest["postgres"]["database_schema_version"] = 11
                f.save()
                with self.assertRaises(mod.SnapshotError):
                    mod.verify(f.root)

    def test_invalid_graph_aborts_restore_before_any_service_call(self):
        with tempfile.TemporaryDirectory() as directory:
            f = Fixture(Path(directory))
            config = f.oci("alpha-oci", "team/image", b"synthetic")
            f.tag(config)  # A blob can never be the target of a manifest tag.
            f.save()
            with patch.object(mod, "postgres") as pg, patch.object(mod, "versioning_enabled") as s3:
                with self.assertRaises(mod.SnapshotError):
                    mod.restore(f.root, object(), "synthetic-target")
                pg.assert_not_called()
                s3.assert_not_called()

    def test_restored_database_references_must_equal_verified_snapshot_sidecar(self):
        class Cursor:
            def __enter__(self): return self
            def __exit__(self, *_): return False
            def execute(self, _): pass
            def fetchone(self): return (0,)
        class Connection:
            def cursor(self): return Cursor()
        with tempfile.TemporaryDirectory() as directory:
            f = Fixture(Path(directory))
            f.save()
            with patch.dict(os.environ, {"PGDATABASE": "synthetic-target"}), \
                    patch.object(mod, "versioning_enabled", return_value=True), \
                    patch.object(mod, "inventory", return_value=([], [])), \
                    patch.object(mod, "postgres", return_value=nullcontext(Connection())), \
                    patch.object(mod, "restore_current_objects", return_value=0), \
                    patch.object(mod, "pg_command"), \
                    patch.object(mod, "database_schema_version", return_value=10), \
                    patch.object(mod, "counts", return_value=f.manifest["postgres"]["table_counts"]), \
                    patch.object(mod, "metadata_fingerprints", return_value=f.manifest["postgres"]["table_fingerprints"]), \
                    patch.object(mod, "feed_references", return_value={"assets": [{}], "oci_tags": []}):
                with self.assertRaisesRegex(mod.SnapshotError, "Restored private feed references"):
                    mod.restore(f.root, object(), "synthetic-target")

    def test_database_v10_and_captured_column_contract(self):
        class Result:
            def __init__(self, value): self.value = value
            def fetchone(self): return (self.value,)
        class Connection:
            def execute(self, query, args=None):
                if "to_regclass" in query: return Result("exists")
                if query.startswith("SELECT version"): return Result(10)
                if query.startswith("SELECT feed_id,kind,path"):
                    return [("alpha-feed", "raw", "demo/1.0.0", "demo", "1.0.0", "application/octet-stream", "a" * 64, 1, STAMP)]
                if query.startswith("SELECT feed_id,name,tag"): return []
                raise AssertionError(query)
        connection = Connection()
        self.assertIn("feed_oci_tags", mod.metadata_tables(connection))
        refs = mod.feed_references(connection)
        self.assertEqual(set(mod.ASSET_COLUMNS), set(refs["assets"][0]))
        self.assertEqual([], refs["oci_tags"])


if __name__ == "__main__":
    unittest.main()

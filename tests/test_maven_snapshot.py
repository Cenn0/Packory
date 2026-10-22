"""Semantic Maven V11 restore checks, independent of outer backup-file hashes."""
import base64
from contextlib import nullcontext
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


mod = module("maven_snapshot_test_target", ROOT / "scripts/maven_snapshot.py")
offline = module("maven_offline_test_target", ROOT / "scripts/offline_backup.py")
snapshot = module("maven_pgs3_test_target", ROOT / "scripts/pg_s3_snapshot.py")
prior = module("maven_feed_fixtures", Path(__file__).with_name("test_pg_s3_snapshot_feeds.py"))
STAMP = "2026-10-22T00:00:00.0000000+00:00"
BASE = "org/example/demo/"


def sha(body):
    return hashlib.sha256(body).hexdigest()


def metadata_record(scope, path, body):
    return {"scope": scope, "path": path, "content_base64": base64.b64encode(body).decode("ascii"),
            "sha256": sha(body), "size": len(body), "updated_at": STAMP}


def artifact_versions(*versions):
    return ("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<metadata modelVersion=\"1.1.0\">\n"
            "<groupId>org.example</groupId><artifactId>demo</artifactId><versioning>"
            "<versions>" + "".join("<version>" + value + "</version>" for value in versions) + "</versions>"
            "<lastUpdated>20261022000000</lastUpdated></versioning></metadata>\n").encode()


def snapshot_versions(build=1, base="2.0-SNAPSHOT", classifier=False):
    value = base[:-9] + "-20261022.000000-" + str(build)
    variants = [("pom", None), ("jar", None)] + ([("jar", "sources")] if classifier else [])
    return ("<metadata modelVersion=\"1.1.0\"><groupId>org.example</groupId><artifactId>demo</artifactId>"
            "<version>" + base + "</version><versioning><snapshot><timestamp>20261022.000000</timestamp>"
            "<buildNumber>" + str(build) + "</buildNumber></snapshot><lastUpdated>20261022000000</lastUpdated>"
            "<snapshotVersions>" + "".join("<snapshotVersion><extension>" + extension + "</extension>" +
                ("<classifier>" + kind + "</classifier>" if kind else "") +
                "<value>" + value + "</value><updated>20261022000000</updated></snapshotVersion>"
                for extension, kind in variants) + "</snapshotVersions></versioning></metadata>\n").encode()


def global_asset(path, body):
    coordinate = mod.parse_asset_path(path)
    return {"path": path, **{field: coordinate[field] for field in ("group_id", "artifact_id", "version", "media_type")},
            "sha256": sha(body), "size": len(body), "created_at": STAMP}


def feed(scope):
    return {"id": scope, "kind": "maven", "mode": "hosted", "visibility": "private"}


class MavenFixture(prior.Fixture):
    def __init__(self, root):
        super().__init__(root)
        self.manifest["schema"] = 4
        self.manifest["postgres"]["database_schema_version"] = 11
        self.manifest["postgres"]["table_counts"]["maven_metadata"] = 0
        self.manifest["postgres"]["table_fingerprints"]["maven_metadata"] = {"count": 0, "sha256": sha(b"")}
        self.manifest["maven_references"] = {"metadata": [], "global_assets": [], "feeds": []}

    def maven_asset(self, scope, path, body):
        row = global_asset(path, body)
        if scope:
            item = prior.asset(scope, "maven", row["group_id"] + ":" + row["artifact_id"],
                               row["version"], path, row["media_type"], body)
            self.add(item, body)
            if feed(scope) not in self.manifest["maven_references"]["feeds"]:
                self.manifest["maven_references"]["feeds"].append(feed(scope))
        else:
            key = "synthetic/maven/" + path
            blob_path = "objects/" + snapshot.unique_blob(key, "synthetic-v1") + ".bin"
            (self.root / blob_path).write_bytes(body)
            self.manifest["objects"].append({"key": key, "version": "synthetic-v1", "path": blob_path,
                "is_latest": True, "size": len(body), "sha256": sha(body)})
            self.manifest["maven_references"]["global_assets"].append(row)

    def metadata(self, scope, path, body):
        record = metadata_record(scope, path, body)
        self.manifest["maven_references"]["metadata"].append(record)
        return record

    def save(self):
        refs = self.manifest.get("maven_references", {})
        for field, table in (("metadata", "maven_metadata"), ("global_assets", "maven_assets"), ("feeds", "feeds")):
            count = len(refs.get(field, []))
            self.manifest["postgres"]["table_counts"][table] = count
            if table in self.manifest["postgres"]["table_fingerprints"]:
                self.manifest["postgres"]["table_fingerprints"][table]["count"] = count
        super().save()


class MavenMetadataValidationTests(unittest.TestCase):
    def test_snapshot_suffixed_artifact_uses_xml_identity_to_disambiguate_metadata(self):
        path = "org/example/demo-SNAPSHOT/maven-metadata.xml"
        raw = artifact_versions("1.0.0").replace(b"<artifactId>demo</artifactId>", b"<artifactId>demo-SNAPSHOT</artifactId>")
        document = mod.parse_metadata(path, raw)
        self.assertEqual(("org.example", "demo-SNAPSHOT", None),
                         (document["group_id"], document["artifact_id"], document["version"]))
        scoped = metadata_record("alpha-feed", path, raw)
        self.assertEqual(document, mod.metadata_record(scoped))
        with self.assertRaises(ValueError):
            mod.parse_metadata(path, artifact_versions("1.0.0"))

    def test_group_plugin_metadata_requires_same_scope_pom(self):
        path = "org/example/maven-metadata.xml"
        xml = (b"<metadata><groupId>org.example</groupId><plugins>"
               b"<plugin><prefix>hello</prefix><artifactId>hello-maven-plugin</artifactId></plugin>"
               b"</plugins></metadata>")
        parsed = mod.parse_metadata(path, xml)
        self.assertEqual("hello", parsed["plugins"][0]["prefix"])
        with self.assertRaises(ValueError):
            mod.parse_metadata(path, xml.replace(b"org.example", b"org.other"))
        with self.assertRaises(ValueError):
            mod.parse_metadata(path, xml.replace(b"</plugins>", b"<plugin><prefix>hello</prefix>"
                               b"<artifactId>duplicate-plugin</artifactId></plugin></plugins>"))
        pom_path = "org/example/hello-maven-plugin/1.0/hello-maven-plugin-1.0.pom"
        pom = b"<project/>"
        metadata = metadata_record("team-alpha", path, xml)
        asset = prior.asset("team-beta", "maven", "org.example:hello-maven-plugin",
                            "1.0", pom_path, "application/xml", pom)
        with self.assertRaises(ValueError):
            mod.validate_inventory([metadata], [], [asset], lambda *args: None,
                                   [feed("team-alpha"), feed("team-beta")])
        own = dict(asset, feed_id="team-alpha")
        mod.validate_inventory([metadata], [], [own], lambda *args: None, [feed("team-alpha")])

    def test_timestamped_snapshot_paths_and_primary_pom_are_canonical(self):
        path = BASE + "2.0-SNAPSHOT/demo-2.0-20261022.000000-1-sources.jar"
        self.assertEqual("sources", mod.parse_asset_path(path)["classifier"])
        invalid = (path.replace("20261022", "20260230"), path.replace("-1-sources", "-01-sources"),
                   path.replace("-1-sources", "-2147483648-sources"),
                   BASE + "2.0-SNAPSHOT/demo-2.0-SNAPSHOT.jar", path.replace("2.0-SNAPSHOT/", "3.0-SNAPSHOT/"),
                   path.replace("2.0-SNAPSHOT", "2.0-snapshot"))
        for candidate in invalid:
            with self.subTest(path=candidate), self.assertRaises(ValueError):
                mod.parse_asset_path(candidate)
        body = snapshot_versions(2)
        self.assertEqual(2, len(mod.parse_metadata(BASE + "2.0-SNAPSHOT/maven-metadata.xml", body)["files"]))
        with self.assertRaises(ValueError):
            mod.parse_metadata(BASE + "2.0-SNAPSHOT/maven-metadata.xml", body.replace(b"<buildNumber>2", b"<buildNumber>3"))

    def test_xml_shape_bounds_duplicate_identity_and_dtd_fail_closed(self):
        valid = artifact_versions("1.0.0")
        self.assertEqual(["1.0.0"], mod.parse_metadata(BASE + "maven-metadata.xml", valid)["versions"])
        mutations = (
            valid.replace(b"<groupId>org.example</groupId>", b"<groupId>org.example</groupId><groupId>org.example</groupId>"),
            valid.replace(b"<groupId>", b'<groupId xmlns="urn:foreign">'),
            valid.replace(b"<version>1.0.0</version>", b"<version>1.0.0</version><version>1.0.0</version>"),
            valid.replace(b"<version>1.0.0</version>", b"<version>1.0-snapshot</version>"),
            valid.replace(b"<versioning>", b"<versioning><url>https://example.test/</url>"),
            valid.replace(b"<versioning>", b"<versioning><release>9.0</release>"),
            valid.replace(b"20261022000000", b"20260230000000"),
            b'<!DOCTYPE metadata [<!ENTITY injected "org.example">]>' + valid.split(b"?>", 1)[1].replace(b"org.example", b"&injected;"),
            b'<!DOCTYPE metadata SYSTEM "file:///untrusted">' + valid.split(b"?>", 1)[1],
        )
        for body in mutations:
            with self.subTest(body=body[:50]), self.assertRaises(ValueError):
                mod.parse_metadata(BASE + "maven-metadata.xml", body)
        with self.assertRaises(ValueError):
            mod.parse_metadata(BASE + "maven-metadata.xml", artifact_versions(*("v" + str(i) for i in range(4097))))
        with self.assertRaises(ValueError):
            mod.parse_metadata(BASE + "maven-metadata.xml", b"<metadata>" * 20 + b"</metadata>" * 20)

    def test_raw_bytes_are_canonical_base64_and_match_both_sha_and_length(self):
        original = metadata_record("", BASE + "maven-metadata.xml", artifact_versions("1.0.0"))
        self.assertEqual("demo", mod.metadata_record(original)["artifact_id"])
        for field, value in (("size", True), ("size", original["size"] + 1), ("sha256", "0" * 64),
                             ("content_base64", original["content_base64"] + "\n"), ("scope", "../other")):
            with self.subTest(field=field), self.assertRaises(ValueError):
                mod.metadata_record({**original, field: value})

    def test_utf16_metadata_keeps_exact_bytes_and_cannot_bypass_dtd_rejection(self):
        raw = artifact_versions("1.0.0").decode().replace("UTF-8", "UTF-16").encode("utf-16")
        record = metadata_record("", BASE + "maven-metadata.xml", raw)
        self.assertEqual(["1.0.0"], mod.metadata_record(record)["versions"])
        self.assertEqual(raw, base64.b64decode(record["content_base64"]))
        text = raw.decode("utf-16")
        for declaration in ('<!DOCTYPE metadata [<!ENTITY injected "org.example">]>',
                            '<!DOCTYPE metadata SYSTEM "file:///untrusted">'):
            document = text.replace("?>", "?>" + declaration, 1)
            if "injected" in declaration:
                document = document.replace("<groupId>org.example", "<groupId>&injected;")
            with self.subTest(declaration=declaration), self.assertRaises(ValueError):
                mod.metadata_record(metadata_record("", BASE + "maven-metadata.xml", document.encode("utf-16")))


class MavenSnapshotTests(unittest.TestCase):
    def test_snapshot_reference_cannot_use_artifact_metadata_at_an_ambiguous_path(self):
        alternate = artifact_versions("1.0.0").replace(
            b"<groupId>org.example</groupId>", b"<groupId>org.example.demo</groupId>").replace(
            b"<artifactId>demo</artifactId>", b"<artifactId>2.0-SNAPSHOT</artifactId>")
        for scope in ("", "alpha-feed"):
            with self.subTest(scope=scope), tempfile.TemporaryDirectory() as folder:
                fixture = MavenFixture(Path(folder))
                fixture.maven_asset(scope, BASE + "2.0-SNAPSHOT/1.0.0/2.0-SNAPSHOT-1.0.0.pom", b"alternate POM")
                fixture.metadata(scope, BASE + "2.0-SNAPSHOT/maven-metadata.xml", alternate)
                fixture.save()
                snapshot.verify(fixture.root)
                fixture.metadata(scope, BASE + "maven-metadata.xml", artifact_versions("2.0-SNAPSHOT"))
                fixture.save()
                with self.assertRaises(snapshot.SnapshotError) as failure:
                    snapshot.verify(fixture.root)
                self.assertIn("different-coordinate snapshot metadata", str(failure.exception.__cause__))

    def populate(self, fixture):
        for scope in ("", "alpha-feed", "beta-feed"):
            fixture.maven_asset(scope, BASE + "1.0.0/demo-1.0.0.pom", ("release " + scope).encode())
            for build in (1, 2):
                for extension in ("pom", "jar"):
                    path = BASE + "2.0-SNAPSHOT/demo-2.0-20261022.000000-" + str(build) + "." + extension
                    fixture.maven_asset(scope, path, (scope + " " + path).encode())
            fixture.metadata(scope, BASE + "2.0-SNAPSHOT/maven-metadata.xml", snapshot_versions(2))
            fixture.metadata(scope, BASE + "maven-metadata.xml", artifact_versions("1.0.0", "2.0-SNAPSHOT"))
        fixture.save()

    def test_exact_global_and_two_private_generations_are_verified(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = MavenFixture(Path(directory))
            self.populate(fixture)
            saved = snapshot.verify(fixture.root)
            self.assertEqual(6, len(saved["maven_references"]["metadata"]))
            self.assertEqual(15, len(saved["objects"]))
            self.assertEqual(snapshot_versions(2), base64.b64decode(saved["maven_references"]["metadata"][0]["content_base64"]))

    def test_v11_cannot_downgrade_or_omit_metadata_fingerprints_or_inventory(self):
        for mutation in ("old-format", "no-table", "no-sidecar", "future-schema"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                fixture = MavenFixture(Path(directory))
                self.populate(fixture)
                if mutation == "old-format": fixture.manifest["schema"] = 3
                if mutation == "no-table": del fixture.manifest["postgres"]["table_fingerprints"]["maven_metadata"]
                if mutation == "no-sidecar": del fixture.manifest["maven_references"]
                if mutation == "future-schema": fixture.manifest["postgres"]["database_schema_version"] = 12
                fixture.save()
                with self.assertRaises(snapshot.SnapshotError):
                    snapshot.verify(fixture.root)

    def test_same_scope_references_and_compatible_feed_required_before_restore(self):
        for mutation in ("foreign", "global-fallback", "missing-pom", "missing-snapshot", "wrong-feed-kind", "metadata-digest", "corrupt-blob"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                fixture = MavenFixture(Path(directory))
                fixture.maven_asset("alpha-feed", BASE + "1.0.0/demo-1.0.0.pom", b"alpha-pom")
                fixture.maven_asset("", BASE + "1.0.0/demo-1.0.0.pom", b"global-pom")
                entry = fixture.metadata("alpha-feed", BASE + "maven-metadata.xml", artifact_versions("1.0.0"))
                if mutation == "foreign":
                    entry["scope"] = "beta-feed"
                    fixture.manifest["maven_references"]["feeds"].append(feed("beta-feed"))
                if mutation == "global-fallback":
                    fixture.manifest["feed_references"]["assets"].clear()
                if mutation == "missing-pom":
                    fixture.manifest["maven_references"]["metadata"][0] = metadata_record("alpha-feed", entry["path"], artifact_versions("2.0"))
                if mutation == "missing-snapshot":
                    fixture.manifest["maven_references"]["metadata"][0] = metadata_record("alpha-feed", entry["path"], artifact_versions("2.0-SNAPSHOT"))
                if mutation == "wrong-feed-kind": fixture.manifest["maven_references"]["feeds"][0]["kind"] = "raw"
                if mutation == "metadata-digest": entry["sha256"] = "0" * 64
                if mutation == "corrupt-blob":
                    (fixture.root / fixture.manifest["objects"][0]["path"]).write_bytes(b"changed")
                fixture.save()
                with patch.object(snapshot, "postgres") as postgres, patch.object(snapshot, "versioning_enabled") as s3:
                    with self.assertRaises(snapshot.SnapshotError):
                        snapshot.restore(fixture.root, object(), "synthetic-target")
                    postgres.assert_not_called()
                    s3.assert_not_called()

    def test_restored_metadata_must_equal_verified_sidecar(self):
        class Cursor:
            def __enter__(self): return self
            def __exit__(self, *_): return False
            def execute(self, _): pass
            def fetchone(self): return (0,)
        class Connection:
            def cursor(self): return Cursor()
        with tempfile.TemporaryDirectory() as directory:
            fixture = MavenFixture(Path(directory))
            fixture.save()
            with patch.dict(os.environ, {"PGDATABASE": "synthetic-target"}), \
                    patch.object(snapshot, "versioning_enabled", return_value=True), \
                    patch.object(snapshot, "inventory", return_value=([], [])), \
                    patch.object(snapshot, "postgres", return_value=nullcontext(Connection())), \
                    patch.object(snapshot, "restore_current_objects", return_value=0), \
                    patch.object(snapshot, "pg_command"), \
                    patch.object(snapshot, "database_schema_version", return_value=11), \
                    patch.object(snapshot, "counts", return_value=fixture.manifest["postgres"]["table_counts"]), \
                    patch.object(snapshot, "metadata_fingerprints", return_value=fixture.manifest["postgres"]["table_fingerprints"]), \
                    patch.object(snapshot, "feed_references", return_value=fixture.manifest["feed_references"]), \
                    patch.object(snapshot, "maven_references", return_value={"metadata": ["changed"]}):
                with self.assertRaisesRegex(snapshot.SnapshotError, "Restored Maven metadata references"):
                    snapshot.restore(fixture.root, object(), "synthetic-target")


class MavenOfflineTests(unittest.TestCase):
    def populate(self, root):
        root.mkdir()
        with sqlite3.connect(root / "packory.sqlite") as connection:
            connection.execute("CREATE TABLE packory_schema_version(slot INTEGER PRIMARY KEY,version INTEGER)")
            connection.execute("INSERT INTO packory_schema_version VALUES(1,11)")
            for table, columns in (("maven_metadata", mod.METADATA_COLUMNS), ("maven_assets", mod.ASSET_COLUMNS),
                                   ("feed_assets", snapshot.ASSET_COLUMNS), ("feeds", mod.FEED_COLUMNS)):
                connection.execute("CREATE TABLE " + table + " (" + ",".join(name + (" INTEGER" if name == "size" else " TEXT") for name in columns) + ")")
            path = BASE + "1.0.0/demo-1.0.0.pom"
            body = b"synthetic immutable POM bytes"
            asset = global_asset(path, body)
            connection.execute("INSERT INTO maven_assets VALUES(" + ",".join("?" for _ in mod.ASSET_COLUMNS) + ")",
                               tuple(asset[name] for name in mod.ASSET_COLUMNS))
            record = metadata_record("", BASE + "maven-metadata.xml", artifact_versions("1.0.0"))
            connection.execute("INSERT INTO maven_metadata VALUES(" + ",".join("?" for _ in mod.METADATA_COLUMNS) + ")",
                               tuple(record[name] for name in mod.METADATA_COLUMNS))
        blob = root / "blobs/maven" / path
        blob.parent.mkdir(parents=True)
        blob.write_bytes(body)
        return record, blob

    def test_v11_offline_backup_preserves_exact_metadata_and_immutable_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            record, blob = self.populate(root / "source")
            offline.create(root / "source", root / "backup")
            offline.restore(root / "backup", root / "restored")
            with sqlite3.connect(root / "restored/packory.sqlite") as connection:
                restored = connection.execute("SELECT content_base64,sha256 FROM maven_metadata").fetchone()
            self.assertEqual((record["content_base64"], record["sha256"]), restored)
            self.assertEqual(blob.read_bytes(), (root / "restored" / blob.relative_to(root / "source")).read_bytes())

    def test_rehashed_outer_backup_does_not_hide_invalid_metadata_or_missing_targets(self):
        for mutation in ("metadata", "target", "blob", "table", "future"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                _, blob = self.populate(root / "source")
                offline.create(root / "source", root / "backup")
                with sqlite3.connect(root / "backup/packory.sqlite") as connection:
                    if mutation == "metadata": connection.execute("UPDATE maven_metadata SET sha256=?", ("0" * 64,))
                    if mutation == "target": connection.execute("DELETE FROM maven_assets")
                    if mutation == "table": connection.execute("DROP TABLE maven_metadata")
                    if mutation == "future": connection.execute("UPDATE packory_schema_version SET version=12")
                if mutation == "blob":
                    (root / "backup" / blob.relative_to(root / "source")).write_bytes(b"changed")
                manifest = offline.manifest_for(root / "source")
                # Recompute every outer digest so the rejection proves semantic verification.
                manifest["entries"] = [{"path": path, "size": file.stat().st_size, "sha256": offline.digest(file)}
                                       for path, file in sorted(offline.files_in(root / "backup")) if path != "manifest.json"]
                (root / "backup/manifest.json").write_text(json.dumps(manifest))
                with self.assertRaises(ValueError):
                    offline.restore(root / "backup", root / "must-not-exist")
                self.assertFalse((root / "must-not-exist").exists())


if __name__ == "__main__":
    unittest.main()

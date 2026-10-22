"""Isolated real PostgreSQL + Moto S3 end-to-end snapshot/restore regression.

Synthetic schema/fixtures, disposable PostgreSQL databases and local S3 buckets.
This tests the tool's real pg_dump/pg_restore and object-version API paths,
but does NOT certify an external S3 provider or native Packory client reads.
"""
import importlib.util
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
import uuid

spec = importlib.util.spec_from_file_location(
    "pg_s3_snapshot", Path(__file__).resolve().parents[1] /
    "scripts" / "pg_s3_snapshot.py")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)

fixture_spec = importlib.util.spec_from_file_location(
    "pg_s3_feed_fixture", Path(__file__).with_name("test_pg_s3_snapshot_feeds.py"))
fixtures = importlib.util.module_from_spec(fixture_spec)
fixture_spec.loader.exec_module(fixtures)

maven_spec = importlib.util.spec_from_file_location(
    "pg_s3_maven_fixture", Path(__file__).with_name("test_maven_snapshot.py"))
maven_fixtures = importlib.util.module_from_spec(maven_spec)
maven_spec.loader.exec_module(maven_fixtures)


def tar_members(members):
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w:gz") as archive:
        for path, body in members:
            entry = tarfile.TarInfo(path)
            entry.size, entry.mtime, entry.mode = len(body), 0, 0o644
            archive.addfile(entry, io.BytesIO(body))
    return output.getvalue()


def private_objects():
    """Native archive shapes and OCI descriptors, with distinct bytes per feed."""
    objects, tags = [], []
    for feed in ("alpha-feed", "beta-feed"):
        marker = ("synthetic " + feed).encode()
        raw = marker + b" immutable raw"
        objects.append((fixtures.asset(feed + "-raw", "raw", "same.name", "1.0.0", "same.name/1.0.0", "application/octet-stream", raw), raw))
        pom = ("<project><modelVersion>4.0.0</modelVersion><groupId>org.example</groupId>"
               "<artifactId>demo</artifactId><version>1.0.0</version><description>" + feed + "</description></project>").encode()
        objects.append((fixtures.asset(feed + "-maven", "maven", "org.example:demo", "1.0.0",
            "org/example/demo/1.0.0/demo-1.0.0.pom", "application/xml", pom), pom))
        go_zip = fixtures.bundle([("example.test/hello@v1.0.0/go.mod", b"module example.test/hello\n"),
                                  ("example.test/hello@v1.0.0/hello.go", b"package hello\n// " + marker + b"\n")])
        go = fixtures.bundle([("info", json.dumps({"Version": "v1.0.0", "Time": fixtures.STAMP}).encode()),
                              ("mod", b"module example.test/hello\n"), ("zip", go_zip)])
        objects.append((fixtures.asset(feed + "-go", "go", "example.test/hello", "v1.0.0",
            "example.test/hello/@v/v1.0.0.zip", "application/vnd.packory.go-module-bundle+zip", go), go))
        npm = tar_members([("package/package.json", json.dumps({"name": "@scope/package", "version": "1.0.0", "description": feed}).encode()),
                           ("package/index.js", b"module.exports = " + json.dumps(feed).encode() + b";\n")])
        objects.append(fixtures.package(feed + "-npm", "npm", npm))
        cargo = tar_members([("synthetic-package-1.0.0+fixture/Cargo.toml", b'[package]\nname="synthetic-package"\nversion="1.0.0+fixture"\nedition="2021"\n'),
                             ("synthetic-package-1.0.0+fixture/src/lib.rs", b"// " + marker + b"\npub fn fixture() {}\n")])
        objects.append(fixtures.package(feed + "-cargo", "cargo", cargo))
        wheel = fixtures.bundle([
            ("synthetic_package/__init__.py", b"FEED = " + json.dumps(feed).encode() + b"\n"),
            ("synthetic_package-1.0.0.dist-info/METADATA", b"Metadata-Version: 2.1\nName: synthetic-package\nVersion: 1.0.0\n"),
            ("synthetic_package-1.0.0.dist-info/WHEEL", b"Wheel-Version: 1.0\nGenerator: synthetic-fixture\nRoot-Is-Purelib: true\nTag: py3-none-any\n"),
            ("synthetic_package-1.0.0.dist-info/RECORD", b"")])
        objects.append(fixtures.package(feed + "-pypi", "pypi", wheel))
        config = json.dumps({"architecture": "amd64", "os": "linux", "config": {"Labels": {"synthetic.feed": feed}}, "rootfs": {"type": "layers", "diff_ids": []}}).encode()
        digest = fixtures.sha(config)
        config_row = fixtures.asset(feed + "-oci", "oci", "team/image", "sha256:" + digest,
            "blob/team/image/" + digest, "application/octet-stream", config)
        objects.append((config_row, config))
        manifest = fixtures.image(config_row)
        digest = fixtures.sha(manifest)
        manifest_row = fixtures.asset(feed + "-oci", "oci", "team/image", "sha256:" + digest,
            "manifest/team/image/" + digest, "application/vnd.oci.image.manifest.v1+json", manifest)
        objects.append((manifest_row, manifest))
        tags.append({"feed_id": feed + "-oci", "name": "team/image", "tag": "latest", "digest": "sha256:" + digest, "updated_at": fixtures.STAMP})
    return objects, tags


def maven_objects():
    """Two immutable generations and current exact metadata in all three scopes."""
    public, private, metadata = [], [], []
    for scope in ("", "alpha-feed-maven", "beta-feed-maven"):
        for version, resolved in (("1.0.0", "1.0.0"),
                                  ("2.0-SNAPSHOT", "2.0-20261022.000000-1"),
                                  ("2.0-SNAPSHOT", "2.0-20261022.000000-2")):
            if scope and version == "1.0.0":
                continue  # Existing private_objects() release POM remains authoritative.
            for extension in (("pom",) if version == "1.0.0" else ("pom", "jar")):
                path = maven_fixtures.BASE + version + "/demo-" + resolved + "." + extension
                body = ("<project><groupId>org.example</groupId><artifactId>demo</artifactId><version>" +
                        version + "</version><description>synthetic " + scope + "</description></project>").encode()
                if extension == "jar":
                    body = fixtures.bundle([("fixture.txt", (scope + " " + resolved).encode())])
                row = maven_fixtures.global_asset(path, body)
                if scope:
                    private.append((fixtures.asset(scope, "maven", "org.example:demo", version,
                        path, row["media_type"], body), body))
                else:
                    public.append((row, body))
        metadata.append(maven_fixtures.metadata_record(scope,
            maven_fixtures.BASE + "2.0-SNAPSHOT/maven-metadata.xml", maven_fixtures.snapshot_versions(2)))
        metadata.append(maven_fixtures.metadata_record(scope,
            maven_fixtures.BASE + "maven-metadata.xml", maven_fixtures.artifact_versions("1.0.0", "2.0-SNAPSHOT")))
    return public, private, metadata


@unittest.skipUnless(
    os.getenv("PACKORY_TEST_S3") and os.getenv("PACKORY_TEST_POSTGRES"),
    "requires disposable PostgreSQL service and local S3 emulator")
class CoordinatedRestoreIntegrationTests(unittest.TestCase):
    def test_versioned_s3_and_schema_v4_postgres_restore_with_exact_row_hashes(self):
        self.coordinated_restore(4)

    def test_v10_private_bundles_and_oci_graph_restore_with_exact_feed_references(self):
        self.coordinated_restore(10)

    def test_v11_maven_snapshots_restore_exact_metadata_and_same_scope_references(self):
        self.coordinated_restore(11)

    def test_v14_restores_exact_npm_tag_tombstones_and_cargo_yank_states(self):
        self.coordinated_restore(14)

    def coordinated_restore(self, version):
        import boto3
        import psycopg
        from botocore.config import Config

        if not all(os.getenv(name) for name in ("PGHOST", "PGUSER", "PGDATABASE")):
            self.fail("Explicit disposable PostgreSQL environment required")

        # Only a synthetic CI PostgreSQL superuser may create/drop databases.
        # The random names are strictly alphanumeric and never taken from a URL.
        suffix = uuid.uuid4().hex[:16]
        source_db, target_db = "packory_src_" + suffix, "packory_dst_" + suffix
        source_bucket, target_bucket = "packory-src-" + suffix, "packory-dst-" + suffix
        prefix = "packory-fixture/"
        s3 = boto3.client(
            "s3", region_name="us-east-1", endpoint_url=os.environ["PACKORY_TEST_S3"],
            config=Config(s3={"addressing_style": "path"}))
        previous_database = os.environ["PGDATABASE"]
        databases_created, buckets_created = [], []
        feed_objects, feed_tags = private_objects() if version >= 10 else ([], [])
        public_maven, private_maven, maven_metadata = maven_objects() if version >= 11 else ([], [], [])
        feed_objects.extend(private_maven)

        try:
            with psycopg.connect("dbname=" + previous_database, autocommit=True) as admin:
                for name in (source_db, target_db):
                    admin.execute('CREATE DATABASE "' + name + '"')
                    databases_created.append(name)

            with psycopg.connect("dbname=" + source_db, autocommit=True) as source:
                tables = (*mod.CORE_TABLES, *mod.FEED_TABLES, *mod.DIGEST_TABLES, *mod.ISOLATED_FEED_TABLES,
                          *((*mod.RAW_TABLES, *mod.MAVEN_TABLES, *mod.GO_TABLES, *mod.SECURITY_REGISTRY_TABLES) if version >= 10 else ()),
                          *(mod.MAVEN_METADATA_TABLES if version >= 11 else ()),
                          *(mod.FEED_NPM_TAG_TABLES if version >= 13 else ()),
                          *(mod.FEED_CARGO_YANK_TABLES if version >= 14 else ()))
                for name in tables:
                    if ((version >= 10 and name == "feeds") or
                            (version >= 11 and name in ("maven_assets", "maven_metadata")) or
                            (version >= 13 and name == "feed_npm_tags") or
                            (version >= 14 and name == "feed_cargo_yanks")):
                        continue
                    source.execute(
                        "CREATE TABLE " + name + " (id TEXT PRIMARY KEY, payload TEXT NOT NULL)")
                    source.execute(
                        "INSERT INTO " + name + " (id,payload) VALUES (%s,%s)",
                        (name + "-one", "synthetic-" + name))
                source.execute(
                    "CREATE TABLE packory_schema_version (slot INTEGER PRIMARY KEY, version INTEGER)")
                source.execute(
                    "INSERT INTO packory_schema_version (slot,version) VALUES (1,%s)", (version,))
                if version >= 10:
                    source.execute("""CREATE TABLE feeds (
                        id TEXT PRIMARY KEY, kind TEXT NOT NULL, mode TEXT NOT NULL,
                        visibility TEXT NOT NULL, display_name TEXT NOT NULL, enabled INTEGER NOT NULL,
                        created_at TEXT NOT NULL, updated_at TEXT NOT NULL)""")
                    source.execute("""CREATE TABLE feed_assets (
                        feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
                        kind TEXT NOT NULL CHECK(kind IN ('raw','maven','go','npm','cargo','pypi','oci')),
                        path TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL,
                        media_type TEXT NOT NULL, sha256 TEXT NOT NULL,
                        size BIGINT NOT NULL CHECK(size > 0 OR (kind='oci' AND size=0)),
                        created_at TEXT NOT NULL, PRIMARY KEY(feed_id,kind,path))""")
                    source.execute("""CREATE TABLE feed_oci_tags (
                        feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
                        name TEXT NOT NULL, tag TEXT NOT NULL, digest TEXT NOT NULL, updated_at TEXT NOT NULL,
                        PRIMARY KEY(feed_id,name,tag))""")
                    for feed_id, kind in sorted({(item["feed_id"], item["kind"]) for item, _ in feed_objects}):
                        source.execute("INSERT INTO feeds VALUES (%s,%s,'hosted','private','Synthetic fixture',1,%s,%s)",
                                       (feed_id, kind, fixtures.STAMP, fixtures.STAMP))
                    for item, _ in feed_objects:
                        source.execute("INSERT INTO feed_assets (" + ",".join(mod.ASSET_COLUMNS) + ") VALUES (" + ",".join(["%s"] * len(mod.ASSET_COLUMNS)) + ")",
                                       tuple(item[name] for name in mod.ASSET_COLUMNS))
                    for item in feed_tags:
                        source.execute("INSERT INTO feed_oci_tags (" + ",".join(mod.OCI_TAG_COLUMNS) + ") VALUES (" + ",".join(["%s"] * len(mod.OCI_TAG_COLUMNS)) + ")",
                                       tuple(item[name] for name in mod.OCI_TAG_COLUMNS))
                if version >= 11:
                    source.execute("""CREATE TABLE maven_assets (
                        path TEXT PRIMARY KEY, group_id TEXT NOT NULL, artifact_id TEXT NOT NULL,
                        version TEXT NOT NULL, sha256 TEXT NOT NULL, size BIGINT NOT NULL,
                        media_type TEXT NOT NULL, created_at TEXT NOT NULL)""")
                    source.execute("""CREATE TABLE maven_metadata (
                        scope TEXT NOT NULL,path TEXT NOT NULL,content_base64 TEXT NOT NULL,
                        sha256 TEXT NOT NULL,size BIGINT NOT NULL,updated_at TEXT NOT NULL,
                        PRIMARY KEY(scope,path))""")
                    for item, _ in public_maven:
                        source.execute("INSERT INTO maven_assets (" + ",".join(mod.maven.ASSET_COLUMNS) + ") VALUES (" + ",".join(["%s"] * len(mod.maven.ASSET_COLUMNS)) + ")",
                                       tuple(item[name] for name in mod.maven.ASSET_COLUMNS))
                    for item in maven_metadata:
                        source.execute("INSERT INTO maven_metadata (" + ",".join(mod.maven.METADATA_COLUMNS) + ") VALUES (" + ",".join(["%s"] * len(mod.maven.METADATA_COLUMNS)) + ")",
                                       tuple(item[name] for name in mod.maven.METADATA_COLUMNS))
                if version >= 13:
                    source.execute("""CREATE TABLE feed_npm_tags (
                        feed_id TEXT NOT NULL REFERENCES feeds(id), name TEXT NOT NULL,
                        tag TEXT NOT NULL, version TEXT NOT NULL, deleted INTEGER NOT NULL,
                        updated_at TEXT NOT NULL, PRIMARY KEY(feed_id,name,tag))""")
                    for item, _ in feed_objects:
                        if item["kind"] == "npm":
                            for tag, target, deleted in (("latest", item["version"], 0), ("retired", "", 1)):
                                source.execute("INSERT INTO feed_npm_tags VALUES(%s,%s,%s,%s,%s,%s)",
                                               (item["feed_id"], item["name"], tag, target, deleted, fixtures.STAMP))
                if version >= 14:
                    source.execute("""CREATE TABLE feed_cargo_yanks (
                        feed_id TEXT NOT NULL REFERENCES feeds(id), name TEXT NOT NULL,
                        version TEXT NOT NULL, yanked INTEGER NOT NULL CHECK(yanked IN (0,1)),
                        updated_at TEXT NOT NULL, PRIMARY KEY(feed_id,name,version))""")
                    for item, _ in feed_objects:
                        if item["kind"] == "cargo":
                            source.execute("INSERT INTO feed_cargo_yanks VALUES(%s,%s,%s,%s,%s)",
                                           (item["feed_id"], item["name"], item["version"],
                                            int(item["feed_id"].startswith("alpha-")), fixtures.STAMP))
                before = mod.metadata_fingerprints(source)
                self.assertEqual(
                    set(before),
                    set((*tables, "packory_schema_version", *((*mod.FEED_ASSET_TABLES, *mod.FEED_OCI_TABLES) if version >= 10 else ()))))
                self.assertEqual(14 if version >= 10 else 1, before["feeds"]["count"])

            for bucket in (source_bucket, target_bucket):
                s3.create_bucket(Bucket=bucket)
                buckets_created.append(bucket)
                s3.put_bucket_versioning(
                    Bucket=bucket, VersioningConfiguration={"Status": "Enabled"})
                self.assertTrue(mod.versioning_enabled(s3, bucket))

            current_key = prefix + "nuget/synthetic/1.0.0.nupkg"
            quarantine_key = prefix + "recovery/quarantine/synthetic"
            removed_key = prefix + "removed-key"
            s3.put_object(Bucket=source_bucket, Key=current_key, Body=b"old-version")
            s3.put_object(Bucket=source_bucket, Key=current_key, Body=b"current-version")
            s3.put_object(Bucket=source_bucket, Key=quarantine_key, Body=b"quarantine")
            s3.put_object(Bucket=source_bucket, Key=removed_key, Body=b"removed")
            s3.delete_object(Bucket=source_bucket, Key=removed_key)
            for item, body in feed_objects:
                s3.put_object(Bucket=source_bucket, Key=prefix + mod.feed_asset_key(item), Body=body)
            for item, body in public_maven:
                s3.put_object(Bucket=source_bucket, Key=prefix + "maven/" + item["path"], Body=body)

            with tempfile.TemporaryDirectory(prefix="packory-pgs3-ci-") as folder:
                archive = Path(folder) / "snapshot"
                os.environ["PGDATABASE"] = source_db
                saved = mod.backup(
                    archive, s3, source_bucket, prefix, max_bytes=1024 * 1024)
                self.assertEqual(mod.SCHEMA, saved["schema"])
                self.assertEqual(4 + len(feed_objects) + len(public_maven), len(saved["objects"]))
                self.assertEqual(1, len(saved["delete_markers"]))
                self.assertEqual(before, saved["postgres"]["table_fingerprints"])
                self.assertEqual(version, saved["postgres"]["database_schema_version"])
                self.assertEqual(saved, mod.verify(archive))

                os.environ["PGDATABASE"] = target_db
                self.assertEqual(2 + len(feed_objects) + len(public_maven), mod.restore(archive, s3, target_bucket))
                with psycopg.connect("dbname=" + target_db, autocommit=True) as restored:
                    self.assertEqual(before, mod.metadata_fingerprints(restored))
                    if version >= 10:
                        self.assertEqual(saved["feed_references"], mod.feed_references(restored))
                        self.assertEqual(len(feed_objects), len(saved["feed_references"]["assets"]))
                        self.assertEqual(2, len(saved["feed_references"]["oci_tags"]))
                    if version >= 14:
                        references = mod.feed_references(restored)
                        self.assertEqual(4, len(references["npm_tags"]))
                        self.assertEqual(2, sum(row["deleted"] for row in references["npm_tags"]))
                        self.assertEqual([1, 0], [row["yanked"] for row in references["cargo_yanks"]])
                    if version >= 11:
                        self.assertEqual(saved["maven_references"], mod.maven_references(restored))
                        restored_metadata = mod.maven_references(restored)["metadata"]
                        self.assertEqual(sorted(maven_metadata, key=lambda row: (row["scope"], row["path"])), restored_metadata)
                    # Detect a post-restore modification which preserves row count.
                    if version >= 10:
                        restored.execute("UPDATE feeds SET display_name='Changed synthetic fixture' WHERE id='alpha-feed-npm'")
                    else:
                        restored.execute("UPDATE feeds SET payload=%s WHERE id=%s", ("tampered", "feeds-one"))
                    self.assertEqual(14 if version >= 10 else 1, mod.counts(restored)["feeds"])
                    self.assertNotEqual(before, mod.metadata_fingerprints(restored))

                self.assertEqual(
                    b"current-version",
                    s3.get_object(Bucket=target_bucket, Key=current_key)["Body"].read())
                self.assertEqual(
                    b"quarantine",
                    s3.get_object(Bucket=target_bucket, Key=quarantine_key)["Body"].read())
                with self.assertRaises(Exception):
                    s3.get_object(Bucket=target_bucket, Key=removed_key)
                for item, body in feed_objects:
                    self.assertEqual(body, s3.get_object(Bucket=target_bucket, Key=prefix + mod.feed_asset_key(item))["Body"].read())
                for item, body in public_maven:
                    self.assertEqual(body, s3.get_object(Bucket=target_bucket, Key=prefix + "maven/" + item["path"])["Body"].read())
                if version >= 10:
                    # Replacing one current package bundle with its namesake
                    # from a different feed must abort a new coordinated backup.
                    first = next((item, body) for item, body in feed_objects if item["feed_id"] == "alpha-feed-npm")
                    foreign = next(body for item, body in feed_objects if item["feed_id"] == "beta-feed-npm")
                    s3.put_object(Bucket=source_bucket, Key=prefix + mod.feed_asset_key(first[0]), Body=foreign)
                    os.environ["PGDATABASE"] = source_db
                    with self.assertRaises(mod.SnapshotError):
                        mod.backup(Path(folder) / "invalid-snapshot", s3, source_bucket, prefix, max_bytes=1024 * 1024)
                    self.assertFalse((Path(folder) / "invalid-snapshot").exists())
        finally:
            os.environ["PGDATABASE"] = previous_database
            for bucket in reversed(buckets_created):
                try:
                    paginator = s3.get_paginator("list_object_versions")
                    for page in paginator.paginate(Bucket=bucket):
                        for kind in ("Versions", "DeleteMarkers"):
                            for entry in page.get(kind, []):
                                s3.delete_object(
                                    Bucket=bucket, Key=entry["Key"], VersionId=entry["VersionId"])
                    s3.delete_bucket(Bucket=bucket)
                except Exception:
                    pass  # Disposable emulator cleanup, do not hide the test result.
            if databases_created:
                try:
                    with psycopg.connect(
                            "dbname=" + previous_database, autocommit=True) as admin:
                        for name in reversed(databases_created):
                            admin.execute('DROP DATABASE IF EXISTS "' + name + '" WITH (FORCE)')
                except Exception:
                    pass  # CI PostgreSQL service is discarded after the job.


if __name__ == "__main__":
    unittest.main()

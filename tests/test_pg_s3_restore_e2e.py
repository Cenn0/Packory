"""Isolated real PostgreSQL + Moto S3 end-to-end snapshot/restore regression.

Synthetic schema/fixtures, disposable PostgreSQL databases and local S3 buckets.
This tests the tool's real pg_dump/pg_restore and object-version API paths,
but does NOT certify an external S3 provider or native Packory client reads.
"""
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
import uuid

spec = importlib.util.spec_from_file_location(
    "pg_s3_snapshot", Path(__file__).resolve().parents[1] /
    "scripts" / "pg_s3_snapshot.py")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


@unittest.skipUnless(
    os.getenv("PACKORY_TEST_S3") and os.getenv("PACKORY_TEST_POSTGRES"),
    "requires disposable PostgreSQL service and local S3 emulator")
class CoordinatedRestoreIntegrationTests(unittest.TestCase):
    def test_versioned_s3_and_schema_v3_postgres_restore_with_exact_row_hashes(self):
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

        try:
            with psycopg.connect("dbname=" + previous_database, autocommit=True) as admin:
                for name in (source_db, target_db):
                    admin.execute('CREATE DATABASE "' + name + '"')
                    databases_created.append(name)

            with psycopg.connect("dbname=" + source_db, autocommit=True) as source:
                for name in (*mod.CORE_TABLES, *mod.FEED_TABLES, *mod.DIGEST_TABLES):
                    source.execute(
                        "CREATE TABLE " + name + " (id TEXT PRIMARY KEY, payload TEXT NOT NULL)")
                    source.execute(
                        "INSERT INTO " + name + " (id,payload) VALUES (%s,%s)",
                        (name + "-one", "synthetic-" + name))
                source.execute(
                    "CREATE TABLE packory_schema_version (slot INTEGER PRIMARY KEY, version INTEGER)")
                source.execute(
                    "INSERT INTO packory_schema_version (slot,version) VALUES (1,3)")
                before = mod.metadata_fingerprints(source)
                self.assertEqual(
                    set(before),
                    set((*mod.CORE_TABLES, "packory_schema_version",
                         *mod.FEED_TABLES, *mod.DIGEST_TABLES)))
                self.assertEqual(1, before["feeds"]["count"])

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

            with tempfile.TemporaryDirectory(prefix="packory-pgs3-ci-") as folder:
                archive = Path(folder) / "snapshot"
                os.environ["PGDATABASE"] = source_db
                saved = mod.backup(
                    archive, s3, source_bucket, prefix, max_bytes=1024 * 1024)
                self.assertEqual(mod.SCHEMA, saved["schema"])
                self.assertEqual(4, len(saved["objects"]))
                self.assertEqual(1, len(saved["delete_markers"]))
                self.assertEqual(before, saved["postgres"]["table_fingerprints"])
                self.assertEqual(3, saved["postgres"]["database_schema_version"])
                self.assertEqual(saved, mod.verify(archive))

                os.environ["PGDATABASE"] = target_db
                self.assertEqual(2, mod.restore(archive, s3, target_bucket))
                with psycopg.connect("dbname=" + target_db, autocommit=True) as restored:
                    self.assertEqual(before, mod.metadata_fingerprints(restored))
                    # Detect a post-restore modification which preserves row count.
                    restored.execute(
                        "UPDATE feeds SET payload=%s WHERE id=%s",
                        ("tampered", "feeds-one"))
                    self.assertEqual(1, mod.counts(restored)["feeds"])
                    self.assertNotEqual(before, mod.metadata_fingerprints(restored))

                self.assertEqual(
                    b"current-version",
                    s3.get_object(Bucket=target_bucket, Key=current_key)["Body"].read())
                self.assertEqual(
                    b"quarantine",
                    s3.get_object(Bucket=target_bucket, Key=quarantine_key)["Body"].read())
                with self.assertRaises(Exception):
                    s3.get_object(Bucket=target_bucket, Key=removed_key)
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

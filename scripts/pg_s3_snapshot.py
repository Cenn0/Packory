#!/usr/bin/env python3
"""Offline-only, hash-verified PostgreSQL/S3 snapshot and isolated restore.

This is an operator tool, not proof of S3 provider certification. It requires
application writes stopped, holds the Packory PostgreSQL writer advisory lock,
and stores every selected S3 object VERSION including quarantine evidence.
An isolated restore reconstructs current live objects; old version IDs, deletion
markers, retention/legal holds and object timestamps cannot be reproduced.
Never run the restore against a production database or bucket.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
from urllib.parse import urlsplit

SCHEMA = 2
LOCK = 0x5041434B4F525931  # PostgresSingleWriterLease.LockId
CORE_TABLES = ("packages", "npm_versions", "npm_tags", "cargo_versions", "pypi_files",
               "skills", "oci_blobs", "oci_manifests", "oci_tags", "oci_referrers",
               "publication_intents", "recovery_quarantines", "audit_events", "managed_tokens")
FEED_TABLES = ("feeds", "feed_namespaces", "feed_policies", "feed_upstreams")
DIGEST_TABLES = ("nuget_digests",)
ALLOWED_TABLES = frozenset((*CORE_TABLES, "packory_schema_version", *FEED_TABLES, *DIGEST_TABLES))
SHA = re.compile(r"[0-9a-f]{64}")
PAYLOAD = re.compile(r"objects/[0-9a-f]{64}\.bin")
MAX_MANIFEST_BYTES = 64 * 1024 * 1024


class SnapshotError(RuntimeError):
    pass


def checksum(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def now():
    return datetime.now(timezone.utc).isoformat()


def validate_endpoint(url, local_test=False):
    if not url:
        return None
    u = urlsplit(url)
    local_http = local_test and u.scheme == "http" and u.hostname in ("127.0.0.1", "localhost")
    if ((u.scheme != "https" and not local_http) or not u.hostname or
            u.username or u.password or u.query or u.fragment or u.path not in ("", "/")):
        raise SnapshotError("S3 endpoint must use HTTPS (loopback HTTP only with test-emulator option)")
    return url


def environment():
    for key in ("PGHOST", "PGUSER", "PGDATABASE"):
        if not os.getenv(key):
            raise SnapshotError("Missing PostgreSQL libpq environment variables (PGHOST, PGUSER, PGDATABASE)")
    return os.environ.copy()


def client(endpoint, region, test_emulator=False):
    import boto3  # operator-only dependency; AWS workload credential chain
    return boto3.client("s3", region_name=region,
                        endpoint_url=validate_endpoint(endpoint, test_emulator))


def postgres():
    import psycopg  # operator-only dependency; libpq environment
    con = psycopg.connect("", autocommit=True)
    held = con.execute("SELECT pg_try_advisory_lock(%s)", (LOCK,)).fetchone()[0]
    if not held:
        con.close()
        raise SnapshotError("Packory writer is still active; stop it before taking a snapshot or restoring")
    return con


def metadata_tables(connection):
    """Select all authoritative tables for a supported Packory schema version."""
    marker = connection.execute(
        "SELECT to_regclass('packory_schema_version')").fetchone()[0]
    if marker is None:
        return CORE_TABLES
    row = connection.execute(
        "SELECT version FROM packory_schema_version WHERE slot=1").fetchone()
    if not row or type(row[0]) is not int or row[0] not in (1, 2, 3):
        raise SnapshotError("Unknown Packory database schema version")
    return (CORE_TABLES + ("packory_schema_version",) +
            (FEED_TABLES if row[0] >= 2 else ()) +
            (DIGEST_TABLES if row[0] >= 3 else ()))


def counts(connection):
    return {name: connection.execute(
        "SELECT COUNT(*) FROM " + name).fetchone()[0]
        for name in metadata_tables(connection)}


def metadata_fingerprints(connection, table_names=None):
    """Hash canonical rows; persist no raw row values or identities in evidence."""
    tables = metadata_tables(connection) if table_names is None else tuple(table_names)
    if not tables or len(set(tables)) != len(tables) or not set(tables) <= ALLOWED_TABLES:
        raise SnapshotError("Unexpected metadata table in fingerprint request")
    fingerprints = {}
    for name in tables:
        digest = hashlib.sha256()
        count = 0
        sql = 'SELECT row_to_json(r)::text COLLATE "C" FROM ' + name + ' AS r ORDER BY 1'
        for row in connection.execute(sql):
            payload = row[0].encode("utf-8")
            digest.update(len(payload).to_bytes(8, "big"))
            digest.update(payload)
            count += 1
        fingerprints[name] = {"count": count, "sha256": digest.hexdigest()}
    return fingerprints


def pg_command(operation, filename):
    executable = shutil.which("pg_dump" if operation == "backup" else "pg_restore")
    if not executable:
        raise SnapshotError("Install a compatible PostgreSQL client (pg_dump / pg_restore)")
    if operation == "backup":
        command = [executable, "--format=custom", "--no-owner", "--no-acl",
                   "--file=" + str(filename)]
    else:
        command = [executable, "--no-owner", "--no-acl", "--single-transaction",
                   "--exit-on-error", "--dbname=" + os.environ["PGDATABASE"], str(filename)]
    completed = subprocess.run(command, env=environment(), stdout=subprocess.DEVNULL,
                               stderr=subprocess.DEVNULL, check=False)
    if completed.returncode:
        raise SnapshotError(f"{operation}: PostgreSQL client failed (check its installation/version and target state)")


def versioning_enabled(s3, bucket):
    return s3.get_bucket_versioning(Bucket=bucket).get("Status") == "Enabled"


def plain_identifier(value, maximum=1024):
    return (isinstance(value, str) and bool(value) and
            len(value.encode("utf-8")) <= maximum and
            not any(ord(char) < 32 or ord(char) == 127 for char in value))


def valid_prefix(prefix):
    return (plain_identifier(prefix) and prefix.endswith("/") and
            not prefix.startswith("/") and ".." not in prefix.split("/") and
            "." not in prefix.split("/") and "//" not in prefix and "\\" not in prefix)


def valid_key(key, prefix=None):
    return (plain_identifier(key) and not key.startswith("/") and
            "\\" not in key and all(part not in ("", ".", "..") for part in key.split("/")) and
            (prefix is None or key.startswith(prefix)))


def nonnegative_integer(value):
    return type(value) is int and value >= 0


def digest_string(value):
    return isinstance(value, str) and SHA.fullmatch(value) is not None


def inventory(s3, bucket, prefix):
    if not valid_prefix(prefix):
        raise SnapshotError("A safe, non-empty, slash-terminated Packory S3 prefix is required")
    versions, markers = [], []
    for page in s3.get_paginator("list_object_versions").paginate(Bucket=bucket, Prefix=prefix):
        for field, destination in (("Versions", versions), ("DeleteMarkers", markers)):
            for item in page.get(field, []):
                # Never silently omit a provider response outside the requested prefix.
                if not isinstance(item, dict) or not valid_key(item.get("Key"), prefix):
                    raise SnapshotError("S3 returned an invalid or out-of-prefix inventory key")
                destination.append(item)
    inventory_signature(versions, markers, prefix)
    return versions, markers


def inventory_signature(versions, markers, prefix):
    """Compare exact identities, latest markers, ETags and sizes, independent of page order."""
    normalized = []
    seen = set()
    latest = {}
    for kind, entries in (("object", versions), ("delete", markers)):
        for item in entries:
            key, version = item.get("Key"), item.get("VersionId")
            current = item.get("IsLatest")
            if (not valid_key(key, prefix) or not plain_identifier(version) or
                    type(current) is not bool or (key, version) in seen):
                raise SnapshotError("Invalid or duplicate S3 version inventory")
            seen.add((key, version))
            latest[key] = latest.get(key, 0) + int(current)
            size, etag = item.get("Size"), item.get("ETag")
            if ((size is not None and not nonnegative_integer(size)) or
                    (etag is not None and not plain_identifier(etag))):
                raise SnapshotError("Invalid S3 inventory object attributes")
            normalized.append((key, version, kind, current, size, etag))
    if any(count != 1 for count in latest.values()):
        raise SnapshotError("S3 inventory has conflicting or missing current version markers")
    return sorted(normalized, key=lambda item: item[:3])


def unique_blob(key, version):
    return hashlib.sha256((key + "\0" + version).encode("utf-8")).hexdigest()


def capture_object_versions(s3, bucket, prefix, staged, max_bytes, source_inventory=None):
    if not nonnegative_integer(max_bytes) or max_bytes == 0:
        raise SnapshotError("Invalid maximum snapshot size")
    versions, markers = inventory(s3, bucket, prefix) if source_inventory is None else source_inventory
    inventory_signature(versions, markers, prefix)
    entries = []
    total = 0
    (staged / "objects").mkdir()
    for item in sorted(versions, key=lambda v: (v["Key"], v["VersionId"])):
        key, version = item["Key"], item["VersionId"]
        local = "objects/" + unique_blob(key, version) + ".bin"
        path = staged / local
        if path.exists():
            raise SnapshotError("Duplicate S3 object version in inventory")
        response = s3.get_object(Bucket=bucket, Key=key, VersionId=version)
        digest = hashlib.sha256()
        length = 0
        try:
            advertised = response.get("ContentLength")
            if (not nonnegative_integer(advertised) or advertised > max_bytes - total or
                    ("VersionId" in response and response["VersionId"] != version)):
                raise SnapshotError("Unexpected S3 object length or version in snapshot response")
            with open(path, "xb") as output:
                for block in iter(lambda: response["Body"].read(1024 * 1024), b""):
                    length += len(block)
                    if length > max_bytes - total or length > advertised:
                        raise SnapshotError("Snapshot object capacity or advertised length exceeded")
                    digest.update(block)
                    output.write(block)
        finally:
            response["Body"].close()
        if length != advertised or ("Size" in item and length != item["Size"]):
            raise SnapshotError("S3 object changed or was truncated during snapshot")
        total += length
        entries.append({"key": key, "version": version, "is_latest": item["IsLatest"],
                        "size": length, "sha256": digest.hexdigest(), "path": local})
    deletes = [{"key": item["Key"], "version": item["VersionId"],
                "is_latest": item["IsLatest"]} for item in markers]
    return entries, deletes, total


def safe_files(folder):
    if folder.is_symlink() or not folder.is_dir():
        raise SnapshotError("Snapshot root must be a non-symlink directory")
    def failed(_error):
        raise SnapshotError("Snapshot directory could not be enumerated")
    for root, dirs, files in os.walk(folder, followlinks=False, onerror=failed):
        for name in dirs:
            if (Path(root) / name).is_symlink():
                raise SnapshotError("Symlinks in snapshot are prohibited")
        for name in files:
            path = Path(root) / name
            info = path.lstat()
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
                raise SnapshotError("Snapshot requires regular non-linked files")
            yield path.relative_to(folder).as_posix()


def validate_object_manifest(manifest):
    """Validate the complete object/delete-marker selection before any restore write."""
    entries, markers = manifest.get("objects"), manifest.get("delete_markers")
    if not isinstance(entries, list) or not isinstance(markers, list):
        raise SnapshotError("Invalid object inventory")
    prefix = manifest.get("source", {}).get("prefix")
    if prefix is not None and not valid_prefix(prefix):
        raise SnapshotError("Unsafe snapshot prefix")
    seen, paths, latest = set(), set(), {}
    for kind, items in (("object", entries), ("delete", markers)):
        for item in items:
            if not isinstance(item, dict):
                raise SnapshotError("Invalid snapshot inventory record")
            key, version, current = item.get("key"), item.get("version"), item.get("is_latest")
            if (not valid_key(key, prefix) or not plain_identifier(version) or
                    type(current) is not bool or (key, version) in seen):
                raise SnapshotError("Invalid or duplicate object/delete-marker identity")
            seen.add((key, version))
            latest[key] = latest.get(key, 0) + int(current)
            if kind == "object":
                path = item.get("path")
                if (not isinstance(path, str) or not PAYLOAD.fullmatch(path) or
                        path != "objects/" + unique_blob(key, version) + ".bin" or path in paths or
                        not digest_string(item.get("sha256")) or not nonnegative_integer(item.get("size"))):
                    raise SnapshotError("Invalid object payload description")
                paths.add(path)
    if any(value != 1 for value in latest.values()):
        raise SnapshotError("Conflicting or missing latest object/delete-marker selection")
    if "snapshot_bytes" in manifest and (
            not nonnegative_integer(manifest["snapshot_bytes"]) or
            manifest["snapshot_bytes"] != sum(item["size"] for item in entries)):
        raise SnapshotError("Snapshot size accounting disagrees with inventory")
    return paths


def unique_json(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise SnapshotError("Duplicate JSON manifest fields are prohibited")
        result[key] = value
    return result


def invalid_json_constant(_value):
    raise SnapshotError("Non-finite JSON manifest numbers are prohibited")


def verify(folder):
    folder = Path(folder)
    try:
        actual_files = set(safe_files(folder))
        manifest_path = folder / "manifest.json"
        if "manifest.json" not in actual_files or manifest_path.stat().st_size > MAX_MANIFEST_BYTES:
            raise SnapshotError("Snapshot manifest missing or too large")
        with open(manifest_path, "rb") as stream:
            payload = stream.read(MAX_MANIFEST_BYTES + 1)
        if len(payload) > MAX_MANIFEST_BYTES:
            raise SnapshotError("Snapshot manifest too large")
        manifest = json.loads(payload, object_pairs_hook=unique_json, parse_constant=invalid_json_constant)
        if (not isinstance(manifest, dict) or type(manifest.get("schema")) is not int or
                manifest["schema"] not in (1, SCHEMA) or manifest.get("profile") != "postgresql-s3-offline"):
            raise SnapshotError("Unknown snapshot format")
        pg = manifest["postgres"]
        if (not isinstance(pg, dict) or pg.get("path") != "metadata.dump" or
                not digest_string(pg.get("sha256")) or not nonnegative_integer(pg.get("size"))):
            raise SnapshotError("Invalid PostgreSQL manifest")
        table_counts = pg["table_counts"]
        if (not isinstance(table_counts, dict) or not set(table_counts) <= ALLOWED_TABLES or
                any(not nonnegative_integer(count) for count in table_counts.values())):
            raise SnapshotError("Invalid PostgreSQL row counts")
        if manifest["schema"] == SCHEMA:
            fingerprints, db_version = pg["table_fingerprints"], pg["database_schema_version"]
            if type(db_version) is not int or db_version not in (0, 1, 2, 3):
                raise SnapshotError("Unknown Packory database schema version")
            required = set(CORE_TABLES)
            if db_version in (1, 2, 3):
                required.add("packory_schema_version")
            if db_version >= 2:
                required.update(FEED_TABLES)
            if db_version >= 3:
                required.update(DIGEST_TABLES)
            if (not isinstance(fingerprints, dict) or set(fingerprints) != required or
                    set(fingerprints) != set(table_counts) or
                    any(not isinstance(entry, dict) or not nonnegative_integer(entry.get("count")) or
                        not digest_string(entry.get("sha256")) or table_counts[name] != entry["count"]
                        for name, entry in fingerprints.items())):
                raise SnapshotError("Invalid or incomplete PostgreSQL row fingerprints")
        source = manifest["source"]
        if (not isinstance(source, dict) or not valid_prefix(source.get("prefix")) or
                not plain_identifier(source.get("bucket")) or not plain_identifier(source.get("database"))):
            raise SnapshotError("Unsafe or incomplete snapshot source identifiers")
        expected = {"manifest.json", "metadata.dump"} | validate_object_manifest(manifest)
        if actual_files != expected:
            raise SnapshotError("Snapshot contains missing or unexpected files")
        if (folder / "metadata.dump").stat().st_size != pg["size"] or checksum(folder / "metadata.dump") != pg["sha256"]:
            raise SnapshotError("PostgreSQL dump integrity failure")
        for entry in manifest["objects"]:
            path = folder / entry["path"]
            if path.stat().st_size != entry["size"] or checksum(path) != entry["sha256"]:
                raise SnapshotError("S3 object version checksum mismatch")
        return manifest
    except (KeyError, TypeError, ValueError, OSError, UnicodeError, RecursionError) as error:
        raise SnapshotError("Snapshot manifest or file integrity invalid") from error


def backup(directory, s3, bucket, prefix, max_bytes):
    directory = Path(directory)
    if directory.exists() or directory.is_symlink():
        raise SnapshotError("Backup destination must not already exist")
    if not versioning_enabled(s3, bucket):
        raise SnapshotError("S3 bucket versioning must be enabled to snapshot all object versions")
    with postgres() as pg:
        tables = metadata_tables(pg)
        initial = metadata_fingerprints(pg, tables)
        original_inventory = inventory(s3, bucket, prefix)
        original_signature = inventory_signature(*original_inventory, prefix)
        database = os.environ["PGDATABASE"]
        directory.parent.mkdir(parents=True, exist_ok=True)
        staged = Path(tempfile.mkdtemp(prefix=".packory-pgs3-", dir=directory.parent))
        try:
            pg_command("backup", staged / "metadata.dump")
            entries, markers, total = capture_object_versions(
                s3, bucket, prefix, staged, max_bytes, source_inventory=original_inventory)
            final = metadata_fingerprints(pg, tables)
            if final != initial or metadata_tables(pg) != tables:
                raise SnapshotError("Metadata changed during supposedly offline snapshot")
            if (not versioning_enabled(s3, bucket) or
                    inventory_signature(*inventory(s3, bucket, prefix), prefix) != original_signature):
                raise SnapshotError("S3 inventory changed during supposedly offline snapshot")
            manifest = {
                "schema": SCHEMA, "profile": "postgresql-s3-offline", "created_utc": now(),
                "source": {"bucket": bucket, "prefix": prefix, "database": database},
                "postgres": {"path": "metadata.dump", "size": (staged / "metadata.dump").stat().st_size,
                             "sha256": checksum(staged / "metadata.dump"),
                             "table_counts": {name: value["count"] for name, value in final.items()},
                             "table_fingerprints": final,
                             "database_schema_version": (3 if "nuget_digests" in tables else
                                                         2 if "feeds" in tables else
                                                         1 if "packory_schema_version" in tables else 0)},
                "objects": entries, "delete_markers": markers,
                "snapshot_bytes": total,
                "historical_versions_retained_in_backup": True,
                "restored_object_versions_and_retention_equivalent": False,
            }
            (staged / "manifest.json").write_text(
                json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
            verify(staged)
            staged.rename(directory)
            return manifest
        finally:
            if staged.exists():
                shutil.rmtree(staged)


def restore_current_objects(s3, target_bucket, manifest, directory):
    # Selection is validated before the first write, including delete-marker identities.
    validate_object_manifest(manifest)
    directory = Path(directory)
    live = [entry for entry in manifest["objects"] if entry["is_latest"]]
    for item in sorted(live, key=lambda entry: entry["key"]):
        path = directory / item["path"]
        with open(path, "rb") as data:
            s3.put_object(Bucket=target_bucket, Key=item["key"], Body=data,
                          IfNoneMatch="*", ContentType="application/octet-stream")
        actual = s3.get_object(Bucket=target_bucket, Key=item["key"])
        digest, length = hashlib.sha256(), 0
        try:
            if (not nonnegative_integer(actual.get("ContentLength")) or
                    actual["ContentLength"] != item["size"]):
                raise SnapshotError("Target S3 object length mismatch: aborting PostgreSQL restore")
            for block in iter(lambda: actual["Body"].read(1024 * 1024), b""):
                length += len(block)
                if length > item["size"]:
                    raise SnapshotError("Target S3 object exceeded expected length")
                digest.update(block)
        finally:
            actual["Body"].close()
        if length != item["size"] or digest.hexdigest() != item["sha256"]:
            raise SnapshotError("Target S3 object checksum mismatch: aborting PostgreSQL restore")
    return len(live)


def restore(directory, s3, target_bucket):
    manifest = verify(Path(directory))
    source = manifest["source"]
    if source["bucket"] == target_bucket:
        raise SnapshotError("Restore bucket must differ from the source bucket")
    if os.environ["PGDATABASE"] == source["database"]:
        raise SnapshotError("Restore database must differ from the source database")
    if not versioning_enabled(s3, target_bucket):
        raise SnapshotError("Restore S3 bucket must have versioning enabled")
    versions, markers = inventory(s3, target_bucket, source["prefix"])
    if versions or markers:
        raise SnapshotError("Restore bucket prefix must be completely empty (including prior versions)")
    with postgres() as pg:
        with pg.cursor() as cur:
            cur.execute("""
                SELECT COUNT(*) FROM pg_catalog.pg_tables
                WHERE schemaname NOT IN ('pg_catalog','information_schema')
                """)
            if cur.fetchone()[0] != 0:
                raise SnapshotError("Restore PostgreSQL database must be empty")
        restored = restore_current_objects(s3, target_bucket, manifest, Path(directory))
        final_versions, final_markers = inventory(s3, target_bucket, source["prefix"])
        expected_keys = {item["key"] for item in manifest["objects"] if item["is_latest"]}
        if (final_markers or len(final_versions) != restored or
                {item["Key"] for item in final_versions} != expected_keys or
                any(not item["IsLatest"] for item in final_versions)):
            raise SnapshotError("Restore destination inventory changed before PostgreSQL restore")
        pg_command("restore", Path(directory) / "metadata.dump")
        expected, actual = manifest["postgres"]["table_counts"], counts(pg)
        if {name: actual.get(name) for name in expected} != expected:
            raise SnapshotError("Restored PostgreSQL row counts disagree with snapshot")
        if manifest["schema"] == SCHEMA:
            if metadata_fingerprints(pg) != manifest["postgres"]["table_fingerprints"]:
                raise SnapshotError("Restored PostgreSQL row values disagree with snapshot")
        return restored


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("backup", "verify", "restore"))
    parser.add_argument("--snapshot", required=True, type=Path)
    parser.add_argument("--bucket")
    parser.add_argument("--prefix")
    parser.add_argument("--target-bucket")
    parser.add_argument("--endpoint", default=os.getenv("PACKORY_S3_ENDPOINT"))
    parser.add_argument("--region", default=os.getenv("PACKORY_S3_REGION", "us-east-1"))
    parser.add_argument("--max-bytes", type=int, default=100 * 1024 ** 3)
    parser.add_argument("--writes-stopped-confirmed", action="store_true")
    parser.add_argument("--isolated-restore-confirmed", action="store_true")
    parser.add_argument("--local-test-emulator", action="store_true")
    args = parser.parse_args()
    try:
        if args.operation == "verify":
            report = verify(args.snapshot)
            print(f"Verified offline snapshot: {len(report['objects'])} saved S3 versions.")
            return 0
        if not args.writes_stopped_confirmed:
            raise SnapshotError("Stop all Packory writes and explicitly confirm the offline window")
        environment()
        if args.max_bytes <= 0:
            raise SnapshotError("Invalid maximum snapshot size")
        s3 = client(args.endpoint, args.region, args.local_test_emulator)
        if args.operation == "backup":
            if not args.bucket or not args.prefix:
                raise SnapshotError("Source --bucket and --prefix are required")
            report = backup(args.snapshot, s3, args.bucket, args.prefix, args.max_bytes)
            print(f"Offline snapshot verified: {len(report['objects'])} object versions archived.")
        else:
            if not args.isolated_restore_confirmed or not args.target_bucket:
                raise SnapshotError("Restore requires isolated-target confirmation and destination bucket")
            number = restore(args.snapshot, s3, args.target_bucket)
            print(f"Isolated restore checked: {number} current S3 objects copied; database " +
                  ("rows and SHA-256 fingerprints checked." if verify(args.snapshot)["schema"] == SCHEMA
                   else "row counts only (legacy snapshot; NOT full metadata-integrity evidence)."))
        return 0
    except Exception as error:
        # Terminal CLI boundary: SDK and libpq exceptions can contain credentials
        # or private endpoint names. Do not print exception repr or tracebacks.
        if isinstance(error, SnapshotError):
            print("ERROR: " + str(error), file=sys.stderr)
        else:
            print("ERROR: offline snapshot/restore failed; check the private operator environment", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

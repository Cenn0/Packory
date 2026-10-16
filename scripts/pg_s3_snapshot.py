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
import subprocess
import sys
import tempfile
from urllib.parse import urlsplit

SCHEMA = 1
LOCK = 0x5041434B4F525931  # PostgresSingleWriterLease.LockId
TABLES = ("packages", "npm_versions", "npm_tags", "cargo_versions", "pypi_files",
          "skills", "oci_blobs", "oci_manifests", "oci_tags", "oci_referrers",
          "publication_intents", "recovery_quarantines", "audit_events", "managed_tokens")
SHA = re.compile(r"^[0-9a-f]{64}$")
PAYLOAD = re.compile(r"^objects/[0-9a-f]{64}\.bin$")


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


def counts(connection):
    result = {}
    for name in TABLES:
        # Fixed identifiers, not user input. A missing table is a hard error.
        result[name] = connection.execute("SELECT COUNT(*) FROM " + name).fetchone()[0]
    return result


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
    # Never include PG credentials in argv, stdout, reports or logs.
    completed = subprocess.run(command, env=environment(), stdout=subprocess.DEVNULL,
                               stderr=subprocess.DEVNULL, check=False)
    if completed.returncode:
        raise SnapshotError(f"{operation}: PostgreSQL client failed (check its installation/version and target state)")


def versioning_enabled(s3, bucket):
    return s3.get_bucket_versioning(Bucket=bucket).get("Status") == "Enabled"


def inventory(s3, bucket, prefix):
    if not isinstance(prefix, str) or not prefix or prefix.startswith("/") or ".." in prefix.split("/"):
        raise SnapshotError("A safe, non-empty Packory S3 prefix is required")
    paginator = s3.get_paginator("list_object_versions")
    versions, markers = [], []
    for page in paginator.paginate(Bucket=bucket, Prefix=prefix):
        for item in page.get("Versions", []):
            if item["Key"].startswith(prefix):
                versions.append(item)
        for item in page.get("DeleteMarkers", []):
            if item["Key"].startswith(prefix):
                markers.append(item)
    return versions, markers


def unique_blob(key, version):
    return hashlib.sha256((key + "\0" + version).encode("utf-8")).hexdigest()


def capture_object_versions(s3, bucket, prefix, staged, max_bytes):
    versions, markers = inventory(s3, bucket, prefix)
    entries = []
    total = 0
    (staged / "objects").mkdir()
    for item in sorted(versions, key=lambda v: (v["Key"], str(v["VersionId"]))):
        key, version = item["Key"], str(item["VersionId"])
        if len(key) > 1024 or not version or len(version) > 1024:
            raise SnapshotError("Invalid S3 version or object identifier")
        local = "objects/" + unique_blob(key, version) + ".bin"
        path = staged / local
        if path.exists():
            raise SnapshotError("Duplicate S3 object version in inventory")
        response = s3.get_object(Bucket=bucket, Key=key, VersionId=version)
        digest = hashlib.sha256()
        length = 0
        try:
            with open(path, "xb") as output:
                for block in iter(lambda: response["Body"].read(1024 * 1024), b""):
                    length += len(block)
                    if length > max_bytes - total:
                        raise SnapshotError("Snapshot object capacity limit reached")
                    digest.update(block)
                    output.write(block)
        finally:
            response["Body"].close()
        if length != int(response.get("ContentLength", -1)):
            raise SnapshotError("S3 object changed or was truncated during snapshot")
        total += length
        entries.append({"key": key, "version": version, "is_latest": bool(item.get("IsLatest")),
                        "size": length, "sha256": digest.hexdigest(), "path": local})
    deletes = [{"key": item["Key"], "version": str(item["VersionId"]),
                "is_latest": bool(item.get("IsLatest"))} for item in markers]
    return entries, deletes, total


def safe_files(folder):
    for root, dirs, files in os.walk(folder, followlinks=False):
        for name in dirs + files:
            if (Path(root) / name).is_symlink():
                raise SnapshotError("Symlinks in snapshot are prohibited")
        for name in files:
            yield (Path(root) / name).relative_to(folder).as_posix()


def verify(folder):
    try:
        manifest = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
        if manifest.get("schema") != SCHEMA or manifest.get("profile") != "postgresql-s3-offline":
            raise SnapshotError("Unknown snapshot format")
        pg = manifest["postgres"]
        if pg["path"] != "metadata.dump" or not SHA.fullmatch(pg["sha256"]):
            raise SnapshotError("Invalid PostgreSQL manifest")
        entries = manifest["objects"]
        if not isinstance(entries, list) or not isinstance(manifest["delete_markers"], list):
            raise SnapshotError("Invalid object inventory")
        expected = {"manifest.json", "metadata.dump"}
        seen = set()
        for entry in entries:
            path, key, version = entry["path"], entry["key"], entry["version"]
            if (not isinstance(path, str) or not PAYLOAD.fullmatch(path) or
                not isinstance(key, str) or not key.startswith(manifest["source"]["prefix"]) or
                not isinstance(version, str) or
                path != "objects/" + unique_blob(key, version) + ".bin" or
                not SHA.fullmatch(entry["sha256"]) or
                not isinstance(entry["size"], int) or entry["size"] < 0 or
                (key, version) in seen or path in expected):
                raise SnapshotError("Invalid or duplicate object version manifest entry")
            seen.add((key, version))
            expected.add(path)
        if set(safe_files(folder)) != expected:
            raise SnapshotError("Snapshot contains missing or unexpected files")
        if checksum(folder / "metadata.dump") != pg["sha256"] or (
                folder / "metadata.dump").stat().st_size != pg["size"]:
            raise SnapshotError("PostgreSQL dump integrity failure")
        for entry in entries:
            path = folder / entry["path"]
            if path.stat().st_size != entry["size"] or checksum(path) != entry["sha256"]:
                raise SnapshotError("S3 object version checksum mismatch")
        return manifest
    except (KeyError, TypeError, ValueError, OSError, json.JSONDecodeError) as error:
        raise SnapshotError("Snapshot manifest or file integrity invalid") from error


def backup(directory, s3, bucket, prefix, max_bytes):
    directory = Path(directory)
    if directory.exists():
        raise SnapshotError("Backup destination must not already exist")
    if not versioning_enabled(s3, bucket):
        raise SnapshotError("S3 bucket versioning must be enabled to snapshot all object versions")
    # Acquiring the Packory writer advisory lock prevents a cooperating writer
    # from starting throughout the snapshot. It does NOT fence uncooperative writes.
    with postgres() as pg:
        initial = counts(pg)
        database = os.environ["PGDATABASE"]
        directory.parent.mkdir(parents=True, exist_ok=True)
        staged = Path(tempfile.mkdtemp(prefix=".packory-pgs3-", dir=directory.parent))
        try:
            pg_command("backup", staged / "metadata.dump")
            entries, markers, total = capture_object_versions(s3, bucket, prefix, staged, max_bytes)
            final = counts(pg)
            if final != initial:
                raise SnapshotError("Metadata changed during supposedly offline snapshot")
            manifest = {
                "schema": SCHEMA, "profile": "postgresql-s3-offline", "created_utc": now(),
                "source": {"bucket": bucket, "prefix": prefix, "database": database},
                "postgres": {"path": "metadata.dump", "size": (staged / "metadata.dump").stat().st_size,
                             "sha256": checksum(staged / "metadata.dump"),
                             "table_counts": final},
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
    # Restore only currently live versions. Historical versions remain in the
    # offline snapshot; S3-assigned VersionId and retention cannot be cloned.
    live = [e for e in manifest["objects"] if e["is_latest"]]
    latest_keys = {e["key"] for e in live}
    deleted = {m["key"] for m in manifest["delete_markers"] if m["is_latest"]}
    if latest_keys & deleted or len(latest_keys) != len(live):
        raise SnapshotError("Conflicting latest object versions in backup")
    for item in sorted(live, key=lambda e: e["key"]):
        path = directory / item["path"]
        with open(path, "rb") as data:
            s3.put_object(Bucket=target_bucket, Key=item["key"], Body=data,
                          IfNoneMatch="*", ContentType="application/octet-stream")
        actual = s3.get_object(Bucket=target_bucket, Key=item["key"])
        h = hashlib.sha256()
        try:
            for block in iter(lambda: actual["Body"].read(1024 * 1024), b""):
                h.update(block)
        finally:
            actual["Body"].close()
        if h.hexdigest() != item["sha256"]:
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
        pg_command("restore", Path(directory) / "metadata.dump")
        if counts(pg) != manifest["postgres"]["table_counts"]:
            raise SnapshotError("Restored PostgreSQL row counts disagree with snapshot")
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
            print(f"Isolated restore verified: {number} current S3 objects copied; database row counts checked.")
        return 0
    except (SnapshotError, subprocess.SubprocessError, OSError) as error:
        # Avoid reflecting operator connection URLs, passwords or S3 key names.
        if isinstance(error, SnapshotError):
            print("ERROR: " + str(error), file=sys.stderr)
        else:
            print("ERROR: offline snapshot/restore failed; inspect secure operator logs", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

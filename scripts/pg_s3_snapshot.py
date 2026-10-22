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
import base64
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import zipfile
from urllib.parse import urlsplit

SCHEMA = 4
LOCK = 0x5041434B4F525931  # PostgresSingleWriterLease.LockId
CORE_TABLES = ("packages", "npm_versions", "npm_tags", "cargo_versions", "pypi_files",
               "skills", "oci_blobs", "oci_manifests", "oci_tags", "oci_referrers",
               "publication_intents", "recovery_quarantines", "audit_events", "managed_tokens")
FEED_TABLES = ("feeds", "feed_namespaces", "feed_policies", "feed_upstreams")
DIGEST_TABLES = ("nuget_digests",)
ISOLATED_FEED_TABLES = ("feed_nuget_versions",)
RAW_TABLES = ("raw_artifacts",)
MAVEN_TABLES = ("maven_assets",)
GO_TABLES = ("go_modules",)
FEED_ASSET_TABLES = ("feed_assets",)
FEED_OCI_TABLES = ("feed_oci_tags",)
FEED_NPM_TAG_TABLES = ("feed_npm_tags",)
FEED_CARGO_YANK_TABLES = ("feed_cargo_yanks",)
FEED_NPM_DEPRECATION_TABLES = ("feed_npm_deprecations",)
MAVEN_METADATA_TABLES = ("maven_metadata",)
# Authoritative security and reviewed MCP metadata are independent of core package schema.
# The v7 application initializes these tables, so an absent table must abort a new backup.
SECURITY_REGISTRY_TABLES = ("skill_security_revisions", "skill_security_audit",
                            "mcp_registry_manifests", "mcp_registry_review_audit")
ALLOWED_TABLES = frozenset((
    *CORE_TABLES, "packory_schema_version", *FEED_TABLES,
    *DIGEST_TABLES, *ISOLATED_FEED_TABLES, *RAW_TABLES, *MAVEN_TABLES,
    *GO_TABLES, *FEED_ASSET_TABLES, *FEED_OCI_TABLES, *FEED_NPM_TAG_TABLES, *FEED_CARGO_YANK_TABLES, *FEED_NPM_DEPRECATION_TABLES, *MAVEN_METADATA_TABLES, *SECURITY_REGISTRY_TABLES))
SHA = re.compile(r"[0-9a-f]{64}")
PAYLOAD = re.compile(r"objects/[0-9a-f]{64}\.bin")
MAX_MANIFEST_BYTES = 64 * 1024 * 1024
MAX_PACKAGE_BYTES = 512 * 1024 * 1024
MAX_PACKAGE_METADATA = 4 * 1024 * 1024
ASSET_COLUMNS = ("feed_id", "kind", "path", "name", "version", "media_type",
                 "sha256", "size", "created_at")
OCI_TAG_COLUMNS = ("feed_id", "name", "tag", "digest", "updated_at")
NPM_TAG_COLUMNS = ("feed_id", "name", "tag", "version", "deleted", "updated_at")
CARGO_YANK_COLUMNS = ("feed_id", "name", "version", "yanked", "updated_at")
NPM_DEPRECATION_COLUMNS = ("feed_id", "name", "version", "message", "updated_at")
SEMVER = re.compile(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
                    r"(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)"
                    r"(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?"
                    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?")
OCI_REPOSITORY = re.compile(r"[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*"
                            r"(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*")
OCI_MEDIA = frozenset(("application/vnd.oci.image.manifest.v1+json",
                       "application/vnd.oci.image.index.v1+json",
                       "application/vnd.docker.distribution.manifest.v2+json",
                       "application/vnd.docker.distribution.manifest.list.v2+json",
                       "application/vnd.oci.artifact.manifest.v1+json"))

_maven_spec = importlib.util.spec_from_file_location(
    "packory_maven_snapshot", Path(__file__).with_name("maven_snapshot.py"))
maven = importlib.util.module_from_spec(_maven_spec)
_maven_spec.loader.exec_module(maven)


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


def database_schema_version(connection):
    """Read the exact Packory version, not a guess from table existence."""
    marker = connection.execute(
        "SELECT to_regclass('packory_schema_version')").fetchone()[0]
    if marker is None:
        return 0
    row = connection.execute(
        "SELECT version FROM packory_schema_version WHERE slot=1").fetchone()
    if not row or type(row[0]) is not int or row[0] not in range(1, 16):
        raise SnapshotError("Unknown Packory database schema version")
    return row[0]


def metadata_tables(connection):
    """Select all authoritative tables for a supported Packory schema version."""
    version = database_schema_version(connection)
    if version == 0:
        return CORE_TABLES
    if version >= 7:
        for table in SECURITY_REGISTRY_TABLES:
            if connection.execute("SELECT to_regclass(%s)", (table,)).fetchone()[0] is None:
                raise SnapshotError("Missing authoritative Skill/MCP security metadata table")
    return (CORE_TABLES + ("packory_schema_version",) +
            (FEED_TABLES if version >= 2 else ()) +
            (DIGEST_TABLES if version >= 3 else ()) +
            (ISOLATED_FEED_TABLES if version >= 4 else ()) +
            (RAW_TABLES if version >= 5 else ()) +
            (MAVEN_TABLES if version >= 6 else ()) +
            (GO_TABLES if version >= 7 else ()) +
            (FEED_ASSET_TABLES if version >= 8 else ()) +
            (FEED_OCI_TABLES if version >= 10 else ()) +
            (FEED_NPM_TAG_TABLES if version >= 13 else ()) +
            (FEED_CARGO_YANK_TABLES if version >= 14 else ()) +
            (FEED_NPM_DEPRECATION_TABLES if version >= 15 else ()) +
            (MAVEN_METADATA_TABLES if version >= 11 else ()) +
            (SECURITY_REGISTRY_TABLES if version >= 7 else ()))


def feed_references(connection):
    """Capture immutable artifact identities, never credentials or actor identities."""
    def rows(table, columns, order):
        query = "SELECT " + ",".join(columns) + " FROM " + table + " ORDER BY " + order
        return [dict(zip(columns, row)) for row in connection.execute(query)]
    references = {"assets": rows("feed_assets", ASSET_COLUMNS, 'feed_id COLLATE "C",kind COLLATE "C",path COLLATE "C"'),
                  "oci_tags": rows("feed_oci_tags", OCI_TAG_COLUMNS, 'feed_id COLLATE "C",name COLLATE "C",tag COLLATE "C"')}
    if database_schema_version(connection) >= 13:
        references["npm_tags"] = rows("feed_npm_tags", NPM_TAG_COLUMNS,
                                      'feed_id COLLATE "C",name COLLATE "C",tag COLLATE "C"')
    if database_schema_version(connection) >= 14:
        references["cargo_yanks"] = rows("feed_cargo_yanks", CARGO_YANK_COLUMNS,
                                         'feed_id COLLATE "C",name COLLATE "C",version COLLATE "C"')
    if database_schema_version(connection) >= 15:
        references["npm_deprecations"] = rows("feed_npm_deprecations", NPM_DEPRECATION_COLUMNS,
                                              'feed_id COLLATE "C",name COLLATE "C",version COLLATE "C"')
    return references


def maven_references(connection):
    """Retain exact mutable metadata bytes plus global immutable file identities."""
    def rows(table, columns, order):
        query = "SELECT " + ",".join(columns) + " FROM " + table + " ORDER BY " + order
        return [dict(zip(columns, row)) for row in connection.execute(query)]
    return {"metadata": rows("maven_metadata", maven.METADATA_COLUMNS, 'scope COLLATE "C",path COLLATE "C"'),
            "global_assets": rows("maven_assets", maven.ASSET_COLUMNS, 'path COLLATE "C"'),
            "feeds": rows("feeds", maven.FEED_COLUMNS, 'id COLLATE "C"')}


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


def valid_timestamp(value):
    if not plain_identifier(value, 64):
        return False
    try:
        datetime.fromisoformat(value.replace("Z", "+00:00"))
        return True
    except ValueError:
        return False


def valid_media(value):
    return (isinstance(value, str) and len(value) <= 100 and
            re.fullmatch(r"[a-z0-9][a-z0-9!#$&^_.+-]*/[a-z0-9][a-z0-9!#$&^_.+-]*", value) is not None)


def valid_feed_id(value):
    return isinstance(value, str) and re.fullmatch(r"[a-z][a-z0-9-]{1,62}[a-z0-9]", value) is not None


def escaped_go(value):
    return "".join("!" + char.lower() if "A" <= char <= "Z" else char for char in value)


def valid_go(name, version):
    parts = name.split("/")
    if (not 4 <= len(name) <= 255 or not 2 <= len(parts) <= 32 or
            any(not re.fullmatch(r"[A-Za-z0-9_~-][A-Za-z0-9._~-]{0,127}", part) or
                part.endswith(".") or re.match(r"(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)", part, re.I) or
                re.search(r"~[0-9]+$", part.split(".")[0]) for part in parts)):
        return False
    domain = parts[0]
    if ("." not in domain or not re.fullmatch(r"[a-z0-9.-]+", domain) or
            any(not piece or piece.startswith("-") or piece.endswith("-") for piece in domain.split("."))):
        return False
    if not 6 <= len(version) <= 160 or not SEMVER.fullmatch(version[1:]) or not version.startswith("v"):
        return False
    if "+" in version and not version.endswith("+incompatible"):
        return False
    major = version[1:].split(".")[0]
    explicit = re.fullmatch(r"v([2-9]|[1-9][0-9]+)", parts[-1])
    if (parts[-1].startswith("v") and len(parts[-1]) > 1 and parts[-1][1].isdigit() and
            not explicit and domain != "gopkg.in"):
        return False
    gopkg = re.search(r"\.v([1-9][0-9]*)$", parts[-1]) if domain == "gopkg.in" else None
    if explicit or gopkg:
        return (explicit or gopkg).group(1) == major and "+" not in version
    return major in ("0", "1") or version.endswith("+incompatible")


def normalize_python(name):
    if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9._-]{0,198}[A-Za-z0-9])?", name):
        return None
    return re.sub(r"[-_.]+", "-", name.lower())


def feed_asset_key(asset):
    """Derive keys from typed coordinates; a manifest cannot supply a storage key."""
    if (not isinstance(asset, dict) or set(asset) != set(ASSET_COLUMNS) or
            not valid_feed_id(asset.get("feed_id")) or not digest_string(asset.get("sha256")) or
            not nonnegative_integer(asset.get("size")) or not valid_timestamp(asset.get("created_at")) or
            any(not isinstance(asset.get(field), str) for field in ("kind", "path", "name", "version", "media_type")) or
            not valid_key(asset["path"]) or len(asset["path"]) > 2048 or
            not 0 < len(asset["name"]) <= 255 or not 0 < len(asset["version"]) <= 160):
        raise SnapshotError("Invalid private feed artifact reference")
    feed, kind, path, name, version, media = (asset[field] for field in
        ("feed_id", "kind", "path", "name", "version", "media_type"))
    prefix = "feeds/" + feed + "/" + kind + "/"
    valid, key = False, ""
    if kind == "raw":
        valid = (re.fullmatch(r"[a-z0-9](?:[a-z0-9._-]{0,126}[a-z0-9])?", name) is not None and
                 ".." not in name and re.fullmatch(r"[0-9A-Za-z][0-9A-Za-z.+-]{0,127}", version) is not None and
                 ".." not in version and path == name + "/" + version and valid_media(media))
        key = prefix + path + "/payload.blob"
    elif kind == "maven":
        try:
            coordinate = maven.parse_asset_path(path)
            valid = (name == coordinate["group_id"] + ":" + coordinate["artifact_id"] and
                     version == coordinate["version"] and media == coordinate["media_type"])
        except ValueError:
            valid = False
        key = prefix + path
    elif kind == "go":
        valid = (valid_go(name, version) and path == escaped_go(name) + "/@v/" + escaped_go(version) + ".zip" and
                 media == "application/vnd.packory.go-module-bundle+zip" and asset["size"] <= 33 * 1024 * 1024 + 32768)
        key = prefix + hashlib.sha256(name.encode()).hexdigest() + "/" + hashlib.sha256(version.encode()).hexdigest() + ".bundle"
    elif kind in ("npm", "cargo", "pypi"):
        if kind == "npm":
            valid = (len(name) <= 214 and re.fullmatch(r"(?:@[a-z0-9][a-z0-9._-]{0,100}/)?[a-z0-9][a-z0-9._-]{0,100}", name) is not None and
                     len(version) <= 128 and SEMVER.fullmatch(version) is not None and "+" not in version and
                     all(int(part) <= 9007199254740991 for part in version.split("-")[0].split(".")) and path == name + "/" + version)
        elif kind == "cargo":
            valid = (re.fullmatch(r"[a-z][a-z0-9_-]{0,63}", name) is not None and len(version) <= 128 and
                     SEMVER.fullmatch(version) is not None and path == name + "/" + version.split("+", 1)[0])
        else:
            filename = path[len(name) + 1:] if path.startswith(name + "/") else ""
            valid = (normalize_python(name) == name and len(version) <= 128 and
                     re.fullmatch(r"[0-9]+(?:\.[0-9]+)*(?:(?:a|b|rc)[0-9]+)?(?:\.post[0-9]+)?(?:\.dev[0-9]+)?(?:\+[a-z0-9]+(?:\.[a-z0-9]+)*)?", version) is not None and
                     len(filename) <= 255 and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+-]{0,254}\.(?:whl|zip|tar\.gz)", filename) is not None)
            if valid and filename.endswith(".whl"):
                parts = filename[:-4].split("-")
                valid = len(parts) in (5, 6) and normalize_python(parts[0]) == name and parts[1] == version and all(parts[2:])
            elif valid:
                stem = filename[:-7] if filename.endswith(".tar.gz") else filename[:-4]
                valid = stem.endswith("-" + version) and normalize_python(stem[:-(len(version) + 1)]) == name
        valid = valid and media == "application/vnd.packory." + kind + "-package+zip" and asset["size"] <= MAX_PACKAGE_BYTES + MAX_PACKAGE_METADATA + 65536
        key = prefix + hashlib.sha256(path.encode("utf-8")).hexdigest() + ".bundle"
    elif kind == "oci":
        full_name = "feeds/" + feed + "/" + name
        valid = (len(full_name) <= 255 and OCI_REPOSITORY.fullmatch(full_name) is not None and asset["size"] <= MAX_PACKAGE_BYTES and
                 version == "sha256:" + asset["sha256"] and
                 ((path == "blob/" + name + "/" + asset["sha256"] and media == "application/octet-stream") or
                  (path == "manifest/" + name + "/" + asset["sha256"] and media in OCI_MEDIA and 0 < asset["size"] <= 64 * 1024 * 1024)))
        key = prefix + path + (".json" if path.startswith("manifest/") else ".blob")
    if not valid or (asset["size"] == 0 and kind != "oci") or not valid_key(key):
        raise SnapshotError("Invalid private feed coordinate or media reference")
    return key


def load_json_bytes(payload):
    return json.loads(payload, object_pairs_hook=unique_json, parse_constant=invalid_json_constant)


def zip_member(archive, name, limit):
    item = archive.getinfo(name)
    if (item.is_dir() or item.flag_bits & 1 or item.compress_type != zipfile.ZIP_STORED or
            not 0 < item.file_size <= limit or item.compress_size != item.file_size):
        raise SnapshotError("Unsafe private feed bundle member")
    return item


def verify_feed_bundle(file, asset):
    with zipfile.ZipFile(file) as archive:
        expected = {"info", "mod", "zip"} if asset["kind"] == "go" else {"metadata.json", "payload"}
        if len(archive.infolist()) != len(expected) or set(archive.namelist()) != expected:
            raise SnapshotError("Unexpected private feed bundle members")
        if asset["kind"] == "go":
            info = load_json_bytes(archive.read(zip_member(archive, "info", 2048)))
            mod = archive.read(zip_member(archive, "mod", 1024 * 1024)).decode("utf-8")
            directives = [line.strip().split() for line in mod.splitlines()
                          if line.strip().startswith(("module ", "module\t"))]
            if (not isinstance(info, dict) or info.get("Version") != asset["version"] or
                    not valid_timestamp(info.get("Time")) or "\0" in mod or directives != [["module", asset["name"]]]):
                raise SnapshotError("Go feed bundle identity mismatch")
            content = zip_member(archive, "zip", 32 * 1024 * 1024)
            with archive.open(content) as stream:
                while stream.read(1024 * 1024):
                    pass  # CRC, truncation and declared length are checked by ZipExtFile.
            return
        envelope = load_json_bytes(archive.read(zip_member(archive, "metadata.json", MAX_PACKAGE_METADATA)))
        payload = zip_member(archive, "payload", MAX_PACKAGE_BYTES)
        if (not isinstance(envelope, dict) or type(envelope.get("schemaVersion")) is not int or envelope["schemaVersion"] != 1 or
                any(envelope.get(field) != asset[column] for field, column in
                    (("feedId", "feed_id"), ("kind", "kind"), ("path", "path"), ("name", "name"), ("version", "version"))) or
                type(envelope.get("payloadSize")) is not int or envelope["payloadSize"] != payload.file_size or
                not digest_string(envelope.get("payloadSha256")) or not isinstance(envelope.get("metadata"), dict)):
            raise SnapshotError("Private package bundle identity mismatch")
        sha256, sha512, sha1, length = hashlib.sha256(), hashlib.sha512(), hashlib.sha1(), 0
        with archive.open(payload) as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                length += len(block)
                if length > payload.file_size:
                    raise SnapshotError("Private package expands past declared size")
                sha256.update(block)
                sha512.update(block)
                sha1.update(block)
        if length != payload.file_size or sha256.hexdigest() != envelope["payloadSha256"]:
            raise SnapshotError("Private package payload checksum mismatch")
        metadata = envelope["metadata"]
        if asset["kind"] == "cargo":
            valid = metadata.get("name") == asset["name"] and metadata.get("vers") == asset["version"] and metadata.get("cksum") == sha256.hexdigest()
        elif asset["kind"] == "pypi":
            valid = (metadata.get("name") == asset["name"] and metadata.get("version") == asset["version"] and
                     metadata.get("filename") == asset["path"][len(asset["name"]) + 1:] and
                     metadata.get("sha256") == sha256.hexdigest() and type(metadata.get("size")) is int and metadata["size"] == length and
                     "requiresPython" in metadata and (metadata["requiresPython"] is None or isinstance(metadata["requiresPython"], str)))
        else:
            record, tags = metadata.get("manifest"), metadata.get("tags")
            dist = record.get("dist") if isinstance(record, dict) else None
            valid = (isinstance(record, dict) and record.get("name") == asset["name"] and record.get("version") == asset["version"] and
                     isinstance(dist, dict) and dist.get("shasum") == sha1.hexdigest() and
                     dist.get("integrity") == "sha512-" + base64.b64encode(sha512.digest()).decode("ascii") and
                     isinstance(tags, dict) and bool(tags) and all(re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]{0,63}", tag) is not None and
                         value == asset["version"] for tag, value in tags.items()))
        if not valid:
            raise SnapshotError("Private package protocol metadata mismatch")


def verify_feed_references(manifest, folder):
    references = manifest.get("feed_references")
    counts = manifest["postgres"]["table_counts"]
    schema_version = manifest["postgres"].get("database_schema_version", 0)
    has_npm_tags = schema_version >= 13
    has_cargo_yanks = schema_version >= 14
    has_deprecations = schema_version >= 15
    keys = {"assets", "oci_tags"}
    if has_npm_tags:
        keys.add("npm_tags")
    if has_cargo_yanks:
        keys.add("cargo_yanks")
    if has_deprecations:
        keys.add("npm_deprecations")
    if not isinstance(references, dict) or set(references) != keys:
        raise SnapshotError("Missing private feed reference inventory")
    assets, tags = references["assets"], references["oci_tags"]
    npm_tags = references.get("npm_tags", [])
    cargo_yanks = references.get("cargo_yanks", [])
    deprecations = references.get("npm_deprecations", [])
    if (not isinstance(assets, list) or not isinstance(tags, list) or
            not isinstance(npm_tags, list) or not isinstance(cargo_yanks, list) or not isinstance(deprecations, list) or
            len(assets) != counts["feed_assets"] or len(tags) != counts["feed_oci_tags"] or
            (has_npm_tags and len(npm_tags) != counts["feed_npm_tags"]) or
            (has_cargo_yanks and len(cargo_yanks) != counts["feed_cargo_yanks"]) or
            (has_deprecations and len(deprecations) != counts["feed_npm_deprecations"])):
        raise SnapshotError("Private feed reference inventory disagrees with metadata counts")
    current = {entry["key"]: entry for entry in manifest["objects"] if entry["is_latest"]}
    ledger, files = {}, {}
    for asset in assets:
        key = manifest["source"]["prefix"] + feed_asset_key(asset)
        identity = (asset["feed_id"], asset["kind"], asset["path"])
        stored = current.get(key)
        if (identity in ledger or stored is None or stored["size"] != asset["size"] or stored["sha256"] != asset["sha256"]):
            raise SnapshotError("Private feed artifact is missing, ambiguous or differs from its committed ledger")
        ledger[identity], files[identity] = asset, folder / stored["path"]
        if asset["kind"] in ("go", "npm", "cargo", "pypi"):
            verify_feed_bundle(files[identity], asset)

    # Versioned mutable npm pointers must resolve only to committed assets
    # in their own feed; tombstones intentionally contain no version.
    npm_assets = {(asset["feed_id"], asset["name"], asset["version"])
                  for asset in assets if asset["kind"] == "npm"}
    seen_npm_tags = set()
    for pointer in npm_tags:
        if not isinstance(pointer, dict) or set(pointer) != set(NPM_TAG_COLUMNS):
            raise SnapshotError("Invalid npm dist-tag snapshot pointer")
        identity = (pointer["feed_id"], pointer["name"], pointer["tag"])
        if (identity in seen_npm_tags or
                not isinstance(pointer["feed_id"], str) or
                not isinstance(pointer["name"], str) or
                not isinstance(pointer["tag"], str) or
                re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]{0,63}", pointer["tag"]) is None or
                type(pointer["deleted"]) is not int or pointer["deleted"] not in (0, 1) or
                not isinstance(pointer["version"], str) or
                not isinstance(pointer["updated_at"], str)):
            raise SnapshotError("Invalid npm dist-tag snapshot identity")
        seen_npm_tags.add(identity)
        if (pointer["deleted"] == 1 and pointer["version"] != "" or
                pointer["deleted"] == 0 and
                (pointer["feed_id"], pointer["name"], pointer["version"]) not in npm_assets):
            raise SnapshotError("npm dist-tag refers to missing/cross-feed revision")

    seen_deprecations = set()
    for pointer in deprecations:
        if not isinstance(pointer, dict) or set(pointer) != set(NPM_DEPRECATION_COLUMNS):
            raise SnapshotError("Invalid npm deprecation reference shape")
        if any(not isinstance(pointer[k], str) for k in NPM_DEPRECATION_COLUMNS):
            raise SnapshotError("Invalid npm deprecation field type")
        identity = pointer["feed_id"], pointer["name"], pointer["version"]
        message = pointer["message"]
        if (identity not in npm_assets or identity in seen_deprecations or
                len(message.encode("utf-16-le", errors="surrogatepass")) > 4096 or
                any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in message) or not pointer["updated_at"]):
            raise SnapshotError("Invalid or cross-feed npm deprecation reference")
        seen_deprecations.add(identity)

    # V14 mutable Cargo yanks must reference immutable committed crate
    # revisions in exactly their own feed. Neither the bytes nor the original
    # index metadata are rewritten by a yank.
    cargo_assets = {(asset["feed_id"], asset["name"], asset["version"])
                    for asset in assets if asset["kind"] == "cargo"}
    seen_cargo_yanks = set()
    for pointer in cargo_yanks:
        if not isinstance(pointer, dict) or set(pointer) != set(CARGO_YANK_COLUMNS):
            raise SnapshotError("Invalid Cargo yank reference shape")
        identity = (pointer["feed_id"], pointer["name"], pointer["version"])
        if (identity in seen_cargo_yanks or identity not in cargo_assets or
                any(not isinstance(piece, str) or not piece for piece in identity) or
                SEMVER.fullmatch(pointer["version"]) is None or
                type(pointer["yanked"]) is not int or pointer["yanked"] not in (0, 1) or
                not isinstance(pointer["updated_at"], str) or not pointer["updated_at"]):
            raise SnapshotError("Cargo yank points to missing/cross-feed revision or invalid state")
        seen_cargo_yanks.add(identity)

    def descriptor(value):
        if (not isinstance(value, dict) or "urls" in value or not isinstance(value.get("digest"), str) or
                re.fullmatch(r"sha256:[0-9a-f]{64}", value["digest"]) is None or
                not nonnegative_integer(value.get("size")) or not valid_media(value.get("mediaType"))):
            raise SnapshotError("Invalid OCI descriptor in private feed snapshot")
        if "data" in value:
            encoded = value["data"]
            if not isinstance(encoded, str):
                raise SnapshotError("Invalid OCI inline content in private feed snapshot")
            try:
                data = base64.b64decode(encoded, validate=True)
            except ValueError as error:
                raise SnapshotError("Invalid OCI inline encoding in private feed snapshot") from error
            if (len(data) != value["size"] or base64.b64encode(data).decode("ascii") != encoded or
                    "sha256:" + hashlib.sha256(data).hexdigest() != value["digest"]):
                raise SnapshotError("OCI inline content differs from its descriptor identity")
        return value["digest"][7:]

    for identity, asset in ledger.items():
        if asset["kind"] != "oci" or not asset["path"].startswith("manifest/"):
            continue
        document = load_json_bytes(files[identity].read_bytes())
        if (not isinstance(document, dict) or type(document.get("schemaVersion")) is not int or document["schemaVersion"] != 2 or
                document.get("mediaType", asset["media_type"]) != asset["media_type"]):
            raise SnapshotError("Invalid committed OCI manifest in private feed snapshot")
        if ("artifactType" in document and not valid_media(document["artifactType"]) or
                "annotations" in document and (not isinstance(document["annotations"], dict) or
                    any(not isinstance(value, str) for value in document["annotations"].values()))):
            raise SnapshotError("Invalid OCI manifest artifact metadata")
        index = asset["media_type"] in ("application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json")
        artifact = asset["media_type"] == "application/vnd.oci.artifact.manifest.v1+json"
        required = "manifests" if index else "blobs" if artifact else "layers"
        if not isinstance(document.get(required), list) or (not index and not artifact and "config" not in document):
            raise SnapshotError("Incomplete private OCI manifest graph")
        for field in ("config", "layers", "blobs", "manifests"):
            if field not in document:
                continue
            entries = [document[field]] if field == "config" else document[field]
            if not isinstance(entries, list) or len(entries) > 4096:
                raise SnapshotError("Invalid private OCI manifest descriptors")
            for entry in entries:
                digest = descriptor(entry)
                path = ("manifest/" if field == "manifests" else "blob/") + asset["name"] + "/" + digest
                target = ledger.get((asset["feed_id"], "oci", path))
                if target is None or target["size"] != entry["size"] or (field == "manifests" and target["media_type"] != entry["mediaType"]):
                    raise SnapshotError("Private OCI manifest refers outside its committed feed/repository graph")
        if "subject" in document:
            descriptor(document["subject"])  # OCI permits an as-yet unpublished subject.
    seen_tags = set()
    for tag in tags:
        if (not isinstance(tag, dict) or set(tag) != set(OCI_TAG_COLUMNS) or not valid_feed_id(tag.get("feed_id")) or
                not isinstance(tag.get("name"), str) or not isinstance(tag.get("tag"), str) or
                re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}", tag["tag"]) is None or
                not isinstance(tag.get("digest"), str) or re.fullmatch(r"sha256:[0-9a-f]{64}", tag["digest"]) is None or
                not valid_timestamp(tag.get("updated_at"))):
            raise SnapshotError("Invalid private OCI tag reference")
        identity = (tag["feed_id"], tag["name"], tag["tag"])
        path = "manifest/" + tag["name"] + "/" + tag["digest"][7:]
        if identity in seen_tags or (tag["feed_id"], "oci", path) not in ledger:
            raise SnapshotError("Private OCI tag does not reference its committed feed/repository manifest")
        seen_tags.add(identity)


def verify_maven_references(manifest):
    references = manifest.get("maven_references")
    if not isinstance(references, dict) or set(references) != {"metadata", "global_assets", "feeds"}:
        raise SnapshotError("Missing Maven metadata reference inventory")
    records, assets, feeds = references["metadata"], references["global_assets"], references["feeds"]
    counts = manifest["postgres"]["table_counts"]
    if (not isinstance(records, list) or not isinstance(assets, list) or not isinstance(feeds, list) or
            len(records) != counts["maven_metadata"] or len(assets) != counts["maven_assets"] or len(feeds) != counts["feeds"]):
        raise SnapshotError("Maven reference inventory disagrees with metadata counts")
    current = {entry["key"]: entry for entry in manifest["objects"] if entry["is_latest"]}

    def verify_blob(scope, path, sha256, size):
        key = manifest["source"]["prefix"] + ("feeds/" + scope + "/" if scope else "") + "maven/" + path
        stored = current.get(key)
        if stored is None or stored["sha256"] != sha256 or stored["size"] != size:
            raise SnapshotError("Maven metadata references missing or changed immutable bytes")

    try:
        maven.validate_inventory(records, assets, manifest["feed_references"]["assets"], verify_blob, feeds)
    except (ValueError, KeyError, TypeError, UnicodeError) as error:
        raise SnapshotError("Maven metadata bytes, coordinates or same-scope references are invalid") from error


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
                manifest["schema"] not in (1, 2, 3, SCHEMA) or manifest.get("profile") != "postgresql-s3-offline"):
            raise SnapshotError("Unknown snapshot format")
        pg = manifest["postgres"]
        if (not isinstance(pg, dict) or pg.get("path") != "metadata.dump" or
                not digest_string(pg.get("sha256")) or not nonnegative_integer(pg.get("size"))):
            raise SnapshotError("Invalid PostgreSQL manifest")
        table_counts = pg["table_counts"]
        if (not isinstance(table_counts, dict) or not set(table_counts) <= ALLOWED_TABLES or
                any(not nonnegative_integer(count) for count in table_counts.values())):
            raise SnapshotError("Invalid PostgreSQL row counts")
        if (("feed_oci_tags" in table_counts or pg.get("database_schema_version") == 10) and
                manifest["schema"] < 3):
            raise SnapshotError("V10 requires the complete snapshot format")
        if (("maven_metadata" in table_counts or pg.get("database_schema_version", 0) >= 11) and
                manifest["schema"] < 4):
            raise SnapshotError("V11 requires the Maven metadata snapshot format")
        if manifest["schema"] >= 2:
            fingerprints, db_version = pg["table_fingerprints"], pg["database_schema_version"]
            if type(db_version) is not int or db_version not in range(16):
                raise SnapshotError("Unknown Packory database schema version")
            if db_version >= 10 and manifest["schema"] < 3:
                raise SnapshotError("V10 requires the complete snapshot format")
            if db_version >= 11 and manifest["schema"] < 4:
                raise SnapshotError("V11 requires the Maven metadata snapshot format")
            required = set(CORE_TABLES)
            if db_version >= 1:
                required.add("packory_schema_version")
            if db_version >= 2:
                required.update(FEED_TABLES)
            if db_version >= 3:
                required.update(DIGEST_TABLES)
            if db_version >= 4:
                required.update(ISOLATED_FEED_TABLES)
            if db_version >= 5:
                required.update(RAW_TABLES)
            if db_version >= 6:
                required.update(MAVEN_TABLES)
            if db_version >= 7:
                required.update(GO_TABLES)
                if manifest["schema"] >= 3:
                    required.update(SECURITY_REGISTRY_TABLES)
            if db_version >= 8:
                required.update(FEED_ASSET_TABLES)
            if db_version >= 10:
                required.update(FEED_OCI_TABLES)
            if db_version >= 11:
                required.update(MAVEN_METADATA_TABLES)
            if db_version >= 13:
                required.update(FEED_NPM_TAG_TABLES)
            if db_version >= 14:
                required.update(FEED_CARGO_YANK_TABLES)
            if db_version >= 15:
                required.update(FEED_NPM_DEPRECATION_TABLES)
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
        if manifest["schema"] >= 2 and pg["database_schema_version"] >= 10:
            verify_feed_references(manifest, folder)
        if manifest["schema"] >= 4 and pg["database_schema_version"] >= 11:
            verify_maven_references(manifest)
        return manifest
    except (KeyError, TypeError, ValueError, OSError, UnicodeError, RecursionError,
            zipfile.BadZipFile, NotImplementedError) as error:
        raise SnapshotError("Snapshot manifest or file integrity invalid") from error


def backup(directory, s3, bucket, prefix, max_bytes):
    directory = Path(directory)
    if directory.exists() or directory.is_symlink():
        raise SnapshotError("Backup destination must not already exist")
    if not versioning_enabled(s3, bucket):
        raise SnapshotError("S3 bucket versioning must be enabled to snapshot all object versions")
    with postgres() as pg:
        db_version = database_schema_version(pg)
        tables = metadata_tables(pg)
        initial = metadata_fingerprints(pg, tables)
        private_references = feed_references(pg) if db_version >= 10 else None
        mutable_maven = maven_references(pg) if db_version >= 11 else None
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
            if final != initial or metadata_tables(pg) != tables or database_schema_version(pg) != db_version:
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
                             "database_schema_version": db_version},
                "objects": entries, "delete_markers": markers,
                "snapshot_bytes": total,
                "historical_versions_retained_in_backup": True,
                "restored_object_versions_and_retention_equivalent": False,
            }
            if private_references is not None:
                manifest["feed_references"] = private_references
            if mutable_maven is not None:
                manifest["maven_references"] = mutable_maven
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
        restored_version = database_schema_version(pg)
        if restored_version >= 10 and (
                manifest["schema"] < (4 if restored_version >= 11 else 3) or
                manifest["postgres"].get("database_schema_version") != restored_version):
            raise SnapshotError("Restored database requires its complete schema-versioned snapshot evidence")
        expected, actual = manifest["postgres"]["table_counts"], counts(pg)
        if {name: actual.get(name) for name in expected} != expected:
            raise SnapshotError("Restored PostgreSQL row counts disagree with snapshot")
        if manifest["schema"] >= 2:
            if metadata_fingerprints(pg) != manifest["postgres"]["table_fingerprints"]:
                raise SnapshotError("Restored PostgreSQL row values disagree with snapshot")
            if manifest["postgres"]["database_schema_version"] >= 10 and feed_references(pg) != manifest["feed_references"]:
                raise SnapshotError("Restored private feed references disagree with verified artifact inventory")
            if manifest["postgres"]["database_schema_version"] >= 11 and maven_references(pg) != manifest["maven_references"]:
                raise SnapshotError("Restored Maven metadata references disagree with verified inventory")
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
                  ("rows and SHA-256 fingerprints checked." if verify(args.snapshot)["schema"] >= 2
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

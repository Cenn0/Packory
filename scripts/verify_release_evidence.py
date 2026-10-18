#!/usr/bin/env python3
"""Offline, fail-closed release-evidence verifier. Never executes Packory deployments.

A valid operator signature protects the manifest, but does NOT establish that
the cited real-provider tests actually happened: humans must independently
qualify evidence and custody before signing. This script never prints evidence.
"""
import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys

GATES = (
    "pg_s3_restore",
    "real_s3_provider",
    "writer_fencing",
    "multi_node_protocol",
    "security_audit",
    "slo_faults",
    "load_interop",
    "supply_chain",
)
FILE = re.compile(r"\A[a-z0-9][a-z0-9._-]{0,99}\.json\Z")
DIGEST = re.compile(r"\A[0-9a-f]{64}\Z")
SHA = re.compile(r"\A[0-9a-f]{40}\Z")
KEY_ID = re.compile(r"\A[A-Za-z0-9_-]{1,64}\Z")
MAX_MANIFEST = 32768
MAX_REPORT = 2 * 1024 * 1024


class Rejected(ValueError):
    pass


def exact(obj, keys):
    if type(obj) is not dict or set(obj) != set(keys):
        raise Rejected("schema_invalid")
    return obj


def no_duplicate_keys(pairs):
    obj = {}
    for key, value in pairs:
        if key in obj:
            raise Rejected("schema_duplicate_key")
        obj[key] = value
    return obj


def bounded_file(path, maximum):
    if path.is_symlink() or not path.is_file():
        raise Rejected("file_unavailable")
    with path.open("rb") as handle:
        value = handle.read(maximum + 1)
    if len(value) > maximum:
        raise Rejected("file_too_large")
    return value


def utc_timestamp(value):
    if not isinstance(value, str) or not re.fullmatch(
        r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z", value
    ):
        raise Rejected("timestamp_invalid")
    try:
        return dt.datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ").replace(
            tzinfo=dt.timezone.utc
        )
    except ValueError as exc:
        raise Rejected("timestamp_invalid") from exc


def validate(root, expected_sha, public_key, key_id, now=None):
    root = pathlib.Path(root)
    public_key = pathlib.Path(public_key)
    if root.is_symlink() or not root.is_dir():
        raise Rejected("evidence_root_invalid")
    if not SHA.fullmatch(expected_sha):
        raise Rejected("revision_invalid")
    if not KEY_ID.fullmatch(key_id):
        raise Rejected("key_id_invalid")
    if public_key.is_symlink() or not public_key.is_file():
        raise Rejected("trust_anchor_invalid")
    if b"-----BEGIN PUBLIC KEY-----" not in bounded_file(public_key, 8192):
        raise Rejected("trust_anchor_invalid")

    manifest = root / "release-manifest.json"
    signature = root / "release-manifest.sig"
    raw = bounded_file(manifest, MAX_MANIFEST)
    bounded_file(signature, 8192)
    try:
        doc = json.loads(raw.decode("utf-8", errors="strict"),
                         object_pairs_hook=no_duplicate_keys)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise Rejected("schema_invalid") from exc

    exact(doc, (
        "schema_version", "revision", "audience", "environment", "verifier_key_id",
        "issued_at", "expires_at", "gates"
    ))
    if type(doc["schema_version"]) is not int or doc["schema_version"] != 1:
        raise Rejected("schema_version_invalid")
    if (doc["revision"] != expected_sha or
        doc["audience"] != "packory-production-acceptance" or
        doc["environment"] != "isolated-production-like" or
        doc["verifier_key_id"] != key_id):
        raise Rejected("identity_mismatch")

    issued = utc_timestamp(doc["issued_at"])
    expires = utc_timestamp(doc["expires_at"])
    current = now or dt.datetime.now(dt.timezone.utc)
    if (issued > current + dt.timedelta(minutes=5) or expires <= current or
        expires <= issued or expires - issued > dt.timedelta(days=30)):
        raise Rejected("evidence_not_current")

    exact(doc["gates"], GATES)
    observed = set()
    for gate in GATES:
        entry = exact(doc["gates"][gate], ("status", "report", "sha256"))
        filename = entry["report"]
        if (entry["status"] != "pass" or
            not isinstance(filename, str) or not FILE.fullmatch(filename) or
            ".." in filename or
            not isinstance(entry["sha256"], str) or
            not DIGEST.fullmatch(entry["sha256"]) or
            filename in observed):
            raise Rejected("gate_invalid")
        observed.add(filename)
        content = bounded_file(root / filename, MAX_REPORT)
        if hashlib.sha256(content).hexdigest() != entry["sha256"]:
            raise Rejected("evidence_digest_mismatch")

    # Validate the detached signature over the EXACT manifest bytes.
    # No shell, no unsigned fallback, no network or agent-selected command.
    try:
        completed = subprocess.run(
            ["openssl", "dgst", "-sha256", "-verify", str(public_key),
             "-signature", str(signature), "-sigopt", "rsa_padding_mode:pss",
             "-sigopt", "rsa_pss_saltlen:-1", str(manifest)],
            capture_output=True, timeout=5, check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise Rejected("signature_verification_unavailable") from exc
    if completed.returncode != 0:
        raise Rejected("signature_invalid")
    return len(GATES)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-dir", required=True)
    parser.add_argument("--expected-sha", required=True)
    parser.add_argument("--public-key", required=True)
    parser.add_argument("--key-id", required=True)
    args = parser.parse_args(argv)
    try:
        count = validate(args.evidence_dir, args.expected_sha,
                         args.public_key, args.key_id)
    except (Rejected, OSError, TypeError, KeyError) as exc:
        code = str(exc) if isinstance(exc, Rejected) else "evidence_invalid"
        print(f"BLOCKED: {code}", file=sys.stderr)
        return 1
    print(f"VERIFIED: {count} signed, digest-bound gate records for requested revision")
    print("Operator must still independently qualify evidence authenticity and adequacy.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

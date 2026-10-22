#!/usr/bin/env python3
"""Verify integrity and narrow claims of a Packory CI source-evidence artifact.

This checks the archive's recorded digest and the exact source identifier. It
cannot prove production readiness, release approval or the authenticity of a
GitHub Actions run; callers must independently check workflow identity and
provenance against GitHub.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys

SHA = re.compile(r"\A[0-9a-f]{40}\Z")
DIGEST = re.compile(r"\A[0-9a-f]{64}\Z")
MAX_MANIFEST_BYTES = 4096
MAX_ARCHIVE_BYTES = 128 * 1024 * 1024


class Rejected(ValueError):
    pass


def _unique_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise Rejected("duplicate_manifest_key")
        result[key] = value
    return result


def _nonfinite(_value):
    raise Rejected("invalid_manifest_value")


def _exact(value, keys):
    if type(value) is not dict or set(value) != set(keys):
        raise Rejected("invalid_manifest_schema")
    return value


def validate(folder, expected_sha):
    folder = Path(folder)
    if folder.is_symlink() or not folder.is_dir():
        raise Rejected("invalid_evidence_directory")
    if not isinstance(expected_sha, str) or not SHA.fullmatch(expected_sha):
        raise Rejected("invalid_source_sha")
    manifest = folder / "manifest.json"
    if manifest.is_symlink() or not manifest.is_file():
        raise Rejected("manifest_unavailable")
    with manifest.open("rb") as stream:
        raw = stream.read(MAX_MANIFEST_BYTES + 1)
    if len(raw) > MAX_MANIFEST_BYTES:
        raise Rejected("manifest_too_large")
    try:
        info = json.loads(raw.decode("utf-8", errors="strict"),
                          object_pairs_hook=_unique_keys,
                          parse_constant=_nonfinite)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise Rejected("invalid_manifest_json") from exc
    _exact(info, ("schema", "sourceSha", "runId", "runAttempt", "qualification",
                  "productionRecommended", "archive"))
    if info["schema"] != "packory.source-evidence/1" or info["sourceSha"] != expected_sha:
        raise Rejected("source_identity_mismatch")
    if (type(info["runId"]) is not int or info["runId"] <= 0 or
            type(info["runAttempt"]) is not int or info["runAttempt"] <= 0):
        raise Rejected("invalid_run_identity")
    if info["qualification"] != "source-only" or info["productionRecommended"] is not False:
        raise Rejected("qualification_overclaim")
    archive = _exact(info["archive"], ("name", "sha256", "bytes"))
    if (archive["name"] != "source.tar.gz" or
            not isinstance(archive["sha256"], str) or
            not DIGEST.fullmatch(archive["sha256"])):
        raise Rejected("invalid_archive_identity")
    length = archive["bytes"]
    if type(length) is not int or length < 1 or length > MAX_ARCHIVE_BYTES:
        raise Rejected("invalid_archive_size")
    source = folder / archive["name"]
    if source.is_symlink() or not source.is_file() or source.stat().st_size != length:
        raise Rejected("archive_unavailable_or_size_mismatch")
    digest = hashlib.sha256()
    total = 0
    with source.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            total += len(block)
            if total > MAX_ARCHIVE_BYTES:
                raise Rejected("archive_too_large")
            digest.update(block)
    if total != length or digest.hexdigest() != archive["sha256"]:
        raise Rejected("archive_digest_mismatch")
    return {"sourceSha": expected_sha, "runId": info["runId"],
            "runAttempt": info["runAttempt"], "qualification": "source-only",
            "productionRecommended": False}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-dir", required=True)
    parser.add_argument("--expected-sha", required=True)
    args = parser.parse_args(argv)
    try:
        result = validate(args.evidence_dir, args.expected_sha)
    except (Rejected, OSError, TypeError, KeyError, ValueError):
        print("BLOCKED: source evidence integrity or scope validation failed", file=sys.stderr)
        return 1
    print("VERIFIED: source-only evidence integrity for " + result["sourceSha"])
    print("This is not release approval, provider qualification, or production evidence.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

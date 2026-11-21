#!/usr/bin/env python3
"""Offline readback of the exact, reviewed development-resource pilot.

Only the fixed manifest and resource tree are read. Nothing is executed,
downloaded, repaired, installed or authorized. Reviewed Git/source evidence
provides trust; matching local hashes alone does not authenticate a publisher.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys

SOURCE_REVISION = "adedfd9016a01368b3d56f65965e39a5699242ca"
MANIFEST = ".ais-install-manifest.json"
MANIFEST_SHA256 = "b0360219764f188e6dcbc38ef8fed13c0f305c8d95ed3322eddead4db08d63f9"
MAX_MANIFEST_BYTES = 64 * 1024
MAX_FILE_BYTES = 256 * 1024
MAX_TOTAL_BYTES = 2 * 1024 * 1024
MAX_ENTRIES = 128
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
EXPECTED = {
    ".agents/policy": ("policy", "aiset-policy", (
        "evidence.md", "metadata.json", "security-boundaries.md",
    )),
    ".agents/skills/audit-repository-baseline": ("skill", "audit-repository-baseline", (
        "SKILL.md", "agents/openai.yaml", "assets/resource-rollout.md",
    )),
    ".agents/skills/design-effective-tests": ("skill", "design-effective-tests", (
        "SKILL.md", "agents/openai.yaml", "assets/test-case-card.md",
        "references/test-design-patterns.md",
    )),
    ".agents/skills/qualify-api-contracts": ("skill", "qualify-api-contracts", (
        "SKILL.md", "agents/openai.yaml",
    )),
    ".agents/skills/qualify-package-registry": ("skill", "qualify-package-registry", (
        "SKILL.md", "agents/openai.yaml",
    )),
    ".agents/skills/review-security": ("skill", "review-security", (
        "SKILL.md", "agents/openai.yaml",
    )),
    ".agents/skills/software-engineering": ("skill", "software-engineering", (
        "SKILL.md", "agents/openai.yaml", "assets/engineering-change-record.md",
        "references/engineering-method.md", "references/primary-sources.md",
    )),
}


class CheckFailure(Exception):
    """A fixed diagnostic code, never source bytes or untrusted path content."""


def _require_primitives():
    required = ("O_DIRECTORY", "O_NOFOLLOW", "O_CLOEXEC", "O_NONBLOCK")
    if (not all(hasattr(os, flag) for flag in required)
            or os.open not in os.supports_dir_fd
            or os.stat not in os.supports_dir_fd
            or os.stat not in os.supports_follow_symlinks
            or os.scandir not in os.supports_fd):
        raise CheckFailure("UNSUPPORTED_SAFE_READ_PRIMITIVES")


def _directory_flags():
    return os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC


@contextmanager
def _root_descriptor(root):
    _require_primitives()
    # abspath normalizes caller-supplied dot components without resolving links.
    path = Path(os.path.abspath(os.fspath(root)))
    descriptor = os.open(path.anchor, _directory_flags())
    try:
        for component in path.parts[1:]:
            child = os.open(component, _directory_flags(), dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
        yield descriptor
    finally:
        os.close(descriptor)


def _identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_size,
            info.st_mtime_ns, info.st_ctime_ns, info.st_nlink)


def _unchanged(parent, name, descriptor, before):
    current = os.stat(name, dir_fd=parent, follow_symlinks=False)
    if _identity(current) != _identity(before) or _identity(os.fstat(descriptor)) != _identity(before):
        raise CheckFailure("RESOURCE_CHANGED_DURING_READ")


def _read_regular(parent, name, limit, budget=None):
    info = os.stat(name, dir_fd=parent, follow_symlinks=False)
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
        raise CheckFailure("UNSAFE_FILE_TYPE")
    if info.st_size > limit:
        raise CheckFailure("BYTE_BUDGET_EXCEEDED")
    flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK
    descriptor = os.open(name, flags, dir_fd=parent)
    try:
        opened = os.fstat(descriptor)
        if _identity(opened) != _identity(info) or not stat.S_ISREG(opened.st_mode):
            raise CheckFailure("RESOURCE_CHANGED_DURING_READ")
        chunks, size = [], 0
        while True:
            block = os.read(descriptor, min(65536, limit + 1 - size))
            if not block:
                break
            size += len(block)
            if size > limit:
                raise CheckFailure("BYTE_BUDGET_EXCEEDED")
            if budget is not None:
                budget["bytes"] += len(block)
                if budget["bytes"] > MAX_TOTAL_BYTES:
                    raise CheckFailure("BYTE_BUDGET_EXCEEDED")
            chunks.append(block)
        _unchanged(parent, name, descriptor, opened)
        return b"".join(chunks)
    finally:
        os.close(descriptor)


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise CheckFailure("INVALID_MANIFEST")
        result[key] = value
    return result


def _invalid_number(_value):
    raise CheckFailure("INVALID_MANIFEST")


def _manifest(raw):
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=_object,
                           parse_constant=_invalid_number)
    except (UnicodeError, ValueError, RecursionError):
        raise CheckFailure("INVALID_MANIFEST") from None
    if (not isinstance(value, dict)
            or set(value) != {"schema_version", "provenance", "records"}
            or type(value["schema_version"]) is not int
            or value["schema_version"] != 1):
        raise CheckFailure("INVALID_MANIFEST")
    provenance = value["provenance"]
    if (not isinstance(provenance, dict)
            or set(provenance) != {"git_dirty", "git_revision", "selected_source_sha256"}
            or provenance["git_dirty"] is not False
            or provenance["git_revision"] != SOURCE_REVISION
            or not isinstance(provenance["selected_source_sha256"], str)
            or not SHA256.fullmatch(provenance["selected_source_sha256"])):
        raise CheckFailure("INVALID_PROVENANCE")
    records = value["records"]
    if not isinstance(records, dict) or set(records) != set(EXPECTED):
        raise CheckFailure("UNEXPECTED_MANIFEST_SCOPE")
    tree = {}
    for destination, (kind, name, files) in EXPECTED.items():
        record = records[destination]
        if (not isinstance(record, dict) or set(record) != {"kind", "name", "files"}
                or record["kind"] != kind or record["name"] != name
                or not isinstance(record["files"], dict) or set(record["files"]) != set(files)
                or any(not isinstance(digest, str) or not SHA256.fullmatch(digest)
                       for digest in record["files"].values())):
            raise CheckFailure("INVALID_RESOURCE_RECORD")
        # Only compile the fixed allowlist; no manifest-provided path is opened.
        for relative in files:
            node = tree
            components = (destination + "/" + relative).split("/")
            for component in components[:-1]:
                node = node.setdefault(component, {})
            node[components[-1]] = record["files"][relative]
    if hashlib.sha256(raw).hexdigest() != MANIFEST_SHA256:
        raise CheckFailure("MANIFEST_PIN_MISMATCH")
    return tree[".agents"]


def _names(descriptor, budget):
    names = set()
    with os.scandir(descriptor) as entries:
        for entry in entries:
            budget["entries"] += 1
            if budget["entries"] > MAX_ENTRIES:
                raise CheckFailure("ENTRY_BUDGET_EXCEEDED")
            names.add(entry.name)
    return names


def _walk(parent, name, expected, budget):
    before = os.stat(name, dir_fd=parent, follow_symlinks=False)
    if not stat.S_ISDIR(before.st_mode):
        raise CheckFailure("UNSAFE_DIRECTORY_TYPE")
    descriptor = os.open(name, _directory_flags(), dir_fd=parent)
    try:
        if _identity(before) != _identity(os.fstat(descriptor)):
            raise CheckFailure("RESOURCE_CHANGED_DURING_READ")
        if _names(descriptor, budget) != set(expected):
            raise CheckFailure("RESOURCE_TREE_DRIFT")
        for child, value in expected.items():
            if isinstance(value, dict):
                _walk(descriptor, child, value, budget)
            else:
                actual = _read_regular(descriptor, child, MAX_FILE_BYTES, budget)
                if hashlib.sha256(actual).hexdigest() != value:
                    raise CheckFailure("RESOURCE_HASH_DRIFT")
                budget["files"] += 1
        _unchanged(parent, name, descriptor, before)
    finally:
        os.close(descriptor)


def check(root):
    """Verify the fixed pilot, raising only redacted fixed-category failures."""
    try:
        with _root_descriptor(root) as descriptor:
            raw = _read_regular(descriptor, MANIFEST, MAX_MANIFEST_BYTES)
            expected = _manifest(raw)
            budget = {"entries": 0, "bytes": 0, "files": 0}
            _walk(descriptor, ".agents", expected, budget)
            if _read_regular(descriptor, MANIFEST, MAX_MANIFEST_BYTES) != raw:
                raise CheckFailure("RESOURCE_CHANGED_DURING_READ")
        return {"status": "VERIFIED", "scope": "fixed-pilot-file-readback",
                "source_revision": SOURCE_REVISION, "resources": len(EXPECTED),
                "files": budget["files"], "bytes": budget["bytes"],
                "manifest_pin": "MATCH", "publisher_authentication": "NOT_ESTABLISHED",
                "agent_load": "NOT_RUN", "runtime_smoke": "NOT_RUN",
                "host_qualification": "NOT_ESTABLISHED"}
    except (OSError, ValueError, RecursionError):
        raise CheckFailure("UNREADABLE_OR_UNSAFE_PATH") from None


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1],
                        help="Explicit local checkout; defaults to this script's repository")
    arguments = parser.parse_args(argv)
    try:
        result = check(arguments.root)
    except CheckFailure as exc:
        result = {"status": "FAILED", "scope": "fixed-pilot-file-readback", "reason": str(exc)}
    print(json.dumps(result, sort_keys=True))
    return 0 if result["status"] == "VERIFIED" else 1


if __name__ == "__main__":
    sys.exit(main())

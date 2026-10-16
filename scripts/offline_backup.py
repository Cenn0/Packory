#!/usr/bin/env python3
"""Offline-only, hash-verified backup/restore for Packory's SQLite+filesystem profile.

This deliberately refuses live SQLite WAL files, symlinks and partial restores.
External PostgreSQL and S3 installations require provider-specific backups.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import sqlite3
import tempfile

SCHEMA = 1

def safe_name(name: str) -> bool:
    path = PurePosixPath(name)
    return bool(name) and not name.startswith("/") and "\\" not in name and all(
        part not in ("", ".", "..") for part in name.split("/")
    ) and path.as_posix() == name

def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()

def files_in(folder: Path):
    for root, dirs, files in os.walk(folder, followlinks=False):
        for name in dirs + files:
            child = Path(root) / name
            if child.is_symlink():
                raise ValueError("Refusing symlink inside backup source")
        for filename in files:
            file = Path(root) / filename
            rel = file.relative_to(folder).as_posix()
            if not safe_name(rel) or not file.is_file():
                raise ValueError("Invalid backup entry")
            yield rel, file

def check_sqlite(folder: Path):
    database = folder / "packory.sqlite"
    if not database.is_file() or database.is_symlink():
        raise ValueError("Expected packory.sqlite in offline data directory")
    for suffix in ("-wal", "-shm"):
        if (folder / ("packory.sqlite" + suffix)).exists():
            raise ValueError("SQLite WAL/SHM still present. Stop Packory and checkpoint SQLite first.")
    with sqlite3.connect(f"file:{database}?mode=ro&immutable=1", uri=True) as connection:
        if connection.execute("PRAGMA quick_check").fetchone()[0] != "ok":
            raise ValueError("SQLite integrity check failed")

def manifest_for(folder: Path) -> dict:
    entries = []
    for rel, path in sorted(files_in(folder)):
        if rel == "manifest.json":
            raise ValueError("Data root cannot contain reserved manifest.json")
        entries.append({"path": rel, "size": path.stat().st_size, "sha256": digest(path)})
    return {"schema": SCHEMA, "profile": "sqlite-filesystem-offline", "entries": entries}

def verify(folder: Path) -> dict:
    data = json.loads((folder / "manifest.json").read_text(encoding="utf-8"))
    if data.get("schema") != SCHEMA or data.get("profile") != "sqlite-filesystem-offline":
        raise ValueError("Unknown backup format")
    actual = {}
    for entry in data.get("entries", []):
        rel, size, sha = entry["path"], entry["size"], entry["sha256"]
        if (not isinstance(rel, str) or not safe_name(rel) or rel == "manifest.json"
            or rel in actual or not isinstance(size, int) or size < 0
            or not isinstance(sha, str) or len(sha) != 64):
            raise ValueError("Invalid manifest entry")
        actual[rel] = (size, sha)
    observed = {rel: (path.stat().st_size, digest(path))
                for rel, path in files_in(folder) if rel != "manifest.json"}
    if actual != observed:
        raise ValueError("Missing, unexpected, or modified backup artifact")
    check_sqlite(folder)
    return data

def create(source: Path, destination: Path):
    if destination.exists() or source.resolve() == destination.resolve() or source.resolve() in destination.resolve().parents:
        raise ValueError("Destination must be new and outside the source")
    check_sqlite(source)
    destination.parent.mkdir(parents=True, exist_ok=True)
    staged = Path(tempfile.mkdtemp(prefix=".packory-backup-", dir=destination.parent))
    try:
        for rel, path in files_in(source):
            target = staged / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
        manifest = manifest_for(staged)
        (staged / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        verify(staged)
        staged.rename(destination)
    finally:
        if staged.exists():
            shutil.rmtree(staged)

def restore(source: Path, destination: Path):
    verify(source)
    if destination.exists():
        raise ValueError("Restore destination must not exist; never overwrite a running installation")
    destination.parent.mkdir(parents=True, exist_ok=True)
    staged = Path(tempfile.mkdtemp(prefix=".packory-restore-", dir=destination.parent))
    try:
        for rel, path in files_in(source):
            if rel == "manifest.json":
                continue
            target = staged / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
        check_sqlite(staged)
        staged.rename(destination)
    finally:
        if staged.exists():
            shutil.rmtree(staged)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("create", "verify", "restore"))
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--destination", type=Path)
    parser.add_argument("--offline-confirmed", action="store_true",
                        help="Confirm that Packory is stopped and no writes can occur")
    args = parser.parse_args()
    if args.operation != "verify" and not args.offline_confirmed:
        parser.error("Create/restore require --offline-confirmed")
    if args.operation != "verify" and args.destination is None:
        parser.error("Create/restore require --destination")
    if args.operation == "create":
        create(args.source, args.destination)
    elif args.operation == "restore":
        restore(args.source, args.destination)
    else:
        verify(args.source)
    print("Packory offline backup operation verified.")

if __name__ == "__main__":
    main()

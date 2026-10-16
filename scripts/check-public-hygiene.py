#!/usr/bin/env python3
"""Fail CI if tracked public source contains address-shaped strings or secret files.

This is a conservative guard, not a replacement for human review, secret scanning,
or a Git history / commit metadata audit.
"""
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
ADDRESS = re.compile(rb"\b[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}\b")
FORBIDDEN_FILENAMES = {".env", ".npmrc", "id_rsa", "id_ed25519"}
FORBIDDEN_SUFFIXES = {".pem", ".p12", ".pfx", ".key", ".sqlite", ".db"}
EXCLUDED_FOLDERS = {".git", "bin", "obj", "TestResults", "data", "node_modules"}


def tracked_files() -> list[Path]:
    if (ROOT.joinpath('.git').exists()):
        process = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT,
                                 capture_output=True, check=True)
        return [ROOT / part.decode() for part in process.stdout.split(b"\0") if part]
    return [file for file in ROOT.rglob("*") if file.is_file() and
            not (set(file.relative_to(ROOT).parts) & EXCLUDED_FOLDERS)]


def main() -> int:
    problems: list[str] = []
    for file in tracked_files():
        rel = file.relative_to(ROOT)
        if file.is_symlink() or not file.is_file():
            continue
        if file.name in FORBIDDEN_FILENAMES or file.suffix in FORBIDDEN_SUFFIXES:
            problems.append(f"{rel}: secret-prone file name is not allowed")
            continue
        try:
            data = file.read_bytes()
        except OSError:
            problems.append(f"{rel}: unreadable tracked file")
            continue
        if ADDRESS.search(data):
            problems.append(f"{rel}: contains an email-address-shaped string")
    for problem in problems:
        print(problem, file=sys.stderr)
    if problems:
        print("Public hygiene check failed; inspect files without logging their values.", file=sys.stderr)
        return 1
    print("Public source hygiene check passed (commit metadata must be reviewed separately).")
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Fail-closed privacy and common-secret guard for the PUBLIC Packory repository.

Scans tracked source/docs only, plus checked-out commit identity. It never prints
detected private addresses or credential values, only relative location + category.
Not a substitute for a full third-party secret scanner or history remediation.
"""
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
EMAIL = re.compile(r"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", re.I)
SECRET_PATTERNS = {
    "private key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----"),
    "GitHub credential": re.compile(r"(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{45,})"),
    "AWS access key": re.compile(r"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"),
    "Slack credential": re.compile(r"\bxox[baprs]-[0-9A-Za-z-]{24,}\b"),
}
EXAMPLE_DOMAINS = frozenset({"example.com", "example.org", "example.net", "example.test", "example.invalid"})
BLOCKED_FILENAMES = frozenset({".env", ".npmrc", ".pypirc", ".netrc", "id_rsa", "id_ed25519"})
ALLOWED_EXTENSIONS = frozenset({
    ".cs", ".csproj", ".slnx", ".md", ".txt", ".py", ".js", ".cjs", ".mjs", ".ts", ".tsx",
    ".html", ".css", ".scss", ".json", ".jsonl", ".xml", ".yml", ".yaml",
    ".sh", ".bash", ".toml", ".ini", ".config", ".props", ".targets", ".svg",
})
ALLOWED_BASENAMES = frozenset({".gitignore", ".dockerignore", "dockerfile", "makefile", "license"})


def permitted_email(value: str) -> bool:
    value = value.lower()
    if value.endswith("@users.noreply.github.com") or value == "noreply@github.com":
        return True
    return value.rsplit("@", 1)[-1] in EXAMPLE_DOMAINS


def scan_text(path: str, contents: str) -> list[str]:
    errors = []
    for lineno, line in enumerate(contents.splitlines(), 1):
        if any(not permitted_email(match.group()) for match in EMAIL.finditer(line)):
            errors.append(f"{path}:{lineno}: non-example contact address; remove or use noreply identity")
        for label, pattern in SECRET_PATTERNS.items():
            if pattern.search(line):
                errors.append(f"{path}:{lineno}: possible {label}; remove from public source")
    return errors


def scanned_file(path: Path) -> bool:
    return path.suffix.lower() in ALLOWED_EXTENSIONS or path.name.lower() in ALLOWED_BASENAMES


def check_paths(repo: Path, names: list[str]) -> list[str]:
    problems = []
    for name in names:
        file = repo / name
        if (file.name.lower() in BLOCKED_FILENAMES or
                file.suffix.lower() in {".pem", ".p12", ".pfx", ".key"} or
                file.name.lower().startswith(".env.")):
            problems.append(f"{name}: sensitive credential file tracked in public Git")
            continue
        if not scanned_file(file):
            continue  # binary artifacts require separate inspection and approval
        if file.is_symlink():
            problems.append(f"{name}: symlinks in source require manual review")
            continue
        try:
            contents = file.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            problems.append(f"{name}: expected inspectable UTF-8 text")
            continue
        problems.extend(scan_text(name, contents))
    return problems


def git(repo: Path, *arguments: str) -> bytes:
    return subprocess.check_output(["git", "-C", str(repo), *arguments], stderr=subprocess.DEVNULL)


def check_current_commit(repo: Path) -> list[str]:
    try:
        identities = git(repo, "show", "-s", "--format=%ae%n%ce", "HEAD").decode("utf-8").splitlines()
    except (subprocess.CalledProcessError, UnicodeDecodeError):
        return ["HEAD: unable to verify commit identity"]
    if len(identities) != 2 or any(not permitted_email(value) for value in identities):
        return ["HEAD: public commit author/committer must use GitHub noreply identities"]
    return []


def main():
    try:
        files = [p.decode("utf-8") for p in git(ROOT, "ls-files", "-z").split(b"\x00") if p]
    except (subprocess.CalledProcessError, UnicodeDecodeError):
        print("FAIL: unable to read tracked public repository files")
        return 1
    problems = check_paths(ROOT, files) + check_current_commit(ROOT)
    for problem in problems:
        print("FAIL: " + problem)
    print(f"Public privacy gate: {len(files)} tracked paths inspected, {len(problems)} finding(s).")
    return int(bool(problems))


if __name__ == "__main__":
    sys.exit(main())

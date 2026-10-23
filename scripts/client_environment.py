#!/usr/bin/env python3
"""Capture allowlisted client versions, never raw output, paths or identities."""
import argparse
import json
from pathlib import Path
import platform
import re
import subprocess

VERSION = r"([0-9]+\.[0-9]+(?:\.[0-9]+)?(?:[-+][A-Za-z0-9.-]+)?)"
CLIENTS = {
    "dotnet": (["dotnet", "--version"], r"^" + VERSION + r"\s*$"),
    "node": (["node", "--version"], r"^v" + VERSION + r"\s*$"),
    "npm": (["npm", "--version"], r"^" + VERSION + r"\s*$"),
    "pnpm": (["pnpm", "--version"], r"^" + VERSION + r"\s*$"),
    "yarn": (["yarn", "--version"], r"^" + VERSION + r"\s*$"),
    "cargo": (["cargo", "--version"], r"^cargo " + VERSION + r"(?:\s|$)"),
    "python": (["python3", "--version"], r"^Python " + VERSION + r"\s*$"),
    "pip": (["python3", "-m", "pip", "--version"], r"^pip " + VERSION + r"(?:\s|$)"),
    "twine": (["python3", "-m", "twine", "--version"], r"^twine version " + VERSION + r"(?:\s|$)"),
    "go": (["go", "version"], r"^go version go" + VERSION + r"(?:\s|$)"),
    "maven": (["mvn", "--version"], r"^Apache Maven " + VERSION + r"(?:\s|$)"),
    "gradle": (["gradle", "--version"], r"^Gradle " + VERSION + r"\s*$"),
    "docker": (["docker", "--version"], r"^Docker version " + VERSION + r"(?:,|\s|$)"),
    "helm": (["helm", "version", "--short"], r"^v" + VERSION + r"\s*$"),
    "curl": (["curl", "--version"], r"^curl " + VERSION + r"(?:\s|$)"),
}


def collect(sha, run=subprocess.run):
    if re.fullmatch(r"[0-9a-f]{40}", sha) is None:
        raise ValueError("Exact source SHA required")
    clients = {}
    for name, (command, pattern) in CLIENTS.items():
        try:
            result = run(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                         text=True, timeout=5, check=False)
            # Strip terminal colour codes but retain neither raw text nor paths.
            output = re.sub(r"\x1b\[[0-9;]*m", "", result.stdout[:8192])
            match = re.search(pattern, output, re.MULTILINE) if result.returncode == 0 else None
            clients[name] = ({"status": "observed", "version": match.group(1)} if match else
                             {"status": "unavailable", "reason": "failed_or_unrecognized_version"})
        except FileNotFoundError:
            clients[name] = {"status": "unavailable", "reason": "not_installed"}
        except (OSError, subprocess.TimeoutExpired, UnicodeError):
            clients[name] = {"status": "unavailable", "reason": "execution_unavailable"}
    os_info = {}
    try:
        for line in Path("/etc/os-release").read_text(encoding="utf-8").splitlines():
            key, _, value = line.partition("=")
            value = value.strip('"')
            if key in {"ID", "VERSION_ID"} and re.fullmatch(r"[A-Za-z0-9._-]{1,64}", value):
                os_info[key] = value
    except (OSError, UnicodeError):
        pass
    system, machine = platform.system(), platform.machine()
    return {
        "schema": "packory.client-environment/1", "sourceSha": sha,
        "qualification": "versions-only", "productionRecommended": False,
        "environment": {
            "system": system if system in {"Linux", "Windows", "Darwin"} else "unknown",
            "architecture": machine if machine in {"x86_64", "aarch64", "arm64", "armv7l", "AMD64"} else "unknown",
            "osId": os_info.get("ID", "unknown"), "osVersion": os_info.get("VERSION_ID", "unknown"),
        }, "clients": clients,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if args.output.is_symlink() or args.output.exists():
        parser.error("Output must be a new file")
    try:
        result = collect(args.sha)
        with args.output.open("x", encoding="utf-8") as output:
            output.write(json.dumps(result, indent=2) + "\n")
    except (ValueError, OSError):
        print("Client version capture failed; no raw diagnostic output retained")
        return 1
    print("Recorded allowlisted client versions; unavailable clients are not qualified")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Bounded, privacy-safe evidence ledger for single-node Docker acceptance.

This is a *self-reported evidence validator*, not a Docker/TLS test runner.
It never connects to a service, reads secrets, performs restores, or promotes images.
"""
import argparse
from collections import Counter
import json
from pathlib import Path
import re

SCHEMA = "packory.single-node-acceptance/1"
PROFILE = "single-writer-sqlite-filesystem-tls-v1"
SHA = re.compile(r"\A[0-9a-f]{40}\Z")
DIGEST = re.compile(r"\Asha256:[0-9a-f]{64}\Z")
VERSION = re.compile(r"\A[0-9]{1,4}(?:\.[0-9]{1,4}){1,3}(?:[-+][A-Za-z0-9.-]{1,32})?\Z")
CLIENTS = ("dotnet", "npm", "docker", "mcp_sdk", "docker_engine", "docker_compose")
METRICS = (
    "idle_cpu_percent", "idle_ram_mib", "idle_disk_mib",
    "concurrent_cpu_percent", "concurrent_ram_mib", "concurrent_disk_mib",
    "p95_publish_ms", "p95_download_ms",
)
CASES = (
    "tls_liveness", "tls_readiness", "authorized_native_read",
    "authorized_native_publish", "anonymous_write_denied", "read_only_write_denied",
    "invalid_credential_denied", "nuget_publish_restore_hash",
    "npm_publish_install_hash", "recreate_immutable_hashes",
    "boot_startup_persistence", "offline_backup_verify",
    "isolated_restore_native_hash", "upgrade_schema_compatibility",
    "rollback_via_restore", "idle_resource_sample", "bounded_concurrency_sample",
    "disk_full_or_quota", "scanner_failure", "upstream_outage",
)
STATES = ("passed", "failed", "skipped", "not_run")
# A status cannot be explained by a reason that contradicts execution.
# In particular, "not_run" must never be confused with an executed failure.
STATUS_REASONS = {
    "failed": frozenset(("assertion_failed", "timeout")),
    "skipped": frozenset(("missing_tool", "unavailable_provider",
                          "unsafe_to_run", "operator_not_authorized")),
    "not_run": frozenset(("not_executed",)),
}


class EvidenceError(ValueError):
    """A deliberately path- and input-free validation error."""


def exact_keys(value, keys, label):
    if type(value) is not dict or set(value) != set(keys):
        raise EvidenceError("Invalid " + label + " structure.")


def sha(value):
    return isinstance(value, str) and SHA.fullmatch(value) is not None and value != "0" * 40


def digest(value):
    return isinstance(value, str) and DIGEST.fullmatch(value) is not None and value != "sha256:" + "0" * 64


def number(value, lower, upper):
    return type(value) in (int, float) and lower <= value <= upper


def reject_duplicate_keys(items):
    result = {}
    for key, value in items:
        if key in result:
            raise EvidenceError("Duplicate evidence field.")
        result[key] = value
    return result


def read(path):
    try:
        with path.open("rb") as handle:
            raw = handle.read(65537)
        if len(raw) > 65536:
            raise EvidenceError("Evidence exceeds the 64 KiB limit.")
        return json.loads(raw.decode("utf-8"), object_pairs_hook=reject_duplicate_keys,
                          parse_constant=lambda _: (_ for _ in ()).throw(EvidenceError("Invalid numeric field.")))
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise EvidenceError("Evidence file cannot be read as bounded UTF-8 JSON.") from None


def template():
    return {
        "schema": SCHEMA,
        "profile": PROFILE,
        "source": {
            "sha": "",
            "image_revisions": {"app": "", "scanner": ""},
            "image_digests": {"app": "", "scanner": ""},
        },
        "clients": {name: "" for name in CLIENTS},
        "boot_scope": "not_tested",
        "workload": {"concurrent_clients": 4, "duration_seconds": 300,
                     "artifact_bytes": 4096},
        "measurements": {name: None for name in METRICS},
        "cases": [{"id": case, "status": "not_run", "reason": "not_executed"}
                  for case in CASES],
    }


def validate(data):
    exact_keys(data, ("schema", "profile", "source", "clients", "boot_scope",
                      "workload", "measurements", "cases"), "evidence")
    if data["schema"] != SCHEMA or data["profile"] != PROFILE:
        raise EvidenceError("Unexpected acceptance schema or profile.")
    source = data["source"]
    exact_keys(source, ("sha", "image_revisions", "image_digests"), "source")
    for key in ("image_revisions", "image_digests"):
        exact_keys(source[key], ("app", "scanner"), key)
    if not sha(source["sha"]) or any(
        source["image_revisions"][kind] != source["sha"] for kind in ("app", "scanner")
    ):
        raise EvidenceError("Source SHA and both declared image revisions must match.")
    if any(not digest(source["image_digests"][kind]) for kind in ("app", "scanner")):
        raise EvidenceError("Both immutable image digests are required.")
    if source["image_digests"]["app"] == source["image_digests"]["scanner"]:
        raise EvidenceError("App and scanner must have distinct immutable image digests.")

    clients = data["clients"]
    exact_keys(clients, CLIENTS, "client versions")
    if any(not isinstance(clients[key], str) or
           VERSION.fullmatch(clients[key]) is None for key in CLIENTS):
        raise EvidenceError("Pinned numerical client versions are required.")

    if data["boot_scope"] not in ("not_tested", "disposable_host_reboot",
                                  "independent_daemon_restart_equivalent"):
        raise EvidenceError("Boot test scope must be explicitly declared.")
    workload = data["workload"]
    exact_keys(workload, ("concurrent_clients", "duration_seconds", "artifact_bytes"), "workload")
    for key, low, high in (("concurrent_clients", 1, 64),
                           ("duration_seconds", 1, 3600),
                           ("artifact_bytes", 1024, 100 * 1024 * 1024)):
        value = workload[key]
        if type(value) is not int or not low <= value <= high:
            raise EvidenceError("Workload is outside the bounded reference profile.")

    measurements = data["measurements"]
    exact_keys(measurements, METRICS, "measurements")
    for key in METRICS:
        value = measurements[key]
        if value is not None and not number(value, 0, 1_000_000):
            raise EvidenceError("Invalid or non-finite resource measurement.")
    for key in ("idle_cpu_percent", "concurrent_cpu_percent"):
        value = measurements[key]
        if value is not None and not number(value, 0, 100):
            raise EvidenceError("CPU measurement must be in percent (0..100).")

    scenarios = data["cases"]
    if type(scenarios) is not list or len(scenarios) != len(CASES):
        raise EvidenceError("Every acceptance scenario must be present exactly once.")
    outcomes = {}
    for entry in scenarios:
        exact_keys(entry, ("id", "status", "reason"), "scenario")
        case_id, state, reason = entry["id"], entry["status"], entry["reason"]
        if not isinstance(case_id, str) or case_id not in CASES or case_id in outcomes:
            raise EvidenceError("Unknown or duplicate acceptance scenario.")
        if not isinstance(state, str) or state not in STATES:
            raise EvidenceError("Invalid scenario outcome.")
        if state == "passed":
            if reason is not None:
                raise EvidenceError("Passing case must not include a failure/skip reason.")
        elif reason not in STATUS_REASONS[state]:
            raise EvidenceError("Non-passing case requires a reason compatible with its status.")
        outcomes[case_id] = state

    if set(outcomes) != set(CASES):
        raise EvidenceError("A required acceptance scenario is missing.")
    if outcomes["boot_startup_persistence"] == "passed" and data["boot_scope"] == "not_tested":
        raise EvidenceError("Passing boot persistence requires a declared actual test scope.")

    for name, prefix in (("idle_resource_sample", "idle_"),
                         ("bounded_concurrency_sample", "concurrent_")):
        if outcomes[name] == "passed" and any(
            measurements[key] is None for key in METRICS if key.startswith(prefix)
        ):
            raise EvidenceError("Passed resource sampling requires numerical measurements.")
    if outcomes["bounded_concurrency_sample"] == "passed" and (
        measurements["p95_publish_ms"] is None or measurements["p95_download_ms"] is None
    ):
        raise EvidenceError("Passed concurrent workload requires latency percentiles.")

    counts = Counter(outcomes.values())
    return counts, all(outcome == "passed" for outcome in outcomes.values())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    choice = parser.add_mutually_exclusive_group(required=True)
    choice.add_argument("--template", type=Path, help="Create a new, unexecuted evidence ledger")
    choice.add_argument("--validate", type=Path, help="Validate one self-reported evidence ledger")
    args = parser.parse_args()
    try:
        if args.template is not None:
            with args.template.open("x", encoding="utf-8") as handle:
                json.dump(template(), handle, indent=2)
                handle.write("\n")
            print("Unexecuted evidence template created; no acceptance has been performed.")
            return 0
        counts, complete = validate(read(args.validate))
        print("Self-reported cases: " +
              ", ".join(f"{name}={counts[name]}" for name in STATES))
        print("This ledger does not independently verify execution, TLS, hash checks or release readiness.")
        if complete:
            print("All required outcomes are self-reported as passed; independent review remains required.")
            return 0
        print("Acceptance incomplete or failed; release remains blocked.")
        return 2
    except (EvidenceError, OSError) as error:
        # The exception types above never preserve user-supplied paths or bytes.
        print("Rejected unsafe or incomplete evidence structure." if isinstance(error, OSError)
              else str(error))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

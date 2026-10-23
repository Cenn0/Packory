#!/usr/bin/env python3
"""Fail-closed exact-revision Actions qualification; no operational certification.

Only the fixed public repository's read-only Actions API is contacted. Never
persist raw responses: they contain identities and other unnecessary metadata.
"""
import argparse
import json
import os
from pathlib import Path
import re
import sys
import time
import urllib.request

REPOSITORY = "Cenn0/Packory"
API = "https://api.github.com/repos/" + REPOSITORY
REQUIRED = {
    "keycloak.yml": {"Real signed Keycloak JWT"},
    "mcp-typescript.yml": {"Official TS SDK v2.3.1 HTTP conformance"},
    "native-matrix.yml": {"Cargo and pip (Python 3.12)", "Cargo and pip (Python 3.13)",
                          "npm pnpm Yarn Classic private lockfiles"},
    "postgres.yml": {"postgres"},
    "public-docs.yml": {"documentation"},
    "s3.yml": {"S3 backend integration"},
    "skillspector-smoke.yml": {"scanner"},
    "source-evidence.yml": {"Exact source evidence"},
}
REQUIRED_STEPS = {
    "Real signed Keycloak JWT": {"Real Keycloak OIDC issuer, audience and authorization"},
    "Official TS SDK v2.3.1 HTTP conformance": {"Exercise independent HTTP client"},
    "Cargo and pip (Python 3.12)": {"Real hosted Cargo and pip workflows"},
    "Cargo and pip (Python 3.13)": {"Real hosted Cargo and pip workflows"},
    "npm pnpm Yarn Classic private lockfiles": {
        "Verify isolated private native consumers and locked fresh-cache installs"},
    "postgres": {"Run the full suite with PostgreSQL provider checks enabled"},
    "documentation": {"Validate public README and local links"},
    "S3 backend integration": {
        "Test coordinated PostgreSQL and versioned Moto S3 snapshot and isolated restore (emulator only)",
        "Test real AWS S3 client requests",
        "Test PostgreSQL-S3 publication recovery and exclusive writer lease",
        "Test private feed bundles with PostgreSQL and S3 including cold reads and corruption",
        "Verify native private feeds with PostgreSQL and S3 (emulator only)",
    },
    "scanner": {"Scan a synthetic benign Skill in a networkless runtime",
                "Test Compose scan, approval, MCP distribution and scanner outage recovery",
                "Integration smoke - compose staging, offline scan, approval and revoke"},
    "Exact source evidence": {"Verify source identity and public privacy",
                              "Verify generated source archive digest and narrow qualification claim"},
}


class Blocked(ValueError):
    pass


class Pending(Blocked):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise Blocked("unexpected_api_redirect")


def api_reader(token):
    opener = urllib.request.build_opener(NoRedirect)

    def read(path):
        # API paths are constructed locally, never taken from a response URL.
        if not path.startswith("/actions/") or ".." in path or "#" in path:
            raise Blocked("invalid_api_path")
        request = urllib.request.Request(API + path, headers={
            "Accept": "application/vnd.github+json",
            "Authorization": "Bearer " + token,
            "X-GitHub-Api-Version": "2026-03-10",
        })
        with opener.open(request, timeout=20) as response:
            raw = response.read(4 * 1024 * 1024 + 1)
        if len(raw) > 4 * 1024 * 1024:
            raise Blocked("oversized_api_response")
        return json.loads(raw)
    return read


def positive_int(value):
    return type(value) is int and value > 0


def qualify(read, sha, event):
    if not re.fullmatch(r"[0-9a-f]{40}", sha) or event not in {"push", "pull_request"}:
        raise Blocked("invalid_source_identity")
    if set(REQUIRED_STEPS) != set().union(*REQUIRED.values()):
        raise Blocked("incomplete_required_step_policy")
    evidence = []
    pending = False
    for filename, names in REQUIRED.items():
        page = read("/actions/workflows/" + filename +
                    "/runs?head_sha=" + sha + "&event=" + event + "&per_page=100")
        runs = page["workflow_runs"]
        if type(runs) is not list or type(page["total_count"]) is not int or page["total_count"] != len(runs):
            raise Blocked("incomplete_workflow_inventory")
        candidates = [r for r in runs if r["head_sha"] == sha and r["event"] == event]
        if not candidates:
            pending = True
            continue
        if any(not positive_int(r["id"]) or not positive_int(r["run_attempt"]) for r in candidates):
            raise Blocked("invalid_run_identity")
        run = max(candidates, key=lambda r: r["id"])
        if (run["path"].split("@", 1)[0] != ".github/workflows/" + filename or
                run["repository"]["full_name"] != REPOSITORY or
                (event == "push" and run["head_branch"] != "main")):
            raise Blocked("workflow_identity_mismatch")
        if run["status"] != "completed":
            pending = True
            continue
        if run["conclusion"] != "success":
            raise Blocked("unsuccessful_workflow:" + filename)
        run_id, attempt = run["id"], run["run_attempt"]
        jobs = []
        for number in range(1, 11):
            page = read(f"/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100&page={number}")
            if type(page["jobs"]) is not list or type(page["total_count"]) is not int:
                raise Blocked("invalid_job_inventory")
            jobs.extend(page["jobs"])
            if len(jobs) == page["total_count"]:
                break
            if not page["jobs"] or len(jobs) > page["total_count"]:
                raise Blocked("incomplete_job_inventory")
        else:
            raise Blocked("job_inventory_limit")
        if not jobs or not names.issubset({j["name"] for j in jobs}):
            raise Blocked("missing_required_job:" + filename)
        if len({j["id"] for j in jobs}) != len(jobs):
            raise Blocked("duplicate_job_identity")
        validated_steps = []
        for job in jobs:
            if (not positive_int(job["id"]) or job["run_id"] != run_id or
                    job["head_sha"] != sha or job["status"] != "completed" or
                    job["conclusion"] != "success"):
                raise Blocked("unsuccessful_or_mismatched_job:" + filename)
            if job["name"] not in names:
                continue
            steps = job.get("steps")
            if type(steps) is not list or not steps:
                raise Blocked("missing_step_inventory:" + filename)
            if any(not positive_int(step.get("number")) for step in steps) or \
                    len({step["number"] for step in steps}) != len(steps):
                raise Blocked("invalid_step_identity:" + filename)
            for required in REQUIRED_STEPS[job["name"]]:
                matches = [step for step in steps if step.get("name") == required]
                if len(matches) != 1 or matches[0].get("status") != "completed" or \
                        matches[0].get("conclusion") != "success":
                    raise Blocked("required_step_not_successful:" + filename)
                validated_steps.append({"jobId": job["id"], "number": matches[0]["number"],
                                        "name": required})
        # Re-read after the job inventory: a newly queued rerun invalidates it.
        current = read(f"/actions/runs/{run_id}")
        if (current["run_attempt"] != attempt or current["head_sha"] != sha or
                current["status"] != "completed" or current["conclusion"] != "success"):
            raise Pending("workflow_changed_during_verification")
        evidence.append({"workflow": filename, "runId": run_id, "attempt": attempt,
                         "jobs": sorted(j["id"] for j in jobs),
                         "validatedSteps": sorted(validated_steps,
                                                  key=lambda step: (step["jobId"], step["number"])),
                         "url": f"https://github.com/{REPOSITORY}/actions/runs/{run_id}"})
    if pending:
        raise Pending("required_workflow_missing_or_running")
    return {"schema": "packory.ci-qualification/2", "sourceSha": sha,
            "event": event, "qualification": "ci-workflows-only",
            "productionRecommended": False, "independentApprovalVerified": False,
            "workflows": evidence}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--event", required=True, choices=("push", "pull_request"))
    parser.add_argument("--wait-seconds", type=int, default=0)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    if not 0 <= args.wait_seconds <= 1800 or not os.getenv("GH_TOKEN"):
        print("BLOCKED: missing API credential or invalid wait bound", file=sys.stderr)
        return 1
    # No stale successful index may survive a failed new verification.
    if args.output.is_symlink() or args.output.exists():
        print("BLOCKED: output must be a new local file", file=sys.stderr)
        return 1
    deadline = time.monotonic() + args.wait_seconds
    try:
        while True:
            try:
                result = qualify(api_reader(os.environ["GH_TOKEN"]), args.sha, args.event)
                break
            except Pending:
                if time.monotonic() >= deadline:
                    raise Blocked("qualification_timeout")
                time.sleep(min(20, max(0, deadline - time.monotonic())))
        with args.output.open("x", encoding="utf-8") as stream:
            stream.write(json.dumps(result, indent=2) + "\n")
    except Exception:
        # Never echo response bodies, headers, credentials, URLs or identities.
        print("BLOCKED: exact-revision workflow qualification failed", file=sys.stderr)
        return 1
    print("VERIFIED: eight workflow inventories for " + args.sha)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

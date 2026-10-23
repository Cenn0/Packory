# Single-node Docker acceptance and recovery profile (issue #133)

This is a **qualification plan and sanitized evidence ledger**, not an
automatically qualified release. Packory must remain a **single writer** with
SQLite/filesystem and its isolated SkillSpector scanner. For HTTPS access,
use the optional [private ingress reference](reverse-proxy.md), which itself
requires native-client/TLS qualification and is not a live deployment.

**Safety:** Perform disruptive restart, full-disk, backup/restore, upgrade,
rollback and worker-outage tests on a **disposable, isolated host and volumes
only**, under an explicit operator change authorization. Never run them
against an installed Packory service merely because this guide exists.
This repository does not execute or schedule those operations automatically.

## Exact-version, bounded inputs

- Record the exact Packory source **40-hex commit SHA** and the **two separate
  immutable image digests**, together with the declared source revision label
  of each image. Confirm image contents with the independent anonymous
  [delivery verifier](container-images.md), not only Docker tag strings.
- Record pinned numerical versions of .NET, npm, Docker client/daemon, Docker
  Compose and official MCP SDK. Keep the workload synthetic: a bounded
  artifact of declared size, four concurrent clients and five minutes is
  the default **test definition**, not a measured capacity limit.
- Use a throwaway TLS origin and operator-generated protected token files.
  Do not put credential values, host names, usernames, IPs, private package
  names, filesystem paths, command output or raw logs in a public report.
- Preserve the current development-image designation; any stable release
  still requires the independent [production release gates](release-gates.md).

## Disposable-host acceptance sequence

| Stage | Required execution and observation | Boundary |
| --- | --- | --- |
| 1. Startup | Run both digest-pinned images via `compose.proxy.yaml`, with correctly configured TLS and non-root secret access. Test `/health/live` and `/health/ready` over **HTTPS** | A 200 response alone is insufficient |
| 2. IAM and native clients | Exercise authorized read/publish and rejected anonymous, read-only and invalid credentials. Publish/consume synthetic NuGet and npm artifacts with pinned native CLIs and digest comparisons | No browser HTML login redirects for registry endpoints |
| 3. Recreation | Recreate application/scanner with volumes preserved; repeat original artifact/hash checks | Ensure external advertised URLs remain HTTPS |
| 4. Boot semantics | Test an actual disposable-host reboot **or** independently specified Docker daemon-restart equivalent. Explicitly record which was executed | A `restart:` declaration is not proof |
| 5. Offline recovery | Stop all writers; export the **entire** SQLite/artifact volume to an isolated local directory. Reject live `-wal`/`-shm` copies, create/verify a full backup, restore into **new, separate** directories/volumes and retest native downloads/hashes | No reuse of existing production volumes or live filesystem snapshots |
| 6. Upgrade and rollback | Make a verified backup before an image upgrade. Test schema compatibility and a rollback **by restoring matching database and artifact bytes** with their original image; repeat native checks | An older image cannot reverse schema migrations |
| 7. Capacity and adversity | Measure idle and bounded concurrent CPU/RAM/disk plus p95 publish/download latency; deliberately test disk-full or quota, scanner failure and upstream outage in isolation | No universal minimum capacity implied |
| 8. Evidence | Record each named case as passed, failed, skipped or not run; attach only a sanitized exact-source ledger for independent review | No secret-containing raw logs or "certified" flag |

Reuse the repository's real [native client harnesses](ci-qualification.md)
and the [offline backup implementation](../scripts/offline_backup.py). To run
the offline backup tooling on a **locally extracted, stopped and exclusively
operator-owned data directory** (never a live Docker volume mount):

```bash
python3 scripts/offline_backup.py create \
  --source /disposable/local/source --destination /disposable/local/new-backup \
  --offline-confirmed
python3 scripts/offline_backup.py verify --source /disposable/local/new-backup
python3 scripts/offline_backup.py restore \
  --source /disposable/local/new-backup --destination /disposable/local/fresh-restore \
  --offline-confirmed
```

The backup tool rejects destination overwrites, active WAL/SHM and symlinks.
Copying out of a running container is **not** equivalent to exclusive offline
source ownership. Confirm all scanners, workers, shared mounts and other
writers have been stopped before exporting data.

## Sanitized machine-readable evidence

Generate an **unexecuted** template and fill outcomes only from actual
independent observations (keep it outside Git until reviewed):

```bash
python3 scripts/single_node_acceptance.py --template /tmp/packory-acceptance.json
python3 scripts/single_node_acceptance.py --validate /tmp/packory-acceptance.json
```

The validator requires one exact source SHA, both `sha256:` image digests,
both matching image revision labels, six pinned native/runtime versions,
declared boot-test scope, bounded workload and numeric measurements when
claiming resource tests passed. It requires 20 separate case outcomes, with
finite status/reason enums, forbids additional fields and caps input at
64 KiB. Missing or failed cases return nonzero; complete results say
**self-reported**, not independently corroborated.

A `passed` entry has **no cryptographic proof of execution**. The JSON
format intentionally has no fields for arbitrary URLs, comments, tokens,
paths, logs or identity details. Verify actual test commands, timestamp,
runner scope and evidentiary hashes privately against the operator's
approved records before accepting or sharing a sanitized ledger.
No measured host figures or passing real-deployment results are included
in this repository. Production qualification and separate issue #36
remain blocked without real-provider and security/operations evidence.

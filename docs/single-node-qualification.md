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

## Automated CI subset (disposable loopback, not an operator drill)

The `Build Docker image` CI job builds `packory:ci` from the proposed source
and starts it with file-mounted credentials in a uniquely named Compose project.
It runs the following actual operations; check the job result for the exact
commit before claiming they passed:

1. Generate a one-day synthetic CA/server certificate with an IP SAN. Mount
   the PFX read-only for non-root Kestrel; bind HTTP and HTTPS to loopback only.
   Trust the CA **only in fixture processes**, using curl `--cacert`, Linux
   .NET/OpenSSL's `SSL_CERT_FILE`/`SSL_CERT_DIR`, and npm `cafile` with
   `strict-ssl=true`. No system trust-store change or TLS verification bypass.
2. Check HTTPS liveness/readiness, reject wrong-root and wrong-peer-name
   connections with curl's certificate error **60**, and exercise denied
   anonymous/invalid/read-only credentials.
3. Publish/consume synthetic Raw, NuGet and npm packages. Check original NuGet
   package bytes/SHA-256 and npm tarball SHA-512 against the HTTPS-origin
   lockfile. Force container recreation, use fresh native caches and `npm ci`,
   and verify byte equality plus immutable-write conflicts.
4. Gracefully stop the sole writer and confirm clean exit. Export its complete
   mounted data tree, checkpoint **only the offline exported copy**, then run
   `offline_backup.py create` and `verify`. Never remove a WAL to suppress an
   incomplete database snapshot.
5. Restore into a **new Compose project and distinct data volume** before any
   writer starts. Preserve non-root ownership, repeat native HTTPS consumption
   with fresh caches, and compare the original artifact bytes.
6. Publish one post-backup Raw revision, stop that restored installation, and
   restore the original backup into a third project/volume. The original
   packages must match and the post-backup revision must be absent. This proves
   **same-image, same-schema point-in-time rollback by restore**, not a schema
   downgrade or prior-release upgrade.

The cleanup trap removes only the three fixture projects/volumes and the private
`mktemp` directory. No existing installation is selected by a script parameter.
A seven-day `container-client-environment-<SHA>-<attempt>` Actions artifact
records allowlisted runner, native client, Docker CLI/**daemon**, Compose and
OpenSSL versions; absent tools are explicit, and no raw logs or credential
material are uploaded. This version inventory is not proof of execution by itself.

Run only on a **dedicated disposable CI runner** with Docker and passwordless
local `sudo`; first build the local application image as `packory:ci`.
The scanner is intentionally not started. TLS terminates in **Kestrel**, so this
fixture does **not** qualify Nginx/Nginx Proxy Manager, scanner failure, host or
daemon reboot, resource capacity, a previous-release schema upgrade, real
provider recovery or production readiness. Those acceptance cases stay open.
The test certificate/key must never be reused outside the disposable fixture.

Primary configuration references, checked 23 October 2026:
[Microsoft ASP.NET Core 10 container HTTPS](https://learn.microsoft.com/en-us/aspnet/core/security/docker-compose-https?view=aspnetcore-10.0)
and [.NET Linux root-certificate locations](https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography#trusted-root-certificate-locations-on-linux).

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

The validator requires one exact source SHA, two distinct `sha256:` image digests
(one for the application, one for the scanner), both matching image revision
labels, six pinned native/runtime versions,
declared boot-test scope, bounded workload and numeric measurements when
claiming resource tests passed. It requires 20 separate case outcomes, with status-consistent reason codes (`failed`: assertion/timeout;
`skipped`: missing tool/provider, unsafe or unauthorized;
`not_run`: not executed), forbids additional fields and caps input at
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

## Opt-in bounded latency probe

`scripts/single_node_load_probe.py` provides an executable **Raw HTTP workload
subset**, separate from the self-reported ledger. Run it only against an
explicitly authorized disposable installation. It creates retained synthetic
immutable Raw versions; it never deletes them or changes deployment settings.

```bash
python3 scripts/single_node_load_probe.py --confirm-isolated-test \
  --origin https://packory.example.invalid \
  --token-file /protected/disposable-write-token \
  --source-sha <40_HEX_SOURCE_REVISION> \
  --workers 4 --operations 16 --payload-bytes 32768 --deadline-seconds 60 \
  --output /protected/new-latency-evidence.json
```

The defaults mean **16 publish/download pairs** across at most four workers,
with 32 KiB per artifact. Maximums are four workers, 40 pairs, 1 MiB each and
a 300-second declared deadline. The CLI runs the workload in a disposable
child process and terminates it when the declared deadline plus a two-second
startup margin expires; process cleanup can add a short bounded interval.
Each network operation also has at most a ten-second I/O timeout. A failed,
redirected, oversized GET or PUT response, mismatched bytes, timeout or
rate-limited operation makes the result fail.

TLS certificate and hostname validation remain enabled. The script disables
environment proxy routing and rejects redirects instead of forwarding the
credential to another destination. The token is read from a bounded absolute
regular file, not an argument or environment variable; symlink/reparse-point
files and control characters are rejected. POSIX files must belong to the
current user or root, may be group-readable, and cannot be group-writable or
world-accessible. On Windows the script checks for reparse points and file
identity but does not inspect NTFS ACLs; the operator must protect the token
and evidence parent directories. Output is created exclusively with requested
mode 0600 and never overwrites prior evidence; POSIX enforces this mode,
while Windows access depends on the inherited ACL.

The JSON contains workload counts, successful pair count, elapsed time and
nearest-rank p95 publish/download latency. It excludes origin, paths, tokens,
response/error bodies and logs. The source SHA is **operator-asserted**, not
remotely attested. Verify both image digests and labels independently before
binding the result to a deployment. Partial results never count as a pass.

This is neither a sustained capacity/SLO test nor a complete native-client
matrix. It does not measure CPU/RAM/disk, reboot a host, inject provider faults,
perform recovery or grant production qualification. The synthetic unit tests
cover parsing, file checks, error redaction and timeouts, including a local
stalled handshake. They do not establish real-provider TLS, proxy or redirect
behavior. Actual execution and certificate validation must be recorded separately.

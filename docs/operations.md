# Operations and security guidance

## Deployment topology

Deploy one Packory instance behind a TLS reverse proxy on a controlled host. The default Compose file publishes `127.0.0.1:8080:8080` and stores data in a named Docker volume. Set `PACKORY_PUBLIC_BASE_URL` to the externally reachable HTTPS origin, because it is embedded in NuGet service-index and npm download URLs. Do **not** enable untrusted forwarded header handling and do **not** put sensitive token material in checked-in examples.

### Environment

| Variable | Default | Meaning |
| --- | --- | --- |
| `PACKORY_BLOB_PROVIDER` | `filesystem` | Select `filesystem` or `s3` artifact backend |
| `PACKORY_S3_BUCKET` | *(unset)* | Required private bucket when using S3 |
| `PACKORY_S3_REGION` | `us-east-1` | AWS signature region |
| `PACKORY_S3_KEY_PREFIX` | `packory` | Object key namespace for Packory |
| `PACKORY_S3_ENDPOINT` | *(unset)* | Optional S3-compatible HTTPS endpoint; omit for AWS S3 |
| `PACKORY_S3_ACCESS_KEY`, `PACKORY_S3_SECRET_KEY` | *(unset)* | Optional paired static secrets; IAM roles preferred |
| `PACKORY_S3_ALLOW_LOOPBACK_HTTP` | `false` | Local test emulator only; remote HTTP is prohibited |
| `PACKORY_METADATA_PROVIDER` | `sqlite` | Select `sqlite` or `postgresql` metadata provider |
| `PACKORY_INSTANCE_ROLE` | `writer` | `writer` holds the exclusive PostgreSQL advisory lease; `reader` is opt-in PostgreSQL/S3 read-only replica with a restricted protocol allowlist |
| `PACKORY_POSTGRES_CONNECTION_STRING` | *(unset)* | PostgreSQL ADO.NET connection string (runtime secret; required for postgresql provider) |
| `PACKORY_DATA_DIRECTORY` | `./data` or `/data` in Docker | SQLite + blob root |
| `PACKORY_PUBLIC_BASE_URL` | `http://localhost:8080` | Absolute origin (no path) advertised to clients |
| `PACKORY_NUGET_UPSTREAM_URL` | `https://api.nuget.org/v3-flatcontainer/` | Trusted NuGet flat container |
| `PACKORY_NUGET_REGISTRATION_URL` | `https://api.nuget.org/v3/registration5-semver2/` | Trusted NuGet registration source |
| `PACKORY_NPM_UPSTREAM_URL` | `https://registry.npmjs.org/` | Trusted npm upstream |
| `PACKORY_ANONYMOUS_READ` | `true` | Permit unauthenticated registry read |
| `PACKORY_OCI_AUTHENTICATED_PING` | `false` | Require writer authentication for Docker `/v2/` negotiation even if generic reads are anonymous; see OCI docs for pull tradeoff |
| `PACKORY_RESERVED_NUGET_PREFIXES` | *(empty)* | Comma-separated private NuGet ID prefixes (e.g. `internal.`); package names under these prefixes never fall back to nuget.org |
| `PACKORY_RESERVED_NPM_SCOPES` | *(empty)* | Comma-separated npm scopes (e.g. `@internal`); packages under these scopes never fall back to registry.npmjs.org |
| `PACKORY_DENIED_NUGET_PREFIXES` | *(empty)* | Comma-separated ID prefixes blocked for both read and publish, case-insensitively |
| `PACKORY_DENIED_NPM_SCOPES` | *(empty)* | Comma-separated npm scopes blocked for both read and publish |
| `PACKORY_OIDC_AUTHORITY` | *(unset)* | HTTPS authority / issuer URL for optional JWT validation |
| `PACKORY_OIDC_AUDIENCE` | *(unset)* | Required JWT resource-server audience; must accompany authority |
| `PACKORY_MAX_RAW_FEED_BYTES` | `0` (disabled) | Logical committed-byte limit independently applied to every private Raw feed; 0–1 TiB. |
| `PACKORY_MAX_RAW_FEED_ARTIFACTS` | `0` (disabled) | Committed asset-count limit independently applied to every private Raw feed; 0–100,000,000. |
| `PACKORY_MAX_BLOB_STORAGE_BYTES` | `0` (unlimited) | Optional single-process filesystem blob capacity; writes fail HTTP 507 if exceeded. Not a S3 quota. |
| `PACKORY_PUBLISH_REQUESTS_PER_MINUTE` | `60` | Maximum attempts per minute in each credential-role partition (admin, write, read, unauthenticated). Range 1–10000; 429 when exceeded |
| `PACKORY_ADMIN_TOKEN` | *(unset)* | Required to enable admin/write access |
| `PACKORY_READ_TOKEN` | *(unset)* | Optional read-only credential |
| `PACKORY_WRITE_TOKEN` | *(unset)* | Optional publish credential |
| `PACKORY_WRITE_NAMESPACES` | *(empty = unrestricted write token)* | Optional comma-separated namespace grants for shared write token: `nuget:internal.*`, `npm:@internal/*`, `oci:team/*`; exact names or suffix `*` prefix matches |
| `PACKORY_OIDC_REQUIRE_NAMESPACE_GRANTS` | `false` | When true, every OIDC `packory.write` request must include matching signed `packory_namespace` claims; administrators remain unrestricted |

All defined tokens must be unique, at least 32 characters. **Unset tokens mean no permission of that type is available**; the service will not silently enable writes. Runtime settings can also be set via the `Packory` JSON section, but do not persist real secrets there.

### Mounted runtime secrets (opt-in Docker profile)

For the six supported sensitive settings below, configure either the normal
environment/JSON setting **or** the corresponding `_FILE` environment variable.
The latter must name one explicit **absolute** path to a mounted, readable regular
UTF-8 file. Paths are resolved once during process startup; changing a file does
**not** rotate an active credential until Packory is restarted.

| Direct variable | File variable |
| --- | --- |
| `PACKORY_ADMIN_TOKEN` | `PACKORY_ADMIN_TOKEN_FILE` |
| `PACKORY_READ_TOKEN` | `PACKORY_READ_TOKEN_FILE` |
| `PACKORY_WRITE_TOKEN` | `PACKORY_WRITE_TOKEN_FILE` |
| `PACKORY_POSTGRES_CONNECTION_STRING` | `PACKORY_POSTGRES_CONNECTION_STRING_FILE` |
| `PACKORY_S3_ACCESS_KEY` | `PACKORY_S3_ACCESS_KEY_FILE` |
| `PACKORY_S3_SECRET_KEY` | `PACKORY_S3_SECRET_KEY_FILE` |

An explicitly present direct environment value (including an empty string), or
a nonempty value bound through the `Packory` configuration section, **conflicts**
with the matching `_FILE` setting and fails startup. Nonexistent, unreadable,
relative, directory, symlink, empty, larger than 16 KiB, invalid UTF-8 and
multi-line files also fail startup using messages that include the setting
name but **not** the path or secret contents. Exactly one terminal LF or CRLF
is removed, with no other trimming; spaces are preserved. API token length,
uniqueness and S3 static key-pair validation are unchanged.

`compose.secrets.yaml` is a separate loopback-only development example using
Compose **file-sourced secrets**, not plaintext environment values:

```bash
# Create three separate, operator-protected files OUTSIDE the repository.
# Set these host paths in your own shell or ignored .env; never commit their values.
export PACKORY_ADMIN_TOKEN_SOURCE=/absolute/private/path/admin-token
export PACKORY_READ_TOKEN_SOURCE=/absolute/private/path/read-token
export PACKORY_WRITE_TOKEN_SOURCE=/absolute/private/path/write-token
docker compose -f compose.secrets.yaml config --quiet
docker compose -f compose.secrets.yaml up -d
```

Secret sources are mounted read-only at `/run/secrets/...`; the application
image runs as its configured non-root `APP_UID`. Verify readability by
that UID before relying on a deployment. With file-sourced Compose secrets,
host-file ownership/mode controls and effective container mount permissions
are platform-dependent; do **not** assume Compose's `uid`, `gid` or `mode`
options can enforce host bind-mount permissions. Keep originals restricted on
the host, protect backup/snapshot scope, exclude secrets from data-volume
backups, container images and Git, and never include their bytes in logs or
bug reports. Rotation requires replacing the source safely and recreating
the Packory container under an authorized maintenance procedure. This reduces
environment/config exposure; it does not protect against a privileged host
administrator or process memory inspection.

### Limited multi-instance topology: one writer plus read-only replicas

After completing the PostgreSQL/S3 recovery procedure, Packory supports an
explicit **read-only replica** role for a narrow set of native protocols.
Run one normal `PACKORY_INSTANCE_ROLE=writer` deployment (default) with
PostgreSQL and S3. Each additional node sets
`PACKORY_INSTANCE_ROLE=reader`, the same PostgreSQL database and S3
bucket/prefix, but a **different local data/cache directory**.

Reader nodes never initialize schemas, reconcile local/remote publication
intents, acquire a writer lease, or accept HTTP mutation methods. Startup
and `/health/ready` require the **exact current metadata schema version and
all core/feed tables**; an uninitialized, partial or future-version database
fails closed. Upgrade the writer and complete the schema migration before
starting version-matched readers. A healthy metadata schema does **not**
validate S3 object integrity or confer production certification. They permit
read-only catalog, skill, Cargo, hosted PyPI and OCI blob/manifest/tag endpoints.
They reject **all** `/nuget/` and `/npm/` traffic, because those adapters
currently write remote metadata or cache bytes even during GET operations.
OCI upload-session URLs and admin APIs are also refused. A refused request
returns HTTP 503. Keep all writes, NuGet mirroring and npm mirroring routed
**only** to the writer. The two-node PostgreSQL/S3 CI test exercises real
concurrent writer/reader hosts and rejects a second writer.

This is **NOT** full active-active Packory, a distributed transaction,
an officially certified multi-node architecture, or a substitute for sticky
OCI uploads, DB fencing, remote cache invalidation, coordinated snapshots,
capacity management and operational SLO validation. Do not route an arbitrary
mix of native protocols to readers. Prefer explicit reverse-proxy routing
based on tested paths, and use TLS on all exposed endpoints.

### Opt-in strict deployment preflight

Set `PACKORY_STRICT_DEPLOYMENT=true` to **reject unsafe runtime
configurations at startup**. This preflight requires PostgreSQL metadata,
S3 blob storage, a non-loopback HTTPS `PACKORY_PUBLIC_BASE_URL`,
non-anonymous package reads, an administrator token or configured OIDC issuer,
and PostgreSQL `SSL Mode=VerifyFull` with trusted hostname-validated
certificates. It rejects the loopback-HTTP S3 emulator option. Secrets
remain runtime-provided; no credentials or private contact details belong
in the public repository.

For example, an external PostgreSQL connection must set
`SSL Mode=VerifyFull` and present a hostname-matching certificate
under a trusted CA. `SSL Mode=Require` alone does **not** validate the
server identity. Do not disable certificate checking to get a deployment
passing. Keep the reverse proxy's forwarded-header trust list restrictive.

**Strict preflight is not a production certification.** It cannot verify
the external S3 provider's conditional-delete conformance, database backup
retention and restore recoverability, reverse-proxy TLS termination, object
versioning, vulnerability scanning, malware/attestation policy, multi-node
fencing or recovery drills. Those require independent deployment-specific
acceptance evidence.

`GET /health/ready` now returns HTTP 503 if there are missing hosted
blobs or unresolved publication intents older than five minutes, or if
prepared quarantine evidence remains. Normal in-flight uploads within the
grace window do not cause this alert. Admin-only
`GET /api/admin/operations/status` exposes provider/role and aggregate
health counts without tokens or private identifiers. Monitor these
counts and alert on sustained 503 before treating the deployment as
reliable. Reader nodes must retain separate writable local cache roots.

### Secure baseline

Legacy NuGet/npm upstream requests retain their configured HTTP timeout
through streamed body reads (90 seconds by default). NuGet version indexes
are streamed and metadata-bounded before parsing. Cached metadata/hosted
fallback remains available under the existing adapter policy when an
upstream body times out; a cold timeout returns 504. This is a cooperative
I/O deadline, not a CPU-time or total request latency guarantee.

On PostgreSQL writer-lease loss, mutation requests and legacy NuGet/npm
mirror GETs return 503. Static and AI dispatchers stop admitting work; AI
provider calls are cancelled and cannot reserve new requests without the
lease. These checks do not fence an arbitrary already-running transaction
or establish safe automatic failover. Stop the old process and workers
before transferring ownership; active-active remains unsupported.

1. Generate tokens using a cryptographically secure RNG; store them in a deployment secrets manager. Rotate by changing the environment and restarting the process. For an HTTP reverse proxy, enforce HTTPS for **all** authenticated reads and writes.
2. Restrict egress from Packory to the HTTPS registry hosts, configure DNS/egress filtering, and ensure only trusted operators can change upstream URLs. npm metadata supplies tarball paths; the app enforces the configured upstream host, disables automatic redirects, and does not assess package reputation. Upstream endpoints that require cross-host redirects may not work in this MVP.
3. Publish behind a reverse proxy with rate limiting, body-size limits, TLS, access logs that **redact Authorization, X-NuGet-ApiKey, URL credentials, and cookies**. Packory additionally applies an in-process publish rate limit, partitioned by known credential role so an unauthenticated flood cannot exhaust the admin/write bucket; this does not replace network-edge protections. Avoid proxy access logs that disclose sensitive query strings.
4. Disable anonymous reads for private deployments (`PACKORY_ANONYMOUS_READ=false`). **Test client authentication** before relying on private-feed NuGet and npm workflows. Static shared tokens provide no individual accountability.
5. Run as a non-root UID, pin container release digests for regulated environments, scan runtime dependencies and artifact uploads, and monitor disk usage. Back up the configured metadata database *consistently* with the corresponding filesystem or S3 artifact store.
6. Do not run archive contents or agent-skill scripts on the Packory server. An accepted package is **not** a security endorsement. Enforce dependency provenance, policy, and malware controls before using packages in production.

### Health, logs, backups

`GET /health/live` is liveness; `GET /health/ready` checks the selected metadata and artifact providers. These endpoints are unauthenticated and intentionally show no identifiers/secrets. Logs use the standard ASP.NET Core logger.

Default filesystem/SQLite data layout (PostgreSQL and S3 keep their authoritative data in their configured external services):

```text
/data/
  packory.sqlite
  packory.sqlite-wal  (when active)
  packory.sqlite-shm  (when active)
  blobs/
    nuget/...
    npm/...
    npm-metadata/...
    nuget-registration/...
    skills/...
```

Stop Packory before a simple file-level backup of `/data` in the default SQLite/filesystem deployment. Online backups require SQLite's backup API plus a consistent artifact-store snapshot; copying only the `.sqlite` file with WAL active is **not** a consistent restore strategy. To restore the default deployment, stop the service, replace the entire data set with a verified backup, then restart and query readiness and test artifact hashes. PostgreSQL/S3 requires provider-specific consistent backups and an isolated restore drill; see the backend sections below.

### Real native-client smoke tests

After building the solution in Release mode, run `bash scripts/smoke-hosted.sh` to exercise hosted publishing and downloads with real .NET/npm clients. It starts a loopback-only server on port 18080 using a newly generated short-lived token and temporary data directory, then performs a hosted NuGet package publish/restore and a scoped npm package publish/install/`npm ci`. This test requires `dotnet` 10, Node.js/npm, Bash, curl and OpenSSL. It fails if port 18080 is already occupied. `bash scripts/smoke-ecosystems.sh` additionally checks a real hosted Cargo alternate-registry publish/consume and a transitive pip install on loopback port 18081; it requires Cargo, Python/pip and the same built server. All temporary data and credentials are removed on exit. CI runs it on every PR; the separate scheduled `smoke-live.yml` checks public upstream mirroring and may need external connectivity. Passing the hosted smoke does not establish full NuGet/npm protocol conformance, especially multi-version publishing or transitive dependency resolution.

### Capacity and safe cache eviction

`PACKORY_MAX_BLOB_STORAGE_BYTES` sets an opt-in total limit for files under the
local blob root; when reached, new writes fail HTTP 507 **before publication**.
The accounting is serialized inside a single Packory process; multiple processes,
external writers and S3 object stores are outside this guarantee. Bound the HTTP
upload limit independently; temporary in-flight files still consume disk space.

Administrators may invoke `POST /api/admin/cache/mutable/evict` with their token
to drop only re-fetchable **npm packument** and **NuGet registration** cache files.
Hosted package bytes, OCI blobs/manifests and other immutable artifacts are never
deleted by this endpoint. Cache eviction requires the filesystem backend and
network connectivity for subsequent upstream metadata requests. It is a
**manual** operation, not time-based eviction or a distributed GC.

### Crash-recovery publication journal (filesystem or S3)

Every hosted NuGet, npm, Cargo, PyPI, Agent Skill, and OCI blob/manifest write
now creates a **durable database publication-intent record before its immutable
blob write**. On a successful metadata commit the corresponding journal entry
is removed. After restart, reconciliation clears records that are fully committed
or have no remaining object. For **filesystem storage**, a blob associated with
an incomplete publication intent is **never automatically relocated as an
unindexed orphan**: both its original bytes and its durable journal entry
remain in place for explicit operator investigation. Truly untracked local
immutable objects are moved to `recovery/orphaned` for inspection. Do not
serve a revision or remove its bytes merely because an incomplete intent
exists. An S3 object whose write succeeded but whose
metadata commit did **not** succeed remains recorded for operator inspection via
admin-only `GET /api/admin/publications/pending`. This endpoint exposes artifact
keys, kind, package name/version and start time, **never credentials**. The S3
test emulates an interrupted publish and verifies that the remote intent
survives until the missing metadata is committed.

**Not a distributed ACID transaction:** S3 and PostgreSQL cannot participate
in a common atomic commit. Concurrent writers across instances are unsupported.
There is no automatic remote S3 orphan deletion, catalog reconstruction, or
transaction replay. Inspect and reconcile pending records before migrating,
backing up, or enabling new writes. Do not treat an empty pending list as proof
that independently restored database/object-store snapshots are consistent.
Keep one writer and take coordinated offline snapshots of both stores.

## Safe PostgreSQL/S3 orphan reconciliation

Packory deliberately does not attempt distributed ACID transactions between
PostgreSQL and S3. **A recovery operation is opt-in and requires a single
database writer.** A PostgreSQL session-level advisory lock is held by every
running Packory process using that metadata backend. A second modern Packory
instance connecting to the same database fails startup instead of writing
concurrently. This is a safety guard, **not** active-active operation or
distributed fencing against uncooperative/older software.

If a publish is interrupted after its immutable S3 object is written but
before its metadata transaction commits, the durable publication intent remains
visible. Resolve it only through this procedure:

1. Stop all applications, workers and external writers targeting the PostgreSQL
   database and the Packory S3 prefix. Take a **coordinated backup** of both.
2. Start exactly one Packory process with
   `PACKORY_METADATA_PROVIDER=postgresql`,
   `PACKORY_BLOB_PROVIDER=s3` and
   `PACKORY_RECOVERY_MAINTENANCE=true`. This explicitly blocks normal package
   traffic, including all normal publish and cache writes. Readiness returns
   HTTP 503 on purpose; the admin reconciliation endpoints remain available.
   **Startup now requires an existing, compatible PostgreSQL schema and must
   not run automatic schema initialization/migration, orphan reconciliation,
   publication-journal cleanup or the Skill scan background dispatcher.**
   If schema validation fails, repair/migrate separately under an approved
   offline procedure rather than allowing recovery startup to mutate metadata.
   Explicitly requested administrative reconciliation remains an intentional,
   authorized mutation; this is not a universally read-only process.
   S3 client setup may create a local cache directory but must not publish
   or delete authoritative objects without the operator's explicit action.
3. Authenticate as administrator and inspect
   `GET /api/admin/publications/pending`. It now returns the observed
   `state` and any existing recovery-copy location for each intent.
4. For the intended artifact, send a JSON POST to
   `/api/admin/publications/reconcile` with **the exact** `key` and
   `expectedStartedAt` obtained from that inspection. Do not fabricate
   object keys or modify the database directly.
5. Reinspect. For a true orphan, Packory copies the bytes to a unique
   immutable `recovery/quarantine/...` object in the same S3 bucket and
   independently verifies its SHA-256. The evidence location, checksum and
   source ETag are recorded **transactionally in PostgreSQL before** a
   conditional S3 `If-Match` DELETE is attempted. The journal is closed
   only after the source disappears. Prepared evidence survives a crash;
   repeating the operation verifies the existing archive and completes it.
6. Verify the archive and restored package in staging. Stop recovery mode
   before restarting the normal instance, which must reacquire the writer lock.

Possible outcomes: `committed-journal-cleared`, `unwritten-journal-cleared`,
or `quarantined-recoverable`. A committed metadata reference with a missing
S3 object, conflicting ETag, corrupt archive or untrusted S3 implementation
**fails closed**, requiring manual restoration or investigation. No
user-controlled identity or credentials are written to recovery records.

**Limitations:** S3 compatibility with conditional DELETE must be confirmed
for the deployed provider; Moto CI alone does not establish third-party S3
certification. A crash before a PostgreSQL evidence entry can leave an
extra, untracked recovery copy; never apply lifecycle deletion to recovery
prefixes without an independent retention policy. DB and bucket must have
coordinated restore procedures. Reads and writes still use single-process
coordination and local OCI upload sessions; multi-node production operation
remains unsupported. Do **not** enable two active instances.

Reference: [S3 conditional deletes](https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-deletes.html),
[PostgreSQL advisory locks](https://www.postgresql.org/docs/current/functions-admin.html).

### Known failure modes

- Upstream request fails: if the artifact has been downloaded before, its blob can still be served; version discovery and metadata rely on a best-effort online request with local fallback. A first download while offline fails.
- Unexpected shutdown between immutable blob write and SQLite insert: pre-start recovery moves unindexed immutable package/skill files to `/data/recovery/orphaned/<startup-id>/`. This operation preserves bytes but does **not** reconstruct missing DB metadata or validate quarantined content. Inspect and verify before manually restoring an artifact; do not blindly copy it back.
- Hosted metadata exists but its blob is missing: `/health/ready` reports HTTP 503 after reconciliation on startup. Restore the matching blob from a consistent backup; do not substitute an upstream artifact. Health is evaluated on each readiness request, including deletions after startup. Restart after manually repairing blob files to rerun orphan reconciliation.
- Multiple processes sharing `/data`: **unsupported**. Per-artifact locks and startup recovery only coordinate **one process**; do not horizontally scale until external coordinated persistence is implemented.
- Disk exhaustion: requests fail; cache size is not bounded in MVP. Provide monitoring and volume headroom.
- A compromised publish token permits arbitrary artifacts with the holder's scope. Rotate immediately and manually review affected versions. A limited `GET /api/admin/audit` feed records **successful hosted publishes only** and identifies static token classes (e.g. `write-token`), not individuals. It does not prove who performed an action, capture denied attempts, rotate/revoke credentials, or replace reverse-proxy access controls. SQLite audit records have **no automatic retention policy** yet; include the DB in backups and implement retention before sustained high-volume publishing.



### Verified offline backup and restore (SQLite + filesystem only)

Use `scripts/offline_backup.py` only if **both** `PACKORY_METADATA_PROVIDER=sqlite`
and `PACKORY_BLOB_PROVIDER=filesystem`. Stop Packory and any other writers
before creating a backup. The script refuses remaining SQLite WAL/SHM files;
checkpoint cleanly first. It copies the complete data directory to a *new*
destination, includes an SHA-256/size manifest and runs SQLite `PRAGMA quick_check`.
Restores target a new, empty location and never overwrite existing data.

```bash
python3 scripts/offline_backup.py create --source /srv/packory/data --destination /srv/packory/backup-001 --offline-confirmed
python3 scripts/offline_backup.py verify --source /srv/packory/backup-001
python3 scripts/offline_backup.py restore --source /srv/packory/backup-001 --destination /srv/packory/restore-test --offline-confirmed
```

Run a real artifact restore and readiness test in an isolated instance before
accepting the backup. This is not a hot backup, migration or atomic snapshot
of PostgreSQL plus S3. An SHA-256 manifest detects accidental byte changes;
it is not a cryptographic signature and cannot detect an attacker who edits
both manifest and files. Encrypt and protect backup destinations separately.
Do not upload backups to the public repository.

## Public repository hygiene

Do not commit personal emails, contact details, tokens, `.env`, `.npmrc`, generated archives, SQLite files, local volumes, private hostnames, or system logs. Depend on GitHub Issues, Pull Requests and Security Advisories, not personal contacts. Review **commit author metadata and existing history**, not just tracked source files; deleting a source file does not scrub Git history. Rewriting already-public history additionally requires GitHub's sensitive-data removal procedure and credential/key rotation where applicable.


### Optional OIDC / Keycloak API integration

The bearer-token resource-server integration requires valid JWTs from an HTTPS OIDC authority and explicit Packory scopes. Refer to [OIDC and Keycloak](oidc-keycloak.md) for Keycloak audience-mapper configuration and security boundaries. The web dashboard does not yet implement interactive SSO; do not assume logging into Keycloak unlocks a browser session.


### PostgreSQL metadata backend

Set `PACKORY_METADATA_PROVIDER=postgresql` and provide `PACKORY_POSTGRES_CONNECTION_STRING` from the runtime secret manager. Packory creates its catalog, native-adapter metadata and audit tables with additive `CREATE TABLE IF NOT EXISTS` statements. The SQLite profile is unchanged and remains the default. **This is a fresh-deployment backend**: automatic SQLite-to-PostgreSQL migration is not implemented; export/import tooling and a verified migration sequence remain open work. The PostgreSQL backend does **not** make filesystem blobs shared or enable multi-node operation. Use TLS and `SSL Mode=VerifyFull` in the connection string for networked PostgreSQL connections where certificates are provisioned, and perform consistent relational and blob backups together. A disposable PostgreSQL 17 CI container runs provider integration tests.


### S3 artifact backing

For optional S3-compatible artifact storage configure `PACKORY_BLOB_PROVIDER=s3` and review the [S3 operations and limitations](s3.md). AWS SDK conditional immutable writes and a local read-through materialization cache are used. This remains a **single-node design**, and switching to S3 does not automatically migrate or garbage-collect old filesystem blobs. Use remote encrypted transport, minimum-privilege IAM/bucket permissions and consistent backups.

### Revocable time-limited API credentials

Administrators can provision database-backed scoped credentials through
`POST /api/admin/tokens`, supplying JSON
`{"role":"write","namespace":"nuget:internal.*","expiresInHours":24}` or
`{"role":"read","namespace":null,"expiresInHours":24}`.
Valid lifetime: 1–2160 hours. The response returns an unpredictable 32-byte
hexadecimal token **once** with `Cache-Control: no-store` and a random token ID.
Only SHA-256 of the token is persisted in the selected metadata database.
Use `DELETE /api/admin/tokens/{id}` to revoke it without restarting Packory.
A managed write token is **always namespace-scoped**; managed read tokens cannot
publish. Create/revoke operations append metadata-only admin audit entries
without storing the secret. Keep the token response out of terminal transcripts,
logs, issue reports and public repository history.

This is a basic lifecycle, not OAuth refresh-token management, personal user
accounts, PKI provenance, an immutable audit log or a multi-node credential
cache. Static environment credentials remain accepted until independently rotated.
For public deployments, put TLS and request rate limits at the edge.

### Scoped publishing (single-node configuration policy)

Set `PACKORY_WRITE_NAMESPACES` to restrict the **shared** write token to explicit
kind/name allow rules. Without rules the existing write token remains global for
backward compatibility. A rule uses `kind:name` for exact matches or a trailing
`*` for prefix matches. The supported kinds are `nuget`, `npm`, `skill`,
`cargo`, `pypi`, and `oci`; OCI uses the repository name, not a tag or digest.
The administrator token bypasses namespace limits. Configure secrets only at runtime.

For OIDC grant enforcement, set `PACKORY_OIDC_REQUIRE_NAMESPACE_GRANTS=true` and
have Keycloak issue signed `packory_namespace` **access-token** claims with these
same rule strings, after validating the exact client/role mappings. Missing or
nonmatching claims yield HTTP 403 on uploads. Role-agnostic browser access remains
unsupported. This is a static, deployment-defined policy, not user self-service,
per-token revocation or database-managed token lifecycle. Be aware that clients
may upload metadata before the package identifier is parsed; the server checks
authorization before the first persisted blob write.

## Offline catalog-reference integrity inspection (MS1.6, experimental)

The **maintenance-only**, administrator-authenticated `GET /api/admin/integrity/scan`
allows an operator to inspect up to **10 referenced artifacts per request**. The
endpoint cannot run during ordinary production service; start Packory in the
existing PostgreSQL/S3 `PACKORY_RECOVERY_MAINTENANCE=true` mode, with all
other writers stopped, a compatible initialized database schema and the
exclusive single-writer lease. Normal package traffic stays blocked.

Example for a **private** maintenance environment (never paste credentials or
responses into public issues):

```sh
curl --fail --header "Authorization: Bearer ${PACKORY_ADMIN_TOKEN}" \
  'http://127.0.0.1:8080/api/admin/integrity/scan?after=0&limit=5'
```

`after` is a **zero-based scan offset**, not a cryptographic snapshot
watermark. The JSON includes `offset`, `totalReferences`, `checked`,
`nextOffset` and `pendingPublicationIntents`, plus matching `items`.
Continue from `nextOffset` until it is null. No individual request or
collection of paged responses proves a consistent distributed snapshot:
an external stale writer, bucket lifecycle or changed metadata can invalidate
the observations. The response includes only the configured backend type
and explicit coverage limitations; no remote hosts, credentials or bucket names
are returned.

The scanner checks indexed **NuGet, npm, Cargo, PyPI, OCI blobs/manifests and
approved Skills** against independently recorded metadata where available.
`digest_match` means bytes currently materialized from the configured store
match an **existing recorded** SHA-256 or SHA-512 digest. It is not signed
provenance, malware scanning, remote S3 version certification or a claim that
the recorded reference itself was authentic.

Result states:

| State | Meaning and required action |
| --- | --- |
| `digest_match` | Bytes match recorded SHA-256/SHA-512, within this bounded observation |
| `weak_digest_match` | Only legacy SHA-1 is present; **not** sufficient for high-assurance release |
| `unverifiable` | Stored reference lacks a trustworthy recorded digest (currently includes most NuGet hosted data); do **not** infer success |
| `missing` | A committed catalog reference points to an absent object |
| `digest_mismatch` | Size/hash mismatch or an object rejected by storage-integrity validation; **stop release** |
| `invalid_reference` | Malformed catalog identity, unsupported origin or unsafe key |
| `size_limit` | Object exceeds configured verification bound; independently inspect offline |
| `storage_unavailable` | Provider, filesystem or permission failure; treat as inconclusive |

The scanner uses bounded per-request pages, a shared **two requests/minute**
zero-queue limiter and a 45-second application cancellation budget. For S3,
it performs a direct cancellation-aware **remote HEAD** when there is no
recorded digest, or a bounded streaming **GET plus SHA-256/SHA-512/SHA-1**
when a digest exists; it never trusts an older local artifact cache copy
as remote integrity evidence. The legacy npm mirror **metadata** lookup can
still materialize a local cache and may have its own storage/network
timeout; a 45-second wall-clock completion guarantee is not established.
The auditor does **not** mutate registry metadata, quarantine, delete,
publish, garbage collect or automatically repair any S3 object. No existing
local artifact copy is deleted or refreshed by direct remote inspection.
Some objects (notably NuGet) have no durable publisher-time digests;
checking only remote existence does not validate their bytes.

Treat any nonzero `pendingPublicationIntents` as an outstanding recovery
blocker and separately inspect `/api/admin/publications/pending`. Even when
all scanned rows are `digest_match`, the result does not cover **unreferenced
S3 versions, bucket retention, historical object versions, orphan recovery
copies, source authenticity or metadata consistency during a scan**.
Independent real-provider restore and native client acceptance are still
required by [MS1 and release gates](release-gates.md).

CI exercises actual SQLite/filesystem references across six ecosystem
families and PostgreSQL/S3 maintenance authorization (S3 emulated). This
improves preflight observability, **not** acceptance of M1.6 or MS1 as a
whole.


## Maven metadata upgrade and restore (schema V11)

Maven release and timestamped SNAPSHOT deploys persist mutable repository XML
inside the metadata database. V11 adds `maven_metadata(scope,path,content_base64,
sha256,size,updated_at)` without moving or changing existing artifact bytes.
A metadata change and its audit record commit atomically; immutable artifact
publication retains the existing journal and blob/ledger sequence. Native
deployment of a complete multi-file release is still not atomic.

Stop all writers and take a verified consistent backup before upgrading. Start
the upgraded writer to apply V10→V11, then start matching reader versions. Old
binaries reject the newer schema marker. To roll back, restore the pre-upgrade
database **and** its matching blob snapshot with the old binary; do not manually
lower the schema version.

The SQLite offline tooling verifies Maven metadata bytes, XML identity and
references during backup and restore. PostgreSQL/S3 schema V11 requires snapshot
format4 and its embedded `maven_references` inventory; format3 remains valid for
V10. Verification checks exact database metadata and global/private artifact
references before restoring; the restored inventories must still match.
Snapshots with missing, cross-scope or corrupt metadata references fail closed.
Configured PostgreSQL/Moto tests cover database schemas V4, V10 and V11; Moto
evidence is separate from qualification of a real object-storage provider.

Run `bash scripts/smoke-maven-deploy.sh` for pinned Maven/Gradle native acceptance
and `python3 scripts/smoke-feed-recovery.py` for the all-kind SQLite restore
smoke. See [Maven operations](maven.md) for routes, credentials and deployment
limits.

## Runtime observations (V5-15)

The .NET `Packory` Meter exposes `packory.http.requests`,
`packory.http.duration` (seconds), `packory.http.active` and
`packory.auth.denials`. Labels contain only registered route templates, a
fixed HTTP method set and response status class. Request paths, feed/package
IDs, subjects, IPs, tokens, query strings and exception text are not labels.
An OpenTelemetry host can subscribe to this Meter; an exporter/collector is
not bundled or automatically configured.

`GET /api/admin/metrics` requires an administrator and returns private/no-store
process-local totals: requests, active requests, authorization denials, server
errors and rate-limited requests. Counters reset on restart, are not an audit
log or persistent monitoring history, and are not cross-instance totals. The
observation endpoint's current request may be included in active requests.
Choose alert thresholds and validate them on the actual deployment; this
instrumentation does not establish capacity, SLO, RPO/RTO or an incident drill.

## Private Raw feed capacity and retention

The optional Raw byte/count settings apply independently to every private Raw
feed, on both metadata providers and both blob backends. Limits are checked
before creating an intent or uploading bytes, under a single-writer per-feed
lock that spans the final ledger commit. Concurrent publications cannot each
consume the same remaining capacity. Exceeding either limit returns HTTP 507;
an existing immutable revision still returns 409. Zero disables that limit.
Existing assets remain readable after an operator lowers the limit below usage.

`GET /feeds/{feedId}/raw/usage` returns committed counts/bytes and configured
limits under the exact feed read grant, with no global or anonymous fallback.
Accounting is reconstructed from the immutable metadata ledger after restart
and restore; no separate usage counter can drift. It excludes pending/orphaned
bytes, other protocols and global Raw. This logical quota is not physical
bucket accounting, an S3 capacity limit or distributed multi-writer fencing.
Monitor orphan/recovery storage and configure physical capacity separately.
Uploads with an actual body length different from Content-Length cannot commit.

Retention is indefinite immutable storage: no online delete/expiry/eviction,
version replacement or automatic quota-driven GC. Deactivating a feed does not
free capacity. Any offline operator removal requires coherent metadata/blob
backup and recovery procedures; quotas never silently remove pinned data.
Content still follows the bounded Raw name/version/media-type and byte-integrity
profile; it is never executed or interpreted as a native package format.

### Concurrent PostgreSQL bootstrap

Cooperating metadata, Skill-release and MCP-catalog initializers acquire a
shared per-database/schema PostgreSQL advisory lock before version checks,
bootstrap DDL or migrations. `CREATE TABLE IF NOT EXISTS` alone cannot safely
serialize concurrent type/table creation. Acquisition is bounded to 30 seconds;
successful or failed initialization releases the lock before returning its
connection. A terminated session releases it server-side. This coordinates
initialization only, with a distinct lock key space; it does not permit multiple
active application writers or supply independent failover fencing.
See [PostgreSQL advisory locks](https://www.postgresql.org/docs/17/explicit-locking.html#ADVISORY-LOCKS).

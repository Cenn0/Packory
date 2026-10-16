# Packory V2 operations and security

## Supported deployment profile

Standalone: one ASP.NET Core process, one SQLite writer, local filesystem content-addressed storage. Run the container as non-root and mount a persistent volume. Configure TLS termination at a trusted proxy for any non-loopback access. SQLite on a network filesystem, multiple writing replicas, HA failover, PostgreSQL and S3 are **not supported** in this profile.

## Secrets and authorization

- `PACKORY_ADMIN_TOKEN`: root/bootstrap credential; generate a random value of at least 32 characters. No checked-in or baked-in default.
- `PACKORY_WRITE_TOKEN`, `PACKORY_READ_TOKEN`: optional legacy global-scoped secrets; prefer `POST /api/v1/tokens` for clients restricted to one V2 feed.
- `PACKORY_PUBLIC_BASE_URL`: exact externally reachable base origin, with HTTPS for deployed endpoints.
- `PACKORY_DATA_DIRECTORY`: mount with permissions permitting only the Packory process to read token hashes, SQLite and CAS metadata.
- `PACKORY_ANONYMOUS_READ`: **legacy global endpoints only**; V2 feeds are private until explicitly made public via management API.

Do not put credentials in URL query strings, browser local storage, README examples, CI logs, or package metadata. Keep an offline recovery copy of the bootstrap admin token. If it is exposed, rotate it and review audit logs.

## Storage and lifecycle

Large artifacts are streamed into `staging/` using bounded buffers and incremental SHA-256. Staged files are moved into immutable digest-addressed paths before publishing the reference in SQLite. The two systems do not have a global ACID transaction: a crash can leave unreferenced CAS files, not half-visible published asset references. The `blob.reconcile` job removes abandoned staging older than two hours and unreferenced CAS files older than 24 hours. It does not delete hosted artifacts still referenced by active records.

Artifact hash is an integrity marker **not** a publisher identity guarantee. npm integrity values and NuGet origin SHA-512 sidecars are verified **when present**; malformed or mismatched content is not published in the feed. Official NuGet V3 flat-container origins do not have to supply a `.nupkg.sha512` sidecar. In that case NuGet uses HTTPS transport, validates the package ID/version and stores a local SHA-256, marked `mirror-tls` instead of `mirror-sha512`; this does not independently authenticate publisher provenance. A raw file's `X-Packory-SHA256` optionally provides an expected digest supplied by the publisher.

## Network egress

New V2 proxy feeds accept only known origins on HTTPS port 443; custom internal registry hosts, arbitrary redirect following, untrusted tarball hosts and dynamic Git imports are not enabled. Current allowlist: `api.nuget.org`, `registry.npmjs.org`, selected per protocol. Origins with custom network requirements need a separate security-reviewed opt-in and DNS/IP validation, not a relaxed default. This is stricter than the legacy feed configuration, which should not be externally exposed without a dedicated review.

## Logs and jobs

`GET /api/v1/jobs` lists persisted queued/running/completed/failed jobs. `POST /api/v1/maintenance/reconcile` queues the supported reconciliation operation. Other job types from the architecture design are not yet implemented. Audit is append-only in the application API, excludes token secrets, and is visible to administrators at `GET /api/v1/audit`.

## Recovery and incident response

1. Inspect `/health/live` and `/health/ready` and the last CI/image version deployed.
2. Stop writes during a consistent data backup. Snapshot both DB and CAS.
3. For checksum/identity errors, check upstream consistency and metadata, not merely retrying until a corrupt blob appears in the catalog.
4. If a scoped token is compromised, immediately revoke by ID, inspect feed-specific audit events, then issue a new token with shorter expiry.
5. To quarantine a raw artifact, use the state-management endpoint; this removes download visibility without deleting a referenced CAS blob.
6. For a failed restore, restore **both** files and SQLite from the same snapshot. Do not run older and newer Packory versions against one live volume.

## Unresolved production gates

There is no built-in rate-limiter, distributed leases, malware scanner, signature verification, quota policy or complete CAS checksum scrub. OIDC/Keycloak sessions are implemented optionally but have not undergone real Keycloak integration or security testing. Complete threat-model testing and native-client compatibility remain release requirements; see `docs/v2-status.md`. A successful CI run is not a security certification.

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
| `PACKORY_POSTGRES_CONNECTION_STRING` | *(unset)* | PostgreSQL ADO.NET connection string (runtime secret; required for postgresql provider) |
| `PACKORY_DATA_DIRECTORY` | `./data` or `/data` in Docker | SQLite + blob root |
| `PACKORY_PUBLIC_BASE_URL` | `http://localhost:8080` | Absolute origin (no path) advertised to clients |
| `PACKORY_NUGET_UPSTREAM_URL` | `https://api.nuget.org/v3-flatcontainer/` | Trusted NuGet flat container |
| `PACKORY_NUGET_REGISTRATION_URL` | `https://api.nuget.org/v3/registration5-semver2/` | Trusted NuGet registration source |
| `PACKORY_NPM_UPSTREAM_URL` | `https://registry.npmjs.org/` | Trusted npm upstream |
| `PACKORY_ANONYMOUS_READ` | `true` | Permit unauthenticated registry read |
| `PACKORY_RESERVED_NUGET_PREFIXES` | *(empty)* | Comma-separated private NuGet ID prefixes (e.g. `internal.`); package names under these prefixes never fall back to nuget.org |
| `PACKORY_RESERVED_NPM_SCOPES` | *(empty)* | Comma-separated npm scopes (e.g. `@internal`); packages under these scopes never fall back to registry.npmjs.org |
| `PACKORY_DENIED_NUGET_PREFIXES` | *(empty)* | Comma-separated ID prefixes blocked for both read and publish, case-insensitively |
| `PACKORY_DENIED_NPM_SCOPES` | *(empty)* | Comma-separated npm scopes blocked for both read and publish |
| `PACKORY_OIDC_AUTHORITY` | *(unset)* | HTTPS authority / issuer URL for optional JWT validation |
| `PACKORY_OIDC_AUDIENCE` | *(unset)* | Required JWT resource-server audience; must accompany authority |
| `PACKORY_PUBLISH_REQUESTS_PER_MINUTE` | `60` | Maximum attempts per minute in each credential-role partition (admin, write, read, unauthenticated). Range 1–10000; 429 when exceeded |
| `PACKORY_ADMIN_TOKEN` | *(unset)* | Required to enable admin/write access |
| `PACKORY_READ_TOKEN` | *(unset)* | Optional read-only credential |
| `PACKORY_WRITE_TOKEN` | *(unset)* | Optional publish credential |

All defined tokens must be unique, at least 32 characters. **Unset tokens mean no permission of that type is available**; the service will not silently enable writes. Runtime settings can also be set via the `Packory` JSON section, but do not persist real secrets there.

### Secure baseline

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

### Known failure modes

- Upstream request fails: if the artifact has been downloaded before, its blob can still be served; version discovery and metadata rely on a best-effort online request with local fallback. A first download while offline fails.
- Unexpected shutdown between immutable blob write and SQLite insert: pre-start recovery moves unindexed immutable package/skill files to `/data/recovery/orphaned/<startup-id>/`. This operation preserves bytes but does **not** reconstruct missing DB metadata or validate quarantined content. Inspect and verify before manually restoring an artifact; do not blindly copy it back.
- Hosted metadata exists but its blob is missing: `/health/ready` reports HTTP 503 after reconciliation on startup. Restore the matching blob from a consistent backup; do not substitute an upstream artifact. Health is evaluated on each readiness request, including deletions after startup. Restart after manually repairing blob files to rerun orphan reconciliation.
- Multiple processes sharing `/data`: **unsupported**. Per-artifact locks and startup recovery only coordinate **one process**; do not horizontally scale until external coordinated persistence is implemented.
- Disk exhaustion: requests fail; cache size is not bounded in MVP. Provide monitoring and volume headroom.
- A compromised publish token permits arbitrary artifacts with the holder's scope. Rotate immediately and manually review affected versions. A limited `GET /api/admin/audit` feed records **successful hosted publishes only** and identifies static token classes (e.g. `write-token`), not individuals. It does not prove who performed an action, capture denied attempts, rotate/revoke credentials, or replace reverse-proxy access controls. SQLite audit records have **no automatic retention policy** yet; include the DB in backups and implement retention before sustained high-volume publishing.

## Public repository hygiene

Do not commit personal emails, contact details, tokens, `.env`, `.npmrc`, generated archives, SQLite files, local volumes, private hostnames, or system logs. Depend on GitHub Issues, Pull Requests and Security Advisories, not personal contacts. Review **commit author metadata and existing history**, not just tracked source files; deleting a source file does not scrub Git history. Rewriting already-public history additionally requires GitHub's sensitive-data removal procedure and credential/key rotation where applicable.


### Optional OIDC / Keycloak API integration

The bearer-token resource-server integration requires valid JWTs from an HTTPS OIDC authority and explicit Packory scopes. Refer to [OIDC and Keycloak](oidc-keycloak.md) for Keycloak audience-mapper configuration and security boundaries. The web dashboard does not yet implement interactive SSO; do not assume logging into Keycloak unlocks a browser session.


### PostgreSQL metadata backend

Set `PACKORY_METADATA_PROVIDER=postgresql` and provide `PACKORY_POSTGRES_CONNECTION_STRING` from the runtime secret manager. Packory creates its catalog, native-adapter metadata and audit tables with additive `CREATE TABLE IF NOT EXISTS` statements. The SQLite profile is unchanged and remains the default. **This is a fresh-deployment backend**: automatic SQLite-to-PostgreSQL migration is not implemented; export/import tooling and a verified migration sequence remain open work. The PostgreSQL backend does **not** make filesystem blobs shared or enable multi-node operation. Use TLS and `SSL Mode=VerifyFull` in the connection string for networked PostgreSQL connections where certificates are provisioned, and perform consistent relational and blob backups together. A disposable PostgreSQL 17 CI container runs provider integration tests.


### S3 artifact backing

For optional S3-compatible artifact storage configure `PACKORY_BLOB_PROVIDER=s3` and review the [S3 operations and limitations](s3.md). AWS SDK conditional immutable writes and a local read-through materialization cache are used. This remains a **single-node design**, and switching to S3 does not automatically migrate or garbage-collect old filesystem blobs. Use remote encrypted transport, minimum-privilege IAM/bucket permissions and consistent backups.

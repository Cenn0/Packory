# Operations and security guidance

## Deployment topology

Deploy one Packory instance behind a TLS reverse proxy on a controlled host. The default Compose file publishes `127.0.0.1:8080:8080` and stores data in a named Docker volume. Set `PACKORY_PUBLIC_BASE_URL` to the externally reachable HTTPS origin, because it is embedded in NuGet service-index and npm download URLs. Do **not** enable untrusted forwarded header handling and do **not** put sensitive token material in checked-in examples.

### Environment

| Variable | Default | Meaning |
| --- | --- | --- |
| `PACKORY_DATA_DIRECTORY` | `./data` or `/data` in Docker | SQLite + blob root |
| `PACKORY_PUBLIC_BASE_URL` | `http://localhost:8080` | Absolute origin (no path) advertised to clients |
| `PACKORY_NUGET_UPSTREAM_URL` | `https://api.nuget.org/v3-flatcontainer/` | Trusted NuGet flat container |
| `PACKORY_NUGET_REGISTRATION_URL` | `https://api.nuget.org/v3/registration5-semver2/` | Trusted NuGet registration source |
| `PACKORY_NPM_UPSTREAM_URL` | `https://registry.npmjs.org/` | Trusted npm upstream |
| `PACKORY_ANONYMOUS_READ` | `true` | Permit unauthenticated registry read |
| `PACKORY_ADMIN_TOKEN` | *(unset)* | Required to enable admin/write access |
| `PACKORY_READ_TOKEN` | *(unset)* | Optional read-only credential |
| `PACKORY_WRITE_TOKEN` | *(unset)* | Optional publish credential |

All defined tokens must be unique, at least 32 characters. **Unset tokens mean no permission of that type is available**; the service will not silently enable writes. Runtime settings can also be set via the `Packory` JSON section, but do not persist real secrets there.

### Secure baseline

1. Generate tokens using a cryptographically secure RNG; store them in a deployment secrets manager. Rotate by changing the environment and restarting the process. For an HTTP reverse proxy, enforce HTTPS for **all** authenticated reads and writes.
2. Restrict egress from Packory to the HTTPS registry hosts, configure DNS/egress filtering, and ensure only trusted operators can change upstream URLs. npm metadata supplies tarball paths; the app enforces the configured upstream host, disables automatic redirects, and does not assess package reputation. Upstream endpoints that require cross-host redirects may not work in this MVP.
3. Publish behind a reverse proxy with rate limiting, body-size limits, TLS, access logs that **redact Authorization, X-NuGet-ApiKey, URL credentials, and cookies**. Avoid proxy access logs that disclose sensitive query strings.
4. Disable anonymous reads for private deployments (`PACKORY_ANONYMOUS_READ=false`). **Test client authentication** before relying on private-feed NuGet and npm workflows. Static shared tokens provide no individual accountability.
5. Run as a non-root UID, pin container release digests for regulated environments, scan runtime dependencies and artifact uploads, and monitor disk usage. Back up SQLite *consistently* and the entire blob directory together.
6. Do not run archive contents or agent-skill scripts on the Packory server. An accepted package is **not** a security endorsement. Enforce dependency provenance, policy, and malware controls before using packages in production.

### Health, logs, backups

`GET /health/live` is liveness; `GET /health/ready` opens SQLite. These endpoints are unauthenticated and intentionally show no identifiers/secrets. Logs use the standard ASP.NET Core logger.

Data layout:

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

Stop Packory before a simple file-level backup of `/data`. Online backups require SQLite's backup API plus a consistent artifact-store snapshot; copying only the `.sqlite` file with WAL active is **not** a consistent restore strategy. To restore, stop the service, replace the entire data set with a verified backup, then restart and query readiness and test artifact hashes.

### Known failure modes

- Upstream request fails: if the artifact has been downloaded before, its blob can still be served; version discovery and metadata rely on a best-effort online request with local fallback. A first download while offline fails.
- Unexpected shutdown between immutable blob write and SQLite insert: a blob may be orphaned. Inspect before removal; there is no automated recovery/GC in MVP.
- Multiple processes sharing `/data`: **unsupported**. Do not horizontally scale until external coordinated persistence is implemented.
- Disk exhaustion: requests fail; cache size is not bounded in MVP. Provide monitoring and volume headroom.
- A compromised publish token permits arbitrary artifacts with the holder's scope. Rotate immediately and manually review affected versions. There is no built-in revoke/UI or audit stream yet.

## Public repository hygiene

Do not commit personal emails, contact details, tokens, `.env`, `.npmrc`, generated archives, SQLite files, local volumes, private hostnames, or system logs. Depend on GitHub Issues, Pull Requests and Security Advisories, not personal contacts. Review **commit author metadata and existing history**, not just tracked source files; deleting a source file does not scrub Git history. Rewriting already-public history additionally requires GitHub's sensitive-data removal procedure and credential/key rotation where applicable.

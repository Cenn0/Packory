# API and native client compatibility (experimental)

**Go V4 E3 is not an API:** the Go coordinate parser and ZIP preflight are internal code only. Do **not** configure GOPROXY to Packory or treat a generic Raw URL as a Go module registry. See [Go groundwork](go-proxy.md).

All endpoints are relative to `PACKORY_PUBLIC_BASE_URL`. Example values assume `http://localhost:8080` for loopback-only development. Registry URLs embed the configured public origin and must be routable by the client.

## Authentication

Three optional environment variables define independent static permissions:

| Variable | Allowed operations |
| --- | --- |
| `PACKORY_ADMIN_TOKEN` | Read, write, future admin operations |
| `PACKORY_WRITE_TOKEN` | Read and publish |
| `PACKORY_READ_TOKEN` | Read |

Token values must be **at least 32 characters**, distinct, and delivered via `Authorization: Bearer <token>` or (for NuGet publish) `X-NuGet-ApiKey: <token>`. NuGet native restore can also authenticate using HTTP Basic where the password is the token. Tokens are compared in constant time after SHA-256 hashing in memory. No built-in login or browser session is implemented.

`PACKORY_ANONYMOUS_READ=true` by default. Setting it to `false` blocks anonymous registry and catalog requests. Health endpoints and the dashboard HTML remain public. Browser clients must supply a Bearer token; some native clients need additional configuration. Authentication must be protected with HTTPS except for loopback testing.

## Private namespace reservations

Operators may set `PACKORY_RESERVED_NUGET_PREFIXES` (comma-separated ID prefixes, matched case-insensitively) and `PACKORY_RESERVED_NPM_SCOPES` (comma-separated lowercase scopes, each beginning with `@`). For matching package names, Packory returns only locally hosted/cached metadata and artifacts and **never calls a configured public upstream**. A missing privately reserved package returns 404. Prefixes apply to the complete NuGet ID string, so use a deliberate delimiter (for example `internal.`) to avoid unintentionally reserving additional IDs. Both settings are optional but recommended wherever internal package names could collide with public ones.

## Explicit namespace blocking

Optional `PACKORY_DENIED_NUGET_PREFIXES` and `PACKORY_DENIED_NPM_SCOPES` configuration deny **both reads and writes** for matching names with 403. Distinguish these blocklists from `PACKORY_RESERVED_*`, which prohibit public-upstream fallback but permit local hosted packages. A blocklist alone does not implement malware scanning, licensing approval or per-user policy.

## Administrative audit and write throttling

`GET /api/admin/audit?limit=50&before=<exclusive-id>` returns up to 100 successful hosted publish events, ordered newest first. **Only `PACKORY_ADMIN_TOKEN` can read this endpoint**; a write token receives 403, no token receives 401. Event fields: `id`, `action`, `kind`, `name`, `version`, `actor`, `createdAt`. `actor` is a static credential class (`admin-token` or `write-token`), not a person. Tokens, passwords, IP addresses and email addresses are not written into audit rows. Audit creation is part of the same SQLite transaction as hosted metadata publication, but blob storage remains outside SQLite transactions.

Writes to NuGet push, npm publish and Skills import are subject to `PACKORY_PUBLISH_REQUESTS_PER_MINUTE` (default `60`) separately per credential class. Requests exceeding the limit receive HTTP 429. Since the limiter is in-process, its counters reset on restart and it does not coordinate multiple instances; it is not a replacement for upstream rate limiting at the reverse proxy.

## Native-client compatibility evidence

The PR CI executes `scripts/smoke-hosted.sh` with .NET 10 and npm under Node.js 22: `dotnet pack` / `dotnet nuget push` for a privately reserved package, then a NuGet source-cleared project restore/build; plus `npm publish` for `@packory-smoke/demo`, then unauthenticated `npm install` and fresh-cache `npm ci`. The source restrictions prevent public registry fallback in these tests. These are tested behaviors, not certification of the full native protocol. `scripts/smoke-nuget.sh` and `scripts/smoke-npm.sh` remain external mirror acceptance tests in a separate scheduled GitHub workflow.

## NuGet v3

- Service index: `GET /nuget/v3/index.json`
- Flat container versions: `GET /nuget/v3/flatcontainer/{lower-id}/index.json`
- Content: `GET /nuget/v3/flatcontainer/{lower-id}/{lower-version}/{lower-id}.{lower-version}.nupkg`
- Hash: same location plus `.sha512`
- Manifest: `GET /nuget/v3/flatcontainer/{lower-id}/{lower-version}/{lower-id}.nuspec`
- Registration: `GET /nuget/v3/registration/{lower-id}/index.json`, plus discovered pages and leaves
- Search (indexed packages): `GET /nuget/v3/search?q={text}&skip=0&take=20`
- Hosted publish: `PUT /nuget/v3/push` with multipart/form-data and `X-NuGet-ApiKey`

Verify end-to-end using an **isolated** NuGet.Config to ensure the SDK does not silently fall back to nuget.org:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packory" value="http://localhost:8080/nuget/v3/index.json" allowInsecureConnections="true" />
  </packageSources>
</configuration>
```

Run `dotnet restore --configfile NuGet.Config` after adding a dependency. Network access from Packory to nuget.org is still required for uncached packages. For HTTPS remove the insecure opt-in.

**Important:** the experimental does not expose a fully featured NuGet server API: symbols, vulnerability resources, unlisting, repository signatures, and advanced metadata/registration behaviors are not included. NuGet client compatibility is tested incrementally and should be treated as **basic** until the live smoke workflow succeeds.

## npm

- Packument: `GET /npm/{package}` (including scoped package names)
- Version metadata: `GET /npm/{package}/{version}`
- Rewritten tarball: `GET /npm/{package}/-/{version}.tgz`
- Search of indexed versions: `GET /npm/-/v1/search?text={term}`
- Hosted publish: `PUT /npm/{package}`, one-version packument with one base64 tarball attachment

`GET` packuments merge upstream data with local hosted versions (local versions take precedence) and replace tarball URLs with Packory URLs. Tarballs are downloaded and cached by version upon first request. Upstream metadata has a best-effort offline file cache. npm publishing requires a pre-configured token; **npm login is not an endpoint** in this experimental.

## Skills and unified catalog

`GET /api/packages?q=example&kind=nuget&limit=50` returns:

```json
{
  "items": [
    { "kind": "nuget", "name": "example.package", "version": "1.2.3", "description": "Example", "origin": "mirror", "updatedAt": "2026-10-16T00:00:00Z" }
  ]
}
```

`GET /api/skills` returns `{ "items": [...] }`. `POST /api/skills` accepts multipart fields `version` and `file` (ZIP). `GET /api/skills/{name}/{version}` returns metadata. Download the immutable ZIP from `/download`, or retrieve just the `SKILL.md` content from `/skill.md`.

Skill imports validate required `name`/`description` metadata and bounded ZIP contents. MCP returns JSON-compatible YAML frontmatter and exact-byte resources. These implemented checks do not establish complete [Agent Skills specification](https://agentskills.io/specification) conformance or runtime correctness; the [static quality profile](skill-quality.md) describes the additional documentation checks.

## HTTP error codes

Common codes: `400` malformed names or uploads, `401` no/invalid token, `403` insufficient token permission, `404` absent package/skill, `409` version already exists, `413` artifact too large, `502` upstream/error data, `504` upstream timeout. Errors do not intentionally expose environment secrets, connection strings, or upstream credentials. Inconsistent unindexed/missing local immutable blobs fail closed (502) rather than silently serving or substituting bytes from another origin. Readiness is 503 if a hosted blob is missing at startup. npm proxy tarballs require an upstream `sha512-` integrity or SHA-1 `shasum` claim that matches the downloaded bytes.

## OCI upload and integrity contract

See [OCI endpoints](oci.md) and [chunk recovery / upgrade](oci-upload-recovery.md).
`POST /v2/<repository>/blobs/uploads/` starts a bounded session; use the returned
Location for PATCH, GET, DELETE and final PUT with `?digest=sha256:<hex>`.
PATCH/final PUT may provide inclusive `Content-Range: start-end`. A supplied
range must begin at the committed offset and match Content-Length and the body.
Invalid or replayed ranges return **416** with the current Range and Location;
GET on the session returns **204** with the acknowledged offset. Complete chunks
return **202**, successful digest-verified publication **201**, and cancellation
**204**. Unknown or cross-repository sessions return **404**. Session exhaustion
returns **429** with Retry-After. Existing artifact size limits still return 413.

A process restart can resume current-format sessions on the same data directory;
old session formats without committed offsets are preserved but not trusted.
Filesystem OCI digest corruption is refused with **503** on read/mount, not
returned as artifact bytes. This does not add independent private-feed read
permissions, a Bearer grant service, multi-node sessions or provider certification.
Referrers honor `artifactType` before setting OCI-Filters-Applied. Accepted subject
associations return OCI-Subject; `tags/list?n=0` returns an empty page.


## Offline artifact integrity inspection

Administrators may inspect catalog references **only** while
`PACKORY_RECOVERY_MAINTENANCE=true` holds the single-writer lease.
`GET /api/admin/integrity/scan?after=0&limit=5` returns a bounded page
of checksum comparisons and explicit missing/unverifiable states. Valid
`after` is a nonnegative offset and `limit` is 1–10. The endpoint rejects
anonymous, read-only and publisher credentials; ordinary running instances
return 409. The response is `Cache-Control: no-store` and rate limited.
For S3 it hashes bytes streamed directly from the configured remote object,
bypassing the potentially stale artifact cache. This is a best-effort
point-in-time observation, never an automatic production certification. See
[operations](operations.md#offline-catalog-reference-integrity-inspection-ms16-experimental)
for interpretation, caveats and a redacted invocation example.


## Hosted NuGet feed API (V4 experimental)

`POST /api/v1/admin/feeds/{id}/activate` enables a reviewed **private
NuGet hosted** draft only. `POST .../{id}/deactivate` disables client
traffic while retaining immutable package bytes and metadata. Both are
admin-only, audited and rate-limited. Other feed kinds remain drafts.

Under `/feeds/{id}/nuget/v3/` the supported native operations are:
`GET index.json`, `GET flatcontainer/{package}/index.json`,
`GET flatcontainer/{package}/{version}/{file}` for exact
`.nupkg`, `.nupkg.sha512` and `.nuspec`, plus
`PUT/POST push` with the existing multipart NuGet package contract.
There is **no** mirror fetch, public anonymous read, cross-feed
fallback or generic catalog exposure.

Use revocable tokens via `POST /api/admin/tokens` with
`{"role":"read","namespace":"feed:alpha-feed:read","expiresInHours":1}`
or the analogous `write/publish` grant. Never check issued tokens into
source files. Generic `packory.read`, legacy static read/write
credentials and tokens for other feeds do not grant private feed access.
See [feed operations](feeds.md) for precise limitations.


## MCP artifact and Skill discovery

The authenticated Streamable HTTP endpoint is `/mcp`. It requires an explicit global Bearer read credential even when REST reads are anonymous. Managed credentials restricted to a feed or publishing namespace receive 403. Native C# SDK tests cover MCP revisions 2025-06-18, 2025-11-25 and 2026-07-28.

| Method/tool | Paging and identity |
| --- | --- |
| `search_artifacts(query, limit=30, kind?, cursor?)` | Global hosted/cached artifacts, including Maven and Raw; `items` and `nextCursor`; limit 1–100 |
| `artifact_versions(kind, name, limit=100, cursor?)` | Exact native name (Maven uses `groupId:artifactId`); lexical version order and `nextCursor`; limit 1–100 |
| `skills/list` | Approved Skill discovery; opaque pages of at most 25 entries |
| `skills/get` | Exact approved manifest, file digests and canonical resource URIs |
| `resources/directory/read` | Direct children of one approved directory; at most 100 entries, archive/directory-bound `nextCursor` |
| `resources/read` | Exact approved file bytes, text or binary as appropriate |

Continue each page with the returned cursor and the original query/kind/name/directory. Cursors grant no permissions and are not historical snapshots. Invalid or mismatched parameters return MCP `InvalidParams` (`-32602`). Skill eligibility is applied before artifact page limits and rechecked for content delivery; private-feed records are excluded from global discovery. See [MCP integration](mcp-security-integration.md) for URI, digest, caching and OAuth metadata behavior.

## Read-only Skill quality

`GET /api/v1/skills/{name}/{version}/quality` and MCP `assess_skill_quality(name, version)` return the same deterministic `packory.skill-quality/1` report for the exact published bytes. Reports include archive/manifest SHA-256, coverage, independent dimensions, findings and concrete suggestions. `completed` means the defined checks ran and may include `issues_found`; incomplete coverage stays explicit. No approval, execution or aggregate safety score is implied.

REST follows explicit anonymous-read configuration and otherwise requires global read access. Authentication runs before a separate 60-request/minute per-role in-process limiter. Responses are `no-store`; unavailable, revoked or tampered revisions return REST 404 and MCP `status: unavailable`. The UI action is **Assess quality**. See [Skill quality](skill-quality.md) for resource bounds and exclusions.

## MCP Registry metadata

Packory provides a versioned, **remote-only** MCP server.json catalog separate from its own Streamable HTTP MCP service. For operator staging and digest-bound approval/revocation, use `POST /api/v1/admin/mcp/servers/stage` (raw 16 KiB server.json) and `POST /api/v1/admin/mcp/servers/decide` (name, version, sha256, action and reason). Both require an administrator; publish/revoke decisions are immutable-key and auditable.

The supported Registry v0.1 read contract provides `GET /v0.1/servers`, `GET /v0.1/servers/{serverName}/versions` and `GET /v0.1/servers/{serverName}/versions/{version}` (exact or `latest`). Lists use official `servers`/`metadata` envelopes and opaque `nextCursor`; per-entry Packory review information lives in `_meta["io.github.cenn0.packory/registry"]`. Search is literal and name-only, limits are 1–100/default 30. Encode a complete server name as one path segment. Unsupported synchronization/deleted-record filters are explicitly rejected.

Packory `GET /api/v1/mcp/servers?q=...&limit=25&cursor=...` adds name/title search for the dashboard; explicit offsets remain for legacy clients. `GET /api/v1/mcp/server?name=...&version=...` returns an exact descriptor. MCP tools `search_mcp_servers(query, limit=20, cursor?, version?)` and `get_mcp_server(name, version)` share the published catalog. Every route exposes only approved-for-discovery records; publisher ownership and connection safety remain unverified. Complete supported fields, lifecycle, envelope/digest semantics and remaining Registry work are in [MCP registry catalog](mcp-registry-catalog.md).

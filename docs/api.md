# API and native client compatibility (experimental)

**Hosted feeds:** NuGet, npm, Cargo, PyPI, OCI, Maven, Go and Raw have independent private routes and exact feed grants. Their native route table is [below](#private-hosted-feed-api-e0), with limits in [feed operations](feeds.md). Passing local native/provider/restore results and the completed published-commit GitHub checks are recorded in [E0 acceptance](e0-acceptance.md).

**Go hosted:** global publication uses multipart POST `/api/go/modules`; private publication uses `/feeds/{id}/api/go/modules` (module, version, mod, zip, optional time). Responses include the archive SHA-256 and locally computed Go `h1:` integrity; this is not external checksum-database verification. Native clients consume `/go/` or the authorized `/feeds/{id}/go/` root. Upstream mirroring remains separate E3 work. See [global Go](go-proxy.md) and [private Go](feeds.md#go-modules).

All endpoints are relative to `PACKORY_PUBLIC_BASE_URL`. Example values assume `http://localhost:8080` for loopback-only development. Registry URLs embed the configured public origin and must be routable by the client.

## Authentication

Three optional environment variables define independent static permissions:

| Variable | Allowed operations |
| --- | --- |
| `PACKORY_ADMIN_TOKEN` | Global/private read, write and administrator operations |
| `PACKORY_WRITE_TOKEN` | Global read and publish, subject to configured namespace policy |
| `PACKORY_READ_TOKEN` | Global read |

Token values must be **at least 32 characters**, distinct, and delivered via `Authorization: Bearer <token>` or (for NuGet publish) `X-NuGet-ApiKey: <token>`. NuGet native restore can also authenticate using HTTP Basic where the password is the token. Tokens are compared in constant time after SHA-256 hashing in memory. No built-in login or browser session is implemented.

`PACKORY_ANONYMOUS_READ=true` by default. Setting it to `false` blocks anonymous registry and catalog requests. Health endpoints and the dashboard HTML remain public. Browser clients must supply a Bearer token; some native clients need additional configuration. Authentication must be protected with HTTPS except for loopback testing.

Private hosted routes always authenticate, regardless of anonymous global read configuration. They require an exact managed `feed:{id}:read`/`feed:{id}:publish` grant or a corresponding validated OIDC feed claim; generic static read/write tokens do not grant access. Publish permits same-feed read. When anonymous global reads are disabled, scoped managed credentials cannot read global registries or catalog data. The authenticated OCI `/v2/` handshake does not confer repository permission. See [feed authorization](feeds.md#exact-feed-authorization).

## Private namespace reservations

Operators may set `PACKORY_RESERVED_NUGET_PREFIXES` (comma-separated ID prefixes, matched case-insensitively) and `PACKORY_RESERVED_NPM_SCOPES` (comma-separated lowercase scopes, each beginning with `@`). For matching package names, Packory returns only locally hosted/cached metadata and artifacts and **never calls a configured public upstream**. A missing privately reserved package returns 404. Prefixes apply to the complete NuGet ID string, so use a deliberate delimiter (for example `internal.`) to avoid unintentionally reserving additional IDs. Both settings are optional but recommended wherever internal package names could collide with public ones.

## Explicit namespace blocking

Optional `PACKORY_DENIED_NUGET_PREFIXES` and `PACKORY_DENIED_NPM_SCOPES` configuration deny **both reads and writes** for matching names with 403. Distinguish these blocklists from `PACKORY_RESERVED_*`, which prohibit public-upstream fallback but permit local hosted packages. A blocklist alone does not implement malware scanning, licensing approval or per-user policy.

## Administrative audit and write throttling

`GET /api/admin/audit?limit=50&before=<exclusive-id>` returns up to 100 events, newest first, including successful hosted publications and selected management actions such as feed lifecycle changes. Administrator access is required; an ordinary write token receives 403 and missing credentials receive 401. Fields are `id`, `action`, `kind`, `name`, `version`, `actor` and `createdAt`. Actors identify credential classes or managed-token IDs rather than a person. Tokens, passwords, IP addresses and email addresses are not written into these rows. Publication metadata and its audit event share a metadata transaction; blob storage remains outside that transaction.

Writes to NuGet push, npm publish and Skills import are subject to `PACKORY_PUBLISH_REQUESTS_PER_MINUTE` (default `60`) separately per credential class. Requests exceeding the limit receive HTTP 429. Since the limiter is in-process, its counters reset on restart and it does not coordinate multiple instances; it is not a replacement for upstream rate limiting at the reverse proxy.

## Native-client compatibility evidence

The PR CI executes `scripts/smoke-hosted.sh` with .NET 10 and npm under Node.js 22: `dotnet pack` / `dotnet nuget push` for a privately reserved package, then a NuGet source-cleared project restore/build; plus `npm publish` for `@packory-smoke/demo`, then unauthenticated `npm install` and fresh-cache `npm ci`. The source restrictions prevent public registry fallback in these tests. These are tested behaviors, not certification of the full native protocol. `scripts/smoke-nuget.sh` and `scripts/smoke-npm.sh` remain external mirror acceptance tests in a separate scheduled GitHub workflow.

Private feed tests additionally exercise independently authorized same-name revisions in two feeds. .NET SDK 10.0.402, npm 11.9.0, Go 1.24.13, Cargo 1.99.1, Python 3.12/pip 26.2.1/Twine 6.2.0, Helm 3.22.0, Maven 3.9.11/OpenJDK 17.0.20 and curl 8.5.0 have passed the documented local SQLite/filesystem scenarios. The npm/Cargo/PyPI/Helm profiles also passed against PostgreSQL 16 and Moto S3. These versioned observations do not establish untested client/provider combinations. Full commands, per-scenario coverage, local suite totals and the GitHub gate are in [E0 acceptance](e0-acceptance.md).

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

Common codes: `400` malformed names or uploads, `401` no/invalid token, `403` insufficient permission, `404` absent package/skill or authorized inactive feed, `409` immutable revision conflict, `413` artifact too large, `502` upstream/error data, `503` unavailable committed content, and `504` upstream timeout. For private routes, authorization precedes feed/package existence checks: a foreign-feed credential receives 403 rather than learning whether the target exists. Corrupted/missing committed private bundles fail closed with 503; they are never replaced from another origin. Existing global adapters retain their documented error profiles. Readiness is 503 if a hosted blob is missing at startup. npm proxy tarballs require upstream SHA-512 integrity or SHA-1 shasum claims that match the bytes.

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
returned as artifact bytes. Private OCI applies the same bounded upload model
under `/v2/feeds/{id}/{repository}/` with exact feed/repository session identity.
A Bearer-token issuer, multi-node sessions and broader provider qualification
remain outside this profile.
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


## Private hosted feed API (E0)

Administrator lifecycle routes are `POST /api/v1/admin/feeds/drafts`,
`POST /api/v1/admin/feeds/{id}/activate`, `POST .../{id}/deactivate` and
`DELETE /api/v1/admin/feeds/drafts/{id}` for a never-activated, completely
unreferenced disabled draft. A previously activated feed ID stays reserved
even when empty/deactivated, preserving the identity of managed/OIDC grants;
reactivation remains available. Creation also rejects historically deleted IDs
that retain activation audit, exact managed-grant or pending-publication
references. Managed grants and pending publication intents
also prevent draft deletion.
Activation supports private hosted NuGet, npm, Cargo, PyPI, OCI, Maven, Go
and Raw. Proxy and Skill drafts stay disabled; group mode is not accepted.
Deactivation preserves package data and denies further native traffic.
Inventory includes `isolatedHostedKinds`; the legacy
`isolationSupported: false` compatibility field still describes the
non-routing inventory/default descriptors.

Use revocable tokens via `POST /api/admin/tokens` with
`{"role":"read","namespace":"feed:alpha-feed:read","expiresInHours":1}`
or `role: "write"` and `namespace: "feed:alpha-feed:publish"`.
Every route below enforces exact feed permission, active kind and hosted mode.
There is no anonymous, cross-feed, global or upstream fallback.

| Adapter | Native private operations | Bounded profile |
| --- | --- | --- |
| NuGet | `/feeds/{id}/nuget/v3/index.json`, flat-container lists/files and `PUT/POST push` | Immutable nupkg/SHA-512/nuspec; no private registration/search or symbols |
| npm | `PUT /feeds/{id}/npm/{package}`; `GET/HEAD` packument, version/tag and `{package}/-/{version}.tgz` | One version/attachment; tags supplied during publish; no mutable tag API or unpublish |
| Cargo | `/feeds/{id}/cargo/index/config.json`, sparse index paths; `PUT .../cargo/api/v1/crates/new`; `GET/HEAD .../crates/{name}/{version}/download` | Authenticated sparse registry, immutable metadata/crate bundle; no yank |
| PyPI | `GET/HEAD /feeds/{id}/pypi/simple/` and project pages; `/files/{name}/{filename}`; `POST .../pypi/legacy/` | Simple HTML/JSON and Twine upload; bounded canonical PEP 440 without epochs; no direct dependency URLs or extra-index fallback |
| OCI | `/v2/feeds/{id}/{repository}/blobs/...`, `/manifests/{reference}`, `/tags/list`, `/referrers/{digest}` | SHA-256 digest assets, mutable tags and durable sessions; alternate descriptor URLs rejected, inline data digest/size-bound; no blob/manifest deletion |
| Maven | `PUT/GET/HEAD /feeds/{id}/maven/{release-path}`, generated `maven-metadata.xml` and checksum sidecars | Immutable validated release files; matching checksum acknowledgements; no mutable metadata PUT/deploy/SNAPSHOT |
| Go | `POST /feeds/{id}/api/go/modules`; `GET /feeds/{id}/go/{module}/@v/list`, `.info/.mod/.zip`, `@latest` | Immutable triplet; native GOAUTH over HTTPS; no upstream mirror or external checksum trust |
| Raw | `PUT/GET/HEAD /feeds/{id}/raw/{name}/{version}`; `GET .../metadata` | Immutable generic bytes with SHA-256/range reads, without a native package format |

OCI preserves the native `/v2/` prefix, so `feeds/` is reserved as a global
OCI repository prefix. Other legacy roots are retained. Schema V10 records
scoped asset identity and separate OCI tags. npm/Cargo/PyPI publish metadata
and original bytes in one immutable bundle; Go publishes one immutable
triplet. [Feed operations](feeds.md) gives client configuration, exact
storage/authorization semantics and supported features. Private feed records
are excluded from global REST/MCP discovery; [MCP-P5](mcp-roadmap.md) is separate.


## MCP

The MCP Registry accepts a bounded, inert `server.json` descriptor with remote and/or package distributions; package descriptors never trigger installation, execution, interpolation or outbound requests. See [Registry profile and rejected shapes](mcp-registry-catalog.md). Full official schema conformance, publisher verification and signatures remain separate roadmap tasks.

### Artifact and Skill discovery

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

Packory provides a versioned, bounded **remote/package** MCP server.json catalog separate from its own Streamable HTTP MCP service. For operator staging and digest-bound approval/revocation, use `POST /api/v1/admin/mcp/servers/stage` (raw 16 KiB server.json) and `POST /api/v1/admin/mcp/servers/decide` (name, version, sha256, action and reason). Both require an administrator; publish/revoke decisions are immutable-key and auditable.

The supported Registry v0.1 read contract provides `GET /v0.1/servers`, `GET /v0.1/servers/{serverName}/versions` and `GET /v0.1/servers/{serverName}/versions/{version}` (exact or `latest`). Lists use official `servers`/`metadata` envelopes and opaque `nextCursor`; per-entry Packory review information lives in `_meta["io.github.cenn0.packory/registry"]`. Search is literal and name-only, limits are 1–100/default 30. Encode a complete server name as one path segment. Unsupported synchronization/deleted-record filters are explicitly rejected.

Packory `GET /api/v1/mcp/servers?q=...&limit=25&cursor=...` adds name/title search for the dashboard; explicit offsets remain for legacy clients. `GET /api/v1/mcp/server?name=...&version=...` returns an exact descriptor. MCP tools `search_mcp_servers(query, limit=20, cursor?, version?)` and `get_mcp_server(name, version)` share the published catalog. Every route exposes only approved-for-discovery records; publisher ownership and connection safety remain unverified. Complete supported fields, lifecycle, envelope/digest semantics and remaining Registry work are in [MCP registry catalog](mcp-registry-catalog.md).

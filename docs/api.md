# API and client compatibility (MVP)

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

**Important:** the MVP does not expose a fully featured NuGet server API: symbols, vulnerability resources, unlisting, repository signatures, and advanced metadata/registration behaviors are not included. NuGet client compatibility is tested incrementally and should be treated as **basic** until the live smoke workflow succeeds.

## npm

- Packument: `GET /npm/{package}` (including scoped package names)
- Version metadata: `GET /npm/{package}/{version}`
- Rewritten tarball: `GET /npm/{package}/-/{version}.tgz`
- Search of indexed versions: `GET /npm/-/v1/search?text={term}`
- Hosted publish: `PUT /npm/{package}`, one-version packument with one base64 tarball attachment

`GET` packuments merge upstream data with local hosted versions (local versions take precedence) and replace tarball URLs with Packory URLs. Tarballs are downloaded and cached by version upon first request. Upstream metadata has a best-effort offline file cache. npm publishing requires a pre-configured token; **npm login is not an endpoint** in this MVP.

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

Skills follow the [Agent Skills specification](https://agentskills.io/specification) conceptually, but this MVP only parses simple inline `name`/`description` frontmatter. Do not assume complete conformance or trust executable content in arbitrary ZIP files.

## HTTP error codes

Common codes: `400` malformed names or uploads, `401` no/invalid token, `403` insufficient token permission, `404` absent package/skill, `409` version already exists, `413` artifact too large, `502` upstream/error data, `504` upstream timeout. Errors do not intentionally expose environment secrets, connection strings, or upstream credentials. Inconsistent unindexed/missing local immutable blobs fail closed (502) rather than silently serving or substituting bytes from another origin. Readiness is 503 if a hosted blob is missing at startup. npm proxy tarballs require an upstream `sha512-` integrity or SHA-1 `shasum` claim that matches the downloaded bytes.

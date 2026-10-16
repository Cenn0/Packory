# Packory V2 REST and native protocol routes

V2 management APIs use Bearer tokens (`Authorization: Bearer …`). API secrets must not be placed in URLs. Other than `/health/*` and explicitly public feed reads, unauthenticated V2 calls are rejected.

## Control plane

| Method | Route | Capability | Description |
|---|---|---|---|
| GET | `/api/v1/repositories` | `catalog.read` | List repositories |
| POST | `/api/v1/repositories` | `repository.manage` | Create a hosted or allowlisted proxy feed |
| GET | `/api/v1/repositories/{slug}` | `repository.read` on feed | Get settings (no secrets) |
| PATCH | `/api/v1/repositories/{slug}/visibility` | `repository.manage` | Explicitly enable or disable anonymous reads |
| GET | `/api/v1/catalog?q=&kind=&feed=&limit=` | `catalog.read` or feed read | Search published V2 assets |
| POST | `/api/v1/tokens` | `system.admin` | Issue high-entropy token (plaintext returned once) |
| DELETE | `/api/v1/tokens/{id}` | `system.admin` | Revoke token |
| GET | `/api/v1/audit` | `system.admin` | Read redacted audit records |
| GET | `/api/v1/jobs` | `system.admin` | Inspect persistent jobs |
| POST | `/api/v1/maintenance/reconcile` | `system.admin` | Queue staging/CAS reconciliation |

Valid feed protocols: `nuget`, `npm`, `raw`, `skills`, `mcp`, `cargo`, `pypi`. Valid modes: `hosted`, `proxy`. Proxy mode is restricted to NuGet and npm with known approved HTTPS origins. Unused feed protocols do **not** imply a working native adapter.

Token scopes: `catalog.read`, `repository.read`, `artifact.publish`, `artifact.yank`, `repository.manage`, `policy.manage`, `mcp.publish`, `system.admin`. For most client tokens, set a specific feed. Legacy environment tokens remain global for backwards compatibility.

## Raw hosted API

`PUT /r/raw/{feed}/{version}/{path}` accepts a bounded raw request body and optional `X-Packory-SHA256: <lowercase SHA-256>`. First successful publication returns 201; the same identity/version cannot be overwritten (409). `GET/HEAD` returns immutable original bytes with range processing and digest response headers; a yanked or quarantined artifact returns 404. State changes use `PATCH /api/v1/artifacts/raw/{feed}/{version}/{path}` with `{ "state": "yanked" }` (alternatively `quarantined`/`published`) and `artifact.yank` scope. No archive is unpacked or executed.

## NuGet V3 per-feed routes

`GET /r/nuget/{feed}/v3/index.json` publishes only the advertised package base, basic registration and hosted push endpoints. `GET /r/nuget/{feed}/v3/flatcontainer/{id}/index.json` lists known and for proxy feeds upstream versions. `GET .../{version}/{id}.{version}.nupkg` streams verified bytes; `.sha512` and `.nuspec` are served from the cached original ZIP. Hosted push accepts one multipart NuGet package at `PUT/POST /r/nuget/{feed}/v3/push`. Proxy downloads verify the original SHA-512 and NuGet identity before publication.

Proxy registration indexes and leaf JSON are fetched from the fixed official NuGet origin, validated for JSON syntax, cached and their registration/package-content URLs rewritten to the current Packory feed. Cached registration JSON remains available on origin connectivity failure. **Known limitation:** registration metadata compatibility is partial. Do not treat the V2 NuGet adapter as universally conformant until native dotnet tests pass.

## npm per-feed routes

`GET /r/npm/{feed}/{name}` returns a packument; version-specific metadata and `/-/{version}.tgz` tarballs are supported. Origin tarball URLs are restricted to approved HTTPS hosts and converted to feed-local Packory URLs. Verify `integrity` or `shasum` before catalog publication. Hosted `PUT /r/npm/{feed}/{name}` accepts single-version npm publish JSON with one base64 attachment.

**Known limitation:** npm full command coverage is not claimed; `npm login`, unpublish, scoped publish edges and metadata freshness rules require additional development.

## Cargo sparse index (experimental)

`GET /r/cargo/{feed}/index/config.json` returns Cargo download metadata. `GET /r/cargo/{feed}/index/{path}` returns checksum-anchored JSON Lines using Cargo's crate-name sharding. `GET /r/cargo/{feed}/api/v1/crates/{crate}/{version}/download` serves an immutable `.crate` archive. The initial Packory-specific upload method is `PUT /r/cargo/{feed}/publish/{crate}/{version}` with a gzip-compatible body. Cargo standard publisher API and client conformance are not claimed.

## PyPI Simple API (experimental)

`GET /r/pypi/{feed}/simple/` lists hosted project names. `GET /r/pypi/{feed}/simple/{normalized-project}/` returns HTML links to hosted files with SHA-256 fragments, and `GET /r/pypi/{feed}/files/{filename}` downloads the original immutable wheel/sdist. The initial **nonstandard** upload method is `PUT /r/pypi/{feed}/upload/{project}/{version}/{filename}`. PEP 691 JSON, Twine publishing and native pip installs must still be verified.

## MCP registry (experimental)

`GET /v0.1/servers`, `GET /v0.1/servers/{serverName}/versions` and the version-detail route list approved servers only. `POST /v0.1/publish` is administrator-only pending publisher namespace validation; newly published server.json is quarantined. Admin approval: `POST /api/v1/mcp/{serverName}/versions/{version}/approve`. Server names containing `/` must be URL-encoded or routed as slash-qualified names. No remote URLs are dereferenced as part of publication.

This is not the MCP runtime wire protocol or an MCP proxy. The generic registry API is evolving; interoperability certification is pending.

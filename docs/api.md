> **V2 notice:** New V2 endpoint contracts are documented in [V2 API](api-v2.md). This earlier page describes original endpoint compatibility.

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

Common codes: `400` malformed names or uploads, `401` no/invalid token, `403` insufficient token permission, `404` absent package/skill, `409` version already exists, `413` artifact too large, `502` upstream/error data, `504` upstream timeout. Errors do not intentionally expose environment secrets, connection strings, or upstream credentials.

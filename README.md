# Packory

**A lightweight, self-hosted distribution hub for software packages and AI artifacts.**

[![CI](https://github.com/Cenn0/Packory/actions/workflows/ci.yml/badge.svg)](https://github.com/Cenn0/Packory/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)

Packory V2 builds on its original NuGet/npm/Agent Skills registry with **isolated repositories, a control-plane API, immutable SHA-256 content-addressed storage, feed-scoped/revocable tokens, native protocol routes, an audit log and a durable maintenance queue**. The modular ASP.NET Core 10 application uses SQLite and local disk, with a single container and no required external services.

**Release status:** V2 architecture is being implemented incrementally. The V2 foundation and adapters described below are implemented; the full [architecture target](docs/design-v2/TARGET-ARCHITECTURE.md) is **not yet a production-certified implementation**. A green CI result is not equivalent to native-client compatibility, a penetration test, or high availability. See the [support matrix](docs/v2-status.md) and [migration guidance](docs/migration-v2.md).

## Install

Requirements: Docker Compose or the .NET 10 SDK. Configure a randomly generated secret via environment variables and keep it out of version control:

```bash
export PACKORY_ADMIN_TOKEN="$(openssl rand -hex 32)"
docker compose up --build -d
```

Open <http://localhost:8080/>. The dashboard holds a pasted API token **only in browser memory**. Do not use plain HTTP with credentials over a shared network; terminate TLS at a trusted reverse proxy and configure `PACKORY_PUBLIC_BASE_URL` accordingly.

```bash
# Without Docker
export PACKORY_ADMIN_TOKEN="$(openssl rand -hex 32)"
export PACKORY_DATA_DIRECTORY="./data"
dotnet run --project src/Packory --urls http://localhost:8080
```

Initial local access is bootstrap-token based, not a default password. Protect the token as a root credential; use scoped tokens for clients. **V2 repository reads default to private** regardless of the legacy global read setting.

## Configure V2 repositories

Create an isolated repository using the admin token (shown here with a Raw feed):

```bash
curl --fail-with-body -X POST http://localhost:8080/api/v1/repositories \
  -H "Authorization: Bearer $PACKORY_ADMIN_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"slug":"team-files","protocol":"raw","mode":"hosted","visibility":"private"}'
```

To publish an immutable file version:

```bash
curl --fail-with-body -X PUT http://localhost:8080/r/raw/team-files/1.0.0/readme.txt \
  -H "Authorization: Bearer $PACKORY_ADMIN_TOKEN" \
  --data-binary @README.md
curl --fail-with-body http://localhost:8080/r/raw/team-files/1.0.0/readme.txt \
  -H "Authorization: Bearer $PACKORY_ADMIN_TOKEN" -o restored.txt
```

To create scoped client credentials, set `feed` to an existing repository and choose minimal scopes such as `repository.read` or `artifact.publish`:

```bash
curl --fail-with-body -X POST http://localhost:8080/api/v1/tokens \
  -H "Authorization: Bearer $PACKORY_ADMIN_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"name":"example-reader","feed":"team-files","scopes":["repository.read"],"expiresAt":null}'
```

The raw token is returned **only once**. The database stores a SHA-256 hash, expiry and revocation state. Scope and repository are checked server-side. Revoke a token using `DELETE /api/v1/tokens/{id}`.

### NuGet feeds

Create a hosted feed with `protocol=nuget,mode=hosted` or a nuget.org proxy with `protocol=nuget,mode=proxy` and `upstream=https://api.nuget.org/v3-flatcontainer/`. Each feed exposes its own service index:

```text
/r/nuget/{feed}/v3/index.json
/r/nuget/{feed}/v3/flatcontainer/{id}/{version}/{id}.{version}.nupkg
/r/nuget/{feed}/v3/push
```

Hosted and proxy data are stored under different repository identities. Proxy package bytes are checked against upstream SHA-512, validated for NuGet ID/version and streamed into SHA-256 CAS. A warm immutable artifact is served without a new origin request. **Full native `dotnet restore` compatibility for the V2 feed routes remains a conformance gate**; use the existing legacy service index if your workflow requires the already exercised v1 integration.

### npm feeds

Create a hosted feed with `protocol=npm,mode=hosted` or a proxy with `protocol=npm,mode=proxy` and upstream `https://registry.npmjs.org/`. Configure the npm registry URL:

```text
/r/npm/{feed}/
```

Published tarballs are immutable and feed isolated; proxy tarballs verify declared `sha512` integrity or `sha1` shasum before their metadata becomes visible in the V2 catalog. Only public registry origins explicitly approved in the code are accepted currently. npm `login`, unpublish and client-wide conformance are **not claimed**.

### MCP server discovery (metadata only)

A metadata registry is available under `/v0.1/servers` and `/v0.1/publish`. Publication currently requires administrative authorization and places entries into **quarantine**. Separate approval is required before discovery; remote MCP tools are **never executed or proxied by Packory**. This is an implementation of a subset of the generic MCP Registry preview API, not full conformity certification. [Read the API details](docs/api-v2.md).

### Existing NuGet, npm and Agent Skills endpoints

The V2 schema is **additive**. Existing clients can continue to access `/nuget/v3/index.json`, `/npm/` and `/api/skills`. The legacy artifact cache is intentionally not rewritten silently; it remains separate until a validated import tool is released. Treat these legacy endpoints as **global feeds**, not as isolated V2 repositories. Their original global read/write token policy still applies.

## Architecture

```text
    dotnet / npm / curl / dashboard / agent
                    │
            ASP.NET Core host
                    │
     ┌──────────────┼─────────────────┐
     │              │                 │
  Native         Control            AI metadata
  adapters       Plane + RBAC       catalog
     │              │                 │
     └──────────────┼─────────────────┘
                    │
              V2 Catalog (SQLite)
                    │
           SHA-256 CAS (filesystem)
                    │
           Durable maintenance jobs
```

- **Database:** additive SQLite schema for repositories, immutable assets, CAS references, token hashes, audit, jobs and MCP manifests.
- **CAS:** bounded streamed writes into temporary staging, computed SHA-256, atomic file publication before DB visibility.
- **Security:** no anonymous publish, per-feed read visibility, bearer/basic scoped tokens, explicitly approved external origins, no external execution.
- **Operations:** worker recovers queued jobs and reclaims abandoned staging/orphan CAS content after a grace period. Quarantine and yank suppress downloads without deleting referenced blobs.
- **Deployment:** one process, one SQLite writer. No supported multi-instance HA and no shared network SQLite mount.

Full specification: [Architecture target](docs/design-v2/TARGET-ARCHITECTURE.md), [ADRs](docs/design-v2/DECISIONS.md), [Protocol goals](docs/design-v2/PROTOCOL-COMPATIBILITY.md) and [Threat model](docs/design-v2/THREAT-MODEL.md). The target documents describe **planned** capabilities as well as delivered foundations; [V2 implementation status](docs/v2-status.md) is the authoritative distinction.

## Verify and contribute

```bash
dotnet restore Packory.slnx
dotnet build Packory.slnx -c Release --no-restore -warnaserror
dotnet test Packory.slnx -c Release --no-build
node --check src/Packory/wwwroot/app.js
python3 scripts/check-public-hygiene.py
```

GitHub Actions runs the deterministic API/integration test suite and builds the Docker image on pull requests. A separately scheduled/manual smoke workflow exercises the legacy native NuGet/npm clients with public upstreams; it is not evidence of V2 feed-route client parity.

**Security-sensitive deployments:** Review [SECURITY.md](SECURITY.md), [operations](docs/operations-v2.md) and the [support matrix](docs/v2-status.md) before internet exposure. No malware scanning or provenance signature validation is included. Never execute untrusted skills or MCP servers as part of registry import.

Licensed under [Apache-2.0](LICENSE). Public contributions: [CONTRIBUTING.md](CONTRIBUTING.md). Do not publish credentials, personal contact data, internal networks, or private configuration in commits, issues, or docs.

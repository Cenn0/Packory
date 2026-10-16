# Packory

**One self-hosted hub for NuGet packages, npm packages, and versioned agent skills.**

[![CI](https://github.com/Cenn0/Packory/actions/workflows/ci.yml/badge.svg)](https://github.com/Cenn0/Packory/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)

Packory's first MVP is an **ASP.NET Core 10 modular monolith**, not a general-purpose Git forge and not a collection of separate services. It provides a unified dashboard and catalog while retaining the **native NuGet v3 and npm registry protocols**. A local SQLite database tracks indexed artifacts; files live on disk. No Redis, message broker, Kubernetes, or external database is needed.

> **MVP scope:** functional pull-through package downloads, basic hosted publishing, a searchable catalog of *cached/hosted* artifacts, agent-skill ZIP imports, static token permissions, a minimal dashboard, health endpoints, Docker support, and tests. Packory is not yet a production-hardened registry replacement. See [Roadmap](docs/roadmap.md) and [Limitations](#limitations).

## Quick start

Prerequisites: **Docker Compose** or the **.NET 10 SDK**.

```bash
# Choose a random 32+-character token. Keep it out of Git and shell history.
export PACKORY_ADMIN_TOKEN="$(openssl rand -hex 32)"
docker compose up --build -d
```

Open **http://localhost:8080/**. The dashboard shows packages after their first cache download or when they are hosted locally. Paste the API token in the dashboard only when importing a skill; the browser keeps it in memory, not local storage.

By default, reads are anonymous, writes require a configured token, and binding is to the local host port in Compose. **Before exposing Packory to a network**, configure TLS at a reverse proxy, access policies, and a publicly reachable `PACKORY_PUBLIC_BASE_URL`. [Operations and security](docs/operations.md) has details.

Run without Docker:

```bash
export PACKORY_ADMIN_TOKEN="$(openssl rand -hex 32)"
export PACKORY_PUBLIC_BASE_URL=http://localhost:8080
export PACKORY_DATA_DIRECTORY=./data
dotnet run --project src/Packory --urls http://localhost:8080
```

### NuGet: mirror nuget.org

Packory v3 service index: `http://localhost:8080/nuget/v3/index.json`

```bash
# List, restore, or install packages through Packory:
dotnet nuget add source http://localhost:8080/nuget/v3/index.json \
  --name packory --allow-insecure-connections

# Use an isolated NuGet.Config with only Packory to prove mirror traffic flows through it:
# See scripts/smoke-nuget.sh or docs/api.md.

# Publish a local package (admin token, from the environment):
dotnet nuget push ./your-package.1.0.0.nupkg \
  --source http://localhost:8080/nuget/v3/index.json \
  --api-key "$PACKORY_ADMIN_TOKEN"
```

NuGet **package version indexes**, `.nupkg`, `.nuspec`, `.sha512` and **registration metadata** are available. For upstream packages Packory contacts nuget.org on first use, caches content, verifies SHA-512 if published upstream, and rewrites registration content URLs back to Packory. Cached artifacts remain available during upstream outages. The upstream version index still requires a live upstream for fresh versions; locally published and already indexed versions remain visible.

### npm: mirror registry.npmjs.org

```bash
# For a project only (avoids modifying your global npm configuration):
npm install --registry=http://localhost:8080/npm/ lodash

# Publishing requires a configured token. npm login is NOT implemented.
# Put registry and token in a local, UNTRACKED user/project .npmrc:
# registry=http://localhost:8080/npm/
# //localhost:8080/npm/:_authToken=${PACKORY_ADMIN_TOKEN}
npm publish --registry=http://localhost:8080/npm/
```

Packory rewrites npm `dist.tarball` URLs to its own endpoint, verifies upstream SHA-512 or SHA-1 if supplied, and caches the tarball. Scoped packages and single-version `npm publish` payloads are supported. **Never commit `.npmrc` with credentials.** Do not use plain HTTP for tokens except loopback development; publish through HTTPS in shared environments.

### Agent skills: import a versioned ZIP

A ZIP must contain `SKILL.md` at its root or inside one top-level directory named after the skill. Its YAML frontmatter must include `name` and `description`. The importer validates the archive paths and size; it does not execute bundled code.

```bash
curl -X POST http://localhost:8080/api/skills \
  -H "Authorization: Bearer $PACKORY_ADMIN_TOKEN" \
  -F 'version=1.0.0' -F 'file=@my-skill.zip'

curl http://localhost:8080/api/skills
```

Skill downloads: `/api/skills/{name}/{version}/download`. The MVP recognizes **simple single-line YAML `name` and `description` fields**; full YAML semantics and portable skill-install tooling are post-MVP work.

## Endpoints

| Purpose | Endpoint |
| --- | --- |
| Dashboard | `/` |
| Unified catalog (indexed/hosted only) | `GET /api/packages?q=&kind=&limit=` |
| NuGet v3 service index | `GET /nuget/v3/index.json` |
| NuGet publish | `PUT /nuget/v3/push` |
| npm registry | `/npm/` |
| Agent skills list/import | `GET/POST /api/skills` |
| Liveness / readiness | `/health/live`, `/health/ready` |

Protocol details and manual examples: [API documentation](docs/api.md).

## Design

```text
 NuGet CLI / npm CLI / Dashboard / curl
                │
                ▼
      ASP.NET Core 10 · Packory
      ├── Native NuGet v3 endpoints ──── nuget.org (fixed upstream)
      ├── Native npm endpoints ───────── registry.npmjs.org (fixed upstream)
      ├── Skills importer / API
      ├── Unified catalog and dashboard
      ├── Token access control (read / write / admin)
      └── SQLite metadata + atomic local blob storage
```

[Architecture and trust boundaries](docs/architecture.md) · [Operations](docs/operations.md) · [Roadmap](docs/roadmap.md)

## Development and tests

```bash
dotnet restore Packory.slnx
dotnet build Packory.slnx -c Release --no-restore
dotnet test Packory.slnx -c Release --no-build
node --check src/Packory/wwwroot/app.js
```

Integration tests use in-process ASP.NET hosting and a deterministic **stub registry**, covering authorization, NuGet caching/checksums/publish, npm scoped mirroring/publish, ZIP validation, and catalog operations. GitHub Actions is configured to run tests and a container build; a separate manual/scheduled workflow is configured to exercise **real nuget.org and npmjs.org installs** through the Packory proxy. See [CI](.github/workflows/ci.yml) and [live smoke test](.github/workflows/smoke-live.yml).

## Limitations

- Single-node deployment and static environment-provided tokens; no accounts, per-package policies, SSO, audit trail, multi-node replication, or resilient HA yet.
- Minimal npm publish implementation (one version per request); **npm login**, unpublish, and comprehensive npm registry endpoints are not provided. Some npm clients/workflows may require missing APIs.
- NuGet supports principal v3 install/mirror/push operations but does not claim full v3 feature parity (symbols, deprecation, vulnerability resources, registration edge cases, advanced SemVer normalization). Full native-client compatibility must be validated per client version.
- Search shows **cached and hosted packages only**, not all upstream entries. The dashboard is a discovery/admin MVP, not a complete registry UI.
- Metadata may need upstream connectivity on cold starts; caches use best-effort offline fallbacks. No quota eviction, coordinated cache locks, malware scanning, package-signature enforcement, upstream allow/block rules, or automatic license compliance checks.
- Agent skills use basic frontmatter validation rather than a complete YAML/Agent Skills conformance implementation. Import does **not** imply an uploaded skill is safe to execute.
- PostgreSQL, S3, additional ecosystems (Cargo, PyPI, OCI), OIDC, and multi-service deployment are **roadmap items, not MVP features**.

## Open source

[Apache License 2.0](LICENSE). Contributions are welcome through [issues and pull requests](https://github.com/Cenn0/Packory/issues). Please read [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

**Privacy:** This public repository must never contain personal contact details, credentials, keys, private URLs, internal hostnames, or private infrastructure configuration. Use GitHub issues and security advisories for communication.

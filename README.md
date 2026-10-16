<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/branding/banner-dark.svg">
  <img src="docs/branding/banner-light.svg" alt="Packory — One hub. Endless possibilities. NuGet, npm and agent skills." width="1200">
</picture>

# Packory

**Brand and UI design:** [Logo + light/dark templates](docs/branding/README.md) (design concepts only).

**Language:** **English** · [Deutsch](README.de.md)

[![CI](https://github.com/Cenn0/Packory/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Cenn0/Packory/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)

**Explore:** [Quick start](#quick-start) · [Capabilities](#what-works-in-the-mvp) · [Architecture](#architecture-overview) · [Docs](docs/README.md) · [Roadmap](docs/roadmap.md)

**A self-hosted hub for NuGet, npm and versioned AI agent skills.**


Packory combines native package-registry endpoints, an indexed artifact catalog and a small web dashboard. The current **MVP** is an **ASP.NET Core / .NET 10 modular monolith**, using SQLite and local file storage. No separate database, broker or Kubernetes cluster is required. Milestone-1 hardening includes single-process artifact locks, reserved private namespaces, startup storage reconciliation, **transactional audit records for successful hosted publishing**, and role-partitioned publish rate limiting.

> **Status: MVP, not production-hardened.** Protocol coverage and real-client compatibility are incomplete. The catalog lists packages already hosted or cached by Packory, not the complete upstream registries.

## Quick start

Requires Docker Compose. For local evaluation:

```bash
git clone https://github.com/Cenn0/Packory.git
cd Packory
export PACKORY_ADMIN_TOKEN="$(openssl rand -hex 32)"
docker compose up -d --build
```

Open **http://localhost:8080/**. The token enables administrative actions; do not commit it or paste it into issues. The example Compose configuration binds to `127.0.0.1:8080` and persists data in the `packory-data` volume.

To run directly with the .NET 10 SDK instead:

```bash
export PACKORY_ADMIN_TOKEN="$(openssl rand -hex 32)"
export PACKORY_PUBLIC_BASE_URL=http://localhost:8080
dotnet run --project src/Packory --urls http://localhost:8080
```

See [operations](docs/operations.md) before enabling network access or choosing a permanent data directory.

## What works in the MVP

| Area | Implemented scope |
| --- | --- |
| **NuGet V3** | Service index, package/version downloads, read-through caching from nuget.org, registration metadata and basic hosted publishing |
| **npm** | Upstream metadata and tarball caching, scoped package handling and basic single-version publishing |
| **OCI (experimental)** | Hosted Distribution v2 endpoints for SHA-256 blobs, chunk uploads, manifests, tags and referrers; incomplete client/auth support |
| **Cargo & PyPI (experimental)** | Hosted Cargo alternate registry (sparse index, binary publish), PyPI Simple API (HTML/JSON) and basic distribution upload; not full ecosystem support |
| **Agent skills** | Import, list and download versioned ZIP files containing a simple `SKILL.md` manifest |
| **Dashboard/API** | Searchable catalog of cached/hosted artifacts and liveness/readiness endpoints |
| **Access** | Configurable static read, write and admin tokens; anonymous read enabled by default; admin-only successful-publish audit |

### Use Packory as a registry

NuGet service index: `http://localhost:8080/nuget/v3/index.json`.

```bash
# HTTP is appropriate only for local development.
dotnet nuget add source http://localhost:8080/nuget/v3/index.json \
  --name packory --allow-insecure-connections

# Use the local npm registry for one command.
npm install --registry=http://localhost:8080/npm/ lodash
```

Publish operations require an appropriate token. [API examples](docs/api.md) explain NuGet push, npm publishing and agent-skill ZIP imports without placing credentials in tracked files. The skills importer **validates archives but does not establish that their contents are safe to execute**.

## Architecture overview

```mermaid
flowchart LR
  C[NuGet and npm clients] --> A[Packory ASP.NET Core]
  K[Skill ZIP importer] --> A
  A --> N[Protocol adapters]
  A --> S[Skills and catalog API]
  N --> D[(SQLite metadata)]
  S --> D
  N --> B[(Local artifact files)]
  S --> B
  N -.->|Optional upstream HTTPS| U[nuget.org and npmjs.org]
```

NuGet and npm keep their **own native protocols** while sharing the catalog and storage. In-process coordination and hosted-publish auditing are not multi-node or distributed ACID guarantees. See [architecture](docs/architecture.md).

## Limitations and security

- NuGet and npm support useful registry flows, but **not full protocol parity**. Verify your exact client/version/workflow before relying on Packory.
- Search covers locally indexed artifacts. Fresh upstream metadata and uncached downloads may require connectivity.
- Static tokens are not individual user accounts. **OIDC/SSO, per-package permissions, individual actor attribution, a comprehensive/immutable audit trail and high availability are not implemented.** The existing admin-only log records **successful hosted publishes**, not every action or denied attempt.
- Reserved NuGet ID prefixes and npm scopes can prevent configured private namespaces from falling back to public upstreams. This is **not** a comprehensive package-policy or malware-scanning solution. Signature enforcement, cache eviction and automatic license compliance are also absent.
- **Cargo, PyPI, OCI, PostgreSQL, S3 and distributed services are planned, not implemented.**

Per-role in-process publish throttling complements, but **does not replace, proxy-level rate limits**. Single-process locks do **not** provide multi-node consistency. Startup reconciliation can detect or quarantine selected inconsistent artifacts; it does not replace verified backups. The default Docker port is loopback-only. For any shared deployment, use HTTPS and explicit access controls; keep tokens in a secrets manager, review logging and upstream network access, and back up **SQLite and blobs together**. Do not execute untrusted skills or packages on the server. See [security and operations](docs/operations.md).

## Build and test

```bash
dotnet restore Packory.slnx
dotnet build Packory.slnx -c Release --no-restore
dotnet test Packory.slnx -c Release --no-build
node --check src/Packory/wwwroot/app.js
# Requires the .NET 10 SDK, npm/Node.js, curl and OpenSSL:
bash scripts/smoke-hosted.sh
```

[CI](.github/workflows/ci.yml) runs builds, deterministic integration tests and a [native-client hosted smoke test](scripts/smoke-hosted.sh): a real `dotnet nuget push` followed by an isolated `dotnet restore/build`, plus a scoped `npm publish` followed by `npm install` and fresh-cache `npm ci`. Test credentials are generated temporarily and never committed. A [separate live mirror workflow](.github/workflows/smoke-live.yml) checks upstream registry behavior; passing one native hosted scenario does not imply comprehensive protocol support.

## Documentation and contributions

[Documentation index](docs/README.md) · [Cargo & PyPI](docs/cargo-pypi.md) · [OCI](docs/oci.md) · [OIDC / Keycloak](docs/oidc-keycloak.md) · [Architecture](docs/architecture.md) · [API](docs/api.md) · [Operations](docs/operations.md) · [Roadmap](docs/roadmap.md) · [Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md)

Licensed under [Apache-2.0](LICENSE). Contributions and public bug reports are welcome; report security vulnerabilities using the private channel described in the security policy.

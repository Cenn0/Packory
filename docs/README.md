# Packory documentation hub

[← README (English)](../README.md) · [README auf Deutsch](../README.de.md) · [Docker quick start](../README.md#docker-quick-start)

Packory is a self-hosted distribution platform in **active development**. Documentation here distinguishes implemented behavior, evidence for **specific** native-client/backend tests and production-release qualification. Start with the quick start for a local setup; do **not** infer broad support from a single test or from a proposed design.

## Get started

| I want to… | Start here |
| --- | --- |
| **Build and run locally with Docker Compose** | [README Docker quick start](../README.md#docker-quick-start) |
| **Pull the development containers instead of building** | [GHCR images, tags and deployment](container-images.md) |
| Set up TLS, access, configuration and backups | [Operations](operations.md) |
| Configure native clients and isolated private feeds | [Feeds](feeds.md) · [API reference](api.md) |
| Check exactly what was tested | [Protocol and deployment support matrix](support-matrix.md) |
| Understand the deployed application | [Architecture](architecture.md) |

## Package registries

| Ecosystem | Documentation |
| --- | --- |
| NuGet / npm and private feed access | [Feeds and client configuration](feeds.md) · [API reference](api.md) |
| Cargo / PyPI | [Cargo and PyPI](cargo-pypi.md) |
| OCI / Helm | [OCI registry](oci.md) · [Upload recovery](oci-upload-recovery.md) · [Process recovery](oci-process-recovery.md) |
| Maven / Gradle | [Maven](maven.md) |
| Go | [Go hosted GOPROXY](go-proxy.md) |
| Generic files | [Raw registry](raw-registry.md) |
| Integrity and identifiers | [Download integrity](download-integrity.md) · [npm integrity](npm-download-integrity.md) · [Identifiers](identifier-validation.md) |

## Agent Skills, MCP and identity

| Topic | Documentation |
| --- | --- |
| Offline SkillSpector, approval and distribution policy | [Skill security integration](mcp-security-integration.md) · [Archive validation](skill-archive-validation.md) |
| **Optional** AI-assisted inspection | [Skill AI: setup, limits and opt-in](skill-ai.md) |
| Deterministic documentation quality assessment | [Skill quality](skill-quality.md) |
| Read-only MCP endpoint and remaining work | [MCP delivery roadmap](mcp-roadmap.md) · [MCP validation](mcp-validation-results.md) |
| Reviewed inert MCP Registry metadata | [MCP Registry catalog](mcp-registry-catalog.md) |
| OIDC resource server / Keycloak test profile | [OIDC and Keycloak](oidc-keycloak.md) |

## Architecture, operations and verification

| Topic | Documentation |
| --- | --- |
| Component model, storage and consistency | [Runtime architecture](architecture.md) · [ADR-001 V3 boundaries](adr/0001-v3-platform-boundaries.md) |
| Storage providers and recovery | [S3](s3.md) · [PostgreSQL/S3 offline recovery](pg-s3-offline-recovery.md) · [Schema/versioning](schema-versioning.md) |
| Build and release verification | [CI qualification](ci-qualification.md) · [Supply chain](ci-supply-chain.md) · [Release gates](release-gates.md) · [Releasing](RELEASING.md) |
| Safety and public contributions | [Public privacy policy](public-privacy.md) · [Security](../SECURITY.md) · [Contributing](../CONTRIBUTING.md) |
| **Latest detailed review in this repository** | [Architecture, correctness and security review — 23 October 2026](review-2026-10-23.md) |
| Evidence for eight private hosted package protocols | [E0 acceptance](e0-acceptance.md) |

## Roadmaps and historical evidence

**Current planning entry point:** [Roadmap V5](roadmap-v5.md). It describes outstanding work and acceptance gates, **not** a commitment that every item is already implemented.

- [Experimental capability graduation](experimental-graduation.md) tracks bounded protocol maturity.
- [Roadmap V4](roadmap-v4.md) and [Roadmap V3](roadmap.md) are historical plans; they can contain superseded status statements.
- [20 October checkpoint](roadmap-status-2026-10-20.md), [18 October checkpoint](roadmap-status-2026-10-18.md) and [17 October delivery evidence](delivery-evidence-2026-10-17.md) describe **dated snapshots**, not today's CI status.
- [Skill security roadmap](skill-security-roadmap.md) records historical work and unresolved assurance tasks.
- [Brand and light/dark assets](branding/README.md) document design concepts, not necessarily the deployed UI.

## How to interpret evidence

| Level | Meaning | What it does **not** establish |
| --- | --- | --- |
| **Implemented** | Code and routes exist at a stated revision | Client compatibility or release readiness |
| **Tested** | Named operation, native client/SDK, provider, configuration and revision passed | Other versions, OSs, services or real-cloud recovery |
| **Packory-supported profile** | A maintained, explicitly scoped compatibility and operating contract has been accepted | Upstream vendor endorsement or universal protocol compliance |
| **Production recommendation** | Actual deployment, recovery, security and operational release gates have been met | A guarantee for untested configurations |

**Important unresolved guarantees:** browser SSO/BFF, broad interoperability, externally verified publisher identity, coordinated real-provider restores, complete supply-chain qualification and active-active writes. Never treat static Skill quality or optional AI output as malware certification. See [Support matrix](support-matrix.md) and [Release gates](release-gates.md).

Do not publish screenshots or examples containing personal identities, email addresses, secrets, private URLs, proprietary packages or production data. This is a public repository.

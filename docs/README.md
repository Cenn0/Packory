# Packory documentation

**Find the right guide without reading the entire repository.**

[← English README](../README.md) · [Deutsch](../README.de.md) · [Current status](project-status.md) · [Support matrix](support-matrix.md)

Packory is a self-hosted registry hub under active development. This index is organized by **the task you want to complete**. Protocol support and security claims depend on the exact documented implementation and test profiles; **no general production release is implied**.

## Choose a starting point

| I want to… | Start here | Continue with |
| --- | --- | --- |
| Evaluate Packory locally | [Quick start](../README.md#get-started) | [API and examples](api.md) |
| Deploy or operate an instance | [Operations & security](operations.md) | [Container images](container-images.md) · [Release gates](release-gates.md) |
| Use a private registry | [Feed setup & permissions](feeds.md) | [Native-client support](support-matrix.md) |
| Integrate the platform API | [API reference](api.md) | [Architecture](architecture.md) |
| Distribute or inspect Agent Skills | [Skill security integration](mcp-security-integration.md) | [Static quality](skill-quality.md) · [Optional AI](skill-ai.md) |
| Discover packages via MCP | [MCP roadmap and delivered scope](mcp-roadmap.md) | [MCP Registry catalog](mcp-registry-catalog.md) |
| Develop a feature or contribute | [Contributing](../CONTRIBUTING.md) | [Architecture](architecture.md) · [CI qualification](ci-qualification.md) |
| Understand what is finished | [Project status — 23 October 2026](project-status.md) | [Support matrix](support-matrix.md) · [Roadmap V5](roadmap-v5.md) |

## 1. Product and architecture

| Document | What it answers |
| --- | --- |
| [Project status](project-status.md) | Concise implemented/tested/open summary with evidence boundaries |
| [Architecture](architecture.md) | Runtime topology, modules, trust boundaries and consistency invariants |
| [ADR 0001 — V3 platform boundaries](adr/0001-v3-platform-boundaries.md) | Why the modular monolith and protocol adapters were chosen |
| [ADR 0002 — validation evidence boundary](adr/0002-validation-evidence-boundary.md) | Why external validation is not equivalent to product safety or approval |
| [API reference](api.md) | Endpoints, authorization and usage examples |
| [Brand and design](branding/README.md) | Light/dark banners, logo and non-production UI designs |

## 2. Registry and AI integrations

| Ecosystem | Reference |
| --- | --- |
| Private feeds, lifecycle and exact grants | [Feeds](feeds.md) |
| NuGet and npm | [API](api.md) · [support and limitations](support-matrix.md) |
| Cargo and PyPI | [Cargo / PyPI](cargo-pypi.md) |
| OCI containers and Helm | [OCI](oci.md) · [upload recovery](oci-upload-recovery.md) · [process recovery](oci-process-recovery.md) |
| Maven and Gradle | [Maven](maven.md) |
| Go modules | [Go hosted GOPROXY](go-proxy.md) |
| Raw / Generic | [Raw registry](raw-registry.md) |
| Agent Skills | [Skill security](mcp-security-integration.md) · [archive admission](skill-archive-validation.md) · [quality](skill-quality.md) |
| Optional LLM analysis | [Skill AI review](skill-ai.md) |
| MCP tools/resources and future work | [MCP roadmap](mcp-roadmap.md) · [MCP Registry](mcp-registry-catalog.md) · [validation results](mcp-validation-results.md) |

## 3. Deployment, security and quality

| Document | Focus |
| --- | --- |
| [Operations](operations.md) | Configuration, TLS, runtime secrets, health and recovery |
| [Container images](container-images.md) | GHCR development tags, exact SHA verification and Compose deployment |
| [Support matrix](support-matrix.md) | Tested clients, providers, limitations and deployment profiles |
| [CI qualification](ci-qualification.md) | Workflows and evidence required at a specific commit |
| [Release gates](release-gates.md) | Production/recovery/security blockers and acceptance criteria |
| [Release process](RELEASING.md) | Versioned release steps distinct from development images |
| [Supply chain](ci-supply-chain.md) | CI source and artifact assurance |
| [S3](s3.md) · [S3 conformance](s3-provider-conformance.md) | Object storage boundaries and provider evidence |
| [PostgreSQL/S3 offline recovery](pg-s3-offline-recovery.md) | Backup/recovery verification procedure |
| [Schema versioning](schema-versioning.md) · [snapshot validation](snapshot-validation.md) | Migration and backup evidence |
| [Public privacy](public-privacy.md) · [Security policy](../SECURITY.md) | Public data limits and private vulnerability reporting |

## 4. Roadmaps and evidence

**Current planning document:** [Roadmap V5](roadmap-v5.md). A roadmap item is **not** a claim that a corresponding feature has passed acceptance.

| Category | Sources |
| --- | --- |
| Current, dated delivery state | [23 October status](project-status.md) · [23 October review](review-2026-10-23.md) |
| E0 private hosted-feed acceptance | [E0 acceptance](e0-acceptance.md) |
| Native and operational validation | [Native support matrix](support-matrix.md) · [CI qualification](ci-qualification.md) |
| Experimental feature graduation | [Graduation workstream](experimental-graduation.md) |
| Skills and MCP workstreams | [Skill security roadmap](skill-security-roadmap.md) · [MCP roadmap](mcp-roadmap.md) |
| Historical plans and dated checkpoints | [Roadmap V3](roadmap.md) · [Roadmap V4](roadmap-v4.md) · [20 October checkpoint](roadmap-status-2026-10-20.md) · [18 October checkpoint](roadmap-status-2026-10-18.md) · [17 October delivery evidence](delivery-evidence-2026-10-17.md) |

Historical documents remain available for traceability. Their original status descriptions are **not automatically current**. For release decisions, cross-check the target commit, workflow runs, [support matrix](support-matrix.md) and [release gates](release-gates.md).

## How to interpret claims

- **Implemented:** the described code path exists on the specified repository revision. This alone does not establish interoperability.
- **Tested:** a named test or native client/profile has documented passing evidence. A local fake provider, Moto or a particular SDK version cannot certify all providers and versions.
- **Packory-supported:** a maintained, qualified combination as explicitly described in the [support matrix](support-matrix.md). This is not an upstream vendor endorsement.
- **Best practice / security recommendation:** HTTPS, least privilege, separated feed grants, approved origins, controlled backups and independent restore drills are advised even where the code functions without them.
- **Not established:** full protocol conformance, general production readiness, active-active writers, independently verified publisher identity and secure execution of third-party Skills.

This is a public project. Keep credentials, personally identifiable information, private artifacts and real environments out of source, examples, screenshots, issues and review evidence. See [public privacy](public-privacy.md).

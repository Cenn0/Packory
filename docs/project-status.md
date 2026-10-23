# Packory project status

**Evidence checkpoint: 23 October 2026** · Applies to the public `Cenn0/Packory` repository. This page is a navigational summary, **not live CI telemetry or a release announcement**.

[README](../README.md) · [Documentation hub](README.md) · [Support matrix](support-matrix.md) · [Roadmap V5](roadmap-v5.md) · [Release gates](release-gates.md)

## What is implemented

- **Single-application platform:** ASP.NET Core / .NET 10 modular monolith, native registry adapters, shared catalog, web dashboard and management APIs.
- **Private hosted package feeds:** NuGet, npm, Cargo, PyPI, OCI, Maven, Go and Raw, with protocol-specific routes, active feed identities and exact read/publish grants. No cross-feed, global or upstream fallback for private hosted requests.
- **Developer workflows:** bounded native hosted publish/consume profiles, global NuGet/npm read-through caching, Maven/Gradle consumption and experimental Helm/Go/Raw scenarios. No blanket compatibility claim.
- **Skills and MCP:** ZIP-staged Agent Skills, offline SkillSpector checks, manual approval/revocation, deterministic static quality results, read-only MCP discovery and reviewed inert MCP Registry descriptors. Optional, operator-enabled AI analysis is a separate experimental review input; it does not replace approval.
- **Storage and identity:** SQLite/filesystem default; optional PostgreSQL/S3, optional OIDC JWT resource-server validation, managed tokens and a limited PostgreSQL/S3 reader role.

## What the evidence establishes

| Source | Observed result | What it does **not** prove |
| --- | --- | --- |
| [E0 acceptance](e0-acceptance.md) and [support matrix](support-matrix.md) | Eight hosted private-feed profiles with documented local CLI, isolation, provider-emulator and restore evidence | Universal native protocol compliance or real-provider disaster recovery |
| [23 October code/security review](review-2026-10-23.md) | Thirteen identified correctness/security issues reported fixed with regression tests; local Release build, 681 .NET tests passed, 17 skipped; Python 148 passed, 6 skipped | All CI/provider cases green at every future commit, full security audit or zero remaining vulnerabilities |
| [Container image pipeline](container-images.md) | Main-branch development image publication workflow with exact-commit qualification and anonymous read-back policy | That a particular image is publicly available or automatically deployed without checking the corresponding run |
| [Skill AI setup](skill-ai.md) | Policy-gated optional LLM request and separated worker architecture documented/tested with synthetic inputs | Real model effectiveness, provider billing correctness or a safe-execution certification |

Recorded results belong to the **specific change and environment** named by their source. For the current HEAD, check actual GitHub Actions status and rerun the required scope; older green results do not carry forward automatically. PostgreSQL/Moto tests are not the same as actual cloud-provider recovery.

## Open delivery and release boundaries

| Area | Still required |
| --- | --- |
| **Production release** | Actual database/object-provider restore, upgrades/rollback, measured operations, scoped threat model, independent supply-chain and security evidence |
| **Authentication** | Browser SSO/BFF and interactive session/CSRF design; finer publisher/actor attribution |
| **Distribution** | Proxy/group feeds, private Skills, feed-aware MCP discovery and broader official-client interoperability |
| **Reliability** | No multi-active-writer support, distributed DB/blob transaction, proven failover fencing or general HA |
| **Skills and AI assurance** | Publisher ownership/signature verification, independent scanner performance and real-model/retention assessments |
| **MCP ecosystem** | Full Registry publish/status lifecycle, external execution gateway and broad SDK/client qualification |

These are described and sequenced in [Roadmap V5](roadmap-v5.md) and checked against [release gates](release-gates.md). Additional protocol-specific limitations are in the [support matrix](support-matrix.md).

## Deployment guidance

| Category | Assessment |
| --- | --- |
| Works in principle | Local Docker Compose and selected hosted/client scenarios have documented success in their stated profiles |
| Officially supported | Only combinations explicitly admitted to Packory's maintained support matrix; no upstream vendor certification |
| Best practice | Stay with one writer, pin published container digests, test exact native clients and restore consistently |
| Security recommendation | TLS reverse proxy, non-public administrative endpoints, least-privilege credentials, constrained upstream egress and independently verified backups |

**Bottom line:** Packory has substantive implemented functionality, but its remaining security, compatibility and recovery gates matter. Do not deploy this development baseline as a generally production-ready multi-tenant registry.

For changes to this page, preserve the checkpoint date and update the referenced evidence; a newer calendar date alone is not validation.

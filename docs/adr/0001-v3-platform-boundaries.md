# ADR-001 — V3 reference architecture and module boundaries

**Status:** Accepted for design and incremental implementation; individual features retain their own release gates. **Date:** 2026-10-17.

## Decision

Keep the modular .NET 10 monolith. Protocol-specific NuGet, npm, Cargo, PyPI, OCI and Skills adapters expose native URLs and wire contracts, not a forced universal registry protocol. Introduce a shared versioned control plane gradually: `Feed`, `Namespace`, `Policy`, `Upstream`, `Artifact`, `ArtifactRevision`, `Blob` and `Origin`. Artifact **kind** is independent of **distribution protocol** (for example, an MCP server may be distributed via an npm or OCI package). A first iteration supports `hosted` and `proxy`; `group` requires separate deterministic conflict and namespace rules.

Existing `IPackageDatabase` and `BlobStore` are implementation seams, **not** already a V3 tenant/feed-isolated domain model. No automatic V2 code transplant from historical PR #8. Schema changes require sequential version identifiers, SQLite and PostgreSQL migration tests, old-client default-feed compatibility and a documented rollback. Package byte keys must be retained until independently verified references are migrated.

## Module contract

- Native adapters: parse/validate client protocols, resolve feed + scoped authorization, invoke shared publication services, own protocol-specific metadata, do not choose arbitrary upstream URLs from client input.
- Control plane: authoritative feed/namespace/policy lifecycle, pagination, idempotent APIs, audit, identity mapping, and managed tokens.
- Storage: immutable bytes, digest verification, staging, durable metadata intent, controlled recovery; **no** claim of distributed ACID between PostgreSQL and S3.
- Operations: readiness, retention, integrity, migrations, backup/restore, bounded telemetry and release evidence.
- AI catalog: metadata provenance and approvals only, never execute untrusted uploaded content.

## Rationale and consequences

Native semantics preserve compatibility while shared feed metadata enables isolation. A monolith avoids premature orchestration, but shared resource saturation requires measurement and guardrails. Tenant separation and row-level auth must be tested before claiming multi-tenancy. Static token/OIDC resource-server access remains usable while an optional PKCE BFF is developed. Decisions about TypeScript framework, a production S3 provider, supported client versions and eventual multi-node deployment remain open and must be resolved **before** claiming their support level.

## Alternatives rejected/deferred

A GitForge/fork as the product core is too broad; a universal wire protocol breaks native clients; a mandatory Keycloak/Kubernetes deployment burdens local installations; generic URL fetching or executing Skills introduces unacceptable SSRF/code-execution risk. Active-active writes and microservice split are deferred until measured need, rigorous fencing and recovery.

Related: [roadmap](../roadmap.md), [support](../support-matrix.md), [release gates](../release-gates.md).

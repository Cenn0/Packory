# Packory implementation checkpoint — 20 October 2026

**State: PARTIAL; not production-qualified.** This separates merged code, scoped CI acceptance and unresolved release criteria. It updates the feature inventory of the [18 October checkpoint](roadmap-status-2026-10-18.md) but does **not** close any operator-only recovery gates.

## Confirmed merged delivery

| Delivery | Merged PR | Concrete change | What remains unproven |
| --- | --- | --- | --- |
| E1 Raw/Generic and Helm OCI | [#90](https://github.com/Cenn0/Packory/pull/90) | Hosted immutable Raw bytes and pinned native Helm OCI smoke | Feed isolation, more clients, real-provider restoration |
| E2 Maven parser | [#92](https://github.com/Cenn0/Packory/pull/92) | Release-coordinate safety groundwork | Native publishing |
| E2 Maven files | [#93](https://github.com/Cenn0/Packory/pull/93) | Global immutable hosted release assets, V6 metadata/recovery, SHA-256 and native Maven consumer with transitive dependency | Maven deploy, SNAPSHOT, private feeds, remote proxy, Gradle and provider conformance |
| E3 Go coordinates | [#94](https://github.com/Cenn0/Packory/pull/94) | Canonical paths, escaped wire names and bounded version parser with negative xUnit coverage | No HTTP GOPROXY route, stored modules or native Go client support |
| CI resilience | [#95](https://github.com/Cenn0/Packory/pull/95) | Bounded retry for recognizable transient Maven Central bootstrap errors | No claim of eliminating all external-service flakiness |

## E3 ZIP archive-inspection continuation

This change introduces a **bounded offline ZIP preflight plus synthetic tests**. It checks ZIP module/version prefix, duplicate and case-fold collisions for its ASCII-only subset, traversal, symlinks, resource limits and root go.mod module identity. It computes SHA-256 of ZIP bytes; this is **not** Go's h1: checksum or a checksum database verification.

It does not register endpoints, alter database schema, introduce public network fetches, promise durable publishing or certify native Go clients. See [Go groundwork](go-proxy.md) for deliberate constraints. Test results count as evidence **only if checks succeed on the exact changed commit**.

## MS5 / E10 MCP functional continuation

The [MCP delivery roadmap](mcp-roadmap.md) records delivered behavior and the remaining protocol, Registry, assessment and gateway work separately. The following functions extend the implementation; they do not close all of MS5 or E10 and are separate from the merged-PR inventory above.

| Delivered function | Concrete behavior | Reproducible verification |
| --- | --- | --- |
| Native Packory MCP | Protocol revisions 2025-06-18, 2025-11-25 and 2026-07-28; legacy initialize and current stateless discovery; strict errors, declared capabilities and exact-byte resources | `McpProtocolTests`, `McpOAuthMetadataTests` and existing native C# SDK integration |
| Global catalog and Skill pagination | Opaque cursors for global artifacts, exact versions, Skills and directory children; Maven/Raw included; approval predicate before page limits; private feed records excluded | `McpArtifactCatalogTests`, `McpProtocolTests`; SQLite and actual PostgreSQL profiles |
| MCP Registry read API | `GET /v0.1/servers`, `GET /v0.1/servers/{serverName}/versions`, `GET /v0.1/servers/{serverName}/versions/{version}`; reviewed remote manifests, exact/latest, bounded pages and filter validation | `McpRegistryTests`, `McpRegistryPostgresProviderTests` and native MCP discovery |
| Static Skill quality | `assess_skill_quality` plus REST and dashboard; frontmatter, instruction and local-reference findings, concrete suggestions, source digests and explicit coverage/incomplete results | `SkillQualityAssessmentTests`: positive/negative fixtures, cancellation, revocation/tampering, auth/rate limits and MCP/REST parity |
| Dashboard continuity | Registry pages, published-Skill quality inspection, safe text rendering and invalidation after credential or lifecycle changes | [Browser regression checks](../scripts/test-mcp-dashboard.cjs) and dashboard contract tests |

The existing staged Skill scan/review/approval/revocation workflow is implemented. [Static Skill quality](skill-quality.md) complements that workflow without changing approval or executing submitted code. A completed assessment can contain defects, and incomplete coverage remains visible. Local approval, safety scanning, source integrity, publisher verification and behavioral correctness are separate claims.

Remaining work has explicit acceptance criteria: **MCP-R2–R6** cover broader manifests, publisher/namespace proof, signature verification, upstream synchronization and full publish/status lifecycle; **MCP-P4–P7** cover independent SDK/client qualification, per-feed discovery, justified optional protocol features and bounded Skill-list database work. **MCP-Q2–Q8** cover durable typed assessment history, supported agent/connector contracts, isolated functional/behavior tests and reviewed improvements. **MCP-G1–G5** define the optional isolated external gateway. None is implied by the delivered v0.1 read profile or static quality checks.

The MCP PostgreSQL-specific tests require `PACKORY_TEST_POSTGRES` and explicitly skip without a configured test database. Cite a successful configured run for PostgreSQL evidence; a skipped test or a green SQLite run does not establish provider acceptance. Actual-provider recovery and release gates remain open.

The configured full-suite run also exposed a pre-existing PostgreSQL feed-list error: its null first-page cursor lacked a parameter type. The cursor is now explicitly typed as text, and the schema regression uses isolated test data to verify first-page, continuation and end-of-list behavior. PostgreSQL CI runs the full suite with provider checks enabled so schema and MCP tests cannot be omitted by a class-name filter.

## Priority-ordered unresolved gates

1. **E0 / MS2 — feed isolation:** common control-plane architecture with per-protocol policies. Verify per-feed same-ID collision and exact 401/403/404 behavior, including anonymous and revoked credentials.
2. **E3 Go publication:** stage .info/.mod/.zip with atomic visibility or incomplete state, durable intents and restart/fault matrix for SQLite/filesystem and PostgreSQL/S3.
3. **E2 Maven native deployment:** Gradle and Maven deploy conformance, staged POM+JAR visibility, snapshot metadata policy and security validation.
4. **MS1/MS6 actual-provider recovery:** coordinated PostgreSQL/S3 stopped-writer snapshots, isolated restore, privately measured RPO/RTO, S3 provider preconditions/retention/quarantine, load and monitoring. [Release gate #36](https://github.com/Cenn0/Packory/issues/36) stays open.
5. **MS4/MS5 / E10:** browser OIDC BFF/CSRF and per-resource authorization/audited denials; publisher proof/signatures, independently reviewed scan policy, broader Registry manifests and synchronization, independent client qualification, durable assessment history and isolated behavior tests. Skill approval, the scoped Registry read API and static quality reports are delivered; follow the [MCP roadmap](mcp-roadmap.md) for the remaining criteria.
6. **E11 release:** reproduce SBOM, license checks, release signatures, accessibility, security negatives and DR evidence at the released commit before any production-readiness claim.

**Evidence levels:** *implemented* = code exists; *tested* = named tests passed on a pinned commit; *Packory-supported* = documented client/operation/provider qualification; *production recommended* = security, recovery and operational gates satisfied. None imply upstream vendor endorsement.

**Public source privacy:** no personal identities, private email addresses, secrets, internal URLs, private projects or production logs in code, docs, PRs or CI artifacts; test fixtures use example domains and noreply Git identities.

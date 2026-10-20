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

## Priority-ordered unresolved gates

1. **E0 / MS2 — feed isolation:** common control-plane architecture with per-protocol policies. Verify per-feed same-ID collision and exact 401/403/404 behavior, including anonymous and revoked credentials.
2. **E3 Go publication:** stage .info/.mod/.zip with atomic visibility or incomplete state, durable intents and restart/fault matrix for SQLite/filesystem and PostgreSQL/S3.
3. **E2 Maven native deployment:** Gradle and Maven deploy conformance, staged POM+JAR visibility, snapshot metadata policy and security validation.
4. **MS1/MS6 actual-provider recovery:** coordinated PostgreSQL/S3 stopped-writer snapshots, isolated restore, privately measured RPO/RTO, S3 provider preconditions/retention/quarantine, load and monitoring. [Release gate #36](https://github.com/Cenn0/Packory/issues/36) stays open.
5. **MS4/MS5:** browser OIDC BFF/CSRF and per-resource authorization/audited denials; signed provenance and independently reviewed skill scanning policy.
6. **E11 release:** reproduce SBOM, license checks, release signatures, accessibility, security negatives and DR evidence at the released commit before any production-readiness claim.

**Evidence levels:** *implemented* = code exists; *tested* = named tests passed on a pinned commit; *Packory-supported* = documented client/operation/provider qualification; *production recommended* = security, recovery and operational gates satisfied. None imply upstream vendor endorsement.

**Public source privacy:** no personal identities, private email addresses, secrets, internal URLs, private projects or production logs in code, docs, PRs or CI artifacts; test fixtures use example domains and noreply Git identities.

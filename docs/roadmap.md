# Packory roadmap

The objective is **one self-hosted registry platform for heterogeneous developer and AI artifacts**, without turning it into a Git forge. Sequence work by actual native-client compatibility, security, and maintainability rather than adding microservices prematurely.

## Milestone 0 — First MVP (this repository)

- [x] .NET 10 modular monolith, Docker runtime, SQLite + filesystem storage
- [x] NuGet v3 service discovery, flat-container pull-through, package hash and `.nuspec`, registration rewrite, basic search and hosted push
- [x] npm packument proxy, rewritten cached tarballs, basic scoped packages and single-version publishing
- [x] Unified indexed/hosted catalog, simple dashboard, health endpoints
- [x] Versioned Agent Skills ZIP import/download and basic manifest validation
- [x] Static read/write/admin API tokens and safe-by-default write access
- [x] Automated integration/unit tests, build + container CI, opt-in live NuGet restore smoke test
- [x] README, API/architecture/operations/security/contribution docs

**MVP exit criteria:** CI green, deterministic protocol tests green, a real `dotnet restore` via Packory from nuget.org and a real npm install through registry.npmjs.org succeed, hosted NuGet/npm publish works in tested client versions, and the deployment can be recovered from backup. **An implementation checked into main is not proof that all those runtime acceptance criteria are met**; live tests and client-version matrix must be completed before production use.

## Milestone 1 — Hardening and client compatibility

- [ ] Test and fix `dotnet restore`, `dotnet nuget push`, `npm install`, `npm publish` using real CLIs (including scoped npm, transitive dependencies, SemVer, edge cases)
- [ ] NuGet complete registration semantics, search fallback, version normalization, package types, content digests, controlled redirects/retries and structured errors
- [ ] npm packument/attachments/metadata fidelity; local dist-tags and scoped auth; no unauthenticated upstream bypass
- [ ] Atomic metadata/artifact publication with startup reconciliation and per-artifact concurrency locks
- [ ] Structured observability, audit events, rate limiting, quotas, cache expiry and eviction
- [ ] Artifact integrity verification, security scanning, package allow/deny policy, legal compliance workflow
- [ ] Open-source compliance review, release automation, security CI (CodeQL/dependency review)

## Milestone 2 — Operational and governance features

- [ ] Token lifecycle, scoped permissions, users/OIDC, write restrictions per namespace/package
- [ ] PostgreSQL backend; S3-compatible object storage; restore/migration tooling
- [ ] Registry metrics, dashboard administration, pagination, full-text search, reporting
- [ ] Strong Agent Skills conformance, signature/provenance support and vetted import sources
- [ ] Policy for npm upstream metadata freshness, mirror pinning and offline determinism

## Milestone 3 — More ecosystems

- [ ] Cargo registry (index protocol + crate downloads + checksum and client tests)
- [ ] PyPI simple API (PEP 503/691, upload semantics)
- [ ] OCI artifacts (container layers, Helm, agent bundles), optional other package formats
- [ ] Artifact relationships, namespace conflict rules, federated search

## Milestone 4 — Scale only when justified

- [ ] Background sync/GC workers, queue/outbox if throughput measurements require them
- [ ] Multi-node coordination, horizontal scaling, durable object storage, SLOs
- [ ] Optional service decomposition of independent registry adapters if operational benefit is demonstrated

### Non-goals

Git hosting, issue tracking, CI runner replacement, generic file sharing, arbitrary URL proxying, running uploaded scripts, or deploying a mandatory Kubernetes/microservice stack.

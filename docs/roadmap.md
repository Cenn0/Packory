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

- [ ] Test and fix `dotnet restore`, `dotnet nuget push`, `npm install`, `npm publish` using real CLIs (deterministic hosted publish/restore for one NuGet package and one scoped npm package, including `npm ci`, now in pull-request CI; broader native client versions, transitive dependencies, SemVer/edge cases and live upstream acceptance are still open)
- [ ] NuGet complete registration semantics, search fallback, version normalization, package types, content digests, controlled redirects/retries and structured errors
- [ ] npm packument/attachments/metadata fidelity; local dist-tags and scoped auth; no unauthenticated upstream bypass
- [ ] Atomic metadata/artifact publication with startup reconciliation and per-artifact concurrency locks (in progress; in-process key locks, atomic SQLite skill+catalog publication, quarantine of orphan blobs and missing-hosted detection implemented; full crash-injection verification and CAS publication outstanding)
- [ ] Structured observability, audit events, rate limiting, quotas, cache expiry and eviction (in progress: transactional audit of successful hosted publishing and role-partitioned write rate limits implemented; other actions, retention, metrics, quotas and eviction outstanding)
- [ ] Artifact integrity verification, security scanning, package allow/deny policy, legal compliance workflow (strict upstream NuGet checksum enforcement and NuGet/npm namespace-deny policy implemented; full security scanning and license compliance require external scanners, policies and evidence)
- [ ] Open-source compliance review, release automation, security CI (CodeQL/dependency review)

### Native client acceptance additions

- [x] Isolated hosted Cargo alternate registry publish and consumption via real Cargo CLI.
- [x] Real pip install of a hosted wheel and its hosted transitive dependency via the PyPI Simple API, without a second package index.
- [x] A dedicated CI smoke matrix repeats the hosted Cargo/pip scenarios with Python 3.12 and 3.13 on the current Linux-hosted Rust toolchain. This checks these two interpreter contexts, not broad Cargo/Twine/multi-OS compatibility.
- [x] One isolated native Docker CLI login/push/tag-discovery/pull against Packory OCI (local scratch image, no upstream image).
- [ ] A broader OCI / Helm / ORAS client matrix, real public registry pull-through and a broad package-version matrix are still outstanding.

### Milestone 1 delivery: consistency and namespace isolation

- [x] Single-flight NuGet and npm artifact fetches and serialized hosted publishing by immutable artifact key (single process, bounded stripe pool).
- [x] Reserved NuGet prefixes and npm scopes with explicit no-public-upstream-fallback behavior.
- [x] One SQLite transaction for Agent Skill metadata and catalog entry; no mirrored overwrite of hosted catalog origin.
- [x] Pre-start orphan quarantine and degraded readiness for missing hosted blobs; quarantine is non-destructive.
- [x] Deterministic concurrency, unsigned npm payload, reserved namespace and crash-orphan regression tests.
- [x] SQLite audit records committed atomically with hosted NuGet/npm/Skill metadata; read access restricted to the admin token.
- [x] Configurable per-role publish rate limiter (429 on exhaustion, no unbounded partitions created from user-supplied credentials).
- [x] Live readiness recalculates missing hosted blobs, fail-closed mirrored NuGet checksum validation and namespace-deny policies for NuGet/npm.
- [x] Real CLI hosted acceptance: `dotnet pack` → `dotnet nuget push` → isolated `dotnet restore/build`; `npm publish` → isolated `npm install` and fresh-cache `npm ci`, including scoped package names, without public upstream fallback.
- [ ] Per-token identity, lifecycle, immutable full action audit, storage quotas and cache eviction remain pending.
- [ ] Multi-process transactional publication, content-addressed asset references and full crash-point testing; **not yet supported**.

### Keycloak acceptance

- [x] Disposable real-Keycloak HTTPS discovery, audience-mapped signed JWT and read/write/admin permission checks in dedicated CI.
- [ ] Interactive dashboard authorization-code/PKCE login with a CSRF-protected server-side BFF session and lifecycle tests remains outstanding.

## Milestone 2 — Operational and governance features

- [ ] Token lifecycle, scoped permissions, users/OIDC, write restrictions per namespace/package (optional OIDC JWT resource-server auth and explicit read/write/admin scopes implemented; browser BFF, identity lifecycle, native scopes by package and live Keycloak tests outstanding)
- [ ] PostgreSQL backend; S3-compatible object storage; restore/migration tooling (full PostgreSQL metadata provider with isolated CI integration tests implemented; S3 conditional immutable object backing with local cache and Moto CI implemented; SQLite migration, cross-store transactional outbox and coordinated backups still open)
- [ ] Registry metrics, dashboard administration, pagination, full-text search, reporting
- [ ] Strong Agent Skills conformance, signature/provenance support and vetted import sources
- [ ] Policy for npm upstream metadata freshness, mirror pinning and offline determinism

## Milestone 3 — More ecosystems

- [ ] Cargo registry (hosted sparse index, checksumed crate downloads, native binary upload and API tests implemented; crates.io proxy, yanking and cross-version real client matrix still open)
- [ ] PyPI simple API (hosted HTML/JSON simple API, basic legacy upload, SHA-256 links and API tests implemented; additional real pip/Twine client versions, metadata sidecars, requires-python and PyPI proxy still open)
- [ ] OCI artifacts (hosted v2 content-addressed blob upload/download, manifests, tags and referrers implemented; real Docker/ORAS/Helm tests, Bearer-token workflow, durable multipart uploads and full conformance remain open)
- [ ] Artifact relationships, namespace conflict rules, federated search

## Milestone 4 — Scale only when justified

- [ ] Background sync/GC workers, queue/outbox if throughput measurements require them
- [ ] Multi-node coordination, horizontal scaling, durable object storage, SLOs
- [ ] Optional service decomposition of independent registry adapters if operational benefit is demonstrated

### Non-goals

Git hosting, issue tracking, CI runner replacement, generic file sharing, arbitrary URL proxying, running uploaded scripts, or deploying a mandatory Kubernetes/microservice stack.

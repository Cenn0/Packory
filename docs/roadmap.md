# Packory roadmap V3 — evidence-based delivery plan

**Baseline:** 17 October 2026; code review against [main at 5f0c6c44d0fba1de0ce41b292f06436aa5801687](https://github.com/Cenn0/Packory/commit/5f0c6c44d0fba1de0ce41b292f06436aa5801687). This is a delivery plan, not a statement that future features have shipped. The [support matrix](support-matrix.md) and [release gates](release-gates.md) are normative for support claims. The [18 October implementation checkpoint](roadmap-status-2026-10-18.md) and [20 October continuation](roadmap-status-2026-10-20.md) record newer work and remaining gates; do not treat this historical baseline as the current implementation inventory. Current MCP functions and follow-up acceptance criteria are maintained in the [MCP delivery roadmap](mcp-roadmap.md).

## Product scope and architecture

Packory is a self-hosted distribution hub for NuGet, npm, Cargo, PyPI, OCI, Agent Skills, MCP registry metadata and eventually vetted AI model artifacts. It is **not** a Git forge, CI server, general URL proxy or execution engine. The reference runtime stays a **modular ASP.NET Core/.NET 10 monolith**; do not introduce microservices or multiple active writers without measured operational justification. Protocol adapters preserve their native wire contracts while sharing identity, policy, feed/catalog metadata and blob abstractions.

Invariant: immutable artifact bytes and authoritative metadata visibility. PostgreSQL and S3 are **not** one ACID transaction; every publish boundary requires a durable intent and controlled recovery. Keycloak/OIDC is optional. Browsers require a future server-side BFF, not long-lived provider tokens in localStorage. AI content is never executed by the Packory process.

**Public privacy policy:** do not publish real names, personal e-mail addresses, credentials, internal URLs, operational logs or private production data anywhere in source, Git metadata, documentation, issues or CI artifacts. Keep examples synthetic.

## Evidence levels

| Label | Mandatory evidence |
| --- | --- |
| Implemented | Merged source exists; runtime behavior not implied. |
| Tested | Versioned reproducible test of the specific operation and deployment context passes. |
| Supported by Packory | Published client/provider/operation matrix, positive and negative tests, documented configuration, upgrade/rollback acceptance. This is **not** certification from a client vendor. |
| Recommended for production | Security review, tested backup/restore on the actual provider, operator handbook, incident drills and measured reliability/SLO evidence. |

No CI green check by itself establishes the fourth level.

## Baseline inventory (17 October 2026)

| Subsystem | Implemented at baseline | Open limitations |
| --- | --- | --- |
| NuGet | Hosted push, basic V3, upstream read-through, native CLI smoke | Registration/SemVer and failure/client matrix |
| npm | Hosted publish, mirror, native `npm publish/install/ci` | Packument/dist-tag/auth edge cases |
| Cargo | Hosted sparse registry, publish/consume with native Cargo | No crates.io proxy or yank/unyank |
| PyPI | Hosted Simple API HTML/JSON, upload, pip smoke | No upstream proxy, sidecars/Twine matrix |
| OCI | Hosted blob/manifest/tag/referrer API, Docker smoke | Local upload sessions, no standard Bearer grant server, limited clients |
| Agent Skills | Validated ZIP import/list/download | Provenance, review and approval absent |
| Persistence | SQLite/filesystem; optional PostgreSQL/S3, intent journal and quarantine | No proven coordinated real-provider restore |
| Authentication | Static/hashed revocable scoped tokens; optional OIDC JWT/Keycloak tests | No interactive BFF or complete policy audit |
| Topology | Exclusive PostgreSQL writer, restricted readers | No partition fencing or active-active guarantees |
| UI | Static catalog/skill importer | No feed, policy, identity or operations administration |
| Raw/MCP/models | Architecture concepts | Not implemented in main runtime |

PRs [#25–#35](https://github.com/Cenn0/Packory/pulls?q=is%3Apr+is%3Amerged) already contributed offline backup, quotas, scoped tokens, native smoke tests, writer lease, read topology and strict preflight. Do not reimplement them from scratch. The historical [V2 PR #8](https://github.com/Cenn0/Packory/pull/8) was **not merged**; selectively review its feed/CAS/control-plane/Raw/MCP concepts, never mass-merge it. [Issue #36](https://github.com/Cenn0/Packory/issues/36) tracks deployment acceptance blockers.

## Delivery gates and acceptance matrix

Priority P0 = data-loss/security/release blocking; P1 = core platform; P2 = extension; P3 = conditional scaling. The acceptance tests below apply to each feature independently, not merely the milestone heading.

### MS0 — Consolidation and engineering controls (P0)

| ID | Work and owner-independent acceptance | Evidence |
| --- | --- | --- |
| M0.1 | Replace contradictory historical roadmap claims with this status-driven backlog; verify PRs #25–#35 and issue #36 | Main diff + PR review |
| M0.2 | Reconcile architecture/S3/OIDC/OCI/API docs to actual code, remove misleading support claims | Source comparison + doc link check |
| M0.3 | Compare obsolete V2 design with current ports; record reuse decisions instead of cherry-picking unreviewed code | ADR + gap review |
| M0.4 | Define module boundaries and explicit SQLite/PostgreSQL schema migrations with reversible rollout | ADR + migration tests |
| M0.5 | Publish version-specific protocol/client/provider and known-gap matrix | Native CI results + version captures |
| M0.6 | Gate PRs on build, unit, integration, native clients, providers and public-data/privacy check; document branch rules | Required checks, green per PR |

**Exit:** docs no longer contradict main; every new feature has acceptance and test requirements; required CI green.

### MS1 — Verified integrity and disaster recovery (P0; production blocker)

| ID | Work and acceptance | Evidence |
| --- | --- | --- |
| M1.1 | Coordinated PostgreSQL/S3 snapshots including object versions, quarantine retention, metadata watermark; provide isolated restore tooling | Reproducible offline backup and restore scripts |
| M1.2 | Restore against the **selected real S3 service** in isolated infrastructure; verify digests and native reads for all six artifact families | Signed-off RPO/RTO and inventory report |
| M1.3 | Inject process death, timeout, retry and network abort before/after blob PUT, metadata commit, journal write and quarantine; retain inconsistent records and fail closed | Deterministic fault matrix, negative tests |
| M1.4 | Certify provider's conditional immutable PUT, ETag conditional DELETE, concurrency, object versioning, retention and lifecycle handling | Real-provider run; Moto is **not** certification |
| M1.5 | Migrate SQLite/filesystem to PostgreSQL/S3 from an unchanged source, validate every hash, exercise rollback | Clean staging migration and rollback |
| M1.6 | Verify stored references across NuGet/npm/Cargo/PyPI/OCI/Skills; quarantine retention; GC only after proven reachability | Integrity job + corrupted/missing cases |

**Exit:** all provider-specific tests and actual restore pass. No active-active write release before MS1.

### MS2 — Unified artifact/feed control plane (P0/P1)

| ID | Work and acceptance | Evidence |
| --- | --- | --- |
| M2.1 | Version `Artifact`, `ArtifactRevision`, `Blob`, `Feed`, `Upstream`, `Namespace`, `Origin`, `Policy`; separate artifact kind from native distribution format | Domain and migration tests |
| M2.2 | Explicit hosted/proxy feeds; defer group feeds until conflict priority can be proven; unique native URLs | Two same-kind isolated feeds |
| M2.3 | Scope authorization, retention, quotas, egress and audit to feed; reject cross-feed references | 401/403/404 matrices and SSRF tests |
| M2.4 | CAS, digest validation and staging; preserve old physical keys until verified migration; do not GC referenced bytes | Crash/reconciliation and reachability tests |
| M2.5 | Stable versioned CRUD API, pagination/filtering, declarative import/export and idempotent mutations | API/OpenAPI and replay tests |
| M2.6 | Introduce explicit default feeds without breaking legacy global URLs; document deprecation and rollback | Fresh + migrated DB and native client tests |
| M2.7 | Reserve private namespaces; hosted/proxy resolution cannot silently fall back to public package names | Dependency-confusion and isolation tests |

**Exit:** identical package names can coexist in two independently authorized feeds without collision. Existing client paths work during a documented compatibility period. MS1 gates any migration of production data.

### MS3 — Native protocol conformance (P1)

| ID | Acceptance workload | Evidence |
| --- | --- | --- |
| M3.1 NuGet | Service index, SemVer2, registration, search, checksums, transitive restore/push, cold/warm/offline and upstream failures | Versioned `dotnet` matrix |
| M3.2 npm | Full packument, scoped names, dist-tags, auth, tarball digests, lockfile-pinned `npm ci`, namespace conflicts | Versioned npm matrix |
| M3.3 Cargo | Yank/unyank, sparse index coherency, native API; add crates.io proxy only after integrity acceptance | Multi-version Cargo matrix |
| M3.4 PyPI | Multiple wheels/sdist per version, Requires-Python, metadata sidecars, yanked, Twine, safe proxy without `extra-index` fallback | pip/Twine matrix |
| M3.5 OCI | Standard Bearer challenge/scopes, resumable sessions across restart, chunk/range/redirect, large blobs and referrers | Docker + ORAS + Helm individually |
| M3.6 Harness | Shared cold/warm/offline, negative, hash, concurrency, timeout and upstream failure cases | Reusable reproducible scripts |

**Exit:** advertise only tested client versions, provider combinations and operations; no protocol-wide blanket certification.

### MS4 — Identity, governance and dashboard (P1)

| ID | Work and acceptance | Evidence |
| --- | --- | --- |
| M4.1 | Authorization Code + PKCE with server-side BFF, CSRF, session rotation, logout, key rotation and secure cookies | Browser/E2E security cases |
| M4.2 | Feed/namespace Read, Publish, Manage and Admin scopes; group claims; documented local break-glass; retain existing managed-token lifecycle | Authorization matrix + token revocation |
| M4.3 | Redacted action/denial/recovery/quarantine audit with explicit retention and tamper-evident export | Tamper/PII tests |
| M4.4 | Responsive accessible TypeScript admin app for feeds, explorer, policies, credentials, jobs, quarantine, status, client setup | Browser E2E + accessibility |
| M4.5 | Versioned API + E2E tests for login, CSRF, role changes, feed isolation, upload, failure | CI browser matrix |
| M4.6 | Atomic per-feed capacity reservation for filesystem and S3, not just local filesystem quota | Concurrency tests and accounting |

**Exit:** administer a feed via UI; limited native publisher, instant revoke, audit and no cross-feed access or persistent browser token storage. Keycloak remains optional.

### MS5 — AI distribution, Skills and MCP (P1/P2)

The historical NVIDIA SkillSpector scanning, quarantine, release-gate and web UI plan is maintained in [Skill Security Roadmap](skill-security-roadmap.md). Its SS-001–SS-017 tasks refine M5.1/M5.6 and depend on the browser identity gates in M4.1/M4.2 for privileged UI actions. The [MCP delivery roadmap](mcp-roadmap.md) is the current actionable continuation for platform MCP, Registry metadata, independent quality assessment and the optional gateway.

**Implementation checkpoint — 20 October 2026: MS5 is partially delivered.** Skill approval is implemented; the historical baseline above must not be read as its current status.

| Workstream | Delivered functional profile | Remaining acceptance |
| --- | --- | --- |
| M5.1 / M5.6 Skills | Immutable staged revisions, offline static scanning, explicit review/approval and revocation; [static quality findings and suggestions](skill-quality.md) with source digests and coverage through MCP/REST/UI | Pinned origin and publisher proof, verified signatures, broader evaluator qualification and separate agent/connector behavior tests |
| M5.2 Registry | Remote `server.json` review/revoke plus all three v0.1 read routes, exact/latest versions and cursor pagination | Broader manifests, ownership/signatures, synchronization and full official publish/status lifecycle: MCP-R2–R6 |
| M5.3 / E10 platform MCP | Read-only native MCP/Skills Extension; paginated global artifacts and exact versions including Maven/Raw, Skill/directory traversal and authorization/approval negatives | Broader independent SDK/client conformance, per-feed discovery and justified optional capabilities: MCP-P4–P7 |
| M5.7 gateway | Separate design and acceptance backlog; no external MCP execution | Isolated connections, credential and tool policy, network controls and native runtime acceptance: MCP-G1–G5 |

Security findings, static documentation quality, local distribution approval, byte integrity and behavioral execution remain independent results. The quality profile checks documentation without executing submitted Skills; it does not close the planned MCP-Q2–Q8 work for durable assessment history, agent/connector contracts or isolated functional tests.

| ID | Work and acceptance | Evidence |
| --- | --- | --- |
| M5.1 | Agent Skill spec/archive validation, pinned Git origin, review/quarantine/approval and download profiles | Hostile ZIP, provenance, approval tests |
| M5.2 | Version-pinned `server.json`/MCP Registry API, owner proof, external imports with origin and unverified/verified/approved states | Registry schema + import tests |
| M5.3 | **Separate read-only** Packory MCP service (authorized search/feed/artifact/approval metadata only); no mutation before security gate | Native MCP client + AuthZ tests |
| M5.4 | Immutable generic blobs with MIME, digest, version, policy and controlled signed references; no arbitrary URL proxy | Upload, traversal and authorization tests |
| M5.5 | OCI-backed model catalog: immutable manifests, license and provenance; large-file streaming only after capacity tests | Multi-GB integrity and capacity |
| M5.6 | Publisher identity, signature/attestation, scanning and policy status represented independently from SHA/hash | Tampering and unsupported signature tests |
| M5.7 | Optional remote MCP Gateway only after separate SSRF, token forwarding, tool-permission, injection and sandbox review | Separate threat model and release |

**Exit:** skill and MCP registry entry can be reviewed and discovered **without execution**. Read-only MCP tool never leaks private metadata.

### MS6 — Production hardening and release engineering (P0 for stable release)

| ID | Acceptance requirement | Evidence |
| --- | --- | --- |
| M6.1 | Split issue #36 into deployment-specific measurable blockers; no HA gate for initial single-writer release | Reviewed release checklist |
| M6.2 | SAST, dependencies, license, container, secret scans, SBOM, signed image provenance, pinned base/build images | Verifiable CI artifacts |
| M6.3 | Load/stress/fuzz, slow uploads, client aborts, disk/DB/S3 outages, saturation and backpressure | Reproducible load report |
| M6.4 | OpenTelemetry-compatible traces/metrics with controlled label cardinality; alerts for latency/errors/storage/intents/backups | Alert drill |
| M6.5 | AuthZ, SSRF/DNS rebinding, logging/header redaction, TLS/proxy/OIDC, supply chain, ZIP/archives and private feeds | Independent security review |
| M6.6 | Migrations, runbooks, restore/rollback/upgrade drills and measured deployment-specific RPO/RTO/SLO | Fresh install + upgrade + incident |
| M6.7 | Reproducible signed tagged release and upgrade guide; exact verified support scope | Released artifacts and verification guide |

**Exit:** only a scoped **single-writer** release if all relevant MS1 and MS6 gates pass, plus MS2–MS5 feature-specific gates for what is included.

### MS7 — Optional multi-node/active-active (P3; not committed to release)

| ID | Prerequisite and tests | Evidence |
| --- | --- | --- |
| M7.1 | Remove GET-side writes from NuGet/npm before general readers; invalidate caches and test restart | Multi-node readonly tests |
| M7.2 | Durable cross-node OCI upload sessions with retry and node switch | Failover session test |
| M7.3 | Monotonic fencing epoch/term; advisory lock alone cannot fence partitioned writers | Split-brain/partition test |
| M7.4 | Idempotent publication and unified metadata/object visibility under concurrent writes | Partition+race matrix |
| M7.5 | Compare measured throughput, latency, SLO and operating costs against single writer + readers; only then consider services | Benchmarks and ADR |

**Exit:** no split brain, silently lost data or unproved failover; otherwise stay single writer.

## Dependency graph and PR slicing

`MS0 -> MS1 -> MS6 release gate`; `MS0 -> MS2 -> (MS3, MS4) -> MS5`; `MS7` depends on **MS1 and MS6** and an explicit scalability justification. Native client tests, UI experiments and observability hardening may proceed in parallel, but schema/metadata changes must rerun fault/restore tests.

Recommended reviewable PR series: (1) baseline, support and ADRs; (2) real S3 conformance harness; (3) coordinated snapshot/restore; (4) fault injection; (5) schema migration; (6) feed domain and isolation; (7) default-feed migration; (8) feed CRUD/audit; (9) native harness by adapter; (10) BFF; (11) admin UI; (12) AI metadata/Raw/MCP as separately accepted features; (13) single-writer release gate. Do not merge a red CI PR and do not mark external gates passed based on mocks.

## Release labels are *targets*, not existing releases

- **Technical preview:** existing bounded single-writer functionality, explicitly experimental.
- **v1.0 single-writer reference release:** MS0, MS1 and applicable MS6 gates; only explicitly qualified adapters.
- **v1.x Unified Hub:** MS2, MS4 and accepted MS3 additions.
- **AI Hub:** separately accepted MS5 parts; MCP registry, platform MCP and Gateway have distinct support levels.
- **Distributed release (optional):** only after MS7 operational proof.

Open decisions: first real S3 provider/PG topology; hosted+proxy before any group feed; feed and namespace isolation vs future tenancy; TypeScript UI framework; native client version/OS pins; exact MCP Registry schema; OCI/AI retention; economic justification for active-active.


## Delivery note — MS1.6 inspection slice (18 October 2026)

A maintenance-only, admin-scoped bounded catalog-reference integrity audit
has been implemented with per-family digest comparisons, negative cases,
a real PostgreSQL/S3-emulator authorization check, and direct streamed S3
integrity reads that cannot silently accept a stale local cache. This covers an **inspection
subset**, not M1.6 exit: NuGet lacks durable publication-time digests, remote
S3 object versions/retention and unreferenced objects are not exhaustively
inventoried, and the scan is not an atomic cross-store snapshot. The existing
real-provider restore, migration/rollback and release gates remain open.


## Incremental MS1.6 delivery — publisher-time NuGet digests (18 October 2026)

A V3 SQLite/PostgreSQL schema and immutable NuGet checksum ledger now
record canonical SHA-512 and exact bytes for **newly hosted and
upstream-verified mirrored** revisions. Downloads, the maintenance integrity
scanner, snapshots and isolated database restore use the immutable metadata.
V1/V2 legacy revisions intentionally remain `unverifiable` rather than
receiving invented checksums. This addresses a concrete MS1.6 integrity
gap, **not** full MS1 completion: old releases, real S3 provider conditions,
coordinated cross-store recovery, retention, operator sign-off and RPO/RTO
remain open.


## MS2 hosted-feed delivery note (18 October 2026)

An initial **operational private hosted NuGet feed** adapter has been
implemented, not simply staged metadata: admin-controlled activation
and reversible disable, per-feed version/immutable byte storage,
separate scoped managed/OIDC authorization, strong SHA-512, recovered
publication-intent journal, private listing isolation, a native
dotnet client test and V4 PostgreSQL/S3 snapshot fingerprints.

**MS2 remains incomplete:** other ecosystems, proxy/group semantics,
tenant-wide control plane, quotas, cross-feed search policy,
declarative import/export, full migration, compatibility and
independent security/provider acceptance are still open. Do not
describe this first NuGet slice as complete cross-protocol isolation.

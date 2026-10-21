# Packory Roadmap V4 — Native package ecosystem expansion

**Planning baseline:** 19 October 2026; **implementation checkpoint updated:** 21 October 2026. **Status:** E0 hosted implementation present, candidate acceptance pending; other milestones retain the states below. This plan is not a claim that all package ecosystems or V3 milestones are complete.
**Depends on:** [V3 delivery roadmap](roadmap.md), [release gates](release-gates.md), [support matrix](support-matrix.md), real-provider restore qualification and secure feed authorization.

## Outcome and non-negotiable rules

Packory should distribute packages through existing native package managers. A Raw binary stored in Packory is **not** Maven, Go, RubyGems, Composer, Terraform or Debian package-manager compatibility. OCI-backed Helm can reuse the existing OCI transport, but requires independent native Helm acceptance.

1. Preserve the modular .NET 10 monolith and separately versioned protocol adapters.
2. Preserve immutable bytes and durable publishing intents. PostgreSQL and S3 do not form a distributed transaction; every adapter needs reconciliation, restore and digest evidence.
3. Do not claim feed isolation before every route enforces scoped reads and writes. No automatic fallback from private namespaces to public upstreams.
4. Allow outbound requests **only** to administrator-configured, pinned HTTPS origins; never follow unvalidated redirects, accept caller-controlled URLs or forward credentials to upstream hosts.
5. Never execute imported code, scripts, build hooks, packages, agent skills or user-controlled binaries in the Packory API process.
6. No personally identifying information, personal email addresses, secrets, real internal URLs or private-project names in this public repository, issue trackers, actions, images or documentation.
7. Independent evidence tiers: planned → implemented → reproducibly tested → Packory-supported **for a pinned client/operation/provider combination** → production recommended after operational/security gates. None of these imply upstream vendor endorsement.
8. Keep new adapters small, independently reviewable and behind separate capability flags until tested.

## Milestones

| ID | Priority | Scope | Acceptance for the milestone | State at this plan |
| --- | --- | --- | --- | --- |
| E0 | P0 | Shared feed/control-plane isolation, typed coordinates, validated storage/digests, origin policies, cross-protocol test harness | Two same-name package revisions in separate independently authorized feeds, native clients fail closed on 401/403/404, restore/recovery under faults | **Implemented hosted foundation; acceptance pending:** private NuGet/npm/Cargo/PyPI/OCI/Maven/Go/Raw routes, exact grants, typed native identities, schema V10 and immutable storage. Local versioned native checks pass; full candidate CI/provider/fault/restore gates are tracked in [E0 acceptance](e0-acceptance.md). Proxy/group and private Skills/MCP remain separate milestones |
| E1.1 | P0 | Raw/Generic hosted binary, immutable version, SHA-256, metadata, streaming and range GET | Auth, namespace, duplicate, tamper, restart, SQLite/PostgreSQL, S3-emulator, offline reference scan | **PR #90 merged**, experimental; no production/provider qualification |
| E1.2 | P0 | Native Helm 3 through existing OCI transport | Pinned Helm push/pull/show/template against local OCI with private-access negatives | **PR #90 merged**, pinned CLI smoke passed; no universal Helm/Kubernetes conformance |
| E2 | P1 | Maven repository layout shared by Maven and ordinary Gradle dependencies: release POM/JAR/classifiers, snapshots, metadata, hosted and proxy | Native Maven deploy/resolve + Gradle resolve, transitive graph, checksum, private namespace, cold/warm/offline/error suite | **Partial:** parser #92 and global immutable hosted release transport + V6 metadata/recovery + Maven native consume #93 merged; bounded Maven Central CI retry #95 merged; bounded verified checksum PUTs for already committed immutable release artifacts added. E0 adds private hosted release feeds. Native deploy (including mutable metadata PUT), Gradle conformance, SNAPSHOT, atomic multi-file release and proxy remain open |
| E3 | P1 | Go Modules GOPROXY, module escaping, immutable .info/.mod/.zip and canonical versions; validated private ingest + public allowlisted mirror | Native go mod download/build with private and mirrored modules, checksum DB rules and retry/case tests | **Hosted slice merged:** [#97](https://github.com/Cenn0/Packory/pull/97) delivers immutable module publishing, GOPROXY reads and V7 recovery; [#102](https://github.com/Cenn0/Packory/pull/102) adds a canonical `h1:` archive-content hash verified against native Go `go.sum`. The pinned-commit CI passed. Private hosted feed IAM and native Go authentication were added in E0/#109. Mirror, public checksum-database trust, full archive compatibility and provider/DR gates remain open |
| E4 | P1 | RubyGems publish/read-through and Bundler Compact Index | gem push/install and bundle install with transitive deps, negative index/redirect tests | **Not implemented** |
| E5 | P1 | Composer 2 metadata API and immutable distribution archives; hosted ingest and proxy | composer install/update from hosted and mirrored packages without remote credential exfiltration | **Not implemented** |
| E6.1 | P2 | Terraform/OpenTofu module registry protocol | terraform init with hosted module; auth/redirect/version tests | **Not implemented** |
| E6.2 | P2 | Terraform/OpenTofu provider registry protocol, checksums and OS/architecture bundles | terraform init with two verified platforms and separate trust/provenance matrix | **Not implemented** |
| E7 | P2 | Conan 2 recipes, revisions, profile-dependent binary packages, hosted + later proxy | conan create/upload/install with multiple profiles, immutable revisions and hash tests | **Not implemented** |
| E8.1 | P2 | APT Debian repository with suite/component/architecture and signed coherent metadata snapshots | isolated apt update/install; reject bad signature, incomplete index and wrong architecture | **Not implemented** |
| E8.2 | P2 | RPM/DNF repository, signed repodata and architecture/epoch variants | isolated dnf makecache/install and fail-closed signature/checksum tests | **Not implemented** |
| E9 | P3 | Optional Conda, Swift Package Registry, traditional Helm chart repository, Windows feeds | individual ADR, exact official protocol/client research and native proof for each | **Deferred / not implemented** |
| E10 | Cross | Format-aware dashboard, catalog, setup instructions, auth, audited admin APIs, read-only MCP | Show accurate per-client support, forbid scoped metadata leakage, accessible UI/E2E | **Partial:** capability API #90; paginated global MCP artifact/version discovery including Maven/Raw, v0.1 MCP Registry reads and static Skill quality through MCP/REST/UI. Per-feed discovery, privileged browser sessions and broader client/accessibility acceptance remain open; [MCP continuation](mcp-roadmap.md) |
| E11 | Cross | Production and per-adapter acceptance/CI/release | Specific client, OS, provider, operation, negative/fault test evidence; signed release/SBOM/security/DR | **Open** (inherits V3 gates) |

Every milestone is separately gated. Enabling a previously absent row requires merging code and its evidence; merely creating its issue, schema, placeholder endpoint or screenshot does not satisfy the acceptance.

### E10 MCP continuation

The [MCP delivery roadmap](mcp-roadmap.md) connects E10 to V3 MS5. Delivered functions include strict paginated native MCP discovery, approval filtering before catalog page limits, reviewed remote Registry metadata and the [static Skill quality profile](skill-quality.md). Dashboard discovery supports subsequent pages and discards stale responses after credential changes; quality inspection also invalidates results after lifecycle changes. These MCP features do not expand native publishing support; private package adapters are documented independently in the [E0 acceptance profile](e0-acceptance.md).

Publisher proof, signature verification, upstream synchronization and broader Registry manifests remain separate MCP-R tasks. Additional independent SDK/client qualification and per-feed discovery remain MCP-P tasks. Durable quality history, agent/connector contracts and isolated behavior testing remain MCP-Q tasks; connecting to external servers remains the separate MCP-G gateway workstream. Completing a static quality report does not certify functional execution or production readiness.

## Delivery sequence and PR boundaries

**Wave A (first):** consolidate global and feed-local controls; Raw and Helm OCI native tests; publication/restore integrity; explicit conservative capabilities and privacy checks. Never present global Raw as private-feed isolation.

**Wave B:** Maven/Gradle and Go Modules in **separate** PR series: native wire/ADR → schema/typed identities → hosted native consumer → hosted publishing/ingest → allowlisted proxy → authorization and fault matrix → UI/MCP. Do not merge an adapter that invents incompatible repository metadata.

**Wave C:** RubyGems and Composer 2, same review sequence, distinct index formats.

**Wave D:** Terraform modules separately from providers; Conan 2 independently.

**Wave E:** APT and RPM in separate streams because GPG trust, index coherence and signing-key lifecycle are release-critical.

**Wave F:** Conda/Swift/legacy Helm/Windows only after relevance and maintenance costs are evaluated. Avoid promising all formats as a prerequisite for a scoped single-writer release.

## Universal adapter test matrix

For each adapter, collect versioned evidence for:

- **Native usage:** publisher/ingest, installation/restore, transitive dependencies, fresh client caches and upgrades.
- **Integrity:** hash mismatch, tampering, metadata/content mismatch, duplicate releases, concurrent writers, rollback and immutable variants.
- **Auth and isolation:** anonymous, revoked, namespace-only and per-feed credentials; 401/403/404; private/public collisions; scopes in MCP and UI.
- **Network:** TLS, allowlisted upstream DNS/IP resolution, redirects, timeouts, retries, upstream 404/429/503, bounded metadata and stream bodies.
- **Persistence:** SQLite/filesystem and PostgreSQL/S3 emulator, unexpected SIGKILL, pending intent, orphan quarantine, metadata schema upgrades and isolated restore.
- **Operations:** documented routes, backup and per-kind inventory, retention, storage limits, latency/memory load, logs without secrets.
- **Supply chain:** SBOM, third-party licenses, reproducible pinned clients/images, signed and independently verified releases where available.

**No adapter is “production ready” based on synthetic CI alone.** Real S3-provider backup/restore and operator evidence in [release gates](release-gates.md) remain mandatory.

## Sources and correct ecosystem boundaries

- [Apache Maven repository layout](https://maven.apache.org/repository/layout.html) and [repository metadata](https://maven.apache.org/repositories/metadata.html): classifier, extension, SNAPSHOT and metadata are distinct wire constructs.
- [Go module proxy protocol](https://go.dev/ref/mod#module-proxies): case escaping, canonical versions, .info/.mod/.zip and go.sum rules are mandatory.
- [Helm OCI registries](https://helm.sh/docs/topics/registries/): OCI artifacts are Helm-native for accepted Helm clients; classic chart repositories are another protocol.
- [OCI Distribution specification](https://github.com/opencontainers/distribution-spec): tags are mutable references; digests identify immutable bytes.
- [V3 architecture](architecture.md), [current native support matrix](support-matrix.md), [release gates](release-gates.md), [Raw API](raw-registry.md).

## Historical implementation checkpoints through 20 October 2026

- [PR #90](https://github.com/Cenn0/Packory/pull/90): Raw/Generic hosted bytes and pinned Helm-OCI CLI smoke merged; both remain experimental.
- [PR #92](https://github.com/Cenn0/Packory/pull/92): Maven coordinate parser and negative tests merged.
- [PR #93](https://github.com/Cenn0/Packory/pull/93): immutable global Maven release files, schema V6, recovery and a real Maven consumer smoke merged. No native deploy, SNAPSHOT, isolated feed or proxy.
- [PR #94](https://github.com/Cenn0/Packory/pull/94): strict Go GOPROXY coordinate parsing merged, **not** a serving endpoint.
- [PR #95](https://github.com/Cenn0/Packory/pull/95): bounded Maven Central bootstrap retry merged without skipping native package validation.
- Follow-up E3 ZIP preflight: conservative offline archive validation only, no persistence or wire behavior; see [Go design](go-proxy.md) and the [current checkpoint](roadmap-status-2026-10-20.md).
- E10/MS5 MCP continuation: paginated global discovery including Maven/Raw, three v0.1 Registry read routes, strict native MCP/Skills Extension behavior and static Skill quality with dashboard integration. See [delivered functions and open acceptance](mcp-roadmap.md); neither E10 nor MS5 is complete.

**Current continuation:** the hosted Go endpoint, immutable triplet and private feed authorization are implemented. The E0 candidate extends hosted isolation to all eight delivered package kinds and must pass the evidence gates below. E11 real-provider and disaster-recovery qualification remains separate.

## Previous checkpoint: PR #90

[PR #90](https://github.com/Cenn0/Packory/pull/90) is merged after green CI; E1 Raw and bounded Helm OCI are experimental. [PR #92](https://github.com/Cenn0/Packory/pull/92) merged the Maven path parser and safety tests. E2 Maven global release transport followed in PR #93. At that checkpoint, private feed isolation had not yet been added; the E0 hosted implementation below supplies it. Native deploy, proxy and SNAPSHOT remain E2 work.

### E0 hosted foundation — 21 October 2026

The implemented E0 package boundary covers **NuGet, npm, Cargo, PyPI, OCI,
Maven, Go and Raw hosted feeds**. Each adapter uses an explicitly activated
private feed, exact read/publish grants, native coordinate validation and
feed-qualified immutable identity. Every read, metadata response and upload
session stays within its authorized feed; there is no global/upstream fallback.
Legacy routes remain available except that OCI repository prefix `feeds/` is
reserved for `/v2/feeds/{id}/{repository}/` private routing.

Schema V10 retains the NuGet checksum ledger, extends `feed_assets` for the
other hosted package kinds and adds independent OCI tag pointers. npm, Cargo
and PyPI commit protocol metadata plus original package bytes in one verified
immutable bundle; Go commits its immutable triplet. OCI digest bytes remain
immutable while tags may move between verified manifests. Recovery and
snapshot tooling enumerate these same identities.

**Acceptance is pending for the complete candidate.** Local Linux,
SQLite/filesystem native tests have passed with npm 11.9.0, Go 1.24.13,
Cargo/rustc 1.99.1, Python 3.12/pip 26.2.1/Twine 6.2.0 and pinned Helm 3.22.0.
Those runs do not qualify other clients, providers or native operations.
The same npm/Cargo/PyPI/Helm native profiles also passed against actual
PostgreSQL 16 and Moto S3, with independent remote-byte/ledger verification.
That emulator parity result leaves fault/restore and real-provider gates separate.
[E0 acceptance](e0-acceptance.md) maps the original E0 row to concrete
cross-feed, authorization, integrity, restart/fault, migration and restore
criteria, test files, commands and remaining CI gates.

This bounded hosted foundation does not complete MS2 or the wider project.
Proxy/group feed semantics, per-feed quotas/retention, full declarative
control-plane operations, CAS migration and browser permissions remain in
MS2/MS4. Private Skills and feed-aware MCP discovery remain MS5/MCP-P5.
Native `mvn deploy`/SNAPSHOT/Gradle/proxy remain E2; Go mirror and public
checksum trust remain E3. Private npm has publication tags without mutable
dist-tag/unpublish APIs, Cargo has no private yank, and PyPI's canonical
version/archive profile does not qualify arbitrary source builds. These
boundaries are detailed in [feed operations](feeds.md) and the
[support matrix](support-matrix.md). E11 retains deployment/provider recovery
and release qualification.

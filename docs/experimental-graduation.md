# Experimental graduation — implementation and acceptance ledger

**Baseline:** 22 October 2026, following Maven metadata PR #113.
**Scope:** all entries labelled `experimental` in `RegistryCapabilities.All`, plus the separate overall release gate.
**Rule:** implementation, repeatable testing, Packory-supported combinations and production recommendation are four distinct statuses. A CI pass never authorizes a blanket production claim.

## Global prerequisites (P0)

| Workstream | Deliverable | Acceptance | State |
| --- | --- | --- | --- |
| EXP-09 Feed control plane | Explicit hosted/proxy origins, exact per-feed IAM, audited changes, no global fallback | Two private feeds sharing coordinates, read/publish/denial matrix, proxy SSRF and dependency-confusion tests | Hosted E0 accepted; private proxy/group still open |
| EXP-10 Storage and recovery | Durable publish intent, bounded blob/metadata verification, migration/rollback and retention | Kill-point replay, all-kind restore, no referenced-blob deletion | Hosted E0 accepted; real-provider and quota gates open |
| EXP-11 Identity | Browser BFF with PKCE, CSRF, secure server session, audited per-feed roles | Login/logout, revocation, denied mutation and role-change E2E | Open |
| EXP-12 PostgreSQL/S3 | Real-provider conditional writes, coherent offline backup, isolated restore, measured RPO/RTO | Operator-reviewed evidence for selected provider and release commit | Open; emulator evidence exists |
| EXP-15 Conformance | Version-pinned native clients, two OS profiles, corruption/timeout/offline/load cases | Published run identifiers, client versions and exact qualified operations | Partial |
| EXP-16 Release | SBOM, dependency/licenses, signatures/provenance, upgrade and rollback | Independent production release-gate evidence per docs/release-gates.md | Open |

## Experimental adapters

| ID | Existing capability | Implementation still required | Per-adapter exit |
| --- | --- | --- | --- |
| EXP-01 NuGet V3 | Hosted/proxy + private flatcontainer/push + checksums; #114 introduces scoped search and paginated registration | Broad SemVer/registration/client matrix, symbols, unlisting, package advisories, signed-package trust as independently scoped features | Native authenticated discovery, restore/transitive, fault, recovery, symbol/unlist profile if advertised |
| EXP-02 npm | Hosted/proxy, private publishing, immutable tarball bundles | Persisted mutable private dist-tags API, defined unpublish/deprecation policy, scoped proxy, cross-client lockfile matrix and attestations | npm/pnpm/Yarn declared versions, tag concurrency, protected retired versions, verified dependency provenance |
| EXP-03 Cargo | Native hosted alternate sparse registry, yank/unyank added after historical baseline | Verify full yank profile, allowed crate naming, owner grants, allowlisted crates.io mirror with immutable index consistency | Native cargo publish/yank/build/update on cold and warm caches and distinct feeds |
| EXP-04 PyPI | Hosted Simple API HTML/JSON, native Twine/pip | Broader PEP 440, Requires-Python, sidecars, yank, source/wheel compatibility and opt-in trusted proxy | Positive/negative pip/Twine matrix, multiple distributions per release, no extra-index confusion |
| EXP-05 OCI | Private Docker/Helm-style Distribution v2 with durable sessions and referrers | Standard Bearer issuer/scopes, bounded referrer/descriptor conformance, optional secure delete/GC, Docker/ORAS/Helm matrix | OCI spec-selected contract, restart/digest/denial/concurrent tests |
| EXP-06 Maven/Gradle | Native deploy/release/SNAPSHOT and V12 inherited POM + plugin-prefix metadata (#113) | Whole-publication visibility contract, allowlisted Central proxy, broader Gradle metadata variants | Maven/Gradle transitive plugins, snapshots, interrupted deployment and recovery |
| EXP-07 Raw/Generic | Immutable global/private byte transport with ranged reads | Feed quotas/retention, upload policy, scoped UI/MCP discovery and provider qualification | Reusable generic API profile; do not claim native package manager conformance |
| EXP-08 Agent Skills | Immutable ZIP, staged security scan, approval/revoke, MCP/REST quality | Publisher proof, signatures/trust policy, scanner qualification, durable quality history, isolated behavioral tests | Fail-closed release/distribution policy, reproducible scanner safety and separate behavior claims |

**Go note:** Go is `native-client-tested`, not `experimental` in the capability list. The upstream proxy, checksum DB trust and ZIP parity gaps remain in E3 and should not be silently marked supported.

## Parallel follow-up (P1)

- **EXP-13 MCP:** MCP-R2b/R3/R4/R5/R6 official profile and lifecycle; MCP-P4/P5/P7 per-feed auth, SDK interoperability and measured capacity; MCP-Q2–Q8 durable assessment plus isolated execution. Optional gateway MCP-G1–G5 has a separate security boundary and is not an adapter-release prerequisite.
- **EXP-14 Dashboard:** BFF-dependent feed, credential, audit, security and operation management; browser accessibility and E2E coverage.
- **New ecosystem adapters:** RubyGems, Composer, Terraform, Conan, APT and RPM are *unimplemented V4 milestones*, not already implemented experimental functions. Track separately so they do not block completed existing adapter profiles.
- **Active-active writers:** expressly not supported. Do not add as an implicit single-writer release gate.

## PR sequencing and evidence

1. Merge pre-existing passing work (#113 completed). Confirm all checks, review the actual head SHA and do not bypass merge protections.
2. EXP-01 feed-scoped NuGet discovery (#114) with per-feed query, registration, dependency groups and corruption/auth negatives.
3. EXP-02 npm dist-tag transactional lifecycle and tests; EXP-03 Cargo native negative/mirror proof can proceed independently.
4. EXP-04 PyPI metadata semantics; EXP-06 Maven atomic staged-publish design and proxy in separate reviewable PRs.
5. EXP-05 OCI auth and client conformance; EXP-07 Raw policies; EXP-08 Skills publisher/signature and isolated quality runner.
6. EXP-11 BFF before privileged admin UI; then EXP-13/14 MCP/dashboard, followed by real-provider EXP-12 and release EXP-16.

Each merged PR must update `RegistryCapabilities`, `docs/support-matrix.md`, the relevant operations guide and version-pinned test evidence. Mark no adapter `supported` without a declared client, operation, provider and OS set. `productionRecommended` remains false until the operator-only evidence in `docs/release-gates.md` is independently reviewed.

## Mandatory security constraints

The registry must never execute arbitrary submitted package or Skill contents. External HTTP sources are operator-selected HTTPS origins with deny-by-default redirects and DNS/IP policy; incoming credentials are never forwarded to upstream hosts. Privileged browser operations require server sessions and CSRF. Private artifacts may never leak through anonymous catalog/registration/MCP/search paths. For this public repository, commit no names, emails, secrets, internal URLs or unredacted operator evidence.

## References

- [V3 roadmap](roadmap.md)
- [V4 adapter roadmap](roadmap-v4.md)
- [Support matrix](support-matrix.md)
- [Release and disaster-recovery gates](release-gates.md)
- [MCP continuation](mcp-roadmap.md)
- [Skill security workstream](skill-security-roadmap.md)

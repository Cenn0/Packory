# Roadmap V3 — delivery evidence and remaining acceptance (2026-10-17)

**Status: incomplete. No production-ready, unified multi-feed, AI Hub or distributed
release is declared.** This ledger records incremental changes merged into
`main` and separates them from milestone acceptance.

## Merged slices and tested evidence

| PR | Merged change | What the passing checks do **not** prove |
| --- | --- | --- |
| [#49](https://github.com/Cenn0/Packory/pull/49) | PostgreSQL row fingerprints and V2 feed-table verification during offline restore | Real S3 provider and real-site RPO/RTO |
| [#51](https://github.com/Cenn0/Packory/pull/51) | Persisted OCI upload offsets, replay rejection and restart safety | Cross-node session mobility or OCI conformance |
| [#52](https://github.com/Cenn0/Packory/pull/52) | Strict PostgreSQL/S3 snapshot manifest and object-inventory validation | Immutability of an untrusted backup or real-provider certification |
| [#54](https://github.com/Cenn0/Packory/pull/54) | In-flight OCI process termination and disconnect regression | Distributed failover |
| [#55](https://github.com/Cenn0/Packory/pull/55) | Full-string artifact/S3 identifier validation | Whole-API input security audit |
| [#56](https://github.com/Cenn0/Packory/pull/56) | Cargo/PyPI stored-byte digest verification before download | Whole-catalog integrity or comprehensive protocol compliance |
| [#57](https://github.com/Cenn0/Packory/pull/57) | npm cached tarball integrity checked against recorded manifest | Signed provenance, offline provider certification or load capacity |
| [#58](https://github.com/Cenn0/Packory/pull/58) | Opt-in disposable-bucket test of S3 version history and delete markers | Actual provider execution, retention/lifecycle, partition behavior or restore |
| [#53](https://github.com/Cenn0/Packory/pull/53) | NVIDIA SkillSpector/approval/security-UI implementation backlog | Scanner or UI integration: these remain unimplemented |
| [#59](https://github.com/Cenn0/Packory/pull/59) | Skill ZIP decompression/path/UTF-8/frontmatter checks | Malware/prompt-injection detection or read-path approval gates |

The listed PR heads passed the repository's established CI profiles prior to
merge. Evidence is held in each PR's check-runs. A successful PR check is not
a substitute for inspecting the **subsequent main push** after merging other
PRs. No fresh-install, real-provider or release-signature acceptance has
been conducted by this ledger.

## Milestone acceptance — stricter than code presence

| Milestone | Status | Outstanding release-grade acceptance |
| --- | --- | --- |
| MS0 — engineering baseline | Partial | Complete version/OS support matrix, current-source documentation reconciliation, release-specific test matrix |
| MS1 — integrity and restore | Partial | Authoritative digests across all families; isolated restore against selected real S3; retention/lifecycle/quarantine; measured RPO/RTO; source-safe upgrade/rollback |
| MS2 — unified feeds | Not accepted | Independent hosted/proxy feeds for each protocol, namespace/role/quotas, native routing, cross-feed isolation and data migration |
| MS3 — native conformance | Partial | Published client/version/operation matrix, full native negative cases, PyPI proxy, OCI Bearer scopes/ORAS/Helm and compatibility gates |
| MS4 — browser governance | Not accepted | BFF+PKCE, CSRF, scoped IAM/audit, full operations dashboard and browser E2E |
| MS5 — AI Hub and MCP | Not accepted | Stage/scan/approval/revoke for Skills; MCP metadata registry; separate read-only MCP server; raw/model catalog; provenance |
| MS6 — production hardening | Partial | Security review, real-provider/restore/load drills, complete SBOM/licensing/signing, telemetry/SLO, verified release |
| MS7 — multi-node/active-active | Deferred optional | Isolation, fencing, partition/failover/rollback proofs and economically justified deployment model |

## Real-provider gates that CI cannot synthesize

1. Select an authorized reference S3 provider and PostgreSQL profile with
   **non-production** credentials kept outside source control.
2. In a disposable versioned bucket, run the documented
   `scripts/s3_provider_probe.py --disposable-test-bucket --verify-version-history`.
   Capture private operator evidence (not public raw endpoint URLs).
3. Run a coordinated PostgreSQL/S3 backup and **isolated** restore against
   that selected provider, exercise all supported artifact families and compare
   recorded digests and metadata. Measure **actual** RPO/RTO.
4. Repeat failure injection and recover/reconcile at the DB-commit, object PUT,
   quarantine and journal boundaries; record any indeterminate state as a
   release blocker.
5. Perform separately reviewed retention, Object Lock/lifecycle, security,
   incident, upgrade/rollback and load qualification before calling the
   Single-Writer v1 reference topology production-ready.

The S3 probe intentionally reports `full_m1_4_provider_certified=false`.
Object history checks are **additional test capability**, not actual provider
approval. Moto is an emulator, not the qualified deployment provider.

## Trust and public-data constraints

Do not put personal email addresses, production tokens, internal endpoints,
private data or raw operational reports in Git history, PRs, logs, screenshots
or release artifacts. CI privacy scans and public-source review are required
on each PR; they reduce risk but do not prove the absence of all leaks.
Fail closed on unverifiable artifacts instead of recomputing expected
checksums from suspect bytes.

**Gate owner:** [Issue #36](https://github.com/Cenn0/Packory/issues/36)
remains open until its actual acceptance evidence exists. The outstanding
release gates are not canceled by documentation changes or green unit tests.

Related: [Roadmap V3](roadmap.md), [Support matrix](support-matrix.md),
[Release gates](release-gates.md), [npm integrity](npm-download-integrity.md),
[Skill archive safety](skill-archive-validation.md),
[S3 probe](s3-provider-conformance.md) and
[Skill Security Roadmap](skill-security-roadmap.md).

# Packory delivery checkpoint — 18 October 2026

**Status: NOT COMPLETE.** This is an evidence-bound milestone inventory, not a production certification or a replacement for the [Roadmap V3](roadmap.md), [support matrix](support-matrix.md), or [release gates](release-gates.md).

**Pinned reviewed change:** [PR #74](https://github.com/Cenn0/Packory/pull/74), merged as [`1c711d31f098d91eed6f9d978419ac66e1018382`](https://github.com/Cenn0/Packory/commit/1c711d31f098d91eed6f9d978419ac66e1018382). Its ten named check runs passed on PR head [`0d1dcbdf2219e3c920df0ce17667f0fc3c88f0c3`](https://github.com/Cenn0/Packory/commit/0d1dcbdf2219e3c920df0ce17667f0fc3c88f0c3): application/API/native integration, S3, PostgreSQL, OIDC, Cargo/PyPI, Docker image, docs/privacy and pinned SkillSpector sidecar. Passing this specific CI matrix does **not** prove a real provider's behavior or release-level safety.

## Milestone audit

| Milestone | Evidence on main | Remaining acceptance / blocker | Status |
| --- | --- | --- | --- |
| MS0: consolidation | V3 roadmap, support matrix, signed-evidence gate, baseline CI and privacy checks | Validate all branch policies and complete documentation/operational consistency for a release | Partial |
| MS1: integrity / recovery | Persistent publication journal, quarantine handling, NuGet SHA-512 ledger, integrity subset, PostgreSQL + Moto S3 restore tests | Real-provider coordinated PG/S3 restore and RPO/RTO, all-family integrity, staging migration/rollback, full fault and S3 retention matrix | Blocked |
| MS2: unified feeds | PR #74: private hosted NuGet V4, two feed-local same-ID/version artifacts, grants/revocation, native publish/restore, recovery | Proxy/group feeds, other protocol adapters, feed-wide quotas, versioned control-plane API, declarative import/export, migration and UI | Partial |
| MS3: protocol conformance | Bounded native CLI tests for NuGet/npm/Cargo/PyPI/Docker OCI | Pinned full client/OS matrix, SemVer2/search, Cargo yank, PyPI wheels/sidecars, OCI Bearer/ORAS/Helm and failure/offline tests | Partial |
| MS4: identity and UI | Revocable managed tokens, optional Keycloak JWT, experimental dashboard/Security Center | OIDC browser BFF/PKCE/CSRF, per-feed IAM parity and auditing, accessibility and full security UI E2E | Partial |
| MS5: AI / Skills / MCP | Staged skill scanning/approval, pinned static SkillSpector sidecar, read-only `/mcp` tools/resources/prompts/Skills Extension | Signed provenance and complete policy tests, MCP registry metadata, generic Raw, model catalog, scanner dependency/license review and independent MCP security assessment | Partial |
| MS6: production hardening | CI, conditional storage probes, opt-in strict startup and signed-evidence **verifier** for operator reports | Independent review, reproducible signed releases/SBOM, real restore/incident drills, measured load/SLO and alerts, production TLS/secret workflows | Blocked |
| MS7: multi-node | Exclusive single-writer PostgreSQL lease and restricted readers | Monotonic fencing, partition/restart race tests, OCI session handoff; active-active only if justified | Deferred / not supported |

## Next critical path

1. **Do not close [production release gate #36](https://github.com/Cenn0/Packory/issues/36).** A green emulator run cannot verify S3 Object Lock, ETag delete, remote versioning, consistency after partitions or independently measured RPO/RTO.
2. Select an actual deployment profile and S3 provider **outside the public repository**; execute stopped-writer coordinated snapshot, isolated restore, replay/failure injection and all-kind digest and native-read verification. Publish only sanitized evidence summary.
3. Exercise real-provider conditional immutable PUT, conditional DELETE, retention/quarantine lifecycle and competing writers; reject an unsupported provider rather than treating Moto as certification.
4. Complete MS2 control-plane policies/migrations and MS3 native conformance **behind separately reviewed PRs**. No anonymous access to private feeds or automatic public-upstream fallback.
5. Implement secure browser BFF/CSRF before privileged UI, then independently test authz/auditing, accessibility and scanning policy, including non-approved skill visibility over REST and MCP.
6. Collect release-specific SBOM/license, signed build provenance, recovery, TLS, secret rotation, alerting and load/SLO evidence. Keep real credentials, identities, private logs and production network identifiers out of this public repo.

## Evidence interpretation

- **Works in code:** endpoint or function exists; not necessarily tested on production topology.
- **Officially supported by Packory:** only narrowly qualified versions and operations listed in the [support matrix](support-matrix.md). This is never a protocol vendor's certification.
- **Best practice:** isolated backup/restore, least-privilege identity, incremental schema migration, immutable digest verification.
- **Security recommendation:** do not expose this experimental deployment as a production multi-tenant registry, do not enable active-active writers, require TLS and validated authorization, and do not run untrusted skills.

This checkpoint deliberately leaves operator-only gates incomplete. Do not create synthetic “passed” RPO/RTO, S3 certification, sign-off or production evidence records.

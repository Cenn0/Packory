# Release and disaster-recovery gates

This is an **operator and reviewer acceptance record template**, not a certificate that production testing has happened. Do not close [issue #36](https://github.com/Cenn0/Packory/issues/36) until specific evidence is attached. Capture dates, code SHA, client/provider versions, exact configuration **without secrets**, test environment type, outcome and reviewer. Do not post raw production logs or user identities in this public project.

## Mandatory single-writer preconditions

1. Pin the release commit, supported client operations, provider versions and container artifact provenance. Record passed CI checks; a green workflow means only its tested scope.
2. Confirm one writer only, restricted readers, no active-active claim; document lease behavior and restart semantics. PostgreSQL session advisory locks **do not fence a stale writer after partition**. Runtime lease checks now verify that the backend still owns its granted advisory lock (rather than merely responding to `SELECT 1`); this is defense-in-depth only and **does not provide independent fencing or active-active correctness**.
3. Check TLS on public ingress and PostgreSQL (`SSL Mode=VerifyFull` in strict mode), S3 HTTPS and least-privilege egress/credentials. No public anonymous reads for private feeds.
4. Obtain a **consistent PostgreSQL metadata watermark and S3 object/version inventory while writes are fully stopped**. Include recovery/quarantine prefixes, immutable object versions, retention policy and encryption-key recovery. Confirm no life-cycle rule deletes quarantine evidence early.
5. Restore database + objects to a completely isolated endpoint and bucket/prefix. Hash-verify NuGet, npm, Cargo, PyPI, OCI and Skills contents against authoritative references. Test native client retrieval **only** from restored Packory. Test abort/restart at every journal/quarantine boundary.
6. Run real-provider conditional create and conditional ETag delete tests with isolated disposable keys; include parallel writers, retries, network cutoff, versioning/Object Lock and retention. Confirm provider **semantics** not simply HTTP success. Moto is *not* a substitute.
7. Conduct security review and load/fault tests with explicit SLO/error-budget and measured RPO/RTO. Test secret leakage and dependency confusion. Verify license scanning, SBOM, image provenance and artifact integrity.
8. Rehearse fresh-install, upgrade, rollback and incident restore. Publish support limitations and remaining risks. Only then consider a tagged single-writer release.

## Deployment evidence table (fill with actual run artifacts)

| Gate | Acceptance | Baseline evidence (17 Oct 2026) | Release status |
| --- | --- | --- | --- |
| Unit/API/native client CI | Named CLI/package smoke passes | Existing PR workflows; see commit check runs | Tested *subset* |
| PostgreSQL transactions + Moto S3 | Durable intent, conditional API and quarantine code path | `.github/workflows/postgres.yml`, `s3.yml` | Emulator-tested *subset* |
| Exact real S3 provider | Conditional PUT/DELETE, versioning, retention, concurrent racing, network failure | **No provider report attached** | **Blocked** |
| Coordinated PG/S3 backup/restore | Offline repeatable restore + hashes across all artifact kinds | **No isolated real-provider drill attached** | **Blocked** |
| Storage migration and rollback | Checksum-confirmed source freeze, repeatable staging + rollback | **No complete migration proof** | **Blocked** |
| Identity/BFF/IAM/security audit | Cross-feed scopes, browser CSRF and audit, redaction | API JWT and managed-token tests only | **Blocked for V3 full scope** |
| Native ecosystem matrix | Pinned version/OS/auth/provider failure matrix | Individual hosted native smokes | **Incomplete** |
| SLO/load/ops | Measured SLO/RTO/RPO, traces/alerts/fault exercises | **Not measured for reference deployment** | **Blocked** |
| Release supply chain | SBOM/signing/provenance/dependency/license scan | Partial CI only | **Blocked** |
| Multi-writer fencing | Monotonic epoch or equivalent, partition and failback | **Not implemented** | **Not eligible (MS7)** |

## Restore run record (private operational copy)

Document `started_at`, `ended_at`, the immutable backup identifier and metadata watermark; elapsed verified restore time determines RTO *for that drill*. For a known committed write immediately preceding outage, calculate observed loss window/RPO by comparing canonical package digests and version IDs, not guessed SLA numbers. Keep sensitive endpoint inventory and raw reports outside the public repository.

## Failure handling

If metadata refers to absent/corrupt bytes, or an S3 ETag/conditional delete behaves unexpectedly, stop serving affected content and retain publication/quarantine evidence. Do not truncate journals or garbage-collect until a verified operator-led recovery completes. `PACKORY_STRICT_DEPLOYMENT` is a configuration preflight **not** production certification.

## Required PR template

- [ ] Code reviewed; diff and public privacy/secret scan clean
- [ ] Unit and API tests, positive + negative authz cases green
- [ ] Native client/protocol-specific smoke tests with exact versions
- [ ] PostgreSQL/S3 suite if storage behavior touched; real-provider evidence if claiming real-provider support
- [ ] Migration and rollback if schema/keys changed
- [ ] Docs, matrix, upgrade notes and accepted limitations updated
- [ ] No false "production-ready", "fully OCI compliant" or "active-active" claim

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

## Offline signed evidence acceptance guard

`scripts/verify_release_evidence.py` is a **fail-closed verification helper**, not a production certification service. It requires a locally stored detached RSA-PSS/SHA-256 signature from an **independently controlled release reviewer**, an exact 40-character source commit SHA, and eight signed, current, content-digest-bound report entries:

| Gate identifier | Required operator evidence |
| --- | --- |
| `pg_s3_restore` | Coordinated stopped-writer backup, isolated restore, hash checks, measured RPO/RTO |
| `real_s3_provider` | Conditional PUT/DELETE, versioning/retention, partition tests on **actual** provider |
| `writer_fencing` | Independent stale-writer fencing and verified failover/partition tests, or independently reviewed no-active-active deployment restriction |
| `multi_node_protocol` | Rolling restarts, native client read/write/routing checks, reader restrictions |
| `security_audit` | Threat-model review, provenance/policy tests, credentials and immutable audit log controls |
| `slo_faults` | Measured capacity/error budget, alerting, outage and disaster exercises |
| `load_interop` | Load, fuzz, timeout and native CLI interoperability matrix |
| `supply_chain` | SBOM, licensing, signed artifacts, digest-pinned image provenance and rollback plan |

**Operator workflow:** collect authentic **private** reports from the real intended infrastructure, sanitize or redact the public-facing approval record, independently review each report, and create `release-manifest.json` in an operator-only directory. The strict v1 manifest contains only `schema_version: 1`, `revision`, `audience: "packory-production-acceptance"`, `environment: "isolated-production-like"`, `verifier_key_id`, `issued_at`, `expires_at`, and `gates`. Each gate contains precisely `status: "pass"`, `report: "<basename>.json"` and `sha256: "<64 lowercase hex>"`. Every referenced report must be a unique regular JSON **object** file in the same directory, not a symlink, up to 2 MiB. The verifier rejects malformed UTF-8, duplicate JSON property names and non-finite JSON values even if their raw bytes were correctly hashed and signed. It verifies the signature against exactly the parsed manifest bytes. **JSON validity, hashes and a signature do not establish that the report's operational claims are genuine.** The signed manifest is limited to 32 KiB and expires no later than 30 days after issuance.

Sign the **exact manifest bytes** using RSA-PSS/SHA-256 and an independently protected private key (not committed to GitHub):

```sh
openssl dgst -sha256 -sign <REVIEWER_PRIVATE_KEY> -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:-1 -out <EVIDENCE_DIR>/release-manifest.sig <EVIDENCE_DIR>/release-manifest.json
python3 scripts/verify_release_evidence.py --evidence-dir <EVIDENCE_DIR> --public-key <APPROVED_PUBLIC_KEY> --key-id <VERIFIER_ID> --expected-sha <EXACT_RELEASE_COMMIT_SHA>
```

The command returns nonzero and prints `BLOCKED` for a missing, unsigned, tampered, expired, inconsistent or incomplete set of evidence. Never publish raw evidence, actual endpoint names, signer private keys, internal logs or operator identities. It performs **no network access and no infrastructure mutations**.

**Important:** An operator can sign misleading reports. Cryptographic envelope authentication and file integrity are necessary but **not sufficient** for independent validation of test provenance, realistic coverage, achieved RPO/RTO/SLO or production safety. Without real-provider acceptance and independently reviewed records, issue #36 remains open. The CI job executes **synthetic negative tests of the verifier**, not an actual release acceptance.


### Catalog-reference verification is not a production restore

The optional maintenance-only `/api/admin/integrity/scan` endpoint
cross-checks indexed references against recorded SHA-256/SHA-512 metadata
where available, using direct bounded S3 remote GET/HEAD instead of trusting
the local artifact cache, and reports absent or unverifiable objects. It is a
**bounded observation**, not an atomic PostgreSQL/S3 snapshot and not a
garbage-collection reachability proof. A successful subset does not clear
the real-provider, retention, restore, safety or full M1.6 gates. The release
reviewer must treat `unverifiable`, `weak_digest_match`, `missing`,
`digest_mismatch`, `storage_unavailable`, `size_limit` and
`invalid_reference` as blocking or independently resolvable findings.


## NuGet V3 integrity migration gate

The new durable `nuget_digests` table must survive PostgreSQL/S3 snapshot
fingerprints and isolated restores together with immutable package bytes.
For newly published NuGet revisions, a missing/invalid SHA-512 or mismatched
byte count/hash is a **release blocker**, not an invitation to recompute a
trusted checksum from the bytes being inspected. Historical V1/V2 packages
without this ledger remain explicitly unverified; they are not accepted as
proof of an all-six-ecosystem integrity pass. Independently test historical
upgrade/rollback and real-provider restore before production deployment.


## Private hosted-feed V4 release gate

A native client passing a test against one private NuGet hosted feed
is **not** global multi-feed certification. Verify at least two
same-package/same-version revisions in separate feed stores and
independently scoped readers/publishers. Confirm denied cross-feed
access, revocation, reserved legacy namespace isolation,
disabled-feed behavior and recovery after interrupted uploads.
Check V4 `feed_nuget_versions` fingerprints in PostgreSQL/S3 restore
and retention of all private objects. MS2 still requires independent
provider tests, quota/accounting, IAM and browser hardening and the
remaining ecosystem adapters.

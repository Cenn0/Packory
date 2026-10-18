# Agent Skill Security Scanning — NVIDIA SkillSpector implementation roadmap

**Status:** historical implementation plan, **partially delivered** as of 2026-10-18; not a verified per-task completion ledger. **Baseline:** 2026-10-17, Packory main branch. See the [current status checkpoint](roadmap-status-2026-10-18.md) and [MCP/SkillSpector integration](mcp-security-integration.md) for implemented behavior.  
**Owner:** Packory contributors and coding agents. **Primary scanner:** [NVIDIA SkillSpector](https://github.com/NVIDIA/SkillSpector) (Apache-2.0, with transitive-license verification required).  
**Parent:** [Packory V3 roadmap](roadmap.md), especially M4 (identity/dashboard) and M5 (skills/MCP).  
**Purpose:** actionable, sequential implementation backlog with objective acceptance gates. The completion of a task does **not** constitute a claim that Packory or NVIDIA certifies a skill as safe.

## 0. Context, constraints, and non-goals

At the 2026-10-17 design baseline, the skill ZIP importer made revisions immediately available and there was no scanner release gate. **This is no longer the current release behavior:** a staged approval pipeline, experimental offline SkillSpector worker and read-only MCP endpoint are now implemented. See [MCP/SkillSpector integration](mcp-security-integration.md) for the runtime contract. The web UI still lives in src/Packory/wwwroot (plain JS/CSS, **not React**) and lacks a production-qualified browser BFF/session for privileged security administration. The MCP endpoint is **experimental, not a production-certified standalone MCP service**. SQLite/filesystem and PostgreSQL/S3 are supported in different experimental profiles; neither is a cross-store ACID transaction.

The security feature must first harden the existing artifact lifecycle. It must **not** introduce a required message broker, Kubernetes, uncontrolled remote LLM calls, a new registry protocol, or a generic "execute arbitrary agents" facility. Do not migrate the entire frontend as part of scanner integration. Do not start a downloaded skill, run an install script, or launch an untrusted MCP server.

**Supported versus aspirational:** Every task below is a plan. "Implemented", "tested", "supported", and "production-recommended" remain distinct evidence levels from the parent roadmap.

**Public repository privacy:** Never use a personal email, real credential, real user data, internal hostname, private network path, or unredacted secret in source, examples, fixtures, commits, issues, CI logs, or reports. Follow AGENTS.md and use an appropriate GitHub noreply identity for commits.

## 1. Architecture and trust boundaries

~~~mermaid
flowchart TD
    A[ZIP upload / later pinned Git import] --> B[Packory API: syntax, quotas, auth]
    B --> C[Immutable quarantined snapshot + digest]
    C --> D[Persistent scan job and lease]
    D --> E[Isolated SkillSpector worker]
    E --> F[Canonical JSON findings + coverage]
    F --> G[Packory policy evaluation]
    G -->|approved| H[Published immutable revision]
    G -->|review| I[Authorized review queue]
    G -->|rejected / failed| J[Quarantine; not distributed]
    H --> K[REST download and future MCP Skills Extension]
    F --> L[Security API]
    G --> L
    L --> M[Web UI: security overview, findings, history, decisions]
~~~

**Required isolation:** The API does not invoke SkillSpector inside its own process or expose the Docker socket. SkillSpector receives a **local, read-only, digest-verified snapshot**, never a user-controlled URL. It runs without production credentials, with dedicated UID, restricted filesystem, CPU/memory/time/process limits, and no network egress in the default static mode. If OSV updates are needed, supply an explicitly configured trusted data channel or offline database; --no-llm alone is not a network isolation guarantee. A local process-only proof of concept is allowed for development, but cannot be advertised as production isolation. For production, use a separate hardened container or equivalently isolated execution environment.

**Immutable evidence:** Persist uploaded artifact SHA-256, scan policy revision, scanner name/version/image digest, inspection coverage/completeness, report digest, timestamps, origin information when available, and decision actor. Scan the exact bytes that will later be distributed. A hash is an integrity check, not proof of publisher identity or safety.

**Distinct state machines:**
- Artifact: PENDING_SCAN -> SCANNING -> APPROVED | NEEDS_REVIEW | QUARANTINED | SCAN_FAILED, with later REVOKED, and explicit controlled rescan/review transitions.
- Job: QUEUED -> LEASED/RUNNING -> SUCCEEDED | FAILED | TIMED_OUT | CANCELLED (with durable retries/reconciliation).
- Scan verdict and human release decision are separate records. A completed scanner run is not automatically an approval.

**Strict publication invariant:** No public listing, archive download, SKILL.md read, cached blob redirect, future MCP skills/list/get/resources/read, or equivalent legacy route may expose a pending, quarantined, failed, revoked or unapproved revision under an enforcing policy. Authorized administrators can inspect quarantined content only via separately protected, sanitized inspection endpoints. Validate all read paths against the authoritative server-side state, not UI badges.

## 2. Scan/approval policy

Default enforced policy for **new installations that enable the skill security feature**:
- Scanner: NVIDIA SkillSpector; static mode; LLM disabled; outbound network disabled.
- Ingest rejects malformed, traversal, symlink, duplicate-normalized-path, oversized, or ambiguous ZIP inputs; never execute a member.
- Verify the scanner process result **and** machine-readable JSON. Exit code 0 is not an "approved" verdict: SkillSpector's default exit 0 can include CAUTION. Exit 1 can indicate a security gate or incomplete coverage; exit 2 indicates a scanner error.
- Parse execution_successful and analysis_completeness / inspection ledger where supported by the pinned version. Unknown required fields, incomplete coverage, timeout, malformed report, scanner version mismatch and transport failures fail closed.
- Record risk score (0–100), severity, recommendation, normalized findings and raw report digest; never decide solely from a numeric threshold. A configurable policy may block defined severity/rule categories and require human review for CAUTION or uncertain findings.
- Baselines and suppressions must be Packory-controlled, reviewable, versioned, content-bound and auditable; **never** trust suppressions bundled by an artifact publisher or silently allow them to change the score.
- Default network policy and LLM mode must be visible in the UI. Optional remote LLM features must require separate explicit admin opt-in and documented data-transfer implications.

**Legacy upgrade policy:** Existing public skills are not retroactively labeled approved. Create a migration inventory of UNSCANNED legacy revisions, retain bytes, and add a documented enforcement cutover/backfill workflow. While enforcement is enabled, prevent their public delivery until approved; do not silently convert them to APPROVED. Document availability impact and rollback. If a compatibility mode is retained temporarily, label it insecure/not recommended and never claim enforced scanning.

## 3. AI implementation work packages

Each work package is an independently reviewable PR where feasible. Coding agents must inspect live main first, reuse existing primitives, implement tests, update docs/API, and return concrete evidence. Never declare a check passed without executing it.

### Phase A — design, contracts and security gate (P0)

**SS-001 — Repository inventory and scoped ADR** (depends on none)
- Inspect AGENTS.md, docs/roadmap.md, docs/architecture.md, docs/api.md, SkillEndpoints.cs, persistence implementations, BlobStore, startup/recovery, auth and wwwroot.
- Map every way a skill becomes visible (REST, catalog, downloads, SKILL.md, static blob URLs, planned MCP).
- Add an ADR documenting exact trust boundaries, staged publish, no-code-execution invariant, compatibility and auth assumptions.
- **Acceptance:** an endpoint/read-path inventory with file locations; no unsupported claims about existing BFF/MCP or multi-instance support; reviewed rollback path.
- **Agent instruction:** "Inventory every current skill write/read path and propose the minimum incremental implementation against main. Do not write scanner integration until the trust-boundary ADR and endpoint inventory are checked."

**SS-002 — NVIDIA version, contract, licenses and build reproducibility** (depends on SS-001)
- Select a reviewed SkillSpector release/commit and pin exact image digest or reproducible lockfile; do not float :latest or install main at runtime.
- Verify Apache-2.0 project LICENSE plus dependency tree and THIRD_PARTY_NOTICES; document any non-permissive transitives and the deployment implications before shipping.
- Save **sanitized, synthetic** golden JSON reports covering clean, CAUTION, HIGH, incomplete, tool error and unexpected schema. Capture relevant exit-code, execution_successful, inspection-ledger and report-version semantics. Track compatibility for upgrades.
- **Acceptance:** license inventory/SBOM, attribution and pin proof, report parser fixtures, deliberate unsupported-version rejection.
- **Agent instruction:** "Pin a reviewed SkillSpector version. Prove the actual CLI/report contract with synthetic fixtures and audit all shipped components and licenses."

**SS-003 — Safe staging, persistence and schema migration** (depends on SS-001)
- Add additive schemas for skill revision visibility, artifact digest, scan attempts/jobs, normalized findings, policy versions and human decisions. Preserve migration/rollback behavior on SQLite and PostgreSQL.
- Store immutable quarantined ZIP bytes; avoid final public index until policy approval. Build bounded, safe inspection/extraction only inside the sandbox: reject traversal, links, duplicate canonical paths, Unicode confusables where ambiguous, decompression bombs and excessive file counts/depth/size.
- Persist intent and reconcile DB/blob mismatches; never assume PostgreSQL and S3 commit atomically.
- **Acceptance:** crash between blob put/DB commit/job enqueue leaves no published bypass; recoverable intent; schema upgrade and rollback tests on both DB implementations.
- **Agent instruction:** "Introduce durable staged Skill revisions and migrations; preserve storage invariants and prove no public visibility for pending content under fault injection."

**SS-004 — Publication/read authorization gate** (depends on SS-003)
- Enforce a single server-side eligibility decision across /api/skills, /api/skills/{name}/{version}, /download, /skill.md, generic catalog and future MCP endpoints.
- Separate owner/admin inspection endpoints from public read paths. Prevent direct blob access bypass and ensure cache headers or signed URLs never expose revoked content.
- Document response contracts: new async imports return 202 with status/job link; keep or version legacy route behavior explicitly, without bypass.
- **Acceptance:** table-driven 401/403/404, tenant/feed authorization where implemented, version collision, concurrent reads during revocation, inaccessible orphaned blob, and legacy-route regression tests.
- **Agent instruction:** "Close every skill distribution path using one authoritative release policy, including existing legacy routes. Prove failure-closed behavior."

### Phase B — scanner worker and orchestration (P0/P1)

**SS-005 — Isolated SkillSpector adapter** (depends on SS-002, SS-003)
- Implement a small versioned adapter interface in .NET (e.g. ISkillScanEngine); choose local file snapshot input, no remote URLs, and emit structured results (findings, source locations, risk, coverage, engine metadata, execution status).
- Use SkillSpector CLI --no-llm --format json with strict-incomplete/coverage handling where supported by the pinned release. Treat 0/1/2 exit codes as transport/gate signals to be interpreted alongside JSON rather than a boolean verdict.
- Bound stdout/stderr and report bytes, redact environment and logs, validate JSON schema and normalize unknown properties safely. Scanner failure must not look like a clean result.
- **Acceptance:** golden contract tests, clean/CAUTION/HIGH/incomplete/crashed/malformed/timeout cases; raw sensitive evidence is not returned in public APIs.
- **Agent instruction:** "Build an isolated, swappable NVIDIA scanner adapter with resilient JSON parsing and explicit completeness handling; do not call scanner code from HTTP request handlers."

**SS-006 — Worker isolation and deployment profiles** (depends on SS-005)
- Provide opt-in dev Compose worker and a hardened production container profile: pinned image, non-root, read-only root FS, tmpfs scratch, read-only snapshot, drop capabilities, no-new-privileges, seccomp, resource/process/time budgets, default deny egress, no API/DB credentials.
- Prefer queue polling/capability-limited local transfer over handing the entire storage credentials to the worker; if it must access storage, issue short-lived object-scoped credentials. Do not mount docker.sock.
- Expose readiness for security subsystem separately; avoid treating core NuGet/npm health as failed merely because optional scanning is unavailable. Enforced skill uploads fail closed.
- **Acceptance:** worker demonstrably cannot access a fake secret, host path, metadata endpoint or arbitrary network; dead worker/overload cannot publish; compose and docs work for SQLite/filesystem and PostgreSQL/S3 profiles as qualified.
- **Agent instruction:** "Package and harden the scanner worker. Document dev-only limitations and prove isolation with negative tests instead of relying on container defaults."

**SS-007 — Durable job scheduler, retries and recovery** (depends on SS-003, SS-005)
- Implement/reuse persisted job leasing, idempotency, heartbeat, expiry, bounded concurrency, backoff and terminal failure; a scan key includes artifact digest + engine version + policy revision + scan mode.
- Ensure duplicate jobs cannot create duplicate approvals. Include cancellation, redrive and graceful shutdown. No distributed exactly-once claim.
- **Acceptance:** restart at each stage, two competing workers, stale lease, duplicate delivery, database transient error and blob unavailability; a failed/incomplete job never changes visibility to approved.
- **Agent instruction:** "Wire the scanner to durable jobs with restart, concurrency and idempotency tests; never keep a DB transaction open throughout analysis."

**SS-008 — Policy engine, decisions and audit** (depends on SS-004, SS-005, SS-007)
- Implement deterministic, server-side evaluation over normalized findings and completeness. Maintain independent scan outcome, policy verdict, and release decision.
- Support APPROVED, NEEDS_REVIEW, QUARANTINED, SCAN_FAILED, REVOKED; record policy revision, reason, actor, timestamp and optimistic concurrency. Permit manual review only where explicit policy allows, and never override a non-complete scan into an apparently scan-approved state.
- Prevent publisher-supplied suppression rules from automatically affecting policy; scope approvals to immutable artifact digest, never to a mutable skill name alone.
- **Acceptance:** decision matrix for clean/CAUTION/HIGH/CRITICAL/incomplete/engine failure/manual override/changed digest; auditable transitions and negative authorization cases.
- **Agent instruction:** "Create a deterministic Packory security policy independent of SkillSpector scoring and gate every release decision with auditable, role-controlled rules."

### Phase C — management API and web UX (P1)

**SS-009 — Versioned Security Management API** (depends on SS-007, SS-008)
- Create protected management endpoints under /api/v1/security/skills (route names illustrative; reconcile with existing conventions): queue, scan status, findings, scan history, rescans, release decisions and policy view/edit.
- Use bounded pagination, typed DTOs, problem-details errors, ETag/version for transitions, server-side sorting/filtering and no-store for sensitive responses.
- Public skills only expose minimum trust state, score band and last scan summary; never raw attack prompts, redacted secrets, unapproved artifact contents or detailed private findings.
- **Acceptance:** OpenAPI/schema examples, scope tests (read/publish/admin), pagination, CSRF behavior where cookie sessions exist, and end-to-end API smoke tests.
- **Agent instruction:** "Implement a typed, versioned API for the skill scanner dashboard and admin workflow. Explicitly separate public from privileged security metadata."

**SS-010 — Web UI: skills list and overview** (depends on SS-009; read-only portions can start earlier)
- Extend existing src/Packory/wwwroot/app.js and styling, preserving current look and responsiveness; do not rewrite the whole UI into React.
- Add AI Hub > Skills list: searchable name/version, origin, publisher/provenance (only when verified), last scan, scanner version, result, and human-readable badge; counters for queued/review/quarantined/approved/failed.
- Make status explicit: "Not scanned", "Scanning", "Review required", "Approved under policy", "Quarantined", "Scan failed", "Revoked". Never label green as "safe" or "malware-free".
- Poll visible jobs at bounded intervals; stop polling hidden/inactive views, show loading/empty/error/retry; keyboard navigation and accessible status indicators; no state conveyed solely by color.
- **Acceptance:** snapshot/browser tests for responsive mobile/desktop, i18n-safe text and XSS tests for malicious skill names/descriptions.
- **Agent instruction:** "Implement a security-aware skill catalog in Packory's existing web UI with precise trust wording, filters, summary counters and accessible responsive states."

**SS-011 — Web UI: detail, findings, history and reports** (depends on SS-009, SS-010)
- Per-version security detail: risk score **as a scanner signal**, severity, verdict, completeness, scanned files, skipped/failed files, run date, engine version, policy, exact SHA-256 and origin. Show detailed findings to authorized roles with severity/category/file/line/remediation.
- Add scan timeline, changes across scan versions, retry/re-scan events, guarded JSON/SARIF export for authorized users, and sanitized text previews only (no active HTML, script, SVG or remote media from untrusted bundles).
- Every report notes scanner limitations; report download never contains uncontrolled raw secrets by default.
- **Acceptance:** synthetic attack payload cannot execute in browser; incomplete analysis prominently displayed; references and line numbers preserve meaning; UI never mistakes a score of zero for a guaranteed safe skill.
- **Agent instruction:** "Add a reviewer-grade skill security details page with normalized findings, coverage, scan history and safe previews. Test against hostile Markdown/HTML."

**SS-012 — Browser session/BFF prerequisite for privileged UI** (depends on current parent roadmap M4.1/M4.2)
- Introduce/reuse secure same-origin server-side session for interactive admin operations; short-lived CSRF protection, secure/HttpOnly/SameSite cookie semantics, idle timeout, logout and role checks.
- Support local admin mechanism plus optional Keycloak OIDC without forcing Keycloak on standalone installs. **Do not** persist static admin tokens, provider bearer tokens or API keys in localStorage/sessionStorage or scripts.
- While this prerequisite is missing, release only read-only UI capabilities and use authenticated API/CLI for decisions; do not build an unsafe browser-side token workaround.
- **Acceptance:** browser session fixation, CSRF, 401/403, logout, token leakage, role downgrade and two-user isolation tests.
- **Agent instruction:** "Provide a tested browser-safe identity boundary for privileged security actions, compatible with optional OIDC and the current Packory deployment profiles."

**SS-013 — Web UI: reviewer queue and policy administration** (depends on SS-008, SS-009, SS-011, SS-012)
- Add reviewer queue with filter/search, triage, diff between revisions, approve-with-reason where policy allows, quarantine/revoke and re-scan controls, confirmation for destructive changes, audit timeline and read-only diagnostics for non-admins.
- Add security settings: engine version/health, mode (offline static default), policy rules/severity gates, auto-approval behavior, retry/concurrency limits, re-scan interval and retention. Show side effects of policy changes and cutover migration status.
- Concurrency-safe mutations; user-visible feedback only after server confirms the action. Hide unauthorized controls but **always** enforce permission server-side.
- **Acceptance:** browser E2E for authorized/unauthorized actions, conflict/retry, audit trail, mobile layout and WCAG-oriented keyboard focus testing.
- **Agent instruction:** "Create the privileged reviewer workflow and Security Settings screens behind a proper BFF, with auditability, revocation and explicit scan-completeness warnings."

### Phase D — lifecycle, MCP and hardening (P1/P2)

**SS-014 — Existing skills backfill, rescans and incident response** (depends on SS-008, SS-009)
- Inventory all previously published skills as UNSCANNED on upgrade. Support controlled batch backfill and an explicit enforcement cutover, with report of possible download unavailability.
- Trigger rescan for scanner/rule changes, policy changes and security advisories, and allow admin rescan without rewriting the immutable version. A new scan with blocking findings can revoke a previously approved revision.
- Document rollback, retention and notification process; do not silently delete forensics or orphan references.
- **Acceptance:** old ZIP migration, idempotent large backfill, concurrent download vs revoke, policy rollback and rescans without modification of content digests.
- **Agent instruction:** "Migrate historical skill revisions without incorrectly grandfathering unscanned content. Add resumable backfill and safe revoke/rescan behavior."

**SS-015 — MCP Skills Extension distribution gate** (depends on Packory read-only MCP server M5.3 and SS-004, SS-008)
- Implement the pinned official MCP Skills Extension capability only when a conformant base MCP server exists, using skills/list and skills/get plus resources/read, with file digests and snapshot consistency. Directory listing optional.
- Serve only approved immutable revisions; scope authorization to the same feed/catalog policy as REST. Never execute tool definitions or skill scripts to inspect them.
- Verify client interoperability against the pinned MCP revision, pagination, cache headers, digest/size mismatch handling, revocation and absence of unapproved resources. Older MCP clients may require an explicit REST/resources fallback; do not claim universal client compatibility.
- **Acceptance:** protocol conformance suite with authorized and unauthorized MCP clients and proof that MCP returns no bytes unavailable through the REST release gate.
- **Agent instruction:** "Integrate approved skill distribution into a future Packory MCP service using the published Skills Extension, preserving content digests and authorization."

**SS-016 — Threat testing, performance and data protection** (depends on SS-006, SS-013)
- Build benign/malicious synthetic fixtures: prompt injections in frontmatter and Markdown, credential exfiltration patterns, nested archives, oversize bombs, parser errors, Unicode/path ambiguity, package files, false positives and incomplete inspection. All fixtures must use fake credentials and safe inert strings.
- Measure coverage and false positives/negatives on a labeled test corpus; document exact benchmark composition and uncertainty (not generalized detection claims). Test no egress in offline mode and CPU/memory/time limits.
- Redact sensitive evidence in logs, browser APIs, exports and exception traces. Fuzz ZIP validation and failover between metadata/blob writes.
- **Acceptance:** reproducible unit/integration/end-to-end/performance/security suite, results with scanner version and known missed categories, and explicit reviewer approval of remaining risk.
- **Agent instruction:** "Create regression and adversarial coverage across ZIP handling, scanner contract, policy, every read path and the web UI. Report false negatives and failed cases rather than declaring the scanner secure."

**SS-017 — CI, packaging, operations and release gate** (depends on SS-002, SS-016)
- Add build/test jobs and license/SBOM checks for the Python scanner image. Publish provenance of pinned source and image digest. Avoid paid GitHub Actions quota assumptions and do not automatically pull mutable third-party images during an offline test.
- Provide self-hosting guide, worker sizing/timeout, update procedure, alerting, rollback/recovery, policy examples, threat model, operator troubleshooting and THIRD_PARTY_NOTICES; keep examples synthetic.
- Test SQLite/filesystem and PostgreSQL/S3 separately; label any provider without verified restore as experimental. Ensure NuGet/npm/Cargo/PyPI/OCI functionality has no regression from optional scanning.
- **Acceptance:** build, test, UI, license, privacy, isolated-worker smoke, migration, backup/restore, and approval/revocation E2E gates pass. Mark support level and remaining production blockers accurately.
- **Agent instruction:** "Package the feature reproducibly, test and document the complete operator lifecycle, preserve other registry adapters, and update support claims only when evidenced."

## 4. Delivery order and dependencies

| Stage | Work packages | Expected demonstrable deliverable |
| --- | --- | --- |
| Gate 0 (design) | SS-001, SS-002 | Approved ADR, license inventory, pinned scanner contract |
| Gate 1 (no bypass) | SS-003, SS-004 | Pending revisions cannot be publicly distributed |
| Gate 2 (real scanner) | SS-005, SS-006, SS-007 | Isolated static NVIDIA scans with durable jobs and recovery |
| Gate 3 (decision) | SS-008, SS-009 | Policy-controlled approval with versioned API and audit |
| Gate 4 (usable read-only UX) | SS-010, SS-011 | Skills security dashboard and findings/history |
| Gate 5 (safe administration) | SS-012, SS-013 | Interactive review, quarantine, rescan and policy settings |
| Gate 6 (adoption) | SS-014 | Legacy backfill and continuous security lifecycle |
| Gate 7 (protocol) | SS-015 | MCP-served approved skills, conditional on MCP M5.3 |
| Gate 8 (release) | SS-016, SS-017 | Security/e2e performance evidence and reproducible deployment |

**Parallel work:** Once API contracts are reviewed, the static UI shell for SS-010 and fixtures for SS-016 may proceed in parallel. Do not start a browser approval UI ahead of SS-012, or MCP distribution ahead of the REST gate. Gate 4 does not imply Gate 5 is production-ready.

## 5. Proposed API surface (design; not implemented)

| Endpoint (illustrative) | Capability | Visibility |
| --- | --- | --- |
| POST /api/skills | Staged upload; 202 with job reference when enforcement enabled | Authenticated publish; never bypass scan |
| GET /api/skills | Only distributable revisions, optionally minimal trust summary | Existing authorized readers |
| GET /api/skills/{name}/{version}/download | Download approved immutable ZIP | Existing authorized readers |
| GET /api/v1/security/skills | Paginated reviewed scan inventory | Reviewer/admin |
| GET /api/v1/security/skills/{id}/scans | Attempts, completeness, engine version, findings | Reviewer/admin |
| GET /api/v1/security/scans/{scanId}/report | Sanitized JSON/SARIF | Reviewer/admin |
| POST /api/v1/security/skills/{id}/rescan | Idempotent rescan enqueue | Reviewer/admin by policy |
| POST /api/v1/security/skills/{id}/decision | Approve/quarantine/revoke with reason + version/ETag | Reviewer/admin by policy |
| GET/PUT /api/v1/security/policy | Versioned policy snapshot/change | Admin (PUT) |
| GET /api/v1/security/health | Safe subsystem/engine operational status | Admin; aggregate readiness separately |

Response schemas must distinguish scan completeness, scanner recommendation, Packory policy verdict and actual release decision. Use server-generated IDs and version/digest pairing, not mutable names as authorization keys. Routes are proposals and must be checked against existing API conventions before implementation.

## 6. Web UI acceptance layout

Navigation should extend the existing dashboard and work on phone/tablet/desktop:

1. **AI Hub / Skills:** discovery and search; status badge, publisher provenance, last scan, approval state and safe download only if eligible.
2. **Skill detail / Security:** high-level risk assessment, file inventory/coverage, finding table with file/line, affected rule/category, redacted evidence, scan history and SHA-256; readable without being an execution surface.
3. **Security / Review queue:** only reviewers/admins; pending findings, decisions with required reasons, action audit, stale approval warning.
4. **Security / Scan jobs:** queue/worker health, retryable failures, in-progress status, safe error messages.
5. **Administration / Security policies:** scanner connectivity, default offline behavior, severity rules, review settings, retention and explicit legacy backfill/cutover status.

Accessibility: focus management; full keyboard support; WCAG-oriented checks; meaningful text alongside colors; correct empty/error/loading states. Treat all metadata, report text and Markdown as hostile and escape/sanitize before rendering. No fetched remote images or executable previews from uploaded bundles.

## 7. Definition of Done and AI-agent execution protocol

For **each** task:
1. Read AGENTS.md, docs/roadmap.md, this roadmap, affected code and tests from the **current main commit**. Record current SHA and prior completed prerequisites.
2. Implement only the defined scope, preferably in one branch/PR. Reuse code; no irrelevant refactors; do not remove existing protocol regression tests.
3. Provide a concise change log, migrations/rollback notes, explicit API contract and updated docs where applicable.
4. Run relevant targeted tests **plus** repository baseline: dotnet restore Packory.slnx; dotnet build Packory.slnx -c Release --no-restore -warnaserror; dotnet test Packory.slnx -c Release --no-build; node --check src/Packory/wwwroot/app.js. Run additional scanner integration/worker/browser/security tests added by the task.
5. Capture actual test outcome, environment limitations, failures, known gaps and risk level. A skipped test is not a passing test.
6. Inspect changes for hardcoded credentials, private data, email addresses, external network sends, missing license attribution and unsafe report examples.
7. Open a PR with acceptance checklist/evidence and dependency links. Merge only after required checks and review pass; never bypass repository rules or claim a successful merge that did not occur.
8. Only then mark the item complete in this roadmap and update support-matrix/release-gates if supported by evidence.

### Global release blockers

- A pending/revoked/failed skill can still be accessed through **any** REST, catalog, blob or MCP path.
- Scanner errors, partial coverage, unknown versions or disabled mandatory engines are silently treated as clean.
- Untrusted code or MCP tools are executed during import/scan/discovery.
- Default scanning can exfiltrate artifacts or credentials over a network without explicit operator consent.
- Reviewer actions use browser-persisted admin bearer tokens or bypass server-side scope/CSRF checks.
- An old migrated skill is silently considered scanned and approved.
- Workers can access host secrets, privileged runtime sockets, unrestricted egress or production storage credentials.
- PostgreSQL/S3 inconsistencies can expose or permanently delete pending/revoked bytes without safe recovery.
- License obligations and notices of embedded/transitive scanner dependencies are not verified.
- CI or deployment documentation advertises production safety without provider-specific and security tests.

## 8. Source references and contract verification

- [NVIDIA SkillSpector README](https://github.com/NVIDIA/SkillSpector) — Apache-2.0, CLI, --no-llm, JSON/SARIF, exit codes and default risk policy.
- [NVIDIA SkillSpector development guide](https://github.com/NVIDIA/SkillSpector/blob/main/docs/DEVELOPMENT.md) — invocation, scan state and normalized outputs.
- [NVIDIA SkillSpector dependencies](https://github.com/NVIDIA/SkillSpector/blob/main/pyproject.toml) and [third-party notices](https://github.com/NVIDIA/SkillSpector/blob/main/THIRD_PARTY_NOTICES.md) — audit **selected revision**, not moving main.
- [Agent Skills specification](https://agentskills.io/specification) — ZIP/skill manifest compatibility expectations.
- [MCP Skills Extension](https://skills.extensions.modelcontextprotocol.io/specification/stable/skills) — capability negotiation, skills/list, skills/get, resources/read, digest checks.
- [Packory architecture](architecture.md), [API](api.md), [V3 roadmap](roadmap.md), [release gates](release-gates.md) — canonical integration constraints.

**Decision:** Start with SS-001 through SS-004, then integrate pinned NVIDIA SkillSpector. A security scanner is not a substitute for provenance, authorization, content integrity, sandboxing and human review.

## SkillSpector verification and repeat-scan hardening (19 October 2026)

Packory's deterministic scanner policy now requires consistent, fully inspected
coverage, not merely the aggregate `analysis_completeness.is_complete=true`.
Any nonzero partial/entirely-uninspected/skipped/failed file counter, a nonempty
scope exclusion or ledger exception, a known analysis limitation, or an explicitly
reported coverage under 100% yields `SCAN_FAILED` and remains unavailable for
MCP/REST distribution. Duplicate JSON property names and incompatible LLM
usage metadata are rejected as ambiguous/untrusted scan results.

Each persistent QUEUED attempt has a result identity tied to its immutable
artifact digest **and** the revision transition timestamp. Rescanning the same
bytes cannot consume a leftover sidecar report from an earlier attempt.
This does not make the queue distributed, prove scanner completeness beyond
the pinned version's JSON contract, or certify Skill safety. A user/administrator
must still review any eligible report and explicitly approve the exact digest.

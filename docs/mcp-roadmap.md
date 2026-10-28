# MCP delivery roadmap

**Updated: 20 October 2026.** This is the actionable MCP continuation of [V3 MS5](roadmap.md#ms5--ai-distribution-skills-and-mcp-p1p2) and [V4 E10](roadmap-v4.md). It incorporates the protocol, Registry, quality-assessment and optional gateway gaps identified in the MCP completeness review. A task is complete only for its stated behavior, including failure cases and reproducible tests. A parser, schema or disabled placeholder alone does not complete a feature.

## Product boundaries

Packory has four distinct responsibilities:

| Component | Purpose | Current delivery |
| --- | --- | --- |
| Packory MCP server | Let an authorized client discover Packory artifacts and read approved Skills | Native Streamable HTTP, legacy initialize clients and current stateless protocol, tools/resources/prompts and Skills Extension |
| MCP Registry | Store, review, revoke and discover immutable server descriptors | Remote-only manifest profile, v0.1 read API, Packory management API and dashboard |
| Artifact assessment | Explain independently what was checked, found and not checked | Existing offline security scan plus deterministic static Skill quality reports; signed external validation exports remain a separate opt-in feature |
| Optional MCP gateway | Connect to approved external servers under explicit runtime policy | Full remote/isolated gateway **not implemented**; an in-process, OIDC-gated **PostgreSQL-only prototype** is tracked separately as MCP-G0 ([contract](mcp-connectors.md)) |

Registry approval is a local distribution decision. It does not prove publisher identity, signature authenticity, runtime compatibility or execution safety. Quality findings do not alter the approval state or the immutable Skill bytes.

## Completed functional work in this continuation

| ID | Delivered behavior | Reproducible evidence |
| --- | --- | --- |
| MCP-P1 | Native-client compatibility checks for MCP protocol revisions 2025-06-18, 2025-11-25 and 2026-07-28; current discovery/caching metadata and advertised capabilities | `McpProtocolTests`, existing `ApiTests` and `McpOAuthMetadataTests` |
| MCP-P2 | Opaque keyset pagination for Skills and directory children; strict invalid-parameter errors; canonical escaped URIs and exact-byte binary resources | `McpProtocolTests`; directory cursors bind the immutable archive digest |
| MCP-P3 | Complete traversal of the global artifact catalog and exact package versions beyond the former 100-row ceiling, including Maven and Raw; approval predicate applied before LIMIT | `McpArtifactCatalogTests`, SQLite and actual PostgreSQL test profiles |
| MCP-R2a | The admitted inert manifest profile now stores remote, package and mixed distributions, SSE/Streamable HTTP/stdio transport descriptors, bounded declared variables, icons, repository metadata and namespaced extensions with fail-closed negative checks; nothing is executed | `McpRegistryManifestShapeTests`, exact-byte SQLite persist/revoke, existing REST/MCP discovery and provider suites |
| MCP-R1 | All three v0.1 Registry read routes for the supported remote manifest profile; exact/latest versions, bounded keyset pages, correct JSON envelopes and explicit unsupported-filter errors | `McpRegistryTests`, `McpRegistryPostgresProviderTests`, native C# MCP discovery |
| MCP-Q1 | `assess_skill_quality`, read-only REST and dashboard: deterministic findings, suggestions, file/manifest/archive digests, coverage and incomplete-result semantics | `SkillQualityAssessmentTests`; same report through native MCP and HTTP |
| MCP-U1 | Dashboard pagination and quality inspection; stale responses from previous credentials or revoked Skills are discarded | `scripts/test-mcp-dashboard.cjs` (five browser scenarios with synthetic API responses), plus dashboard contract checks |

The three v0.1 endpoints are `GET /v0.1/servers`, `GET /v0.1/servers/{serverName}/versions`, and `GET /v0.1/servers/{serverName}/versions/{version}`. The implemented read contract does not imply full official Registry conformance: publishing, identity verification, broader manifests and synchronization are separate tasks.

Tests are evidence for the exact source revision and environment that ran them. The PostgreSQL tests explicitly skip when no test database is configured; an unconfigured local run is not PostgreSQL evidence. CI executes them with `PACKORY_TEST_POSTGRES`.

The browser job pins Node 22 and Playwright 1.62.1, serves the actual dashboard assets with the application's CSP, and checks completed/incomplete/error quality reports, cursor continuation, delayed-response invalidation and 1440/375-pixel layouts. Its synthetic API responses isolate UI behavior; the .NET/native-client and PostgreSQL suites establish the backend contracts separately.

## Priority 1 — remaining Registry and protocol work

| ID | Work to finish | Completion criteria and dependencies |
| --- | --- | --- |
| MCP-R2b | Qualify the complete official `server.json` compatibility surface beyond Packory's R2a admitted subset | Pin and execute independent official JSON Schema conformance fixtures, reconcile intentionally excluded shapes and non-SemVer version constraints, publish a versioned support matrix; no client may install/connect/interpolate from the Registry |
| MCP-R3 | Publisher/namespace ownership model and verification | Separate claimed identity, verified identity and operator approval; implement selected official proof mechanisms with challenge expiry, replay protection, namespace takeover/revocation tests and audit; do not infer ownership from a URL or package name |
| MCP-R4 | Signature and provenance verification | Bind proof to exact artifact digest, revision and issuer; explicit supported algorithm/key/trust-root policy, rotation/revocation/expiry and malformed proof tests; distinguish unsigned, invalid and verified |
| MCP-R5 | Read-only upstream Registry synchronization | Allowlisted source, bounded pages, persistent checkpoints, idempotence, retries/backoff, differential updates and deleted/revoked records; no import is automatically trusted; fault/restart and SSRF/redirect tests |
| MCP-R6 | Full version/lifecycle management and export | Implement the selected Registry publish/status contract alongside Packory review state; immutable history, defined deprecation/tombstones, cursor compatibility, audit and backup/restore tests |
| MCP-P4 | Broader independent client conformance | An independent real-HTTP conformance smoke using **official TypeScript SDK v2.3.1 on Node 22** is merged in [PR #101](https://github.com/Cenn0/Packory/pull/101) with a successful independent CI workflow: explicit legacy/current era negotiation, bearer-token denials, tool/prompt discovery and read-only tool/resource calls. **Still open:** multiple client revisions, complete schemas, large pagination, cancellation, OAuth metadata/PKCE and protocol mismatch negatives. CI green is required before counting this slice as tested |
| MCP-P5 | Per-feed MCP authorization and native installation guidance | Depends on MS2/E0 feed identity and scope model; list only readable feeds/revisions, authorize exact asset references and return adapter-correct install instructions; two same-name private feeds and revoked/OIDC/managed token tests |
| MCP-P6 | Optional protocol feature decisions | For subscriptions, task/progress extensions, completions, sampling or elicitation, record a concrete Packory use case first; only advertise capabilities with implemented handlers and native positive/negative tests. No capability is enabled merely to increase a feature count |
| MCP-P7 | Bound Skill-list database work and measure read capacity | Provider-side approval-first SQLite/PostgreSQL keyset pages, paginated list tool, bounded Skills Extension candidate scans and revocation tests are implemented (PR #100); **remaining:** independent capacity measurements on representative storage, large malformed-archive stress and approved-content hashing cost characterization before declaring the performance gate complete |

R5 depends on R2/R3/R6 identity and lifecycle contracts. P5 must not reinterpret a namespace-limited publishing token as a global catalog read token. The current global MCP endpoint rejects scoped managed tokens and excludes private feed records.

## Priority 1 — quality beyond security scanning

The quality work must expose **what was checked** rather than a single unexplained score. The existing static profile is a completed function for Skill documentation; it does not complete executable agent/connector validation.

| ID | Work to finish | Completion criteria and dependencies |
| --- | --- | --- |
| MCP-Q2 | Versioned assessment schemas and durable history for Skills, agents and connectors | Typed artifact/profile identity, exact source digest, evaluator version/config digest, coverage, findings and invalidation on changed source; migration and replay tests; keep security, static quality and behavioral results separate |
| MCP-Q3 | Static agent/connector contract validation | Validate actual supported manifests, input/output schemas, declared tools/scopes, examples and pinned dependencies; unknown types are explicitly unsupported; no mandatory invented fields or false claims of functionality |
| MCP-Q4 | Isolated functional test runner | Separate unprivileged disposable process/container, immutable input, explicit test fixtures, synthetic credentials, no host/container socket, denied egress by default, bounded CPU/RAM/time/output and complete cleanup; prove timeout/escape/secret-isolation negatives before accepting jobs |
| MCP-Q5 | Behavior and usefulness tests | Author-supplied examples plus independently maintained regressions; verify outputs, edge/error handling, determinism, declared permissions and compatibility; record skipped/failed/inconclusive separately; never execute a submitted task in the Packory server |
| MCP-Q6 | Controlled improvement suggestions | Evidence-linked diagnosis and a reviewable patch/new revision; separate optional model-assisted suggestions with explicit provider/data policy; require a new assessment against the changed digest; never silently overwrite approved artifacts |
| MCP-Q7 | Quality dashboard and comparison workflow | Compare versions/profile runs, show missing coverage and concrete suggestions, filter findings, export sanitized evidence; privileged job/review actions depend on MS4 browser session/CSRF controls |
| MCP-Q8 | Evaluator qualification | Curated good/bad fixtures and seeded regressions, false-positive/negative tracking by check, repeatability and evaluator-upgrade diffs; document exactly which dimensions cannot be inferred from static content |

Q4 requires the separate execution boundary and threat model; it is not satisfied by adding a shell command, HTTP proxy or generic `run_tool` endpoint to the application. Existing signed external validation exports remain attestations from a configured independent verifier, not proof that Packory executed their tests.

## Priority 2 — optional controlled MCP gateway

**MCP-G0 (experimental implementation, not a G1–G5 closure):** a single-writer,
operator-provisioned PostgreSQL read connector with encrypted credentials, exact OIDC
subject grants, named parameterized SELECT templates, result budgets, audit and a
separate MCP tool entry point. Synthetic policy tests live in
`McpManagedConnectorTests` and read-only PostgreSQL queries are exercised against
a disposable CI service in `McpConnectorPostgresTests`; real TLS, dedicated DB
role, isolated-runtime and cross-client qualification remain required. See [connector operations](mcp-connectors.md).


| ID | Work to finish | Completion criteria |
| --- | --- | --- |
| MCP-G1 | Isolated gateway deployment and versioned connection domain | Reviewed registry identity and descriptor digest, explicit allowed remote/transport, capability negotiation and lifecycle; failures cannot crash the registry process |
| MCP-G2 | Credential isolation and OAuth resource handling | Per-principal/per-upstream credentials, audience/issuer verification, secret rotation/revocation; never forward the incoming Packory bearer token to another service; no credentials in descriptors, logs or browser storage |
| MCP-G3 | Tool/resource authorization and consent | Allowlisted tools and bounded arguments/results per caller; policy decisions and denials audited; clear separation of discovery and execution; unauthorized advertised tool cannot be invoked |
| MCP-G4 | Remote/network and content controls | DNS/IP/redirect validation on every actual connection, explicit egress policy, time/size/concurrency limits and circuit breakers; test SSRF/rebinding/token exfiltration and hostile result/instruction content |
| MCP-G5 | Native end-to-end acceptance | Multiple independent remote servers and clients, denied actions, cancellation, upstream failure, credential changes and restart; measure latency/resource costs before recommending an operating topology |

## Ongoing operating and release work

Reuse MS1/MS4/MS6 and E11 for migrations, selected-provider recovery, TLS/OIDC, sanitized telemetry, capacity limits, dependency/license/SBOM and signed release artifacts. Keep library/SDK version upgrades independent and run the MCP acceptance matrix on every upgrade. Do not equate an official SDK dependency with endorsement of Packory by that SDK's maintainers.

Recommended next implementation order: **R2b/R3/R6 → R4/R5; Q2/Q3 → Q4/Q5 → Q6/Q7; P4 in parallel; P5 after feed isolation; G1–G5 after the separate runtime prerequisites**. R2a addresses the conservative inert model but does not close R2b's broader official compatibility obligation. P7 has a bounded-query implementation but requires load evidence before its capacity gate closes. Each delivery should include working end-to-end behavior, negative tests, documentation and a CI-verified commit.

## Primary specifications

- [MCP 2026-07-28 pagination](https://modelcontextprotocol.io/specification/2026-07-28/server/utilities/pagination)
- [MCP Skills Extension](https://skills.extensions.modelcontextprotocol.io/specification/stable/skills)
- [Agent Skills specification](https://agentskills.io/specification)
- [Official C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [Pinned Registry OpenAPI 2025-12-01](https://github.com/modelcontextprotocol/registry/blob/11b7b0e274d518b6f449c5ee3518a6151d9848b9/docs/reference/api/openapi.yaml)
- [Pinned generic Registry API](https://github.com/modelcontextprotocol/registry/blob/11b7b0e274d518b6f449c5ee3518a6151d9848b9/docs/reference/api/generic-registry-api.md)

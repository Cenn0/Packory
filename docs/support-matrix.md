# Packory protocol and deployment support matrix

**E0 evidence checkpoint: 21 October 2026.** E0 private hosted implementations
cover NuGet, npm, Cargo, PyPI, OCI, Maven, Go and Raw. All eight GitHub
workflows and twelve jobs passed on the implementation commit recorded in
[E0 acceptance](e0-acceptance.md), alongside all eight local native profiles
and the provider/restore results. The final local
Release build passed without warnings/errors; all 591 .NET tests passed with
PostgreSQL/S3 services enabled and zero failures or skips.
Earlier merged global/hosted evidence remains useful for its stated scope,
including Maven native consumption [#93](https://github.com/Cenn0/Packory/pull/93),
Go content-hash parity [#102](https://github.com/Cenn0/Packory/pull/102), and the
private Go native authentication fix [#109](https://github.com/Cenn0/Packory/pull/109).
A prior passing job does not qualify later changes automatically.

The Maven/Gradle row includes the later **22 October 2026 E2 profile**, with its
separate native, provider and restore evidence in [Maven acceptance](maven.md).

Local native provider parity also passed through
[smoke-feed-providers.py](../scripts/smoke-feed-providers.py) with PostgreSQL 16
and Moto S3 for npm, Cargo, Twine/pip and Helm. It reuses the native restart
and authorization scenarios and independently compares remote bytes with the
PostgreSQL digest/length ledger. This is bounded emulator evidence, separate
from real-provider qualification. The local offline restore smoke also passed
for 16 private feeds across all eight kinds. The complete Python suite passed
74 tests without failures or skips, including 37 snapshot checks, with the
configured PostgreSQL/Moto services.

This matrix records implemented contracts and observed tests, without upstream
vendor endorsement. The [MCP roadmap](mcp-roadmap.md) independently tracks global
discovery, private-feed discovery and execution-related work.

## How to read the matrix

**Implemented** means an endpoint or code path exists. **Tested** refers to the
named command, client, OS and provider combination. Broader interoperability,
operational recovery or release conclusions need their own evidence; a local
SQLite test cannot stand in for PostgreSQL/S3 or another native client.

| Protocol / feature | Implemented scope | Evidence | Remaining limits and qualification |
| --- | --- | --- | --- |
| NuGet V3 | Legacy hosted/mirror plus private hosted service index, search, paginated registration, push, flat-container files and immutable SHA-512 | API isolation/revocation negatives; .NET SDK 10.0.402 local two-feed pack/push/restore in `smoke-feed-nuget.sh`, explicit sole sources and native 401/403/404 with fresh caches | Private symbol/unlisting support, broad CLI/OS and large transitive-graph qualification |
| npm | Legacy hosted/mirror plus private scoped/unscoped publish, packuments, mutable audited dist-tags and verified tarballs | `FeedNpmTests`; npm 11.9.0 local dual-feed publish, transitive install, native deprecate/clear, restart and fresh-cache ci with native credential/deactivation negatives | Unpublish and login flows, provenance attachments, yarn/pnpm and broad native-version matrix; direct external dependency origins rejected |
| Cargo | Legacy and private alternate sparse registries, native publish, immutable crate/index bundles; scoped mutable private yank state (V14) | `FeedCargoTests` and `PostgresProviderTests`; native Cargo dual-feed publish/run/yank/undo, fresh caches, restart, cross-feed denials and offline restore checks (pending final CI on exact change) | Owner grants, crates.io replacement/proxy and broader client/OS matrix; explicit foreign dependency registries rejected |
| PyPI | Legacy and private Simple HTML/JSON, Twine upload, immutable distribution files, hash/size/version metadata and private wheel PEP 658/714 sidecars | `FeedPyPiTests`; Python 3.12, pip 26.2.1, Twine 6.2.0 local two-feed upload and transitive wheel install, restart and native denials | Bounded canonical PEP 440 without epochs/aliases; no direct-reference dependency or extra-index fallback, yank/proxy or native source-build qualification |
| OCI / Helm | Legacy and private SHA-256 blobs/manifests, mutable tags, referrers and durable scoped uploads | `FeedOciTests`; pinned Helm 3.22.0 local private push/pull/template, byte comparison, restart and native denials; legacy Docker smoke remains a separate global scenario | No alternate descriptor URLs; inline data requires matching digest/size. No qualified private Docker/ORAS matrix, Bearer issuer, blob/manifest delete, multi-node session handoff or full OCI/Helm/Kubernetes conformance; bounded referrers |
| Maven / Gradle hosted | Global/private native release and unique timestamped SNAPSHOT deployment; immutable POM/binary/classifier files, parent-inherited POM coordinates, plugin-prefix metadata, exact atomic metadata, SHA-512/SHA-256/SHA-1/MD5 sidecars and scoped reference validation | `MavenDeploymentTests`, `MavenMetadataProviderTests`, existing Maven/feed suites; Maven 3.9.11 + deploy 3.1.4, Gradle 9.8.2 and Java 17: native deploy, fresh-cache transitive build, sources, isolation and restart in `smoke-maven-deploy.sh`; SQLite and PostgreSQL/Moto restore | No multi-file atomic publication, Central proxy, complete Gradle plugin/module variant coverage or real-provider qualification |
| Go modules | Global and private immutable triplet import, native GOPROXY list/info/mod/zip/latest, archive digest and canonical local h1 | `FeedGoTests`; Go 1.24.13 local two-feed download/build, separate caches, exact 401/403 and missing module/version/feed/deactivated 404; earlier global go.sum parity evidence | GOAUTH credentials require HTTPS and authority-correct netrc; no public sumdb trust, upstream mirror, universal archive acceptance or broad client/OS matrix |
| Raw / Generic | Global and private immutable HTTP bytes, SHA-256/size/media metadata, HEAD/ranges and bounded private REST discovery | `FeedRawTests`, local curl 8.5.0 `smoke-feed-raw.sh`: exact two-feed bytes, immutable conflict, native 401/403/deactivated 404 and legacy non-leak; provider/recovery tests | Generic transport, without a native package-manager protocol; per-feed quota/retention remains open |
| Agent Skills distribution and review | Validated immutable ZIP revisions; staged offline SkillSpector scan, explicit approval under the enforcing policy, revocation/rescan and shared REST/MCP publication gates | API approval/revocation tests, `SkillArchiveSafetyTests` and native MCP approved-resource tests | Publisher ownership proof, verified signatures, complete Agent Skills conformance and independently qualified scanner coverage |
| Optional SkillSpector AI review (experimental) | Separately queued digest-bound LLM text assessment, admin-configured provider allowlist, external consent, key encryption, request reservation, manual approval gate and UI jobs/audit; independent offline scanner remains mandatory | Synthetic .NET policy/gateway tests, networkless Python worker/relay fixtures and mocked browser UI; pinned SkillSpector smoke validates the unchanged offline worker | No real provider/token billing certification, real S3 AI staging, signed scanner image, browser BFF/SSO or independent security approval; see [Skill AI](skill-ai.md) |
| Static Skill quality | [Profile `packory.skill-quality/1`](skill-quality.md): frontmatter, instruction text and recognized local Markdown references; findings/suggestions, exact digests and coverage through MCP, REST and dashboard | `SkillQualityAssessmentTests`: deterministic good/bad fixtures, resource limits, cancellation, revocation/tamper, scoped auth/rate limits and native MCP/REST parity; [dashboard regression checks](../scripts/test-mcp-dashboard.cjs) | No behavioral or script execution, complete Markdown analysis, safety/publisher certification, agent/connector runtime validation or durable assessment history |
| Managed API tokens | SHA-256 hashed expiring/revocable read/write, namespace policy and **exact per-feed read/publish grants for all eight private hosted package adapters**; scoped tokens fail closed on global native registry reads when anonymous reads are disabled | Existing API auth lifecycle integration; cross-protocol scope-denial regression suite covering NuGet/npm/Cargo/PyPI/OCI/Raw/Maven/Go | Full tenant/per-package policy model, private Skills/MCP permissions and comprehensive audit/denial coverage |
| OIDC / Keycloak | JWT resource-server validation, role/scope checks | CI disposable Keycloak 26.7.3 and signed JWT audience/scope over TLS | PKCE BFF, browser login/logout, group mapping, identity lifecycle |
| PostgreSQL | Provider abstraction + writer advisory lease | Isolated PostgreSQL CI integration | Coordinated real-provider restore, fencing after network partition |
| S3 | Conditional immutable writes, verified orphan quarantine | Moto 5.2.3 emulator and PostgreSQL/S3 CI | Real S3 conditional DELETE/PUT concurrency, versioning/retention, lifecycle, disaster restore |
| Multi-instance | Restricted PostgreSQL/S3 readers | Two-instance integration on test infrastructure | General NuGet/npm read replicas, OCI session handoff, active-active writes |
| Read-only Packory MCP | Authenticated Streamable HTTP tools/resources/prompts and Skills Extension; opaque Skill/directory pagination, exact-byte resources; complete global catalog/version traversal including Maven/Raw with approval filtering before page limits; OAuth resource metadata; separate opt-in signed validation exports | `McpProtocolTests` cover native C# SDK revisions 2025-06-18, 2025-11-25 and 2026-07-28. Merged [#101](https://github.com/Cenn0/Packory/pull/101) qualifies a bounded official TypeScript SDK v2.3.1 / Node 22 real-HTTP smoke across both era profiles, bearer negatives and read-only tool/resource/prompt calls. `McpArtifactCatalogTests`, `McpOAuthMetadataTests`, existing SDK/Keycloak and signed-evidence tests cover other slices | Broader independent clients/versions, per-feed discovery, OAuth PKCE, full schema/pagination/cancellation negatives, optional protocol features and external security/operational qualification |
| MCP Registry metadata | Conservative inert remote/package pinned `server.json` profile with SSE/HTTP/stdio metadata, safe declarative variables/icons and no execution; immutable staged review/revoke; three `/v0.1` read routes with exact/latest versions and bounded keyset pages; Packory management API, MCP tools and dashboard | `McpRegistryTests`, `McpRegistryPostgresProviderTests`, native C# MCP discovery and dashboard regression checks | Independent full official JSON Schema qualification, full official publish/status lifecycle, publisher ownership proof, signature verification, upstream synchronization, external MCP gateway and full Registry conformance |
| AI model catalog | Not implemented | No model-specific acceptance evidence | Model manifests, licensing/provenance policy, large-file capacity, client and provider qualification |

MCP catalog and Registry tests include SQLite and actual PostgreSQL profiles. The MCP PostgreSQL-specific tests require `PACKORY_TEST_POSTGRES` and explicitly skip when it is absent; a default local test run alone does not establish PostgreSQL acceptance. Local Registry approval permits metadata distribution and does not verify the publisher or the referenced server's behavior. Static quality and signed external validation exports do not mean Packory ran an artifact.

## Native client test contract

For *each exact version/OS/provider combination* record source URL, authentication mode, result and evidence link for: publish, fresh-client installation, transitive dependencies, cold cache, warm cache after client cache purge, upstream-offline, tampered bytes, namespace collision, bad auth, retries, timeouts, concurrent requests, restart/recovery and migrations. Record actual `dotnet --version`, `npm --version`, `cargo --version`, `python --version`, `pip --version`, `docker version` etc. in the CI artifact **without tokens or user data**.

Do not infer a client's version from an `ubuntu-latest` runner image. Do not claim that only one successful native CLI smoke test certifies the entire ecosystem. Fake upstream/emulator acceptance is separate from real-provider evidence.

## Deployment profiles

| Profile | Works in code | Official Packory support claim | Best practice | Security recommendation |
| --- | --- | --- | --- | --- |
| SQLite + filesystem | Standalone single process | Experimental single writer; deterministic offline backup tool available | Stop writes, verify and restore to isolated path | TLS proxy and offline hash-verified backup |
| PostgreSQL + S3 | Single writer plus restricted readers | Experimental; writer advisory lease and opt-in quarantine are tested with Moto | Coordinated versioned S3 + DB restore before adoption | Private networking, TLS VerifyFull for PG, least-privilege S3 IAM, strict checks |
| Multiple active writers | No | **Not supported** | Consider only after performance evidence and fencing | **Do not deploy** |

See [Release gates](release-gates.md) for provider and production evidence still required.

Private Raw additionally provides committed-byte/count usage and optional
per-feed limits, with concurrent writer and HTTP-client quota tests. This does
not graduate physical S3 capacity, automatic retention/GC or UI/MCP discovery.

### Pinned private npm consumer matrix

The native matrix workflow independently runs npm 11.9.0, pnpm 10.20.0 and
**Yarn Classic 1.22.22** against two isolated feeds holding different bytes at
identical scoped coordinates. Native npm publishes the synthetic transitive
fixtures. Each alternate consumer performs a cold install and a frozen-lock
install with removed node_modules and a new cache/store after server restart.
Assertions bind dependency bytes, lockfile origins and SHA-512 integrity to the
selected feed and require the frozen run to preserve the lockfile exactly.
A manifest change to an existing 2.0.0 must fail for a lock mismatch, not a
missing-package/network error, without changing the lockfile.

Run with `PACKORY_SMOKE_ALTERNATE_CLIENTS=1` and explicit pinned CLI paths; the
workflow provisions all three versions with installation scripts disabled.
Only the loopback fixture's Yarn process clears inherited proxies because
Classic can force an explicit proxy despite NO_PROXY; fixture packages never
use public dependency origins. Credentials/configurations/caches are temporary
and not exported as evidence. npm native 401/403/404 checks remain in the same
harness; this does not claim every alternate client's authorization edge case.

This is bounded client compatibility evidence, not upstream vendor endorsement,
modern Yarn (2+) support, dependency provenance verification or production
qualification. See [pnpm 10 install](https://github.com/pnpm/pnpm.io/blob/main/versioned_docs/version-10.x/cli/install.md)
and [Yarn Classic install](https://classic.yarnpkg.com/en/docs/cli/install).

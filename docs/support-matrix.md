# Packory protocol and deployment support matrix

**Evidence baseline:** 20 October 2026; merged Maven native consumer [#93](https://github.com/Cenn0/Packory/pull/93), Go parser [#94](https://github.com/Cenn0/Packory/pull/94) and Maven CI reliability [#95](https://github.com/Cenn0/Packory/pull/95). The offline ZIP inspector is not native interoperability evidence. This matrix is a record of test *coverage*, not an official certification by NuGet, npm, Docker, Cargo, PyPI or S3 vendors. **Re-run and update this file when changing the released code.**

The [20 October checkpoint](roadmap-status-2026-10-20.md) also records the MCP functional continuation. The [MCP roadmap](mcp-roadmap.md) distinguishes delivered read/discovery/quality profiles from broader Registry, client, execution and release work.

## Definitions

**Implemented:** endpoint exists. **Tested:** named CLI and operation passed in an identified workflow. **Supported by Packory:** reproducible matrix, negative cases, configuration and upgrade. **Production recommended:** also measured real-provider restore, security, release and operational drills. No protocol below currently qualifies for the last category.

| Protocol / feature | Baseline implemented | Reproducible evidence | Not currently verified or supported broadly |
| --- | --- | --- | --- |
| NuGet V3 | Legacy hosted/push/flat-container mirror and **experimental isolated private hosted NuGet feeds**; immutable SHA-512 V3/V4 metadata | CI `dotnet pack/push/restore/build`, deterministic API, private two-feed scope/duplicate-name native CLI smoke | Broad NuGet CLI versions/OS, complete registration/SemVer2, large transitive graphs, cold/warm/offline/full fault matrix |
| npm | Hosted, scoped metadata, tarball mirror, integrity | CI native `npm publish/install/ci`, API tests; setup-node 22 runtime | Complete packuments/dist-tag semantics, yarn/pnpm, client version pinning, offline lockfile matrix |
| Cargo | Hosted alternate sparse registry | Native `cargo publish` and consumption in hosted/CI smoke | cargo versions unpinned, yank, crates.io replacement proxy |
| PyPI | Hosted Simple API HTML/JSON + upload | Native pip hosted transitive wheel install with Python 3.12 and 3.13 contexts | Twine versions, multiple files/metadata sidecars, yanks, PyPI proxy, multiple operating systems |
| OCI | Hosted v2 blobs, manifests, tags, referrers | Docker login/push/pull scratch-image and pinned Helm 3 OCI push/pull/show/template smoke plus API integration | ORAS, standard bearer-token server, broad Helm/Kubernetes conformance, cross-restart chunk sessions and client/OS matrix |
| Maven (release hosted files) | Global immutable release assets, POM GAV validation, version metadata and SHA-256; V6 | .NET API negatives and native Maven 3 dependency consumption with direct/transitive byte proof in merged PR #93; Maven Central bootstrap network retry in #95 | No native deploy or SNAPSHOT, no Maven Central proxy, no Gradle variant conformance, no independent feed isolation |
| Go Modules (offline groundwork **only**) | GOPROXY coordinate parser and offline ZIP archive preflight; **no Go HTTP endpoint** | xUnit case/major-version and negative path/archive tests; **no native Go CLI proof** | **Not a Go registry:** no triplet persistence, sumdb policy, native install, private feed or upstream proxy |
| Raw / Generic | Global immutable hosted bytes and SHA-256 metadata (V5) | PR #90: .NET negative/identity tests, real-process HTTP smoke, SQLite and PostgreSQL/S3 emulator matrix | No per-feed isolation, no native client protocol, no real-provider restore or production recommendation |
| Agent Skills distribution and review | Validated immutable ZIP revisions; staged offline SkillSpector scan, explicit approval under the enforcing policy, revocation/rescan and shared REST/MCP publication gates | API approval/revocation tests, `SkillArchiveSafetyTests` and native MCP approved-resource tests | Publisher ownership proof, verified signatures, complete Agent Skills conformance and independently qualified scanner coverage |
| Static Skill quality | [Profile `packory.skill-quality/1`](skill-quality.md): frontmatter, instruction text and recognized local Markdown references; findings/suggestions, exact digests and coverage through MCP, REST and dashboard | `SkillQualityAssessmentTests`: deterministic good/bad fixtures, resource limits, cancellation, revocation/tamper, scoped auth/rate limits and native MCP/REST parity; [dashboard regression checks](../scripts/test-mcp-dashboard.cjs) | No behavioral or script execution, complete Markdown analysis, safety/publisher certification, agent/connector runtime validation or durable assessment history |
| Managed API tokens | SHA-256 hashed expiring/revocable read/write, namespace policy and **exact per-feed read/publish grants for private NuGet** | Existing API auth lifecycle integration | Full tenant policy model, cross-protocol feed IAM and audit/denial coverage |
| OIDC / Keycloak | JWT resource-server validation, role/scope checks | CI disposable Keycloak 26.7.3 and signed JWT audience/scope over TLS | PKCE BFF, browser login/logout, group mapping, identity lifecycle |
| PostgreSQL | Provider abstraction + writer advisory lease | Isolated PostgreSQL CI integration | Coordinated real-provider restore, fencing after network partition |
| S3 | Conditional immutable writes, verified orphan quarantine | Moto 5.2.3 emulator and PostgreSQL/S3 CI | Real S3 conditional DELETE/PUT concurrency, versioning/retention, lifecycle, disaster restore |
| Multi-instance | Restricted PostgreSQL/S3 readers | Two-instance integration on test infrastructure | General NuGet/npm read replicas, OCI session handoff, active-active writes |
| Read-only Packory MCP | Authenticated Streamable HTTP tools/resources/prompts and Skills Extension; opaque Skill/directory pagination, exact-byte resources; complete global catalog/version traversal including Maven/Raw with approval filtering before page limits; OAuth resource metadata; separate opt-in signed validation exports | `McpProtocolTests` cover native C# SDK revisions 2025-06-18, 2025-11-25 and 2026-07-28; `McpArtifactCatalogTests`, `McpOAuthMetadataTests`, existing SDK/Keycloak and signed-evidence scope tests | Independent SDK/client matrix, per-feed discovery, OAuth client/authorization-server PKCE qualification, optional unadvertised protocol features and external security/operational qualification |
| MCP Registry metadata | Remote-only pinned `server.json` profile; immutable staged review/revoke; three `/v0.1` read routes with exact/latest versions and bounded keyset pages; Packory management API, MCP tools and dashboard | `McpRegistryTests`, `McpRegistryPostgresProviderTests`, native C# MCP discovery and dashboard regression checks | Broader manifests, full official publish/status lifecycle, publisher ownership proof, signature verification, upstream synchronization, external MCP gateway and full Registry conformance |
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

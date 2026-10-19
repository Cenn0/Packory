# Packory protocol and deployment support matrix

**Evidence baseline:** [main 5f0c6c4](https://github.com/Cenn0/Packory/commit/5f0c6c44d0fba1de0ce41b292f06436aa5801687), 17 October 2026. This matrix is a record of test *coverage*, not an official certification by NuGet, npm, Docker, Cargo, PyPI or S3 vendors. **Re-run and update this file when changing the released code.**

## Definitions

**Implemented:** endpoint exists. **Tested:** named CLI and operation passed in an identified workflow. **Supported by Packory:** reproducible matrix, negative cases, configuration and upgrade. **Production recommended:** also measured real-provider restore, security, release and operational drills. No protocol below currently qualifies for the last category.

| Protocol / feature | Baseline implemented | Reproducible evidence | Not currently verified or supported broadly |
| --- | --- | --- | --- |
| NuGet V3 | Legacy hosted/push/flat-container mirror and **experimental isolated private hosted NuGet feeds**; immutable SHA-512 V3/V4 metadata | CI `dotnet pack/push/restore/build`, deterministic API, private two-feed scope/duplicate-name native CLI smoke | Broad NuGet CLI versions/OS, complete registration/SemVer2, large transitive graphs, cold/warm/offline/full fault matrix |
| npm | Hosted, scoped metadata, tarball mirror, integrity | CI native `npm publish/install/ci`, API tests; setup-node 22 runtime | Complete packuments/dist-tag semantics, yarn/pnpm, client version pinning, offline lockfile matrix |
| Cargo | Hosted alternate sparse registry | Native `cargo publish` and consumption in hosted/CI smoke | cargo versions unpinned, yank, crates.io replacement proxy |
| PyPI | Hosted Simple API HTML/JSON + upload | Native pip hosted transitive wheel install with Python 3.12 and 3.13 contexts | Twine versions, multiple files/metadata sidecars, yanks, PyPI proxy, multiple operating systems |
| OCI | Hosted v2 blobs, manifests, tags, referrers | Local Docker login/push/pull scratch-image smoke and API integration | ORAS, Helm, standard bearer-token server, cross-restart chunk sessions, upload resume, client/OS matrix |
| Raw / Generic | Global immutable hosted bytes and SHA-256 metadata (V5) | Dedicated integration tests added in V4 PR; pending CI confirmation | No per-feed isolation, no native client protocol, no real-provider restore or production recommendation |
| Agent Skills | ZIP validation + immutable named versions | API upload/download and malicious-archive regression subset | Verified provenance/publisher, approval workflow, signatures, complete Agent Skills standard conformance |
| Managed API tokens | SHA-256 hashed expiring/revocable read/write, namespace policy and **exact per-feed read/publish grants for private NuGet** | Existing API auth lifecycle integration | Full tenant policy model, cross-protocol feed IAM and audit/denial coverage |
| OIDC / Keycloak | JWT resource-server validation, role/scope checks | CI disposable Keycloak 26.7.3 and signed JWT audience/scope over TLS | PKCE BFF, browser login/logout, group mapping, identity lifecycle |
| PostgreSQL | Provider abstraction + writer advisory lease | Isolated PostgreSQL CI integration | Coordinated real-provider restore, fencing after network partition |
| S3 | Conditional immutable writes, verified orphan quarantine | Moto 5.2.3 emulator and PostgreSQL/S3 CI | Real S3 conditional DELETE/PUT concurrency, versioning/retention, lifecycle, disaster restore |
| Multi-instance | Restricted PostgreSQL/S3 readers | Two-instance integration on test infrastructure | General NuGet/npm read replicas, OCI session handoff, active-active writes |
| Read-only Packory MCP | Authenticated Skill/MCP catalog and scoped signed validation findings; OIDC/HTTPS serves RFC 9728 resource metadata and Bearer challenge | MCP SDK/Keycloak CI, HTTP discovery tests, synthetic signed evidence and scope checks | OAuth 2.1 client/authorization-server PKCE qualification, full interoperability, external security assessment and production certification still missing
| MCP Registry metadata / Raw / AI Models | Experimental remote-only pinned `server.json` subset, immutable staged review/revoke, read-only REST and MCP discovery UI; Raw and model catalog remain unimplemented | .NET metadata/negative/approval regression tests, HTTP MCP client integration, database persistence; provider-level validation remains limited | No official Registry `/v0.1` API, publisher ownership proof, signed provenance, full schema/import, remote gateway, Raw or model catalog, independent security qualification |

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

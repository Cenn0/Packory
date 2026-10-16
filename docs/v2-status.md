# Packory V2 – implementation and support status

Current source state: V2 modular-monolith foundation, additive to the earlier API. This is a **feature implementation status**, not a product certification. CI validates builds and deterministic integration contracts only.

| Feature | Implemented in source | Officially Packory-supported | Remaining evidence / work |
|---|---|---|---|
| SQLite V2 feed schema and migration | Yes, additive | Tested in integration tests | Migrate legacy packages through an explicit import utility; backup/restore drill |
| Local SHA-256 CAS with staging | Yes | Integration-test level | Crash fault injection, 32 concurrent cold downloads and corruption sweeps |
| Hosted Raw immutable files | Yes | Integration-test level | Large-file load tests, range and operational limits |
| Scoped tokens, expiry and revocation | Yes | Integration-test level | Full permission matrix, key rotation and audit retention |
| Repository-level public/private reads | Yes | Integration-test level | Cross-tenant and browser-origin abuse testing |
| NuGet hosted/proxy V2 routes | Yes, subset, registration metadata rewritten | **Not fully qualified** | Live client smoke, cold/warm/offline transitive graphs and full registration compatibility |
| npm hosted/proxy V2 routes | Yes, subset | **Not fully qualified** | `npm ci` scopes, lockfiles, all metadata variants, error cases |
| MCP Registry metadata and approval | Yes, subset | **Experimental** | Pin OpenAPI revision, schema validation and publisher ownership |
| Agent Skills | Legacy ZIP import | **Legacy subset** | Git provenance, schema validation, V2 feed integration and approval workflow |
| Audit log and persistent maintenance queue | Yes | Integration-test level | Retry/lease behavior under crash and retention/quota policy |
| Dashboard | Existing JS with V2 administration pages | Functional UX prototype | Usability/E2E and accessible testing; React/TypeScript migration |
| Optional Keycloak/OIDC BFF | Implemented with Authorization Code + PKCE, cookies and CSRF | **Not production-qualified** | Real Keycloak E2E, role mapping abuse and session lifecycle tests |
| Cargo sparse index + downloads | Yes, hosted experimental | **Not fully qualified** | Standard `cargo publish`, Source Replacement and `cargo build` E2E |
| PyPI Simple HTML + hosted wheels/sdists | Yes, hosted experimental | **Not fully qualified** | PEP 691 JSON, Twine upload, real `pip` E2E |
| OCI distribution / model bundles | Planned | **Not implemented** | Full `/v2/`, layers/manifests/referrers and large-object transfer |
| PostgreSQL / S3 | Planned | **Not implemented** | Separate provider implementation and qualification |
| Packory MCP server / MCP gateway | Planned | **Not implemented** | MCP C# SDK integration, scopes and threat model |

## Terminology

- **Works in principle:** technically feasible or one scenario shown, not a support statement.
- **Implemented:** source exists with reachable routes. This is not automatically compatible with every native client.
- **Officially supported by Packory:** versioned client test matrix, error/recovery tests and published operational limits all passed.
- **Best practice:** validate protocol-native behavior, minimize privileges, immutable bytes and rigorous operations testing.
- **Security-side recommendation:** requires explicit threat-model validation; a green build alone does not establish security.

When changing this matrix, cite test coverage or observed CI evidence in the PR. Never claim HA, full protocol parity or production safety from a successful build alone.

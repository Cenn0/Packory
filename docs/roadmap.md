# Packory roadmap and release gates

Packory V2 introduces isolated repositories, a common CAS, control-plane authorization, a native Raw feed, NuGet/npm feed adapters, MCP metadata, audit and persistent maintenance jobs. The design target remains broader than the current implementation. See [V2 status](v2-status.md) for each actually delivered feature and tested support level.

## Release gates

1. **Foundation (V2 in source):** durable SQLite migrations, immutable CAS, feed isolation, Raw hosted, token issuance/revocation and audit. CI: deterministic integration tests and container build.
2. **Native protocols:** complete NuGet V3 and npm conformance, transitive cold/warm/offline actual client installs, metadata handling, typed origin errors and concurrency tests.
3. **Enterprise access:** optional OIDC/Keycloak with strict subject-based mapping and CSRF-secured dashboard sessions; complete Keycloak E2E and role-change tests required before support.
4. **AI distribution:** full Agent Skills schema + Git provenance/approval, MCP Registry OpenAPI pinned with publishing/namespace ownership, Packory MCP server read-only.
5. **More distribution formats:** native Cargo, PyPI and OCI adapters (not format-emulated Raw uploads), optional content-signature attestations and larger model bundles.
6. **Operations:** bounded metadata TTL, comprehensive reconciliation and GC, fault injection, checksum scrub, backup/restore drill, tenant quotas and horizontal read-only scaling.
7. **Secondary storage profile:** PostgreSQL and S3, only after separate migrations, locking and provider tests; no unsupported multi-writer SQLite deployments.

## Engineering rules

- An implemented route is not automatically officially supported. Native-client tests must use **Packory as the only source**, with caches cleared and Origin blocked for offline cases.
- Security functionality must be tested for cross-feed IDOR, token expiry/revocation, SSRF and package identity/hash mismatch. Runtime must never run untrusted Skill/MCP/package code.
- Backwards compatibility must be explicit. Existing `/nuget`, `/npm` and `/api/skills` remain while V2 source migration is being built; avoid silently conflating old and new blobs.
- A version is ready for a release label only after CI and protocol acceptance gates actually pass.

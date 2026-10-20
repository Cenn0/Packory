# Packory MCP and SkillSpector integration

**Implementation checkpoint: 20 October 2026.** Packory provides authenticated MCP artifact discovery, approved Skill distribution, reviewed Registry metadata and static Skill quality assessment. The supported protocol revisions and validation boundaries are documented below; broader client conformance and the remaining Registry/assessment work are tracked in the [MCP delivery roadmap](mcp-roadmap.md).

## Architecture

Packory remains a single-writer ASP.NET Core / .NET 10 modular monolith.

The Streamable HTTP endpoint at `/mcp` uses the official MCP C# SDK. It provides read-only artifact/Skill discovery, integrity and quality tools, reviewed MCP Registry discovery, resources, the `review_skill` and `discover_packages` prompts, and MCP Skills Extension `skills/list` / `skills/get`. The extension advertises `directoryRead=true` and implements `resources/directory/read` for approved ZIP roots and nested directories, including explicitly empty directories. Listings contain direct child names, canonical escaped URIs and MIME types (`inode/directory` for subdirectories), with `cacheScope=private` and `ttlMs=0`.

`skills/list` uses opaque keyset pages of 25 Skills. Directory pages return at most 100 children and bind their cursors to the exact archive digest and directory URI. Repeat the same filters/resource and pass `nextCursor`; unknown or mismatched cursors return MCP `InvalidParams` (`-32602`). Resource paths use individually escaped segments in `skill://packory/VERSION/NAME/PATH`. Traversal, encoded separators and noncanonical spellings are rejected before URI normalization. Valid UTF-8 resource text and binary resource data preserve the original bytes; invalid UTF-8 is delivered as binary even when its filename has a text extension.

`search_artifacts(query, limit=30, kind?, cursor?)` and `artifact_versions(kind, name, limit=100, cursor?)` provide keyset traversal beyond the former 100-row ceiling. They share the implemented ecosystem catalog, including global Maven and Raw. Skill approval is filtered in the database before the page limit. Private-feed rows remain excluded. Artifact versions use ordinal lexical order; the MCP tool does not reinterpret ecosystem-specific version semantics or select a universal latest version.

`assess_skill_quality(name, version)` returns the same deterministic profile as the read-only REST endpoint and dashboard. It reports findings, suggestions, exact digests, independent dimensions and explicit coverage; it never changes approval or runs submitted content. See [Skill quality](skill-quality.md). Optional signed external validation exports retain their separate configured verifier and permission checks.

This is a distribution server, not a remote tool-execution gateway. It does not launch untrusted Skill scripts or remote MCP servers. Mutations are intentionally excluded from the MCP attack surface.

## OAuth Protected Resource Metadata (OIDC/HTTPS only)

For an HTTPS public origin with a configured OIDC authority/audience, the Packory MCP resource identifier is the configured public origin plus `/mcp`. Packory exposes the public RFC 9728 metadata document at `GET /.well-known/oauth-protected-resource/mcp` with the exact resource URL, configured OIDC issuer (`authorization_servers`), `bearer_methods_supported=["header"]` and minimum `scopes_supported=["packory.read"]`.

An MCP HTTP 401 response for missing or invalid Bearer credentials includes an absolute `resource_metadata` URL in `WWW-Authenticate`, with `scope="packory.read"`. A 403 for missing scope or restricted managed credentials is not an OAuth challenge.

**Static tokens are not OAuth:** without OIDC or without an HTTPS public origin, Packory does not advertise an OAuth authorization server or expose a protected-resource document. Static Bearer token access can still function, but cannot be called an OAuth-conformant profile.

Resource metadata only provides discovery; the external identity provider is responsible for OAuth 2.1 token issuance, registration, authorization-code/PKCE flow and client interoperability. Packory validates JWT issuer, audience, signature, expiry and explicit scope, but does not implement the authorization server or interactive browser SSO. Independently qualify the identity-provider flow before claiming end-to-end compliance.

References: [RFC 9728](https://www.rfc-editor.org/rfc/rfc9728) and [MCP Authorization, 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization).

## Security pipeline

New ZIP imports are staged under a content-derived SHA-256 key and enter a persistent QUEUED state. They cannot be publicly listed, downloaded or used as MCP resources before approval. A background coordinator writes an immutable scan snapshot to the sidecar's input volume and waits for machine-readable JSON results on a separate output volume.

The NVIDIA SkillSpector sidecar runs in static mode with --no-llm, no network, read-only filesystem, no ambient production credentials, CPU/memory/PID limits and no privileges. It does not execute skills. A completed scan becomes NEEDS_REVIEW if it is reviewable, QUARANTINED for HIGH/CRITICAL aggregate severity, severe individual findings or unknown suppressed findings, or SCAN_FAILED for incomplete/malformed/failed runs. A low numerical score never automatically approves a skill.

Only a privileged administrator can approve a NEEDS_REVIEW result, with an explicit reason; approval also checks that the exact staged bytes match the expected SHA-256 before making them available. Revocation or rescan immediately blocks subsequent REST/MCP reads and is recorded in an audit table. Scan state is stored in SQLite or PostgreSQL; staging uses filesystem or S3, according to existing backend selection.

Use the administrator-only REST endpoints /api/security/skills and /api/security/skills/{name}/{version} to inspect results. POST /api/security/skills/{name}/{version}/approve, /revoke and /rescan accept JSON fields sha256 (64 lowercase hex characters) and reason (3 to 512 characters). The dashboard Security Center uses these APIs via the existing in-memory token entry.

**Important:** This token field is not a server-side browser BFF/OIDC session. Secure browser administration remains a separate roadmap gate. Do not store API tokens in a browser's long-lived local storage.

## Deployment

The optional default Compose stack builds the Packory API plus a pinned SkillSpector worker. Set PACKORY_ADMIN_TOKEN, PACKORY_READ_TOKEN and PACKORY_WRITE_TOKEN to unique cryptographically random values, outside source control. On a local loopback host, build and launch with docker compose up -d --build.

The MCP endpoint requires an authenticated bearer token even when anonymous REST reads are enabled. Only same-origin browser requests are accepted. For external use, deploy behind properly configured HTTPS with host validation and DNS-rebinding protections, and grant least-privilege read tokens to MCP clients.

Imported skills return 202 QUEUED. Review the scanner report before approving; only APPROVED revisions become discoverable. Blocking or failed results do not become automatically available. Native official C# SDK tests exercise protocol revisions **2025-06-18**, **2025-11-25** and **2026-07-28**, including initialize-era hybrid sessions and current stateless discovery. This matrix records those tested client/protocol combinations; another SDK and broader client qualification remain MCP-P4.

## Upgrade: explicitly backfill previously published Skills

Existing pre-scanner Skill versions have no approval record; they must **not** be silently grandfathered. After a tested backup, enable scanning and start the two services. The existing Skills will be temporarily unavailable for public download and MCP distribution until approved.

An administrator may POST to /api/security/skills/backfill?limit=25 with their API bearer credential. This checks existing ZIP content, verifies the manifest, copies it to immutable staging, and queues the matching SHA-256 for scanning. The response contains queued, skipped, inspected and remaining counts. Repeat until remaining is zero. Entries with malformed, missing or oversized ZIPs are skipped and never implicitly approved; examine them separately without disabling the security gate. Every completed scan still needs an explicit review decision before distribution.

Example using a runtime-only administrator token:

    curl -fsS -X POST -H "Authorization: Bearer $PACKORY_ADMIN_TOKEN" \
      "http://127.0.0.1:8080/api/security/skills/backfill?limit=25"

Backfill is idempotent for previously queued revisions and does not rewrite the existing immutable published ZIP. Do not rely on this administrative API as a full automatic upgrade or disaster-recovery system.

## Current limitations / remaining release gates

- Existing published Skills remain unavailable under default enforcement until explicitly scanned and approved. Administrators can enqueue legacy revisions using the documented backfill API below; unscannable/missing revisions remain unavailable and need operator investigation.
- A worker crash, missing output volume or a scan contract change can leave a revision blocked. This is preferable to accidental publication but needs operational recovery/runbooks and retry limits.
- No full PostgreSQL/S3 restore certification, multi-writer guarantees, BFF-based administration, broad independent MCP client matrix, signed scanner image, locked transitive Python dependency tree or complete license/SBOM review yet.
- NVIDIA SkillSpector source is pinned to v2.13.0 / Git commit 091a509b3d6ba8ff5be528966e1f43c2e68cf214, but its transitive dependencies must be verified against the project's preferred permissive-license policy. Its static-only offline mode uses fallback vulnerability data rather than live OSV requests.
- Scanning is not a security certification. Encrypted payloads, novel prompt injections, non-English attacks and runtime-only behavior may be missed.
- This is a public repository. Never commit personal emails, private credentials, operational logs with secrets or real internal addresses.

## Reproducible verification and remaining coverage

Run dotnet restore Packory.slnx; dotnet build Packory.slnx -c Release --no-restore -warnaserror; dotnet test Packory.slnx -c Release --no-build; node --check src/Packory/wwwroot/app.js; python3 -m py_compile workers/skillspector/worker.py; docker compose config; docker compose build.

The ordinary .NET suite covers native HTTP MCP clients, current metadata, capability discovery, pagination, approval/revocation, byte tampering, canonical URIs, strict errors and static quality assessment. PostgreSQL CI executes `McpRegistryPostgresProviderTests` and `McpArtifactCatalogTests` with an actual provider; those provider-specific facts explicitly skip without a configured database. Dashboard browser regressions use synthetic API responses to test the actual JS/CSS and do not replace the backend or scanner tests.

The pinned scanner Compose smoke, provider recovery and independent client/behavioral coverage remain separate evidence. Only record a test as passed after executing it for the relevant source revision and environment.

References: NVIDIA SkillSpector https://github.com/NVIDIA/SkillSpector ; MCP C# SDK https://github.com/modelcontextprotocol/csharp-sdk ; MCP Skills Extension https://skills.extensions.modelcontextprotocol.io/specification/stable/skills ; Packory skill security roadmap skill-security-roadmap.md .

## Authenticated MCP skill byte verification (October 2026)

Both the REST ZIP and `skill.md` delivery endpoints and MCP `skills/get` /
`resources/read` now materialize the current bounded ZIP bytes and compare their
SHA-256 against the server-side `APPROVED` revision **before distributing those
bytes**. A storage replacement after approval fails closed even if an S3/local
cache file is readable. REST returns 404 for inaccessible content. MCP callers
can inspect `verify_skill_integrity(name, version)`, which reports either an
approved immutable digest, `integrity_unavailable` for an inaccessible/mismatched
published revision, or `unavailable` for non-published/revoked revisions.
No raw scanner findings, unapproved ZIPs or executable actions are returned.

**Limits:** each read verifies a bounded object and incurs hashing and
in-memory materialization; performance and multi-node caching are not certified.
`skills/list` bounds the returned page but currently loads the full Skill inventory
and checks approval per revision. Provider-side work bounds are tracked as MCP-P7;
the global artifact catalog already applies its page and approval predicates in SQL.
The returned `safeToExecute=false` is deliberate: a human approval and
matching digest authenticate the reviewed bytes, not their runtime behavior.
MCP hosts must still compare resource digests to the Skills Extension manifest
and apply their own user-approval and sandbox policies. Enabling compatibility
mode by setting `PACKORY_SKILL_SECURITY_ENFORCED=false` disables this policy
check and is not recommended for untrusted registries.

## MCP bearer credentials and scanner report parsing (19 October 2026)

`/mcp` now requires an explicit `Authorization: Bearer ...` credential.
NuGet's separate legacy Basic and `X-NuGet-ApiKey` authentication mechanisms
are not accepted on the MCP transport. Synthetic API tests exercise these
negative paths without exposing credentials.

The offline SkillSpector bridge rejects duplicate/ambiguous JSON properties,
non-finite numeric values, unexpected queue-manifest fields and non-object
scanner reports. Scanner stdout is spooled to bounded local scratch storage
rather than unbounded Python memory; any output exceeding 1 MiB fails closed.
These are parser and isolation hardening measures, not proof of scanner accuracy
against unknown malicious skills. The pinned container smoke remains a separate
integration test.

## Skills directory navigation

For a security-approved Skill whose manifest URI is
`skill://packory/1.0.0/example/SKILL.md`, MCP clients can send
`resources/directory/read` for `skill://packory/1.0.0/example`
or an existing directory such as `.../references`. Only immediate
children are listed. Exact approved ZIP bytes are reverified before
any directory response. Continue pages with `nextCursor`; cursors from another
directory or archive revision are rejected. File URIs, traversal and nonexistent directories
fail closed. The capability is optional in the Skills Extension; this
implementation is covered by synthetic .NET tests and a real scanner
Compose/MCP smoke but is **not** general production protocol certification.

## Global MCP grants and private-feed tokens

MCP exposes a registry-wide artifact and approved-Skill catalog. Therefore,
a revocable **managed token with a feed or publication namespace grant**
is **not** a general MCP credential: MCP returns HTTP 403 even if the token
is otherwise valid for its native private NuGet feed. Unscoped managed read
tokens and explicit global read/admin access remain supported. This boundary
prevents a narrowly granted feed publisher/reader from gaining visibility
through the global MCP search or Skills tools. Feed-scoped tokens continue
to authenticate exclusively against their intended native feed routes.

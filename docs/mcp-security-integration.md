# Experimental Packory MCP and SkillSpector integration

**Status:** experimental integration. Build and tests are tracked by PR/commit in GitHub Actions. Production readiness and full interoperability are not implied by green CI.

## Architecture

Packory remains a single-writer ASP.NET Core / .NET 10 modular monolith.

The new MCP Streamable HTTP endpoint at /mcp uses the official MCP C# SDK and provides read-only tools (search_artifacts, artifact_versions, list_approved_skills, skill_metadata), resources (skill files and catalog information), prompts (review_skill, discover_packages), and MCP Skills Extension methods (skills/list, skills/get). The Skills extension advertises directoryRead=false. Skill files are served as resources with SHA-256 digests, bounded file counts and content sizes. The URI format is skill://packory/VERSION/NAME/SKILL.md.

This is a distribution server, not a remote tool-execution gateway. It does not launch untrusted Skill scripts or remote MCP servers. Mutations are intentionally excluded from the MCP attack surface.

## Security pipeline

New ZIP imports are staged under a content-derived SHA-256 key and enter a persistent QUEUED state. They cannot be publicly listed, downloaded or used as MCP resources before approval. A background coordinator writes an immutable scan snapshot to the sidecar's input volume and waits for machine-readable JSON results on a separate output volume.

The NVIDIA SkillSpector sidecar runs in static mode with --no-llm, no network, read-only filesystem, no ambient production credentials, CPU/memory/PID limits and no privileges. It does not execute skills. A completed scan becomes NEEDS_REVIEW if it is reviewable, QUARANTINED for HIGH/CRITICAL aggregate severity, severe individual findings or unknown suppressed findings, or SCAN_FAILED for incomplete/malformed/failed runs. A low numerical score never automatically approves a skill.

Only a privileged administrator can approve a NEEDS_REVIEW result, with an explicit reason; approval also checks that the exact staged bytes match the expected SHA-256 before making them available. Revocation or rescan immediately blocks subsequent REST/MCP reads and is recorded in an audit table. Scan state is stored in SQLite or PostgreSQL; staging uses filesystem or S3, according to existing backend selection.

Use the administrator-only REST endpoints /api/security/skills and /api/security/skills/{name}/{version} to inspect results. POST /api/security/skills/{name}/{version}/approve, /revoke and /rescan accept JSON fields sha256 (64 lowercase hex characters) and reason (3 to 512 characters). The dashboard Security Center uses these APIs via the existing in-memory token entry.

**Important:** This token field is not a server-side browser BFF/OIDC session. Secure browser administration remains a separate roadmap gate. Do not store API tokens in a browser's long-lived local storage.

## Deployment

The optional default Compose stack builds the Packory API plus a pinned SkillSpector worker. Set PACKORY_ADMIN_TOKEN, PACKORY_READ_TOKEN and PACKORY_WRITE_TOKEN to unique cryptographically random values, outside source control. On a local loopback host, build and launch with docker compose up -d --build.

The MCP endpoint requires an authenticated bearer token even when anonymous REST reads are enabled. Only same-origin browser requests are accepted. For external use, deploy behind properly configured HTTPS with host validation and DNS-rebinding protections, and grant least-privilege read tokens to MCP clients.

Imported skills return 202 QUEUED. Review the scanner report before approving; only APPROVED revisions become discoverable. Blocking or failed results do not become automatically available. The MCP endpoint supports the modern 2026-07-28 protocol and older initialize-era clients through hybrid session handling in the official SDK.

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
- No full PostgreSQL/S3 restore certification, multi-writer guarantees, BFF-based administration, live MCP compatibility matrix, signed scanner image, locked transitive Python dependency tree or complete license/SBOM review yet.
- NVIDIA SkillSpector source is pinned to v2.13.0 / Git commit 091a509b3d6ba8ff5be528966e1f43c2e68cf214, but its transitive dependencies must be verified against the project's preferred permissive-license policy. Its static-only offline mode uses fallback vulnerability data rather than live OSV requests.
- Scanning is not a security certification. Encrypted payloads, novel prompt injections, non-English attacks and runtime-only behavior may be missed.
- This is a public repository. Never commit personal emails, private credentials, operational logs with secrets or real internal addresses.

## Evidence to collect before release

Run dotnet restore Packory.slnx; dotnet build Packory.slnx -c Release --no-restore -warnaserror; dotnet test Packory.slnx -c Release --no-build; node --check src/Packory/wwwroot/app.js; python3 -m py_compile workers/skillspector/worker.py; docker compose config; docker compose build.

Add real sidecar smoke tests, synthetic malicious Skills, interrupted jobs, stale artifacts, backfill/upgrade, auth bypass checks and protocol interoperability against old and current MCP clients. Do not mark tests green without executing them. Update support claims only after the tests are available and pass.

References: NVIDIA SkillSpector https://github.com/NVIDIA/SkillSpector ; MCP C# SDK https://github.com/modelcontextprotocol/csharp-sdk ; MCP Skills Extension https://skills.extensions.modelcontextprotocol.io/specification/stable/skills ; Packory skill security roadmap skill-security-roadmap.md .

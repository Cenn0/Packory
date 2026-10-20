# Packory documentation

[← English README](../README.md) · [Deutsch](../README.de.md)

Packory is a **self-hosted registry hub in active development**. Current hosted and cached capabilities span NuGet, npm, Cargo, PyPI, OCI, global Maven release files, global Raw artifacts and Agent Skills, each with its own compatibility limitations. The read-only MCP server, reviewed MCP Registry metadata and static Skill quality assessment have separate documented functional profiles. The support matrix records their tested scope and remaining gaps; no complete upstream interoperability or production certification is implied. Go has offline validation groundwork only, not a native registry.

| Goal | Guide |
| --- | --- |
| Start locally | [Quick start](../README.md#quick-start) |
| Native APIs, access and examples | [API](api.md) |
| Actual runtime topology and persistence | [Architecture](architecture.md) |
| Versioned architecture decision | [ADR-001 V3 boundaries](adr/0001-v3-platform-boundaries.md) |
| Tested native clients and deployment profiles | [Support matrix](support-matrix.md) |
| Production blockers and real-provider recovery | [Release gates](release-gates.md) |
| Deployment, TLS, tokens and backups | [Operations](operations.md) |
| Evidence-based priorities and acceptance | [Roadmap V3](roadmap.md) |
| Additional package ecosystems and precise implementation status | [Roadmap V4](roadmap-v4.md) |
| Delivered MCP functions, remaining Registry/protocol work, quality and optional gateway | [MCP delivery roadmap](mcp-roadmap.md) |
| Experimental Maven release files and unsupported native deploy | [Maven](maven.md) |
| Go GOPROXY parser and ZIP preflight (**no registry endpoint**) | [Go E3 groundwork](go-proxy.md) |
| Go GOPROXY parser groundwork (no serving endpoint) | [Go proxy](go-proxy.md) |
| Experimental hosted Raw API and access limits | [Raw / Generic](raw-registry.md) |
| Current V4/MS5 progress, MCP continuation and outstanding gates | [20 October 2026 checkpoint](roadmap-status-2026-10-20.md) |
| Earlier delivery evidence and unfinished V3 gates | [18 October 2026 checkpoint](roadmap-status-2026-10-18.md) |
| MCP and NVIDIA SkillSpector integration | [MCP and Skill Security](mcp-security-integration.md) |
| Reviewed remote MCP server.json metadata, v0.1 read API and discovery | [MCP Registry Catalog](mcp-registry-catalog.md) |
| Static Skill findings, improvement suggestions, source digests and coverage through MCP/REST/UI | [Skill quality](skill-quality.md) |
| Merged implementation slices and explicitly open gates | [Delivery evidence](delivery-evidence-2026-10-17.md) |
| Historical Agent Skill scanning and Security UI plan, with current delivery links | [Skill Security Roadmap (NVIDIA SkillSpector)](skill-security-roadmap.md) |
| Cargo and PyPI specifics | [Cargo / PyPI](cargo-pypi.md) |
| OCI capabilities | [OCI](oci.md) |
| S3 single-writer recovery limitations | [S3](s3.md) |
| OIDC / optional Keycloak | [OIDC](oidc-keycloak.md) |
| Contribute/report defects | [Contributing](../CONTRIBUTING.md) |
| Report security issues privately | [Security](../SECURITY.md) |

## Evidence is scoped

- **Implemented:** named routes and storage/authentication code exist on main.
- **Tested:** consult CI workflow commit checks and [the versioned matrix](support-matrix.md), including local fake origins and Moto.
- **Independent assessments:** publication approval, artifact integrity, static quality, signed external validation evidence and functional execution are separate claims. The [quality profile](skill-quality.md) reports its coverage and omissions explicitly.
- **Not guaranteed:** full client protocol parity, browser SSO/BFF, real S3 backup and restore, active-active/multi-writer safety or production readiness.
- **Security best practice:** HTTPS, least-privilege runtime credentials outside Git, namespace reservations, explicit operational backup and isolated restore drills.

No screenshot is published until the source is verified to contain no private project, identities, secrets or addresses.

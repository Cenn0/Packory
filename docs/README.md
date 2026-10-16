# Packory documentation

[← English README](../README.md) · [Deutsch](../README.de.md)

Packory is a self-hosted **MVP** for NuGet, npm and versioned agent skills, not a production-certified replacement for either complete upstream registry protocol.

| Goal | Guide |
| --- | --- |
| Get started locally | [Quick start](../README.md#quick-start) |
| Protocol endpoints, authorization and examples | [API](api.md) |
| Modular architecture and storage consistency | [Architecture](architecture.md) |
| Deployment, TLS, static tokens and backups | [Operations](operations.md) |
| Supported MVP features and future work | [Roadmap](roadmap.md) |
| Contribute and report a defect | [Contributing](../CONTRIBUTING.md) |
| Privately report a vulnerability | [Security](../SECURITY.md) |

## What the evidence means

- **Implemented:** core NuGet V3 package routes, npm registry routes and agent-skill ZIP import.
- **Tested:** unit/integration cases and CI. Consult the result of the separate [live registry workflow](../.github/workflows/smoke-live.yml) for a specific real-client run; the workflow file alone proves nothing.
- **Not guaranteed:** full native protocol parity, multi-node consistency or production suitability.
- **Security best practice:** HTTPS via a trusted reverse proxy, credentials outside Git and a consistent backup of SQLite and artifact files.

No UI screenshot is included until a source-verified, privacy-safe capture is available. The [architecture diagram](architecture.md) documents components rather than certified deployment behavior.

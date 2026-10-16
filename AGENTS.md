# Packory repository guidance

Packory is a public Apache-2.0 .NET 10 modular-monolith registry hub. Read `README.md`, `docs/architecture.md`, and `docs/roadmap.md` before substantial edits.

## Development rules

- Keep protocol-specific behavior in the NuGet, npm, or Agent Skills adapter. Share only storage, catalog, authentication, and administration infrastructure.
- Respect native-client wire protocols; an endpoint returning valid JSON is not evidence that `dotnet restore` or `npm install` succeeds. Add deterministic integration tests and where appropriate a real CLI smoke test.
- Never silently change an immutable `kind/name/version` artifact. Prefer atomic writes and verify upstream-declared artifact checksums.
- Reject unsafe package IDs/versions, unbounded uploads, untrusted redirects, ZIP traversal, and attempts to turn upstream fetching into an arbitrary URL proxy.
- No credentials or personal data in this **public** repo, including commit author emails, fixtures, logs, config, README, or issues. Use GitHub-provided noreply Git identities. Do not infer or reuse a person's email from existing commit metadata.
- Do not add production claims without tests. Clearly distinguish prototype functionality, official protocol support, and security assurances.
- Do not add Redis, message brokers, Kubernetes, or microservices to the MVP without evidence and an architecture decision.

## Checks

```bash
dotnet restore Packory.slnx
dotnet build Packory.slnx -c Release --no-restore -warnaserror
dotnet test Packory.slnx -c Release --no-build
node --check src/Packory/wwwroot/app.js
```

Document any API or config change in `docs/api.md` or `docs/operations.md`, and maintain the roadmap acceptance criteria.

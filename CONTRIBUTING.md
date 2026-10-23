# Contributing to Packory

Packory accepts focused bug reports, documentation improvements and code contributions. Read the [project status](docs/project-status.md), [support matrix](docs/support-matrix.md) and [architecture](docs/architecture.md) before claiming new protocol or deployment guarantees.

## Contribution workflow

1. Open an issue for significant changes, or create a focused branch and pull request for a bounded fix.
2. Keep native protocol behavior in its adapter. Preserve immutable artifact bytes, exact private-feed grants and safe upstream/ZIP processing.
3. Add deterministic positive **and negative** tests for changes to API contracts, authorization, metadata, integrity, persistence or recovery. Use real CLI tests for native protocol behavior when feasible.
4. Update the **canonical** documentation with the code: [API](docs/api.md) for routes, [operations](docs/operations.md) for configuration, [support matrix](docs/support-matrix.md) for tested compatibility and [Roadmap V5](docs/roadmap-v5.md) for outstanding acceptance.
5. Validate the relevant checks before requesting review. Document unavailable tools/providers explicitly; a skipped integration test is not a pass against the real provider.

## Baseline validation

```bash
dotnet restore Packory.slnx
dotnet build Packory.slnx -c Release --no-restore -warnaserror
dotnet test Packory.slnx -c Release --no-build
node --check src/Packory/wwwroot/app.js
python3 scripts/check_public_docs.py
python3 scripts/check_public_privacy.py
```

For native-client and backend-specific qualification, follow [CI qualification](docs/ci-qualification.md) and the test commands in the [support matrix](docs/support-matrix.md). Record the actual client, provider and commit; do not infer interoperability from an HTTP 200 or a single fixture.

## Documentation conventions

- Keep the root [English README](README.md) and [German README](README.de.md) aligned on capabilities, warnings, commands and links.
- Use [docs/README.md](docs/README.md) as the task-oriented index and [docs/project-status.md](docs/project-status.md) for dated summaries. Keep deep technical detail in its canonical topic document.
- Separate **implemented**, **tested**, **Packory-supported** and **recommended for deployment**. Never turn an experiment, code path or vendor emulator into a production or official support claim.
- Keep historical ADRs, review evidence and roadmap snapshots identifiable as historical rather than silently rewriting acceptance evidence.
- Check relative links, Markdown fences, command accuracy and diagrams. Where an acceptance command needs Docker, Node, PostgreSQL or S3, state that prerequisite.

## Security, privacy and licensing

This repository is **public**. Never include direct/personal email addresses, personal data, credentials, internal URLs, real customer artifacts, sensitive logs or secret configs in code, fixtures, docs, screenshots, commits or issues. Use an authorized GitHub-provided **noreply commit identity**; do not infer identity from older Git history. See [public privacy](docs/public-privacy.md). Report vulnerabilities privately following [SECURITY.md](SECURITY.md), not through public proof-of-concept payloads.

Avoid dependencies without an actively supported version and compatible license; discuss uncertain licensing before merging. Contributions are distributed under [Apache-2.0](LICENSE).

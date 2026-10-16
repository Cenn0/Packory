# Contributing to Packory

Thank you for considering a contribution. Open a GitHub issue for a bug report or feature proposal, or create a focused pull request.

- Work against an up-to-date branch and keep changes narrowly scoped.
- Add tests for protocol features: a change is not complete merely because its HTTP handler returns 200. Cover CLI compatibility, metadata, checksums and file content where possible.
- Run `dotnet build Packory.slnx -c Release`, `dotnet test Packory.slnx -c Release`, and `node --check src/Packory/wwwroot/app.js`.
- Add concise documentation for new endpoints, environment variables, and breaking protocol changes.
- Avoid adding dependencies unless they have a current supported release and compatible license; open a separate license-review issue if uncertain.
- **Privacy:** never include personal addresses, direct emails, API tokens, secret configs, internal URLs, identifiable logs, or real credentials in code, docs, test data, issues or commit metadata. Use GitHub noreply identities for commits.
- Do not upload proprietary packages or real private artifacts into public issues/PRs.

This repository is licensed under Apache-2.0. By submitting a contribution, you agree that your contribution can be distributed under this license.

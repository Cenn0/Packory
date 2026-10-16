# Security policy

Packory is an **experimental registry platform** with optional stricter deployment preflight, scoped credentials and limited read-only replicas. It has no security-support guarantee, comprehensive immutable audit trail, content-signature trust policy, or certified production release.

## Reporting vulnerabilities

**Do not publish exploit details, private data, credentials, logs, or internal hostnames in a public issue.** Use GitHub's **Report a vulnerability / Private vulnerability reporting** feature in the repository's Security tab if available. If it is not available, open a public issue requesting that private vulnerability reporting be enabled **without disclosing sensitive details**.

No personal email contact is published or required. See also [Operations](docs/operations.md).

## Security assumptions

- HTTPS is required when transmitting API tokens outside localhost.
- The operator controls upstream settings and deployment egress.
- Local SQLite and blob storage are trusted storage on one host.
- Uploaded code and skills are untrusted and must not be executed by Packory or clients without separate checks.
- Static tokens, short-lived revocable scoped tokens and optional validated OIDC bearer authorization exist. Treat all publisher credentials as privileged.

## Supported versions

Only the latest commit on `main` is currently maintained. No versioned stable release has been made. GitHub Actions CI and external smoke tests are the current quality gates; their presence is not a substitute for a security review.

# Optional offline validation results (MCP)

This feature is **disabled by default**. It never executes code, triggers CI, reaches private validation systems, or exposes raw evidence. It provides two read-only tools on the existing Packory MCP endpoint:

- `list_validation_runs`: operator-approved, currently valid aliases and minimal status/expiry.
- `get_validation_findings`: restricted finding codes/severities and cryptographically authenticated *claims* from a selected sanitized export.

A signed result is **not** independently re-executed by Packory. `runBinding = signed_attestation` means that a separately operated verifier signed the `run_binding_digest` claim. An operator must independently qualify how that verifier validates the binding and underlying test evidence. Do not mistake this for intrinsic evidence integrity or test correctness.

## Opt-in and authorization

Enable only with a locally managed OIDC authority and separately operated verifier. `packory.validation.read` is required **in addition** to `packory.read` or `packory.admin`. A normal static read/write/admin token is **not** enough, even if it can access ordinary MCP catalog tools. This is deliberate least privilege, not a separate general validation token.

Set these environment variables on the Packory host, **not in version control**:

| Variable | Role |
| --- | --- |
| `PACKORY_VALIDATION_ENABLED` | `true` to register the tools |
| `PACKORY_OIDC_AUTHORITY` and `PACKORY_OIDC_AUDIENCE` | Existing validated HTTPS JWT resource-server settings |
| `PACKORY_VALIDATION_EXPORT_DIRECTORY` | Absolute, operator-controlled directory |
| `PACKORY_VALIDATION_VERIFIER_PUBLIC_KEY_PATH` | Absolute path to pinned RSA public key in PEM format (private signing key stays external) |
| `PACKORY_VALIDATION_VERIFIER_KEY_ID` | Exact authenticated signer ID, matching signed export |
| `PACKORY_VALIDATION_APPROVED_ALIASES` | Comma-separated, operator-reviewed alias allowlist |
| `PACKORY_VALIDATION_APPROVED_EXPORTS` | Mandatory comma-separated `alias=sha256:<64 lowercase hex>` pins for **the exact bytes** of each approved JSON export. Must cover precisely the alias allowlist. |
| `PACKORY_VALIDATION_AUDIENCE` | Exact sanitized audience, default `packory-validation` |
| `PACKORY_VALIDATION_TENANT` | Exact tenant (default `local`, change for your isolation boundary) |
| `PACKORY_VALIDATION_REVOKED_KEY_IDS` | Comma-separated revoked signer IDs |

All inputs must be controlled outside the MCP client. The signing process is **external**, with a private RSA key protected by its own operator. A reviewer must sanitize the record, independently check evidence/immutability/source binding and sign exactly the UTF-8 JSON bytes using **RSA-PSS/SHA-256 with 32-byte (digest-length) PSS salt**. OpenSSL must use `-sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:digest`; its `max` salt default is not interoperable with .NET's RSA PSS verifier. For each allowed alias `example-run`, calculate SHA-256 of the **exact signed JSON bytes**, pin it in `PACKORY_VALIDATION_APPROVED_EXPORTS`, and place `example-run.json` and `example-run.sig` (Base64 detached signature of the exact JSON bytes) in the configured directory. The public key must match the private key of the external verifier. No external hostnames, raw evidence, original prompts, private repository names, logs or other private information are allowed in the export.

Strict parser rules: an operator-approved content digest is mandatory, the verifier public key is pinned at startup, reads are bounded without file-growth races, there is a shared 60-requests/minute zero-queue quota for validation tools (never applied to registries), max 32 KiB JSON, max 1 KiB signature file, no duplicate or extra fields, exact schema v1, bounded findings, strict digest syntax, no descriptions or free text, matching audience/tenant/signer, future issuance tolerance at most five minutes, expiry at most 30 days after issuance, and fail-closed missing/revoked/invalid signatures. The spec is [validation-export-v1.schema.json](contracts/validation-export-v1.schema.json). Neither the public schema nor its example field names constitute evidence.

### Operations

- **Review** the independently generated export, including the external verifier's policies, before adding an alias. Do not copy raw private evidence into public source control.
- **Rotate** signing keys by deploying an independently approved key and signer ID, then restarting instances. Reject previously trusted exports when a signer is revoked; remove its ID from the active trust config only as part of a documented requalification.
- **Disable immediately** by setting `PACKORY_VALIDATION_ENABLED=false` and restarting. For immediate expiry without restart, remove an approved export file or its detached signature. Operator digest approvals, key ID and scope configuration changes require a restart.
- **Retention** is managed by the operator; purge signed, sanitized exports after their approved retention period and maintain private revocation records. Never publish the evidence directory.
- **Audit** tool actions by decision and count only. Raw requests, findings, tokens and private evidence must not be logged. Restrict local filesystem access to Packory's process and the external import operator.
- **Availability**: invalid/revoked/expired results disappear from listing and return an undifferentiated `unavailable` for retrieval. The ordinary registry and Skill functions remain available if validation imports are absent.

## Security and qualification limits

Do not enable for public production use until a deployment-specific review covers: independently operated signing key lifecycle, external evidence validation and run binding, end-to-end OIDC scope tests with the selected Keycloak deployment, SDK compatibility, resource/abuse rate limits, allowed-directory permissions, TOCTOU and symlink protections, operational incident response, and safe audit retention. Treat these as release blockers, not as passing automated checks.

The future `request_ci_validation` is expressly **not implemented**; no arbitrary command, provider URL, Docker socket or shell execution is available via these tools.

See [ADR 0002](adr/0002-validation-evidence-boundary.md) for the threat model.

## Real signed Keycloak MCP integration coverage

The dedicated [Keycloak integration workflow](../.github/workflows/keycloak.yml) now starts a disposable TLS-enabled Keycloak with two machine clients: a regular package reader/writer and a separate `packory.validation.read` reader. It publishes only a synthetic RSA-PSS-signed, SHA-256-pinned validation export to a disposable Packory process. The Streamable HTTP MCP smoke test negotiates sessions and verifies regular-token denial, scoped-token list/findings access and unknown-alias denial. It does **not** substitute for the independent external evaluator, the target deployment's access policy review or any real private test data.

# Managed MCP connectors: PostgreSQL read gateway (experimental)

This document describes the **first executable connector slice**. It is separate from the inert
[MCP Registry](mcp-registry-catalog.md) and Packory's existing read-only artifact MCP tools.

A client connects to Packory's authenticated Streamable HTTP endpoint at `/mcp`. Packory
stores connector secrets encrypted in its metadata backend and accepts only the *name* of
an operator-approved query plus typed argument values from the client. Neither the
underlying connection string nor raw SQL is exposed through MCP.

**Support boundary:** this is a single-writer, in-process PostgreSQL prototype. It does
**not** complete [MCP-G1–G5](mcp-roadmap.md): separate runtime isolation, remote MCP proxy
execution, per-upstream OAuth delegation, distributed policy fencing, real-provider
certification, and a complete egress/circuit-breaker profile remain open. Do not deploy
this against sensitive production databases until those requirements are addressed.

## Security design

| Boundary | Current behavior | Residual risk |
| --- | --- | --- |
| MCP authentication | Signed OIDC Bearer validated against configured issuer/audience; requires both `packory.read` (or `packory.admin`) and `packory.connectors.read` | Static catalog/admin tokens, managed tokens and browser cookies do not authorize connector tools |
| Connector grant | Exact OIDC `sub` allowlist on each connector. No wildcard subjects. | Grants are per-subject, not per-query; use separate connectors to isolate narrower query sets |
| Credential vault | AES-256-GCM, random nonce, separate keyring file; AAD binds ciphertext to connector ID and profile | Secrets must be backed up/rotated securely; decrypted connection strings briefly exist in application memory |
| Destination policy | Exact operator-configured hostname allowlist; PostgreSQL port/database/user/password only; `SSL Mode=VerifyFull`; no arbitrary driver options | DNS and network egress are not independently pinned to IPs; enforce egress at the host/firewall |
| Query policy | Administrators define named `SELECT` templates; fixed typed parameters (`text`, `int64`, `bool`, `uuid`); no SQL from clients; transactions marked read-only | PostgreSQL `SELECT` can invoke functions; use a dedicated role with **no** write, admin, file or privileged function access and select grants only on safe views |
| Execution | No pooling; 5-second connect timeout, 4-second command timeout, 8-second total timeout, 1-second lock timeout, max 50 rows, 24 columns, 32 KiB serialized rows | Limited in-process concurrency/denial-of-service protection, not a dedicated worker/sandbox |
| Audit | Policy changes, revocations, allow/deny/error outcomes and pseudonymous subject hashes; no SQL parameters, result rows or secrets | Audit depends on Packory's metadata backend and is not an independent tamper-evident ledger |
| Result trust | `sourceDataUntrusted=true`; unsupported types elided and large strings truncated | Model/client must still treat all returned rows as **data**, never as instructions |

The API and MCP registration are disabled unless explicitly enabled. If the configured
keyring is missing, non-private, or malformed, opt-in startup fails closed.

## Deployment prerequisites

1. Configure Packory's existing OIDC/JWT resource server (`PACKORY_OIDC_AUTHORITY`,
   `PACKORY_OIDC_AUDIENCE`), `/mcp` Bearer authentication and HTTPS reverse proxy.
2. Create a **separate** keyring outside the repository and mount it read-only with owner-only
   permissions (e.g. mode `0400` or `0600` on Linux). File format:

   `{"active":"k1","keys":{"k1":"<base64-encoded-32-random-byte-key>"}}`

   The placeholder is not a key. Generate a new cryptographically random key at deployment
   time; never put the key or a real database connection string in version control, environment
   dumps, agent prompts, logs or browser local storage.
3. Set:

   - `PACKORY_MCP_CONNECTORS_ENABLED=true`
   - `PACKORY_MCP_CONNECTOR_KEYRING_FILE=/run/secrets/packory-mcp-connector-keyring`
   - `PACKORY_MCP_CONNECTOR_HOSTS=db.internal.example` (comma-separated **exact** hostnames)
   - `PACKORY_MCP_ENABLED=true`
   - `PACKORY_INSTANCE_ROLE=writer`

4. Provision a dedicated PostgreSQL login with TLS **full certificate verification**,
   `CONNECT` on the target database and `SELECT` only on approved, preferably
   security-barrier views. Do not use a Packory metadata database superuser.
   Revoke unnecessary function, schema, temporary-object and public privileges as applicable.
   Test the role's *effective* rights with PostgreSQL, not just the declarative grants.
5. Restrict the Packory host's outbound network to intended PostgreSQL destinations.
   The hostname allowlist is not a complete SSRF/DNS-rebinding protection.
6. Treat all connector data as potentially sensitive. Obtain appropriate application
   permission before making it accessible to a model.

## Administrative API

Only Packory administrators may use these endpoints. They are not callable from MCP and
never return the encrypted or plaintext credential.

- `GET /api/admin/mcp-connectors/`: connector policies and `hasCredential` flags.
- `GET /api/admin/mcp-connectors/{id}`: one sanitized policy.
- `PUT /api/admin/mcp-connectors/{id}`: create/update with optimistic revision, optional
  secret replacement and audit reason.
- `POST /api/admin/mcp-connectors/{id}/revoke`: disable the connector and delete its
  encrypted credential; further model requests are denied.
- `GET /api/admin/mcp-connectors/audit`: the newest 100 sanitized audit entries.

Provisioning example (illustrative synthetic data, **not** a functional credential):

```json
{
  "definition": {
    "id": "orders",
    "description": "Approved order lookup",
    "enabled": true,
    "revision": 0,
    "allowedSubjects": ["synthetic-service-subject-123"],
    "queries": [{
      "name": "order_lookup",
      "description": "Read an approved order by its internal ID",
      "sql": "SELECT id, status FROM reporting.safe_orders WHERE id = @id",
      "parameters": { "id": "int64" },
      "maxRows": 10
    }]
  },
  "connectionString": "Host=db.internal.example;Port=5432;Database=reporting;Username=read_only;Password=<operator-supplied>;SSL Mode=VerifyFull",
  "clearCredential": false,
  "reason": "Operator-approved read-only reporting connector"
}
```

Send the JSON over an authenticated HTTPS administrative channel. Prefer a private,
short-lived request file or a secret-manager handoff; avoid copying real secrets
into terminals with retained shell history. The connection string is write-only
and becomes an authenticated ciphertext in the metadata database. On update,
send the **current** revision; omit `connectionString` to retain the existing secret.
To rotate, provide a new secret; to revoke, use the dedicated revoke endpoint.

## MCP client contract

MCP clients obtain a **Packory-audience** Bearer token with scopes
`packory.read packory.connectors.read` from the configured issuer. The principal's exact
`sub` must be included in the connector's `allowedSubjects`. Do not grant these scopes
to public/anonymous users. A connector query is **not** authorized merely because a user
can search public artifacts or has an administrator cookie.

- `list_connector_queries`: shows only the enabled connectors allowed to that subject,
  with descriptions, parameter types and row limits.
- `run_connector_query(connectorId, queryName, arguments)`: runs only an exact approved
  template, with string values parsed/bound by type. Example MCP arguments:
  `{"connectorId":"orders","queryName":"order_lookup","arguments":{"id":"42"}}`.
  No arbitrary SQL, host, URL, tool manifest or credential input is accepted.

The client must regard returned database text as untrusted content. In particular,
text resembling system messages, tool commands or configuration instructions does not
gain authority by being delivered through a gateway.

## Lifecycle and recovery

- Updating the policy/credential and revoking it serialize against ongoing reads on a
  single Packory writer. Revocation waits for any in-flight query to terminate.
- Keep previous key IDs during key rotation until all stored ciphertext has been
  re-encrypted by a subsequent authenticated update. The active key changes for newly
  written values only.
- Back up the metadata database and the secret ring **separately**. Losing the ring
  makes stored credentials unrecoverable. A backup copy of the ring is sensitive.
- The 30-calls/minute quota is per-process and not a distributed throttling guarantee.
- The gateway is explicitly not available on Packory `reader` replicas or maintenance
  instances; this is not a high-availability execution plane.
- Failure/timeout responses are generic, to avoid returning PostgreSQL diagnostics
  containing network paths, SQL, or secrets.

## Qualification still required

The included automated tests are *synthetic and networkless*. Before production use,
run real PostgreSQL TLS and privilege-negative tests, verify cancellation and
read-only function behavior, inspect audit/redaction under failures, add UI authorization
coverage, and isolate execution in a dedicated locked-down worker with network policy.
Remote MCP server proxying is a **separate** follow-up: requires resource-specific
OAuth, reviewed descriptor digest, tool allowlists and DNS/IP/redirect validation at
every connection, plus an independent multi-client interoperability suite.

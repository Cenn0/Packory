# Managed MCP connectors

Packory has two separate MCP concepts: the **inert MCP Registry** stores reviewed server.json metadata; **MCP Connectors** are administrator-managed outbound connections. Publishing a catalog entry does not establish a connection.

## Operator-owned deployment policy

Outbound connector connectivity is **off by default**. Configure these environment variables outside the public repository:

| Variable | Meaning |
| --- | --- |
| PACKORY_MCP_CONNECTOR_ALLOWED_ORIGINS | Comma-separated approved HTTPS origins, for example https://mcp.example.org. No paths or wildcards. |
| PACKORY_MCP_CONNECTOR_ALLOWED_PRIVATE_CIDRS | Optional explicit internal network exceptions. Default: none. Avoid unless private-network access is needed and controlled. |
| PACKORY_MCP_CONNECTOR_KEYRING_FILE | Absolute path to a private AES-256-GCM keyring; required when storing bearer credentials. |

Create the keyring outside the repository, on a private deployment volume:

~~~bash
install -d -m 0700 /var/lib/packory/private
python3 - <<'PY'
import base64, json, os
path = "/var/lib/packory/private/mcp-keyring.json"
fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
with os.fdopen(fd, "w", encoding="utf-8") as f:
    json.dump({"active": "k1", "keys": {"k1": base64.b64encode(os.urandom(32)).decode("ascii")}}, f)
PY
~~~

For the included **optional Docker Compose overlay**, set the following host-side variables before starting it:

~~~bash
export PACKORY_MCP_CONNECTOR_ALLOWED_ORIGINS=https://mcp.example.org
export PACKORY_MCP_CONNECTOR_KEYRING_PATH=/var/lib/packory/private/mcp-keyring.json
docker compose -f compose.yaml -f compose.mcp-connectors.yaml up -d --build
~~~

The overlay mounts the keyring read-only inside the Packory container and sets its internal filename. Use the same overlay together with compose.ghcr.yaml when deploying the prebuilt image (omit --build). For internal-only MCP services, any private CIDR exception must be explicitly supplied and reviewed by the operator.

The service process needs permission to read the file. Restart Packory after changing the environment policy. The keyring must be backed up securely; its loss makes encrypted connector credentials unusable. Old keys must remain available until stored values have been re-encrypted. Automatic key re-encryption is not yet provided. Never commit keyrings, bearer tokens, database credentials, or personal information.

## Dashboard workflow

1. Sign in as a Packory administrator, then open **MCP Connectors**.
2. Create an ID, name and HTTPS Streamable HTTP endpoint within an approved origin. Add an optional bearer credential.
3. Save the new connector disabled, with no allowed tools.
4. Use **Test** to discover remote tool names via the MCP handshake. Discovery does not enable tools or verify their safety.
5. Review tool behavior and remote service permissions. Add only deliberately approved tool names to the allowlist. Enable the connector separately.
6. Edit, deactivate, rotate/remove bearer credentials or delete the connector as required. Optimistic revisions prevent overwriting concurrent updates.

Credentials are encrypted at rest with AES-256-GCM and bound to connector ID and endpoint URL. The API returns only hasBearerToken. Changing endpoint URLs clears existing credentials. Tool calls and configuration updates are audited without recording sensitive arguments or credentials.

Deleted connector IDs remain reserved, including after restart and restore. An old
OIDC tool grant must not attach to a newly created connector. Use a fresh ID for a
new connection. Calls, probes and edits for the same connector share a bounded
operation gate: a successful disable/delete follows preceding operations and blocks
later ones. A busy edit returns `429` and `Retry-After`; retry after the active call
finishes. An already dispatched remote side effect cannot be rolled back by Packory.

## Calling connector tools without giving AI database credentials

The database password stays on the **independently secured remote MCP server**, not in the model's context. Packory holds only an optional credential for that remote MCP service and enforces an exact local tool allowlist.

Do not give an AI an administrator token. Instead issue a validated OIDC access token carrying all of:

- packory.read — authenticates to Packory's existing /mcp endpoint.
- packory.connector.call — grants the connector-invocation permission.
- A packory_connector claim for each specific tool: connector-id:tool-name (example: team-database:query_readonly).

The access token must be verified using Packory's configured OIDC issuer, audience and signature. It must not be an unverified request claim, browser cookie, or static read/write token.

Packory's authenticated MCP server exposes:

- list_connector_tools(connectorId) — bounded names, descriptions and input schemas for an enabled connector. Scoped callers see only tools both locally approved and explicitly granted; administrators can inspect all discovered tools.
- call_connector_tool(connectorId, toolName, argumentsJson) — invokes one enabled, locally approved remote MCP tool. argumentsJson is a JSON object serialized as a string.

The administrator-only REST API also permits probe/call for controlled operational verification; the dashboard UI intentionally provides connection testing but not a general arbitrary tool execution console.

### Database example

Deploy a trusted MCP server in front of PostgreSQL with a restricted read-only database role, selected read-only operations, tenant checks, query timeouts and result limits. Packory connects only to this MCP endpoint. An AI can call the explicitly approved query tool using the scoped Packory token, without ever receiving PostgreSQL credentials.

Packory also provides **native database connectors** for PostgreSQL, MySQL/MariaDB,
Microsoft SQL Server and Oracle. These use a separate administrator workflow,
parameterized SELECT templates and distinct database grants. See
[Native database connectors](database-connectors.md). Remote MCP service credentials
and native database passwords are separate capabilities; the inert Registry grants neither.

## Security boundaries and limitations

- Only remote **MCP Streamable HTTP over HTTPS** is supported. No stdio/subprocess execution, SSE fallback, user-supplied request headers, or dynamic upstream OAuth handling.
- Exact origin allowlist is deployed out-of-band; configured endpoints are checked again on every use. System HTTP proxies, redirects, cookies and transparent compression are disabled. All returned DNS answers must pass the address policy; the selected numeric address is pinned before connection.
- Private-network access needs an explicit deployment-owned CIDR exception. Link-local and metadata addresses remain blocked.
- MCP server metadata and returned tool descriptions are untrusted. Tool discovery never creates access grants and remote tools may perform side effects.
- Tool arguments are bounded at 64 KiB and redacted serialized results at 128 KiB.
  A streaming guard rejects HTTP JSON/SSE responses over 256 KiB **before SDK
  deserialization**, with a cumulative 1 MiB wire budget per operation. Calls have
  a 25-second deadline and 8-second connect timeout. A shared, zero-queue budget
  admits four operations per process. Discovery has at most four protocol pages,
  100 tools, 16 KiB per input schema and 128 KiB across schemas. Oversized data is
  rejected; it is not returned as an apparently complete result.
- TLS validates system trust and the endpoint hostname. Certificate issuer downloads
  and online revocation lookups are disabled on the remote HTTP transport to avoid
  implicit network destinations. Servers must send their complete certificate chain;
  manage certificate revocation/rotation through the deployment trust policy.
- Literal bearer reflection in decoded JSON strings, property names and discovery
  metadata is redacted or rejected. An upstream that deliberately transforms or
  encodes the credential can still disclose it: only connect a credential to a
  trusted service for which it was issued. Provider errors are returned as opaque
  failures, not upstream diagnostics.
- Packory cannot enforce object-, tenant- or row-level authorization inside remote services. Those checks must be implemented by each remote MCP gateway.
- Treat administrator capabilities as privileged; use dedicated model-facing OIDC tokens and minimal per-tool grants. Successful connectivity does not imply a security certification.
- Execution remains inside the Packory application process. These controls are not
  process isolation, a hostile-code sandbox or a production certification. Deploy
  network/resource isolation appropriate to the approved service; full isolated
  gateway execution and per-user upstream OAuth delegation remain separate work.

See [API reference](api.md). The existing inert MCP Registry and read-only package/skill features remain separate.

# MCP Registry Metadata in Packory (experimental)

Packory now has a **version-pinned, remote-only, inert subset** of the official MCP Registry server.json format. This catalog complements the native Packory MCP endpoint and its approved Agent Skills. It does **not** operate a remote MCP gateway or execute untrusted instructions.

## State and trust model

1. Only administrators may submit immutable server.json at **POST /api/v1/admin/mcp/servers/stage**. Packory validates the format and records exact UTF-8 bytes, SHA-256, name and version as **PENDING**.
2. The operator independently inspects the publisher, repository, remote endpoint and SHA-256. **POST /api/v1/admin/mcp/servers/decide** accepts a reason and expected digest to perform **PENDING → PUBLISHED** or **PUBLISHED → REVOKED** atomically and audibly.
3. Read-only REST and MCP discovery expose **PUBLISHED** records only; revocation hides records immediately for later reads. Reusing the same name/version or re-publishing a revoked version is prohibited.
4. The server never contacts, runs, installs or proxies external MCP servers or forwards any token. Registry metadata is always labeled **publisherVerified: false** and **safeToConnect: false**. An operator's approval is not ownership verification.

**Schema subset:** pinned 2025-12-11 server.schema.json, canonical MCP name, strict semantic version, bounded title/description, optional GitHub repository, one to four HTTPS Streamable HTTP remotes. Reject arbitrary unknown properties, duplicate JSON keys, URL templates, inline request headers, package launch commands, local/IP URLs, credentials in URLs, or URL query strings. This is intentionally narrower than the official server.json schema and is **not** a full Registry API implementation.

## Example input

The following public dummy metadata is inert. Do not treat it as a genuine verified remote endpoint.

~~~json
{
  "$schema": "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json",
  "name": "org.example/demo-server",
  "title": "Demo MCP server",
  "description": "Synthetic sample metadata only",
  "version": "1.0.0",
  "repository": {
    "source": "github",
    "url": "https://github.com/example/demo-server"
  },
  "remotes": [
    { "type": "streamable-http", "url": "https://mcp.example.org/mcp" }
  ]
}
~~~

Submit it as an administrator, using a runtime token that is never committed or logged:

~~~bash
curl --fail-with-body -X POST \
  -H "Authorization: Bearer $PACKORY_ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  --data-binary @server.json \
  http://127.0.0.1:8080/api/v1/admin/mcp/servers/stage
~~~

After review, POST the following JSON to **/api/v1/admin/mcp/servers/decide**, authenticated as an administrator:

~~~json
{
  "name": "org.example/demo-server",
  "version": "1.0.0",
  "sha256": "<exact lowercase SHA-256 from the stage response>",
  "action": "publish",
  "reason": "Synthetic demonstration review"
}
~~~

Use action **revoke** and the same digest to prevent subsequent discovery. Transitions write a persisted audit event (role identity, decision, reason, timestamp); do not include personal data in reasons.

## Discovery

- **GET /api/v1/mcp/servers?q=demo&limit=25&offset=0**: versioned published entries and optional nextOffset.
- **GET /api/v1/mcp/server?name=org.example%2Fdemo-server&version=1.0.0**: exact stored server JSON plus trust flags.
- **GET /api/v1/admin/mcp/servers**: administrator-only inventory including pending/revoked records.
- **MCP tools:** search_mcp_servers(query,limit) and get_mcp_server(name,version); authenticated read-only and no remote connection.
- **MCP resource:** packory://mcp/registry/info describes this supported subset.

The ordinary REST discovery obeys PACKORY_ANONYMOUS_READ; /mcp always requires an explicit bearer credential.

## Operational limitations

- No automatic ownership/domain proof, signature verification, upstream registry import, remote runtime authentication, tool permission mediation or third-party scanning. The receiving MCP client must separately verify endpoints and apply SSRF and injection controls. A DNS hostname that passes import checks can later resolve to a private address.
- Persistence uses existing SQLite or PostgreSQL. Writer startup initializes the additive table; read-only instances require the corresponding shared-primary migration.
- The /mcp endpoint has an in-process shared fixed-window budget of 300 requests per minute with no queue. This is not a distributed rate limiter or a measured capacity target.
- Pages use offsets, not a consistent historical snapshot; concurrent state changes can shift rows.
- The REST routes are **Packory-specific**, not the official /v0.1 Registry API. Full MCP Registry conformance, namespace verification, signed provenance and production qualification remain separate M5.2 release gates.
- Keep production credentials, email addresses, private hostnames, internal test results and personal data out of the public repository.

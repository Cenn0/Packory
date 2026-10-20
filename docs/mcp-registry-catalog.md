# MCP Registry metadata and discovery

Packory stores and discovers a **version-pinned, remote-only manifest profile** of the official MCP Registry `server.json` format. The three v0.1 read routes, Packory management API, MCP tools and dashboard share the same immutable reviewed catalog. The [MCP delivery roadmap](mcp-roadmap.md) tracks broader manifests, publisher verification, synchronization and an optional isolated gateway.

## State and trust model

1. Only administrators may submit immutable server.json at **POST /api/v1/admin/mcp/servers/stage**. Packory validates the format and records exact UTF-8 bytes, SHA-256, name and version as **PENDING**.
2. The operator independently inspects the publisher claim, repository, remote endpoint and SHA-256. **POST /api/v1/admin/mcp/servers/decide** accepts a reason and expected digest to perform **PENDING → PUBLISHED** or **PUBLISHED → REVOKED** atomically with a persisted audit event.
3. Read-only REST and MCP discovery expose **PUBLISHED** records only; revocation hides records immediately for later reads. Reusing the same name/version or re-publishing a revoked version is prohibited.
4. The server never contacts, runs, installs or proxies external MCP servers or forwards any token. Registry metadata is always labeled **publisherVerified: false** and **safeToConnect: false**. An operator's approval is not ownership verification.

**Manifest profile:** pinned 2025-12-11 `server.schema.json`, canonical MCP name, strict semantic version (including the prohibition on leading zeroes in numeric prerelease identifiers), title of at most 100 characters, bounded description, optional GitHub repository and one to four HTTPS Streamable HTTP remotes. The profile rejects unknown properties, duplicate JSON keys, URL templates, inline request headers, package launch commands, local/IP URLs, credentials in URLs and URL query strings. Admission of this profile does not establish full official schema conformance. Previously stored bytes remain immutable; records that fail the current profile are excluded from discovery.

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

### Registry v0.1 read API

The read envelopes follow the [official Registry OpenAPI 2025-12-01](https://github.com/modelcontextprotocol/registry/blob/11b7b0e274d518b6f449c5ee3518a6151d9848b9/docs/reference/api/openapi.yaml), pinned to that repository revision.

| Route | Supported query and result |
| --- | --- |
| `GET /v0.1/servers` | `search`, `version`, `limit`, `cursor`; a page of published descriptors |
| `GET /v0.1/servers/{serverName}/versions` | `limit`, `cursor`; published history of the exact URL-encoded name, ordered by publication time descending then ordinal version |
| `GET /v0.1/servers/{serverName}/versions/{version}` | Exact semantic version or `latest`; one published descriptor |

Encode the complete name as one path segment, for example `org.example%2Fdemo-server`. List pages contain `servers` and `metadata` (`count` and optional `nextCursor`). Each entry contains `server` plus `_meta`. Packory adds its review state, publication timestamp, exact-source SHA-256 and false trust flags under **`io.github.cenn0.packory/registry`**. It does not issue metadata under the official registry's reserved namespace.

`search` is a case-insensitive literal substring of the server **name**; `%`, `_` and backslash are ordinary characters. `version` accepts an exact semantic version or `latest`. Latest means the highest published semantic version per name; build metadata has no precedence, with ordinal spelling breaking ties deterministically. Revoked and invalid records cannot become latest.

`limit` defaults to 30 and must be 1–100. Continue with the returned opaque `metadata.nextCursor` and the same filters. Cursors select a position, grant no access and do not represent a historical snapshot. Current publication state is checked on every page. Invalid, mismatched or obsolete cursor formats return 400. Unsupported or repeated query parameters also return 400. `include_deleted=false` is accepted; `include_deleted=true` and `updated_since` are rejected because a complete synchronization/tombstone contract is not yet implemented. Unknown exact servers/versions return 404.

The SHA-256 identifies the original staged UTF-8 `server.json` bytes. JSON envelopes preserve descriptor values but may reserialize whitespace; hashing the envelope or a client-side reserialization does not verify that original digest.

### Packory REST and MCP

- **`GET /api/v1/mcp/servers?q=demo&limit=25`** returns published summaries and `nextCursor`. The optional `q` searches names and titles. Pass the same `q` and `cursor` for the next page. Limits are 1–50. Explicit `offset` remains available for older clients; do not combine it with `cursor`.
- **`GET /api/v1/mcp/server?name=org.example%2Fdemo-server&version=1.0.0`** returns exact descriptor values and review information.
- **`GET /api/v1/admin/mcp/servers`** is an administrator-only inventory including pending/revoked records, with the existing bounded offset contract.
- **MCP tools:** `search_mcp_servers(query, limit=20, cursor?, version?)` and `get_mcp_server(name, version)` (exact or `latest`). Invalid search/cursor parameters return MCP `InvalidParams`.
- **MCP resource:** `packory://mcp/registry/info` describes the supported profile, versions and routes.

Ordinary REST discovery follows `PACKORY_ANONYMOUS_READ`; when anonymous reads are disabled, a global read credential is required. Feed/namespace-scoped managed credentials cannot grant global discovery access. `/mcp` always requires an explicit global Bearer credential. Responses use `Cache-Control: no-store`.

## Web dashboard

The main Packory dashboard provides name/title search and **Load more** with opaque cursor continuation. Version and remote endpoint are displayed as inert text. Token changes clear prior catalog data and discard delayed responses from the previous credentials. The in-memory API token is used only for Packory requests. Staging, publishing and revocation remain administrator-only REST operations; a browser session/CSRF contract is a separate roadmap task. Publisher verification stays explicitly false.

## Operational limitations

- No automatic ownership/domain proof, signature verification, upstream registry import, remote runtime authentication, tool permission mediation or third-party scanning. The receiving MCP client must separately verify endpoints and apply SSRF and injection controls. A DNS hostname that passes import checks can later resolve to a private address.
- Persistence uses existing SQLite or PostgreSQL. Writer startup initializes the additive table; read-only instances require the corresponding shared-primary migration.
- The /mcp endpoint has an in-process shared fixed-window budget of 300 requests per minute with no queue. This is not a distributed rate limiter or a measured capacity target.
- New discovery consumers use keyset cursors. Legacy explicit-offset consumers can still observe shifted rows when publication state changes.
- Official publishing/authentication, full manifest support, ownership proof, signatures, imports and deleted-record synchronization remain separate [MCP-R2–R6](mcp-roadmap.md) tasks. The v0.1 read routes do not imply full Registry conformance.
- Keep production credentials, email addresses, private hostnames, internal test results and personal data out of the public repository.

## Verification

`McpRegistryTests` covers the three read routes, exact/latest versions, envelopes, scope-bound cursors, literal filters, immutable decisions, revocation, corrupt storage and native C# MCP discovery. `McpRegistryPostgresProviderTests` runs the same ordering/lifecycle cases against an actual PostgreSQL database when `PACKORY_TEST_POSTGRES` is configured. SQLite remains covered in the ordinary suite. The dashboard browser script uses synthetic API responses and verifies pagination and credential changes independently of backend tests.

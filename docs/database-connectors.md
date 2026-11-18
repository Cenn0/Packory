# Native database connectors

Packory can expose administrator-approved database queries to an AI through its authenticated MCP endpoint. The AI receives a query name, parameter schema and bounded results. Database passwords, connection strings, hostnames and SQL templates are not part of that MCP contract.

This is an **opt-in implementation for operator-controlled databases**, with native adapters for PostgreSQL, MySQL, MariaDB, Microsoft SQL Server and Oracle Database. It does not make Packory a general SQL console or a database management product. It is separate from both the [inert MCP Registry](mcp-registry-catalog.md) and the [remote MCP connector](mcp-connectors.md), which forwards selected tools to an independently operated HTTPS MCP server.

## Scope and evidence

| Question | Current answer |
| --- | --- |
| Does the feature exist? | Native connection, probe, parameter binding, query execution, administrative lifecycle, dashboard and scoped MCP contracts are implemented. |
| Is a driver officially maintained for its database? | Microsoft.Data.SqlClient and Oracle ODP.NET are vendor drivers. Npgsql and MySqlConnector are independently maintained projects with their own compatibility statements. Using one does not confer vendor certification on Packory. |
| What is the operational baseline? | One active Packory writer, a trusted database server, authenticated TLS, a dedicated database login restricted to reviewed views, exact OIDC query grants and deployment-owned egress controls. |
| Is arbitrary database access safe? | No. Database privileges and server-side row/tenant policy remain essential. The same-process runtime is not a containment boundary for a hostile database protocol implementation or server. |
| What is qualified? | Consult the exact-commit **Native database connectors** workflow and its sanitized evidence artifacts. The workflow's existence is not a passed result or a general production qualification. |

The implementation uses the following pinned packages. These are implementation versions, not claims that every server version or managed database service is supported.

| Provider ID | Driver | Connection and transaction behavior |
| --- | --- | --- |
| `postgresql` | `Npgsql` **10.0.3** | `VerifyFull` TLS; GSS session encryption disabled so the TLS contract is explicit. The driver connects to the approved numeric IP while validating the configured TLS name. A read-only transaction and local statement/lock timeouts are applied. |
| `mysql` | `MySqlConnector` **3.0.0** | TLS is required. A certificate callback checks the TLS stack's chain errors and the original certificate hostname after the socket destination is pinned. Native read-only transactions are used. Server redirection, local infile loading and public-key retrieval are disabled. |
| `mariadb` | `MySqlConnector` **3.0.0** | The same guarded driver configuration and parameterized query path as MySQL, with a separate provider identity and real-server qualification job. |
| `sqlserver` | `Microsoft.Data.SqlClient` **7.1.2** | Explicit TCP address/port, mandatory encryption, `TrustServerCertificate=false`, and the original `HostNameInCertificate`. Connection retries and multi-subnet failover are disabled. SQL Server has no equivalent read-only transaction setting: the independently restricted login is the authorization boundary. |
| `oracle` | `Oracle.ManagedDataAccess.Core` **23.26.401** | A single numeric-IP TCPS descriptor, no connection retries, explicit server-DN matching, the expected certificate distinguished name and OS trust through `WalletLocation=system`. Automatic proxy use is disabled. `SET TRANSACTION READ ONLY` is the first statement in the transaction. |

All paths disable pooling and automatic ambient transaction enlistment. Packory never commits a query transaction. Rollback is useful cleanup, but it does not undo external side effects or replace database permissions. SQL Server's `ApplicationIntent=ReadOnly` is a routing hint, not a permission; Packory does not request that routing mode. PostgreSQL and MySQL read-only transactions also have documented exceptions, including some temporary-table operations. Use the database's authorization controls even where read-only transactions are available.

Oracle's driver documentation lists .NET 10 support from the 23.26 line and specific 64-bit operating-system/database prerequisites. The current Packory container and CI do not qualify every listed combination. Oracle client/database licensing and distribution terms must be checked independently; see the [driver requirements](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/InstallSystemRequirements.html) and [package license](https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core/23.26.401/License).

## Prepare the database account

Create a dedicated login for each intended access boundary. Grant access only to reviewed views or tables and only to the rows and columns the model should receive. Prefer views that apply the required tenant predicate and omit sensitive columns. Do not reuse Packory's own metadata-database account.

The database administrator must inspect inherited roles, public/default grants, ownership, executable functions, definer-rights routines, linked servers, database links, foreign-data wrappers and external-network privileges. A login that can only run a top-level `SELECT` may still invoke a function with side effects or read data through another object's owner rights. A successful Packory connection test checks TLS/login and one fixed scalar query; it does **not** inspect or certify those effective permissions.

| Engine | Minimum design for the connector login |
| --- | --- |
| PostgreSQL | Permit connection to the selected database, schema usage and `SELECT` on the specific views. Review role inheritance, `PUBLIC` function execution, function ownership/security-definer behavior and row-level security. Do not grant superuser or broad database ownership. |
| MySQL / MariaDB | Restrict the login's host/network as well as its selected database/views. Do not grant broad `FILE`, administrative, DDL or routine-execution privileges. Review view definers and any function execution. |
| SQL Server | Use a dedicated SQL-authenticated database user with explicit `SELECT` on approved objects. Avoid `db_owner`, broad write roles, unrestricted `EXECUTE`, linked-server access and external/CLR capabilities. `ApplicationIntent` does not enforce this. |
| Oracle | Use a dedicated account with the required session permission and selected object grants. Avoid `SELECT ANY TABLE`, broad `EXECUTE ANY`/administrative roles, unrestricted database links and network-package access. Review definer rights and database network ACLs. |

Password authentication is the implemented profile. Integrated Windows/Kerberos authentication, cloud identity-token renewal, upstream OAuth, arbitrary Oracle wallet uploads, connection-string passthrough, stored-procedure tools, unrestricted schema introspection, SQLite file access and generic ODBC/JDBC plugins are not exposed by this feature. Additional providers require their own adapter, permission model and native qualification.

## Configure the deployment

The dashboard cannot enlarge the operator's network allowlist. Set the following outside the public repository:

| Variable | Default / requirement |
| --- | --- |
| `PACKORY_DB_CONNECTORS_ENABLED` | `false`. Set to `true` to permit probes and queries. Administrative definitions may be prepared while execution is disabled. |
| `PACKORY_DB_CONNECTOR_ALLOWED_TARGETS` | Empty. Comma-separated exact `provider://host:port` entries, such as `postgresql://db.example.invalid:5432`. No user information, wildcards, path, query or fragment. The configured host and port must match a listed target. |
| `PACKORY_DB_CONNECTOR_ALLOWED_PRIVATE_CIDRS` | Empty. Explicit, deployment-owned exceptions for private addresses. Narrow these to required database addresses. Link-local, metadata and other forbidden address classes remain blocked. |
| `PACKORY_MCP_CONNECTOR_KEYRING_FILE` | Absolute private keyring path, shared with remote MCP connector credential storage. Required to store or use a password. |

At most 64 targets and 16 private CIDR exceptions are accepted. The runtime rechecks the target and all returned DNS addresses before each connection, then passes only the selected numeric address to the driver. An unapproved DNS answer fails the operation. TLS certificate identity is checked separately from that address. `TlsServerName` is the expected certificate name; Oracle additionally requires its expected full certificate distinguished name.

Use the [private keyring creation procedure](mcp-connectors.md#operator-owned-deployment-policy) before starting the optional Compose overlay:

```bash
export PACKORY_DB_CONNECTOR_ALLOWED_TARGETS='postgresql://db.example.invalid:5432,sqlserver://sql.example.invalid:1433,oracle://oracle.example.invalid:2484'
export PACKORY_MCP_CONNECTOR_KEYRING_PATH=/var/lib/packory/private/mcp-keyring.json
docker compose -f compose.yaml -f compose.database-connectors.yaml up -d --build
```

The host-side `PACKORY_MCP_CONNECTOR_KEYRING_PATH` is a Compose input. The overlay mounts it read-only at the internal `PACKORY_MCP_CONNECTOR_KEYRING_FILE` path. The application user must be able to read the file, while unrelated users must not. For prebuilt images, combine the overlay with `compose.ghcr.yaml`, omit `--build`, and use a verified image revision as described in [Container images](container-images.md).

The example names are reserved, non-operational examples. Supply your own private deployment configuration without committing it. If the database resolves to private addresses, set the reviewed private-CIDR exception before starting Packory. Restart the process when environment policy changes.

### Certificate trust

Install the database CA into the operating-system trust store used by the Packory process. The server must provide the necessary intermediate certificates, and its identity must match the configured TLS name or Oracle certificate DN. There is no dashboard switch to ignore a certificate error, disable encryption or trust an arbitrary server certificate.

For a private CA in the container, build a deployment-owned derivative image from the verified Packory image. Add only the required public CA certificate under `/usr/local/share/ca-certificates`, run `update-ca-certificates` as root during the image build, then restore `USER $APP_UID`. Do not copy a CA private key into that image, and do not replace the entire `/etc/ssl/certs` directory with a bind mount. Update and test that image when the CA changes.

In Npgsql, the TLS callback disables certificate-authority downloads while preserving normal trust/hostname validation. Other drivers may perform certificate-chain or revocation lookups internally. An application-level socket pin is not a complete firewall; see [Runtime and network boundaries](#runtime-and-network-boundaries).

## Dashboard workflow

1. Sign in as a Packory administrator and open **Database connectors**.
2. Create an ID and display name, choose the database system, and enter the approved host, port, database/service, username and certificate identity. Oracle uses the service name rather than a user-supplied TNS descriptor.
3. Enter the password over HTTPS. Keep the connector disabled while preparing it. Passwords are never returned by the read API, and the dashboard clears its password field after submitting a change.
4. Add named query definitions, descriptions, SQL templates, typed parameters, row limits and timeouts. Review the database objects and effective account permissions separately.
5. Save and use **Test connection**. This intentionally works for a disabled connector when the operator has enabled the database feature.
6. Enable the connector only after reviewing its queries. Use **Run query** to test a saved approved query with JSON parameters. This test panel cannot submit an alternative SQL template as a query argument.
7. Grant the intended AI identity only the selected connector/query capabilities. Test that another identity and a query outside those grants are denied.

The API returns a `hasPassword` flag, not the encrypted or plaintext credential. Leaving the password empty on an edit retains it; selecting the clear option removes it. Changing provider, host, port, database, username or TLS identity invalidates the saved credential and requires it to be supplied again. An enabled connector must have a password and at least one query.

## Query and parameter contract

Administrators define one `SELECT` template per named query. Runtime callers supply only its parameter values. The implementation never substitutes argument text into the SQL string or accepts a caller-selected table, column, hostname, credential or SQL statement.

The lexical check is deliberately conservative: templates must start with `SELECT`, have one statement, and use simple identifiers. Comments, semicolons, dollar quoting, quoted identifiers and write/administration keywords are rejected. This also excludes otherwise legitimate constructs such as a leading `WITH` common-table expression. The check prevents common configuration mistakes; it is **not a SQL sandbox or a proof of absence of side effects**. Trusted administrator review and restricted database grants remain mandatory.

Use named `@parameter` placeholders for PostgreSQL, MySQL, MariaDB and SQL Server, and `:parameter` for Oracle. Parameter declarations must exactly match the template's placeholders. Oracle binding is explicitly by name. Use distinct, unambiguous parameter names and do not rely on a provider's case-folding behavior.

| Parameter type | JSON input | Native handling |
| --- | --- | --- |
| `string` | A string within its declared `maxLength` | Typed text parameter; never SQL interpolation. |
| `int64` | An integer within signed 64-bit range | Native signed integer. Clients must preserve the numeric literal exactly. |
| `decimal` | A JSON number exactly representable as a .NET `decimal`, using at most 128 literal characters | Typed decimal parameter with explicit precision/scale; values requiring rounding or nonzero underflow are rejected. |
| `bool` | `true` or `false` | Native boolean/bit; Oracle uses numeric `0`/`1` for compatibility with older supported servers. |
| `uuid` | Canonical hyphenated UUID string | Native PostgreSQL UUID / SQL Server uniqueidentifier; MySQL/MariaDB and Oracle bind a 36-character string. |
| `date` | `YYYY-MM-DD` | Date parameter, without a caller-supplied time or timezone. |
| `datetime` | ISO 8601 timestamp with `Z` or an explicit offset | Timestamp/offset representation; PostgreSQL and MySQL normalize to UTC, Oracle uses a timestamp-with-time-zone value. |

A required parameter must be present and non-null. An optional parameter may be omitted or explicitly null, in which case it binds SQL `NULL`. Unknown names, duplicate JSON properties, invalid types and excess values are rejected. Parameter types are not inferred from strings: for example, `"123"` is not accepted for `int64`.

For JavaScript callers, construct large integer/decimal JSON literals without a prior lossy floating-point conversion. The MCP tool accepts `argumentsJson` as a string, allowing an exact literal to be transmitted. Decimal binding verifies the original numeric value against its native representation, including scientific notation; it does not silently round a long fraction, a value above the range boundary or a nonzero value below the minimum scale to zero. Arbitrary-precision decimal inputs beyond .NET `decimal` are not accepted; use a reviewed string parameter and explicit server-side conversion when that behavior is necessary.

### Example disabled definition

The following request body for `POST /api/v1/admin/database-connectors` contains no password. Create this draft, then enter its credential through the private administrative workflow before probing or enabling it:

```json
{
  "definition": {
    "id": "reporting-db",
    "name": "Reporting views",
    "provider": "postgresql",
    "host": "db.example.invalid",
    "port": 5432,
    "database": "reporting",
    "username": "packory_reader",
    "tlsServerName": "db.example.invalid",
    "oracleServerCertificateDn": null,
    "enabled": false,
    "queries": [
      {
        "name": "orders_by_status",
        "description": "Read approved order summaries for one status",
        "sql": "SELECT order_id, total FROM reports.ai_orders WHERE status = @status ORDER BY order_id",
        "parameters": [
          { "name": "status", "type": "string", "required": true, "maxLength": 32 }
        ],
        "maxRows": 100,
        "timeoutSeconds": 5
      }
    ],
    "revision": 0
  },
  "password": null,
  "clearPassword": false
}
```

The saved response contains a new revision. Use that exact revision for subsequent updates and deletion; stale updates return `409`. To adapt the example to Oracle, select `oracle`, configure its approved TCPS target/service and certificate DN, and use `:status` in the template. Review database-specific schema names and SQL syntax for every provider.

## Give an AI narrowly scoped access

An AI database caller needs a validated OIDC bearer token for Packory's configured issuer and audience, containing all of:

- `packory.read` to use the existing authenticated MCP endpoint.
- `packory.database.read` for database query capabilities.
- A `packory_database` claim for every exact pair, for example `reporting-db:orders_by_status`.

These database grants are separate from remote-MCP `packory_connector` grants. Static read/write/admin tokens, managed package tokens, browser cookies and an unverified `HttpContext.User` do not grant database MCP access. An OIDC administrator also needs the exact database/query grants when using MCP. The administrative REST API remains available to authenticated operators for configuration and operational testing.

`list_database_queries()` lists only enabled connectors and queries granted to the caller. Each entry includes the query name, description, limits and JSON input schema. It excludes SQL, username, database address and password.

`run_database_query(connectorId, queryName, argumentsJson)` executes one enabled, approved and granted query. For the example above, its arguments are:

```json
{
  "connectorId": "reporting-db",
  "queryName": "orders_by_status",
  "argumentsJson": "{\"status\":\"open\"}"
}
```

Grants bind the connector ID and query name, **not a SQL digest or revision**. Changing a query's meaning under the same name changes what existing tokens can invoke. When widening access, introduce a new query name and review its grants; disable the connector while changing its access design. Use short-lived tokens and revoke issuer grants when access is no longer needed.

## Results and limits

A successful query returns column names, rows, `truncated`, `cellsTruncated` and `sourceDataUntrusted=true`. The latter is a reminder to treat database content as data, including when a cell contains instructions aimed at an AI.

Decimal and 64-bit integer values are invariant **JSON strings** to preserve exact precision in JavaScript/MCP consumers. Smaller integers and finite floating-point values retain numeric JSON types. SQL Server, Oracle and MySQL/MariaDB use their bounded native decimal types when needed to avoid loss beyond .NET `decimal` precision. PostgreSQL numeric output currently follows Npgsql's .NET decimal conversion; for greater precision or range, the reviewed SQL template must explicitly cast to bounded text. Non-finite floating-point values are represented as `[non_finite_number]`.

Dates/timestamps and UUIDs are formatted as strings. Binary values, arrays, composite/custom provider objects and unsupported object/LOB types are represented as `[unsupported_type]` instead of being materialized. Text is read through a bounded character buffer. Secret redaction operates on string values before JSON encoding and covers a reflected password cut by the cell limit. Redaction cannot make a malicious database trustworthy: a server that already knows a secret could encode or transform it.

| Limit | Value / behavior |
| --- | --- |
| Live connector definitions | 100 |
| Queries per connector / parameters per query | 32 / 32 |
| Serialized definition / SQL template | 48 KiB / 8,192 characters |
| Password | 1–2,048 UTF-8 bytes; stored encrypted, not returned |
| Administrative JSON request | 64 KiB; strict JSON and bounded nesting |
| MCP `argumentsJson` | 32 KiB |
| Rows per query | Configured from 1 to 1,000; an additional row determines the `truncated` flag |
| Columns / column-name length | 32 / 128 characters; duplicate names are rejected |
| Text per cell | 2,048 UTF-16 characters, without splitting a surrogate pair; `cellsTruncated=true` if cut |
| Retained serialized result budget | 128 KiB; an overrun fails rather than returning an oversized/partial result |
| Connection timeout | 8 seconds |
| Query command timeout | Configured from 1 to 30 seconds |
| Overall operation deadline | At most 35 seconds, in addition to bounded admission coordination; native cancellation is requested on expiry/client abort |
| Concurrent database operations | Four per Packory process, with no global wait queue |
| Same-connector configuration/call coordination | Shared bounded stripe lock, up to two seconds to acquire; a busy request returns `429` |
| Audit read | Latest 100 events for the connector |

The row limit is an output limit, not an optimizer/work limit. A query can scan many rows or perform expensive joins before producing its first result. Review indexes, predicates and execution plans, use database resource governance, and keep timeouts small. The overall deadline requests cancellation; native driver cleanup and a misbehaving remote server are not equivalent to a forcibly terminated isolated worker.

## Administrative API and audit

All routes below require Packory administrator access, an active writer, and the existing browser origin/CSRF protections where applicable. Responses use `Cache-Control: no-store`. Probes and query execution additionally require the feature's deployment enable flag.

| Method and route | Behavior |
| --- | --- |
| `GET /api/v1/admin/database-connectors` | Definitions, password-presence flags, operator targets, supported provider IDs and limits. |
| `GET /api/v1/admin/database-connectors/{id}` | One definition and its current revision, without credential bytes. |
| `POST /api/v1/admin/database-connectors` | Create with definition revision `0`; returns `201` and the saved revision. |
| `PUT /api/v1/admin/database-connectors/{id}` | Replace the definition using its current revision; retain, set or clear the password explicitly. |
| `DELETE /api/v1/admin/database-connectors/{id}?revision=N` | Delete the live definition at the matching revision; returns `204`. The ID remains reserved. |
| `POST /api/v1/admin/database-connectors/{id}/probe` | Test authenticated TLS and a fixed scalar query; may test a disabled connector. |
| `POST /api/v1/admin/database-connectors/{id}/query` | Run a saved query using `{ "query": "orders_by_status", "arguments": { "status": "open" } }`. The connector must be enabled. |
| `GET /api/v1/admin/database-connectors/{id}/audit` | Recent configuration and execution events. |

The audit records connector/query identifiers, action, credential role and time. It includes configuration changes, starts, success, failure, cancellation and applicable policy denials. It does not store passwords, query arguments, result rows or raw driver diagnostics. It is a local operational audit, not immutable external evidence or individual user attribution; not every rejected authentication request becomes a connector event. Retention/export policies remain an operator responsibility.

Errors are deliberately opaque. `400` indicates an invalid configuration/envelope, `403` unavailable execution/capability, `409` a stale revision/reserved identity/capacity conflict, `429` busy admission, `502` a connection/query failure, `503` unavailable keyring/writer, and `504` an observed timeout. Do not enable driver-level sensitive logging to work around these responses in a public deployment. Diagnose database permissions and TLS locally using synthetic or redacted evidence.

## Rotation, disablement and recovery

Credentials are encrypted with AES-256-GCM using an operator-owned keyring. Associated data binds the ciphertext to the connector ID and its provider, host, port, database, username and TLS identity. A database snapshot without the matching keyring cannot restore usable credentials. An exposed keyring plus metadata snapshot can reveal them; protect those backups separately.

To rotate an encryption key, add a new 32-byte key under a new key ID, set it active, and atomically replace the private keyring while retaining old keys. Re-enter/save each database password to encrypt it under the active key. Verify the relevant probes before retiring old keys. Existing ciphertext is not automatically re-encrypted, and historical backups still require their original keys. Rotate the database password itself through the database administrator and then update the Packory credential; keep any overlap short and deliberate.

To stop new calls, disable the connector and save its current revision, or disable the feature deployment-wide. Configuration changes share a gate with admitted calls. A busy update can return `429`; retry it and verify success. A successful disable prevents later admissions, but it cannot retract data already read or cancel every already-running external operation. Use database/session and network controls for an emergency stop.

Deletion removes the live definition and encrypted credential while preserving its reserved ID and audit. Reusing the ID is rejected so an old exact OIDC grant cannot attach to a new connector. Deletion is not secure erasure of database journals or old backups. Create a new ID when replacing a connector identity and issue fresh grants.

Back up the complete Packory metadata database, including `database_connectors`, `database_connector_ids`, `database_connector_audit` and the connector schema marker, together with the separate private keyring. Preserve tombstones on restore. Follow the normal [backup/recovery procedure](operations.md), restore to an isolated environment, keep database execution disabled, verify revisions/credentials/grants and only then enable reviewed targets. A restored snapshot may contain previously enabled definitions and historical authorizations; review it before allowing outbound traffic.

## Runtime and network boundaries

The current database gateway runs in the Packory application process. It uses no submitted code, stdio server or dynamically loaded provider. It also shares that process's CPU, memory, filesystem permissions and failure domain. Application row/cell/output limits do not bound every native driver's internal packet buffers.

The initial destination is pinned to an approved numeric IP and the certificate remains bound to the expected database identity. This does not prove that a provider never opens another socket: SQL Server routing, Oracle listener behavior, certificate/AIA lookups and native cancellation can have provider-specific network behavior. SQL Server/Oracle cluster redirection and arbitrary cloud failover arrangements are not qualified by the single-server fixture matrix.

Use deployment egress rules allowing only intended database destinations/ports and required infrastructure. Prefer a separately operated restricted database endpoint/network segment, and apply container/process memory and CPU limits. A dedicated isolated connector worker remains a separate hardening step; this implementation must not be described as hostile-server containment or general production certification. Row/tenant authorization and safe function behavior must be enforced in the database.

## Reproduce verification

Offline tests cover the native builders and certificate policies, exact parameter binding, argument rejection, bounded reads, numeric precision, cancellation admission and credential redaction. REST/MCP lifecycle tests cover separate scopes, grants, revision conflicts, writer restrictions and reserved IDs. Browser tests exercise the administrative workflow and untrusted text rendering.

```bash
dotnet restore Packory.slnx
dotnet build Packory.slnx -c Release --no-restore -warnaserror
dotnet test tests/Packory.Tests/Packory.Tests.csproj -c Release --no-build \
  --filter 'FullyQualifiedName~DatabaseConnector'
```

Real-provider tests are explicitly skipped without disposable fixture settings; skipped tests are not provider evidence. On a suitable Linux runner with Docker, OpenSSL, Python, .NET and passwordless `sudo` for the temporary CA trust operation:

```bash
bash scripts/qualify-database-connectors.sh postgresql
bash scripts/qualify-database-connectors.sh mysql
bash scripts/qualify-database-connectors.sh mariadb
bash scripts/qualify-database-connectors.sh sqlserver
bash scripts/qualify-database-connectors.sh oracle
```

Each fixture creates synthetic data and separate privileged-setup/SELECT-only accounts, requires real TLS, and runs parameter/injection, precise numeric output, permission-denial, password-denial, certificate identity and output-limit cases. The harness then removes its test CA, checks that the same correctly named server is rejected, restores trust and reruns a positive control. It rejects empty or skipped test runs and exports sanitized exact-source/provider/image evidence. It does not upload raw database logs or credentials. Treat SQL Server/Oracle test-image licenses separately from Packory's license.

At implementation time, the new real-provider matrix is a required verification workload, not a pre-existing passed qualification. Read the final exact-commit workflow artifacts before asserting a provider passed. Local mocks and option-level tests do not substitute for those results.

## Primary references

- [Npgsql TLS security](https://www.npgsql.org/doc/security.html) and [connection options](https://www.npgsql.org/doc/connection-string-parameters.html).
- [PostgreSQL transaction access mode](https://www.postgresql.org/docs/current/sql-set-transaction.html).
- [MySqlConnector connection options](https://mysqlconnector.net/connection-options/), [certificate callback implementation at 3.0.0](https://github.com/mysql-net/MySqlConnector/blob/3.0.0/src/MySqlConnector/Core/ServerSession.cs), and [command cancellation](https://mysqlconnector.net/overview/command-cancellation/).
- [MySQL transaction access mode and exceptions](https://dev.mysql.com/doc/refman/8.4/en/set-transaction.html).
- [SqlClient availability/read-only routing](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/sqlclient-support-high-availability-disaster-recovery?view=sql-server-ver17) and [7.1.2 release notes](https://github.com/dotnet/SqlClient/blob/main/release-notes/7.1/7.1.2.md).
- [Oracle ODP.NET connection and TLS configuration](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/featConnecting.html), [transaction access mode](https://docs.oracle.com/en/database/oracle/oracle-database/26/sqlrf/SET-TRANSACTION.html), and [invariant decimal conversion overload](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/DecimalToString2.html).

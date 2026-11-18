# ADR 0004: Operator-approved remote MCP and database connectors

Status: accepted for an opt-in technical preview, 18 November 2026. Qualification applies to named tests and the exact source commit; this decision does not close the isolated-gateway or production-release gates.

## Context

Packory already distributes inert MCP Registry descriptions and exposes read-only catalog tools. The requested next step is to administer approved remote MCP services and native database queries through the same dashboard and authorization system. Registry publication must remain separate from permission to contact a service or disclose a credential.

The [original platform boundary](0001-v3-platform-boundaries.md) and [MCP roadmap](../mcp-roadmap.md) reserve hostile evaluation and a fully isolated connector gateway for a separate execution boundary. The narrower implementation here serves trusted, operator-selected services. It makes no process-isolation promise and does not launch code, stdio commands, downloaded packages or database plugins.

## Decision

Implement two explicit modules in the existing ASP.NET Core application:

- `McpConnectorPolicy`, `McpConnectorStore`, `McpConnectorSecrets` and `McpConnectorGateway` manage approved HTTPS Streamable HTTP endpoints and exact remote tool allowlists. Registry records cannot create active connectors.
- `DatabaseConnectorPolicy`, `DatabaseConnectorStore`, `DatabaseConnectorService` and `DatabaseConnectorExecutor` manage PostgreSQL, MySQL, MariaDB, SQL Server and Oracle password-authenticated connections and administrator-reviewed named SELECT templates. Runtime clients provide typed arguments, never SQL or connection strings.

Share the existing administrator/OIDC authentication, metadata providers and a private operator-owned AES-256-GCM keyring. Keep remote-tool and database-query permissions distinct. Database MCP access requires validated OIDC scopes and an exact connector/query claim even for an administrator; the administrative REST probe/test workflow is a separate capability. Remote MCP administrator use follows its documented administrator policy.

The deployment operator owns the exact network target and private-address exception lists. An administrator can manage definitions only within that boundary. Resolve and validate all DNS answers before execution, pin the initial socket destination, authenticate TLS and reject unsupported configuration. Driver behavior can include secondary connections and certificate lookups, so enforce complete egress policy outside the application when required.

Connector IDs remain reserved after deletion. Optimistic revisions and a bounded per-connector operation gate serialize local mutation and execution. Recheck the active-writer lease at execution boundaries; readers and maintenance processes cannot execute connectors. An acknowledged disable follows an already-running operation and denies later operations. It does not undo a request already dispatched to a remote service. These are single-active-writer guarantees, not distributed fencing of external side effects.

Persist configuration mutations and audit records transactionally in SQLite or PostgreSQL. Database schema version 1 is additive and independent of the existing artifact schema. The snapshot verifier treats each connector table family as an all-or-nothing optional group, while accepting genuine older snapshots without connector tables. Preserve ID tombstones, audit and the private keyring during recovery.

## Consequences

The feature remains disabled until an operator configures approved destinations; database execution also requires its own explicit enable flag. Credentials are encrypted at rest and never returned by read APIs. Bind ciphertext to the connector identity and destination; changing connection identity invalidates the saved credential. Key rotation requires an explicit re-save before retiring an old key.

Remote responses, schemas, pagination, concurrency and deadlines are bounded. Database results have row, column, cell and serialized-output limits, typed parameter binding, command deadlines and cancellation. These application limits do not bound every native-driver allocation or guarantee immediate cancellation of a server operation. Returned content is untrusted data, including tool descriptions and database cells.

A SELECT-only lexical check prevents common configuration mistakes. It is not a SQL sandbox: functions, owner rights, database links and external privileges can make a SELECT sensitive or side-effecting. Dedicated least-privilege logins, reviewed views and database-enforced tenant/row policy are necessary. SQL Server has no read-only transaction setting equivalent to the other implemented profiles; `ApplicationIntent` is routing, not authorization.

Native provider tests use real TLS-enabled disposable engines, independent restricted logins, wrong-name/untrusted-CA negatives, parameter injection and output-bound checks. The complete qualification gate requires all five jobs on the exact commit. Unit and synthetic browser tests establish separate contracts. A package vendor's support statement, a passing fixture and a Packory production qualification are different claims.

## Alternatives and deferred work

A separate worker/gateway remains the required design when offering process containment, untrusted-service execution, stronger resource isolation or independently scoped network credentials. That change needs a versioned request/result protocol, fenced execution ownership, independent deployment/recovery and its own acceptance evidence. It should not be represented by an in-process interface alone.

Arbitrary SQL, stored-procedure execution, automatic schema exposure, generic ODBC/JDBC, Windows/Kerberos authentication, cloud identity refresh, remote OAuth brokerage and automatic credential rotation are outside this profile. Additional providers require concrete driver/TLS behavior, least-privilege guidance and native positive/negative qualification before being advertised.

See [remote MCP operations](../mcp-connectors.md), [database operations](../database-connectors.md), [dependency notices](../../THIRD-PARTY-NOTICES.md) and [CI qualification](../ci-qualification.md).

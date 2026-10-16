# Feed control plane — current experimental coverage

Packory now exposes an admin-only, versioned **read-only feed metadata inventory**. Six legacy descriptors explain which ecosystems share existing global native URLs. Read access is administrator-only and returns `isolationSupported: false` to prevent clients from interpreting descriptors as independently routed/authorized feeds.

| ID | Kind | Mode | Wire behavior |
| --- | --- | --- | --- |
| `default-nuget` | NuGet | legacy | Existing `/nuget/v3/index.json` |
| `default-npm` | npm | legacy | Existing `/npm/` |
| `default-cargo` | Cargo | legacy | Existing `/cargo/index/` |
| `default-pypi` | PyPI | legacy | Existing `/pypi/simple/` |
| `default-oci` | OCI | legacy | Existing `/v2/` |
| `default-skill` | Agent Skills | legacy | Existing `/api/skills` |

## Authenticated read endpoints

`GET /api/v1/admin/feeds?limit=25&after=default-nuget` returns `{apiVersion:"v1",items:[...],nextCursor, isolationSupported:false}`. Valid limits are 1–100; invalid cursors or IDs fail closed. `GET /api/v1/admin/feeds/{id}` returns one descriptor or 404. Admin API tokens or correctly scoped OIDC admin access are required; a read or publish credential is forbidden. Responses use `Cache-Control: no-store`.

## Non-goals and pending MS2 gates

This implementation is **not** multiple hosted/proxy feeds, tenant separation or feed-CRUD. Despite empty prepared tables for namespace/policy/upstream, **no cross-feed isolation exists yet**. Do not create feed records directly in the DB and expect native requests to route to them; ad hoc SQL mutations bypass policy validation and are unsupported. Next gates: explicit feed routing, legacy compatibility, authorization of native reads/writes, namespace reservations, durable CAS/reference migration, quota, audit, rollback and real provider restore tests. See [schema versioning](schema-versioning.md), [roadmap](roadmap.md), and [support matrix](support-matrix.md).

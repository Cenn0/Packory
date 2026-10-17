# Feed control plane — experimental metadata inventory and disabled drafts

Packory now exposes an admin-only, versioned **feed metadata inventory with disabled-only draft lifecycle**. Six legacy descriptors explain which ecosystems share existing global native URLs. Read access is administrator-only and returns `isolationSupported: false` to prevent clients from interpreting descriptors as independently routed/authorized feeds.

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

## Staged administrative drafts (no runtime routing)

Admins may **prepare disabled, private** feed descriptors for review. This
capability is a database-backed metadata staging step, **not** hosted/proxy
feed functionality. It does not register native URLs, publish artifacts, grant
access, connect upstreams, reserve namespaces, or make feeds visible to
native clients.

`POST /api/v1/admin/feeds/drafts` accepts **exactly** four JSON string
properties, e.g.:

```json
{"id":"example-staging","kind":"nuget","mode":"hosted","displayName":"Example staging"}
```

`kind` is one of `nuget`, `npm`, `cargo`, `pypi`, `oci`, `skill`.
`mode` is `hosted` or `proxy`. The service **always** assigns
`enabled: false` and `visibility: private`; clients cannot submit these
fields or an upstream URL. New IDs cannot start with `default-`. Successful
creation returns 201 with `operational: false` and
`isolationSupported: false`; duplicate IDs return 409. The submission
must be <=4096 bytes. Admin tokens or scoped OIDC administrators are
required; ordinary write/read tokens cannot create drafts.

`DELETE /api/v1/admin/feeds/drafts/{id}` removes only an **unused, disabled,
private, non-legacy** draft with no namespace, policy or upstream references.
It returns 204 on removal, 409 if missing/non-draft/referenced, and 400
for invalid identifiers. Both create and delete persist an audit event in
the same DB transaction. Listing remains admin-only and never exposes
native-route declarations. Do not mistake the presence of a staged descriptor
for an operating feed.

## Non-goals and pending MS2 gates

This implementation is **not** operational hosted/proxy feeds, tenant separation or full feed-CRUD. Despite empty prepared tables for namespace/policy/upstream, **no cross-feed isolation exists yet**. Do not create draft feed records via the API or directly in the DB and expect native requests to route to them; ad hoc SQL mutations bypass policy validation and are unsupported. Next gates: explicit feed routing, legacy compatibility, authorization of native reads/writes, namespace reservations, durable CAS/reference migration, quota, audit, rollback and real provider restore tests. See [schema versioning](schema-versioning.md), [roadmap](roadmap.md), and [support matrix](support-matrix.md).

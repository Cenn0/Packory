# Feed control plane — V4 private NuGet hosted feeds (experimental)

Packory exposes a versioned **admin-only feed inventory**, disabled drafts for all protocol types and the first explicitly activated, private NuGet hosted feed adapter. Six legacy descriptors explain which ecosystems share existing global native URLs. Read access is administrator-only and returns `isolationSupported: false` to prevent clients from interpreting descriptors as independently routed/authorized feeds.

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

## Staged administrative drafts (only reviewed private NuGet hosted feeds can be activated)

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

This implementation is **not** general hosted/proxy feed support, tenant separation or full feed-CRUD. The isolated NuGet hosted slice is documented below; all other kinds/modes remain staging-only. Do not create draft feed records via the API or directly in the DB and expect native requests to route to them; ad hoc SQL mutations bypass policy validation and are unsupported. Next gates: explicit feed routing, legacy compatibility, authorization of native reads/writes, namespace reservations, durable CAS/reference migration, quota, audit, rollback and real provider restore tests. See [schema versioning](schema-versioning.md), [roadmap](roadmap.md), and [support matrix](support-matrix.md).


## V4 experimental private hosted NuGet feeds — first operational slice

Packory now has an **independently routed, private hosted NuGet adapter**.
This is a narrow MS2 slice, **not** universal multi-feed isolation or a
verified group/proxy implementation. Only an admin may activate/deactivate
an existing disabled draft with `kind=nuget` and `mode=hosted`. All
drafts for npm/Cargo/PyPI/OCI/Skills and all proxy feeds remain
**non-operational**. The legacy `/nuget/v3/` namespace is unchanged.

### Lifecycle

1. As administrator, `POST /api/v1/admin/feeds/drafts` with
   `{"id":"alpha-feed","kind":"nuget","mode":"hosted","displayName":"Synthetic"}`.
2. Review the descriptor, then use
   `POST /api/v1/admin/feeds/alpha-feed/activate` (no body).
3. Distribute the scoped private source
   `/feeds/alpha-feed/nuget/v3/index.json` to authorized clients.
   Service index, flat-container versions, packages, checksums, nuspec
   and NuGet push are supported on this route with **no upstream fallback**.
4. To stop reads and writes without deleting bytes, call
   `POST /api/v1/admin/feeds/alpha-feed/deactivate`. The package data,
   immutable SHA-512, audit and publication journal are retained.
   Reactivation requires an explicit administrative call. A feed
   containing published revisions cannot be deleted as an unused draft.

These endpoints are **never anonymously readable**, even if `PACKORY_ANONYMOUS_READ`
permits public legacy registry reads.
Unrecognized or disabled feeds return 404 **after feed authorization**,
so credentials lacking a feed grant receive a uniform 403.

### Least-privilege scoped credentials

The existing revocable managed-token admin endpoint accepts these
**exact** `namespace` grants (no wildcard):

- `feed:alpha-feed:read` with `role: "read"`
- `feed:alpha-feed:publish` with `role: "write"`

A publish grant also permits reads for **that same feed**. Generic
read/write tokens, another feed's tokens and anonymous clients do not
gain entry. Static administrators may perform both operations. An
authenticated OIDC access token may use `packory.read` (or
`packory.write` for publication) plus a signed
`packory_feed` claim of `alpha-feed:read` or
`alpha-feed:publish`. No browser token is persisted.

A feed-specific managed token cannot publish into the old global
`/nuget/v3/push` endpoint; the standard package namespace grant
parser rejects feed grants. Runtime token revocation is immediate on
subsequent requests. This is feed-level—not per-package—authorization.

### Isolation, data safety and limits

Two feeds may each host `same.package@1.0.0` with different bytes and
SHA-512. Keys are physically separated as
`feeds/<feed-id>/nuget/<id>/<version>/<id>.<version>.nupkg`, and
metadata is keyed by `(feed_id,name,version)`. Private revision records
are excluded from the general package search and legacy NuGet listing.
Published bytes have a durable publication-intent journal and a
transactional immutable digest/audit record.

The new `feed_nuget_versions` table is included in V4 PostgreSQL/S3
offline backup fingerprints, recovery orphan protection, missing-blob
checks, and the maintenance integrity scanner.

**Out of scope:** NuGet registration/search for isolated feeds, private
proxy/group feeds, feed-specific S3 capacity accounting, per-package
ownership, tenant web UI/BFF and support for the other ecosystems.
Native CLI CI covers a bounded `dotnet nuget push` + restore use case
but does not certify all NuGet versions or tooling combinations.
Operational single-writer and real-provider release gates still apply.

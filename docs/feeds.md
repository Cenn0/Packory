# Feed control plane — E0 private hosted NuGet, Raw, Maven and Go (experimental)

Packory exposes a versioned **admin-only feed inventory**, disabled drafts for all protocol types and explicitly activated, private NuGet and Raw hosted adapters. Six legacy descriptors explain which ecosystems share existing global native URLs. Read access is administrator-only and returns `isolationSupported: false` to prevent clients from interpreting descriptors as independently routed/authorized feeds.

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

## Staged administrative drafts (only reviewed private NuGet, Raw, Maven or Go hosted feeds can be activated)

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

`kind` is one of `nuget`, `npm`, `cargo`, `pypi`, `oci`, `skill`, `raw`, `maven`, `go`.
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

This implementation is **not** general hosted/proxy feed support, tenant separation or full feed-CRUD. The isolated NuGet, Raw, Maven and Go hosted slices are documented below; the other kinds and all proxy modes remain staging-only. Do not create draft feed records via the API or directly in the DB and expect native requests to route to them; ad hoc SQL mutations bypass policy validation and are unsupported. Next gates: explicit feed routing, legacy compatibility, authorization of native reads/writes, namespace reservations, durable CAS/reference migration, quota, audit, rollback and real provider restore tests. See [schema versioning](schema-versioning.md), [roadmap](roadmap.md), and [support matrix](support-matrix.md).


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

**Global read boundary:** when `PACKORY_ANONYMOUS_READ=false`, managed tokens
with any feed grant or package publish-namespace grant receive **403** on
legacy/global NuGet, npm, Cargo, PyPI, OCI, Raw, Maven and Go read APIs,
as well as the global catalog. Such grants authorize their exact private
feed or publication namespace, **not** global discovery. Unscoped managed
readers and static readers retain access. When anonymous legacy reads are
explicitly enabled, those public URLs remain public regardless of a supplied
token; private feed URLs continue to enforce exact grants and never honor
anonymous reads. This is a fail-closed interim measure: it does **not**
implement package-level private reads for the global registries or add
private feeds for other protocols.

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

## V8 isolated private Raw feeds (E0 incremental)

Private Raw is independently activated with `kind=raw`, `mode=hosted`,
`visibility=private`. Only administrators may activate/deactivate it. For
example, create a disabled draft with
`{"id":"alpha-feed","kind":"raw","mode":"hosted","displayName":"Synthetic Raw"}`
and then call `POST /api/v1/admin/feeds/alpha-feed/activate`.

For exact `feed:alpha-feed:publish` and `feed:alpha-feed:read`
managed-token grants, the following routes are available:

- `PUT /feeds/alpha-feed/raw/{name}/{version}` (nonempty fixed Content-Length)
- `GET|HEAD /feeds/alpha-feed/raw/{name}/{version}`
- `GET /feeds/alpha-feed/raw/{name}/{version}/metadata`

Raw name/version/media type constraints match the global Raw adapter. Uploads
are immutable and SHA-256 validated, returning 201 or 409 for an existing
revision. Every write stores a publication intent **before** the immutable
blob and commits the checksum, scoped metadata and audit together. Two
private feeds can use exactly the same name/version with different bytes.
Private keys are `feeds/<id>/raw/<name>/<version>/payload.blob` and the
metadata key is `(feed_id,kind,path)`. Private revisions cannot appear in
the global Raw catalog or legacy `/raw/` URLs. There is no upstream proxy,
fallback, automatic mirror, cross-feed read, URL-controlled fetch, delete or
arbitrary remote execution. A disabled feed retains all bytes and grants but
neither reads nor publishes. Deleting a referenced disabled feed is refused.

Recovery and integrity enumeration include the V8 ledger; the PostgreSQL/S3
snapshot tool requires row fingerprints for `feed_assets`. CLI/API and
SQLite/PostgreSQL coverage are **experimental**, not real S3-provider DR
certification. This slice does not implement Go feeds,
other private ecosystem adapters, cross-feed search, per-feed quotas,
browser-based permissions or production-grade multiwriter semantics.

## V4 E0 private Maven hosted release transport (experimental)

Private Maven feeds use the same disabled-draft and administrator-activation
lifecycle as Raw. Create a draft with `kind: "maven"`, `mode: "hosted"`,
then explicitly activate it. Proxy drafts are not activatable; supplying
an upstream URL to a draft is not supported.

For a private feed `alpha-maven`, the native repository root is
`/feeds/alpha-maven/maven/`. This is independently authorized, without
upstream or legacy namespace fallback. Use exact managed token grants
`feed:alpha-maven:read` for consumers and `feed:alpha-maven:publish`
for uploaders. The latter also permits same-feed reads. Native Maven 3
consumers can use a configured server ID and Basic password containing
the scoped token. Every request requires authentication even when
anonymous reads are enabled for the legacy global `/maven/` route.

Available operations:

- `PUT /feeds/{feedId}/maven/{groupPath}/{artifact}/{version}/{file}`
  accepts validated immutable Maven release POM and binary/classifier files.
  POMs must match the requested group/artifact/version; XML external entities
  and DTDs are rejected. Duplicate assets return 409.
- `GET|HEAD` at the same path verifies the stored SHA-256 and length
  before delivery. Missing/tampered artifacts fail closed.
- The `.sha256`, `.sha1` and `.md5` GET sidecars are derived from
  verified immutable stored bytes. Matching checksum PUT acknowledgements
  are accepted only after the corresponding asset is committed; they never
  mutate bytes or trust client-provided digests.
- `GET|HEAD {groupPath}/{artifact}/maven-metadata.xml` returns a
  synthesized private version list only from the requested feed.
  Checksum GETs are supported for that synthetic metadata.

Files use `feeds/<feedId>/maven/<canonical-path>` keys with immutable
`feed_assets` metadata, transactional audit and a durable
`feed-maven` publication intent for cross-store reconciliation.
No private coordinate appears in the global Maven catalog or the
legacy `/maven/` namespace. Deactivation disables requests while
retaining the immutable metadata and bytes.

**Not supported:** native Maven `mvn deploy` (mutable metadata PUT),
SNAPSHOT, private Maven upstream proxy, full Gradle conformance,
per-feed quotas, multi-writer coordination, real-provider S3 disaster
recovery or production suitability. The CI native Maven dependency
consumption is a bounded test of the specified client/operation only.

## V4 E0 private Go modules — bounded hosted GOPROXY

An administrator may explicitly activate disabled `kind=go`,
`mode=hosted`, private drafts. Private Go clients use
`/feeds/{feedId}/go/` as their GOPROXY root. Packory-specific publishing
accepts an authenticated multipart POST to
`/feeds/{feedId}/api/go/modules` with `module`, `version`,
`mod` and `zip` plus optional RFC3339 `time`.

Exact managed grants `feed:{feedId}:read` and
`feed:{feedId}:publish` are required. Missing credentials get 401;
credentials for a different feed get 403. An inactive but otherwise
authorized feed returns 404. No global Go module, public mirror, or
other feed is consulted as a fallback. Private module discovery does not
appear in global catalog routes.

One validated GOPROXY revision consists of `.info`, `.mod` and
`.zip` in a single immutable bundle under
`feeds/{feedId}/go/<hashed-module>/<hashed-version>.bundle`.
The shared V8 `feed_assets` ledger stores the canonical encoded
coordinate and SHA-256+size of the atomic bundle. The nested Go resource
digests are independently re-derived and checked only **after**
the committed bundle hash is verified; they are not trusted from an
incoming request. Publication uses the durable `feed-go` intent,
feed-qualified lock, immutable blob, then transactional ledger+audit.
Offline recovery and integrity scans enumerate the same ledger.

Read-only GOPROXY supports `@v/list`, `@latest`, versioned
`.info`, `.mod` and `.zip`. The native client harness checks
two private feeds storing different bytes under identical coordinates,
with separate credentials and module caches over an ephemeral HTTPS proxy.
The official Go command sends GOAUTH/.netrc credentials **only via HTTPS**;
plaintext HTTP private Go clients must not be advertised as supported.
A `.netrc` entry is scoped to a hostname, not a URL path: multiple
independently scoped feeds on the same host require separate client
credential contexts, dedicated hosts, or a reviewed path-scoped
`GOAUTH` helper on supported Go versions (Go 1.24+). Never embed
long-lived tokens in `GOPROXY` URLs, shell history or CI logs.
The isolated synthetic test disables the external Go checksum database;
**public checksum trust is not implemented**.

No public read-through mirror, automatic checksum database trust,
universal Go archive acceptance, multi-writer/tenant isolation,
real-provider restore certification, or production suitability is
claimed. Keep broad Go ecosystem support gated on E3/E11.

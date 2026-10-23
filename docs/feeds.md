# Private hosted feeds — E0 package foundation

**Implemented:** independently routed private hosted NuGet, npm, Cargo, PyPI,
OCI, Maven, Go and Raw feeds. **E0 acceptance:** local native, provider,
fault/restore and complete test suites passed, as did the published-commit
GitHub gate recorded in [E0 acceptance](e0-acceptance.md). The later Maven E2
profile and its separate evidence are described in [Maven acceptance](maven.md).
The protocol profiles below describe available operations and their limits.
Proxy feeds and private Skills remain disabled drafts; group routing and
feed-aware MCP discovery are separate work.

## Inventory and legacy URLs

The administrator-only control plane inventories both legacy descriptors and
private feed definitions. It does not make a global package private merely by
creating a record. Existing global routes remain available:

| Kind | Legacy root | Private hosted root |
| --- | --- | --- |
| NuGet | `/nuget/v3/` | `/feeds/{id}/nuget/v3/` |
| npm | `/npm/` | `/feeds/{id}/npm/` |
| Cargo | `/cargo/` | `/feeds/{id}/cargo/` |
| PyPI | `/pypi/` | `/feeds/{id}/pypi/` |
| OCI | `/v2/{repository}/` | `/v2/feeds/{id}/{repository}/` |
| Maven | `/maven/` | `/feeds/{id}/maven/` |
| Go | `/go/` | `/feeds/{id}/go/` |
| Raw | `/raw/` | `/feeds/{id}/raw/` |
| Agent Skills | `/api/skills` | Not implemented |

**OCI compatibility boundary:** the repository prefix `feeds/` under `/v2/`
is reserved for private routing. It cannot also name a legacy global OCI
repository. Other existing global roots and artifact keys are retained.
There is no automatic migration of global artifacts into a private feed.

The six seeded `default-*` descriptors describe NuGet, npm, Cargo, PyPI, OCI
and Skills legacy routes. They do not confer read authorization or isolation.
`GET /api/v1/admin/feeds?limit=25&after=default-nuget` returns `apiVersion`,
`items`, `nextCursor`, the retained compatibility field
`isolationSupported: false`, and `isolatedHostedKinds` for the implemented
package adapters. That compatibility field is not a statement that an active
private hosted package feed lacks isolation. Limits are 1–100.
`GET /api/v1/admin/feeds/{id}` returns one descriptor or 404.
Both operations require an administrator and return `Cache-Control: no-store`.

## Draft, activate and deactivate

`POST /api/v1/admin/feeds/drafts` accepts exactly four string properties:

```json
{"id":"alpha-feed","kind":"npm","mode":"hosted","displayName":"Example packages"}
```

The body is bounded to 4096 bytes. IDs contain 3–64 lowercase ASCII letters,
digits or hyphens, begin with a letter and end with a letter or digit; new
IDs cannot begin with `default-`. Kinds are `nuget`, `npm`, `cargo`, `pypi`,
`oci`, `skill`, `raw`, `maven` or `go`. Modes accepted for staging are
`hosted` and `proxy`; `group` is not an accepted draft mode.
The server assigns `visibility: private` and `enabled: false`; callers cannot
supply these fields or an upstream URL. Success returns 201 with
`operational: false`, and duplicate or historically reserved IDs return 409.
Creation also rejects a previously deleted ID if an activation audit, exact
managed read/publish grant or pending feed publication still references it.
An unreferenced, never-activated draft ID may be reused after deletion.

A draft alone has no serving capability or granted access. For one of the
eight implemented hosted package kinds, an administrator may call
`POST /api/v1/admin/feeds/{id}/activate` without a body. Unsupported kinds,
proxy drafts or an invalid lifecycle transition return 409.
`POST /api/v1/admin/feeds/{id}/deactivate` stops client reads and writes while
retaining package bytes, metadata, tags, grants and publication evidence.
Reactivation is a separate explicit call.

`DELETE /api/v1/admin/feeds/drafts/{id}` removes only a **never-activated**,
disabled, private, non-legacy draft with no package, namespace, policy,
upstream, managed feed-grant or pending-publication references. Once a feed
has been activated, its ID remains reserved even when empty and disabled;
old managed credentials or external OIDC claims cannot attach to a newly
created identity with the same ID. Reactivating that original feed remains
possible. Removal returns 204; an invalid ID returns 400; a missing,
previously activated or referenced draft returns 409. Lifecycle mutations require
administrator access, use the publish rate limiter and persist audit records.

## Exact feed authorization

Every private native operation authenticates and checks the exact feed grant,
including metadata, downloads, upload sessions, tag lists and referrers.
`PACKORY_ANONYMOUS_READ=true` applies to configured global routes and never
makes private feeds public.

Managed tokens are issued through the existing administrator endpoint
`POST /api/admin/tokens` with either of these grant shapes:

```json
{"role":"read","namespace":"feed:alpha-feed:read","expiresInHours":1}
```

```json
{"role":"write","namespace":"feed:alpha-feed:publish","expiresInHours":1}
```

A publish grant also allows reads from that same feed. Generic static
read/write credentials, package namespace grants and another feed's token do
not allow private feed access. Static administrators and validated OIDC
administrators may operate on all feeds. OIDC readers/publishers need the
appropriate `packory.read`/`packory.write` scope plus a signed `packory_feed`
claim of `{id}:read` or `{id}:publish`. Tokens are revocable on subsequent
requests. This is authorization at feed granularity, without per-package
ownership or a browser account/session system.

For valid private route shapes, missing/invalid credentials return 401;
authenticated credentials without the exact permission return 403. The
adapter checks its active feed and coordinate after authorization, so an
authorized missing/deactivated/wrong-kind feed or missing package returns
404. A token for a different feed continues to receive 403, including when
the requested feed is absent. Invalid coordinates are rejected before any
storage-key construction.

When anonymous global reads are disabled, a managed feed or package namespace
token receives 403 from global registry reads and catalog discovery. The
OCI `/v2/` capability ping is a protocol handshake exception: it may accept
an authenticated feed credential without granting access to any global
repository. `/mcp` requires a global reader and excludes private feed rows;
feed-aware discovery is [MCP-P5](mcp-roadmap.md).

## Hosted origin and storage contract

A private request queries only its feed's ledger and storage. Missing content
is never substituted from another feed, a global coordinate, a configured
NuGet/npm mirror, or an arbitrary URL. Generated download URLs use
`PACKORY_PUBLIC_BASE_URL`, never an untrusted Host header. Client test configs
also disable alternative sources; a registry cannot prevent arbitrary code
on a client machine from opening its own network connections.

Schema **V10** extends `feed_assets`, keyed by `(feed_id, kind, path)`, for
Raw, Maven, Go, npm, Cargo, PyPI and OCI. NuGet retains its existing
`feed_nuget_versions` ledger and immutable SHA-512. OCI mutable tag pointers
are separate `feed_oci_tags` references to immutable manifest digests.

npm, Cargo and PyPI use `FeedPackageStore` to commit protocol metadata and
original package bytes together in one immutable object at
`feeds/{id}/{kind}/{sha256(canonical-path)}.bundle`. The envelope binds feed,
kind, canonical path, native name/version, payload size and payload SHA-256.
Reads verify the committed outer SHA-256/length, inner identity and payload
digest before exposing metadata or bytes. Adapters also validate archive
identity and protocol-specific checksum claims. Go similarly commits its
`.info`, `.mod` and `.zip` in one validated immutable triplet bundle.

Immutable artifact publishing writes a durable intent before the object, then commits scoped
metadata and audit. A crash between object and metadata writes does not
publish a guessed package entry. Recovery retains pending evidence or
quarantines an unindexed local object; a committed missing/corrupted object
fails closed. Recovery, integrity scans and offline snapshots enumerate the
same feed references, including OCI tags. Database/object storage writes
remain separate operations, without a distributed transaction or multiple
active writers. See [architecture](architecture.md) and the exact
[provider/recovery acceptance](e0-acceptance.md).

## NuGet

Native source: `/feeds/{id}/nuget/v3/index.json`.
Supported operations are service index, flat-container version list,
`.nupkg`, `.nupkg.sha512`, `.nuspec` and multipart `PUT/POST push`.
Read credentials use the native NuGet source configuration; publication also
accepts `X-NuGet-ApiKey`. Private reads issue a Basic authentication challenge
when a native client needs to retry with its configured credentials.

Identical ID/version pairs can have different bytes in different feeds.
Package files remain under `feeds/{feedId}/nuget/{packageId}/{version}/...` with their
immutable digest ledger. The private profile does not implement registration,
search, symbols, unlisting or proxy/group feeds. Native evidence is a bounded
pack/push and isolated restore, including fresh-cache native 401/403/404
failures, not a broad NuGet client/OS matrix.

## npm

Registry root: `/feeds/{id}/npm/`. Native `npm publish` sends a one-version
packument with one base64 tarball attachment to `PUT {package}`. Scoped and
unscoped names are accepted. Reads provide the packument, exact version or
publication tag, and verified `/{package}/-/{version}.tgz` bytes; authenticated
`-/ping` and `-/whoami` support native tooling. Configure `_authToken` for the
exact feed URL path and clear unrelated registry configuration when testing.

The manifest and archive package identity must agree. Canonical publication
versions follow npm's cleaned SemVer identity; build metadata does not create
a second immutable version. Incoming SHA-512/SHA-1 and attachment length are
checked; returned tarball URLs and integrity values derive from stored bytes.
Publication tags are recorded with the version, and later publications may
update the packument's tag selection. There is no separate mutable dist-tag
API, unpublish, npm login, audit API or provenance-attachment workflow.
Dependencies may use registry ranges/tags/aliases; direct Git, HTTP, file and
workspace dependency origins are rejected in this private hosted profile.

## Cargo

Configure a native alternate registry with the sparse URL
`sparse+https://<host>/feeds/{id}/cargo/index/` and its own Cargo token provider.
`GET index/config.json` returns feed-local `dl` and `api` URLs with
`auth-required: true`. Sparse index entries, native
`PUT api/v1/crates/new` publishing and
`GET|HEAD api/v1/crates/{name}/{version}/download` require exact feed grants.

Index metadata and unchanged `.crate` bytes share one immutable bundle.
Strict SemVer is bounded; build metadata is ignored for uniqueness.
Explicit foreign dependency registries are rejected. V14 provides an experimental
feed-local yank/unyank profile with transactional audit and immutable crate bytes;
crates.io replacement/proxy, search and broad Cargo-version compatibility
remain outside this profile.

## PyPI

Use `/feeds/{id}/pypi/simple/` as the sole pip index and
`/feeds/{id}/pypi/legacy/` as Twine's upload repository. Simple project pages
support HTML and JSON with file hashes, sizes, versions and `requires-python`;
files are served from `/feeds/{id}/pypi/files/{name}/{filename}`. Names are
normalized, filenames and archive metadata must agree, and uploaded SHA-256
claims are verified. Multiple immutable distribution filenames can belong to
one project version.

The version parser accepts a bounded canonical PEP 440 subset with prerelease,
post/dev and local segments; epochs and noncanonical aliases are rejected.
Wheel, ZIP and `.tar.gz` archive validation exists, while current native
consumption evidence covers pure Python wheels and a transitive dependency.
It does not establish source-build/backend compatibility. Direct-reference
`Requires-Dist` URLs are rejected, and tests disable `extra-index-url` and
alternative sources. There is no separate PEP 658/714 core-metadata endpoint,
yank API or PyPI proxy.

## OCI and Helm

Native repository names begin `feeds/{id}/{repository}` beneath `/v2/`, for
example `oci://<host>/feeds/alpha-feed/charts`. Blobs, manifests, tags,
referrers and durable upload sessions are scoped by both feed and repository.
Digest identities use SHA-256 and are immutable; tags are intentionally
mutable references to validated immutable manifests. Manifest content
references must resolve within the same private repository. A subject may be
absent as permitted by the accepted OCI profile.

Alternate descriptor `urls` are unsupported and rejected, including on
config, layers, blobs, manifests and subjects. Inline descriptor `data` must
be canonical Base64 with the exact declared SHA-256 and size; it does not
replace the required same-feed/repository ledger reference. This prevents a
manifest from directing a native client to an alternate artifact origin.

Uploads support monolithic and chunked flow, acknowledged offsets, bounded
staging, cancellation and restart on the same data directory. Cross-feed or
cross-repository upload session reuse is rejected. Mount requests return a
new upload session instead of reusing another namespace's blob. Reads verify
length and SHA-256 before serving data. Referrers are bounded to 1000 manifests
per repository and return 503 instead of a silently incomplete larger result.

The private Helm smoke uses pinned Helm **3.22.0** for two-feed push/pull/template,
exact chart byte comparison, credentials, deactivation and a process restart.
Private Docker and ORAS flows have not been qualified by this run. No standard
Docker Bearer-token issuer, manifest/blob deletion, multi-node session handoff
or complete OCI/Helm/Kubernetes conformance is claimed.

## Maven

Repository root: `/feeds/{id}/maven/`. Exact read/publish tokens may be supplied
through a matching Maven server ID with HTTP Basic password credentials. Native
`mvn deploy` accepts explicit-GAV POMs, immutable release files, source classifiers
and unique timestamped SNAPSHOTs. The POM version remains the base `-SNAPSHOT`
version; every deployed timestamp/build file remains immutable. Reads verify
recorded size/SHA-256 and authoritative remote bytes for S3.

Mutable GA and SNAPSHOT `maven-metadata.xml` documents are scoped to the same
feed. V11 stores their exact original bytes and SHA-256 in one metadata/audit
transaction. Updates validate existing same-feed targets, reject removed or
rewound versions/classifiers and use compare-and-swap to avoid lost updates.
Matching SHA-512/SHA-256/SHA-1/MD5 PUT sidecars acknowledge committed bytes
without replacing them. Artifact keys remain `feeds/{id}/maven/{canonical-path}`.

The pinned [native deploy harness](../scripts/smoke-maven-deploy.sh) covers global
and two private feeds, release plus two SNAPSHOT generations, fresh-cache Maven
and Gradle transitive resolution, compiled consumers, classifiers and restart.
It bootstraps public build plugins separately, then uses `mirrorOf=*` to replace
every Maven repository with the selected Packory feed. Gradle declares only that
feed. Credentials and original package bytes remain isolated between feeds.

Atomic publication of the entire POM/JAR/classifier set, Maven Central proxy,
plugin-prefix metadata, inherited POM GAV and wider Gradle variant conformance
remain separate E2 work. See [Maven operations, migration and acceptance](maven.md).

## Go modules

Private GOPROXY root: `/feeds/{id}/go/`. Publishing is a Packory-specific
multipart `POST /feeds/{id}/api/go/modules` with `module`, `version`, `mod`,
`zip` and optional RFC3339 `time`. Reads support `@v/list`, `@latest` and
versioned `.info`, `.mod`, `.zip`. A validated triplet is stored at
`feeds/{id}/go/{hashed-module}/{hashed-version}.bundle`; publication and
integrity checks bind its native coordinate to the committed ledger.

Native Go sends GOAUTH/.netrc credentials only over HTTPS. For the verified
Go **1.24.13** client, the `.netrc` machine key must match the HTTPS authority,
including a nondefault port. It is not URL-path scoped: multiple feeds on
one authority require separate client credential contexts, dedicated hosts,
or a separately reviewed path-scoped GOAUTH helper. Do not put tokens in
GOPROXY URLs. Use exactly one private proxy, separate caches and appropriate
private-module settings to avoid direct/public fallback.

The native harness verifies two feeds with different bytes at the same module
version and actual 401/403/404 failures with empty caches. It disables the
external checksum database for the synthetic private namespace. Locally
computed Go `h1:` integrity is not public checksum-database verification.
Upstream mirror, broad Go archive acceptance and the remaining E3 profile
are separate work.

## Raw / Generic

`PUT /feeds/{id}/raw/{name}/{version}` accepts a nonempty fixed-length body.
`GET|HEAD` returns verified bytes with range support; `GET .../metadata`
returns the immutable SHA-256, size and media type. Name/version/media rules
match the global Raw adapter. The key is
`feeds/{id}/raw/{name}/{version}/payload.blob`; duplicate revisions return 409.
This is generic HTTP storage, without a native package-manager protocol.

## Later milestones and verification

The E0 hosted foundation does not implement proxy/group routing, private
Skills distribution, feed-aware MCP discovery, per-feed S3 capacity/retention,
full declarative feed CRUD/import/export, CAS reference counting or browser
permission management. These remain in [MS2/MS4/MS5](roadmap.md) and
[MCP-P5](mcp-roadmap.md); broader native features remain adapter milestones.
[E0 acceptance](e0-acceptance.md) is the command/file/evidence map for this
change. Real-provider restore, release operations and multiple active writers
retain their separate [release gates](release-gates.md).

## Private npm deprecation management (V5-21, V15)

`PUT /feeds/{feedId}/npm/-/deprecations/{version}/{encodedName}` accepts
exactly `{"message":"Use the next release"}` under the exact feed publish
grant. Use an empty message to clear a warning. Messages are bounded to
2048 UTF-16 code units and reject control characters. The active feed and
exact committed npm revision are checked in the same metadata transaction
as the annotation/audit update. Immutable original bundles and tarballs
are never rewritten or removed. Native packument/version reads apply the
overlay, including native `npm view ... deprecated` and install warnings.

Native `npm deprecate <name>@<range> <message>` and empty-message clearing
also work through full-packument GET/PUT on the private npm package route.
The client selects matching versions, including prereleases. GET supplies an
opaque `_rev`; PUT must preserve it and may change only `deprecated` strings.
A stale revision returns 409; other metadata, dist-tags, version deletion and
new versions without attachments are rejected. All changed versions and audit
events commit in one database transaction; a missing target rolls back every
change. Both database providers serialize overlay writers and compare current
overlays before commit. Refetch and retry after a conflict.

This does not add npm owner/login or provenance support. Unpublish/delete remains
disabled so pinned/lockfile consumers can still retrieve published bytes.
The mutable empty-string tombstone survives recovery and suppresses an
original published warning. Audit stores action/coordinate, not message
contents. Schema V15 is forward-only: downgrade requires restoring a
verified V14 backup with its matching data, not changing the version marker.
SQLite offline and PostgreSQL/S3 snapshots include and validate the new
`feed_npm_deprecations` ledger against exact feed-local immutable revisions.

## Private Raw accounting

The private Raw profile includes exact-feed `raw/usage` and optional independent
byte/count quotas, enforced across concurrent single-writer publication.
Immutable assets remain retained indefinitely; capacity pressure never causes
online deletion. See [operations](operations.md#private-raw-feed-capacity-and-retention).

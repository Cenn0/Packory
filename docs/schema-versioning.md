# Database schema versioning and immutable feed ledgers

The current metadata schema is **V13**, recorded by a singleton `packory_schema_version` marker in both SQLite and PostgreSQL. Upgrades are transactional and forward only. The sections below describe the historical increments; V10 defines the shared private-feed storage contract; V11/V12 add Maven metadata and V13 adds mutable npm dist-tag pointers. Existing artifact bytes and keys are preserved through migration.

On upgrade from V1 or an unmarked historical installation, six deterministic `default-*` feeds are seeded (`nuget`, `npm`, `cargo`, `pypi`, `oci`, `skill`) with `mode=legacy` and `visibility=runtime`. These descriptors retain their historical global routes and catalog. Separate, explicitly activated private hosted feeds provide independent artifact identities and exact feed permissions for NuGet, npm, Cargo, PyPI, OCI, Raw, Maven and Go. A legacy descriptor does not itself confer isolation. Private Agent Skills feeds remain outside this delivered profile; their existing approval gates are retained.

Migrations reject unknown future versions before initialization and atomically stamp the current version after all changes and integrity checks. A failed transaction leaves the previous version for diagnosis and retry. Downgrading to an earlier executable requires a verified **offline restoration of the pre-upgrade database and blob snapshot**; there is no automatic DOWN script. Editing the marker cannot reverse a migration.

Feed inventory and lifecycle mutations require admin authorization, including when anonymous global registry reads are enabled. Native private reads and publications require grants for the exact feed. See [the API](api.md), [operations](operations.md), and [V4 roadmap](roadmap-v4.md) for supported routes and protocol limits.

Deterministic migration, isolated restore and emulator tests do not establish external S3 provider certification or production disaster recovery readiness. See [release gates](release-gates.md).


## V3 NuGet checksum ledger and migrations

Version **3** adds `nuget_digests(name,version,sha512,size,origin)` on SQLite
and PostgreSQL. The SHA-512 value is strict canonical Base64, 64 decoded
bytes, and the recorded byte count is nonnegative. New hosted NuGet publications
write their checksum **in the same metadata transaction** as the immutable
package row and publish audit. Newly downloaded, independently upstream-verified
mirrors persist the digest and metadata together without overwriting a hosted
revision or changing an existing checksum.

Previously published/cached V1/V2 NuGet packages have **no trusted historical
publication-time checksum**, remain readable for compatibility, and are
deliberately classified `unverifiable` by the maintenance scanner. Do not
retroactively fill missing references by hashing potentially corrupted bytes.
Exporting a NuGet `.sha512` calculated from a legacy blob is only a legacy
format response, **not** an independent storage-integrity assertion.

Upgrade paths V1→V3 and V2→V3 are forward-only; empty V3 digest tables
are created without fabricating entries for existing rows. Back up and
freeze writers before migration; previous binaries reject V3. The offline
PostgreSQL/S3 snapshot verifier now includes `nuget_digests` in version-3
row fingerprints and rejects incomplete V3 manifests. Older version-2 backup
manifests retain their own strictly bounded table selection.


## V4: private hosted feed revisions

Version **4** adds `feed_nuget_versions` keyed by
`(feed_id,name,version)`, referencing the V2 feed inventory,
with SHA-512, size, description and immutable timestamp. It does **not**
rewrite or migrate legacy package keys and does not replace the V3
`nuget_digests` ledger. Activated NuGet-hosted feeds are
explicitly private; metadata-only drafts for all other modes/kinds
remain disabled. A private feed cannot be deleted if any referenced
revision exists, including after deactivation.

Upgrades V1→V4, V2→V4 and V3→V4 are forward-only. The offline
PostgreSQL/S3 snapshot tool fingerprints `feed_nuget_versions` for
database schema V4, and strict verification refuses a V4 snapshot
without it. Previous schema-version-specific snapshot formats remain
separately recognized. No rollback is supported without a pre-upgrade
coordinated offline database+object restore.


## V7: Hosted Go modules

SQLite and PostgreSQL now include `go_modules`, recording immutable module/version identity, bundled triplet SHA-256, ZIP and go.mod digests, byte counts and release timestamp. One blob holds all three GOPROXY responses, made visible by a single metadata+catalog+audit transaction. Publication intents preserve incomplete-write evidence. PostgreSQL/S3 offline snapshots fingerprint `go_modules` at V7. Forward schema migration is tested; a rollback and real-provider restore are separate requirements.

## V8: isolated shared hosted feed assets

V8 expands the permitted `feeds.kind` values to include staged `raw`,
`maven` and `go` descriptors. It creates the immutable `feed_assets`
metadata ledger with composite key `(feed_id,kind,path)`, canonical
coordinate/media validation, published SHA-256/size, and a restricted
foreign key to the existing administrative feed record. **Only private
hosted Raw was activated in the original V8 increment; Maven and Go remained drafts at that point.**
Existing default/global registries retain their original routes and bytes.

SQLite upgrades reconstruct the constrained `feeds` table using the
documented create/copy/drop/rename procedure with foreign-key enforcement
temporarily suspended **before** the migration transaction, verify every
foreign key before commit, and restore enforcement. The PostgreSQL upgrade
transaction expands the `feeds_kind_check` constraint and creates the
same V8 ledger. Both implementations atomically publish an asset metadata
revision together with a privacy-safe audit entry, and block deletion of
feeds referenced by immutable revisions. Crashes between blob persistence
and metadata commit are tracked through durable intents and read-only
recovery/integrity inspection.

A V8 PostgreSQL/S3 snapshot requires `feed_assets` row fingerprints.
Rollback is only a coordinated verified **pre-upgrade** database+blob
restore; no in-place database downgrade is supported. Real-provider S3,
fault-injection and production recovery qualification remain open.

## V9: private Go feed asset kind and rollback boundary

V9 is a **forward-only, transactional metadata migration**, not an
automatic rollback. It extends the `feed_assets.kind` CHECK constraint
from `raw,maven` to `raw,maven,go`. The existing parent feed table,
individual raw/Maven rows, immutable blobs and audit/publication records
are preserved.

- SQLite rebuilds only the `feed_assets` child table inside a single
  migration transaction. Foreign-key checking is suspended before
  transaction start and `PRAGMA foreign_key_check` must pass before commit.
- PostgreSQL replaces the `feed_assets_kind_check` constraint within
  the version-gated transaction; the existing rows and PK/index remain.
- The `packory_schema_version` marker advances from V8 to V9. An older
  V8 executable will reject this state rather than silently write to it.
  Operators must take a verified coordinated backup before upgrade;
  reverting requires restoring a verified V8-era backup, **not**
  editing the version marker.
- The PostgreSQL/S3 offline snapshot verifier retains compatibility with
  prior V8 manifests and now reads the actual database version from the
  version marker. It refuses unsupported versions instead of inferring
  schema solely from presence of `feed_assets`.

No high-availability rolling downgrade compatibility or real-provider
disaster-recovery guarantee is inferred from this migration.

## V10: shared private package assets and OCI tags

V10 extends `feed_assets.kind` to `raw,maven,go,npm,cargo,pypi,oci` while preserving
its composite primary key `(feed_id,kind,path)`, immutable SHA-256/byte count,
creation timestamp, identity index and foreign key to `feeds`. NuGet retains its
separate `feed_nuget_versions` SHA-512 ledger. Existing rows and blob bytes are
copied unchanged. No default/global content is silently imported into a private
feed and no digest is fabricated from legacy bytes.

SQLite rebuilds only the child `feed_assets` table inside the version-gated
transaction. `PRAGMA foreign_key_check` must succeed before commit; enforcement
is restored even if migration fails. PostgreSQL replaces the kind and size
constraints within its migration transaction. A zero-byte object is permitted
only for OCI; native validation further restricts that case to blobs and still
requires nonempty manifests. Other artifact kinds retain `size > 0`.

The new `feed_oci_tags(feed_id,name,tag,digest,updated_at)` table has primary key
`(feed_id,name,tag)` and a restricted foreign key to `feeds`. Tags can move to a
new immutable manifest digest. The database publication method checks the active
OCI feed and a committed manifest in that exact feed and repository in the same
transaction as the tag update and audit. A digest belonging only to a blob,
another repository or another feed cannot become a tag target. Old manifest
revisions remain readable by digest after a tag moves.

### Storage identities

The stored key is derived from validated coordinates, never accepted from an
HTTP request or snapshot reference as an arbitrary path:

| Kind | Key under the blob root / configured S3 prefix |
| --- | --- |
| Raw | `feeds/{id}/raw/{name}/{version}/payload.blob` |
| Maven | `feeds/{id}/maven/{canonical-artifact-path}` |
| Go | `feeds/{id}/go/{sha256(module)}/{sha256(version)}.bundle` |
| npm, Cargo, PyPI | `feeds/{id}/{kind}/{sha256(canonical-path)}.bundle` |
| OCI blob | `feeds/{id}/oci/blob/{repository}/{sha256-hex}.blob` |
| OCI manifest | `feeds/{id}/oci/manifest/{repository}/{sha256-hex}.json` |

String hashes use UTF-8. npm canonical paths are `name/version`, Cargo paths
omit SemVer build metadata from the uniqueness key, and PyPI paths are
`normalized-name/filename`. Go's existing `.info/.mod/.zip` bundle and legacy
Raw/Maven keys remain unchanged.

An npm/Cargo/PyPI bundle is a ZIP with exactly two uncompressed entries:
`metadata.json` and `payload`. The original package bytes form `payload`.
The JSON wrapper binds `schemaVersion=1`, feed, kind, path, name and version
to payload SHA-256/size and the adapter metadata. The outer ledger records
the SHA-256/size of the complete bundle. Reads verify both layers before
exposing content. Wrapper metadata is bounded to 4 MiB; payloads remain bounded
by the configured artifact limit and ZIP overhead to 64 KiB. Swapping a bundle
between two feeds fails its inner identity check even if an outer reference
were also replaced.

### Coordinated PostgreSQL/S3 snapshots

Database schema V10 requires snapshot format 3, all existing authoritative table
fingerprints, the `feed_oci_tags` fingerprint, and `feed_references` containing
the immutable asset and OCI tag identities. This inventory contains package
metadata; keep the entire archive under the same access controls as the source
registry. It does not add credentials or actor identities to the manifest.

Before a restore writes any target object, the verifier checks:

- Reference counts and uniqueness against the database inventory, exact derived
  keys, media/coordinate shape and the current live object's committed hash and
  size. A historical version or a latest delete marker cannot satisfy a live
  artifact reference.
- Exact bounded bundle members, embedded feed/package identity, original payload
  digest/length and adapter checksum metadata for npm, Cargo and PyPI; Go info and
  module identity are checked within its existing triplet bundle.
- Every OCI config/layer/index descriptor against the committed manifest/blob
  graph in the exact feed and repository. OCI subjects may precede publication,
  as permitted by that protocol. Alternate descriptor `urls` are unsupported
  in the private hosted profile; inline `data` must be canonical Base64 with
  the exact descriptor SHA-256 and size. Inline content does not replace a
  committed config/layer/index reference. Every tag must resolve to its own
  committed manifest.

After `pg_restore`, complete row fingerprints and the recaptured feed references
must equal the verified snapshot. Historical S3 versions remain archived as
evidence, while the isolated restore reconstructs current live content. Provider
version IDs, retention settings and resumable upload sessions are not recreated
by this database/object snapshot tool. Older V1–V9 archives retain their own
version-specific validation contract; they do not acquire V10 reference evidence
by changing a manifest marker.

Migration regressions exercise preservation of pre-upgrade bytes, primary and
foreign keys, rollback after a failed foreign-key check, zero-byte OCI handling,
and rejection of tags targeting uncommitted/foreign content. Snapshot tests cover
cross-feed bundle replacement, live-versus-deleted versions, graph/reference
failures before restore writes, and a restored database disagreeing with its
artifact inventory. The optional PostgreSQL/S3 emulator drill adds actual
`pg_dump`/`pg_restore` and versioned object API coverage; external-provider
qualification remains a separate release gate.


## V13: private npm dist-tag pointers

V13 introduces `feed_npm_tags(feed_id,name,tag,version,deleted,updated_at)`.
Its composite key isolates the mutable tag namespace to one private hosted
npm feed and package. `version` targets an already committed immutable npm
revision of that same feed. Deletion writes a tombstone (`deleted=1`,
`version=''`) to suppress the historical publication-time tag without deleting
the original package bytes. Reassignments and removals are audited in the
same SQLite/PostgreSQL transaction as the tag pointer.

Native `/-/package/{name}/dist-tags` GET/PUT/DELETE endpoints require exact
feed grants; mutations use publisher permission. The packument applies the
mutable pointer overlay after validating the underlying immutable bundles.
No private tag leaks into another feed or the global npm registry. The
underlying tarballs cannot be overwritten by tag changes.

Forward upgrade requires a stopped-writer, verified full backup.
PostgreSQL/S3 snapshot format 4 additionally fingerprints the table, checks
tag targets against same-feed committed npm assets and verifies the isolated
restore. SQLite offline backup validates tag targets before accepting the
snapshot. Older executables reject the new marker; rollback requires an
isolated restore of the pre-upgrade metadata and blobs, not a marker edit.

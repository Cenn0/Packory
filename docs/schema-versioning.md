# Database schema versioning — immutable checksum ledger

Packory uses a singleton `packory_schema_version` marker in both SQLite and PostgreSQL. **Version 1** is the historical global registry. **Version 2** adds feed inventory tables; **version 3** adds an immutable `nuget_digests` (name, version, canonical SHA-512 Base64, byte size and immutable origin) ledger, all inside a transactional, forward-only upgrade: `feeds`, `feed_namespaces`, `feed_policies`, `feed_upstreams`. All existing artifact/package tables and physical blob keys are unchanged.

On upgrade from v1 or an unmarked historical installation, six deterministic `default-*` feeds are seeded (`nuget`, `npm`, `cargo`, `pypi`, `oci`, `skill`) with `mode=legacy` and `visibility=runtime`. **These are administrative descriptors only.** The native routes, client permissions, upstream selection, package storage and collision behavior are still global; **none of these feeds yet isolate artifacts**. A `mode=legacy` feed does not claim support for independent hosted/proxy routing.

Migrations reject unknown future versions before initialization and atomically stamp v3 after creating tables and defaults. A failed transaction leaves the previous version for explicit diagnosis and retry. Downgrading to an earlier Packory executable is **not supported**: old binaries reject future schemas. The rollback path is a verified **offline restoration of the pre-upgrade database and blob snapshot**, not an automatic DOWN script.

Security: the read-only `GET /api/v1/admin/feeds` and `GET /api/v1/admin/feeds/{id}` inventory require full admin authorization, even if anonymous registry reads are enabled. Cursor pagination is bounded and inputs validated. Feed mutation, namespace enforcement, provisioning, per-feed quota/audit and multiple namesake packages are *not* implemented by this PR.

This is an increment toward MS0.4 / M2.1 and M2.6, **not** completion of MS2 or a supported production migration. Full rollback and PostgreSQL/S3 real-provider restore drills must precede upgrades of production data. See [release gates](release-gates.md) and [roadmap](roadmap.md).


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
hosted Raw is activated in this delivery; Maven and Go remain drafts.**
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

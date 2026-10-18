# Database schema versioning — immutable checksum ledger

Packory uses a singleton `packory_schema_version` marker in both SQLite and PostgreSQL. **Version 1** is the historical global registry. **Version 2** adds feed inventory tables; **version 3** adds an immutable `nuget_digests` (name, version, canonical SHA-512 Base64, byte size and immutable origin) ledger, all inside a transactional, forward-only upgrade: `feeds`, `feed_namespaces`, `feed_policies`, `feed_upstreams`. All existing artifact/package tables and physical blob keys are unchanged.

On upgrade from v1 or an unmarked historical installation, six deterministic `default-*` feeds are seeded (`nuget`, `npm`, `cargo`, `pypi`, `oci`, `skill`) with `mode=legacy` and `visibility=runtime`. **These are administrative descriptors only.** The native routes, client permissions, upstream selection, package storage and collision behavior are still global; **none of these feeds yet isolate artifacts**. A `mode=legacy` feed does not claim support for independent hosted/proxy routing.

Migrations reject unknown future versions before initialization and atomically stamp v3 after creating tables and defaults. A failed transaction leaves the previous version for explicit diagnosis and retry. Downgrading to an earlier Packory executable is **not supported**: old binaries reject future schemas. The rollback path is a verified **offline restoration of the pre-upgrade database and blob snapshot**, not an automatic DOWN script.

Security: the read-only `GET /api/v1/admin/feeds` and `GET /api/v1/admin/feeds/{id}` inventory require full admin authorization, even if anonymous registry reads are enabled. Cursor pagination is bounded and inputs validated. Feed mutation, namespace enforcement, provisioning, per-feed quota/audit and multiple namesake packages are *not* implemented by this PR.

This is an increment toward MS0.4 / M2.1 and M2.6, **not** completion of MS2 or a supported production migration. Full rollback and PostgreSQL/S3 real-provider restore drills must precede upgrades of production data. See [release gates](release-gates.md) and [roadmap](roadmap.md).

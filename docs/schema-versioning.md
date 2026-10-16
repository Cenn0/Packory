# Database schema versioning — V3 baseline

Packory now stamps SQLite and PostgreSQL metadata with a singleton `packory_schema_version` row at **version 1**. It marks the historical pre-feed schema. Older installations without a marker receive the marker during ordinary idempotent initialization; hosted data is preserved. Unknown/newer versions are rejected **before** existing schema initialization.

This is a safety foundation, **not** a schema migration framework, not an online PostgreSQL/S3 migration, and not a reversible downgrade. No automatic version-2 feed migration or old-key cleanup is available.

Before any future version bump, require versioned SQLite/PostgreSQL upgrade scripts, rollback decision, metadata and object-store backup, interrupted-upgrade tests, native-client compatibility and an isolated real-provider restore rehearsal. Do not reinterpret the marker as evidence for MS0.4, MS1.5 or MS2 completion. See [Roadmap V3](roadmap.md) and [release gates](release-gates.md).

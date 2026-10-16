# Migrating Packory MVP data to V2

## Principle

**Existing data is preserved.** V2 initializes additional SQLite tables in the same `packory.sqlite` and writes new CAS content to a separate filesystem tree. It does **not** rewrite or delete existing `packages`, `npm_versions`, `npm_tags`, `skills` tables or files used by `/nuget`, `/npm` and `/api/skills`.

New repositories do not automatically expose or inherit old packages. This is intentional: treating cached upstream content as a locally owned release or moving global packages across feed boundaries could leak private contents or silently change provenance.

## Upgrade checklist

1. Stop Packory before backing up the SQLite database and `/data` tree. Back up both metadata and blobs; a DB-only copy is insufficient.
2. Record the existing `PACKORY_PUBLIC_BASE_URL`, authentication settings and client source URLs. Keep bootstrap secrets outside the repository.
3. Upgrade the container/binaries with the **same persistent `/data` volume**. The new schema is created additively during startup.
4. Check `/health/ready` and verify old NuGet/npm/skills URLs before changing any clients.
5. Create new per-protocol V2 repositories using `/api/v1/repositories`. For clients switching to V2, use explicit new feed-scoped tokens; do not assume anonymous reads.
6. Re-publish owned artifacts to new Hosted feeds under explicitly chosen names and versions; previously mirrored external content should remain in a proxy feed and be verified on first download.
7. Verify source configuration against actual install/restore tooling, and exercise cold/warm/offline scenarios before switching production clients.
8. Perform a restore drill: stop the application, restore the entire database and CAS/legacy blob folders from the same backup, and start a single writer instance.

## Rollback

Before switching clients, keep the previous image and a consistent full data snapshot. The previous application ignores new tables but does not understand the new V2 assets. It can be used with its own old endpoints if the full snapshot is restored; an in-place downgrade while V2 is still writing is **not supported**.

## Schema contract

`schema_migrations` records V2's additive initialization. The CAS folder is `cas/sha256/{first-two}/{next-two}/{digest}`; files are staged in `staging/`. Published V2 assets refer to CAS by digest through `v2_assets`. A background reconciliation job can prune old unreferenced CAS files after a grace period. Never manually delete CAS files while Packory is running.

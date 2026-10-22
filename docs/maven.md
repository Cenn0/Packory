# Maven and Gradle — experimental hosted repository

**E2 delivery profile, 22 October 2026:** native Maven release and unique
timestamped SNAPSHOT deployment, repository metadata publication and ordinary
Maven/Gradle dependency consumption. The global repository and independently
authorized private feeds use the same protocol implementation. E2 remains
partial: proxying, atomic publication of an entire multi-file release, plugin
prefix metadata and wider client/variant qualification are separate work.

## Repository layout and immutable files

| Operation | Global repository path (prefix private paths with `/feeds/{id}`) |
| --- | --- |
| Release POM | `/maven/com/example/library/1.0.0/library-1.0.0.pom` |
| Release JAR | `/maven/com/example/library/1.0.0/library-1.0.0.jar` |
| Sources classifier | `/maven/com/example/library/1.0.0/library-1.0.0-sources.jar` |
| Timestamped SNAPSHOT | `/maven/com/example/library/1.1.0-SNAPSHOT/library-1.1.0-20261022.100000-1.jar` |
| Version inventory | `/maven/com/example/library/maven-metadata.xml` |
| SNAPSHOT pointers | `/maven/com/example/library/1.1.0-SNAPSHOT/maven-metadata.xml` |

Files and both metadata levels support authenticated `GET`, `HEAD` and `PUT`.
Append `.sha512`, `.sha256`, `.sha1` or `.md5` for checksum sidecars.
Extensions are bounded to `pom`, `jar`, `war`, `aar`, `zip`, `tar.gz` and
`module`; accepting a `.module` file does not qualify every Gradle metadata variant.

The directory keeps the base version `1.1.0-SNAPSHOT`; the timestamp/build number
identifies immutable files within it. New deployments create new files and
advance metadata pointers. Previously published files remain addressable and
cannot be overwritten, including by identical repeat `PUT`s. Nonunique mutable
`library-1.1.0-SNAPSHOT.jar` paths are rejected. Invalid calendar timestamps,
noncanonical build numbers, traversal, encoded separators and oversized paths
are rejected before storage access.

POMs must contain explicit matching `groupId`, `artifactId` and `version` fields.
For a timestamped SNAPSHOT filename the POM version is the base `-SNAPSHOT`
version. Parent-inherited GAV, Maven plugin/group metadata and arbitrary POM
interpolation are outside this profile. XML parsing is bounded; DTDs and external
entities are prohibited.

## Native publishing and consuming

Use the private repository URL
`https://packory.example.invalid/feeds/team-maven/maven/` for an active private
Maven feed. A Maven `<server>` ID must match the repository ID used by
`distributionManagement` or `altDeploymentRepository`. Supply a revocable
`feed:team-maven:publish` grant as the HTTP Basic password; use an exact read
grant for consuming. Store credentials in runtime-generated settings outside
the repository. HTTPS is required outside isolated loopback tests.

```bash
mvn --settings "$PACKORY_MAVEN_SETTINGS" clean deploy \
  -DaltDeploymentRepository=packory::https://packory.example.invalid/feeds/team-maven/maven/
```

Global publishing uses the existing write/admin policy and optional namespace
grant `maven:com.example/library`. Global reads follow the global read policy.
Private requests require the exact feed grant, enabled Maven kind and hosted
mode before any metadata or artifact lookup. There is no anonymous, global,
cross-feed or public-upstream fallback.

For Maven dependency resolution, configure an explicit repository or an
appropriate mirror. A private-only build must not silently retain public
fallback repositories. The native test bootstraps its pinned build plugins
separately, then applies `mirrorOf=*` to Packory for all synthetic project
artifacts with fresh caches. Gradle declares exactly one authenticated Packory
repository and verifies compilation/execution of a transitive dependency graph.

## Mutable metadata contract

Schema V11 stores the publisher's original metadata bytes, canonical base64,
SHA-256, length and update time atomically in SQLite or PostgreSQL. Metadata is
keyed by exact `(scope, path)`; the empty scope is global and a nonempty scope
is a private feed ID. Checksum GETs and acknowledgements use those exact bytes,
including their original whitespace and XML declaration.

Publication checks both the document identity and all referenced same-scope
committed POM/SNAPSHOT files. SNAPSHOT metadata must identify an existing current
POM and unique extension/classifier entries; current and retained older classifier
files must exist. Referenced local and authoritative S3 bytes are checked against
their immutable ledger before accepting new metadata. Missing references fail
with `409`; damaged committed content fails with `503`.

Metadata updates cannot remove previously advertised versions or classifiers,
rewind timestamps/builds or replace a database revision that changed concurrently.
Native clients receive `409` for conflicting updates and must fetch/retry using
the current state. Clients that send `If-Match` can bind an update to an exact
ETag; a stale condition returns `412`. `If-None-Match: *` requires a new document.
Metadata and its audit event commit together. A failed audit/database transaction
does not leave a partially updated document. Directly uploaded legacy releases
still receive a generated version listing until a publisher supplies metadata;
the first stored document must preserve that existing version inventory.
Artifact IDs ending in `-SNAPSHOT` remain valid. If a metadata URL would represent
both an artifact inventory and another artifact's SNAPSHOT pointers, conflicting
metadata requests and new artifact publications return `409`.

All PUTs require positive `Content-Length` and configured size bounds.
Metadata is capped by `PACKORY_MAX_METADATA_BYTES` and a hard 16 MiB limit;
each document/reference expansion is limited to 4096 entries. Checksum bodies
allow only matching ASCII hex plus an optional trailing LF/CRLF, at most 130
bytes. Correct sidecars return `204`; wrong digests return `409`, missing targets
`404`, malformed input `400` and excessive bodies `413`. Sidecars do not create
independent mutable objects. SHA-1/MD5 are compatibility responses; publication
integrity remains SHA-256, without implying signatures or publisher provenance.

## Persistence, recovery and migration

Release and timestamped SNAPSHOT files keep their existing physical global or
feed-local keys. Each immutable file follows intent → object → ledger/audit →
completed intent. Metadata bytes live entirely in the database, avoiding an
additional database/S3 consistency boundary. Each file is visible after its own
commit; **the complete POM/JAR/classifier set is not one atomic deployment**.

V10 → V11 is an additive, transactional metadata-table migration. It preserves
all prior artifact rows, bytes, feed grants and OCI tag references. Startup and
readiness checks reject malformed metadata and missing same-repository
references. Before upgrading, stop writers and take a verified consistent
backup. Rollback requires the matching pre-upgrade backup and old application;
never lower the schema marker on a V11 database.

Offline SQLite backup/restore validates exact metadata and referenced assets.
The PostgreSQL/S3 snapshot format is V4 for schema V11 and adds a
`maven_references` inventory inside the manifest; restore compares it to the
restored database and object inventory. Previously created snapshots for V4/V10
database profiles remain accepted in their supported older formats. Tests use
PostgreSQL and the Moto S3 emulator; they do not establish
qualification of a real S3 provider.

## Reproducible acceptance

`bash scripts/smoke-maven-deploy.sh` runs pinned Maven **3.9.11**, deploy plugin
**3.1.4**, Gradle **9.8.2** and Java **17**. Tool distributions are checked against
pinned upstream checksums. The test exercises:

- Native `mvn clean deploy` for a release and two successive timestamped
  SNAPSHOTs in the global repository and two isolated private feeds.
- POM/JAR/source classifiers, exact original bytes and SHA-1/SHA-256/SHA-512.
- Fresh-cache Maven transitive resolution and Gradle compilation/execution,
  including classifier retrieval and distinct bytes under identical coordinates.
- Authorization failures, isolation, retained prior SNAPSHOT bytes and real
  application restart with unchanged metadata/checksums.

`MavenDeploymentTests`, `MavenMetadataProviderTests`, existing Maven/feed tests
and the parser suite cover malformed XML, invalid paths, namespace permissions,
missing/corrupt references, CAS conflicts, migration and transaction failures.
`test_maven_snapshot.py`, `test_pg_s3_restore_e2e.py` and
`scripts/smoke-feed-recovery.py` cover corrupt inventories and actual isolated
restore. Published CI remains the evidence for the exact merged commit.

This is a tested bounded Packory profile, not Apache/Gradle certification or
complete protocol conformance. Remaining E2 work includes Central proxying,
multi-file atomic visibility, advanced Gradle variants and broader deployment
operations. See the [support matrix](support-matrix.md),
[V4 roadmap](roadmap-v4.md) and [issue #91](https://github.com/Cenn0/Packory/issues/91).

## Primary specifications

- [Apache Maven repository layout](https://maven.apache.org/repository/layout.html)
- [Maven 3.9.11 repository metadata model](https://maven.apache.org/ref/3.9.11/maven-repository-metadata/repository-metadata.html)
- [Maven deploy plugin usage](https://maven.apache.org/plugins/maven-deploy-plugin/usage.html)
- [Gradle repository types](https://docs.gradle.org/9.8.2/userguide/supported_repository_types.html)


## Maven plugin-prefix metadata (schema V12)

A native Maven plugin can publish group-level metadata at
`/maven/<group/path>/maven-metadata.xml` or
`/feeds/<id>/maven/<group/path>/maven-metadata.xml`.
The XML maps each unique `<prefix>` to a committed plugin `<artifactId>`
within the same group and scope. Reference integrity requires a corresponding
immutable release POM; changes may add mappings but cannot silently remove or
reassign an existing prefix. These metadata bytes and their digest sidecars are
stored atomically in the scoped Maven metadata ledger. Path collisions with
ordinary artifact metadata fail closed. An authenticated writer with a
restricted namespace must be allowed to publish each referenced plugin.
POM uploads can inherit `groupId` and `version` from their declared
`<parent>` element; effective coordinate identity must match the URL.

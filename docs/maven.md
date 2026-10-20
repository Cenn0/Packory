# Maven release artifacts — V4 experimental hosted transport

**Scope:** bounded **global** Maven 2 repository layout for immutable release files and independently validated artifact checksum PUTs. Not full Maven/Gradle registry parity, not a native Maven deployment repository and not production-qualified.

The adapter exposes an HTTP Maven repository URL for **native Maven artifact consumption**. The CI smoke retries **only recognizable transient Maven Central plugin bootstrap transfer faults** (up to three attempts); artifact download, digest verification and consumer build failures are never treated as a successful retry. See [PR #95](https://github.com/Cenn0/Packory/pull/95). It does **not** claim native Maven deploy lifecycle, SNAPSHOT publication, mutable metadata PUT, Gradle module metadata conformance, upstream proxy, private per-feed isolation or multi-node writers.

## URLs and coordinates

- Repository: https://packory.example.invalid/maven/
- POM: /maven/com/example/library/1.0.0/library-1.0.0.pom
- JAR: /maven/com/example/library/1.0.0/library-1.0.0.jar
- Sources: /maven/com/example/library/1.0.0/library-1.0.0-sources.jar
- Checksums: add .sha256, .sha1 or .md5 to the individual artifact URL; GET is derived from immutable artifact bytes, and matching artifact checksum PUTs receive 204 without changing stored content
- Versions: /maven/com/example/library/maven-metadata.xml (and checksum variants)
- PUT is a **Packory-specific file upload**, not standard Maven deploy. Requires write/admin credential and optional Maven namespace grant of the form maven:com.example/library.

The layout validator rejects traversal, encoded separators, noncanonical group segments, oversized paths, empty segments and SNAPSHOT revisions. Extensions are limited to pom, jar, war, aar, zip, tar.gz and module.

## Publishing

Upload each file through an authenticated HTTP PUT to its canonical repository path. The request must include a positive Content-Length; unsupported unknown-length uploads receive HTTP 411. An existing path cannot be overwritten. POM uploads must be bounded XML containing explicit matching groupId, artifactId and version fields; DTD/external entity resolution is prohibited. Parent-inherited project GAV and advanced deployment metadata are not supported in this slice.

~~~bash
curl --fail-with-body --request PUT \
  -H "Authorization: Bearer $PACKORY_WRITE_TOKEN" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @library-1.0.0.pom \
  https://packory.example.invalid/maven/com/example/library/1.0.0/library-1.0.0.pom
~~~

A checksum PUT is allowed only **after** its release artifact has been committed. The supplied body must contain exactly the matching ASCII hexadecimal digest (optionally one LF/CRLF), with a bounded Content-Length. Correct checksums return 204 (including idempotent retries); a mismatch returns 409, an absent artifact returns 404, malformed input returns 400/413, and corrupted stored bytes return 503. These checks compare to the verified immutable SHA-256-backed artifact ledger before acknowledging the upload. They are not new authoritative metadata and do not mutate package revisions. The same publication-namespace permission as the underlying artifact is required. **PUT to `maven-metadata.xml` or any of its checksum URLs remains unsupported**; acknowledging immutable checksum sidecars does not make `mvn deploy` compatible.

Never commit credentials or generated settings files with live tokens. Use TLS outside isolated loopback tests.

## Native consumption

For com.example:library:1.0.0, add Packory as an explicitly configured Maven repository. The native smoke test uses a synthetic release, fresh client cache, and an isolated Maven mirror to verify that the bytes actually came from Packory. Maven version metadata is derived from published POMs; it does not implement all Maven release/latest/snapshot discovery semantics. Legacy SHA-1/MD5 checksum responses exist for client compatibility only. **Publication-time authoritative integrity is SHA-256.**

## Persistence and security

- SQLite/PostgreSQL schema V6 stores immutable Maven asset paths, checksums, size, MIME and creation time; per-file catalog plus audit are committed in a DB transaction.
- Bytes are separately committed to filesystem/S3. Durable intents, startup reconciliation, offline integrity inspection and schema-aware PostgreSQL/S3 fingerprints cover Maven files.
- No cross-store ACID transaction. A release has separate POM and binary writes; partial releases must not be treated as atomic or production-qualified.
- Global read policy applies. Feed-scoped managed credentials are not allowed to read global Maven assets when authentication is required.
- No arbitrary upstream URLs, implicit public fallback, remote code execution, automatic malware scanning or signature verification.

**Open:** native Maven deploy (including server-controlled mutable repository metadata lifecycle), native Gradle conformance, atomic multi-file releases, snapshot versions, private feed isolation, upstream proxy, real S3 recovery/provider tests and full client/security acceptance. Track these in [V4 roadmap](roadmap-v4.md), [issue #91](https://github.com/Cenn0/Packory/issues/91) and [release gates](release-gates.md).

Official references: [Maven layout](https://maven.apache.org/repository/layout.html), [metadata](https://maven.apache.org/repositories/metadata.html), [deploy plugin](https://maven.apache.org/plugins/maven-deploy-plugin/usage.html).

## V8 experimental isolated private hosted Maven sources (E0)

The global `/maven/` transport above remains unchanged. Administrators
may now activate `kind=maven` **private hosted** feed drafts. A private
Maven source uses `/feeds/<feed-id>/maven/` and **only** exact
`feed:<id>:read` or `feed:<id>:publish` grants; anonymous and global
read/write credentials cannot access it. Files are immutable under
`feeds/<id>/maven/<canonical-repository-path>` and are indexed by
`(feed_id,kind,path)`. A POM and JAR in one feed do not appear in the
global Maven registry or any other feed. Version metadata is derived
only from that feed's committed assets; all digest/metadata GETs are
validated before revealing content. Publication is still via individual
Packory file PUTs (plus validated checksum-sidecar PUTs), not native
Maven deploy.

The CI includes two synthetic Maven feeds with identical coordinates but
different bytes, exact credential enforcement, native Maven 3 dependency
resolve, byte parity, checksums, negative cross-feed reads, invalid POMs,
tampering and SQLite/PostgreSQL/provider-boundary checks. Real-world Gradle
or Maven deploy compatibility, SNAPSHOT, Maven Central proxying, real
storage provider recovery and broad client matrices remain separate
release gates.

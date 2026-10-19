# Maven release artifacts — V4 experimental hosted transport

**Scope:** bounded **global** Maven 2 repository layout for immutable release files. Not full Maven/Gradle registry parity, not a native Maven deployment repository and not production-qualified.

The adapter exposes an HTTP Maven repository URL for **native Maven artifact consumption**. It does **not** claim native Maven deploy lifecycle, SNAPSHOT publication, mutable metadata PUT, Gradle module metadata conformance, upstream proxy, private per-feed isolation or multi-node writers.

## URLs and coordinates

- Repository: https://packory.example.invalid/maven/
- POM: /maven/com/example/library/1.0.0/library-1.0.0.pom
- JAR: /maven/com/example/library/1.0.0/library-1.0.0.jar
- Sources: /maven/com/example/library/1.0.0/library-1.0.0-sources.jar
- Checksums: add .sha256, .sha1 or .md5 to the individual artifact URL
- Versions: /maven/com/example/library/maven-metadata.xml (and checksum variants)
- PUT is a **Packory-specific file upload**, not standard Maven deploy. Requires write/admin credential and optional Maven namespace grant of the form maven:com.example/library.

The layout validator rejects traversal, encoded separators, noncanonical group segments, oversized paths, empty segments and SNAPSHOT revisions. Extensions are limited to pom, jar, war, aar, zip, tar.gz and module.

## Publishing

Upload each file through an authenticated HTTP PUT to its canonical repository path. An existing path cannot be overwritten. POM uploads must be bounded XML containing explicit matching groupId, artifactId and version fields; DTD/external entity resolution is prohibited. Parent-inherited project GAV and advanced deployment metadata are not supported in this slice.

~~~bash
curl --fail-with-body --request PUT \
  -H "Authorization: Bearer $PACKORY_WRITE_TOKEN" \
  -H "Content-Type: application/octet-stream" \
  --data-binary @library-1.0.0.pom \
  https://packory.example.invalid/maven/com/example/library/1.0.0/library-1.0.0.pom
~~~

Never commit credentials or generated settings files with live tokens. Use TLS outside isolated loopback tests.

## Native consumption

For com.example:library:1.0.0, add Packory as an explicitly configured Maven repository. The native smoke test uses a synthetic release, fresh client cache, and an isolated Maven mirror to verify that the bytes actually came from Packory. Maven version metadata is derived from published POMs; it does not implement all Maven release/latest/snapshot discovery semantics. Legacy SHA-1/MD5 checksum responses exist for client compatibility only. **Publication-time authoritative integrity is SHA-256.**

## Persistence and security

- SQLite/PostgreSQL schema V6 stores immutable Maven asset paths, checksums, size, MIME and creation time; per-file catalog plus audit are committed in a DB transaction.
- Bytes are separately committed to filesystem/S3. Durable intents, startup reconciliation, offline integrity inspection and schema-aware PostgreSQL/S3 fingerprints cover Maven files.
- No cross-store ACID transaction. A release has separate POM and binary writes; partial releases must not be treated as atomic or production-qualified.
- Global read policy applies. Feed-scoped managed credentials are not allowed to read global Maven assets when authentication is required.
- No arbitrary upstream URLs, implicit public fallback, remote code execution, automatic malware scanning or signature verification.

**Open:** native Maven deploy, native Gradle conformance, atomic multi-file releases, snapshot versions, mutable metadata, private feed isolation, upstream proxy, real S3 recovery/provider tests and full client/security acceptance. Track these in [V4 roadmap](roadmap-v4.md), [issue #91](https://github.com/Cenn0/Packory/issues/91) and [release gates](release-gates.md).

Official references: [Maven layout](https://maven.apache.org/repository/layout.html), [metadata](https://maven.apache.org/repositories/metadata.html), [deploy plugin](https://maven.apache.org/plugins/maven-deploy-plugin/usage.html).

# Offline snapshot validation and restore preflight

The PostgreSQL/S3 operator tool retains snapshot formats 1 and 2. Its verification
is deliberately stricter than merely hashing whatever bytes happen to be present.
This is an implementation contract, **not** real-provider or production acceptance.

## Source consistency window

All Packory writes must be stopped. The tool holds the existing PostgreSQL writer
advisory lock and compares canonical metadata fingerprints before and after the
snapshot. It also compares the complete paginated S3 version inventory before
and after capture: key, version ID, object/delete-marker type, current-version
flag, and returned size/ETag attributes. A changed inventory or disabled bucket
versioning aborts the snapshot; only its private temporary directory is removed.
Source objects are never deleted by this failure handling.

This detects observed changes during the offline window. It does **not** fence
uncooperative writers or make PostgreSQL and S3 one atomic transaction. Lifecycle
operations must also be suspended or otherwise excluded during the window.
A real-provider drill is still required. ETags are compared as provider state,
not interpreted as cryptographic content checksums.

## Manifest and payload gates

Before any restore write, every object and delete marker must have a valid,
prefix-bound key, non-empty version ID and an actual JSON boolean `is_latest`.
String values such as `"false"` are not coerced. A key must have exactly one
current object version **or** one current delete marker. Duplicate identities,
conflicting current versions, missing current markers and malformed delete
records are rejected. Historical versions and quarantine bytes remain archived;
only currently live object versions are copied into the isolated destination.

Object sizes, PostgreSQL dump sizes and row counts must be non-negative JSON
integers, not booleans or floats. Digest strings and hashed local payload paths
are validated. Format-2 metadata must contain every required table fingerprint,
including the feed tables for database schema 2. Optional total-size accounting
must agree with the inventory. Format 1 remains explicitly weaker for database
row-value evidence, but does not bypass object-selection validation.

Duplicate JSON fields, non-finite numbers, oversized manifests (64 MiB maximum),
missing/untracked files, symlinks, hardlinks and non-regular files are rejected.
The tool checks the file set before opening payloads; a FIFO cannot make checksum
verification block indefinitely. The operator must keep the snapshot directory
private and unchanged during verification/restore. Hashes prove consistency
against the supplied manifest, **not** authenticity of an untrusted PostgreSQL
dump; restore only trusted backups with least-privilege roles in isolation.

## Destination preconditions and failures

The destination database and bucket differ from the recorded source. Versioning
must be enabled and the target prefix must contain no versions or delete markers.
Each restored object is conditionally created and read back with bounded size
and SHA-256 checks. The destination inventory is checked again before PostgreSQL
is restored; unexpected keys, extra versions or delete markers abort the database
restore. The existing row counts and format-2 row fingerprints remain mandatory.

An interrupted restore may leave objects in its **isolated** destination. This
is not automatically garbage-collected or repaired, and a non-empty target is
not silently reused. Preserve evidence, investigate the cause, and start with a
new verified-empty isolated target. Retention, object version IDs and deletion
history are not reconstructed as equivalent provider state.

Unexpected SDK/libpq exceptions are redacted at the CLI boundary. Do not publish
private endpoint inventories, raw database dumps, manifests or operator logs.

## Evidence

`python3 -m unittest discover -s tests -p test_pg_s3_snapshot_safety.py -v`
exercises hostile manifest types, conflicting history, duplicate fields, unsafe
files, mutation during capture, bounded reads and diagnostic redaction. Existing
snapshot unit tests and the PostgreSQL/Moto end-to-end restore remain required.
Synthetic and Moto checks are not a real S3-provider report or measured RPO/RTO.
See [release gates](release-gates.md) for the remaining acceptance requirements.

Primary API contracts: [S3 ListObjectVersions](https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectVersions.html)
and [S3 GetObject](https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html).


## Database schema V3 acceptance

The offline PostgreSQL/S3 snapshot remains **file format 2**, but now
understands Packory **database schema 3**, including the `nuget_digests`
table of published SHA-512 checksums. For V3 databases the table must be
present in the captured counts and row-value fingerprints. Its absence or
unexpected presence in a legacy schema 2 manifest is rejected; no silent
"restore without checksums" downgrade is allowed. Schema 1/2 snapshots
remain identifiable and do not claim missing V3 checksum evidence.


## V4 private feed coverage

Database schema V4 introduces `feed_nuget_versions`. The existing
file-format-2 PostgreSQL/S3 snapshot requires an exact row-value
fingerprint and count for this table whenever
`database_schema_version=4`. Missing or extra table entries are
rejected. Restore into an isolated fresh database must preserve the
per-feed SHA-512, feed linkage and revision identity, not merely global
NuGet artifacts. This still does **not** provide real S3 provider
certification, consistent hot snapshots or independent publisher
provenance.

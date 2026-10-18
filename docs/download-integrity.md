# Stored download integrity: tested subset

Publication-time metadata, not a newly calculated hash of potentially damaged
bytes, is the reference for a download check.

| Adapter | Reference and check before serving |
| --- | --- |
| OCI blobs / manifests | SHA-256 content address; manifests also check stored size. Corrupt mount sources are rejected. |
| Cargo hosted download | Exact crate name/version in stored sparse-index metadata and its SHA-256 cksum. Missing, ambiguous or malformed claims fail closed. |
| PyPI hosted file | Stored PyFileEntry SHA-256 **and size**. Both must match the file before returning data or ETag. |
| NuGet / npm / Skills | Do not infer complete stored-corruption coverage from the rows above. Their ingestion checks and native client hashes are distinct from a full server-side inventory verifier. |

Cargo/PyPI integrity failures return **503**, including a range request for a
small part of a damaged artifact. Neither damaged bytes nor a newly learned hash
is accepted as a replacement. Existing authentication is checked first; missing
files retain their existing 404 behavior. Failed verification preserves bytes
and metadata for operator investigation rather than silently repairing or deleting
them. I/O failures during verification are also refused; request cancellation is
not converted into successful integrity evidence.

Checks stream files with bounded buffers. Hashing adds a full-file read before
the response, including a range response; throughput/latency at scale is not yet
qualified. A successful read check does not establish trusted publisher identity,
signatures, malware safety, atomic PostgreSQL/S3 commits or protection against
an adversarial local process modifying files after verification. Storage access
must enforce the immutable-blob operating model.

`DownloadIntegrityTests` publishes through HTTP, confirms a successful read,
then changes, truncates or extends the stored file and requires refusal for both
normal and ranged reads. Its synthetic fixtures test integrity boundaries, not
native archive conformance. Existing Cargo/pip client smoke tests remain required.
`ArtifactDigestTests` checks name/version binding, malformed/duplicate metadata,
size/hash mismatches, absent files and identifier boundaries.

Cargo/Python names now reject final control characters instead of accepting a
regex match before a final newline. Python filenames are limited to 255 ASCII
characters, including the extension, for the filesystem-backed staging/cache
contract. Existing accepted case/normalization rules are otherwise unchanged.

This advances M1.6 and MS3/MS6 only for the named cases. Global startup/readiness
still lacks complete authoritative digest coverage across all six artifact
families; do not mark the real-provider restore or production release gates done.


## Persisted NuGet publication SHA-512 (MS1.6)

NuGet packages **newly hosted or verified through nuget.org read-through**
retain canonical SHA-512 plus exact size in PostgreSQL/SQLite schema V3.
Hosted `nuget_digests` rows and audit metadata commit atomically; the
source blob is written first under the existing recoverable publication intent.
NuGet mirror bytes must first match the upstream `.nupkg.sha512` before the
metadata/digest pair is recorded; mirrors cannot overwrite existing hosted
metadata or an immutable recorded checksum.

For revisions with stored metadata, reads now verify size and SHA-512 from
the immutable ledger; corruption or a stale local cache fails closed with a
gateway error, including download, `.sha512` and `.nuspec` paths. The
maintenance-integrity scanner uses the same recorded SHA-512, and its S3
inspection hashes the configured remote object rather than assuming the
local cache is current.

**Limitations:** Legacy V1/V2 NuGet revisions with no saved checksum are
still readable but unverified; compatibility is not integrity assurance.
An upstream package checksum authenticates agreement with upstream-declared
data, not the true producer or malware safety. S3/metadata writes are not an
ACID transaction; coordinated restore, immutable S3 policies and an
independent verifier remain necessary.

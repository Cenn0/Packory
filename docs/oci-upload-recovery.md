# OCI chunk integrity and single-writer restart recovery

This implementation extends the **experimental single-writer** adapter. It is
not an OCI certification, shared-session service, or active-active guarantee.

## Native request contract

`PATCH` and a final `PUT` accept an inclusive `Content-Range: start-end`.
When supplied, the start must equal the persisted committed upload offset;
range length, `Content-Length` (when present), and actual bytes must agree.
Invalid/replayed ranges return **416** and the existing `Location` and `Range`.
Clients can query the upload URL with `GET` (**204**) and continue from that
acknowledged offset. Requests without `Content-Range` remain accepted for
compatibility with existing tested Docker clients, but do not provide ranged
replay detection. Always use the returned Location rather than rebuilding it.

Only a complete chunk is acknowledged. Cancellation, body truncation, an
oversized chunk, or a failed snapshot commit rolls staging back to the previous
committed length. Rollback does not use the cancelled HTTP request token. If
rollback itself fails, the in-memory session is made unusable for investigation.

The final SHA-256 covers the entire blob, not just the final chunk. A failed
final digest does not publish metadata. Successful completion closes the staging
file before deleting it, including on platforms with restrictive file sharing.

## Persisted session format 2

The paired private `oci-staging/<uuid>.json` and `.tmp` files bind a repository,
last-touch time, and **CommittedLength**. Bytes are flushed before an atomically
replaced snapshot commits the offset. A process restart on the same data directory
truncates only a tail beyond that offset. A shorter file is never extended with
zeros and is not resumed. Unknown formats, invalid metadata, symlinks and
oversized files are not trusted or silently repaired.

**Upgrade note:** old session snapshots do not contain an acknowledged offset.
They are intentionally not resumed: complete active uploads before upgrade or
restart those uploads afterward. Their files remain for operator review. Published
blobs, manifests, native URLs, and metadata schema are unchanged. A rollback to
older code must start with an empty staging directory after preserving old staging
in a private backup; old code does not implement the new committed-offset contract.

There are at most **1024 active in-memory sessions**, each capped by
`MaxArtifactBytes`, with the existing 30-minute idle TTL. New sessions return 429
when capacity is exhausted. Cleanup and cancellation respect each ticket's lock;
cleanup never deletes a chunk while that ticket is busy. These bounds do not
replace a staging-volume quota, proxy request limits or a load acceptance test.
Operator retention is still required for abandoned/untrusted on-disk staging.

File flushes and atomic replacement target **process-crash recovery**. Filesystem,
directory-entry durability, device caches, power loss and actual S3-provider
behavior require separate infrastructure drills. The committed-offset protocol
is not distributed fencing or a cross-store transaction.

## Additional fail-closed native behavior

Filesystem OCI blob/manifest GET and HEAD verify SHA-256 before returning bytes;
mounting a corrupted blob is rejected rather than propagating its reference.
The manifest read also checks its metadata size. Existing S3 integrity checks
remain in force. This is byte-integrity checking, not signature or publisher
identity verification. Full filesystem/S3 inventory and non-OCI digest coverage
remain separate MS1 work.

The referrer `artifactType` filter is actually applied before claiming
`OCI-Filters-Applied`. Accepted subject associations return `OCI-Subject`.
`n=0` tag requests return an empty page without a next link. Malformed schema,
subject and descriptor-array values are rejected instead of causing unhandled
JSON conversion exceptions. Descriptor and client coverage is still a tested
subset, not blanket protocol conformance.

## Reproducible checks

`dotnet test Packory.slnx -c Release` includes `OciChunkSafetyTests` and
`OciProtocolHardeningTests`: ordered/raced/replayed chunks, length mismatch,
disconnect, cancellation, capacity failure, metadata-commit failure, crash tails,
truncated committed bytes, stale tickets, cross-repository isolation, native
HTTP statuses, corruption refusal, filtering and malformed manifests.
Existing Docker push/pull smokes and PostgreSQL/Moto tests must remain green.
The in-process fault tests do not substitute for real process-kill or provider
network-partition drills. See [release gates](release-gates.md).

Reference contract: [OCI Distribution specification v1.1.1](https://github.com/opencontainers/distribution-spec/blob/v1.1.1/spec.md).
Roadmap scope: M1.3, M3.5/M3.6 and part of M6.3; no milestone is closed merely
because this bounded change has passed its tests.

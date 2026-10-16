# OCI Distribution (experimental hosted registry)

Packory exposes a hosted OCI Distribution API on `/v2/`. This is an initial
single-node implementation, **not** full distribution-spec certification.

## Implemented

- `GET /v2/`: registry negotiation.
- `GET/HEAD /v2/<repo>/blobs/<sha256:digest>`: repository-authorized,
  content-addressed blob downloads; digest and byte count are preserved.
- `POST /v2/<repo>/blobs/uploads/`, `PATCH` and final `PUT ?digest=`:
  short-lived disk-backed chunk upload sessions; upload digest is computed
  and checked before immutable storage is referenced.
- `POST .../uploads/?mount=<digest>&from=<repo>`: cross-repository mount
  only when the source repository already references the verified blob.
- `GET/HEAD/PUT /v2/<repo>/manifests/<tag|digest>`: OCI image/indices,
  OCI artifact manifests and Docker v2 media types; all blob/manifest
  descriptors must resolve within the repository before publishing.
- `GET /v2/<repo>/tags/list`: sorted tags and paginated Link header.
- `GET /v2/<repo>/referrers/<digest>`: OCI 1.1 style subject index.
- Single-writer locally persisted upload sessions (restart resume on the same data directory), startup orphan quarantine, read/write
  credentials, published-manifest audit, and container-independent tests.

### Authentication

By default reads are anonymous. Writes require an existing Packory admin/write
token via `Authorization: Bearer <token>` or `Authorization: Basic` where
the password contains the token. This is intended for ORAS and other clients
that can configure Basic credentials; TLS is mandatory outside loopback.

Packory does **not** currently expose the Docker Bearer-token challenge/grant
endpoint or per-repository token scopes. A native Docker CLI smoke test now exercises a local scratch image build, Basic-authenticated login, push, tag discovery and pull. This is **one tested Docker flow**, not a Docker/Helm/ORAS compatibility matrix or production certification.

### Native client acceptance\n\nAfter a Release build, run `bash scripts/smoke-oci-docker.sh` with Docker Engine, Bash, .NET 10, Python 3, curl and OpenSSL. The script uses loopback port 18082, a temporary Docker credential directory and a `FROM scratch` image; it does not pull a public base image. It verifies a real `docker push`, the Packory tag-list API and a subsequent `docker pull`. On CI this shares the Linux job with the other hosted registry smoke tests. The test sets `PACKORY_ANONYMOUS_READ=false` so Docker receives a Basic challenge during registry negotiation. With anonymous reads enabled, the Docker push path has not passed this native-client test (Docker did not retry the later 401). Do not claim anonymous-read/native-Docker-push interoperability until that flow has separate passing evidence. A successful run also does not establish offline layer-cache independence, multi-architecture support, signature compatibility, resumable uploads or comprehensive OCI conformance.\n\n### Docker authentication negotiation with anonymous generic reads

Opt-in `PACKORY_OCI_AUTHENTICATED_PING=true` makes `GET /v2/` require a
writer/admin credential **even if** `PACKORY_ANONYMOUS_READ=true`. This causes
Docker to receive a Basic challenge during registry negotiation and proactively
authenticate future push requests. Native CI runs a second real Docker push/pull
scenario with that combination. **Tradeoff:** anonymous Docker clients cannot
complete the registry ping; other Packory APIs still honor anonymous reads.
Leave this flag disabled if public anonymous Docker pull must work, and configure
a private Docker registry instead until a separate, compliant Bearer-token
service supports different pull/push scopes. No OCI Bearer token service,
repository-scoped OAuth grant exchange or vendor conformance is claimed.

### Operational limits

Blob upload sessions are bounded to `PACKORY_MAX_ARTIFACT_BYTES` (default
100 MiB); an active upload expires after 30 minutes and is removed when
the process restarts. **No resumable upload survives restart**.
The active session map is single-process only. OCI registries commonly need
much larger blob sizes; S3 streaming / multipart uploads and scalable,
durable upload sessions are future work.

The implementation supports a restrictive lowercase ASCII repository-name
grammar and SHA-256 digests. It does not expose deletion, garbage collection,
automatic image signature verification, remote OCI proxying or complete
distribution-spec conformance testing. OCI tags are mutable; bytes are immutable
by digest.

Example endpoint: `http://localhost:8080/v2/` for loopback evaluation.

Primary specification: https://github.com/opencontainers/distribution-spec/blob/main/spec.md

## Single-writer session restart semantics

Upload bytes and a bounded repository-bound session descriptor are persisted under `data/oci-staging/`. After a process restart on the **same host/data directory**, eligible 30-minute sessions can resume. Corrupt metadata, oversized bytes or an unknown/reused UUID fail closed; old unbound `.tmp` files remain for operator retention handling. The descriptor is atomically replaced after successful PATCH. 

This is **not** multi-node session transfer, distributed locking, multipart S3, process-failover fencing or power-failure durability certification. Do not use it to claim general OCI upload-resume or active-active support; test the exact native client/version and fault boundary for each support claim.

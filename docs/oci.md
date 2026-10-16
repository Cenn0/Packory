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
- Single-process upload tracking, startup orphan quarantine, read/write
  credentials, published-manifest audit, and container-independent tests.

### Authentication

By default reads are anonymous. Writes require an existing Packory admin/write
token via `Authorization: Bearer <token>` or `Authorization: Basic` where
the password contains the token. This is intended for ORAS and other clients
that can configure Basic credentials; TLS is mandatory outside loopback.

Packory does **not** currently expose the Docker Bearer-token challenge/grant
endpoint or per-repository token scopes. A native Docker CLI smoke test now exercises a local scratch image build, Basic-authenticated login, push, tag discovery and pull. This is **one tested Docker flow**, not a Docker/Helm/ORAS compatibility matrix or production certification.

### Native client acceptance\n\nAfter a Release build, run `bash scripts/smoke-oci-docker.sh` with Docker Engine, Bash, .NET 10, Python 3, curl and OpenSSL. The script uses loopback port 18082, a temporary Docker credential directory and a `FROM scratch` image; it does not pull a public base image. It verifies a real `docker push`, the Packory tag-list API and a subsequent `docker pull`. On CI this shares the Linux job with the other hosted registry smoke tests. A successful run does not establish offline layer-cache independence, multi-architecture support, signature compatibility, resumable uploads or comprehensive OCI conformance.\n\n### Operational limits

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

# OCI Distribution (experimental hosted registry)

Packory exposes a hosted OCI Distribution API on `/v2/`. This is a bounded
single-writer implementation, **not** full distribution-spec certification.

## Implemented

- `GET /v2/`: registry negotiation.
- `GET/HEAD /v2/<repo>/blobs/<sha256:digest>`: repository-bound references,
  content-addressed downloads and digest verification before returning bytes.
- `POST /v2/<repo>/blobs/uploads/`, `PATCH` and final `PUT ?digest=`:
  disk-backed upload sessions with a persisted committed offset. Supplied
  `Content-Range` must match the offset, Content-Length and actual body length.
  Invalid/replayed chunks return 416 with Range and Location.
- `GET/DELETE <upload-location>`: query the committed offset or cancel a session.
- `POST .../uploads/?mount=<digest>&from=<repo>`: mount only when the source
  repository references the blob and the stored SHA-256 matches.
- `GET/HEAD/PUT /v2/<repo>/manifests/<tag|digest>`: OCI image/indices,
  OCI artifact manifests and Docker v2 media types; checked content references
  must resolve within the repository before publishing. Global and private
  manifests share descriptor admission: valid media type, SHA-256, exact
  byte size and matching inline data; alternate download `urls` and duplicate
  JSON fields are rejected. Required config/layer/index/blob shapes and up
  to 4,096 descriptors per array are checked. This is a restricted hosted
  profile; media-type/client combinations remain a tested subset.
- `GET /v2/<repo>/tags/list`: sorted tags and paginated Link header; n=0 returns
  an empty page without a next link.
- `GET /v2/<repo>/referrers/<digest>`: subject index with actual artifactType
  filtering. Accepted manifest subjects are acknowledged with OCI-Subject.
- Single-writer session restart recovery on the same data directory,
  startup orphan quarantine, credentials and published-manifest audit.

### Authentication

Reads are anonymous by default. Writes require an existing Packory admin/write
token via `Authorization: Bearer <token>` or HTTP Basic with the token as the
password. The existing global read policy and write namespace grants still
apply; independent private-feed read isolation is not established by these routes.
TLS is mandatory outside loopback.

Packory does **not** expose the Docker Bearer-token challenge/grant endpoint or
per-repository OAuth token scopes. Native Docker smoke tests exercise Basic-auth
login, push, tag discovery and pull. These are specific tested Docker flows,
not a Docker/Helm/ORAS matrix or production certification.

### Native client acceptance

After a Release build, run `bash scripts/smoke-oci-docker.sh` with Docker Engine,
Bash, .NET 10, Python 3, curl and OpenSSL. The script uses loopback port 18082,
temporary Docker credentials and a `FROM scratch` image. It does not pull a
public base image. It verifies `docker push`, the tag API and `docker pull`.
The baseline test disables anonymous reads so Docker receives a Basic challenge
at registry negotiation. Passing does not establish layer-cache independence,
multi-architecture support, signature compatibility or complete conformance.

### Docker authentication negotiation with anonymous generic reads

`PACKORY_OCI_AUTHENTICATED_PING=true` makes `GET /v2/` require a writer/admin
credential even when `PACKORY_ANONYMOUS_READ=true`. This provides the initial
Basic challenge. CI exercises this combination separately. The tradeoff is that
anonymous Docker clients cannot complete the registry ping; other APIs continue
to honor their existing anonymous-read policy. This is not a substitute for a
standard Bearer grant service with separate pull/push scopes.

### Operational limits and upgrade

Each upload is bounded by `Packory:MaxArtifactBytes`. Sessions expire after 30
idle minutes; at most 1024 sessions can be active in one store. Cleanup does not
delete a ticket while its chunk gate is held. Filesystem volume quotas and
reverse-proxy resource limits remain necessary; no staging-capacity SLA is claimed.

Session format 2 persists a committed length after the payload flush. On a
single-writer process restart, only bytes up to that offset are resumed and an
uncommitted tail is truncated. Missing committed bytes fail closed and are never
reconstructed with zeros. Old session descriptors without an acknowledged offset
are preserved but not resumed: finish uploads before upgrading or restart them.
Published artifacts and database schemas are unaffected by this session-format
change. Preserve staging before rollback and do not run old code against active
format-2 sessions. See [chunk recovery and upgrade contract](oci-upload-recovery.md).

No shared-session transfer, multi-writer fencing, S3 multipart streaming,
power-loss durability certification, image signature verification, upstream OCI
proxy, deletion or garbage collection is provided by this change. OCI tags are
mutable; blob and manifest bytes are immutable by digest. Remaining release
requirements are tracked in [release gates](release-gates.md).

Example endpoint: `http://localhost:8080/v2/` for loopback evaluation.

Reference: [OCI Distribution specification v1.1.1](https://github.com/opencontainers/distribution-spec/blob/v1.1.1/spec.md).

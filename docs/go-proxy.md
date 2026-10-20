# Hosted Go module registry — V4 E3

**Availability:** Packory provides a complete, immutable **hosted GOPROXY read flow for the validated release-version and archive subset** described here. Package publication uses a Packory-specific authenticated multipart endpoint because Go defines no standard module-publish API. Real native-client acceptance is executed in `scripts/smoke-go.sh`; check the exact commit's CI before relying on that evidence.

This is **not** a transparent public Go proxy, private per-feed Go registry, or general Go module conformance certification. The platform release remains gated by real-provider recovery and independent security assessment.

## Native client URL and publishing

HTTP base: `http://127.0.0.1:8080/go` for isolated loopback tests. Shared deployments require HTTPS.

```bash
# Prepare a canonical module ZIP. Entries must use module@version/ as prefix.
export PACKORY_WRITE_TOKEN='<set outside tracked files>'
curl --fail-with-body --request POST \
  -H "Authorization: Bearer $PACKORY_WRITE_TOKEN" \
  -F 'module=example.com/hello' \
  -F 'version=v1.2.3' \
  -F 'time=2026-10-20T08:00:00Z' \
  -F 'mod=@go.mod;type=text/plain' \
  -F 'zip=@module.zip;type=application/zip' \
  http://127.0.0.1:8080/api/go/modules
```

The optional `time` field is used to generate immutable Go `.info` metadata and should reflect the release's authoritative UTC timestamp. Go version coordinates cannot be overwritten. Duplicate publishing returns HTTP 409; incomplete or invalid modules fail before any visible revision is committed. The API responds HTTP 201 with ZIP SHA-256, module-content `zipH1`, size, and proxy URL on successful publication. `zipH1` uses Go's canonical `h1:` content-hashing convention; it is a value computed from the uploaded module, **not** an attestation of publisher identity or a lookup against a public checksum database.

Consumer configuration for a **private synthetic** module where the public Go checksum database does not have a record:

```bash
export GOPROXY=http://127.0.0.1:8080/go
export GOPRIVATE=example.com
export GONOPROXY=none
go mod download example.com/hello@v1.2.3
go mod verify
```

`GOPRIVATE` disables public checksum-database lookup for matching private domains. `GONOPROXY=none` keeps the explicitly configured Packory GOPROXY active instead of switching to VCS-direct fetching. For real deployments, configure these scopes to match only your actual private module namespaces, use HTTPS and audited credentials. Do not disable checksum verification globally. This example assumes anonymous read is enabled; for authenticated global read, configure an approved Go HTTP credential mechanism over TLS.

Supported read routes:

| Native request | Behavior |
| --- | --- |
| `/go/<escaped-module>/@v/list` | Available published versions, SemVer ordered |
| `/go/<escaped-module>/@v/<escaped-version>.info` | Immutable JSON `Version` and `Time` |
| `/go/<escaped-module>/@v/<escaped-version>.mod` | Exactly published `go.mod` bytes |
| `/go/<escaped-module>/@v/<escaped-version>.zip` | Exactly published ZIP bytes |
| `/go/<escaped-module>/@latest` | Highest canonical SemVer revision |

Go's uppercase path encoding uses `!` followed by a lowercase character. Noncanonical or unsupported request paths are rejected. Never assume that generic Raw artifacts are native Go modules.

## Durability and security boundaries

A version is stored in **one immutable object** containing `.info`, `.mod` and `.zip`, with a V7 database row and transactional catalog/audit insertion. A publication intent is persisted before its object write; only a committed database revision becomes visible. On interruption, orphan/missing-object evidence remains in recovery, and readiness is degraded for referenced missing objects. An operator must investigate unmatched intents; this is deliberately not an unsafe automatic cross-store rollback.

- Publication-time SHA-256 covers the outer stored bundle, original module ZIP and `.mod`; reads verify immutable object integrity before serving. The importer additionally computes Go `h1:` from the **uncompressed bytes and full names of ZIP entries**, in lexicographic name order. This is independent of ZIP compression or entry order. It is returned as `zipH1` for independently verifiable client interoperability; no public checksum-database verification, signature or provenance validation is performed.
- The existing ZIP validator enforces a strict cross-platform ASCII path subset, regular files only, matching module/version entry prefix, directory/file collision checks, optional root `go.mod` identity and bounded decompression. A ZIP containing a root `go.mod` must contain the same exact bytes as the separately published `.mod`.
- Ceilings: **32 MiB compressed ZIP**, 64 MiB aggregate uncompressed, 16 MiB per entry, 1 MiB root `go.mod`, at most 4096 entries. These are stricter than Go's own ZIP specification, and thus reject some otherwise valid Go modules.
- Read auth uses the existing global registry policy. Publishing requires a write/admin role, with optional `go:<prefix>*` namespace grants; feed-scoped managed tokens cannot read global Go content when authenticated reads are enforced.
- SQLite and PostgreSQL use the same V7 contract; filesystem and configured S3 blob stores share publication/recovery code. PostgreSQL+S3 metadata snapshots include `go_modules` fingerprints, but **real S3-provider coordinated restore is a separate release gate**.
- No remote fetches, public package fallback, dynamic code execution or arbitrary caller-configured upstream URLs. Multiple active writers remain unsupported.

## Coverage and remaining roadmap

The CI smoke creates a synthetic Go module, imports it with native API calls, and runs a real `go mod download`, `go build`, `go mod verify`, `@latest`, fresh cache and process-restart check against locally served bytes. It compares `zipH1` against the native client's `go.sum` entry and fails on disagreement. xUnit covers auth, immutable version conflicts, corruption rejection, schema, and recovery states. This proves the **exact hosted client/operation combination** that passed CI, not the full upstream Go specification.

Still open under V4 E0/E3/E11: independent multi-feed Go IAM, pseudo-versions and full Unicode archive compatibility, public HTTPS read-through with SSRF-safe origin policy, checksum-database integration, alternative OS/client qualification, provider-specific restore/failure drills and independent security qualification.

References: [official Go Modules Reference](https://go.dev/ref/mod#module-proxies), [module ZIP format](https://go.dev/ref/mod#module-zip-files), [V4 roadmap](roadmap-v4.md), [support matrix](support-matrix.md) and [release gates](release-gates.md).

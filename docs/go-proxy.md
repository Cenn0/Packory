# Go module proxy groundwork — V4 E3

**Status: no Go registry.** Packory implements no Go module HTTP routes, hosted Go publication, GOPROXY mirror, checksum database integration or native Go client qualification. Neither the capability API nor dashboard advertises Go registry support.

## Completed offline building blocks

- **GoProxyCoordinates** parses a limited canonical GOPROXY coordinate subset: module/@v/list and module/@v/vX.Y.Z.{info,mod,zip}. Mixed-case module names use Go's ! escaping. Unsupported pseudo-versions, @latest, legacy edge cases and malformed escaping fail closed.
- **GoModuleZipInspector.Inspect(stream, coordinate)** is an **offline, read-only preflight** for already-downloaded ZIP bytes. It does not save, extract, execute, serve or publish them.
- The inspector requires a readable, seekable stream. It validates the exact decoded module@version/ entry prefix, rejects traversal and ambiguous filenames, duplicates, case-insensitive collisions, file/directory collisions, Unix symlinks, irregular entries and nested go.mod files, and bounds decompression. If a root go.mod exists, a strict UTF-8, unquoted, single-line module directive must match the requested module name. A missing root go.mod is allowed by the Go ZIP format.
- It returns a **SHA-256 digest of compressed ZIP bytes** for a future storage integrity ledger. This digest is **not** the Go h1: module-content hash, is not a checksum database response, and does not establish module provenance or end-to-end integrity.
- Positive and negative xUnit tests cover paths, versions, identity, collisions, symlinks and budgets. These are local code tests, **not** Go CLI smoke or complete Go format conformance.

### Deliberately conservative limits

| Constraint | Current preflight limit | Notes |
| --- | --- | --- |
| Compressed ZIP | 32 MiB | Reject before archive parsing |
| Total uncompressed bytes | 64 MiB | Validate declared and actual read lengths |
| Maximum single file | 16 MiB | Checked per entry |
| Root go.mod | 1 MiB | Strict UTF-8, single unquoted module directive |
| Entries | 4096 | Hard entry-count bound |
| Filenames | Restricted ASCII subset | Unicode names and directory-only ZIP entries intentionally rejected |

Go's **published module ZIP limits are broader** (500 MiB compressed and uncompressed), so this helper intentionally does not accept all valid Go modules. A future ingest endpoint must enforce upload bounds **before buffering**, and the inspector alone is not sufficient for accepting or distributing untrusted modules. Validation of ZIP central-directory edge cases and full Go filename semantics must be independently qualified.

## Required before enabling V4 E3

1. Finish canonical module identity, versioned persistence, per-feed authorization and private namespace suppression. Test same-name module releases in two isolated feeds, revoked grants and 401/403/404 paths.
2. Stage .info, .mod and .zip as **one immutable visible revision** with durable intents; recover incomplete publication after every DB/blob failure on SQLite/filesystem and PostgreSQL/S3-emulator.
3. Use a complete Go-aware ZIP validator (for example, a separate sandboxed worker using official [golang.org/x/mod/zip](https://pkg.go.dev/golang.org/x/mod/zip)) for full Unicode/path/pseudo-version semantics, embedded vs separately published go.mod consistency and Go archive-content hashes. Never execute package code or build hooks on the Packory server.
4. Expose correct GOPROXY list, .info, .mod, .zip, and optionally @latest HTTP routes. Bind authorization and feed policy server-side. Do not leak private module names.
5. Add pinned allowlisted HTTPS upstream policy with strict redirects, DNS/SSRF controls, bounded streaming, retry/timeout rules and no credential forwarding.
6. Run real pinned Go clients against synthetic hosted and transitive dependencies (go mod download, go build, go mod verify) using isolated caches and documented GOPRIVATE/GONOSUMDB/sumdb configuration; do not disable module-integrity checking by default.
7. Update native support matrix and capability flags only with independently reproducible version/client/operation evidence. Real-provider restore and E11 release gates remain open.

**Primary reference:** [Go Modules Reference — module proxies, ZIP and authenticity](https://go.dev/ref/mod#module-zip-files).

See [V4 roadmap](roadmap-v4.md), [20 October checkpoint](roadmap-status-2026-10-20.md), [support matrix](support-matrix.md) and [issue #91](https://github.com/Cenn0/Packory/issues/91).

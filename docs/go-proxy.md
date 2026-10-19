# Go module proxy groundwork — V4 E3

**Status: parser and negative tests only.** No Go registry, hosted module upload, read-through proxy or native Go client support is implemented by this change. Do not advertise GOPROXY support in the Packory capability API or dashboard yet.

The parser covers these deliberately limited request shapes:
- module-name/@v/list
- module-name/@v/vX.Y.Z.info
- module-name/@v/vX.Y.Z.mod
- module-name/@v/vX.Y.Z.zip

Go module paths and versions require wire-level uppercase escaping; github.com/Azure/sdk becomes github.com/!azure/sdk. The implementation validates canonical escaping, rejects ambiguous percent/URL/path syntax, Windows-reserved names and major-version mismatches, and accepts a subset of canonical SemVer release versions. It does not accept all Go pseudo-versions, historical edge cases or optional @latest. This is intentionally fail-closed preparatory code, **not** full Go protocol conformance.

## Missing before native E3 release

1. Specify immutable package metadata and Go module ZIP bundle semantics; verify module@version/ archive prefix, go.mod identity, resource caps, zip-slip, duplicate/case-folding paths and symlinks. Where feasible, evaluate the official golang.org/x/mod/zip implementation without executing untrusted code.
2. Implement consistent staged triplet publish (.info, .mod, .zip) using durable intents, atomic visibility or explicit incomplete states, SHA-256 and checksum database policy.
3. Implement actual GOPROXY read endpoints and version lists with native HTTP status semantics; no private-namespace fallback.
4. Implement allowlisted HTTPS upstream proxy with strict redirects, DNS/SSRF defenses and per-feed authorization; never forward local credentials upstream.
5. Test native go mod download and go build using fresh caches, synthetic modules and explicit checksum policy, recording Go versions; negative tests for tamper, incomplete triplets and offline use.
6. Add Go to the public capability API, dashboard and support matrix **only after tests confirm native behavior**.

This change intentionally does not alter schema, routes, active readers or production support claims.

**Primary source:** [Go Modules Reference – module proxies and ZIP format](https://go.dev/ref/mod#module-proxies).

Broader gates remain in [V4 Roadmap](roadmap-v4.md), [Support Matrix](support-matrix.md) and [issue #91](https://github.com/Cenn0/Packory/issues/91).

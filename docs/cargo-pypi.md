# Cargo and PyPI hosted adapters (experimental)

The new adapters are **hosted-only**, not public upstream mirrors. They retain native-client
wire formats and feed into the existing catalog, static-token authorization, audit stream,
per-artifact locks and recovery process. These are not a production-complete implementation
of either ecosystem.

## Cargo alternate registry

Registry sparse index: `<base>/cargo/index/`

```toml
# .cargo/config.toml for a loopback evaluation
[registries.packory]
index = "sparse+http://localhost:8080/cargo/index/"
```

With a write token configured as an environment variable, `cargo publish --registry packory`
can target the Cargo publish binary protocol. Cargo sends an **unprefixed** `Authorization`
token, accepted only on the `/cargo` route family. Other routes retain Bearer or Basic
authentication. For authenticated read access set `PACKORY_ANONYMOUS_READ=false`;
the index `config.json` then advertises `auth-required: true`.

The hosted index is deterministic, uses the mandated sparse path layout, emits
ETag/304 and records a SHA-256 checksum of each immutable `.crate` file.
This registry is **not suitable as an interchangeable crates.io source-replacement**
without a separately implemented, byte-exact crates.io mirror. Only lowercase ASCII
crate names are currently accepted; this is an explicit subset of Cargo's name grammar.
Yank/unyank, ownership, reverse dependencies, provenance, index consistency under
multiple nodes and crates.io mirroring are not implemented.

## PyPI / pip

Simple API root: `<base>/pypi/simple/`. Both PEP 503 HTML and PEP 691 JSON
representations are supported. Project names follow PEP 503 normalization.
Every uploaded file carries a SHA-256 digest in HTML fragment/JSON metadata;
the byte stream can be downloaded from `/pypi/files/<name>/<filename>`.

Upload endpoint: `POST <base>/pypi/legacy/` accepts one multipart file and
the traditional `name`, `version`, `content` and optional `sha256_digest`
fields (compatible with Twine's basic form of upload).

Example local-only pip use:

```bash
python -m pip install --index-url http://localhost:8080/pypi/simple/ your-package
```

There is no implicit upstream fallback or PyPI mirror. To prevent dependency
confusion, configure pip sources deliberately and use HTTPS for authenticated
operations outside localhost.

Multiple wheel/sdist filenames may belong to one version. Filename checks are
currently deliberately restrictive; full PEP 427/625 archive metadata validation,
Requires-Python markers, metadata sidecars, yanked semantics, provenance and
signature validation are not implemented.

## Evidence and limits

Deterministic API-level tests verify binary Cargo publishing, sparse-index lines,
SHA-256, ETag/304 and PyPI upload, HTML/JSON, integrity and downloads. In addition,
regular CI runs `scripts/smoke-ecosystems.sh` against one isolated local Packory
instance: real `cargo publish` and consumption via Cargo's alternate registry,
and real `pip install` resolving two locally hosted wheels including a transitive
dependency. This establishes a concrete tested native-client workflow, **not**
broad Cargo/pip/Twine version interoperability or release readiness. Running a trusted real-client E2E suite
(including transitive dependencies and offline cache) remains required.

Primary specifications:
- https://doc.rust-lang.org/cargo/reference/registry-index.html
- https://doc.rust-lang.org/cargo/reference/registry-web-api.html
- https://packaging.python.org/en/latest/specifications/simple-repository-api/
- https://docs.pypi.org/api/upload/

## Native Cargo yank / unyank (hosted only)

Packory now exposes Cargo's native DELETE `/cargo/api/v1/crates/{name}/{version}/yank` and PUT `.../unyank` APIs. Both require a publisher/admin token and the matching Cargo namespace grant. They update only the `yanked` flag within the sparse index and write a transactional audit entry; immutable `.crate` bytes remain available for locked dependencies, following Cargo's interpretation. The sparse index ETag changes and is marked `no-cache` for revalidation. Concurrent requests within one writer are serialized by artifact locks.

The CI hosted smoke invokes the native `cargo yank` CLI in both directions. Exact multi-version Cargo client compatibility, crates.io proxying and distributed races are still **unverified**; this is not complete Cargo registry conformance.

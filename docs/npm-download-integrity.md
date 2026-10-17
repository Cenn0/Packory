# npm cached-download integrity boundary

All **previously stored** npm tarballs (hosted and read-through mirror) are checked
against metadata already recorded before a successful response. This applies to
ordinary GETs and HTTP Range requests; HTTP 503 refuses corrupted or
unverifiable bytes without returning any artifact data.

- Hosted versions are checked against their persisted version-manifest
  `dist.integrity` claim. This is generated from publication bytes during npm PUT.
- Mirrored versions are checked against the last cached upstream packument
  for the **exact package name and version**. The remote network is not contacted
  merely to validate an existing cached tarball. Absent/missing cached
  metadata fails closed. If the upstream later changes its digest, the old
  immutable cached bytes are not silently substituted.
- Valid SHA-512 integrity is preferred; legacy `shasum` (SHA-1) is accepted
  only when no `integrity` property is present. Malformed, duplicate or
  unsupported integrity claims do not fall back to SHA-1.
- Malformed JSON, unexpected values, duplicate identity/digest fields,
  file truncation, content modifications and I/O errors fail closed.
  The comparison hash is never learned from potentially damaged bytes.

**Scope:** Runtime verification of cached npm TAR files only. An upstream
integrity value is evidence of consistency with upstream metadata, **not**
evidence of the upstream publisher's trustworthiness. A compromised cache
metadata file and package file could be changed consistently by an attacker
with write access to both. Signed manifests, transparency and immutable
DB-backed digest pinning for mirrors remain separate gates.

**Operational effects:** Pre-response full-file hashing adds disk I/O and
latency, including for Range responses, and must be load-tested on the real
reference provider. After an upgrade, pre-existing mirrored packages without
usable cached metadata, or with ambiguous integrity metadata previously
accepted, will now return HTTP 503 and require operator investigation or
controlled reimport. Do not silently regenerate expected checksums from the
suspect tarball, delete forensic evidence or declare production readiness
based on these tests.

**Test evidence:** `NpmTarballIntegrityTests` covers publication,
same-size mutation, truncate/extend, Range refusal, exact identity/version,
malformed/duplicate JSON, SHA-512 refusal and legacy shasum parsing.
Native npm and provider CI jobs remain required for PR acceptance.

Related: [roadmap](roadmap.md), [release gates](release-gates.md),
[download integrity](download-integrity.md).

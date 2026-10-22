# CI supply-chain posture and SHA-pinned actions

As of **2026-10-17**, all first-party GitHub Actions in the public Packory workflows at this checkpoint are pinned to exact upstream commit SHAs. The SHA/tag associations were verified against GitHub's official `actions/*` release tags:

| Upstream action | Release | Exact tag commit |
| --- | --- | --- |
| `actions/checkout` | v7.0.1 | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
| `actions/setup-dotnet` | v6.0.0 | `a98b56852c35b8e3190ac28c8c2271da59106c68` |
| `actions/setup-node` | v7.1.0 | `949feb2413d6458794dcd2491c4babbbce0c15c1` |
| `actions/setup-python` | v7.0.0 | `5fda3b95a4ea91299a34e894583c3862153e4b97` |

Every checkout step sets `persist-credentials: false` and workflows retain minimal `contents: read` permissions. `setup-node` explicitly opts out of package-manager caching to avoid implicit cache storage.

## Updating pins

Review a new upstream patch release and its changes, fetch the **commit object behind the tag**, review required runner/Node versions, update workflow references together and run the complete PR matrix. An immutable SHA improves reproducibility but **does not certify** that an action or downloaded dependency is uncompromised.

## Remaining gates

[Exact-commit CI qualification](ci-qualification.md) now aggregates provider,
interoperability, source and scanner workflows and exports a minimal public
evidence index. This prepares V5-01/V5-03 controls; required branch contexts,
independent approval and production supply-chain evidence remain separate.

Docker build/base images and PostgreSQL service images are not yet digest-pinned; SBOM, image signatures/provenance, dependency/license/SAST coverage and independent supply-chain review remain open in [release gates](release-gates.md). Public repository workflow jobs are generally free under GitHub's public-repository terms, but this cannot be treated as a permanent zero-quota guarantee.

## Exact-source CI artifact (V5 assurance slice)

The dedicated `Source qualification evidence` workflow archives only
privacy-scanned, tracked files from the exact PR head or successful main SHA.
The short-lived artifact contains `source.tar.gz` and `manifest.json` with
its SHA-256, byte count, commit ID and GitHub run/attempt identifiers.

`scripts/verify_source_evidence.py` independently checks the manifest shape,
source-SHA equality, archive checksum and length, rejects symlinks/duplicate
JSON keys/unsafe scopes and allows **only** `source-only` evidence with
`productionRecommended: false`. Its negative fixtures run in normal CI.

Use the artifact only after checking the claimed run ID, workflow identity,
commit and conclusion through GitHub Actions. A self-reported run identifier
and SHA-256 checksum **are not cryptographic provenance**. This does not
establish native client interoperability, an actual provider restore,
independent review, vulnerability certification or production readiness.
Operator reports, identities and secrets must never be uploaded here. Consult
[release gates](release-gates.md) for independent production acceptance.

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

Docker build/base images and PostgreSQL service images are not yet digest-pinned; SBOM, image signatures/provenance, dependency/license/SAST coverage and independent supply-chain review remain open in [release gates](release-gates.md). Public repository workflow jobs are generally free under GitHub's public-repository terms, but this cannot be treated as a permanent zero-quota guarantee.

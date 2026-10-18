# Controlled GitHub Releases

GitHub Actions → **Controlled Packory release** runs **only by explicit manual dispatch on protected `main`**. The version input is a new `vMAJOR.MINOR.PATCH[-alpha/beta/rc.N]` tag. There is no routine automatic production publication.

- **Candidate** (default) checks source and tests, builds a framework-dependent Linux x64 package and container, and creates a private **draft** GitHub Release with tag `candidate-vX.Y.Z`. No public GHCR image is pushed. Draft candidate evidence is not equivalent to operational approval.
- **Public** publishes `vX.Y.Z` assets with SHA-256 and a versioned GHCR image **only after independent review**. Every one of the eight evidence gates in [release-gates.md](release-gates.md) must be verifiably signed for the exact `main` SHA. Missing evidence always stops publication; never add a bypass or a Boolean-only `ready` flag.

## Required independent approval configuration

Configure the repository Actions environment named **`release-approval`** with required reviewers and protect it from untrusted branches. For a public run, set the following environment variables **after the acceptance process**:

| Environment variable | Meaning |
| --- | --- |
| `PACKORY_PUBLIC_RELEASE_SHA` | Exact 40-character accepted commit from `main` |
| `PACKORY_RELEASE_ACCEPTANCE_REF` | Public-safe reference identifying the signed approval record (no names, hosts, or secrets) |
| `PACKORY_RELEASE_REVIEWER_KEY_ID` | Identifier of an independently managed verification key |

Set these **environment secrets** (never commit or print them):

| Secret | Meaning |
| --- | --- |
| `PACKORY_RELEASE_EVIDENCE_ZIP_B64` | Base64 ZIP of private `release-manifest.json`, `release-manifest.sig` and all eight signed report JSON summaries. Filenames must be flat, ordinary files without paths; total compressed size at most 6 MiB and each file at most 2 MiB. GitHub secret size limits may require concise, reviewed summaries; keep full raw operational proof in private custody. |
| `PACKORY_RELEASE_REVIEWER_KEY_B64` | Base64-encoded trusted PEM RSA public key, separately controlled from the release author |

The workflow validates the signature, hashes, report syntax, exact source SHA, reviewer key identifier and freshness using `scripts/verify_release_evidence.py`. Authenticity and relevance of the underlying results **still require human verification**. Never upload private reports or credentials as GitHub Release assets.

## Artifacts and policy

Successful public releases contain `packory-VERSION-linux-x64.tar.gz` (requires compatible .NET 10 runtime), `SHA256SUMS.txt`, and `ghcr.io/cenn0/packory:VERSION`. Verify downloads using `sha256sum -c SHA256SUMS.txt`. Review initial GHCR package visibility before advertising it as publicly pullable.

**Current state:** do not set acceptance variables or secrets until the real-provider PostgreSQL/S3 recovery, restore, security, load, privacy, supply-chain and production gates have actual independently reviewed results. GitHub workflow success is not a production certificate.

Tags created by the release job do not re-trigger tag-push workflows with `GITHUB_TOKEN`. Enable tag rules and GitHub release immutability in repository Settings. The workflow cannot configure those repository settings.

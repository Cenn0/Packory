# Automatic main-branch container images

Packory publishes **development** container images to GitHub Container Registry
(GHCR) after the complete `CI` GitHub Actions workflow succeeds for a push to
`main`. `Complete qualification` additionally requires all eight independent
provider/interoperability/source workflows at that exact commit; see
[CI qualification](ci-qualification.md). The publisher rechecks their results
before building. A merged pull request produces such a push. A failed/canceled CI run
does **not** publish an image. A superseded CI commit is skipped **before**
the workflow reads its build files, so the mutable `main` image tracks
the most recent *successfully validated* main commit
rather than an older workflow that happened to finish later.

## Image names and tags

| Service | Moving development tag | Commit-specific tag |
| --- | --- | --- |
| Registry + dashboard | `ghcr.io/cenn0/packory:main` | `ghcr.io/cenn0/packory:sha-<full-40-character-SHA>` |
| Offline SkillSpector worker | `ghcr.io/cenn0/packory-skillspector:main` | `ghcr.io/cenn0/packory-skillspector:sha-<full-40-character-SHA>` |

Both images are built from the **same exact CI-tested commit**. The workflow
validates that the Packory process starts, responds to liveness and readiness
checks, and that the worker image contains the expected scanner executable.
It assigns the `org.opencontainers.image.source` and `revision` OCI labels
and publishes both full-SHA tags before moving `main`.

**Use matching SHA tags for repeatable deployments**; updates of two
separate GHCR images are not atomic. For exact artifact reproducibility, pin
image **digests** after pulling and recording the published SHA manifests.
Commit-derived tags can still be overwritten by registry writers; the registry
does not enforce tag immutability here.
The `main` tag is mutable and can change on any subsequent successful merge.

## Deploy prebuilt images

On a machine with Docker Engine and Docker Compose:

```bash
git clone https://github.com/Cenn0/Packory.git
cd Packory
export PACKORY_ADMIN_TOKEN="$(openssl rand -hex 32)"
docker compose -f compose.ghcr.yaml pull
docker compose -f compose.ghcr.yaml up -d --no-build
docker compose -f compose.ghcr.yaml ps
```

The dashboard is available at `http://localhost:8080/` for local evaluation.
The published port is restricted to loopback. The named volumes retain artifact
data and offline scanner queues through image upgrades.

To deploy a specific verified commit, use the **same complete commit SHA** for
both services:

```bash
export PACKORY_IMAGE_TAG=sha-<full-40-character-SHA>
docker compose -f compose.ghcr.yaml pull
docker compose -f compose.ghcr.yaml up -d --no-build
```

Replace the placeholder with the commit SHA from the successful publishing
workflow. This is an example placeholder, not a literal command to run.
Rollback by selecting a previously published SHA tag and repeating pull/up.
Take verified backups before schema-changing upgrades; rolling back an image
does not automatically reverse a database migration.

For manual upgrades of the moving `main` tag:

```bash
docker compose -f compose.ghcr.yaml pull
docker compose -f compose.ghcr.yaml up -d --no-build
```

**Publishing an image does not update an already running deployment.** Automatic
server rollout is deliberately not configured, because that requires an
operator-defined target, backup/rollback process, and deploy credentials.

## GitHub package settings

The workflow uses the repository-scoped `GITHUB_TOKEN` with
`contents: read` and `packages: write`. No long-lived personal access token
and no email address are required. The `org.opencontainers.image.source`
label links each image to this public source repository. If a package already
exists with restrictive GHCR Actions permissions, grant this repository write
access to that package. Verify each package's visibility separately: a public
Git repository does **not** guarantee anonymous pulls from an existing
private GHCR package. The workflow performs authenticated SHA-tag read-back,
then requires **anonymous delivery of both complete images** before moving
either `main` tag. A credential-free Python OCI reader retrieves each manifest,
config and every compressed layer directly from GHCR and hashes the received
bytes against the exact descriptors. Local Docker layer caches cannot satisfy
this check. It verifies the expected source/revision labels and the declared
non-root user, then rereads the manifest by digest. Private/unreadable,
truncated, corrupt, wrong-revision or unexpected-platform images block mutable
tag promotion. The admitted profile is a single Linux/amd64 image manifest;
future multi-platform indexes need their own verification before enabling them.

A sanitized `container-delivery-<SHA>-<attempt>` Actions artifact records the
two immutable manifest digests, source, declared user, layer counts and verified
byte totals for 30 days. It contains no tokens, signed CDN URLs, config bodies
or raw diagnostics. An independently repeatable verification is:

```bash
python3 scripts/verify_container_delivery.py --sha <EXACT_COMMIT_SHA> \
  --output /tmp/packory-container-delivery.json
```

The output must be a new file. This command downloads the complete compressed
image content and can transfer several hundred megabytes. It requires only
Python 3, HTTPS and anonymous registry availability, with no Docker daemon or
account credentials. It does not extract or execute images. The separate CI
container build/runtime smoke and the qualified release gates remain necessary.
See the [OCI Distribution pull contract](https://github.com/opencontainers/distribution-spec/blob/v1.1.1/spec.md#pull).

- Images: [Packory](https://github.com/Cenn0/Packory/pkgs/container/packory)
  and [SkillSpector](https://github.com/Cenn0/Packory/pkgs/container/packory-skillspector)
- Pipeline: [Publish main container images](../.github/workflows/container-publish.yml)
- Deployment hardening: [Operations](operations.md)

## Security and release policy

`main` images are automatically distributed **development artifacts**, not
approved production releases. The existing manually dispatched
[Controlled Packory release](../.github/workflows/release.yml) workflow
continues to gate versioned public images on independent, signed acceptance.
Do not promote `main` to the stable `latest` tag or treat a green CI run as
production certification. A deployment still needs TLS, secret handling,
database/object-store resilience, image vulnerability review, supply-chain
attestations, backups and documented disaster-recovery evidence as specified
in [release gates](release-gates.md).

Superseded runs that exit before completing full delivery verification do not
try to upload nonexistent evidence. The uploader is gated by a proof-completed
step output, written only after the byte verifier succeeds. A subsequent
mutable-tag failure can still preserve that completed proof; missing expected
evidence remains an error. This does not weaken either CI or promotion gates.

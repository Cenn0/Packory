# Automatic main-branch container images

Packory publishes **development** container images to GitHub Container Registry
(GHCR) after the complete `CI` GitHub Actions workflow succeeds for a push to
`main`. A merged pull request produces such a push. A failed/canceled CI run
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
private GHCR package. The workflow performs authenticated SHA-tag read-back
with an OCI revision-label check, records full manifest digests, and separately
reports whether anonymous **manifest access** succeeded. An anonymous
manifest read is not a complete anonymous image pull, does not verify layer
downloads, and is not production qualification.

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

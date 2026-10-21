# E0 acceptance — private hosted package foundation

**Checkpoint: 21 October 2026. Status: implemented; final candidate acceptance
pending.** The eight hosted package adapters and their regression/native
harnesses are present. Versioned local native results are recorded below.
The complete current-head CI, PostgreSQL/S3, fault, migration and restore gates
must finish before this checkpoint is marked accepted.

## Acceptance source and scope

The source is the [E0 row in Roadmap V4](roadmap-v4.md#milestones): shared feed
and control-plane isolation, typed coordinates, validated storage/digests,
origin policies and a cross-protocol harness. Its acceptance calls for the
same package revision in independently authorized feeds, native failures on
401/403/404, and recovery/restore under faults.

This implementation applies those requirements to the delivered **hosted
package** adapters: NuGet, npm, Cargo, PyPI, OCI, Maven, Go and Raw. Raw's native
transport is HTTP; it is not a package-manager implementation. The hosted
origin policy is explicit: private requests may use only their own feed's
committed objects. They never fall back to global coordinates, another feed
or any upstream. Native client fixtures also use only the intended feed and
separate empty caches.

Legacy global package roots and keys are retained. The specific compatibility
boundary is OCI: `/v2/feeds/{id}/{repository}/` reserves the legacy repository
prefix `feeds/`. No migration silently moves global packages into private
feeds. See [feed lifecycle and protocol contracts](feeds.md).

## Criteria and implementation evidence

| Criterion | Required observable behavior | Code and tests |
| --- | --- | --- |
| E0-1 Control plane | Eight hosted kinds can be explicitly activated; proxy/Skill drafts remain disabled; lifecycle actions require admin, audit transitions and retain referenced bytes on deactivation | [FeedDefinition](../src/Packory/FeedDefinition.cs), [FeedEndpoints](../src/Packory/FeedEndpoints.cs), [ApiTests](../tests/Packory.Tests/ApiTests.cs), [FeedRawTests](../tests/Packory.Tests/FeedRawTests.cs) |
| E0-2 Exact authorization | Anonymous/invalid credentials fail 401; valid foreign or insufficient feed grants fail 403; authorized missing/deactivated content fails 404; reads and writes use the same exact feed boundary, including metadata and sessions | [Access](../src/Packory/Access.cs), adapter test files below, native negative scenarios; global catalog/MCP scope-denial tests in `ApiTests` |
| E0-3 Independent native identity | Two feeds serve different bytes under the same native coordinate without cache contamination, lookup collision or global catalog exposure | Every adapter's two-feed test and native harness below; [FeedAssetEntry](../src/Packory/FeedAssetEntry.cs) |
| E0-4 Immutable visibility and integrity | Native identity, declared digests, sizes and archive safety are checked; conflicts cannot replace bytes; missing/tampered committed content fails closed | [FeedPackageStore](../src/Packory/FeedPackageStore.cs), [FeedGoBundle](../src/Packory/FeedGoBundle.cs), adapter validation/tamper/concurrency tests; OCI tags are mutable references to immutable digests |
| E0-5 Origin policy | Private metadata, files and manifests never consult global/upstream data; generated URLs use the configured origin; clients have no test fallback source | Adapter tests for absent private/global collisions, fixed-origin metadata and foreign dependency references; isolated configs in each smoke |
| E0-6 Interrupted publication and recovery | Faults before/after object and metadata boundaries retain evidence; no guessed serving row is invented; pending/missing objects remain distinguishable; restart preserves committed private identities | [FeedProviderRecoveryTests](../tests/Packory.Tests/FeedProviderRecoveryTests.cs), [ArtifactRecovery](../src/Packory/ArtifactRecovery.cs), OCI session/restart tests and native restart scenarios |
| E0-7 Migration and restore | V10 preserves prior private rows/keys and foreign keys; failed migrations retain the prior marker; snapshots include private assets/OCI tags and isolated restore checks metadata and bytes | [SchemaVersionTests](../tests/Packory.Tests/SchemaVersionTests.cs), [SchemaVersionGuard](../src/Packory/SchemaVersionGuard.cs), [snapshot tool](../scripts/pg_s3_snapshot.py), [offline backup tests](../tests/test_offline_backup.py), [PG/S3 restore tests](../tests/test_pg_s3_restore_e2e.py) |
| E0-8 Provider and reproducibility | SQLite/filesystem and explicitly configured PostgreSQL/S3-emulator tests execute on the current candidate; native versions and operation results are recorded; required CI passes on the reviewed head | [CI](../.github/workflows/ci.yml), [PostgreSQL workflow](../.github/workflows/postgres.yml), [S3 workflow](../.github/workflows/s3.yml), commands and gates below |

Implemented tests are evidence of the intended contract. Their presence is not
itself a passing result: the current candidate gates below remain pending.
Provider-specific tests that skip because a service variable is absent do not
satisfy E0-8.

## Adapter files, native operations and limits

| Adapter | Implementation and regression tests | Native harness | Deliberate profile boundary |
| --- | --- | --- | --- |
| NuGet | [FeedNugetEndpoints](../src/Packory/FeedNugetEndpoints.cs), private cases in [ApiTests](../tests/Packory.Tests/ApiTests.cs) | [smoke-feed-nuget.sh](../scripts/smoke-feed-nuget.sh): pack/push, source-cleared restore/build in two feeds | Private flat-container/push, without registration/search/symbols |
| npm | [FeedNpmEndpoints](../src/Packory/FeedNpmEndpoints.cs), [FeedNpmTests](../tests/Packory.Tests/FeedNpmTests.cs) | [smoke-feed-npm.sh](../scripts/smoke-feed-npm.sh): publish, transitive install, restart and fresh-cache ci | Tags are supplied during publish; no separate mutable tag API, unpublish, login or provenance attachment |
| Cargo | [FeedCargoEndpoints](../src/Packory/FeedCargoEndpoints.cs), [FeedCargoTests](../tests/Packory.Tests/FeedCargoTests.cs) | [smoke-feed-ecosystems.sh](../scripts/smoke-feed-ecosystems.sh): native publish, sparse registry download and run before/after restart | No private yank/unyank or crates.io proxy; foreign dependency registries rejected |
| PyPI | [FeedPyPiEndpoints](../src/Packory/FeedPyPiEndpoints.cs), [FeedPyPiTests](../tests/Packory.Tests/FeedPyPiTests.cs) | [smoke-feed-ecosystems.sh](../scripts/smoke-feed-ecosystems.sh): Twine upload and transitive pip wheel install before/after restart | Bounded canonical PEP 440 without epochs/aliases; no direct-reference dependency/extra-index fallback, metadata sidecars or source-build qualification |
| OCI | [FeedOciEndpoints](../src/Packory/FeedOciEndpoints.cs), [FeedOciTests](../tests/Packory.Tests/FeedOciTests.cs) | [smoke-feed-oci.sh](../scripts/smoke-feed-oci.sh): pinned Helm push/pull/template and exact bytes after restart | `/v2/feeds/{id}/`; SHA-256 immutable digests and mutable tags; no private Docker/ORAS evidence, Bearer issuer, blob/manifest deletion or multi-node handoff |
| Maven | [FeedMavenEndpoints](../src/Packory/FeedMavenEndpoints.cs), [FeedMavenTests](../tests/Packory.Tests/FeedMavenTests.cs) | [smoke-feed-maven.sh](../scripts/smoke-feed-maven.sh): independent Maven 3 dependency resolution | Immutable release files/checksum acknowledgements; no native deploy, mutable metadata PUT, SNAPSHOT or full Gradle variants |
| Go | [FeedGoEndpoints](../src/Packory/FeedGoEndpoints.cs), [FeedGoTests](../tests/Packory.Tests/FeedGoTests.cs) | [smoke-feed-go.sh](../scripts/smoke-feed-go.sh): native download/build over verified loopback TLS, separate caches and native HTTP failures | Hosted triplet import; no public sumdb trust, mirror or universal archive compatibility |
| Raw | [FeedRawEndpoints](../src/Packory/FeedRawEndpoints.cs), [FeedRawTests](../tests/Packory.Tests/FeedRawTests.cs) | [smoke-feed-raw.sh](../scripts/smoke-feed-raw.sh): real HTTP upload/read with independent bytes and credentials | Generic HTTP bytes, metadata and ranges; no invented package-manager protocol |

### Storage identity

Schema V10 covers `feed_assets` and `feed_oci_tags`; existing NuGet private
SHA-512 rows remain in `feed_nuget_versions`. npm/Cargo/PyPI share a bundle
format binding feed, kind, canonical path, native name/version, metadata,
payload size and SHA-256. The ledger verifies the complete immutable object's
digest/length before envelope and payload checks. Go binds its three native
resources in one immutable triplet. OCI content stays digest-addressed within
feed/repository boundaries, while tag pointers may change transactionally.

The write sequence is durable intent, immutable object, transactional scoped
metadata/audit, completed intent. The filesystem/PostgreSQL fault tests
exercise interrupted boundaries; the PostgreSQL/S3 case exercises cold
materialization and remote tampering with a warm cache. These are bounded
recovery tests, without a distributed database/object-store transaction.
The offline snapshot inventory includes private ledgers and OCI tag pointers.

## Recorded local native results

The first table records **21 October 2026** runs on Linux x86-64 with
SQLite/filesystem and .NET SDK **10.0.402**. A separately executed
PostgreSQL/S3-emulator parity run follows. The complete final candidate gates
remain pending. Exact observed versions are recorded rather than inferred
from an unpinned runner image.

| Client | Observed version | Passed local private-feed scenario |
| --- | --- | --- |
| npm / Node.js | npm **11.9.0**, Node.js **24.19.0** | Two scoped packages with a transitive dependency in each feed; same names/versions with different exports; native publish/install; immutable republish rejection; fresh-cache ci after process restart; native 401/403/deactivated 404; lockfile URLs confined to the correct feed |
| Go | **1.24.13**, official archive SHA-256 checked | Same module/version yields different binaries from two feeds; independent module/build caches; native 401, foreign-token 403, missing module/version/feed 404 and deactivated 404; HTTPS netrc authority includes the test port |
| Cargo / rustc | **1.99.1 / 1.99.1** | Native publish and run from both feeds, independent Cargo homes/target directories, fresh-cache restart read and native credential/deactivation negatives |
| Python / pip / Twine | **3.12 / 26.2.1 / 6.2.0** | Two-feed Twine upload and pip transitive pure-Python wheel installation; sole index, no-cache clients, restart and native credential/deactivation negatives |
| Helm | **3.22.0**, official archive SHA-256 checked | Same chart name/version with different bytes in two feeds; push/pull/template, exact archive comparison after process restart and credential/deactivation negatives |

The pinned Helm script verifies its downloaded archive checksum. Go 1.24.13
was also verified against the official archive checksum for this local run;
the Go script uses the installed `go` command, so a CI run must record its
actual version. Cargo and npm likewise use installed clients. The observations
above do not endorse untested versions or imply a maintained multi-version
matrix. Private Docker/ORAS and native PyPI source-build scenarios have no
result in this table.

NuGet, Maven and Raw have existing native harnesses; their rerun on the complete
current candidate belongs to the pending gate below. Earlier green merged
workflows are not substituted for that rerun.

### Recorded local PostgreSQL/S3 native parity

[smoke-feed-providers.py](../scripts/smoke-feed-providers.py) also completed
successfully with actual **PostgreSQL 16** and local **Moto S3** on Ubuntu
24.04.3 LTS x86-64. It runs the existing native scripts sequentially in
separately generated PostgreSQL schemas and S3 buckets, then independently
reads every remote object and checks its SHA-256 and length against the
PostgreSQL ledger. It also verifies exact remote key sets, no global package
aliases and no unfinished publication intents.

| Native profile | Versions in the run | Verified private revisions |
| --- | --- | --- |
| npm publish/install/ci | npm 11.9.0 | 4 npm bundles |
| Cargo publish/run and Twine/pip wheel flow | Cargo 1.99.1, Twine 6.2.0, pip 26.2.1 with Python 3.12 | 2 Cargo and 4 PyPI bundles |
| Helm OCI push/pull/template | Helm 3.22.0 | 6 OCI blob/manifest assets |

These runs include each script's process restart, fresh client caches and
authorization/deactivation negatives. They establish provider parity for
those native profiles, with an S3 emulator. They do not replace the separate
fault-injection, migration/restore or real-S3-provider gates.

## Reproduce the checks

Run from the repository root. Native scripts need the matching tools: .NET 10,
Node/npm, Cargo/rustc, Python/pip/Twine, Maven, Go, curl and OpenSSL; the OCI
script downloads and checksum-checks pinned Helm unless an explicitly selected
matching binary is supplied. Each script creates disposable synthetic packages,
credentials/configs and independent caches, then removes them. Private Go uses
a temporary verified loopback TLS proxy because native GOAUTH sends credentials
over HTTPS.

```bash
dotnet restore Packory.slnx
dotnet build Packory.slnx -c Release --no-restore -warnaserror
dotnet test Packory.slnx -c Release --no-build
node --check src/Packory/wwwroot/app.js

bash scripts/smoke-feed-nuget.sh
bash scripts/smoke-feed-npm.sh
bash scripts/smoke-feed-ecosystems.sh
bash scripts/smoke-feed-oci.sh
bash scripts/smoke-feed-maven.sh
bash scripts/smoke-feed-go.sh
bash scripts/smoke-feed-raw.sh
```

PostgreSQL facts require `PACKORY_TEST_POSTGRES`. Combined S3 facts also require
`PACKORY_TEST_S3`. Use the disposable service profiles in the linked PostgreSQL
and S3 workflows. Once those services and the current Release build exist:

```bash
test -n "${PACKORY_TEST_POSTGRES:-}"
dotnet test tests/Packory.Tests/Packory.Tests.csproj -c Release --no-build

test -n "${PACKORY_TEST_S3:-}"
dotnet test tests/Packory.Tests/Packory.Tests.csproj -c Release --no-build \
  --filter FullyQualifiedName~FeedProviderRecoveryTests
dotnet test tests/Packory.Tests/Packory.Tests.csproj -c Release --no-build \
  --filter FullyQualifiedName~PostgresS3RecoveryTests
python3 scripts/smoke-feed-providers.py
```

The native provider wrapper additionally needs `boto3`, `psycopg[binary]` and
the npm/Cargo/Python/Helm native tooling. It accepts only explicit loopback
test services and synthetic credentials, creates its own schemas/buckets,
and removes those generated fixtures afterward. The S3 workflow supplies the
matching disposable PostgreSQL profile.

Offline/snapshot logic tests run locally without establishing real-provider
coverage. The final two commands require the configured disposable
PostgreSQL/Moto services and compatible PostgreSQL backup tools from the S3
workflow:

```bash
python3 -m unittest discover -s tests -p test_offline_backup.py -v
python3 -m unittest discover -s tests -p test_pg_s3_snapshot.py -v
python3 -m unittest discover -s tests -p test_pg_s3_snapshot_safety.py -v
python3 -m unittest discover -s tests -p test_pg_s3_snapshot_s3.py -v
python3 -m unittest discover -s tests -p test_pg_s3_restore_e2e.py -v
```

## Pending candidate gates

- [ ] Full Release build with warnings as errors and complete test suite after all E0 changes.
- [ ] All eight private adapter harnesses on the same candidate, including existing NuGet/Maven/Raw flows and native failure checks.
- [ ] Actual PostgreSQL tests, including V9→V10 preservation/rollback constraints and private feed recovery.
- [ ] PostgreSQL + S3 emulator cold reads, remote corruption checks, intent recovery and restored feed/tag references.
- [ ] Interruption/restart/missing-object tests and offline snapshot/isolated restore tests pass without treating skipped provider facts as evidence.
- [ ] Documentation, public-data/privacy checks and all required PR checks pass on the exact reviewed head.

When a gate completes, record the immutable commit/run and the actual
client/provider outcome here or in the release evidence artifact. A successful
local native run closes only its corresponding scenario.

## Separate milestones that remain open

| Work outside this hosted acceptance | Roadmap owner |
| --- | --- |
| Explicit upstream proxy/group feeds, conflict priority, general namespace/policy/retention/quota model, CAS reference counting and declarative full CRUD/import/export | V3 MS2; origin/network acceptance for each later proxy adapter |
| Browser BFF, login/account lifecycle and UI permission administration | V3 MS4 |
| Private Skills delivery and feed-aware MCP catalog/resource/installation authorization | V3 MS5 and MCP-P5 |
| Full Maven deploy/Gradle/SNAPSHOT/proxy and atomic multi-file release semantics | V4 E2 |
| Go upstream mirror, public checksum-database trust and broader archive/client compatibility | V4 E3 |
| Broader native client/OS matrix, real S3-provider restore, measured recovery objectives and release operations | V3 MS1/MS6 and V4 E11 |
| Multiple active writers, fencing and cross-node upload/session failover | V3 MS7 |

Completing the E0 hosted acceptance does not complete these milestones, MS2 as
a whole or the wider Packory project. The [support matrix](support-matrix.md)
and [release gates](release-gates.md) keep those conclusions separate.

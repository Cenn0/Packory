# Offline PostgreSQL/S3 snapshot and isolated restore (experimental)

The operator-only `scripts/pg_s3_snapshot.py` is a **first implementation** of a coordinated, hash-verified offline metadata/object backup. It requires all Packory writes stopped and obtains the same PostgreSQL session advisory lock as the Packory writer. While holding that lock, it runs `pg_dump`, snapshots **all S3 object versions** under the selected Packory prefix (including `recovery/quarantine`), records delete markers and source-table row-count watermarks, verifies SHA-256 of every saved file and atomically publishes the snapshot directory.

A restore only targets a **different, empty PostgreSQL database** and a **different versioned S3 bucket with an empty Packory key prefix**. It verifies all snapshot bytes, copies the **currently live** object generations and verifies their SHA-256, then runs transactional `pg_restore` and verifies database row counts. On any failure the operator must discard/isolate the partial target before retrying. It never overwrites source assets or attempts production rollback automatically.

## Requirements

- Packory writers stopped; independent operational confirmation of an offline window.
- PostgreSQL client binaries **matching/compatible with server major version**, `pg_dump`, `pg_restore`; Python 3, `boto3`, `psycopg[binary]`.
- Existing `PGHOST`, `PGPORT`, `PGUSER`, `PGDATABASE`, `PGPASSWORD` environment variables loaded **outside Git and shell history**. The exact source database and restored target database must be separate.
- Least-privileged PostgreSQL account able to obtain the Packory advisory lock and read all relevant tables; S3 ListBucketVersions/GetObjectVersion and target PutObject rights.
- S3 source and target bucket **versioning enabled**, unique non-empty `--prefix`. Default endpoint uses AWS HTTPS; explicit third-party S3 origin must be HTTPS. HTTP loopback permitted only for local test emulator.
- Enough temporary disk to hold the PostgreSQL dump **and every S3 object version**, including historical/quarantine bytes; default object capacity limit is 100 GiB.

## Example (private, isolated operator workstation)

```sh
python3 -m pip install 'boto3>=1.35,<2' 'psycopg[binary]>=3,<4'
# Set PGHOST, PGPORT, PGDATABASE, PGUSER and PGPASSWORD using a secret manager.
# Use short-lived workload credentials / IAM role for S3.
python3 scripts/pg_s3_snapshot.py backup --snapshot /secure/offline-copy-001 \
  --bucket disposable-source-bucket --prefix 'packory/' --writes-stopped-confirmed

python3 scripts/pg_s3_snapshot.py verify --snapshot /secure/offline-copy-001

# Change PGDATABASE and S3 credentials to an entirely separate EMPTY target.
python3 scripts/pg_s3_snapshot.py restore --snapshot /secure/offline-copy-001 \
  --target-bucket disposable-restore-bucket \
  --writes-stopped-confirmed --isolated-restore-confirmed
```

**Never commit the resulting snapshot/report, database names, bucket names, logs, object keys, personal data, cloud credentials or operator paths to the public repository.**

## Guarantees and boundaries

**Works in code:** a versioned offline snapshot format, per-object SHA-256 verification, explicit freeze/lease precondition, isolated current-state restore, and metadata row-count comparison.

**Tests:** Synthetic S3 and snapshot-corruption tests in `tests/test_pg_s3_snapshot.py` (CI). No independent real PostgreSQL/S3 combined restore evidence yet.

**Not officially Packory-supported/production-recommended:** full coordinated restoration on the actual provider, independently measured RPO/RTO, large dataset performance, history/retention/legal hold reconstruction, migration of storage credentials, key rotation, DB sequences post-restore or native client integrity verification across all six adapters. `pg_restore --single-transaction` cannot reverse already written S3 objects after failure.

**Critical historical-version limitation:** backup archives every S3 version, but automated restore materializes only *current live generations*. Original S3-assigned VersionIds, delete markers, object retention, legal holds, lifecycle policy and timestamps are **not** recreated. An operator must retain and protect historical backup evidence separately and explicitly review these semantics before any production restore. This does **not** close M1.1/M1.2 real-provider or MS6 release gates.

For the production release checklist see [release gates](release-gates.md) and the [S3 provider probe](s3-provider-conformance.md).

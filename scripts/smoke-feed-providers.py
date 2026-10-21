#!/usr/bin/env python3
"""Run native private-feed acceptance against disposable PostgreSQL and S3.

Required: PACKORY_TEST_POSTGRES (Npgsql syntax) and PACKORY_TEST_S3. Both
endpoints must use literal loopback addresses and explicit ports. PostgreSQL
uses only the synthetic packory/packory_test users and an empty or "test"
password; S3 always uses test/test. No production configuration is inherited.
Install boto3 and psycopg[binary], build Release, and provide the native tools
required by smoke-feed-npm.sh, smoke-feed-ecosystems.sh and smoke-feed-oci.sh.

The scripts run sequentially: Packory's writer lease covers the entire
PostgreSQL database, even when each script uses its own isolated schema.
This is native-client parity with an S3 emulator, not real-provider acceptance.
"""

from __future__ import annotations

import argparse
from collections import Counter
from contextlib import closing
import hashlib
import ipaddress
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
from urllib.parse import urlsplit
import uuid


ROOT = Path(__file__).resolve().parents[1]
PROFILES = (
    ("npm", {"npm": 4}),
    ("ecosystems", {"cargo": 2, "pypi": 4}),
    ("oci", {"oci": 6}),
)


def loopback_literal(host: str) -> str:
    try:
        address = ipaddress.ip_address(host)
    except ValueError as error:
        raise ValueError("Test services require literal loopback addresses, without DNS names.") from error
    if not address.is_loopback or "%" in host:
        raise ValueError("Only loopback test services are allowed.")
    return str(address)


def postgres_parameters(raw: str) -> dict[str, str | int]:
    allowed = {"host", "port", "database", "username", "password", "pooling"}
    fields: dict[str, str] = {}
    for item in raw.split(";"):
        if not item.strip():
            continue
        key, separator, value = item.partition("=")
        key, value = key.strip().lower(), value.strip()
        if not separator or key not in allowed or key in fields:
            raise ValueError("PACKORY_TEST_POSTGRES has duplicate or unsupported connection fields.")
        fields[key] = value
    if not {"host", "port", "database", "username"}.issubset(fields):
        raise ValueError("PACKORY_TEST_POSTGRES must explicitly set Host, Port, Database and Username.")
    host = loopback_literal(fields["host"])
    port = int(fields["port"])
    if not 1 <= port <= 65535 or not re.fullmatch(r"[A-Za-z_][A-Za-z_0-9]{0,62}", fields["database"]):
        raise ValueError("Invalid disposable PostgreSQL port or database name.")
    if fields["username"] not in {"packory", "packory_test"} or fields.get("password", "") not in {"", "test"}:
        raise ValueError("Only synthetic packory/packory_test PostgreSQL credentials are accepted.")
    if fields.get("pooling", "false").lower() not in {"true", "false"}:
        raise ValueError("Invalid PostgreSQL pooling option.")
    return {"host": host, "hostaddr": host, "port": port, "dbname": fields["database"],
            "user": fields["username"], "password": "test",
            "connect_timeout": 5, "sslmode": "disable"}


def s3_endpoint(raw: str) -> str:
    endpoint = urlsplit(raw)
    if (endpoint.scheme not in {"http", "https"} or not endpoint.hostname or
            endpoint.port is None or not 1 <= endpoint.port <= 65535 or
            endpoint.username is not None or endpoint.password is not None or
            endpoint.path not in {"", "/"} or endpoint.query or endpoint.fragment):
        raise ValueError("PACKORY_TEST_S3 must be an explicit loopback endpoint and port without credentials or path.")
    loopback_literal(endpoint.hostname)
    return raw.rstrip("/")


def native_environment(pg: dict[str, str | int], endpoint: str, schema: str, bucket: str) -> dict[str, str]:
    # In particular, never let inherited PACKORY_/Packory__ production settings
    # or libpq/AWS defaults select another database, object store or credentials.
    environment = {key: value for key, value in os.environ.items()
                   if not key.upper().startswith(("PACKORY_", "PACKORY:", "AWS_", "PG", "ASPNETCORE_", "KESTREL__"))
                   and key.upper() not in {"URLS", "HTTP_PORTS", "HTTPS_PORTS", "DOTNET_URLS"}}
    environment.update({
        "PACKORY_METADATA_PROVIDER": "postgresql", "PACKORY_BLOB_PROVIDER": "s3",
        "PACKORY_POSTGRES_CONNECTION_STRING": (
            f"Host={pg['host']};Port={pg['port']};Database={pg['dbname']};Username={pg['user']};"
            f"Password={pg['password']};SearchPath={schema};Pooling=false;SSL Mode=Disable;Timeout=5"),
        "PACKORY_S3_ENDPOINT": endpoint, "PACKORY_S3_BUCKET": bucket,
        "PACKORY_S3_KEY_PREFIX": "native-private", "PACKORY_S3_REGION": "us-east-1",
        "PACKORY_S3_ACCESS_KEY": "test", "PACKORY_S3_SECRET_KEY": "test",
        "PACKORY_S3_ALLOW_LOOPBACK_HTTP": "true", "PACKORY_INSTANCE_ROLE": "writer",
        "PACKORY_RECOVERY_MAINTENANCE": "false", "PACKORY_STRICT_DEPLOYMENT": "false",
        "PACKORY_MCP_ENABLED": "false", "PACKORY_VALIDATION_ENABLED": "false",
        "PACKORY_NPM_UPSTREAM_URL": "https://upstream.invalid/npm/",
        "PACKORY_NUGET_UPSTREAM_URL": "https://upstream.invalid/nuget/",
        "PACKORY_NUGET_REGISTRATION_URL": "https://upstream.invalid/registration/",
        "DOTNET_ENVIRONMENT": "Production", "ASPNETCORE_ENVIRONMENT": "Production",
    })
    for key in ("NO_PROXY", "no_proxy"):
        environment[key] = environment.get(key, "") + ",127.0.0.1,localhost,::1"
    # A caller may select an already verified Helm binary to avoid a download.
    if os.environ.get("PACKORY_HELM_BIN"):
        helm = Path(os.environ["PACKORY_HELM_BIN"])
        if not helm.is_absolute() or not helm.is_file() or not os.access(helm, os.X_OK):
            raise ValueError("PACKORY_HELM_BIN must be an executable absolute file path.")
        environment["PACKORY_HELM_BIN"] = str(helm)
    return environment


def run_native(script: Path, environment: dict[str, str]) -> None:
    process = subprocess.Popen(["bash", str(script)], cwd=ROOT, env=environment, start_new_session=True)
    try:
        if process.wait(timeout=600) != 0:
            raise RuntimeError(f"Native provider smoke failed: {script.name}")
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()


def verify_persistence(connection, sql, s3, schema: str, bucket: str, expected: dict[str, int]) -> None:
    # These reads independently prove that the native processes used the
    # configured providers, rather than silently succeeding against SQLite or
    # a filesystem cache. Only this invocation's schema/bucket is inspected.
    rows = connection.execute(sql.SQL(
        "SELECT feed_id,kind,path,sha256,size FROM {}.feed_assets ORDER BY feed_id,kind,path"
    ).format(sql.Identifier(schema))).fetchall()
    if Counter(row[1] for row in rows) != expected:
        raise RuntimeError("Native provider catalog does not contain the expected private fixture revisions.")
    for table in ("packages", "publication_intents"):
        count = connection.execute(sql.SQL("SELECT COUNT(*) FROM {}.{}").format(
            sql.Identifier(schema), sql.Identifier(table))).fetchone()[0]
        if count:
            raise RuntimeError("Native provider run left global packages or incomplete publication intents.")
    remote_keys = set()
    for page in s3.get_paginator("list_objects_v2").paginate(Bucket=bucket):
        remote_keys.update(item["Key"] for item in page.get("Contents", []))
    ledger_keys = set()
    for feed, kind, path, expected_digest, expected_size in rows:
        if kind == "oci":
            suffix = ".json" if path.startswith("manifest/") else ".blob"
            key = f"native-private/feeds/{feed}/oci/{path}{suffix}"
        else:
            path_digest = hashlib.sha256(path.encode()).hexdigest()
            key = f"native-private/feeds/{feed}/{kind}/{path_digest}.bundle"
        ledger_keys.add(key)
        response = s3.get_object(Bucket=bucket, Key=key)
        digest, size = hashlib.sha256(), 0
        with closing(response["Body"]) as body:
            for block in body.iter_chunks(64 * 1024):
                size += len(block)
                if size > expected_size:
                    raise RuntimeError("Remote native fixture exceeds its immutable ledger size.")
                digest.update(block)
        if size != expected_size or digest.hexdigest() != expected_digest:
            raise RuntimeError("Remote native fixture bytes disagree with the immutable ledger.")
    if remote_keys != ledger_keys:
        raise RuntimeError("Native provider bucket contains unexpected or missing artifact objects.")
    print(f"Verified PostgreSQL + S3 persistence: {len(rows)} private revisions, matching remote SHA-256, no global aliases.", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--skip-if-unconfigured", action="store_true",
                        help="Explicitly report SKIP when either disposable service variable is absent.")
    args = parser.parse_args()
    pg_raw, s3_raw = os.environ.get("PACKORY_TEST_POSTGRES"), os.environ.get("PACKORY_TEST_S3")
    if not pg_raw or not s3_raw:
        if args.skip_if_unconfigured:
            print("SKIP: PostgreSQL/S3 native parity was not tested; disposable service variables are absent.")
            return 0
        raise ValueError("Set both PACKORY_TEST_POSTGRES and PACKORY_TEST_S3 to explicit disposable local services.")
    pg, endpoint = postgres_parameters(pg_raw), s3_endpoint(s3_raw)
    for tool in ("bash", "dotnet", "npm", "node", "cargo", "rustc", "python3", "curl", "openssl", "tar", "sha256sum", "cmp"):
        if shutil.which(tool) is None:
            raise ValueError(f"Missing required native tool: {tool}")
    if not (ROOT / "src/Packory/bin/Release/net10.0/Packory.dll").is_file():
        raise ValueError("Build Packory Release before running native provider acceptance.")

    import boto3
    from botocore.config import Config
    from botocore.exceptions import ClientError
    import psycopg
    from psycopg import sql

    # libpq must not load a service file or host address from the parent shell.
    for key in list(os.environ):
        if key.upper().startswith("PG"):
            del os.environ[key]
    s3 = boto3.client("s3", endpoint_url=endpoint, region_name="us-east-1",
                      aws_access_key_id="test", aws_secret_access_key="test",
                      config=Config(signature_version="s3v4", s3={"addressing_style": "path"},
                                    connect_timeout=5, read_timeout=20, retries={"max_attempts": 1}, proxies={}))
    with psycopg.connect(**pg, autocommit=True) as connection:
        for profile, expected in PROFILES:
            nonce = uuid.uuid4().hex
            schema, bucket = "packory_e0_native_" + nonce, "packory-e0-native-" + nonce
            environment = native_environment(pg, endpoint, schema, bucket)
            created_schema = created_bucket = False
            try:
                connection.execute(sql.SQL("CREATE SCHEMA {}").format(sql.Identifier(schema)))
                created_schema = True
                try:
                    s3.head_bucket(Bucket=bucket)
                except ClientError as error:
                    if error.response["ResponseMetadata"]["HTTPStatusCode"] != 404:
                        raise
                else:
                    raise RuntimeError("Generated test bucket unexpectedly exists; refusing to reuse it.")
                s3.create_bucket(Bucket=bucket)
                created_bucket = True
                print(f"Running private {profile} native acceptance with PostgreSQL + local S3.", flush=True)
                run_native(ROOT / "scripts" / f"smoke-feed-{profile}.sh", environment)
                verify_persistence(connection, sql, s3, schema, bucket, expected)
            finally:
                # Never delete resources supplied by callers or pre-existing
                # resources: both names were generated here and created above.
                try:
                    if created_bucket:
                        for page in s3.get_paginator("list_objects_v2").paginate(Bucket=bucket):
                            for item in page.get("Contents", []):
                                s3.delete_object(Bucket=bucket, Key=item["Key"])
                        s3.delete_bucket(Bucket=bucket)
                finally:
                    if created_schema:
                        connection.execute(sql.SQL("DROP SCHEMA {} CASCADE").format(sql.Identifier(schema)))
    print("E0 native PostgreSQL/S3-emulator parity passed for npm, Cargo, Twine/pip and Helm OCI.", flush=True)
    return 0


if __name__ == "__main__":
    def interrupted(_signum, _frame):
        raise RuntimeError("Native provider acceptance interrupted.")

    signal.signal(signal.SIGTERM, interrupted)
    try:
        raise SystemExit(main())
    except (Exception, KeyboardInterrupt) as error:
        print(f"FAIL: {type(error).__name__}: {error}", file=sys.stderr)
        raise SystemExit(1) from None

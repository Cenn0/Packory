#!/usr/bin/env python3
"""Non-certifying, isolated conditional-write/delete probe for an intended real S3 provider.

Creates uniquely named disposable objects in a bucket explicitly selected by
the operator. NEVER reuses Packory application keys or erases other objects.
No credentials or endpoint URLs are included in stdout/report.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import secrets
from urllib.parse import urlsplit

PROBE_ROOT = "packory-provider-conformance/"


class ProbeFailure(RuntimeError):
    pass


def status_of(error):
    response = getattr(error, "response", None)
    if not isinstance(response, dict):
        return None
    meta = response.get("ResponseMetadata", {})
    value = meta.get("HTTPStatusCode")
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def expect_precondition(operation, label):
    """Only HTTP 409/412 counts as rejecting a stale/mismatched condition."""
    try:
        operation()
    except Exception as error:
        code = status_of(error)
        if code in (409, 412):
            return code
        # Error details can embed real provider URLs. Do not reflect them.
        raise ProbeFailure(f"{label}: unexpected error class {type(error).__name__}, HTTP {code}") from None
    raise ProbeFailure(f"{label}: provider silently accepted a prohibited write/delete")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def read_bytes(client, bucket, key):
    response = client.get_object(Bucket=bucket, Key=key)
    with response["Body"] as stream:
        return stream.read()


def _put(client, bucket, key, data, exclusive=True):
    kwargs = {"Bucket": bucket, "Key": key, "Body": data,
              "ContentType": "application/octet-stream"}
    if exclusive:
        kwargs["IfNoneMatch"] = "*"
    return client.put_object(**kwargs)


def _head(client, bucket, key):
    return client.head_object(Bucket=bucket, Key=key)["ETag"]


def _delete(client, bucket, key, etag):
    client.delete_object(Bucket=bucket, Key=key, IfMatch=etag)


def run_probe(client, bucket, prefix, concurrency=8):
    """Check bytes, CAS collision and ETag-conditional delete (no broad delete)."""
    if not isinstance(bucket, str) or not bucket.strip():
        raise ValueError("Test bucket is required")
    if not prefix.startswith(PROBE_ROOT) or len(prefix) <= len(PROBE_ROOT):
        raise ValueError("Probe must use a unique reserved namespace")
    if concurrency < 2 or concurrency > 32:
        raise ValueError("Concurrency must be between 2 and 32")

    # This is a snapshot of provider bucket policy, not proof that retention,
    # lifecycle and quarantine are correctly configured.
    try:
        versioning = client.get_bucket_versioning(Bucket=bucket).get("Status", "Disabled")
    except Exception:
        versioning = "Unverified"

    key = prefix + "/immutable"
    expected = secrets.token_bytes(48)
    _put(client, bucket, key, expected)
    expect_precondition(lambda: _put(client, bucket, key, b"should-never-overwrite"),
                        "If-None-Match duplicate")
    if read_bytes(client, bucket, key) != expected:
        raise ProbeFailure("Immutable bytes changed after denied conditional write")

    # These keys belong only to the probe, and neither bucket nor prefix existed
    # before this run. AWS SDK clients can issue these independent calls in threads.
    racing = prefix + "/parallel"
    payloads = [secrets.token_bytes(64) for _ in range(concurrency)]

    def race(payload):
        try:
            _put(client, bucket, racing, payload)
            return ("success", sha(payload))
        except Exception as error:
            if status_of(error) in (409, 412):
                return ("rejected", None)
            raise ProbeFailure(f"Concurrent put failed: {type(error).__name__}") from None

    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        outcomes = list(pool.map(race, payloads))
    winners = [checksum for state, checksum in outcomes if state == "success"]
    if len(winners) != 1 or sha(read_bytes(client, bucket, racing)) != winners[0]:
        raise ProbeFailure("Concurrent If-None-Match writes were not serialized safely")

    # Change only our own isolated key, then require that a stale ETag cannot
    # delete the new generation. Never use an unconditional object DELETE.
    mutable = prefix + "/etag-delete"
    before = secrets.token_bytes(32)
    after = secrets.token_bytes(32)
    _put(client, bucket, mutable, before)
    old_tag = _head(client, bucket, mutable)
    _put(client, bucket, mutable, after, exclusive=False)
    new_tag = _head(client, bucket, mutable)
    if old_tag == new_tag:
        raise ProbeFailure("Overwritten content unexpectedly has the same ETag")
    expect_precondition(lambda: _delete(client, bucket, mutable, old_tag),
                        "If-Match stale ETag DELETE")
    if read_bytes(client, bucket, mutable) != after:
        raise ProbeFailure("Stale conditional DELETE modified the latest object")
    _delete(client, bucket, mutable, new_tag)
    try:
        client.head_object(Bucket=bucket, Key=mutable)
    except Exception as error:
        if status_of(error) != 404:
            raise ProbeFailure("Post-delete object HEAD returned unexpected error") from None
    else:
        raise ProbeFailure("ETag-matched conditional DELETE did not remove latest key")

    return {
        "schema": 1,
        "tested_at_utc": datetime.now(timezone.utc).isoformat(),
        "scope": "conditional-put-and-delete-only",
        "versioning_observed": versioning,
        "unique_probe_namespace": prefix,
        "conditional_immutable_put": "pass",
        "parallel_create_only_one_winner": "pass",
        "etag_match_delete": "pass",
        "tested_concurrent_writers": concurrency,
        "left_immutable_evidence_objects": 2,
        "retention_lifecycle_object_lock": "not-tested",
        "network_partition_restore_and_rpo_rto": "not-tested",
        "full_m1_4_provider_certified": False,
    }


def check_endpoint(endpoint):
    if endpoint is None or endpoint == "":
        return None  # boto3 standard AWS endpoints
    parsed = urlsplit(endpoint)
    if (parsed.scheme != "https" or not parsed.hostname or parsed.username or
            parsed.password or parsed.query or parsed.fragment or parsed.path not in ("", "/")):
        raise ValueError("The selected real provider must use a simple HTTPS origin")
    return endpoint


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bucket", default=os.getenv("PACKORY_S3_BUCKET"))
    parser.add_argument("--endpoint", default=os.getenv("PACKORY_S3_ENDPOINT"))
    parser.add_argument("--region", default=os.getenv("PACKORY_S3_REGION", "us-east-1"))
    parser.add_argument("--concurrency", type=int, default=8)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--disposable-test-bucket", action="store_true",
                        help="I verified the bucket is a dedicated disposable test bucket")
    args = parser.parse_args()
    if not args.disposable_test_bucket:
        parser.error("A dedicated disposable test bucket must be explicitly confirmed")
    if not args.bucket:
        parser.error("--bucket or PACKORY_S3_BUCKET is required")
    try:
        endpoint = check_endpoint(args.endpoint)
    except ValueError as error:
        parser.error(str(error))
    # boto3 is intentionally an optional *operator-only* dependency.
    # Prefer IAM role/temporary workload credentials through the AWS default chain.
    import boto3
    client = boto3.client("s3", region_name=args.region, endpoint_url=endpoint)
    unique_prefix = PROBE_ROOT + secrets.token_hex(16)
    report = run_probe(client, args.bucket, unique_prefix, args.concurrency)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))
    print("CONDITIONAL API TESTS PASSED; real-provider lifecycle, restore and retention approval still REQUIRED.")


if __name__ == "__main__":
    main()

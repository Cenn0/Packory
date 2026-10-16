# Conditional API probe for a real S3 provider (MS1.4)

This probe checks **only** conditional immutable PUT, concurrent create-if-absent, and ETag-conditional DELETE with the current S3 API client. It is **not full provider certification**. Testing with Moto proves the test harness, not provider semantics.

## Safe invocation

Use a dedicated disposable test bucket with **versioning enabled**, a separate IAM role, TLS and a retention configuration that permits the expected temporary test objects. The probe creates objects only under a unique randomly generated `packory-provider-conformance/<id>/` prefix. It conditionally deletes exactly **one** object it owns, and intentionally leaves two immutable evidence objects for an operator to inspect and clean up under the approved policy. Never run with a production Packory bucket.

Install the probe dependency outside source control:

```sh
python3 -m pip install 'boto3>=1.35,<2'
export PACKORY_S3_BUCKET='disposable-test-bucket'
export PACKORY_S3_REGION='eu-central-1'
# Optionally set PACKORY_S3_ENDPOINT for a third-party HTTPS S3 implementation.
# Use the standard AWS workload credentials chain; never paste secrets into flags.
python3 scripts/s3_provider_probe.py --disposable-test-bucket --report provider-report.json
```

**Keep `provider-report.json` outside the public repository** until reviewed/sanitized: even non-secret bucket key prefixes may disclose deployment configuration. The script itself does not print credentials or service URL.

The output records observed versioning state. A successful run is **not** proof of correctly configured retention, Object Lock, version preservation, lifecycle exclusions, network partition handling, restore, concurrency across Packory writers, or PostgreSQL metadata consistency. Perform each of those on the intended provider and record independently in [release gates](release-gates.md).

To validate the *harness logic* without credentials:

```sh
python3 -m unittest discover -s tests -p 'test_s3_provider_probe.py' -v
```

False-positive resistance: unit tests emulate a provider that ignores `If-None-Match` and a provider that ignores `If-Match`, and require both to fail. The output is always `full_m1_4_provider_certified: false` pending real-provider review.

"""Synthetic S3 clients test the probe's failure-detection logic, not vendor conformance."""
import importlib.util
import io
from pathlib import Path
import threading
import unittest

spec = importlib.util.spec_from_file_location(
    "s3_provider_probe", Path(__file__).resolve().parents[1] /
    "scripts" / "s3_provider_probe.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class S3Error(Exception):
    def __init__(self, status):
        self.response = {"ResponseMetadata": {"HTTPStatusCode": status}}
        super().__init__("synthetic S3 response")


class DummyBody(io.BytesIO):
    pass


class FakeS3:
    def __init__(self):
        self.lock = threading.Lock()
        self.objects = {}

    def get_bucket_versioning(self, **kwargs):
        return {"Status": "Enabled"}

    def put_object(self, Bucket, Key, Body, IfNoneMatch=None, **kwargs):
        with self.lock:
            if IfNoneMatch == "*" and Key in self.objects:
                raise S3Error(412)
            if hasattr(Body, "read"):
                Body = Body.read()
            # ETag does not need to be MD5, as with encrypted AWS S3 objects.
            self.objects[Key] = bytes(Body)
            return {"ResponseMetadata": {"HTTPStatusCode": 200}}

    def head_object(self, Bucket, Key):
        with self.lock:
            if Key not in self.objects:
                raise S3Error(404)
            return {"ETag": probe.sha(self.objects[Key])}

    def get_object(self, Bucket, Key):
        with self.lock:
            if Key not in self.objects:
                raise S3Error(404)
            return {"Body": DummyBody(self.objects[Key])}

    def delete_object(self, Bucket, Key, IfMatch):
        with self.lock:
            if Key not in self.objects:
                raise S3Error(404)
            if probe.sha(self.objects[Key]) != IfMatch:
                raise S3Error(412)
            del self.objects[Key]


class BrokenCreateOnly(FakeS3):
    def put_object(self, Bucket, Key, Body, IfNoneMatch=None, **kwargs):
        return super().put_object(Bucket, Key, Body, IfNoneMatch=None, **kwargs)


class BrokenDelete(FakeS3):
    def delete_object(self, Bucket, Key, IfMatch):
        with self.lock:
            self.objects.pop(Key, None)


class ProviderProbeTests(unittest.TestCase):
    def test_all_conditional_scenarios(self):
        result = probe.run_probe(FakeS3(), "test-only", probe.PROBE_ROOT + "unit", 8)
        self.assertEqual("pass", result["conditional_immutable_put"])
        self.assertEqual("pass", result["etag_match_delete"])
        self.assertEqual("not-tested", result["retention_lifecycle_object_lock"])
        self.assertFalse(result["full_m1_4_provider_certified"])

    def test_non_exclusive_provider_fails_closed(self):
        with self.assertRaises(probe.ProbeFailure):
            probe.run_probe(BrokenCreateOnly(), "test-only", probe.PROBE_ROOT + "unit")

    def test_provider_ignoring_delete_etag_fails_closed(self):
        with self.assertRaises(probe.ProbeFailure):
            probe.run_probe(BrokenDelete(), "test-only", probe.PROBE_ROOT + "unit")

    def test_guardrails(self):
        with self.assertRaises(ValueError):
            probe.run_probe(FakeS3(), "test-only", "../unsafe")
        with self.assertRaises(ValueError):
            probe.run_probe(FakeS3(), "test-only", probe.PROBE_ROOT + "unit", 1000)
        with self.assertRaises(ValueError):
            probe.check_endpoint("http://s3.example.test")
        with self.assertRaises(ValueError):
            probe.check_endpoint("https://user:pass" + "@" + "s3.example.test")
        self.assertEqual("https://s3.example.test", probe.check_endpoint("https://s3.example.test"))


if __name__ == "__main__":
    unittest.main()

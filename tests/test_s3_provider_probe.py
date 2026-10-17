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



class FakeVersionedS3(FakeS3):
    def __init__(self):
        super().__init__()
        self.history = {}
        self.sequence = 0

    def put_object(self, Bucket, Key, Body, IfNoneMatch=None, **kwargs):
        with self.lock:
            if IfNoneMatch == "*" and Key in self.objects:
                raise S3Error(412)
            if hasattr(Body, "read"):
                Body = Body.read()
            data = bytes(Body)
            self.sequence += 1
            version = f"revision-{self.sequence}"
            self.objects[Key] = data
            self.history.setdefault(Key, []).append((version, data, False))
            return {"VersionId": version}

    def get_object(self, Bucket, Key, VersionId=None):
        with self.lock:
            if VersionId is None:
                if Key not in self.objects:
                    raise S3Error(404)
                return {"Body": DummyBody(self.objects[Key])}
            for identifier, content, marker in self.history.get(Key, ()):
                if identifier == VersionId and not marker:
                    return {"Body": DummyBody(content)}
            raise S3Error(404)

    def delete_object(self, Bucket, Key, IfMatch):
        with self.lock:
            if Key not in self.objects:
                raise S3Error(404)
            if probe.sha(self.objects[Key]) != IfMatch:
                raise S3Error(412)
            del self.objects[Key]
            self.sequence += 1
            version = f"revision-{self.sequence}"
            self.history[Key].append((version, None, True))
            return {"DeleteMarker": True, "VersionId": version}

    def list_object_versions(self, Bucket, Prefix, MaxKeys):
        versions, markers = [], []
        with self.lock:
            for key, history in self.history.items():
                if not key.startswith(Prefix):
                    continue
                for version, _, marker in history:
                    entry = {"Key": key, "VersionId": version}
                    (markers if marker else versions).append(entry)
        return {"Versions": versions, "DeleteMarkers": markers,
                "IsTruncated": False}


class BrokenHistory(FakeVersionedS3):
    def get_object(self, Bucket, Key, VersionId=None):
        # Regression: provider ignored the requested version and read latest.
        return super().get_object(Bucket, Key, VersionId=None)


class DisabledVersioning(FakeVersionedS3):
    def get_bucket_versioning(self, **kwargs):
        return {"Status": "Suspended"}


class BrokenDeleteMarker(FakeVersionedS3):
    def delete_object(self, Bucket, Key, IfMatch):
        super().delete_object(Bucket, Key, IfMatch)
        return {"DeleteMarker": False}


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

    def test_versioned_generations_remain_accessible_across_delete_marker(self):
        result = probe.run_versioning_probe(
            FakeVersionedS3(), "disposable", probe.PROBE_ROOT + "unit")
        self.assertEqual("pass", result["versioning_and_history"])
        self.assertEqual(2, result["left_version_generations"])
        self.assertEqual(1, result["left_delete_markers"])
        self.assertFalse(result["full_m1_4_provider_certified"])

    def test_versioning_suspended_fails_closed(self):
        with self.assertRaises(probe.ProbeFailure):
            probe.run_versioning_probe(
                DisabledVersioning(), "disposable", probe.PROBE_ROOT + "unit")

    def test_ignored_historical_version_fails_closed(self):
        with self.assertRaises(probe.ProbeFailure):
            probe.run_versioning_probe(
                BrokenHistory(), "disposable", probe.PROBE_ROOT + "unit")

    def test_delete_without_tombstone_fails_closed(self):
        with self.assertRaises(probe.ProbeFailure):
            probe.run_versioning_probe(
                BrokenDeleteMarker(), "disposable", probe.PROBE_ROOT + "unit")

    def test_versioning_namespace_guardrails(self):
        for prefix in ("../bad", probe.PROBE_ROOT, ""):
            with self.assertRaises(ValueError):
                probe.run_versioning_probe(FakeVersionedS3(), "disposable", prefix)

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

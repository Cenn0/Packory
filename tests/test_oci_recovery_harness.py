"""Pure helper guards; these tests do not claim real process recovery evidence."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location(
    "smoke_oci_recovery", Path(__file__).resolve().parents[1] / "scripts" / "smoke_oci_recovery.py")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class HarnessGuards(unittest.TestCase):
    def test_inherited_identity_storage_and_profiler_settings_are_removed(self):
        env = mod.isolated_environment({"PATH": "/usr/bin", "PACKORY_OIDC_AUTHORITY": "unused",
                                       "Packory__S3Bucket": "unused", "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES": "unused",
                                       "CORECLR_PROFILER": "unused", "OTEL_EXPORTER_OTLP_ENDPOINT": "unused"},
                                      Path("/fixture"), "http://127.0.0.1:12345", "generated-token-fixture")
        self.assertEqual("/usr/bin", env["PATH"])
        self.assertEqual("sqlite", env["PACKORY_METADATA_PROVIDER"])
        self.assertEqual("filesystem", env["PACKORY_BLOB_PROVIDER"])
        for key in ("PACKORY_OIDC_AUTHORITY", "Packory__S3Bucket", "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES",
                    "CORECLR_PROFILER", "OTEL_EXPORTER_OTLP_ENDPOINT"):
            self.assertNotIn(key, env)

    def test_foreign_or_credentialed_locations_do_not_receive_a_token(self):
        origin = "http://127.0.0.1:12345"
        for location in ("https://elsewhere.invalid/v2/path", "http://127.0.0.1:12346/v2/path",
                         "//127.0.0.1:12345/v2/path", "/v2/path", origin + "/v2/path#fragment",
                         origin + "/admin", "http://user:pass" + "@127.0.0.1:12345/v2/path",
                         origin + "/v2/path\n"):
            with self.subTest(location=location), self.assertRaises(mod.SmokeFailure):
                mod.safe_location(location, origin)
        self.assertEqual("/v2/team/upload?offset=3", mod.safe_location(origin + "/v2/team/upload?offset=3", origin))


if __name__ == "__main__":
    unittest.main()

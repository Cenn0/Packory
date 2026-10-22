import importlib.util
import json
from pathlib import Path
import subprocess
from types import SimpleNamespace
import unittest

spec = importlib.util.spec_from_file_location("client_environment", Path(__file__).resolve().parents[1] / "scripts/client_environment.py")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class ClientEnvironmentTests(unittest.TestCase):
    def test_only_parsed_versions_are_exported(self):
        private = "private" + "@" + "private.example"
        def run(command, **kwargs):
            self.assertFalse(kwargs.get("shell", False))
            self.assertEqual(5, kwargs["timeout"])
            self.assertEqual(subprocess.DEVNULL, kwargs["stderr"])
            if command[:1] == ["node"]:
                return SimpleNamespace(returncode=0, stdout="v22.4.1\n")
            if command[:1] == ["mvn"]:
                return SimpleNamespace(returncode=0, stdout="\x1b[1mApache Maven 3.9.11\x1b[m\nHome: /private/path/" + private)
            if command[:1] == ["curl"]:
                return SimpleNamespace(returncode=0, stdout="curl 8.5.0 (platform)\n" + private)
            raise FileNotFoundError(private)
        result = mod.collect("a" * 40, run)
        self.assertEqual("22.4.1", result["clients"]["node"]["version"])
        self.assertEqual("3.9.11", result["clients"]["maven"]["version"])
        self.assertEqual("8.5.0", result["clients"]["curl"]["version"])
        self.assertEqual("unavailable", result["clients"]["dotnet"]["status"])
        self.assertNotIn(private, json.dumps(result))
        self.assertNotIn("/private/path", json.dumps(result))
        self.assertFalse(result["productionRecommended"])

    def test_failed_or_unparseable_output_never_becomes_version(self):
        for status, text in ((1, "22.1.0\n"), (0, "invalid"), (0, "v22.1.0 injected")):
            result = mod.collect("a" * 40, lambda *a, **k: SimpleNamespace(returncode=status, stdout=text))
            self.assertEqual("unavailable", result["clients"]["node"]["status"])

    def test_timeouts_and_invalid_source_are_explicit(self):
        def run(*args, **kwargs):
            raise subprocess.TimeoutExpired("synthetic", 5)
        result = mod.collect("a" * 40, run)
        self.assertTrue(all(row["status"] == "unavailable" for row in result["clients"].values()))
        with self.assertRaises(ValueError):
            mod.collect("main", run)

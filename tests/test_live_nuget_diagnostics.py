"""Offline tests for bounded, credential-free NuGet live-smoke diagnostics."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'scripts' / 'diagnose-live-nuget.sh'


class LiveNugetDiagnosticTests(unittest.TestCase):
    def run_probe(self, upstream_status: str, checksum_status: str, local_status: str):
        with tempfile.TemporaryDirectory() as temp:
            stub = Path(temp) / 'curl'
            stub.write_text(
                '#!/usr/bin/env bash\n'
                'for arg in "$@"; do\n'
                '  case "$arg" in\n'
                '    https://api.nuget.org/*.sha512) printf "%s" "$FAKE_CHECKSUM"; exit 0 ;;\n'
                '    https://api.nuget.org/*) printf "%s" "$FAKE_UPSTREAM"; exit 0 ;;\n'
                '    http://127.0.0.1:8080/*) printf "%s" "$FAKE_LOOPBACK"; exit 0 ;;\n'
                '  esac\n'
                'done\n'
                'printf "sensitive-sentinel" >&2\n'
                'exit 22\n',
                encoding='utf-8',
            )
            stub.chmod(0o755)
            env = dict(os.environ, PATH=f"{temp}:{os.environ.get('PATH', '')}",
                       FAKE_UPSTREAM=upstream_status, FAKE_CHECKSUM=checksum_status,
                       FAKE_LOOPBACK=local_status)
            return subprocess.run(['bash', str(SCRIPT)], env=env, text=True,
                                  capture_output=True, check=False, timeout=10)

    def test_reports_only_bounded_http_status_codes(self):
        result = self.run_probe('200', '200', '502')
        self.assertEqual(result.returncode, 0)
        self.assertEqual(result.stderr, '')
        self.assertEqual(result.stdout.splitlines(), [
            'Public NuGet artifact: HTTP 200',
            'Public NuGet checksum: HTTP 200',
            'Packory loopback artifact: HTTP 502',
            'Diagnostic only: status codes do not establish root cause.',
        ])

    def test_invalid_status_does_not_leak_untrusted_text(self):
        result = self.run_probe('sensitive-sentinel', '404', '000')
        self.assertEqual(result.returncode, 0)
        self.assertEqual(result.stderr, '')
        self.assertIn('Public NuGet artifact: HTTP 000', result.stdout)
        self.assertNotIn('sensitive-sentinel', result.stdout)

    def test_requires_bounded_requests_and_fixed_origins(self):
        source = SCRIPT.read_text(encoding='utf-8')
        self.assertIn('--connect-timeout 5', source)
        self.assertIn('--max-time 15', source)
        self.assertIn('--retry 0', source)
        self.assertIn('--proto "=$protocol"', source)
        self.assertIn('https://api.nuget.org/v3-flatcontainer', source)
        self.assertIn('http://127.0.0.1:8080/nuget/v3/flatcontainer', source)
        self.assertNotIn('insecure', source.lower())
        self.assertNotIn('--location', source)


if __name__ == '__main__':
    unittest.main()

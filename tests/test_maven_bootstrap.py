"""Run the real shell classifier against captured-format Maven diagnostics."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
HELPER = ROOT / 'scripts/maven-bootstrap.sh'


class MavenBootstrapTests(unittest.TestCase):
    def test_recognizes_only_transient_transport_or_http_status(self):
        cases = {
            '[ERROR] Could not transfer artifact from central: HTTP Status: 429': True,
            'status code: 503, reason phrase: Service Unavailable': True,
            '429 Too Many Requests': True,
            'HTTP Status: 502': True,
            'HTTP Status: 504': True,
            'Connection reset': True,
            'HTTP Status: 404': False,
            'HTTP Status: 4290': False,
            'PluginResolutionException: missing-plugin:429': False,
            'Compilation failure: cannot find symbol': False,
            'Package checksum mismatch': False,
        }
        with tempfile.TemporaryDirectory() as root:
            log = Path(root) / 'maven.log'
            for text, expected in cases.items():
                with self.subTest(text=text):
                    log.write_text(text)
                    result = subprocess.run(['bash', '-c', 'source "$1"; packory_maven_transient "$2"',
                                             'test', str(HELPER), str(log)], capture_output=True, timeout=5)
                    self.assertEqual(expected, result.returncode == 0)

    def test_backoff_is_bounded_and_does_not_sleep_after_final_attempt(self):
        result = subprocess.run(['bash', '-c', '''
            source "$1"
            sleep() { printf '%s\\n' "$1"; }
            packory_maven_backoff 1
            packory_maven_backoff 2
            packory_maven_backoff 3
            ''', 'test', str(HELPER)], capture_output=True, text=True, timeout=5)
        self.assertEqual(2, result.returncode)
        self.assertEqual('15\n30\n', result.stdout)


if __name__ == '__main__':
    unittest.main()

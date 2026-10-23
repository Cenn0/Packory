"""Synthetic transport tests: no external endpoints or live credentials."""
import importlib.util
import contextlib
import io
import json
import os
from pathlib import Path
import ssl
import tempfile
import threading
import time
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location('load_probe', Path(__file__).resolve().parents[1] /
                                             'scripts/single_node_load_probe.py')
probe = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probe)
TOKEN = 'synthetic-load-token-not-a-real-secret'


class Reply(io.BytesIO):
    def __init__(self, status, content):
        super().__init__(content)
        self.status = status


class Transport:
    def __init__(self, *, corrupt=False, oversized=False, status=None, fail=False):
        self.payloads = {}
        self.lock = threading.Lock()
        self.corrupt, self.oversized, self.status, self.fail = corrupt, oversized, status, fail

    def open(self, request, timeout):
        if self.fail:
            raise OSError('Potentially sensitive transport message ' + TOKEN)
        self.assert_request(request, timeout)
        with self.lock:
            if request.method == 'PUT':
                self.payloads[request.full_url] = request.data
                return Reply(self.status or 201, b'{}')
            content = self.payloads[request.full_url]
            if self.corrupt:
                content = b'X' + content[1:]
            if self.oversized:
                content += b'x'
            return Reply(self.status or 200, content)

    @staticmethod
    def assert_request(request, timeout):
        assert request.full_url.startswith('https://synthetic.example.invalid/raw/packory.synthetic.load/')
        assert request.get_header('Authorization') == 'Bearer ' + TOKEN
        assert 0 < timeout <= 10


class BoundedLoadProbeTests(unittest.TestCase):
    def test_concurrent_publish_download_verifies_every_pair_and_sanitizes_evidence(self):
        transport = Transport()
        result = probe.workload('https://synthetic.example.invalid/', TOKEN, operations=12,
                                opener_factory=lambda: transport)
        self.assertEqual(result['result'], 'passed')
        self.assertEqual(result['completed_pairs'], 12)
        self.assertEqual(len(transport.payloads), 12)
        for forbidden in (TOKEN, 'synthetic.example.invalid', '/raw/', 'Authorization'):
            self.assertNotIn(forbidden, json.dumps(result))
        self.assertFalse(result['production_qualified'])
        self.assertEqual(result['resource_measurements'], 'not_collected')

    def test_corruption_oversize_wrong_status_and_transport_failure_fail_closed(self):
        for setting in ({'corrupt': True}, {'oversized': True}, {'status': 202}, {'fail': True}):
            with self.subTest(setting=setting):
                result = probe.workload('https://synthetic.example.invalid', TOKEN, operations=4,
                                        opener_factory=lambda: Transport(**setting))
                self.assertEqual(result['result'], 'failed')
                self.assertGreater(result['failed_pairs'], 0)
                self.assertNotIn(TOKEN, json.dumps(result))

    def test_unsafe_origin_rejected_before_transport_construction(self):
        for value in ('http://synthetic.example.invalid', 'https://fixture:fixture@example.invalid',
                      'https://synthetic.example.invalid/a', 'https://synthetic.example.invalid/?token=x',
                      'https://synthetic.example.invalid/#fragment', 'https://host:99999',
                      'https://host\\@other', 'https://host\n'):
            with self.subTest(value=value), self.assertRaises(probe.ProbeError):
                probe.origin(value)

    def test_workload_caps_and_invalid_types_rejected_before_requests(self):
        for setting in ({'workers': 5}, {'workers': True}, {'operations': 41}, {'operations': 0},
                        {'payload_bytes': 1048577}, {'payload_bytes': False}, {'deadline_seconds': 301}):
            with self.subTest(setting=setting), self.assertRaises(probe.ProbeError):
                probe.workload('https://synthetic.example.invalid', TOKEN, **setting)

    def test_insecure_tls_context_and_control_token_rejected(self):
        context = ssl._create_unverified_context()
        with self.assertRaises(probe.ProbeError):
            probe.workload('https://synthetic.example.invalid', TOKEN, context=context)
        with self.assertRaises(probe.ProbeError):
            probe.workload('https://synthetic.example.invalid', TOKEN + '\n')

    def test_redirect_is_rejected_without_a_followup_request(self):
        with self.assertRaises(probe.ProbeError):
            probe.NoRedirect().redirect_request(None, None, 307, '', {}, 'https://other.example.invalid')

    def test_absolute_deadline_checked_between_slow_chunks(self):
        class SlowReply:
            def read1(self, limit):
                time.sleep(.02)
                return b'x'
        with self.assertRaises(probe.ProbeError):
            probe.bounded_read(SlowReply(), 100, time.monotonic() + .01, threading.Event())

    def test_token_file_validation_and_optional_single_terminal_newline(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'fixture'
            for suffix in (b'', b'\n', b'\r\n'):
                path.write_bytes(TOKEN.encode() + suffix)
                path.chmod(0o600)
                self.assertEqual(probe.read_token(path), TOKEN)
            for data in (TOKEN.encode() + b'\n\n', TOKEN.encode() + b'\r', b'x' * 1025,
                         b'x' * 31, b'\xef\xbb\xbf' + TOKEN.encode()):
                path.write_bytes(data)
                with self.assertRaises(probe.ProbeError):
                    probe.read_token(path)
            path.write_text(TOKEN)
            if os.name != 'nt':
                alias = Path(folder) / 'alias'
                alias.symlink_to(path)
                with self.assertRaises(probe.ProbeError):
                    probe.read_token(alias)
                path.chmod(0o644)
                with self.assertRaises(probe.ProbeError):
                    probe.read_token(path)
            with self.assertRaises(probe.ProbeError):
                probe.read_token(Path(folder))
            with self.assertRaises(probe.ProbeError):
                probe.read_token('relative-fixture')

    def test_existing_evidence_is_not_overwritten_and_no_network_work_is_started(self):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / 'existing.json'
            output.write_text('original evidence')
            arguments = ['probe', '--confirm-isolated-test', '--origin', 'https://synthetic.example.invalid',
                         '--token-file', str(Path(folder) / 'absent'), '--source-sha', 'a' * 40,
                         '--output', str(output)]
            with mock.patch('sys.argv', arguments), mock.patch.object(probe, 'workload') as workload:
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(probe.main(), 1)
                workload.assert_not_called()
            self.assertEqual(output.read_text(), 'original evidence')

    def test_percentile_nearest_rank_has_defined_empty_and_small_samples(self):
        self.assertIsNone(probe.percentile95([]))
        self.assertEqual(probe.percentile95([5]), 5)
        self.assertEqual(probe.percentile95(list(range(1, 21))), 19)


if __name__ == '__main__':
    unittest.main()

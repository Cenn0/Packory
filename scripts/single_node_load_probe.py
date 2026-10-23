#!/usr/bin/env python3
"""Opt-in bounded Raw publish/download latency probe for disposable installations.

Creates retained synthetic immutable artifacts. It never deletes data, modifies
host configuration, reads arbitrary server responses into evidence or qualifies
a deployment. Resource, boot, provider and independent-review gates are separate.
"""
import argparse
import concurrent.futures
import http.client
import hashlib
import json
import math
import multiprocessing
import os
from pathlib import Path
import re
import secrets
import ssl
import stat
import threading
import time
import urllib.error
import urllib.parse
import urllib.request


class ProbeError(Exception):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ProbeError('redirect_rejected')


def origin(value):
    try:
        parsed = urllib.parse.urlsplit(value)
        port = parsed.port
    except ValueError:
        raise ProbeError('invalid_origin') from None
    if (parsed.scheme != 'https' or not parsed.hostname or parsed.username is not None or
            parsed.password is not None or parsed.path not in ('', '/') or parsed.query or
            parsed.fragment or any(ord(c) <= 32 or ord(c) >= 127 for c in value) or
            '\\' in value or (port is not None and not 1 <= port <= 65535)):
        raise ProbeError('invalid_origin')
    return value.rstrip('/')


def read_token(path):
    path = Path(path)
    if not path.is_absolute():
        raise ProbeError('invalid_token_file')
    descriptor = None
    try:
        before = path.lstat()
        if (not stat.S_ISREG(before.st_mode) or stat.S_ISLNK(before.st_mode) or
                getattr(before, 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0)):
            raise ProbeError('invalid_token_file')
        descriptor = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0) |
                             getattr(os, 'O_NONBLOCK', 0) | getattr(os, 'O_BINARY', 0))
        opened = os.fstat(descriptor)
        if (not stat.S_ISREG(opened.st_mode) or (before.st_dev, before.st_ino) != (opened.st_dev, opened.st_ino) or
                not 32 <= opened.st_size <= 1024 or (os.name != 'nt' and
                (opened.st_mode & 0o027 or opened.st_uid not in (0, os.geteuid())))):
            raise ProbeError('invalid_token_file')
        with os.fdopen(descriptor, 'rb') as handle:
            descriptor = None
            raw = handle.read(1025)
        # Only a single optional terminal newline is admitted; never trim controls.
        value = raw[:-2] if raw.endswith(b'\r\n') else raw.removesuffix(b'\n')
        if not 32 <= len(value) <= 1024 or any(c <= 32 or c >= 127 for c in value):
            raise ProbeError('invalid_token_file')
        return value.decode('ascii')
    except OSError:
        raise ProbeError('invalid_token_file') from None
    finally:
        if descriptor is not None:
            os.close(descriptor)


def percentile95(values):
    if not values:
        return None
    return round(sorted(values)[math.ceil(len(values) * .95) - 1], 3)


def bounded_read(response, limit, end, stop):
    content = bytearray()
    while len(content) < limit:
        if stop.is_set() or time.monotonic() >= end:
            raise ProbeError('deadline_exceeded')
        # One buffered/socket read, rather than read(n), which may keep waiting
        # for many dribbled chunks without returning to the absolute deadline.
        chunk = response.read1(min(65536, limit - len(content)))
        if time.monotonic() >= end:
            raise ProbeError('deadline_exceeded')
        if not chunk:
            break
        content.extend(chunk)
    return bytes(content)


def workload(base, token, *, workers=4, operations=16, payload_bytes=32768, deadline_seconds=60,
             context=None, opener_factory=None):
    base = origin(base)
    if (type(workers) is not int or not 1 <= workers <= 4 or
            type(operations) is not int or not 1 <= operations <= 40 or
            type(payload_bytes) is not int or not 1 <= payload_bytes <= 1048576 or
            type(deadline_seconds) is not int or not 1 <= deadline_seconds <= 300):
        raise ProbeError('invalid_workload')
    if not isinstance(token, str) or not 32 <= len(token) <= 1024 or any(ord(c) <= 32 or ord(c) >= 127 for c in token):
        raise ProbeError('invalid_token')
    context = context or ssl.create_default_context()
    if context.verify_mode != ssl.CERT_REQUIRED or not context.check_hostname:
        raise ProbeError('tls_validation_required')
    factory = opener_factory or (lambda: urllib.request.build_opener(
        urllib.request.ProxyHandler({}), NoRedirect(), urllib.request.HTTPSHandler(context=context)))
    seed = b'Packory synthetic bounded load fixture\n'
    payload = (seed * (payload_bytes // len(seed) + 1))[:payload_bytes]
    expected = hashlib.sha256(payload).digest()
    nonce = secrets.token_hex(12)
    stop = threading.Event()
    end = time.monotonic() + deadline_seconds

    def one(index):
        if stop.is_set() or time.monotonic() >= end:
            raise ProbeError('deadline_exceeded')
        opener = factory()
        url = base + '/raw/packory.synthetic.load/1.0.0-probe' + nonce + '-' + str(index)
        durations = []
        for method in ('PUT', 'GET'):
            remaining = end - time.monotonic()
            if remaining <= 0 or stop.is_set():
                raise ProbeError('deadline_exceeded')
            headers = {'Authorization': 'Bearer ' + token, 'Accept-Encoding': 'identity'}
            if method == 'PUT':
                headers['Content-Type'] = 'application/octet-stream'
            request = urllib.request.Request(url, method=method,
                data=payload if method == 'PUT' else None, headers=headers)
            started = time.perf_counter()
            with opener.open(request, timeout=min(10, remaining)) as response:
                if response.status != (201 if method == 'PUT' else 200):
                    raise ProbeError('unexpected_status')
                limit = payload_bytes + 1 if method == 'GET' else 4097
                content = bounded_read(response, limit, end, stop)
                if method == 'PUT' and len(content) > 4096:
                    raise ProbeError('oversized_response')
                if method == 'GET' and (len(content) != payload_bytes or hashlib.sha256(content).digest() != expected):
                    raise ProbeError('byte_integrity_failed')
            durations.append((time.perf_counter() - started) * 1000)
        return durations

    started = time.monotonic()
    passed, failures = [], 0
    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
        futures = [pool.submit(one, index) for index in range(operations)]
        for future in concurrent.futures.as_completed(futures):
            try:
                passed.append(future.result())
            except (ProbeError, OSError, TimeoutError, urllib.error.URLError,
                    http.client.HTTPException):
                failures += 1
                stop.set()
    return {
        'schema': 'packory.single-node-load-probe/1',
        'result': 'passed' if len(passed) == operations and failures == 0 else 'failed',
        'workload': {'workers': workers, 'operations': operations, 'payload_bytes': payload_bytes,
                     'deadline_seconds': deadline_seconds},
        'completed_pairs': len(passed), 'failed_pairs': failures,
        'elapsed_seconds': round(time.monotonic() - started, 3),
        'p95_publish_ms': percentile95([item[0] for item in passed]),
        'p95_download_ms': percentile95([item[1] for item in passed]),
        'tls': 'certificate_and_hostname_validated', 'synthetic_artifacts_retained': True,
        'resource_measurements': 'not_collected', 'production_qualified': False,
    }


def isolated_worker(sender, options):
    try:
        result = workload(**options)
    except BaseException:
        # The HTTPS peer can control exception text from the HTTP parser.
        # No traceback or exception string crosses the child boundary.
        result = {'schema': 'packory.single-node-load-probe/1', 'result': 'failed',
                  'reason': 'transport_or_worker_rejected', 'production_qualified': False}
    try:
        sender.send(result)
    except OSError:
        pass
    finally:
        sender.close()


def isolated_workload(options):
    context = multiprocessing.get_context('spawn')
    receiver, sender = context.Pipe(duplex=False)
    process = context.Process(target=isolated_worker, args=(sender, options))
    try:
        process.start()
        sender.close()
        if receiver.poll(options['deadline_seconds'] + 2):
            try:
                result = receiver.recv()
            except (EOFError, OSError):
                result = None
        else:
            result = None
        if result is None or not isinstance(result, dict) or result.get('result') not in ('passed', 'failed'):
            return {'schema': 'packory.single-node-load-probe/1', 'result': 'failed',
                    'reason': 'deadline_or_worker_failed', 'production_qualified': False}
        return result
    finally:
        receiver.close()
        sender.close()
        process.join(timeout=.2) if process.pid is not None else None
        if process.is_alive():
            process.terminate()
            process.join(timeout=1)
        if process.is_alive():
            process.kill()
            process.join(timeout=1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--confirm-isolated-test', action='store_true', required=True)
    parser.add_argument('--origin', required=True)
    parser.add_argument('--token-file', required=True)
    parser.add_argument('--source-sha', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--workers', type=int, default=4)
    parser.add_argument('--operations', type=int, default=16)
    parser.add_argument('--payload-bytes', type=int, default=32768)
    parser.add_argument('--deadline-seconds', type=int, default=60)
    args = parser.parse_args()
    # Validate output before performing any request. Exclusive creation prevents
    # overwriting a prior evidence file or following an existing symlink.
    if not re.fullmatch('[0-9a-f]{40}', args.source_sha):
        parser.error('source-sha must be a 40-hex source revision')
    try:
        descriptor = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except OSError:
        print('Bounded load probe: output unavailable; no request was performed.')
        return 1
    try:
        with os.fdopen(descriptor, 'w') as output:
            try:
                result = isolated_workload({'base': args.origin, 'token': read_token(args.token_file),
                    'workers': args.workers, 'operations': args.operations,
                    'payload_bytes': args.payload_bytes, 'deadline_seconds': args.deadline_seconds})
                result['declared_source_sha'] = args.source_sha
                result['source_binding'] = 'operator_asserted_not_remotely_verified'
            except Exception:
                result = {'schema': 'packory.single-node-load-probe/1', 'result': 'failed',
                          'reason': 'input_or_transport_rejected', 'production_qualified': False}
            output.write(json.dumps(result, indent=2, allow_nan=False) + '\n')
        print('Bounded load probe: ' + result['result'] + '; no deployment qualification inferred.')
        return 0 if result['result'] == 'passed' else 1
    except OSError:
        print('Bounded load probe: evidence write failed; execution may have occurred.')
        return 1


if __name__ == '__main__':
    raise SystemExit(main())

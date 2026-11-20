#!/usr/bin/env python3
"""Disposable, authenticated UDS negative probes against the real worker process.

Uses a synthetic credential and no actual database. The separate native-driver
matrix tests real database connections. This smoke does not claim a container
escape test or validate externally routed database egress.
"""
import hashlib
import hmac
import json
import os
import socket
import stat
import struct
import sys
import uuid

sock_path, key_path = sys.argv[1:]
key = open(key_path, "rb").read()
assert len(key) == 32
assert stat.S_IMODE(os.stat(sock_path).st_mode) == 0o600, "worker UDS must be owner-only"


def frame(body: bytes, *, invalid_mac=False) -> bytes:
    mac = hmac.new(key, body, hashlib.sha256).digest()
    if invalid_mac:
        mac = bytes([mac[0] ^ 1]) + mac[1:]
    return struct.pack("!I", len(body)) + mac + body


def recv_exact(s, length):
    data = b""
    while len(data) < length:
        chunk = s.recv(length - len(data))
        if not chunk:
            raise EOFError("unexpected worker EOF")
        data += chunk
    return data


def transact(packet: bytes, *, expect_reply: bool) -> dict | None:
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
        s.settimeout(22)
        s.connect(sock_path)
        s.sendall(packet)
        s.shutdown(socket.SHUT_WR)
        if not expect_reply:
            assert s.recv(1) == b"", "worker must never reply to an unauthenticated frame"
            return None
        header = recv_exact(s, 36)
        size = struct.unpack("!I", header[:4])[0]
        assert 0 < size <= 140 * 1024, "oversized worker reply"
        body = recv_exact(s, size)
        assert hmac.compare_digest(
            hmac.new(key, body, hashlib.sha256).digest(), header[4:]
        ), "worker reply did not authenticate"
        return json.loads(body)


request_id = uuid.uuid4().hex
synthetic_password = "synthetic-worker-password-never-log"
request = {
    "version": 1,
    "requestId": request_id,
    "operation": "probe",
    "definition": {
        "id": "isolation-probe",
        "name": "Synthetic isolated process probe",
        "provider": "postgresql",
        "host": "8.8.8.8",
        "port": 5432,
        "database": "fixture",
        "username": "fixture_reader",
        "tlsServerName": "db.example.invalid",
        "oracleServerCertificateDn": None,
        "enabled": False,
        "queries": [],
        "revision": 1,
    },
    "query": None,
    "arguments": {},
    "password": synthetic_password,
}
body = json.dumps(request, separators=(",", ":")).encode("utf-8")
transact(frame(body, invalid_mac=True), expect_reply=False)
transact(struct.pack("!I", 96 * 1024 + 1) + b"\0" * 32,
         expect_reply=False)

reply = transact(frame(body), expect_reply=True)
assert reply == {
    "version": 1, "requestId": request_id,
    "succeeded": False, "result": None
}, "Worker failed to return a generic, bounded negative result"
assert synthetic_password not in json.dumps(reply)
assert "8.8.8.8" not in json.dumps(reply)

print("Isolated worker: owner-only socket, authenticated request/reply, "
      "invalid HMAC/oversize denied, generic native failure confirmed.")

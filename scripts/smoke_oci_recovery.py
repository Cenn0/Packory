#!/usr/bin/env python3
"""Real single-process OCI upload crash/disconnect regression on loopback.

Run after dotnet build -c Release. Uses only its own disposable SQLite/filesystem
instance. This is NOT a PostgreSQL/S3, multi-node or power-loss acceptance test.
"""
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
from urllib.parse import urlsplit


class SmokeFailure(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise SmokeFailure(message)


def isolated_environment(inherited, root, origin, token):
    # Exclude application/provider/identity/profiler configuration inherited from
    # the operator shell. This process must never attach to a production service.
    blocked = ("PACKORY", "ASPNETCORE", "OTEL_", "CORECLR_", "COR_", "COMPLUS_",
               "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE")
    env = {key: value for key, value in inherited.items()
           if not key.upper().startswith(blocked)}
    env.update({
        "PACKORY_DATA_DIRECTORY": str(root / "data"),
        "PACKORY_METADATA_PROVIDER": "sqlite",
        "PACKORY_BLOB_PROVIDER": "filesystem",
        "PACKORY_INSTANCE_ROLE": "writer",
        "PACKORY_PUBLIC_BASE_URL": origin,
        "PACKORY_ADMIN_TOKEN": token,
        "PACKORY_ANONYMOUS_READ": "false",
        "PACKORY_STRICT_DEPLOYMENT": "false",
        "PACKORY_RECOVERY_MAINTENANCE": "false",
        "Packory__MaxArtifactBytes": str(2 * 1024 * 1024),
        "ASPNETCORE_URLS": origin,
        "ASPNETCORE_ENVIRONMENT": "Production",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
        "DOTNET_EnableDiagnostics": "0",
    })
    return env


def safe_location(value, origin):
    candidate, expected = urlsplit(value), urlsplit(origin)
    require(candidate.scheme == expected.scheme and candidate.netloc == expected.netloc and
            not candidate.username and not candidate.password and not candidate.fragment and
            candidate.path.startswith("/v2/") and
            not any(ord(c) < 32 or ord(c) == 127 for c in value),
            "Registry returned an unexpected upload origin or path")
    return candidate.path + ("?" + candidate.query if candidate.query else "")


class Instance:
    def __init__(self, root, dll):
        self.root, self.dll = root, dll
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            self.port = reservation.getsockname()[1]
        self.origin = "http://127.0.0.1:" + str(self.port)
        self.token = secrets.token_hex(32)
        self.env = isolated_environment(os.environ, root, self.origin, self.token)
        self.process = None
        self.log = None

    def start(self):
        require(self.process is None, "A previous test process is still tracked")
        self.log = open(self.root / "private-server.log", "ab")
        self.process = subprocess.Popen([shutil.which("dotnet"), str(self.dll)],
                                        cwd=self.root, env=self.env, stdin=subprocess.DEVNULL,
                                        stdout=self.log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            require(self.process.poll() is None, "Disposable Packory process exited during startup")
            try:
                if self.request("GET", "/health/ready")[0] == 200:
                    return
            except (OSError, http.client.HTTPException):
                pass
            time.sleep(0.1)
        raise SmokeFailure("Disposable Packory readiness deadline exceeded")

    def stop(self, crash=False):
        process, self.process = self.process, None
        try:
            if process is not None and process.poll() is None:
                if crash:
                    process.kill()
                else:
                    process.terminate()
                try:
                    code = process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    code = process.wait(timeout=10)
                if crash:
                    require(code == -signal.SIGKILL, "Expected an actual SIGKILL process termination")
            elif crash:
                raise SmokeFailure("Process exited before the injected crash")
        finally:
            if self.log is not None:
                self.log.close()
                self.log = None

    def request(self, method, path, body=b"", content_range=None):
        require(path.startswith("/") and not path.startswith("//"), "Unexpected local request path")
        headers = {"Authorization": "Bearer " + self.token}
        if body or method in ("PATCH", "PUT", "POST"):
            headers["Content-Type"] = "application/octet-stream"
        if content_range is not None:
            headers["Content-Range"] = content_range
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=15)
        try:
            connection.request(method, path, body=body, headers=headers)
            response = connection.getresponse()
            payload = response.read(1024 * 1024 + 1)
            require(len(payload) <= 1024 * 1024, "Unexpected oversized test response")
            return response.status, {key.lower(): value for key, value in response.getheaders()}, payload
        finally:
            connection.close()

    def begin(self):
        status, headers, _ = self.request("POST", "/v2/team/recovery/blobs/uploads/")
        require(status == 202, "Upload initialization failed")
        path = safe_location(headers.get("location", ""), self.origin)
        identifier = path.rsplit("/", 1)[-1]
        require(re.fullmatch(r"[0-9a-f]{32}", identifier) is not None, "Unexpected upload identifier")
        status, headers, _ = self.request("PATCH", path, b"abc", "0-2")
        require(status == 202 and headers.get("range") == "0-2", "Initial chunk was not acknowledged")
        return path, self.root / "data" / "oci-staging" / (identifier + ".tmp")

    def partial_body(self, path, staging):
        """Keep a real TCP request incomplete until the process has written a tail."""
        channel = socket.create_connection(("127.0.0.1", self.port), timeout=10)
        try:
            declared = 1024 * 1024
            header = ("PATCH " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + str(self.port) +
                      "\r\nAuthorization: Bearer " + self.token +
                      "\r\nContent-Type: application/octet-stream\r\nContent-Length: " + str(declared) +
                      "\r\nContent-Range: 3-" + str(3 + declared - 1) + "\r\n\r\n")
            channel.sendall(header.encode("ascii") + b"x" * (256 * 1024))
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                require(self.process is not None and self.process.poll() is None,
                        "Process exited before an uncommitted body was observed")
                if staging.exists() and staging.stat().st_size > 3:
                    metadata = json.loads(staging.with_suffix(".json").read_text())
                    require(metadata.get("CommittedLength") == 3,
                            "Incomplete chunk incorrectly changed the acknowledged offset")
                    return channel
                time.sleep(0.02)
            raise SmokeFailure("No uncommitted write observed; crash evidence would be insufficient")
        except BaseException:
            channel.close()
            raise


def exercise(dll):
    require(os.name == "posix", "This acceptance profile requires POSIX SIGKILL")
    require(shutil.which("dotnet") is not None and dll.is_file(),
            "Build the Release application with .NET before running this smoke")
    with tempfile.TemporaryDirectory(prefix="packory-oci-kill-") as temporary:
        instance = Instance(Path(temporary), dll)
        channel = None
        try:
            instance.start()
            path, staging = instance.begin()
            channel = instance.partial_body(path, staging)
            instance.stop(crash=True)
            channel.close()
            channel = None
            instance.start()
            # Accessing the upload initializes the singleton store and its replay.
            status, headers, _ = instance.request("GET", path)
            require(status == 204 and headers.get("range") == "0-2", "Restart returned an incorrect committed offset")
            require(staging.read_bytes() == b"abc", "Restart retained an unacknowledged tail or lost committed bytes")
            status, _, _ = instance.request("PATCH", path, b"def", "3-5")
            require(status == 202, "Upload did not resume after a real process crash")
            status, headers, _ = instance.request("PATCH", path, b"def", "3-5")
            require(status == 416 and headers.get("range") == "0-5", "Chunk replay was not rejected after restart")
            digest = "sha256:" + hashlib.sha256(b"abcdef").hexdigest()
            status, headers, _ = instance.request("PUT", path + "?digest=" + digest)
            require(status == 201, "Resumed content could not be published")
            blob = safe_location(headers.get("location", ""), instance.origin)
            status, _, body = instance.request("GET", blob)
            require(status == 200 and body == b"abcdef", "Resumed content digest/bytes disagree")
            require(instance.request("GET", path)[0] == 404, "Completed upload remained resumable")

            path, staging = instance.begin()
            channel = instance.partial_body(path, staging)
            channel.shutdown(socket.SHUT_RDWR)
            channel.close()
            channel = None
            status, headers, _ = instance.request("GET", path)
            require(status == 204 and headers.get("range") == "0-2", "Disconnected upload reported uncommitted bytes")
            require(staging.read_bytes() == b"abc", "Disconnected chunk was not rolled back")
            require(instance.request("DELETE", path)[0] == 204, "Upload cancellation failed")
            require(not staging.exists() and not staging.with_suffix(".json").exists(),
                    "Cancelled upload retained a resumable identity")
            return {"profile": "sqlite-filesystem-single-writer-process-recovery",
                    "actual_sigkill": True, "observed_uncommitted_body": True,
                    "restart_offset": 3, "replay_status": 416,
                    "resumed_bytes_verified": True, "tcp_disconnect_rollback": True}
        finally:
            if channel is not None:
                channel.close()
            instance.stop()


def main():
    dll = Path(__file__).resolve().parents[1] / "src/Packory/bin/Release/net10.0/Packory.dll"
    try:
        print(json.dumps(exercise(dll), sort_keys=True))
        return 0
    except Exception as error:
        # Never expose private process logs, tokens, environment or unexpected URLs.
        print("ERROR: " + (str(error) if isinstance(error, SmokeFailure)
                            else "OCI recovery smoke failed in the disposable test environment"), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

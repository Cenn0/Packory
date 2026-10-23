"""Optional offline SkillSpector LLM runner. Only a loopback HTTP relay is opened.

The API brokers inference over bounded files. No provider credential, external
endpoint, tool execution, or network access is available in this container.
"""
from __future__ import annotations

import hashlib
import importlib.metadata
import json
import os
import pathlib
import secrets
import signal
import subprocess
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from worker import decode_json, MAX_ARCHIVE, MAX_REPORT

INPUT = pathlib.Path("/work/in")
OUTPUT = pathlib.Path("/work/out")
SCANNER_VERSION = "2.13.0"
FIELDS = {"id", "sha256", "revision", "deadline", "model", "provider", "outputLanguage",
          "maxPromptBytes", "maxOutputTokens", "maxRequests"}


def identity(value: object, length: int) -> bool:
    return isinstance(value, str) and len(value) == length and all(c in "0123456789abcdef" for c in value)


def bounded_read(path: pathlib.Path, limit: int) -> str:
    if path.is_symlink() or not path.is_file() or path.stat().st_size > limit:
        raise ValueError("Invalid exchange file")
    with path.open("rb") as stream:
        payload = stream.read(limit + 1)
    if len(payload) > limit:
        raise ValueError("Exchange file exceeds limit")
    return payload.decode("utf-8", errors="strict")


def atomic_write(path: pathlib.Path, value: object) -> None:
    payload = json.dumps(value, ensure_ascii=True, separators=(",", ":"), allow_nan=False)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.unlink(missing_ok=True)
    with temporary.open("x", encoding="utf-8") as stream:
        stream.write(payload)
    os.replace(temporary, path)


def validate_manifest(path: pathlib.Path) -> dict:
    job = decode_json(bounded_read(path, 4096))
    if not isinstance(job, dict) or set(job) != FIELDS:
        raise ValueError("Invalid AI manifest")
    if (not identity(job["id"], 32) or path.name != job["id"] + ".job.json" or
            not identity(job["sha256"], 64) or type(job["revision"]) is not int or job["revision"] < 1 or
            type(job["deadline"]) is not int or not 0 < job["deadline"] - time.time() * 1000 <= 610_000 or
            job["provider"] not in ("openai", "anthropic") or
            job["outputLanguage"] not in ("English", "German") or
            not isinstance(job["model"], str) or not 1 <= len(job["model"]) <= 160 or
            type(job["maxPromptBytes"]) is not int or not 1024 <= job["maxPromptBytes"] <= 131072 or
            type(job["maxOutputTokens"]) is not int or not 64 <= job["maxOutputTokens"] <= 16384 or
            type(job["maxRequests"]) is not int or not 1 <= job["maxRequests"] <= 64):
        raise ValueError("Invalid AI manifest limits")
    return job


class Relay(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 8

    def __init__(self, job: dict, manifest: pathlib.Path):
        self.job, self.manifest = job, manifest
        self.token = secrets.token_hex(32)  # Local relay authentication, not a provider credential.
        self.slots = threading.BoundedSemaphore(8)
        self.lock = threading.Lock()
        self.requests = 0
        super().__init__(("127.0.0.1", 0), Handler)

    def process_request(self, request, client_address):
        if not self.slots.acquire(blocking=False):
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except BaseException:
            self.slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()


class Handler(BaseHTTPRequestHandler):
    server: Relay
    protocol_version = "HTTP/1.0"

    def log_message(self, *_args):
        pass  # Never log prompts, model output or headers.

    def respond(self, status: int, body: str) -> None:
        encoded = body.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def do_POST(self):
        self.connection.settimeout(5)
        job = self.server.job
        expected = "/v1/messages" if job["provider"] == "anthropic" else "/v1/chat/completions"
        credential = self.headers.get("x-api-key") if job["provider"] == "anthropic" else self.headers.get("Authorization", "").removeprefix("Bearer ")
        try:
            if (self.path != expected or self.headers.get("Transfer-Encoding") is not None or
                    len(self.headers.get_all("Content-Length", [])) != 1 or
                    not secrets.compare_digest(credential or "", self.server.token)):
                raise ValueError("Invalid relay request")
            size = int(self.headers["Content-Length"])
            if not 0 < size <= job["maxPromptBytes"]:
                raise ValueError("Request limit")
            raw = self.rfile.read(size)
            if len(raw) != size or not isinstance(decode_json(raw.decode("utf-8")), dict):
                raise ValueError("Invalid request body")
            with self.server.lock:
                if self.server.requests >= job["maxRequests"]:
                    raise ValueError("Request count limit")
                self.server.requests += 1
            request_id = secrets.token_hex(16)
            prefix = job["id"] + "." + request_id
            request = OUTPUT / (prefix + ".request.json")
            response = INPUT / (prefix + ".response.json")
            atomic_write(request, {"jobId": job["id"], "requestId": request_id, "body": raw.decode("utf-8")})
            while self.server.manifest.exists() and time.time() * 1000 < job["deadline"]:
                if response.exists():
                    answer = decode_json(bounded_read(response, MAX_REPORT * 2))
                    if (not isinstance(answer, dict) or set(answer) != {"status", "body"} or
                            answer["status"] not in (200, 503) or not isinstance(answer["body"], str)):
                        raise ValueError("Invalid relay response")
                    self.respond(answer["status"], answer["body"])
                    return
                time.sleep(0.05)
            raise TimeoutError("Relay cancelled")
        except (ValueError, OSError, TimeoutError, TypeError):
            self.respond(503, '{"error":{"message":"AI relay unavailable"}}')


def run_scanner(job: dict, manifest: pathlib.Path, archive: pathlib.Path, relay: Relay) -> tuple[int, dict | None]:
    base = "http://127.0.0.1:" + str(relay.server_address[1])
    env = {"PATH": os.environ.get("PATH", "/usr/local/bin:/usr/bin"), "HOME": "/tmp",
           "TMPDIR": "/tmp", "PYTHONDONTWRITEBYTECODE": "1",
           "SKILLSPECTOR_PROVIDER": job["provider"], "SKILLSPECTOR_MODEL": job["model"],
           "SKILLSPECTOR_OUTPUT_LANGUAGE": job["outputLanguage"]}
    if job["provider"] == "anthropic":
        env.update(ANTHROPIC_API_KEY=relay.token, ANTHROPIC_BASE_URL=base)
    else:
        env.update(OPENAI_API_KEY=relay.token, OPENAI_BASE_URL=base + "/v1")
    with tempfile.TemporaryFile(mode="w+b") as stdout:
        process = subprocess.Popen(["skillspector", "scan", str(archive), "--format", "json", "--fail-on-incomplete"],
                                   stdout=stdout, stderr=subprocess.DEVNULL, cwd="/tmp", env=env, start_new_session=True)
        try:
            while process.poll() is None:
                if not manifest.exists() or time.time() * 1000 >= job["deadline"] or os.fstat(stdout.fileno()).st_size > MAX_REPORT:
                    raise TimeoutError("AI scan expired or exceeded output limit")
                time.sleep(0.1)
            if process.returncode not in (0, 1):
                return process.returncode, None
            stdout.seek(0)
            payload = stdout.read(MAX_REPORT + 1)
            if len(payload) > MAX_REPORT:
                raise ValueError("Oversized report")
            report = decode_json(payload.decode("utf-8"))
            if not isinstance(report, dict):
                raise ValueError("Invalid report")
            return process.returncode, report
        finally:
            # Kill the entire process group, including abandoned SDK/runtime children.
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait(timeout=5)


def process(manifest: pathlib.Path) -> None:
    job_id = manifest.name.removesuffix(".job.json")
    if not identity(job_id, 32):
        return
    target = OUTPUT / (job_id + ".result.json")
    if target.exists():
        return
    result = {"id": job_id, "sha256": "", "revision": 0, "scanner_version": SCANNER_VERSION,
              "exit_code": 2, "report": None}
    try:
        job = validate_manifest(manifest)
        result.update(sha256=job["sha256"], revision=job["revision"])
        if importlib.metadata.version("skillspector") != SCANNER_VERSION:
            raise ValueError("Unsupported scanner version")
        archive = INPUT / (job_id + ".zip")
        if archive.is_symlink() or not archive.is_file() or archive.stat().st_size > MAX_ARCHIVE:
            raise ValueError("Invalid skill snapshot")
        with archive.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != job["sha256"]:
                raise ValueError("Skill digest mismatch")
        with Relay(job, manifest) as relay:
            serving = threading.Thread(target=relay.serve_forever, daemon=True)
            serving.start()
            try:
                code, report = run_scanner(job, manifest, archive, relay)
                result.update(exit_code=code, report=report)
            finally:
                relay.shutdown()
                serving.join(timeout=5)
    except (ValueError, OSError, TimeoutError, subprocess.TimeoutExpired, importlib.metadata.PackageNotFoundError):
        result.update(exit_code=2, report=None)
    if len(json.dumps(result, ensure_ascii=True).encode("utf-8")) > MAX_REPORT:
        result.update(exit_code=2, report=None)
    if manifest.exists():
        atomic_write(target, result)


def main() -> None:
    OUTPUT.mkdir(exist_ok=True)
    # Backend owns global concurrency. Up to four active immutable manifests.
    with __import__("concurrent.futures", fromlist=["ThreadPoolExecutor"]).ThreadPoolExecutor(max_workers=4) as pool:
        active = {}
        while True:
            active = {k: v for k, v in active.items() if not v.done()}
            for path in sorted(INPUT.glob("*.job.json"))[:100]:
                if len(active) >= 4:
                    break
                if path not in active and not (OUTPUT / (path.name.removesuffix(".job.json") + ".result.json")).exists():
                    active[path] = pool.submit(process, path)
            time.sleep(0.25)


if __name__ == "__main__":
    main()

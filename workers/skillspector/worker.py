"""Offline-only SkillSpector bridge. No network endpoints, no arbitrary input paths."""
import hashlib
import json
import os
import pathlib
import subprocess
import time

INPUT = pathlib.Path("/work/in")
OUTPUT = pathlib.Path("/work/out")
MAX_REPORT = 1024 * 1024
MAX_ARCHIVE = 20 * 1024 * 1024
TIMEOUT_SECONDS = 180


def process(job_file: pathlib.Path) -> None:
    job_id = job_file.stem
    if len(job_id) != 64 or any(c not in "0123456789abcdef" for c in job_id):
        return
    zip_file = INPUT / (job_id + ".zip")
    output_file = OUTPUT / (job_id + ".json")
    if output_file.exists() or not zip_file.is_file():
        return
    result = {"sha256": "", "exit_code": 2, "report": None}
    try:
        if job_file.stat().st_size > 1024:
            raise ValueError("Invalid queue manifest")
        job = json.loads(job_file.read_text(encoding="utf-8"))
        digest = job.get("sha256")
        if job.get("id") != job_id or not isinstance(digest, str) or len(digest) != 64:
            raise ValueError("Invalid job identity")
        result["sha256"] = digest
        if zip_file.stat().st_size > MAX_ARCHIVE:
            raise ValueError("Oversized archive")
        with zip_file.open("rb") as handle:
            hash_value = hashlib.file_digest(handle, "sha256").hexdigest()
        if hash_value != digest:
            raise ValueError("Snapshot digest differs from queue manifest")
        completed = subprocess.run(
            ["skillspector", "scan", str(zip_file), "--no-llm",
             "--format", "json", "--fail-on-incomplete"],
            check=False, capture_output=True, text=True,
            timeout=TIMEOUT_SECONDS, cwd="/tmp", env={
                "PATH": os.environ.get("PATH", "/usr/local/bin:/usr/bin"),
                "HOME": "/tmp", "TMPDIR": "/tmp", "PYTHONDONTWRITEBYTECODE": "1"
            },
        )
        result["exit_code"] = completed.returncode
        if len(completed.stdout) > MAX_REPORT:
            raise ValueError("Oversized scanner output")
        if completed.returncode in (0, 1):
            result["report"] = json.loads(completed.stdout)
    except (ValueError, OSError, TimeoutError, subprocess.TimeoutExpired, json.JSONDecodeError):
        # Never publish a traceback or stderr: attacker-controlled skill text may appear there.
        result["exit_code"] = 2
    encoded = json.dumps(result, ensure_ascii=True, separators=(",", ":"))
    if len(encoded) > MAX_REPORT:
        encoded = json.dumps({"sha256": result["sha256"], "exit_code": 2, "report": None})
    # Write output only after finishing. An atomic rename avoids half-read report files.
    temporary = OUTPUT / (job_id + ".tmp")
    temporary.write_text(encoded, encoding="utf-8")
    os.replace(temporary, output_file)


def main() -> None:
    OUTPUT.mkdir(exist_ok=True)
    while True:
        for manifest in sorted(INPUT.glob("*.json"))[:16]:
            process(manifest)
        time.sleep(2)


if __name__ == "__main__":
    main()

#!/usr/bin/env bash
# Real Cargo and pip acceptance against localhost Packory, with native-client
# HTTP requests constrained to this one, newly started registry.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
TOKEN="$(openssl rand -hex 32)"
BASE="http://127.0.0.1:18081"
PID=""
cleanup() {
  result=$?
  trap - EXIT
  if [ "$result" -ne 0 ]; then
    echo "Packory native-client smoke failed; diagnostic server log:" >&2
    test ! -e "$WORK/server.log" || tail -n 100 "$WORK/server.log" >&2
  fi
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  rm -rf "$WORK"
  exit "$result"
}
trap cleanup EXIT

test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
command -v cargo >/dev/null
command -v python3 >/dev/null
command -v curl >/dev/null

PACKORY_DATA_DIRECTORY="$WORK/data" \
PACKORY_PUBLIC_BASE_URL="$BASE" \
PACKORY_ADMIN_TOKEN="$TOKEN" \
PACKORY_ANONYMOUS_READ=true \
ASPNETCORE_URLS="$BASE" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
PID="$!"
READY=false
for n in $(seq 1 60); do
  if curl -fsS "$BASE/health/ready" >/dev/null 2>&1; then READY=true; break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.2
done
if [ "$READY" != true ]; then exit 1; fi

# Cargo alternate registry: publish and resolve a no-dependencies crate.
export CARGO_HOME="$WORK/cargo-home"
export CARGO_REGISTRIES_PACKORY_TOKEN="$TOKEN"
mkdir -p "$CARGO_HOME" "$WORK/rust-package/src" "$WORK/rust-consumer/src"
cat >"$CARGO_HOME/config.toml" <<CARGO
[registries.packory]
index = "sparse+$BASE/cargo/index/"
CARGO
cat >"$WORK/rust-package/Cargo.toml" <<'CARGO'
[package]
name = "packory-smoke-lib"
version = "1.0.0"
edition = "2021"
license = "MIT"
description = "Temporary Packory Cargo acceptance artifact"
[lib]
path = "src/lib.rs"
CARGO
printf 'pub fn answer() -> u32 { 42 }\n' >"$WORK/rust-package/src/lib.rs"
(cd "$WORK/rust-package" && cargo publish --registry packory --allow-dirty --no-verify)
cat >"$WORK/rust-consumer/Cargo.toml" <<'CARGO'
[package]
name = "packory-smoke-client"
version = "1.0.0"
edition = "2021"
[dependencies]
packory-smoke-lib = { version = "1.0.0", registry = "packory" }
CARGO
cat >"$WORK/rust-consumer/src/main.rs" <<'RUST'
fn main() { assert_eq!(packory_smoke_lib::answer(), 42); }
RUST
(cd "$WORK/rust-consumer" && cargo run --quiet)

# Build two standards-compliant pure-Python wheels using only the Python stdlib.
# The top-level wheel declares a transitive dependency hosted by Packory.
python3 - "$WORK" <<'PY'
import base64
import hashlib
from pathlib import Path
import sys
import zipfile

root = Path(sys.argv[1])
for dist, module, dependency in [
    ("packory_smoke_dep", "packory_smoke_dep", ""),
    ("packory_smoke_main", "packory_smoke_main", "packory-smoke-dep (==1.0.0)")
]:
    wheel = root / f"{dist}-1.0.0-py3-none-any.whl"
    dist_info = f"{dist}-1.0.0.dist-info"
    code = "def answer():\n    return 42\n" if not dependency else (
        "def answer():\n    import packory_smoke_dep\n    return packory_smoke_dep.answer()\n"
    )
    metadata = f"Metadata-Version: 2.1\nName: {dist}\nVersion: 1.0.0\n"
    if dependency:
        metadata += f"Requires-Dist: {dependency}\n"
    contents = {
        f"{module}/__init__.py": code.encode(),
        f"{dist_info}/WHEEL": b"Wheel-Version: 1.0\nGenerator: packory-smoke\nRoot-Is-Purelib: true\nTag: py3-none-any\n",
        f"{dist_info}/METADATA": metadata.encode(),
    }
    record_lines = []
    for name, data in contents.items():
        digest = base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b"=").decode()
        record_lines.append(f"{name},sha256={digest},{len(data)}")
    record_name = f"{dist_info}/RECORD"
    record_lines.append(f"{record_name},,")
    with zipfile.ZipFile(wheel, "w", compression=zipfile.ZIP_DEFLATED) as z:
        for name, data in contents.items():
            z.writestr(name, data)
        z.writestr(record_name, "\n".join(record_lines) + "\n")
PY
for PACKAGE in dep main; do
  WHEEL="$WORK/packory_smoke_${PACKAGE}-1.0.0-py3-none-any.whl"
  DIGEST="$(sha256sum "$WHEEL" | cut -d ' ' -f1)"
  curl --fail --silent --show-error -X POST "$BASE/pypi/legacy/" \
    -H "Authorization: Bearer $TOKEN" \
    -F "name=packory_smoke_${PACKAGE}" \
    -F "version=1.0.0" \
    -F "sha256_digest=$DIGEST" \
    -F "content=@$WHEEL;type=application/octet-stream" >/dev/null
done
python3 -m pip install --disable-pip-version-check --no-cache-dir \
  --no-input --index-url "$BASE/pypi/simple/" --target "$WORK/py-installed" \
  'packory-smoke-main==1.0.0'
PYTHONPATH="$WORK/py-installed" python3 -c \
  "import packory_smoke_main; assert packory_smoke_main.answer() == 42"

printf 'Real hosted Cargo publish/run and pip transitive install passed.\n'

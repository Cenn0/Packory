#!/usr/bin/env bash
# E0: native Cargo and Twine/pip with distinct credentials, same coordinates,
# different bytes, fresh caches, negative auth cases and process restart.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
BASE="http://127.0.0.1:18096"
ADMIN="$(openssl rand -hex 32)"
PID=""
cleanup() {
  code=$?
  trap - EXIT
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  rm -rf "$WORK"
  exit "$code"
}
trap cleanup EXIT
for cmd in dotnet cargo rustc python3 curl openssl; do
  command -v "$cmd" >/dev/null || { echo "Missing $cmd" >&2; exit 1; }
done
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
cargo --version
python3 -m pip --version
# Tool bootstrap occurs before private fixture publication. Registry workloads
# below use only Packory. Keep the uploader separate from tested client caches.
python3 -m venv "$WORK/twine"
"$WORK/twine/bin/python" -m pip install --disable-pip-version-check --quiet 'twine==6.2.0' >"$WORK/twine-bootstrap.log" 2>&1 || {
  echo "Pinned Twine bootstrap failed" >&2; exit 1;
}
"$WORK/twine/bin/python" -m twine --version

start() {
  PACKORY_DATA_DIRECTORY="$WORK/data" PACKORY_PUBLIC_BASE_URL="$BASE" \
    PACKORY_ADMIN_TOKEN="$ADMIN" PACKORY_ANONYMOUS_READ=true ASPNETCORE_URLS="$BASE" \
    dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
  PID="$!"
  ready=false
  for attempt in $(seq 1 75); do
    if curl -fsS "$BASE/health/ready" >/dev/null 2>&1; then ready=true; break; fi
    if ! kill -0 "$PID" 2>/dev/null; then break; fi
    sleep 0.2
  done
  test "$ready" = true || { echo "Private ecosystem server readiness failed" >&2; exit 1; }
}
start
admin_post() {
  curl --fail --silent --show-error -H "Authorization: Bearer $ADMIN" \
    -H "Content-Type: application/json" --data-binary "$2" "$BASE$1"
}
grant() {
  admin_post /api/admin/tokens \
    "{\"role\":\"$1\",\"namespace\":\"feed:$2:$3\",\"expiresInHours\":1}"
}
for kind in cargo pypi; do
  for label in alpha beta; do
    feed="$kind-$label"
    admin_post /api/v1/admin/feeds/drafts \
      "{\"id\":\"$feed\",\"kind\":\"$kind\",\"mode\":\"hosted\",\"displayName\":\"Synthetic private ecosystem\"}" >/dev/null
    admin_post "/api/v1/admin/feeds/$feed/activate" '' >/dev/null
    grant write "$feed" publish >"$WORK/$feed-write.json"
    grant read "$feed" read >"$WORK/$feed-read.json"
  done
done
token() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["token"])' "$WORK/$1-$2.json"; }
status() { curl --silent --show-error -o "$WORK/response" -w '%{http_code}' "$@"; }

configure_cargo() {
  directory="$1"
  feed="$2"
  mkdir -p "$directory"
  cat >"$directory/config.toml" <<CARGO
[registry]
global-credential-providers = ["cargo:token"]
[registries.private]
index = "sparse+$BASE/feeds/$feed/cargo/index/"
credential-provider = "cargo:token"
CARGO
}
for label in alpha beta; do
  feed="cargo-$label"
  value=11
  test "$label" != beta || value=22
  package="$WORK/$feed-package"
  mkdir -p "$package/src"
  configure_cargo "$WORK/$feed-publish-home" "$feed"
  cat >"$package/Cargo.toml" <<'CARGO'
[package]
name = "packory-e0-private"
version = "1.0.0"
edition = "2021"
license = "MIT"
description = "Synthetic private feed acceptance crate"
[lib]
path = "src/lib.rs"
CARGO
  printf 'pub fn answer() -> u32 { %s }\n' "$value" >"$package/src/lib.rs"
  (cd "$package" && CARGO_HOME="$WORK/$feed-publish-home" \
    CARGO_REGISTRIES_PRIVATE_TOKEN="$(token "$feed" write)" \
    cargo publish --registry private --allow-dirty --no-verify) >"$WORK/$feed-publish.log" 2>&1 || {
      echo "Native private Cargo publication failed for $feed" >&2; exit 1;
    }
  mkdir -p "$WORK/$feed-consumer/src"
  cat >"$WORK/$feed-consumer/Cargo.toml" <<'CARGO'
[package]
name = "packory-e0-consumer"
version = "1.0.0"
edition = "2021"
[dependencies]
packory-e0-private = { version = "=1.0.0", registry = "private" }
CARGO
  printf 'fn main() { assert_eq!(packory_e0_private::answer(), %s); }\n' "$value" >"$WORK/$feed-consumer/src/main.rs"
done

# Build deterministic pure-Python wheels, including a feed-local dependency.
python3 - "$WORK" <<'PY'
import base64
import hashlib
from pathlib import Path
import sys
import zipfile

root = Path(sys.argv[1])
for label, value in [('alpha', 11), ('beta', 22)]:
    folder = root / ('pypi-' + label)
    folder.mkdir()
    for dist, dependency in [('packory_e0_dependency', ''), ('packory_e0_private', 'packory-e0-dependency (==1.0.0)')]:
        info = dist + '-1.0.0.dist-info'
        code = f'def answer():\n    return {value}\n' if not dependency else (
            'def answer():\n    import packory_e0_dependency\n    return packory_e0_dependency.answer()\n')
        metadata = f'Metadata-Version: 2.1\nName: {dist}\nVersion: 1.0.0\nRequires-Python: >=3.10\n'
        if dependency:
            metadata += 'Requires-Dist: ' + dependency + '\n'
        contents = {
            dist + '/__init__.py': code.encode(),
            info + '/METADATA': metadata.encode(),
            info + '/WHEEL': b'Wheel-Version: 1.0\nGenerator: packory-e0\nRoot-Is-Purelib: true\nTag: py3-none-any\n',
        }
        records = []
        for name, data in contents.items():
            digest = base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b'=').decode()
            records.append(f'{name},sha256={digest},{len(data)}')
        record_name = info + '/RECORD'
        records.append(record_name + ',,')
        with zipfile.ZipFile(folder / f'{dist}-1.0.0-py3-none-any.whl', 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            for name, data in contents.items():
                archive.writestr(name, data)
            archive.writestr(record_name, '\n'.join(records) + '\n')
PY
for label in alpha beta; do
  feed="pypi-$label"
  TWINE_NON_INTERACTIVE=1 TWINE_USERNAME=__token__ TWINE_PASSWORD="$(token "$feed" write)" \
    "$WORK/twine/bin/python" -m twine upload --disable-progress-bar \
    --repository-url "$BASE/feeds/$feed/pypi/legacy/" "$WORK/$feed/"*.whl >"$WORK/$feed-upload.log" 2>&1 || {
      echo "Native private Twine publication failed for $feed" >&2; exit 1;
    }
done

consume() {
  iteration="$1"
  for label in alpha beta; do
    value=11
    test "$label" != beta || value=22
    feed="cargo-$label"
    configure_cargo "$WORK/$feed-home-$iteration" "$feed"
    (cd "$WORK/$feed-consumer" && CARGO_HOME="$WORK/$feed-home-$iteration" \
      CARGO_TARGET_DIR="$WORK/$feed-target-$iteration" CARGO_REGISTRIES_PRIVATE_TOKEN="$(token "$feed" read)" \
      cargo run --quiet) >"$WORK/$feed-read-$iteration.log" 2>&1 || {
        echo "Native private Cargo consumption failed for $feed" >&2; exit 1;
      }
    feed="pypi-$label"
    # NETRC paths isolate same-host credentials without putting tokens in URLs.
    printf 'machine 127.0.0.1 login __token__ password %s\n' "$(token "$feed" read)" >"$WORK/$feed.netrc"
    chmod 600 "$WORK/$feed.netrc"
    NETRC="$WORK/$feed.netrc" PIP_CONFIG_FILE=/dev/null PIP_EXTRA_INDEX_URL='' \
      python3 -m pip install --disable-pip-version-check --no-cache-dir --no-input \
      --only-binary=:all: --index-url "$BASE/feeds/$feed/pypi/simple/" \
      --target "$WORK/$feed-installed-$iteration" 'packory-e0-private==1.0.0' >"$WORK/$feed-read-$iteration.log" 2>&1 || {
        echo "Native private pip consumption failed for $feed" >&2; exit 1;
      }
    PYTHONPATH="$WORK/$feed-installed-$iteration" python3 -c "import packory_e0_private; assert packory_e0_private.answer() == $value"
  done
}
consume cold
kill "$PID"
wait "$PID" || true
PID=""
start
consume restarted

# Native Cargo uses the new V14 feed-local API. The index flag changes
# without mutating or deleting the immutable crate. Another feed with the same
# crate coordinate remains unyanked.
cargo_yank() {
  action="$1"
  (cd "$WORK/cargo-alpha-package" && CARGO_HOME="$WORK/cargo-alpha-publish-home" \
    CARGO_REGISTRIES_PRIVATE_TOKEN="$(token cargo-alpha write)" \
    cargo yank --registry private --version 1.0.0 $action packory-e0-private) \
    >"$WORK/cargo-yank-$action.log" 2>&1 || {
      echo "Native Cargo yank operation failed" >&2; exit 1;
    }
}
assert_yanked() {
  feed="$1"
  credential="$(token "$feed" read)"
  expected="$2"
  curl --fail --silent --show-error -H "Authorization: $credential" \
    "$BASE/feeds/$feed/cargo/index/pa/ck/packory-e0-private" |
    python3 -c 'import json,sys; rows=[json.loads(row) for row in sys.stdin if row.strip()]; assert len(rows)==1; assert rows[0]["yanked"] is (sys.argv[1]=="true")' "$expected"
}
assert_yanked cargo-alpha false
assert_yanked cargo-beta false
test "$(status -X DELETE "$BASE/feeds/cargo-alpha/cargo/api/v1/crates/packory-e0-private/1.0.0/yank")" = 401
test "$(status -X DELETE -H "Authorization: $(token cargo-beta write)" "$BASE/feeds/cargo-alpha/cargo/api/v1/crates/packory-e0-private/1.0.0/yank")" = 403
cargo_yank ""
assert_yanked cargo-alpha true
assert_yanked cargo-beta false
cargo_yank "--undo"
assert_yanked cargo-alpha false

# A foreign valid credential cannot authorize private discovery or assets.
test "$(status "$BASE/feeds/cargo-alpha/cargo/index/config.json")" = 401
test "$(status -H "Authorization: $(token cargo-beta read)" "$BASE/feeds/cargo-alpha/cargo/index/config.json")" = 403
test "$(status "$BASE/feeds/pypi-alpha/pypi/simple/")" = 401
test "$(status -H "Authorization: Bearer $(token pypi-beta read)" "$BASE/feeds/pypi-alpha/pypi/simple/")" = 403
test "$(status "$BASE/cargo/index/pa/ck/packory-e0-private")" = 404
test "$(status "$BASE/pypi/simple/packory-e0-private/")" = 404

# Native clients also fail with fresh caches for 401, 403 and disabled-feed 404.
denied_cargo() {
  case_name="$1"
  credential="$2"
  configure_cargo "$WORK/cargo-denied-$case_name" cargo-alpha
  if (cd "$WORK/cargo-alpha-consumer" && CARGO_HOME="$WORK/cargo-denied-$case_name" \
    CARGO_TARGET_DIR="$WORK/cargo-denied-target-$case_name" CARGO_REGISTRIES_PRIVATE_TOKEN="$credential" \
    cargo fetch --locked) >"$WORK/cargo-denied-$case_name.log" 2>&1; then
    echo "Cargo unexpectedly accepted $case_name access" >&2; exit 1
  fi
}
denied_pip() {
  case_name="$1"
  credential_file="$2"
  if NETRC="$credential_file" PIP_CONFIG_FILE=/dev/null PIP_EXTRA_INDEX_URL='' \
    python3 -m pip install --disable-pip-version-check --no-cache-dir --no-input --only-binary=:all: \
    --index-url "$BASE/feeds/pypi-alpha/pypi/simple/" --target "$WORK/pip-denied-$case_name" \
    'packory-e0-private==1.0.0' >"$WORK/pip-denied-$case_name.log" 2>&1; then
    echo "pip unexpectedly accepted $case_name access" >&2; exit 1
  fi
}
denied_cargo anonymous ''
denied_cargo foreign "$(token cargo-beta read)"
denied_pip anonymous /dev/null
denied_pip foreign "$WORK/pypi-beta.netrc"
admin_post /api/v1/admin/feeds/cargo-alpha/deactivate '' >/dev/null
admin_post /api/v1/admin/feeds/pypi-alpha/deactivate '' >/dev/null
test "$(status -H "Authorization: $(token cargo-alpha read)" "$BASE/feeds/cargo-alpha/cargo/index/config.json")" = 404
test "$(status -H "Authorization: Bearer $(token pypi-alpha read)" "$BASE/feeds/pypi-alpha/pypi/simple/")" = 404
denied_cargo disabled "$(token cargo-alpha read)"
denied_pip disabled "$WORK/pypi-alpha.netrc"
echo 'E0 private Cargo/Twine/pip: independent same-coordinate contents, transitive Python dependency, clean-cache restart and fail-closed native authorization passed.'

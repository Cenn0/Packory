#!/usr/bin/env bash
# Real Go CLI acceptance: immutable multipart publish, full GOPROXY consumer,
# module compilation, fresh cache, checksum verification and restart persistence.
# All artifacts, credentials and paths are synthetic/ephemeral.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
BASE="http://127.0.0.1:18086"
TOKEN="$(openssl rand -hex 32)"
MODULE="example.com/packory-smoke"
VERSION="v1.2.3"
PID=""
cleanup() {
  status=$?
  trap - EXIT
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT
for command in go dotnet python3 curl cmp openssl; do
  command -v "$command" >/dev/null || { echo "Missing $command" >&2; exit 1; }
done
echo "Go client: $(go version)"
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"

start_server() {
  PACKORY_DATA_DIRECTORY="$WORK/data" \
    PACKORY_ADMIN_TOKEN="$TOKEN" \
    PACKORY_PUBLIC_BASE_URL="$BASE" \
    PACKORY_ANONYMOUS_READ=true \
    dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" \
    --urls "$BASE" >"$WORK/server.log" 2>&1 &
  PID="$!"
  for n in $(seq 1 90); do
    if curl --fail -fsS "$BASE/health/ready" >/dev/null 2>&1; then return; fi
    if ! kill -0 "$PID" 2>/dev/null; then
      echo "Packory process stopped before readiness" >&2
      grep -Ei 'exception|error|fail|crit' "$WORK/server.log" | tail -n 16 >&2 || true
      return 1
    fi
    sleep 1
  done
  echo "Packory did not become ready" >&2
  return 1
}
mkdir -p "$WORK/data" "$WORK/module" "$WORK/consumer"
cat >"$WORK/module/go.mod" <<'MOD'
module example.com/packory-smoke

go 1.23
MOD
python3 - "$WORK/module" <<'PY'
import pathlib
import sys
import zipfile

directory = pathlib.Path(sys.argv[1])
prefix = "example.com/packory-smoke@v1.2.3/"
entries = {
    prefix + "go.mod": (directory / "go.mod").read_bytes(),
    prefix + "hello/hello.go": b'package hello\nfunc Message() string { return "packory-go-ok" }\n',
}
with zipfile.ZipFile(directory / "module.zip", "w", compression=zipfile.ZIP_DEFLATED) as archive:
    for name, contents in entries.items():
        entry = zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0))
        entry.compress_type = zipfile.ZIP_DEFLATED
        entry.external_attr = 0o100644 << 16
        archive.writestr(entry, contents)
PY

start_server
code="$(curl -sS -o "$WORK/created.json" -w '%{http_code}' -X POST \
    -H "Authorization: Bearer $TOKEN" \
    -F "module=$MODULE" -F "version=$VERSION" \
    -F "time=2026-10-20T08:00:00Z" \
    -F "mod=@$WORK/module/go.mod;type=text/plain" \
    -F "zip=@$WORK/module/module.zip;type=application/zip" \
    "$BASE/api/go/modules")"
test "$code" = 201 || {
  echo "Go hosted publish failed (status $code)" >&2
  cat "$WORK/created.json" >&2
  exit 1
}
code="$(curl -sS -o /dev/null -w '%{http_code}' -X POST \
    -H "Authorization: Bearer $TOKEN" \
    -F "module=$MODULE" -F "version=$VERSION" \
    -F "mod=@$WORK/module/go.mod" -F "zip=@$WORK/module/module.zip" \
    "$BASE/api/go/modules")"
test "$code" = 409 || { echo "Go publish was not immutable (status $code)" >&2; exit 1; }
curl -fsS "$BASE/go/$MODULE/@v/$VERSION.zip" -o "$WORK/consumed.zip"
cmp "$WORK/consumed.zip" "$WORK/module/module.zip"
curl -fsS "$BASE/go/$MODULE/@v/$VERSION.mod" -o "$WORK/consumed.mod"
cmp "$WORK/consumed.mod" "$WORK/module/go.mod"
curl -fsS "$BASE/go/$MODULE/@v/$VERSION.info" -o "$WORK/consumed.info"
python3 - "$WORK/consumed.info" <<'PY'
import json
import pathlib
import sys
info = json.loads(pathlib.Path(sys.argv[1]).read_text())
assert info["Version"] == "v1.2.3"
assert info["Time"].startswith("2026-10-20T08:00:00")
PY
test "$(curl -fsS "$BASE/go/$MODULE/@v/list")" = "$VERSION"

cat >"$WORK/consumer/go.mod" <<'MOD'
module example.com/consumer

go 1.23

require example.com/packory-smoke v1.2.3
MOD
cat >"$WORK/consumer/main.go" <<'GO'
package main
import (
  "fmt"
  "example.com/packory-smoke/hello"
)
func main() { fmt.Println(hello.Message()) }
GO
go_consume() {
  cache="$1"
  (cd "$WORK/consumer" &&
    GOPROXY="$BASE/go" GOPRIVATE="example.com" GONOPROXY="none" \
    GOMODCACHE="$cache" GOTOOLCHAIN=local \
    go mod download "$MODULE@$VERSION" &&
    GOPROXY="$BASE/go" GOPRIVATE="example.com" GONOPROXY="none" \
    GOMODCACHE="$cache" GOTOOLCHAIN=local \
    go mod verify &&
    GOPROXY="$BASE/go" GOPRIVATE="example.com" GONOPROXY="none" \
    GOMODCACHE="$cache" GOTOOLCHAIN=local \
    go build -o "$WORK/client" ./...)
  test "$("$WORK/client")" = packory-go-ok
}
go_consume "$WORK/modcache1"
(
  cd "$WORK/consumer"
  GOPROXY="$BASE/go" GOPRIVATE="example.com" GONOPROXY="none" \
    GOMODCACHE="$WORK/latest-cache" GOTOOLCHAIN=local \
    go list -m -json "$MODULE@latest" >"$WORK/latest.json"
)
python3 - "$WORK/latest.json" <<'PY'
import json
import pathlib
import sys
assert json.loads(pathlib.Path(sys.argv[1]).read_text())["Version"] == "v1.2.3"
PY

kill "$PID"
wait "$PID" 2>/dev/null || true
PID=""
start_server
go_consume "$WORK/modcache2"
echo "Go hosted: native download, build, verify, @latest, immutable publish and restart passed"

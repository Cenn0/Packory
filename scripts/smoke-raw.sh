#!/usr/bin/env bash
# Real-process API smoke: experimental global immutable Raw/Generic endpoint.
# Loopback-only, synthetic input and short-lived credentials.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
TOKEN="$(openssl rand -hex 32)"
BASE="http://127.0.0.1:18084"
PID=""

cleanup() {
  status=$?
  trap - EXIT
  if [ "$status" -ne 0 ]; then
    echo "Raw native API smoke failed; server startup and errors:" >&2
    if [ -f "$WORK/server.log" ]; then
      grep -E 'error|fail|exception|crit' "$WORK/server.log" | tail -n 25 >&2 || true
    fi
  fi
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT

for command in dotnet curl openssl python3 sha256sum cmp; do command -v "$command" >/dev/null; done
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"

printf 'Packory Raw hosted integration fixture\n' >"$WORK/payload"
ORIGINAL_HASH="$(sha256sum "$WORK/payload" | cut -d' ' -f1)"
PACKORY_DATA_DIRECTORY="$WORK/data" \
PACKORY_PUBLIC_BASE_URL="$BASE" \
PACKORY_ADMIN_TOKEN="$TOKEN" \
PACKORY_ANONYMOUS_READ=false \
ASPNETCORE_URLS="$BASE" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
PID="$!"
ready=false
for n in $(seq 1 75); do
  if curl -fsS "$BASE/health/ready" >/dev/null 2>&1; then ready=true; break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.2
done
test "$ready" = true

test "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/raw/synthetic.asset/1.0.0")" = 401
test "$(curl -s -o /dev/null -w '%{http_code}' \
  -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/octet-stream" \
  --data-binary "@$WORK/payload" "$BASE/raw/synthetic.asset/1.0.0")" = 201
test "$(curl -s -o /dev/null -w '%{http_code}' \
  -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/octet-stream" \
  --data-binary "@$WORK/payload" "$BASE/raw/synthetic.asset/1.0.0")" = 409

curl -fsS -H "Authorization: Bearer $TOKEN" \
  "$BASE/raw/synthetic.asset/1.0.0" -o "$WORK/actual"
cmp "$WORK/payload" "$WORK/actual"
ACTUAL_HASH="$(sha256sum "$WORK/actual" | cut -d' ' -f1)"
test "$ORIGINAL_HASH" = "$ACTUAL_HASH"
curl -fsS -H "Authorization: Bearer $TOKEN" \
  "$BASE/raw/synthetic.asset/1.0.0/metadata" |
  python3 -c 'import json,sys; v=json.load(sys.stdin); assert v["name"]=="synthetic.asset" and v["version"]=="1.0.0" and len(v["sha256"])==64'

printf 'tampered-fixture\n' >"$WORK/data/blobs/raw/synthetic.asset/1.0.0/payload.blob"
test "$(curl -s -o /dev/null -w '%{http_code}' \
  -H "Authorization: Bearer $TOKEN" "$BASE/raw/synthetic.asset/1.0.0")" = 503
echo 'Raw real-process hosted publish/download/immutable/digest/reject-tamper smoke passed.'

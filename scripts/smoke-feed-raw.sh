#!/usr/bin/env bash
# E0 native HTTP client proof for separate private Raw feeds.
# Uses only synthetic data and disposable credentials; never prints tokens.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
BASE="http://127.0.0.1:18091"
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
for cmd in dotnet curl python3 openssl cmp; do
  command -v "$cmd" >/dev/null || { echo "Missing $cmd" >&2; exit 1; }
done
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
curl --version | sed -n '1p'
PACKORY_DATA_DIRECTORY="$WORK/data" \
PACKORY_PUBLIC_BASE_URL="$BASE" \
PACKORY_ADMIN_TOKEN="$ADMIN" \
PACKORY_ANONYMOUS_READ=true \
PACKORY_MAX_RAW_FEED_ARTIFACTS=1 \
ASPNETCORE_URLS="$BASE" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
PID="$!"
ready=false
for attempt in $(seq 1 75); do
  if curl -fsS "$BASE/health/ready" >/dev/null 2>&1; then ready=true; break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.2
done
test "$ready" = true || { echo "Raw feed server failed readiness" >&2; exit 1; }

admin_post() {
  curl --fail-with-body -sS -H "Authorization: Bearer $ADMIN" \
    -H "Content-Type: application/json" --data-binary "$2" "$BASE$1"
}
for feed in alpha-feed beta-feed; do
  admin_post "/api/v1/admin/feeds/drafts" \
    "{\"id\":\"$feed\",\"kind\":\"raw\",\"mode\":\"hosted\",\"displayName\":\"Synthetic Raw\"}" >/dev/null
  admin_post "/api/v1/admin/feeds/$feed/activate" '' >/dev/null
done
grant() {
  admin_post "/api/admin/tokens" \
    "{\"role\":\"$1\",\"namespace\":\"feed:$2:$3\",\"expiresInHours\":1}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])'
}
ALPHA_R="$(grant read alpha-feed read)"
ALPHA_W="$(grant write alpha-feed publish)"
BETA_R="$(grant read beta-feed read)"
BETA_W="$(grant write beta-feed publish)"
printf 'alpha private synthetic artifact\n' >"$WORK/alpha"
printf 'beta private synthetic artifact\n' >"$WORK/beta"

code() {
  curl -sS -o "$WORK/response" -w '%{http_code}' "$@"
}
A="$BASE/feeds/alpha-feed/raw/same.name/1.0.0"
B="$BASE/feeds/beta-feed/raw/same.name/1.0.0"
test "$(code -H "Authorization: Bearer $ALPHA_W" \
  -H "Content-Type: application/octet-stream" \
  --data-binary "@$WORK/alpha" -X PUT "$A")" = 201
test "$(code -H "Authorization: Bearer $BETA_W" \
  -H "Content-Type: application/octet-stream" \
  --data-binary "@$WORK/beta" -X PUT "$B")" = 201
test "$(code -H "Authorization: Bearer $ALPHA_W" \
  -H "Content-Type: application/octet-stream" \
  --data-binary "@$WORK/beta" -X PUT "$A")" = 409

test "$(code -H "Authorization: Bearer $ALPHA_W" \
  -H "Content-Type: application/octet-stream" \
  --data-binary "@$WORK/alpha" -X PUT "$BASE/feeds/alpha-feed/raw/over-quota/1.0.0")" = 507
curl --fail-with-body -sS -H "Authorization: Bearer $ALPHA_R" \
  "$BASE/feeds/alpha-feed/raw/usage" >"$WORK/usage.json"
python3 - "$WORK/usage.json" "$WORK/alpha" <<'CHECK'
import json,sys
from pathlib import Path
usage=json.loads(Path(sys.argv[1]).read_text())
assert usage['artifacts'] == 1 and usage['bytes'] == Path(sys.argv[2]).stat().st_size
assert usage['maxArtifacts'] == 1
CHECK

curl --fail-with-body -sS -H "Authorization: Bearer $ALPHA_R" "$A" -o "$WORK/a-read"
curl --fail-with-body -sS -H "Authorization: Bearer $BETA_R" "$B" -o "$WORK/b-read"
cmp "$WORK/alpha" "$WORK/a-read"
cmp "$WORK/beta" "$WORK/b-read"
test "$(code -H "Authorization: Bearer $ALPHA_R" "$B")" = 403
test "$(code -H "Authorization: Bearer $BETA_R" "$A")" = 403
test "$(code "$A")" = 401
test "$(code "$BASE/raw/same.name/1.0.0")" = 404
test "$(code -H "Authorization: Bearer $ALPHA_R" -I "$A")" = 200

admin_post "/api/v1/admin/feeds/alpha-feed/deactivate" '' >/dev/null
test "$(code -H "Authorization: Bearer $ALPHA_R" "$A")" = 404
test "$(code -H "Authorization: Bearer $BETA_R" "$B")" = 200
echo "E0 private Raw: two-feed immutable bytes, exact authorization, legacy non-leak, independent quotas and disabled-feed checks passed."

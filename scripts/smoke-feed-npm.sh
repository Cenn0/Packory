#!/usr/bin/env bash
# Native npm publish/install/ci through independently authorized hosted feeds.
# Packages, credentials, configuration, caches and processes are disposable.
set -euo pipefail
umask 077
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
BASE="http://127.0.0.1:18095"
ADMIN="$(openssl rand -hex 32)"
PID=""
cleanup() {
  code=$?
  trap - EXIT
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  chmod -R u+w "$WORK" 2>/dev/null || true
  rm -rf "$WORK"
  exit "$code"
}
trap cleanup EXIT
for command in dotnet npm node curl python3 openssl; do
  command -v "$command" >/dev/null || { echo "Missing required $command" >&2; exit 1; }
done
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
mkdir -p "$WORK/data"
: >"$WORK/empty.npmrc"
: >"$WORK/empty-user.npmrc"
start() {
  PACKORY_DATA_DIRECTORY="$WORK/data" \
  PACKORY_PUBLIC_BASE_URL="$BASE" \
  PACKORY_RESERVED_NPM_SCOPES="@packory-private" \
  PACKORY_ANONYMOUS_READ=true \
  PACKORY_ADMIN_TOKEN="$ADMIN" \
  ASPNETCORE_URLS="$BASE" \
  dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
  PID=$!
  for _ in $(seq 1 90); do
    if curl --noproxy 127.0.0.1 -fsS "$BASE/health/ready" >/dev/null 2>&1; then return; fi
    if ! kill -0 "$PID" 2>/dev/null; then break; fi
    sleep 0.2
  done
  echo "Private npm server failed readiness" >&2
  exit 1
}
start
admin_post() {
  curl --noproxy 127.0.0.1 --fail-with-body -sS \
    -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' \
    --data-binary "$2" "$BASE$1"
}
grant() {
  admin_post /api/admin/tokens \
    "{\"role\":\"$1\",\"namespace\":\"feed:$2:$3\",\"expiresInHours\":1}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])'
}
config() {
  printf 'registry=%s/feeds/%s/npm/\n//127.0.0.1:18095/feeds/%s/npm/:_authToken=%s\n' \
    "$BASE" "$1" "$1" "$2" >"$3"
}
for feed in npm-alpha npm-beta; do
  admin_post /api/v1/admin/feeds/drafts \
    "{\"id\":\"$feed\",\"kind\":\"npm\",\"mode\":\"hosted\",\"displayName\":\"Synthetic npm\"}" >/dev/null
  admin_post "/api/v1/admin/feeds/$feed/activate" '' >/dev/null
done
A_READ="$(grant read npm-alpha read)"
B_READ="$(grant read npm-beta read)"
A_WRITE="$(grant write npm-alpha publish)"
B_WRITE="$(grant write npm-beta publish)"
config npm-alpha "$A_READ" "$WORK/alpha-read.npmrc"
config npm-beta "$B_READ" "$WORK/beta-read.npmrc"
config npm-alpha "$A_WRITE" "$WORK/alpha-write.npmrc"
config npm-beta "$B_WRITE" "$WORK/beta-write.npmrc"
# Deliberately present beta's valid token on alpha's path: this must be 403.
config npm-alpha "$B_READ" "$WORK/wrong-feed.npmrc"
printf 'Private npm client: %s\n' "$(npm --version)"

for flavor in alpha beta; do
  feed="npm-$flavor"
  mkdir -p "$WORK/$flavor/dep" "$WORK/$flavor/demo" "$WORK/$flavor/consumer"
  cat >"$WORK/$flavor/dep/package.json" <<'JSON'
{"name":"@packory-private/dep","version":"1.0.0","main":"index.js","license":"UNLICENSED"}
JSON
  printf 'module.exports = "%s";\n' "$flavor" >"$WORK/$flavor/dep/index.js"
  cat >"$WORK/$flavor/demo/package.json" <<'JSON'
{"name":"@packory-private/demo","version":"1.0.0","main":"index.js","license":"UNLICENSED","dependencies":{"@packory-private/dep":"1.0.0"}}
JSON
  printf 'module.exports = require("@packory-private/dep");\n' >"$WORK/$flavor/demo/index.js"
  for package in dep demo; do
    (
      cd "$WORK/$flavor/$package"
      npm publish --registry="$BASE/feeds/$feed/npm/" \
        --userconfig="$WORK/$flavor-write.npmrc" --globalconfig="$WORK/empty.npmrc" \
        --cache="$WORK/$flavor/publish-cache" --access restricted --provenance=false \
        --ignore-scripts --fetch-retries=0 --loglevel error \
        >"$WORK/$flavor-$package-publish.log" 2>&1
    ) || { echo "Native npm publish failed for $feed/$package" >&2; exit 1; }
  done
  cat >"$WORK/$flavor/consumer/package.json" <<'JSON'
{"name":"packory-private-consumer","version":"1.0.0","private":true,"dependencies":{"@packory-private/demo":"1.0.0"}}
JSON
  (
    cd "$WORK/$flavor/consumer"
    npm install --registry="$BASE/feeds/$feed/npm/" \
      --userconfig="$WORK/$flavor-read.npmrc" --globalconfig="$WORK/empty.npmrc" \
      --cache="$WORK/$flavor/install-cache" --ignore-scripts --no-audit --no-fund \
      --fetch-retries=0 --loglevel error >"$WORK/$flavor-install.log" 2>&1
  ) || { echo "Native npm install failed for $feed" >&2; exit 1; }
  node -e 'if (require(process.argv[1]) !== process.argv[2]) process.exit(1)' \
    "$WORK/$flavor/consumer/node_modules/@packory-private/demo" "$flavor"
  node - "$WORK/$flavor/consumer/package-lock.json" "$BASE/feeds/$feed/npm/" <<'JS'
const fs = require('node:fs');
const lock = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const resolved = Object.values(lock.packages).filter(p => p.resolved).map(p => p.resolved);
if (resolved.length !== 2 || resolved.some(url => !url.startsWith(process.argv[3]))) {
  throw new Error('Private npm lockfile contains an unexpected package origin');
}
JS
done

# Real native clients, fresh cache per negative, and exactly one configured registry.
negative() {
  expected="$1"; configuration="$2"; label="$3"
  if npm view @packory-private/demo@1.0.0 version \
    --registry="$BASE/feeds/npm-alpha/npm/" \
    --userconfig="$configuration" --globalconfig="$WORK/empty.npmrc" \
    --cache="$WORK/negative-$label" --fetch-retries=0 --loglevel error \
    >"$WORK/negative-$label.log" 2>&1; then
    echo "Private npm $label unexpectedly succeeded" >&2; exit 1
  fi
  grep -q "E$expected" "$WORK/negative-$label.log" || {
    echo "Private npm $label did not return expected E$expected" >&2; exit 1;
  }
}
negative 401 "$WORK/empty-user.npmrc" unauthenticated
negative 403 "$WORK/wrong-feed.npmrc" cross-feed
if npm publish "$WORK/alpha/demo" --registry="$BASE/feeds/npm-alpha/npm/" \
  --userconfig="$WORK/alpha-write.npmrc" --globalconfig="$WORK/empty.npmrc" \
  --cache="$WORK/duplicate-cache" --access restricted --provenance=false \
  --ignore-scripts --fetch-retries=0 --loglevel error >"$WORK/duplicate.log" 2>&1; then
  echo 'Duplicate private npm publication unexpectedly succeeded' >&2; exit 1
fi
grep -Eq 'E409|EPUBLISHCONFLICT|previously published' "$WORK/duplicate.log"

# Prove committed bundles and private metadata remain usable after restart.
curl --noproxy 127.0.0.1 --fail -sS -X PUT \
  -H "Authorization: Bearer $A_WRITE" -H 'Content-Type: application/json' \
  --data-binary '{"message":"Use the next release"}' \
  "$BASE/feeds/npm-alpha/npm/-/deprecations/1.0.0/%40packory-private%2Fdemo" >/dev/null
kill "$PID"
wait "$PID" || true
PID=""
start
npm view @packory-private/demo@1.0.0 deprecated --json \
  --registry="$BASE/feeds/npm-alpha/npm/" --userconfig="$WORK/alpha-read.npmrc" \
  --globalconfig="$WORK/empty.npmrc" --cache="$WORK/deprecation-cache" \
  --fetch-retries=0 --loglevel error >"$WORK/deprecation.json"
python3 - "$WORK/deprecation.json" <<'PY'
import json,sys
assert json.load(open(sys.argv[1])) == "Use the next release"
PY
curl --noproxy 127.0.0.1 --fail -sS -X PUT \
  -H "Authorization: Bearer $A_WRITE" -H 'Content-Type: application/json' \
  --data-binary '{"message":""}' \
  "$BASE/feeds/npm-alpha/npm/-/deprecations/1.0.0/%40packory-private%2Fdemo" >/dev/null
for flavor in alpha beta; do
  (
    cd "$WORK/$flavor/consumer"
    npm ci --registry="$BASE/feeds/npm-$flavor/npm/" \
      --userconfig="$WORK/$flavor-read.npmrc" --globalconfig="$WORK/empty.npmrc" \
      --cache="$WORK/$flavor/restarted-cache" --ignore-scripts --no-audit --no-fund \
      --fetch-retries=0 --loglevel error >"$WORK/$flavor-ci.log" 2>&1
  ) || { echo "Native npm ci failed after restart for npm-$flavor" >&2; exit 1; }
  node -e 'if (require(process.argv[1]) !== process.argv[2]) process.exit(1)' \
    "$WORK/$flavor/consumer/node_modules/@packory-private/demo" "$flavor"
done
admin_post /api/v1/admin/feeds/npm-alpha/deactivate '' >/dev/null
negative 404 "$WORK/alpha-read.npmrc" disabled
curl --noproxy 127.0.0.1 -fsS -H "Authorization: Bearer $B_READ" \
  "$BASE/feeds/npm-beta/npm/@packory-private%2Fdemo" >/dev/null
echo 'E0 private npm: dual-feed publish, transitive install, restart ci, immutable versions and native 401/403/404 passed.'

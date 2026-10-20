#!/usr/bin/env bash
# E0 native Maven dependency resolution from separate authenticated hosted feeds.
# All credentials, metadata and payloads are synthetic and disposable.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
BASE="http://127.0.0.1:18092"
ADMIN="$(openssl rand -hex 32)"
PID=""
cleanup() {
  status=$?
  trap - EXIT
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT
for command in dotnet mvn java python3 openssl curl cmp; do
  command -v "$command" >/dev/null || { echo "Missing $command" >&2; exit 1; }
done
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
mvn -version | sed -n '1p'

# Prime the pinned public Maven dependency plugin before configuring the
# private repository. A native Maven consumer must not fetch the synthetic
# artifacts from a public fallback.
mkdir -p "$WORK/m2prime"
primed=false
for attempt in 1 2 3; do
  if mvn -B -q --no-transfer-progress -Dmaven.repo.local="$WORK/m2prime" \
    org.apache.maven.plugins:maven-dependency-plugin:3.7.1:help \
    >"$WORK/prime.log" 2>&1; then
    primed=true
    break
  fi
  if ! grep -Eiq 'Connection reset|Read timed out|Connection timed out|Temporary failure|Network is unreachable|502 Bad Gateway|503 Service Unavailable' "$WORK/prime.log"; then
    echo "Non-network Maven bootstrap failure" >&2
    exit 1
  fi
  if [ "$attempt" -lt 3 ]; then sleep "$attempt"; fi
done
test "$primed" = true || { echo "Maven plugin bootstrap unavailable" >&2; exit 1; }
cp -a "$WORK/m2prime" "$WORK/m2alpha"
cp -a "$WORK/m2prime" "$WORK/m2beta"

PACKORY_DATA_DIRECTORY="$WORK/data" \
PACKORY_PUBLIC_BASE_URL="$BASE" \
PACKORY_ADMIN_TOKEN="$ADMIN" \
PACKORY_ANONYMOUS_READ=true \
ASPNETCORE_URLS="$BASE" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
PID="$!"
ready=false
for attempt in $(seq 1 75); do
  if curl -fsS "$BASE/health/ready" >/dev/null 2>&1; then ready=true; break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.2
done
test "$ready" = true || { echo "Private Maven server unavailable" >&2; exit 1; }

admin_post() {
  curl --fail-with-body -sS -H "Authorization: Bearer $ADMIN" \
    -H "Content-Type: application/json" --data-binary "$2" "$BASE$1"
}
for feed in alpha-maven beta-maven; do
  admin_post "/api/v1/admin/feeds/drafts" \
    "{\"id\":\"$feed\",\"kind\":\"maven\",\"mode\":\"hosted\",\"displayName\":\"Synthetic Maven\"}" >/dev/null
  admin_post "/api/v1/admin/feeds/$feed/activate" '' >/dev/null
done
token() {
  admin_post "/api/admin/tokens" \
    "{\"role\":\"$1\",\"namespace\":\"feed:$2:$3\",\"expiresInHours\":1}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])'
}
ALPHA_READ="$(token read alpha-maven read)"
ALPHA_WRITE="$(token write alpha-maven publish)"
BETA_READ="$(token read beta-maven read)"
BETA_WRITE="$(token write beta-maven publish)"
mkdir -p "$WORK/payload"
cat >"$WORK/payload/sample-1.0.0.pom" <<'POM'
<project xmlns="http://maven.apache.org/POM/4.0.0">
<modelVersion>4.0.0</modelVersion>
<groupId>com.packory.isolated</groupId><artifactId>sample</artifactId>
<version>1.0.0</version><packaging>jar</packaging></project>
POM
python3 - "$WORK/payload" <<'PY'
import sys, zipfile
from pathlib import Path
for flavor in ("alpha", "beta"):
    output = Path(sys.argv[1]) / (flavor + "-sample.jar")
    with zipfile.ZipFile(output, "w") as archive:
        archive.writestr("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n")
        archive.writestr("private-feed-proof.txt", flavor + " isolated bytes\n")
PY

publish() {
  local feed="$1" grant="$2" artifact="$3" source="$4"
  curl --fail-with-body -sS -X PUT -H "Authorization: Bearer $grant" \
    -H "Content-Type: application/octet-stream" \
    --data-binary "@$source" \
    "$BASE/feeds/$feed/maven/com/packory/isolated/sample/1.0.0/sample-1.0.0.$artifact" >/dev/null
}
for feed in alpha beta; do
  case "$feed" in
    alpha) name=alpha-maven; writer="$ALPHA_WRITE" ;;
    beta) name=beta-maven; writer="$BETA_WRITE" ;;
  esac
  publish "$name" "$writer" pom "$WORK/payload/sample-1.0.0.pom"
  publish "$name" "$writer" jar "$WORK/payload/$feed-sample.jar"
done

# Maven matches server credentials by exact repository ID; no credentials
# are placed on URLs, environment logs, global settings or source control.
cat >"$WORK/settings.xml" <<XML
<settings xmlns="http://maven.apache.org/SETTINGS/1.2.0">
<servers>
<server><id>packory-alpha</id><username>synthetic</username><password>$ALPHA_READ</password></server>
<server><id>packory-beta</id><username>synthetic</username><password>$BETA_READ</password></server>
</servers>
</settings>
XML
for feed in alpha beta; do
  case "$feed" in
    alpha) name=alpha-maven; repo=packory-alpha ;;
    beta) name=beta-maven; repo=packory-beta ;;
  esac
  if ! mvn -B -q --no-transfer-progress -s "$WORK/settings.xml" \
    -Dmaven.repo.local="$WORK/m2$feed" \
    org.apache.maven.plugins:maven-dependency-plugin:3.7.1:get \
    -Dartifact=com.packory.isolated:sample:1.0.0 \
    "-DremoteRepositories=$repo::default::$BASE/feeds/$name/maven/" \
    -Dtransitive=false >"$WORK/resolve-$feed.log" 2>&1; then
    echo "Native Maven private $feed dependency resolve failed" >&2
    exit 1
  fi
  cmp "$WORK/payload/$feed-sample.jar" \
    "$WORK/m2$feed/com/packory/isolated/sample/1.0.0/sample-1.0.0.jar"
done

status="$(curl -sS -o /dev/null -w '%{http_code}' \
  -H "Authorization: Bearer $ALPHA_READ" \
  "$BASE/feeds/beta-maven/maven/com/packory/isolated/sample/1.0.0/sample-1.0.0.jar")"
test "$status" = 403
status="$(curl -sS -o /dev/null -w '%{http_code}' \
  "$BASE/maven/com/packory/isolated/sample/1.0.0/sample-1.0.0.jar")"
test "$status" = 404
echo "E0 Maven native CLI: separate authenticated feed resolution, byte parity and scope negatives passed."

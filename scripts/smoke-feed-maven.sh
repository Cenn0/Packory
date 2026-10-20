#!/usr/bin/env bash
# E0 private Maven acceptance with two same-coordinate feeds and native Maven 3.
# No native mvn deploy, SNAPSHOT, proxy or real-provider qualification is implied.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
BASE="http://127.0.0.1:18092"
ADMIN="$(openssl rand -hex 32)"
PID=""
cleanup() {
  code=$?
  trap - EXIT
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  if [ "$code" -ne 0 ]; then
    echo "Private Maven smoke failed; sanitized diagnostics follow" >&2
    for log in "$WORK"/maven-*.log; do
      test -f "$log" || continue
      grep -Ei 'error|fail|unable|Could not' "$log" | tail -n 10 >&2 || true
    done
  fi
  rm -rf "$WORK"
  exit "$code"
}
trap cleanup EXIT
for command in dotnet mvn java openssl curl cmp python3; do
  command -v "$command" >/dev/null || { echo "Missing required $command" >&2; exit 1; }
done
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
mkdir -p "$WORK/m2-prime" "$WORK/payload"
# Prime only the pinned public Maven plugin. Do not retry functional failures.
primed=false
for attempt in 1 2 3; do
  if mvn -B -q --no-transfer-progress -Dmaven.repo.local="$WORK/m2-prime" \
    org.apache.maven.plugins:maven-dependency-plugin:3.7.1:help \
    >"$WORK/maven-prime.log" 2>&1; then
    primed=true
    break
  fi
  if ! grep -Eiq 'Connection reset|Read timed out|Connection timed out|Temporary failure|Network is unreachable|502 Bad Gateway|503 Service Unavailable' "$WORK/maven-prime.log"; then
    echo "Maven bootstrap failed with a non-network error" >&2
    exit 1
  fi
  if test "$attempt" -lt 3; then sleep "$attempt"; fi
done
test "$primed" = true || { echo "Maven public plugin bootstrap unavailable" >&2; exit 1; }
PACKORY_DATA_DIRECTORY="$WORK/data" \
PACKORY_PUBLIC_BASE_URL="$BASE" \
PACKORY_ADMIN_TOKEN="$ADMIN" \
PACKORY_ANONYMOUS_READ=true \
ASPNETCORE_URLS="$BASE" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
PID="$!"
ready=false
for n in $(seq 1 75); do
  if curl -fsS "$BASE/health/ready" >/dev/null 2>&1; then ready=true; break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.2
done
test "$ready" = true || { echo "Private Maven Packory not ready" >&2; exit 1; }

admin_post() {
  curl --fail-with-body -sS -H "Authorization: Bearer $ADMIN" \
    -H "Content-Type: application/json" --data-binary "$2" "$BASE$1"
}
grant() {
  admin_post "/api/admin/tokens" \
    "{\"role\":\"$1\",\"namespace\":\"feed:$2:$3\",\"expiresInHours\":1}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])'
}
for feed in alpha-maven beta-maven; do
  admin_post "/api/v1/admin/feeds/drafts" \
    "{\"id\":\"$feed\",\"kind\":\"maven\",\"mode\":\"hosted\",\"displayName\":\"Synthetic Maven\"}" >/dev/null
  admin_post "/api/v1/admin/feeds/$feed/activate" '' >/dev/null
done
A_READ="$(grant read alpha-maven read)"
A_WRITE="$(grant write alpha-maven publish)"
B_READ="$(grant read beta-maven read)"
B_WRITE="$(grant write beta-maven publish)"
P="com/packory/e0/isolated/1.0.0/isolated-1.0.0"
cat >"$WORK/payload/isolated-1.0.0.pom" <<'POM'
<project xmlns="http://maven.apache.org/POM/4.0.0">
  <modelVersion>4.0.0</modelVersion>
  <groupId>com.packory.e0</groupId>
  <artifactId>isolated</artifactId>
  <version>1.0.0</version>
  <packaging>jar</packaging>
</project>
POM
python3 - "$WORK/payload/alpha.jar" "$WORK/payload/beta.jar" <<'PY'
import sys, zipfile
for index, destination in enumerate(sys.argv[1:]):
    with zipfile.ZipFile(destination, "w") as archive:
        archive.writestr("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n")
        archive.writestr("isolation-proof.txt", "alpha" if index == 0 else "beta")
PY
code() {
  curl -sS -o "$WORK/response" -w '%{http_code}' "$@"
}
for feed in alpha-maven beta-maven; do
  if [ "$feed" = alpha-maven ]; then WRITE="$A_WRITE"; JAR=alpha; else WRITE="$B_WRITE"; JAR=beta; fi
  for ext in pom jar; do
    if [ "$ext" = pom ]; then FILE="$WORK/payload/isolated-1.0.0.pom"; else FILE="$WORK/payload/$JAR.jar"; fi
    test "$(code -X PUT -H "Authorization: Bearer $WRITE" --data-binary "@$FILE" \
      "$BASE/feeds/$feed/maven/$P.$ext")" = 201
  done
done
A_URL="$BASE/feeds/alpha-maven/maven/$P.jar"
B_URL="$BASE/feeds/beta-maven/maven/$P.jar"
test "$(code "$A_URL")" = 401
test "$(code -H "Authorization: Bearer $B_READ" "$A_URL")" = 403
test "$(code -H "Authorization: Bearer $A_READ" "$B_URL")" = 403
test "$(code -H "Authorization: Bearer $A_READ" "$A_URL")" = 200
test "$(code "$BASE/maven/$P.jar")" = 404
test "$(code -H "Authorization: Bearer $A_WRITE" -X PUT --data-binary "@$WORK/payload/beta.jar" "$A_URL")" = 409

cat >"$WORK/settings.xml" <<SETTINGS
<settings xmlns="http://maven.apache.org/SETTINGS/1.0.0">
  <servers>
    <server><id>alpha-maven</id><username>packory</username><password>$A_READ</password></server>
    <server><id>beta-maven</id><username>packory</username><password>$B_READ</password></server>
  </servers>
</settings>
SETTINGS
for feed in alpha-maven beta-maven; do
  cp -a "$WORK/m2-prime" "$WORK/m2-$feed"
  if ! mvn -B -q --no-transfer-progress -s "$WORK/settings.xml" \
    -Dmaven.repo.local="$WORK/m2-$feed" \
    org.apache.maven.plugins:maven-dependency-plugin:3.7.1:get \
    -Dartifact=com.packory.e0:isolated:1.0.0 \
    "-DremoteRepositories=$feed::default::$BASE/feeds/$feed/maven/" \
    -Dtransitive=true >"$WORK/maven-$feed.log" 2>&1; then
    echo "Native Maven failed resolving $feed" >&2
    exit 1
  fi
  if [ "$feed" = alpha-maven ]; then EXPECT=alpha; else EXPECT=beta; fi
  cmp "$WORK/payload/$EXPECT.jar" \
    "$WORK/m2-$feed/com/packory/e0/isolated/1.0.0/isolated-1.0.0.jar"
  if ! grep -Fq "GET $BASE/feeds/$feed/maven/$P.jar" "$WORK/server.log"; then
    echo "Native Maven did not request isolated artifact from Packory" >&2
    exit 1
  fi
done
admin_post "/api/v1/admin/feeds/alpha-maven/deactivate" '' >/dev/null
test "$(code -H "Authorization: Bearer $A_READ" "$A_URL")" = 404
test "$(code -H "Authorization: Bearer $B_READ" "$B_URL")" = 200
echo "E0 private Maven: isolated native Maven 3 consumption and authorization passed."

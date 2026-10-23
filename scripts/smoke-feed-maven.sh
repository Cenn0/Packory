#!/usr/bin/env bash
# E0 private Maven acceptance with two same-coordinate feeds and native Maven 3.
# No native mvn deploy, SNAPSHOT, proxy or real-provider qualification is implied.
set -euo pipefail
umask 077
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/maven-bootstrap.sh"
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
mvn --version
mkdir -p "$WORK/m2-prime" "$WORK/payload"
# Prime only the pinned public Maven plugin, using the same repository ID that
# the isolated client will use later. Maven's local repository tracks that ID;
# changing the mirror URL after bootstrap does not require fetching tooling
# from a private package feed. No fixture coordinate exists during bootstrap.
cat >"$WORK/bootstrap-settings.xml" <<'SETTINGS'
<settings xmlns="http://maven.apache.org/SETTINGS/1.0.0">
  <mirrors><mirror><id>packory-fixture</id><mirrorOf>*</mirrorOf>
    <url>https://repo.maven.apache.org/maven2/</url>
  </mirror></mirrors>
</settings>
SETTINGS
# Maven does not read the standard HTTPS_PROXY environment itself. Honor an
# existing public-tooling proxy only during bootstrap; native fixture configs
# below contain no proxy or external repository URL.
python3 - "$WORK/bootstrap-settings.xml" <<'PY'
import os
from pathlib import Path
import sys
from urllib.parse import urlsplit
import xml.etree.ElementTree as ET

raw = os.environ.get('HTTPS_PROXY') or os.environ.get('https_proxy')
if raw:
    endpoint = urlsplit(raw)
    if endpoint.scheme not in ('http', 'https') or not endpoint.hostname or endpoint.path not in ('', '/') or endpoint.query or endpoint.fragment:
        raise SystemExit('Unsupported Maven tooling proxy configuration')
    namespace = 'http://maven.apache.org/SETTINGS/1.0.0'
    ET.register_namespace('', namespace)
    tree = ET.parse(sys.argv[1])
    proxies = ET.SubElement(tree.getroot(), '{' + namespace + '}proxies')
    proxy = ET.SubElement(proxies, '{' + namespace + '}proxy')
    values = {'id': 'tooling-bootstrap', 'active': 'true', 'protocol': endpoint.scheme,
              'host': endpoint.hostname, 'port': str(endpoint.port or (443 if endpoint.scheme == 'https' else 80)),
              'nonProxyHosts': '127.0.0.1|localhost|[::1]'}
    if endpoint.username is not None:
        values['username'] = endpoint.username
    if endpoint.password is not None:
        values['password'] = endpoint.password
    for key, value in values.items():
        ET.SubElement(proxy, '{' + namespace + '}' + key).text = value
    tree.write(sys.argv[1], encoding='utf-8', xml_declaration=True)
PY
# Only transient public tooling bootstrap failures are retried.
primed=false
for attempt in 1 2 3; do
  if mvn -U -B -q --no-transfer-progress -s "$WORK/bootstrap-settings.xml" \
    -Dmaven.repo.local="$WORK/m2-prime" \
    org.apache.maven.plugins:maven-dependency-plugin:3.7.1:help \
    >"$WORK/maven-prime.log" 2>&1; then
    primed=true
    break
  fi
  if ! packory_maven_transient "$WORK/maven-prime.log"; then
    echo "Maven bootstrap failed with a non-network error" >&2
    exit 1
  fi
  if test "$attempt" -lt 3; then packory_maven_backoff "$attempt"; fi
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

settings() {
  local destination="$1" feed="$2" credential="$3"
  cat >"$destination" <<SETTINGS
<settings xmlns="http://maven.apache.org/SETTINGS/1.0.0">
  <mirrors><mirror><id>packory-fixture</id><mirrorOf>*</mirrorOf>
    <url>$BASE/feeds/$feed/maven/</url>
  </mirror></mirrors>
SETTINGS
  if [ -n "$credential" ]; then
    cat >>"$destination" <<SETTINGS
  <servers><server><id>packory-fixture</id><username>packory</username>
    <password>$credential</password>
  </server></servers>
SETTINGS
  fi
  printf '</settings>\n' >>"$destination"
}

consume() {
  local label="$1" feed="$2" credential="$3" version="$4"
  cp -a "$WORK/m2-prime" "$WORK/m2-$label" || return 1
  test ! -e "$WORK/m2-$label/com/packory/e0" || return 1
  settings "$WORK/settings-$label.xml" "$feed" "$credential" || return 1
  # remoteRepositories alone would still allow Super-POM Central. The mirror
  # replaces every repository with this one feed, including transitive lookups.
  mvn -B -q --no-transfer-progress -s "$WORK/settings-$label.xml" \
    -Dmaven.repo.local="$WORK/m2-$label" \
    org.apache.maven.plugins:maven-dependency-plugin:3.7.1:get \
    "-Dartifact=com.packory.e0:isolated:$version" -Dtransitive=true \
    >"$WORK/maven-$label.log" 2>&1
}

native_rejection() {
  local label="$1" expected="$2" credential="$3" version="${4:-1.0.0}"
  if consume "negative-$label" alpha-maven "$credential" "$version"; then
    echo "Native Maven $label unexpectedly succeeded" >&2
    exit 1
  fi
  if [ "$expected" = 404 ]; then
    grep -Eq 'Could not find artifact|was not found in|404' "$WORK/maven-negative-$label.log"
  else
    grep -Eq "(^|[^[:digit:]])$expected([^[:digit:]]|$)" "$WORK/maven-negative-$label.log"
  fi
  # Maven may translate an HTTP 404 into ArtifactNotFoundException without
  # printing the status. Verify the exact fixture's status independently too.
  local target="$BASE/feeds/alpha-maven/maven/com/packory/e0/isolated/$version/isolated-$version.jar"
  if [ -n "$credential" ]; then
    test "$(code -H "Authorization: Bearer $credential" "$target")" = "$expected"
  else
    test "$(code "$target")" = "$expected"
  fi
}

for feed in alpha-maven beta-maven; do
  if [ "$feed" = alpha-maven ]; then READ="$A_READ"; EXPECT=alpha; else READ="$B_READ"; EXPECT=beta; fi
  if ! consume "$feed" "$feed" "$READ" 1.0.0; then
    echo "Native Maven failed resolving $feed" >&2
    exit 1
  fi
  cmp "$WORK/payload/$EXPECT.jar" \
    "$WORK/m2-$feed/com/packory/e0/isolated/1.0.0/isolated-1.0.0.jar"
  if ! grep -Fq "GET $BASE/feeds/$feed/maven/$P.jar" "$WORK/server.log"; then
    echo "Native Maven did not request isolated artifact from Packory" >&2
    exit 1
  fi
done
native_rejection unauthenticated 401 ''
native_rejection foreign-feed 403 "$B_READ"
native_rejection missing-version 404 "$A_READ" 9.9.9
admin_post "/api/v1/admin/feeds/alpha-maven/deactivate" '' >/dev/null
native_rejection deactivated-feed 404 "$A_READ"
test "$(code -H "Authorization: Bearer $B_READ" "$B_URL")" = 200
echo "E0 private Maven: feed-only native consumption, independent bytes and fresh-cache native 401/403/404 passed."

#!/usr/bin/env bash
# Native Apache Maven consumer acceptance for the experimental hosted RELEASE-only adapter.
# Offline-generated source fixtures; Maven plugin download may use Maven Central.
# No automatic arbitrary upstream proxy, snapshots or Maven deploy-plugin claims.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
TOKEN="$(openssl rand -hex 32)"
BASE="http://127.0.0.1:18085"
PID=""

cleanup() {
  status=$?
  trap - EXIT
  if [ "$status" -ne 0 ]; then
    echo "Maven release consumer smoke failed; sanitized recent server errors:" >&2
    if [ -f "$WORK/server.log" ]; then
      grep -E 'error|fail|exception|crit' "$WORK/server.log" | tail -n 25 >&2 || true
    fi
  fi
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT

for cmd in dotnet mvn curl openssl jar sha256sum cmp python3; do command -v "$cmd" >/dev/null; done
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
mvn -version | head -n 3

mkdir -p "$WORK/assets"
printf 'synthetic-java-dependency\n' > "$WORK/assets/sentinel.txt"
jar --create --file "$WORK/dependency.jar" -C "$WORK/assets" .
printf 'synthetic-java-consumer\n' > "$WORK/assets/sentinel.txt"
jar --create --file "$WORK/consumer.jar" -C "$WORK/assets" .

cat >"$WORK/dependency.pom" <<'POM'
<project xmlns="http://maven.apache.org/POM/4.0.0">
  <modelVersion>4.0.0</modelVersion>
  <groupId>com.packory</groupId>
  <artifactId>dependency</artifactId>
  <version>1.0.0</version>
  <packaging>jar</packaging>
</project>
POM
cat >"$WORK/consumer.pom" <<'POM'
<project xmlns="http://maven.apache.org/POM/4.0.0">
  <modelVersion>4.0.0</modelVersion>
  <groupId>com.packory</groupId>
  <artifactId>consumer</artifactId>
  <version>1.0.0</version>
  <packaging>jar</packaging>
  <dependencies>
    <dependency>
      <groupId>com.packory</groupId>
      <artifactId>dependency</artifactId>
      <version>1.0.0</version>
    </dependency>
  </dependencies>
</project>
POM

PACKORY_DATA_DIRECTORY="$WORK/packory" \
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

for name in dependency consumer; do
  path="$BASE/maven/com/packory/$name/1.0.0/$name-1.0.0"
  test "$(curl -s -o /dev/null -w '%{http_code}' \
    --request PUT -H "Authorization: Bearer $TOKEN" \
    --data-binary "@$WORK/$name.pom" "$path.pom")" = "201"
  test "$(curl -s -o /dev/null -w '%{http_code}' \
    --request PUT -H "Authorization: Bearer $TOKEN" \
    --data-binary "@$WORK/$name.jar" "$path.jar")" = "201"
done

# The Maven native client is authenticated with a temporary settings.xml.
# Client/plugin dependencies may come from Maven Central, but our synthetic
# package coordinates can only be fetched from Packory.
cat >"$WORK/settings.xml" <<SETTINGS
<settings xmlns="http://maven.apache.org/SETTINGS/1.2.0">
  <servers>
    <server><id>packory</id><username>packory</username><password>$TOKEN</password></server>
  </servers>
</settings>
SETTINGS
chmod 600 "$WORK/settings.xml"

mvn -B -ntp -q -s "$WORK/settings.xml" \
  -Dmaven.repo.local="$WORK/maven-cache" \
  org.apache.maven.plugins:maven-dependency-plugin:3.9.0:get \
  -Dartifact=com.packory:consumer:1.0.0 \
  -DremoteRepositories="packory::default::$BASE/maven" \
  -Dtransitive=true

cmp "$WORK/consumer.jar" "$WORK/maven-cache/com/packory/consumer/1.0.0/consumer-1.0.0.jar"
cmp "$WORK/dependency.jar" "$WORK/maven-cache/com/packory/dependency/1.0.0/dependency-1.0.0.jar"

curl -fsS -H "Authorization: Bearer $TOKEN" \
  "$BASE/maven/com/packory/consumer/maven-metadata.xml" |
  python3 -c 'import sys,xml.etree.ElementTree as e; r=e.parse(sys.stdin).getroot(); assert r.findtext("artifactId") == "consumer" and r.findtext("versioning/versions/version") == "1.0.0"'
test "$(curl -s -o /dev/null -w '%{http_code}' \
  "$BASE/maven/com/packory/consumer/1.0.0/consumer-1.0.0.jar")" = "401"
echo "Native Maven 3 hosted immutable release and transitive dependency resolution passed."

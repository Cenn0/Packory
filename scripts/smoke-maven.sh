#!/usr/bin/env bash
# Bounded native Maven 3 release-consumer acceptance.
# Hosted bytes are uploaded using Packory PUT, NOT the native Maven deploy plugin.
# A native Maven deploy would also PUT mutable maven-metadata.xml: still unsupported.
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
    echo "Maven hosted consumption smoke failed. Sanitized server errors:" >&2
    if test -f "$WORK/server.log"; then
      grep -Ei 'exception|error|fail|crit' "$WORK/server.log" | tail -n 25 >&2 || true
    fi
  fi
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT

for command in dotnet mvn java python3 openssl curl cmp; do
  command -v "$command" >/dev/null || { echo "Missing required $command" >&2; exit 1; }
done
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"

# Resolve the pinned Maven consumer plugin before enabling a synthetic isolated mirror.
# The plugin itself is downloaded from the normal public Maven distribution origin.
mkdir -p "$WORK/m2"
mvn -B -q -Dmaven.repo.local="$WORK/m2" \
  org.apache.maven.plugins:maven-dependency-plugin:3.7.1:help >/dev/null

mkdir -p "$WORK/payload"
cat >"$WORK/payload/sample-1.0.0.pom" <<'POM'
<project xmlns="http://maven.apache.org/POM/4.0.0">
  <modelVersion>4.0.0</modelVersion>
  <groupId>com.packory.smoke</groupId>
  <artifactId>sample</artifactId>
  <version>1.0.0</version>
  <packaging>jar</packaging>
</project>
POM
python3 - "$WORK/payload/sample-1.0.0.jar" <<'PY'
import sys
import zipfile
with zipfile.ZipFile(sys.argv[1], "w") as archive:
    archive.writestr("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\n")
    archive.writestr("packory-proof.txt", "synthetic immutable Maven acceptance\n")
PY

PACKORY_DATA_DIRECTORY="$WORK/data" \
PACKORY_PUBLIC_BASE_URL="$BASE" \
PACKORY_ADMIN_TOKEN="$TOKEN" \
PACKORY_ANONYMOUS_READ=true \
ASPNETCORE_URLS="$BASE" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
PID="$!"
READY=false
for n in $(seq 1 75); do
  if curl -fsS "$BASE/health/ready" >/dev/null 2>&1; then READY=true; break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.2
done
test "$READY" = true
for extension in pom jar; do
  curl --fail-with-body -sS -X PUT \
    -H "Authorization: Bearer $TOKEN" \
    -H "Content-Type: application/octet-stream" \
    --data-binary "@$WORK/payload/sample-1.0.0.$extension" \
    "$BASE/maven/com/packory/smoke/sample/1.0.0/sample-1.0.0.$extension" >/dev/null
done

# Mirror all Maven repositories to Packory for the actual dependency resolution.
# Plug-in binaries have been primed in the isolated local Maven cache above.
cat >"$WORK/settings.xml" <<XML
<settings xmlns="http://maven.apache.org/SETTINGS/1.2.0">
  <mirrors>
    <mirror>
      <id>packory-test</id>
      <mirrorOf>*</mirrorOf>
      <url>$BASE/maven/</url>
    </mirror>
  </mirrors>
</settings>
XML
mvn -B -q --settings "$WORK/settings.xml" \
  -Dmaven.repo.local="$WORK/m2" \
  org.apache.maven.plugins:maven-dependency-plugin:3.7.1:get \
  -Dartifact=com.packory.smoke:sample:1.0.0 \
  -Dtransitive=false >/dev/null

cmp "$WORK/payload/sample-1.0.0.jar" \
  "$WORK/m2/com/packory/smoke/sample/1.0.0/sample-1.0.0.jar"
echo "Native Maven 3 release consumer installed the Packory-hosted synthetic artifact."

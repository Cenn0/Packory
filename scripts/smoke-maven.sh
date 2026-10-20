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
mvn -version | sed -n '1p'

# Resolve the pinned Maven consumer plugin before enabling a synthetic isolated mirror.
# The plugin itself is downloaded from the normal public Maven distribution origin.
mkdir -p "$WORK/m2"
echo "Maven native smoke: priming pinned dependency consumer"
# Maven Central may reset a short-lived transfer. Retry ONLY recognizable
# upstream transport faults, never plugin/build/validation failures.
primed=false
for attempt in 1 2 3; do
  if mvn -B -q --no-transfer-progress -Dmaven.repo.local="$WORK/m2" \
    org.apache.maven.plugins:maven-dependency-plugin:3.7.1:help \
    >"$WORK/maven-prime.log" 2>&1; then
    primed=true
    break
  fi
  if ! grep -Eiq 'Connection reset|Read timed out|Connection timed out|Temporary failure|Network is unreachable|502 Bad Gateway|503 Service Unavailable' \
    "$WORK/maven-prime.log"; then
    echo "Maven plugin priming failed with a non-network error; no retry" >&2
    grep -Ei 'error|fail|exception' "$WORK/maven-prime.log" | tail -n 12 >&2 || true
    exit 1
  fi
  if test "$attempt" -lt 3; then
    echo "Maven Central transient transfer failure; retry attempt $((attempt + 1))/3" >&2
    sleep "$attempt"
  fi
done
if [ "$primed" != true ]; then
  echo "Maven Central unavailable after bounded plugin bootstrap retries" >&2
  grep -Ei 'error|fail|exception' "$WORK/maven-prime.log" | tail -n 12 >&2 || true
  exit 1
fi
echo "Maven native smoke: plugin available"

mkdir -p "$WORK/payload"
cat >"$WORK/payload/sample-1.0.0.pom" <<'POM'
<project xmlns="http://maven.apache.org/POM/4.0.0">
  <modelVersion>4.0.0</modelVersion>
  <groupId>com.packory.smoke</groupId>
  <artifactId>sample</artifactId>
  <version>1.0.0</version>
  <packaging>jar</packaging>
  <dependencies>
    <dependency>
      <groupId>com.packory.smoke</groupId>
      <artifactId>helper</artifactId>
      <version>1.0.0</version>
    </dependency>
  </dependencies>
</project>
POM
cat >"$WORK/payload/helper-1.0.0.pom" <<'POM'
<project xmlns="http://maven.apache.org/POM/4.0.0">
  <modelVersion>4.0.0</modelVersion>
  <groupId>com.packory.smoke</groupId>
  <artifactId>helper</artifactId>
  <version>1.0.0</version>
  <packaging>jar</packaging>
</project>
POM
python3 - "$WORK/payload/sample-1.0.0.jar" "$WORK/payload/helper-1.0.0.jar" <<'PY'
import sys
import zipfile
for path in sys.argv[1:]:
    with zipfile.ZipFile(path, "w") as archive:
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
if [ "$READY" != true ]; then echo "Maven smoke server failed readiness" >&2; exit 1; fi
echo "Maven native smoke: Packory ready"
for artifact in sample helper; do
  for extension in pom jar; do
    curl --fail-with-body -sS -X PUT \
      -H "Authorization: Bearer $TOKEN" \
      -H "Content-Type: application/octet-stream" \
      --data-binary "@$WORK/payload/$artifact-1.0.0.$extension" \
      "$BASE/maven/com/packory/smoke/$artifact/1.0.0/$artifact-1.0.0.$extension" >/dev/null
  done
done
echo "Maven native smoke: two POMs and JARs published"

# Maven itself needs its plugin and plugin dependency resolution through its
# regular public Maven repository. Only the synthetic dependency coordinates are
# resolved from Packory, with the exact request proven from server-side HTTP logs.
mvn -B -Dmaven.repo.local="$WORK/m2" \
  org.apache.maven.plugins:maven-dependency-plugin:3.7.1:get \
  -Dartifact=com.packory.smoke:sample:1.0.0 \
  "-DremoteRepositories=packory-test::default::$BASE/maven/" \
  -Dtransitive=true >"$WORK/maven-resolve.log" 2>&1 || {
    echo "Native Maven artifact resolution failed" >&2
    grep -Ei 'error|fail|unable|Could not|Downloading' "$WORK/maven-resolve.log" | tail -n 20 >&2 || true
    exit 1
  }
# Fail if the client obtained its artifact from any source other than Packory.
if ! grep -Eq 'GET http://127\.0\.0\.1:18085/maven/com/packory/smoke/sample/1\.0\.0/sample-1\.0\.0\.jar' "$WORK/server.log"; then
  echo "Maven did not request the hosted JAR from Packory" >&2
  exit 1
fi
if ! grep -Eq 'GET http://127\.0\.0\.1:18085/maven/com/packory/smoke/helper/1\.0\.0/helper-1\.0\.0\.jar' "$WORK/server.log"; then
  echo "Maven did not request the transitive hosted JAR from Packory" >&2
  exit 1
fi
echo "Maven native smoke: direct and transitive dependencies resolved"

if ! cmp "$WORK/payload/sample-1.0.0.jar" \
  "$WORK/m2/com/packory/smoke/sample/1.0.0/sample-1.0.0.jar"; then
  echo "Native Maven cache did not contain expected synthetic JAR bytes" >&2
  exit 1
fi
cmp "$WORK/payload/helper-1.0.0.jar" \
  "$WORK/m2/com/packory/smoke/helper/1.0.0/helper-1.0.0.jar"
echo "Native Maven 3 release consumer installed Packory-hosted direct and transitive artifacts."

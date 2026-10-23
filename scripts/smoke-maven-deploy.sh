#!/usr/bin/env bash
# Bounded E2 native Maven deployment and Maven/Gradle dependency acceptance.
# Public tooling is bootstrapped before fixture creation. Every fixture lookup
# then uses exactly one Packory repository, including transitive dependencies.
set +x
set -euo pipefail
umask 077
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/maven-bootstrap.sh"
WORK="$(mktemp -d)"
BASE="http://127.0.0.1:18103"
TOOLS="${PACKORY_MAVEN_TOOL_CACHE:-$WORK/tools}"
PRIME="$TOOLS/m2-prime"
MAVEN_VERSION=3.9.11
MAVEN_SHA512=bcfe4fe305c962ace56ac7b5fc7a08b87d5abd8b7e89027ab251069faebee516b0ded8961445d6d91ec1985dfe30f8153268843c89aa392733d1a3ec956c9978
GRADLE_VERSION=9.8.2
# Official https://gradle.org/release-checksums/ (9.8.2 binary distribution).
GRADLE_SHA256=21bd5da2de49b642904077f38c0efdc7778e92b3ed0889392dca61b97e389607
ADMIN=""
PID=""
cleanup() {
  local status=$?
  trap - EXIT
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  if [ "$status" -ne 0 ]; then
    echo "Native Maven/Gradle acceptance failed; sanitized diagnostics follow" >&2
    python3 - "$WORK" <<'PY' || true
from pathlib import Path
import re, sys
root = Path(sys.argv[1])
secrets = (root / 'redactions').read_text().splitlines() if (root / 'redactions').exists() else []
for path in sorted(root.glob('*.log')):
    lines = [line for line in path.read_text(errors='replace').splitlines()
             if re.search(r'error|exception|fail|could not|not found|unauthorized|forbidden', line, re.I)]
    if not lines:
        continue
    print(path.name, file=sys.stderr)
    for line in lines[-8:]:
        for secret in secrets:
            if secret:
                line = line.replace(secret, '[redacted]')
        line = re.sub(r'(?i)(authorization|password|token)\s*[:=]\s*\S+', r'\1=[redacted]', line)
        print(line, file=sys.stderr)
PY
  fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT
for command in dotnet java javac python3 openssl curl cmp timeout grep; do
  command -v "$command" >/dev/null || { echo "Missing required $command" >&2; exit 1; }
done
mkdir -p "$TOOLS" "$PRIME" "$WORK/home" "$WORK/payload"
export GRADLE_USER_HOME="$WORK/gradle-home"
export MAVEN_SKIP_RC=true

download_tool() {
  local name="$1" url="$2" algorithm="$3" digest="$4"
  if [ ! -f "$TOOLS/$name" ]; then
    curl --fail --silent --show-error --location --retry 2 --connect-timeout 20 \
      --max-time 300 --output "$TOOLS/$name.part" "$url"
    mv "$TOOLS/$name.part" "$TOOLS/$name"
  fi
  python3 - "$TOOLS/$name" "$algorithm" "$digest" <<'PY'
from pathlib import Path
import hashlib, sys, tarfile, zipfile
path, algorithm, expected = Path(sys.argv[1]), sys.argv[2], sys.argv[3]
with path.open('rb') as stream:
    actual = hashlib.file_digest(stream, algorithm).hexdigest()
if actual != expected:
    raise SystemExit('Native tooling checksum mismatch: ' + path.name)
root = path.parent.resolve()
if path.suffix == '.zip':
    with zipfile.ZipFile(path) as archive:
        if any(not (root / item.filename).resolve().is_relative_to(root) for item in archive.infolist()):
            raise SystemExit('Unsafe native tooling archive')
        archive.extractall(root)
else:
    with tarfile.open(path) as archive:
        archive.extractall(root, filter='data')
PY
}
download_tool "apache-maven-$MAVEN_VERSION-bin.tar.gz" \
  "https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/$MAVEN_VERSION/apache-maven-$MAVEN_VERSION-bin.tar.gz" \
  sha512 "$MAVEN_SHA512"
download_tool "gradle-$GRADLE_VERSION-bin.zip" \
  "https://services.gradle.org/distributions/gradle-$GRADLE_VERSION-bin.zip" \
  sha256 "$GRADLE_SHA256"
MVN="$TOOLS/apache-maven-$MAVEN_VERSION/bin/mvn"
GRADLE="$TOOLS/gradle-$GRADLE_VERSION/bin/gradle"
chmod u+x "$GRADLE"
"$MVN" --version | sed -n '1p'
"$GRADLE" --version | sed -n '/^Gradle /p'
java -version 2>&1 | sed -n '1p'

# The same mirror ID is used for public tooling bootstrap and private fixture
# resolution. Maven records repository IDs in its local cache, not only URLs.
cat >"$WORK/bootstrap-settings.xml" <<'XML'
<settings xmlns="http://maven.apache.org/SETTINGS/1.0.0">
  <mirrors><mirror><id>packory-fixture</id><mirrorOf>*</mirrorOf>
    <url>https://repo.maven.apache.org/maven2/</url>
  </mirror></mirrors>
</settings>
XML
python3 - "$WORK/bootstrap-settings.xml" <<'PY'
import os, sys, xml.etree.ElementTree as ET
from urllib.parse import urlsplit, unquote
raw = os.environ.get('HTTPS_PROXY') or os.environ.get('https_proxy')
if raw:
    endpoint = urlsplit(raw)
    if endpoint.scheme not in ('http', 'https') or not endpoint.hostname or endpoint.path not in ('', '/') or endpoint.query or endpoint.fragment:
        raise SystemExit('Unsupported tooling proxy configuration')
    ns = 'http://maven.apache.org/SETTINGS/1.0.0'
    ET.register_namespace('', ns)
    tree = ET.parse(sys.argv[1])
    proxy = ET.SubElement(ET.SubElement(tree.getroot(), '{' + ns + '}proxies'), '{' + ns + '}proxy')
    values = {'id': 'tooling-bootstrap', 'active': 'true', 'protocol': endpoint.scheme,
              'host': endpoint.hostname, 'port': str(endpoint.port or (443 if endpoint.scheme == 'https' else 80)),
              'nonProxyHosts': '127.0.0.1|localhost|[::1]'}
    if endpoint.username is not None: values['username'] = unquote(endpoint.username)
    if endpoint.password is not None: values['password'] = unquote(endpoint.password)
    for key, value in values.items(): ET.SubElement(proxy, '{' + ns + '}' + key).text = value
    tree.write(sys.argv[1], encoding='utf-8', xml_declaration=True)
PY

create_project() {
  local directory="$1" artifact="$2" version="$3" group="$4"
  mkdir -p "$directory/src/main/java/com/packory/e2"
  cat >"$directory/pom.xml" <<XML
<project xmlns="http://maven.apache.org/POM/4.0.0">
  <modelVersion>4.0.0</modelVersion>
  <groupId>$group</groupId><artifactId>$artifact</artifactId><version>$version</version>
  <properties><maven.compiler.release>17</maven.compiler.release><project.build.sourceEncoding>UTF-8</project.build.sourceEncoding></properties>
  <build><plugins>
    <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-clean-plugin</artifactId><version>3.4.0</version></plugin>
    <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-resources-plugin</artifactId><version>3.3.1</version></plugin>
    <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-compiler-plugin</artifactId><version>3.13.0</version></plugin>
    <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-surefire-plugin</artifactId><version>3.5.2</version></plugin>
    <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-jar-plugin</artifactId><version>3.4.2</version></plugin>
    <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-install-plugin</artifactId><version>3.1.3</version></plugin>
    <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-deploy-plugin</artifactId><version>3.1.4</version></plugin>
    <plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-source-plugin</artifactId><version>3.3.1</version>
      <executions><execution><id>sources</id><goals><goal>jar-no-fork</goal></goals></execution></executions>
    </plugin>
  </plugins></build>
</project>
XML
  if [ "$artifact" = sample ]; then
    python3 - "$directory/pom.xml" <<'PY'
from pathlib import Path
import sys
path = Path(sys.argv[1])
path.write_text(path.read_text().replace('</project>', '''<dependencies><dependency>
  <groupId>com.packory.e2</groupId><artifactId>helper</artifactId><version>1.0.0</version>
</dependency></dependencies></project>'''))
PY
  fi
}
create_project "$WORK/bootstrap" bootstrap 1.0.0 com.packory.tooling
cat >"$WORK/bootstrap/src/main/java/com/packory/e2/Bootstrap.java" <<'JAVA'
package com.packory.e2;
public final class Bootstrap { public static String value() { return "tooling only"; } }
JAVA
primed=false
for attempt in 1 2 3; do
  if timeout 240 "$MVN" -U -B -C -q --no-transfer-progress -s "$WORK/bootstrap-settings.xml" \
    -Dmaven.repo.local="$PRIME" -f "$WORK/bootstrap/pom.xml" \
    clean deploy "-DaltDeploymentRepository=tooling::file://$WORK/bootstrap-repository" \
    org.apache.maven.plugins:maven-dependency-plugin:3.7.1:help \
    >"$WORK/maven-bootstrap.log" 2>&1; then primed=true; break; fi
  if ! packory_maven_transient "$WORK/maven-bootstrap.log"; then
    echo "Maven bootstrap failed with a non-network error" >&2; exit 1
  fi
  if [ "$attempt" -lt 3 ]; then packory_maven_backoff "$attempt"; fi
done
test "$primed" = true || { echo "Public Maven tooling unavailable" >&2; exit 1; }
test ! -e "$PRIME/com/packory/e2"
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
echo "Native build tooling primed; fixture resolution now uses Packory only"

ADMIN="$(openssl rand -hex 32)"
printf '%s\n' "$ADMIN" >"$WORK/redactions"
start_server() {
  PACKORY_DATA_DIRECTORY="$WORK/data" PACKORY_PUBLIC_BASE_URL="$BASE" \
  PACKORY_ADMIN_TOKEN="$ADMIN" PACKORY_ANONYMOUS_READ=true ASPNETCORE_URLS="$BASE" \
  PACKORY_PUBLISH_REQUESTS_PER_MINUTE=1000 \
  dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >>"$WORK/server.log" 2>&1 &
  PID="$!"
  for n in $(seq 1 100); do
    if curl --fail --silent --max-time 2 "$BASE/health/ready" >/dev/null 2>&1; then return; fi
    if ! kill -0 "$PID" 2>/dev/null; then break; fi
    sleep 0.2
  done
  echo "Maven acceptance server failed readiness" >&2; return 1
}
stop_server() { kill "$PID"; wait "$PID" || true; PID=""; }
start_server
admin_post() {
  curl --fail --silent --show-error --max-time 30 -H "Authorization: Bearer $ADMIN" \
    -H 'Content-Type: application/json' --data-binary "$2" "$BASE$1"
}
grant() {
  local token
  token="$(admin_post /api/admin/tokens "{\"role\":\"$1\",\"namespace\":\"feed:$2:$3\",\"expiresInHours\":1}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')"
  printf '%s\n' "$token" >>"$WORK/redactions"
  printf '%s' "$token"
}
for feed in e2-alpha e2-beta; do
  admin_post /api/v1/admin/feeds/drafts \
    "{\"id\":\"$feed\",\"kind\":\"maven\",\"mode\":\"hosted\",\"displayName\":\"Synthetic Maven acceptance\"}" >/dev/null
  admin_post "/api/v1/admin/feeds/$feed/activate" '' >/dev/null
done
A_READ="$(grant read e2-alpha read)"; A_WRITE="$(grant write e2-alpha publish)"
B_READ="$(grant read e2-beta read)"; B_WRITE="$(grant write e2-beta publish)"

settings() {
  local destination="$1" repository="$2" credential="$3"
  cat >"$destination" <<XML
<settings xmlns="http://maven.apache.org/SETTINGS/1.0.0">
  <mirrors><mirror><id>packory-fixture</id><mirrorOf>*</mirrorOf><url>$repository</url></mirror></mirrors>
  <profiles><profile><id>fixture</id><repositories><repository><id>fixture</id><url>$repository</url>
    <releases><enabled>true</enabled><checksumPolicy>fail</checksumPolicy></releases>
    <snapshots><enabled>true</enabled><updatePolicy>always</updatePolicy><checksumPolicy>fail</checksumPolicy></snapshots>
  </repository></repositories></profile></profiles><activeProfiles><activeProfile>fixture</activeProfile></activeProfiles>
XML
  if [ -n "$credential" ]; then
    cat >>"$destination" <<XML
  <servers><server><id>packory-fixture</id><username>packory</username><password>$credential</password></server></servers>
XML
  fi
  printf '</settings>\n' >>"$destination"
}
fresh_maven() {
  test ! -e "$WORK/m2-$1" || return 1
  cp -a "$PRIME" "$WORK/m2-$1" || return 1
  test ! -e "$WORK/m2-$1/com/packory/e2"
}
deploy() {
  local label="$1" repository="$2" credential="$3" artifact="$4" version="$5" marker="$6"
  local project="$WORK/project-$label"
  create_project "$project" "$artifact" "$version" com.packory.e2
  if [ "$artifact" = helper ]; then
    printf 'package com.packory.e2; public final class Helper { public static String value() { return "%s"; } }\n' "$marker" \
      >"$project/src/main/java/com/packory/e2/Helper.java"
  else
    printf 'package com.packory.e2; public final class Sample { public static String value() { return Helper.value() + ":%s"; } }\n' "$marker" \
      >"$project/src/main/java/com/packory/e2/Sample.java"
  fi
  settings "$WORK/settings-$label.xml" "$repository" "$credential"
  fresh_maven "$label"
  timeout 120 "$MVN" -B -C -q --no-transfer-progress -s "$WORK/settings-$label.xml" \
    -Dmaven.repo.local="$WORK/m2-$label" -f "$project/pom.xml" \
    clean deploy "-DaltDeploymentRepository=packory-fixture::$repository" \
    >"$WORK/maven-deploy-$label.log" 2>&1
  mkdir -p "$WORK/payload/$label"
  cp "$project/pom.xml" "$WORK/payload/$label/$artifact.pom"
  cp "$project/target/$artifact-$version.jar" "$WORK/payload/$label/$artifact.jar"
  cp "$project/target/$artifact-$version-sources.jar" "$WORK/payload/$label/$artifact-sources.jar"
}
get() {
  curl --fail --silent --show-error --max-time 30 -H "Authorization: Bearer $2" "$1"
}
code() { curl --silent --show-error --max-time 30 -o "$WORK/response" -w '%{http_code}' "$@"; }
verify_file() {
  local url="$1" token="$2" expected="$3"
  get "$url" "$token" >"$WORK/download"
  cmp "$expected" "$WORK/download"
  for algorithm in sha1 sha256 sha512; do
    get "$url.$algorithm" "$token" >"$WORK/checksum"
    python3 - "$WORK/download" "$WORK/checksum" "$algorithm" <<'PY'
from pathlib import Path
import hashlib, sys
actual = hashlib.new(sys.argv[3], Path(sys.argv[1]).read_bytes()).hexdigest()
assert Path(sys.argv[2]).read_text().strip().lower() == actual, 'Native sidecar differs from original bytes'
PY
  done
}
verify_metadata() {
  local url="$1" token="$2" destination="$3"
  get "$url" "$token" >"$destination"
  verify_file "$url" "$token" "$destination"
}
verify_release() {
  local label="$1" repository="$2" token="$3" artifact="$4"
  local path="$repository/com/packory/e2/$artifact/1.0.0/$artifact-1.0.0"
  verify_file "$path.pom" "$token" "$WORK/payload/$label/$artifact.pom"
  verify_file "$path.jar" "$token" "$WORK/payload/$label/$artifact.jar"
  verify_file "$path-sources.jar" "$token" "$WORK/payload/$label/$artifact-sources.jar"
  verify_metadata "$repository/com/packory/e2/$artifact/maven-metadata.xml" "$token" "$WORK/$label-metadata.xml"
}
verify_snapshot() {
  local label="$1" repository="$2" token="$3"
  local path="$repository/com/packory/e2/sample/2.0-SNAPSHOT"
  verify_metadata "$path/maven-metadata.xml" "$token" "$WORK/$label-metadata.xml"
  python3 - "$WORK/$label-metadata.xml" "$WORK/$label-paths" <<'PY'
from pathlib import Path
import re, sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
assert root.findtext('groupId') == 'com.packory.e2'
assert root.findtext('artifactId') == 'sample'
assert root.findtext('version') == '2.0-SNAPSHOT'
entries = {(entry.findtext('extension'), entry.findtext('classifier') or ''): entry.findtext('value')
           for entry in root.findall('versioning/snapshotVersions/snapshotVersion')}
assert set(entries) == {('pom', ''), ('jar', ''), ('jar', 'sources')}, entries
assert len(set(entries.values())) == 1, 'One native deploy must bind the same snapshot generation'
value = entries[('jar', '')]
assert re.fullmatch(r'2\.0-\d{8}\.\d{6}-[1-9]\d*', value), value
Path(sys.argv[2]).write_text('\n'.join([value, f'sample-{value}.pom', f'sample-{value}.jar', f'sample-{value}-sources.jar']) + '\n')
PY
  mapfile -t paths <"$WORK/$label-paths"
  verify_file "$path/${paths[1]}" "$token" "$WORK/payload/$label/sample.pom"
  verify_file "$path/${paths[2]}" "$token" "$WORK/payload/$label/sample.jar"
  verify_file "$path/${paths[3]}" "$token" "$WORK/payload/$label/sample-sources.jar"
}

GLOBAL="$BASE/maven"
ALPHA="$BASE/feeds/e2-alpha/maven"
BETA="$BASE/feeds/e2-beta/maven"
for profile in global alpha beta; do
  case "$profile" in
    global) REPOSITORY="$GLOBAL"; WRITE="$ADMIN"; READ="$ADMIN" ;;
    alpha) REPOSITORY="$ALPHA"; WRITE="$A_WRITE"; READ="$A_READ" ;;
    beta) REPOSITORY="$BETA"; WRITE="$B_WRITE"; READ="$B_READ" ;;
  esac
  deploy "$profile-helper" "$REPOSITORY" "$WRITE" helper 1.0.0 "$profile-helper"
  verify_release "$profile-helper" "$REPOSITORY" "$READ" helper
  deploy "$profile-release" "$REPOSITORY" "$WRITE" sample 1.0.0 release
  verify_release "$profile-release" "$REPOSITORY" "$READ" sample
  deploy "$profile-snapshot-1" "$REPOSITORY" "$WRITE" sample 2.0-SNAPSHOT generation-one
  verify_snapshot "$profile-snapshot-1" "$REPOSITORY" "$READ"
  # Exercise build-number progression even when both deploys share a timestamp.
  deploy "$profile-snapshot-2" "$REPOSITORY" "$WRITE" sample 2.0-SNAPSHOT generation-two
  verify_snapshot "$profile-snapshot-2" "$REPOSITORY" "$READ"
  python3 - "$WORK/$profile-snapshot-1-paths" "$WORK/$profile-snapshot-2-paths" <<'PY'
from pathlib import Path
import sys
old, new = [Path(path).read_text().splitlines()[0] for path in sys.argv[1:]]
assert old != new, 'Second native deployment did not advance the immutable snapshot identity'
assert int(new.rsplit('-', 1)[1]) > int(old.rsplit('-', 1)[1]), 'Snapshot build number did not advance'
PY
  mapfile -t old_paths <"$WORK/$profile-snapshot-1-paths"
  verify_file "$REPOSITORY/com/packory/e2/sample/2.0-SNAPSHOT/${old_paths[2]}" "$READ" \
    "$WORK/payload/$profile-snapshot-1/sample.jar"
  verify_metadata "$REPOSITORY/com/packory/e2/sample/maven-metadata.xml" "$READ" "$WORK/$profile-ga-metadata.xml"
  echo "Native mvn deploy: $profile release, sources and two immutable SNAPSHOT generations passed"
done

maven_consume() {
  local label="$1" repository="$2" token="$3" version="$4"
  settings "$WORK/settings-$label.xml" "$repository" "$token" || return 1
  fresh_maven "$label" || return 1
  timeout 90 "$MVN" -B -C -q --no-transfer-progress -U -s "$WORK/settings-$label.xml" \
    -Dmaven.repo.local="$WORK/m2-$label" org.apache.maven.plugins:maven-dependency-plugin:3.7.1:get \
    "-Dartifact=com.packory.e2:sample:$version" -Dtransitive=true \
    >"$WORK/maven-consume-$label.log" 2>&1
}
assert_maven_bytes() {
  local label="$1" profile="$2" version="$3" generation="$4"
  cmp "$WORK/payload/$profile-$generation/sample.jar" \
    "$WORK/m2-$label/com/packory/e2/sample/$version/sample-$version.jar"
  cmp "$WORK/payload/$profile-helper/helper.jar" \
    "$WORK/m2-$label/com/packory/e2/helper/1.0.0/helper-1.0.0.jar"
}
gradle_consume() {
  local label="$1" repository="$2" token="$3" version="$4" expected="$5" expected_source="$6"
  local directory="$WORK/gradle-$label"
  mkdir -p "$directory/src/main/java/com/packory/consumer"
  printf "rootProject.name = 'packory-native-consumer'\n" >"$directory/settings.gradle"
  cat >"$directory/build.gradle" <<'GRADLE'
plugins { id 'application' }
repositories {
    maven {
        url = uri(providers.environmentVariable('PACKORY_TEST_REPOSITORY').get())
        allowInsecureProtocol = true // Synthetic loopback acceptance only.
        credentials {
            username = 'packory'
            password = providers.environmentVariable('PACKORY_TEST_TOKEN').get()
        }
        authentication { basic(BasicAuthentication) }
    }
}
configurations { sources { transitive = false } }
dependencies {
    implementation "com.packory.e2:sample:${providers.environmentVariable('PACKORY_TEST_VERSION').get()}"
    sources "com.packory.e2:sample:${providers.environmentVariable('PACKORY_TEST_VERSION').get()}:sources"
}
application { mainClass = 'com.packory.consumer.Main' }
tasks.named('run') { args providers.environmentVariable('PACKORY_TEST_EXPECTED').get() }
tasks.register('verifySources') {
    doLast {
        def archive = configurations.sources.singleFile
        def source = zipTree(archive).matching { include 'com/packory/e2/Sample.java' }.singleFile.text
        assert source.contains('class Sample')
        copy { from archive; into layout.buildDirectory.dir('resolved-sources') }
    }
}
GRADLE
  cat >"$directory/src/main/java/com/packory/consumer/Main.java" <<'JAVA'
package com.packory.consumer;
import com.packory.e2.Sample;
public final class Main {
    public static void main(String[] args) {
        if (!Sample.value().equals(args[0])) throw new AssertionError("Wrong direct/transitive fixture bytes");
        System.out.println("Packory native dependency graph verified");
    }
}
JAVA
  # A fresh Gradle home prevents fixture reuse from another feed or generation.
  PACKORY_TEST_REPOSITORY="$repository" PACKORY_TEST_TOKEN="$token" \
  PACKORY_TEST_VERSION="$version" PACKORY_TEST_EXPECTED="$expected" \
  GRADLE_USER_HOME="$WORK/gradle-cache-$label" \
  timeout 150 "$GRADLE" --no-daemon --console=plain --max-workers=2 \
    -Duser.home="$WORK/home" -p "$directory" build run verifySources \
    >"$WORK/gradle-$label.log" 2>&1
  python3 - "$directory/build/resolved-sources" "$expected_source" <<'PY'
from pathlib import Path
import sys
files = list(Path(sys.argv[1]).glob('*.jar'))
assert len(files) == 1, 'Expected one native sources classifier artifact'
assert files[0].read_bytes() == Path(sys.argv[2]).read_bytes(), 'Native sources classifier bytes differ'
PY
}

for profile in global alpha beta; do
  case "$profile" in global) REPOSITORY="$GLOBAL"; READ="$ADMIN" ;; alpha) REPOSITORY="$ALPHA"; READ="$A_READ" ;; beta) REPOSITORY="$BETA"; READ="$B_READ" ;; esac
  maven_consume "$profile-release-read" "$REPOSITORY" "$READ" 1.0.0
  assert_maven_bytes "$profile-release-read" "$profile" 1.0.0 release
  maven_consume "$profile-latest" "$REPOSITORY" "$READ" 2.0-SNAPSHOT
  assert_maven_bytes "$profile-latest" "$profile" 2.0-SNAPSHOT snapshot-2
  gradle_consume "$profile-latest" "$REPOSITORY" "$READ" 2.0-SNAPSHOT "$profile-helper:generation-two" \
    "$WORK/payload/$profile-snapshot-2/sample-sources.jar"
done
gradle_consume alpha-release "$ALPHA" "$A_READ" 1.0.0 alpha-helper:release "$WORK/payload/alpha-release/sample-sources.jar"
echo "Fresh Maven and Gradle caches resolved the correct direct, transitive and classifier artifacts"

native_rejection() {
  local label="$1" expected="$2" token="$3" version="${4:-1.0.0}"
  if maven_consume "denied-$label" "$ALPHA" "$token" "$version"; then
    echo "Native Maven $label unexpectedly succeeded" >&2; exit 1
  fi
  if [ "$expected" = 404 ]; then
    grep -Eq 'Could not find artifact|was not found in|404' "$WORK/maven-consume-denied-$label.log"
  else
    grep -Eq "(^|[^[:digit:]])$expected([^[:digit:]]|$)" "$WORK/maven-consume-denied-$label.log"
  fi
}
native_rejection anonymous 401 ''
native_rejection foreign-feed 403 "$B_READ"
native_rejection missing-version 404 "$A_READ" 9.9.9
native_publish_rejection() {
  local label="$1" expected="$2" token="$3"
  settings "$WORK/settings-publish-denied-$label.xml" "$ALPHA" "$token"
  fresh_maven "publish-denied-$label"
  if timeout 90 "$MVN" -B -C -q --no-transfer-progress \
    -s "$WORK/settings-publish-denied-$label.xml" -Dmaven.repo.local="$WORK/m2-publish-denied-$label" \
    org.apache.maven.plugins:maven-deploy-plugin:3.1.4:deploy-file \
    -DrepositoryId=packory-fixture "-Durl=$ALPHA" \
    "-Dfile=$WORK/payload/alpha-release/sample.jar" "-DpomFile=$WORK/payload/alpha-release/sample.pom" \
    >"$WORK/maven-publish-denied-$label.log" 2>&1; then
    echo "Native Maven $label publishing unexpectedly succeeded" >&2; exit 1
  fi
  grep -Eq "(^|[^[:digit:]])$expected([^[:digit:]]|$)" "$WORK/maven-publish-denied-$label.log"
}
native_publish_rejection anonymous 401 ''
native_publish_rejection read-only 403 "$A_READ"
native_publish_rejection foreign-feed 403 "$B_WRITE"
P=com/packory/e2/sample/1.0.0/sample-1.0.0.jar
test "$(code "$ALPHA/$P")" = 401
test "$(code -H "Authorization: Bearer $B_READ" "$ALPHA/$P")" = 403
test "$(code -H "Authorization: Bearer $A_READ" -X PUT --data-binary "@$WORK/payload/alpha-release/sample.jar" "$ALPHA/$P")" = 403
test "$(code -H "Authorization: Bearer $B_WRITE" -X PUT --data-binary "@$WORK/payload/alpha-release/sample.jar" "$ALPHA/$P")" = 403
test "$(code -H "Authorization: Bearer $A_WRITE" -X PUT --data-binary "@$WORK/payload/beta-release/sample.jar" "$ALPHA/$P")" = 409
test "$(code -H "Authorization: Bearer $A_WRITE" -X PUT --data-binary '00' "$ALPHA/$P.sha256")" = 400
echo "Private native 401/403/404, read-only writes, cross-feed writes and immutable/checksum rejection passed"

# Both mutable metadata bytes and old immutable generations must survive a
# genuine application restart, with all native fixture caches discarded.
stop_server
start_server
for profile in global alpha beta; do
  case "$profile" in global) REPOSITORY="$GLOBAL"; READ="$ADMIN" ;; alpha) REPOSITORY="$ALPHA"; READ="$A_READ" ;; beta) REPOSITORY="$BETA"; READ="$B_READ" ;; esac
  get "$REPOSITORY/com/packory/e2/sample/2.0-SNAPSHOT/maven-metadata.xml" "$READ" >"$WORK/restarted.xml"
  cmp "$WORK/$profile-snapshot-2-metadata.xml" "$WORK/restarted.xml"
  get "$REPOSITORY/com/packory/e2/sample/maven-metadata.xml" "$READ" >"$WORK/restarted.xml"
  cmp "$WORK/$profile-ga-metadata.xml" "$WORK/restarted.xml"
  maven_consume "$profile-restarted" "$REPOSITORY" "$READ" 2.0-SNAPSHOT
  assert_maven_bytes "$profile-restarted" "$profile" 2.0-SNAPSHOT snapshot-2
  mapfile -t old_paths <"$WORK/$profile-snapshot-1-paths"
  verify_file "$REPOSITORY/com/packory/e2/sample/2.0-SNAPSHOT/${old_paths[2]}" "$READ" \
    "$WORK/payload/$profile-snapshot-1/sample.jar"
done
gradle_consume alpha-restarted "$ALPHA" "$A_READ" 2.0-SNAPSHOT alpha-helper:generation-two \
  "$WORK/payload/alpha-snapshot-2/sample-sources.jar"
echo "E2 native Maven $MAVEN_VERSION deploy/resolve and Gradle $GRADLE_VERSION build/resolve passed, including restart"

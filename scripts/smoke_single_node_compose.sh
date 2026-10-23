#!/usr/bin/env bash
# Real Docker application smoke in disposable CI resources only.
# Tests file-mounted credentials, native HTTPS NuGet/npm and immutable Raw data
# across recreation, offline backup and isolated restore. No host boot or upgrade.
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
for tool in docker openssl curl cmp sha256sum sudo dotnet npm node python3; do
  command -v "$tool" >/dev/null
done

WORK="$(mktemp -d)"
PROJECT="packory-ci-${GITHUB_RUN_ID:-$$}-${GITHUB_RUN_ATTEMPT:-1}"
RESTORE_PROJECT="$PROJECT-restore"
ROLLBACK_PROJECT="$PROJECT-rollback"
ACTIVE_PROJECT="$PROJECT"
BASE="https://127.0.0.1:18443"
compose() {
  docker compose -p "$ACTIVE_PROJECT" -f compose.secrets.yaml -f "$WORK/ci-images.yaml" "$@"
}
cleanup() {
  status=$?
  trap - EXIT
  for ACTIVE_PROJECT in "$PROJECT" "$RESTORE_PROJECT" "$ROLLBACK_PROJECT"; do
    compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  done
  # Docker exports/restored copies may contain files owned by container UID 1654.
  sudo rm -rf -- "$WORK"
  if [ "$status" -ne 0 ]; then
    printf 'Disposable single-node Compose smoke failed; no runtime logs exported.\n' >&2
  fi
  exit "$status"
}
trap cleanup EXIT

source "$ROOT/scripts/smoke_single_node_tls.sh"
tls_prepare

# Generate three independent throwaway credentials. Keep them out of command
# arguments, output and Git; allow the non-root app UID/GID to read the mounts.
for role in admin read write; do
  openssl rand -hex 32 >"$WORK/$role"
  # Render the curl credential while this runner still owns the secret source.
  printf 'header = "Authorization: Bearer %s"\n' "$(cat "$WORK/$role")" >"$WORK/$role.curl"
done
# Generate native client configs while these disposable synthetic secrets are
# still readable by the runner; the container itself gets separate root-owned
# group-readable bind mounts only after preparation.
source "$ROOT/scripts/smoke_single_node_native.sh"
native_prepare
for role in admin read write; do
  chmod 0640 "$WORK/$role"
  sudo chown 0:1654 "$WORK/$role"
done
export PACKORY_ADMIN_TOKEN_SOURCE="$WORK/admin"
export PACKORY_READ_TOKEN_SOURCE="$WORK/read"
export PACKORY_WRITE_TOKEN_SOURCE="$WORK/write"
export PACKORY_PUBLIC_BASE_URL="$BASE"
chmod 0640 "$WORK/tls/server.pfx"
sudo chown 0:1654 "$WORK/tls/server.pfx"

# Override only the application image with the locally built CI image.
# The scanner service is deliberately not started: this is NOT worker testing.
cat >"$WORK/ci-images.yaml" <<YAML
services:
  packory:
    image: packory:ci
    ports:
      - "127.0.0.1:18443:8443"
    environment:
      ASPNETCORE_URLS: "http://+:8080;https://+:8443"
      ASPNETCORE_Kestrel__Certificates__Default__Path: /run/ci-tls/server.pfx
    volumes:
      - "$WORK/tls/server.pfx:/run/ci-tls/server.pfx:ro"
YAML
compose config --quiet

# curl -K avoids exposing token values as process arguments.
printf 'header = "Authorization: Bearer invalid-fixture"\n' >"$WORK/invalid.curl"
printf 'Packory single-node immutable synthetic fixture\n' >"$WORK/payload"
SHA="$(sha256sum "$WORK/payload" | cut -d' ' -f1)"

ready() {
  local attempt
  for attempt in $(seq 1 90); do
    if curl --noproxy '*' --cacert "$SSL_CERT_FILE" -fsS --max-time 3 "$BASE/health/ready" >/dev/null 2>&1; then
      return 0
    fi
    sleep .3
  done
  return 1
}
code() {
  curl --noproxy '*' --cacert "$SSL_CERT_FILE" -sS --max-time 10 -o /dev/null -w '%{http_code}' "$@"
}
ARTIFACT="$BASE/raw/packory.compose.smoke/1.0.0"

compose up -d --no-deps packory >/dev/null
ready
test "$(code "$BASE/health/live")" = "200"
# Wrong trust roots and wrong peer identity must both fail certificate validation.
# Never use curl --insecure, NuGet certificate bypass or npm strict-ssl=false.
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=Untrusted disposable CI root' \
  -keyout "$WORK/tls/wrong.key" -out "$WORK/tls/wrong.pem" >/dev/null 2>&1
tls_status=0
curl --noproxy '*' --cacert "$WORK/tls/wrong.pem" -fsS --max-time 5 \
  "$BASE/health/live" >/dev/null 2>&1 || tls_status=$?
test "$tls_status" = 60 # curl: peer certificate cannot be authenticated
tls_status=0
curl --noproxy '*' --cacert "$SSL_CERT_FILE" -fsS --max-time 5 \
  --resolve 'localhost:18443:127.0.0.1' 'https://localhost:18443/health/live' \
  >/dev/null 2>&1 || tls_status=$?
test "$tls_status" = 60
test "$(code "$BASE/api/packages")" = "401"
test "$(code -K "$WORK/invalid.curl" "$BASE/api/packages")" = "401"
test "$(code -K "$WORK/read.curl" "$BASE/api/packages")" = "200"
test "$(code -K "$WORK/read.curl" -X PUT -H 'Content-Type: application/octet-stream' \
  --data-binary "@$WORK/payload" "$ARTIFACT")" = "403"
test "$(code -K "$WORK/write.curl" -X PUT -H 'Content-Type: application/octet-stream' \
  --data-binary "@$WORK/payload" "$ARTIFACT")" = "201"
curl --noproxy '*' --cacert "$SSL_CERT_FILE" -fsS --max-time 10 -K "$WORK/read.curl" "$ARTIFACT" -o "$WORK/download-1"
cmp "$WORK/payload" "$WORK/download-1"

# Confirm the write token can read the native index before invoking NuGet,
# isolating application auth from client-side source credential propagation.
test "$(code -K "$WORK/write.curl" "$BASE/nuget/v3/index.json")" = "200"
# Publish and consume using real .NET and npm clients with fresh caches.
native_publish
native_consume before

# The second container is created against the same isolated named volume.
# Re-running the immutable PUT must be a conflict, not a silent overwrite.
compose up -d --no-deps --force-recreate packory >/dev/null
ready
curl --noproxy '*' --cacert "$SSL_CERT_FILE" -fsS --max-time 10 -K "$WORK/read.curl" "$ARTIFACT" -o "$WORK/download-2"
cmp "$WORK/payload" "$WORK/download-2"
test "$(sha256sum "$WORK/download-2" | cut -d' ' -f1)" = "$SHA"
test "$(code -K "$WORK/write.curl" -X PUT -H 'Content-Type: application/octet-stream' \
  --data-binary "@$WORK/payload" "$ARTIFACT")" = "409"

native_consume after

# Stop the sole writer before copying the complete mounted data tree. This
# fixture never accepts operator-selected project names, volumes or endpoints.
source_cid="$(compose ps --all --quiet packory)"
source_volume="$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/data"}}{{.Name}}{{end}}{{end}}' "$source_cid")"
test -n "$source_volume"
compose stop -t 30 packory >/dev/null
test "$(docker inspect --format '{{.State.Status}}' "$source_cid")" = exited
test "$(docker inspect --format '{{.State.ExitCode}}' "$source_cid")" = 0
mkdir "$WORK/offline-source"
docker cp "$source_cid:/data/." "$WORK/offline-source"
sudo chown -R "$(id -u):$(id -g)" "$WORK/offline-source"

# Checkpoint ONLY the private exported copy after confirming the writer stopped.
# Never delete a WAL file or treat copying a live DB as a coherent snapshot.
python3 - "$WORK/offline-source/packory.sqlite" <<'PY'
import sqlite3, sys
with sqlite3.connect(sys.argv[1]) as database:
    result = database.execute('PRAGMA wal_checkpoint(TRUNCATE)').fetchone()
    if result[0] != 0:
        raise SystemExit('Offline fixture checkpoint was busy.')
PY
python3 scripts/offline_backup.py create --source "$WORK/offline-source" \
  --destination "$WORK/backup" --offline-confirmed
python3 scripts/offline_backup.py verify --source "$WORK/backup"
compose down --remove-orphans >/dev/null

restore_into_fresh_project() {
  local project="$1" suffix="$2" cid target_volume
  ACTIVE_PROJECT="$project"
  python3 scripts/offline_backup.py restore --source "$WORK/backup" \
    --destination "$WORK/$suffix" --offline-confirmed
  # Create the destination volumes and container without starting any writer.
  compose create --no-deps packory >/dev/null
  cid="$(compose ps --all --quiet packory)"
  test "$(docker inspect --format '{{.State.Status}}' "$cid")" = created
  target_volume="$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/data"}}{{.Name}}{{end}}{{end}}' "$cid")"
  test -n "$target_volume"
  test "$source_volume" != "$target_volume"
  # Archive mode preserves the non-root application ownership in the new volume.
  sudo chown -R 1654:1654 "$WORK/$suffix"
  # The runner no longer owns mode-0700 restore directories after chown;
  # root must read them while preserving UID/GID for the non-root application.
  sudo docker cp -a "$WORK/$suffix/." "$cid:/data"
  compose start packory >/dev/null
  ready
  curl --noproxy '*' --cacert "$SSL_CERT_FILE" -fsS --max-time 10 \
    -K "$WORK/read.curl" "$ARTIFACT" -o "$WORK/download-$suffix"
  cmp "$WORK/payload" "$WORK/download-$suffix"
  test "$(code -K "$WORK/write.curl" -X PUT -H 'Content-Type: application/octet-stream' \
    --data-binary "@$WORK/payload" "$ARTIFACT")" = "409"
  native_consume "$suffix"
}

restore_into_fresh_project "$RESTORE_PROJECT" restored
# Demonstrate point-in-time rollback by restoring the original backup again.
# Same image/schema only: this does NOT exercise an old-to-new schema upgrade.
NEW_ARTIFACT="$BASE/raw/packory.compose.post-backup/1.0.0"
test "$(code -K "$WORK/write.curl" -X PUT -H 'Content-Type: application/octet-stream' \
  --data-binary "@$WORK/payload" "$NEW_ARTIFACT")" = "201"
compose down --remove-orphans >/dev/null
restore_into_fresh_project "$ROLLBACK_PROJECT" rollback
test "$(code -K "$WORK/read.curl" "$NEW_ARTIFACT")" = "404"
printf 'CI-only HTTPS native NuGet/npm, TLS rejection, recreation, offline backup, isolated restore and same-schema rollback passed.\n'

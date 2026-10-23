#!/usr/bin/env bash
# Real Docker application smoke in disposable CI resources only.
# Tests file-mounted credentials and immutable Raw data across recreation;
# deliberately does NOT test TLS, scanner behavior, host boot or full restore.
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
for tool in docker openssl curl cmp sha256sum sudo python3; do
  command -v "$tool" >/dev/null
done

WORK="$(mktemp -d)"
PROJECT="packory-ci-${GITHUB_RUN_ID:-$}-${GITHUB_RUN_ATTEMPT:-1}"
RESTORE_PROJECT="${PROJECT}-restore"
RESTORE_VOLUME="${RESTORE_PROJECT}_packory-data"
BASE="http://127.0.0.1:8080"
compose() {
  docker compose -p "$PROJECT" -f compose.secrets.yaml -f "$WORK/ci-images.yaml" "$@"
}
restored_compose() {
  docker compose -p "$RESTORE_PROJECT" -f compose.secrets.yaml -f "$WORK/restore-images.yaml" "$@"
}
cleanup() {
  status=$?
  trap - EXIT
  restored_compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  compose down --volumes --remove-orphans >/dev/null 2>&1 || true
  docker volume rm "$RESTORE_VOLUME" >/dev/null 2>&1 || true
  rm -rf "$WORK"
  if [ "$status" -ne 0 ]; then
    printf 'Disposable single-node Compose smoke failed; no runtime logs exported.\n' >&2
  fi
  exit "$status"
}
trap cleanup EXIT

# Generate three independent throwaway credentials. Keep them out of command
# arguments, output and Git; allow the non-root app UID/GID to read the mounts.
for role in admin read write; do
  openssl rand -hex 32 >"$WORK/$role"
  # Render the curl credential while this runner still owns the secret source.
  printf 'header = "Authorization: Bearer %s"\n' "$(cat "$WORK/$role")" >"$WORK/$role.curl"
  chmod 0640 "$WORK/$role"
  sudo chown 0:1654 "$WORK/$role"
done
export PACKORY_ADMIN_TOKEN_SOURCE="$WORK/admin"
export PACKORY_READ_TOKEN_SOURCE="$WORK/read"
export PACKORY_WRITE_TOKEN_SOURCE="$WORK/write"
export PACKORY_PUBLIC_BASE_URL="http://localhost:8080"

# Override only the application image with the locally built CI image.
# The scanner service is deliberately not started: this is NOT worker testing.
cat >"$WORK/ci-images.yaml" <<'YAML'
services:
  packory:
    image: packory:ci
YAML
compose config --quiet

# curl -K avoids exposing token values as process arguments.
printf 'header = "Authorization: Bearer invalid-fixture"\n' >"$WORK/invalid.curl"
printf 'Packory single-node immutable synthetic fixture\n' >"$WORK/payload"
SHA="$(sha256sum "$WORK/payload" | cut -d' ' -f1)"

ready() {
  local attempt
  for attempt in $(seq 1 90); do
    if curl --noproxy '*' -fsS --max-time 3 "$BASE/health/ready" >/dev/null 2>&1; then
      return 0
    fi
    sleep .3
  done
  return 1
}
code() {
  curl --noproxy '*' -sS --max-time 10 -o /dev/null -w '%{http_code}' "$@"
}
ARTIFACT="$BASE/raw/packory.compose.smoke/1.0.0"

compose up -d --no-deps packory >/dev/null
ready
test "$(code "$BASE/api/packages")" = "401"
test "$(code -K "$WORK/invalid.curl" "$BASE/api/packages")" = "401"
test "$(code -K "$WORK/read.curl" "$BASE/api/packages")" = "200"
test "$(code -K "$WORK/read.curl" -X PUT -H 'Content-Type: application/octet-stream' \
  --data-binary "@$WORK/payload" "$ARTIFACT")" = "403"
test "$(code -K "$WORK/write.curl" -X PUT -H 'Content-Type: application/octet-stream' \
  --data-binary "@$WORK/payload" "$ARTIFACT")" = "201"
curl --noproxy '*' -fsS --max-time 10 -K "$WORK/read.curl" "$ARTIFACT" -o "$WORK/download-1"
cmp "$WORK/payload" "$WORK/download-1"

# The second container is created against the same isolated named volume.
# Re-running the immutable PUT must be a conflict, not a silent overwrite.
compose up -d --no-deps --force-recreate packory >/dev/null
ready
curl --noproxy '*' -fsS --max-time 10 -K "$WORK/read.curl" "$ARTIFACT" -o "$WORK/download-2"
cmp "$WORK/payload" "$WORK/download-2"
test "$(sha256sum "$WORK/download-2" | cut -d' ' -f1)" = "$SHA"
test "$(code -K "$WORK/write.curl" -X PUT -H 'Content-Type: application/octet-stream' \
  --data-binary "@$WORK/payload" "$ARTIFACT")" = "409"

# Stop the sole writer and export its named volume using an ephemeral root
# *copy helper*; the Packory service itself always runs as non-root.
compose stop packory >/dev/null
mkdir "$WORK/exported"
docker run --rm --network none --security-opt no-new-privileges --user 0:0 --entrypoint /bin/sh \
  -v "${PROJECT}_packory-data:/source:ro" \
  -v "$WORK/exported:/destination" packory:ci \
  -c 'cp -a /source/. /destination/' >/dev/null
sudo chown -R "$(id -u):$(id -g)" "$WORK/exported"

# Exercise the existing offline verifier, rather than claiming that a
# running SQLite filesystem copy is consistent.
python3 scripts/offline_backup.py create --source "$WORK/exported" \
  --destination "$WORK/backup" --offline-confirmed >/dev/null
python3 scripts/offline_backup.py verify --source "$WORK/backup" >/dev/null
python3 scripts/offline_backup.py restore --source "$WORK/backup" \
  --destination "$WORK/restored" --offline-confirmed >/dev/null

# Import the verified bytes into a different, newly created named volume.
# Compose must not reinterpret that volume as the original application data.
docker volume create "$RESTORE_VOLUME" >/dev/null
docker run --rm --network none --security-opt no-new-privileges --user 0:0 --entrypoint /bin/sh \
  -v "$WORK/restored:/source:ro" -v "$RESTORE_VOLUME:/destination" \
  packory:ci -c 'cp -a /source/. /destination/' >/dev/null
cat >"$WORK/restore-images.yaml" <<YAML
services:
  packory:
    image: packory:ci
volumes:
  packory-data:
    external: true
    name: $RESTORE_VOLUME
YAML
restored_compose config --quiet
restored_compose up -d --no-deps packory >/dev/null
ready
test "$(code "$ARTIFACT")" = "401"
curl --noproxy '*' -fsS --max-time 10 -K "$WORK/read.curl" "$ARTIFACT" -o "$WORK/download-restored"
cmp "$WORK/payload" "$WORK/download-restored"
test "$(sha256sum "$WORK/download-restored" | cut -d' ' -f1)" = "$SHA"
test "$(code -K "$WORK/write.curl" -X PUT -H 'Content-Type: application/octet-stream' \
  --data-binary "@$WORK/payload" "$ARTIFACT")" = "409"

printf 'CI-only Docker file-secret, immutable artifact, offline restore and fresh-volume readback passed.\n'

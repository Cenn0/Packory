#!/usr/bin/env bash
# Real Docker application smoke in disposable CI resources only.
# Tests file-mounted credentials, native NuGet/npm and immutable Raw data
# across recreation; does NOT test TLS, scanner, host boot or full restore.
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
for tool in docker openssl curl cmp sha256sum sudo dotnet npm node; do
  command -v "$tool" >/dev/null
done

WORK="$(mktemp -d)"
PROJECT="packory-ci-${GITHUB_RUN_ID:-$$}-${GITHUB_RUN_ATTEMPT:-1}"
BASE="http://127.0.0.1:8080"
compose() {
  docker compose -p "$PROJECT" -f compose.secrets.yaml -f "$WORK/ci-images.yaml" "$@"
}
cleanup() {
  status=$?
  trap - EXIT
  compose down --volumes --remove-orphans >/dev/null 2>&1 || true
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

source "$ROOT/scripts/smoke_single_node_native.sh"
native_prepare
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

# Publish and consume using real .NET and npm clients with fresh caches.
native_publish
native_consume before

# The second container is created against the same isolated named volume.
# Re-running the immutable PUT must be a conflict, not a silent overwrite.
compose up -d --no-deps --force-recreate packory >/dev/null
ready
curl --noproxy '*' -fsS --max-time 10 -K "$WORK/read.curl" "$ARTIFACT" -o "$WORK/download-2"
cmp "$WORK/payload" "$WORK/download-2"
test "$(sha256sum "$WORK/download-2" | cut -d' ' -f1)" = "$SHA"
test "$(code -K "$WORK/write.curl" -X PUT -H 'Content-Type: application/octet-stream' \
  --data-binary "@$WORK/payload" "$ARTIFACT")" = "409"

native_consume after
printf 'CI-only Docker file-secret, native NuGet/npm, immutable Raw and recreation smoke passed.\n'

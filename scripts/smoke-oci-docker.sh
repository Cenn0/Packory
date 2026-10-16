#!/usr/bin/env bash
# Native Docker client acceptance against a fresh, loopback-only Packory OCI registry.
# A scratch image avoids pulling external base images during the test.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
TOKEN="$(openssl rand -hex 32)"
BASE="http://127.0.0.1:18082"
REGISTRY="127.0.0.1:18082"
TAG="$REGISTRY/packory-smoke/oci:1.0.0"
PID=""
export DOCKER_CONFIG="$WORK/docker-config"

cleanup() {
  status=$?
  trap - EXIT
  if [ "$status" -ne 0 ]; then
    echo "Packory OCI Docker smoke failed; diagnostic server log:" >&2
    test ! -f "$WORK/server.log" || tail -n 90 "$WORK/server.log" >&2
    test ! -f "$WORK/docker.log" || tail -n 90 "$WORK/docker.log" >&2
  fi
  docker image rm -f "$TAG" >/dev/null 2>&1 || true
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT

test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
for command in dotnet docker curl openssl python3; do
  command -v "$command" >/dev/null
done
docker info --format '{{.ServerVersion}}' >/dev/null
mkdir -p "$DOCKER_CONFIG" "$WORK/build"

PACKORY_DATA_DIRECTORY="$WORK/data" \
PACKORY_PUBLIC_BASE_URL="$BASE" \
PACKORY_ADMIN_TOKEN="$TOKEN" \
PACKORY_ANONYMOUS_READ="${PACKORY_SMOKE_ANONYMOUS_READ:-false}" \
PACKORY_OCI_AUTHENTICATED_PING="${PACKORY_SMOKE_AUTHENTICATED_PING:-false}" \
ASPNETCORE_URLS="$BASE" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
PID="$!"
READY=false
for n in $(seq 1 75); do
  if curl -fsS "$BASE/health/ready" >/dev/null 2>&1; then READY=true; break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.2
done
if [ "$READY" != true ]; then exit 1; fi

# Local registry credentials live only in the isolated, disposable Docker config.
printf '%s' "$TOKEN" | docker login "$REGISTRY" --username packory --password-stdin >"$WORK/docker.log" 2>&1

cat >"$WORK/build/Dockerfile" <<'DOCKERFILE'
FROM scratch
COPY sentinel.txt /smoke.txt
LABEL org.opencontainers.image.title="Packory OCI smoke"
DOCKERFILE
printf 'Packory isolated Docker registry acceptance\n' >"$WORK/build/sentinel.txt"
docker build --pull=false -t "$TAG" "$WORK/build" >>"$WORK/docker.log" 2>&1
docker push "$TAG" >>"$WORK/docker.log" 2>&1

# Verify Packory actually indexed the pushed Docker tag before pulling again.
curl --fail --silent --show-error --user "packory:$TOKEN" "$BASE/v2/packory-smoke/oci/tags/list" |
  python3 -c 'import json,sys; assert "1.0.0" in json.load(sys.stdin)["tags"]'
docker image rm -f "$TAG" >>"$WORK/docker.log" 2>&1
docker pull "$TAG" >>"$WORK/docker.log" 2>&1
docker image inspect "$TAG" --format '{{.Id}}' | grep -q '^sha256:'

echo 'Native Docker push/tag discovery/pull succeeded against Packory OCI.'

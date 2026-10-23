#!/usr/bin/env bash
# Test synthetic Compose expansion without running Docker or exposing real credentials.
set -euo pipefail
umask 077
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
for name in admin read write; do
  # The files are placeholders for parsing only; no credential or live service.
  printf '%064d\n' 0 > "$WORK/$name"
done
export PACKORY_ADMIN_TOKEN_SOURCE="$WORK/admin"
export PACKORY_READ_TOKEN_SOURCE="$WORK/read"
export PACKORY_WRITE_TOKEN_SOURCE="$WORK/write"

# CI-only immutable-looking fixture references. These are not downloadable images.
export PACKORY_APP_IMAGE="ghcr.io/cenn0/packory@sha256:$(printf 'a%.0s' {1..64})"
export PACKORY_SKILLSPECTOR_IMAGE="ghcr.io/cenn0/packory-skillspector@sha256:$(printf 'b%.0s' {1..64})"
export PACKORY_PUBLIC_BASE_URL="https://packory.example.invalid"
export PACKORY_INGRESS_NETWORK="packory-fixture"

docker compose -f compose.secrets.yaml config --format json |
  python3 scripts/check_deployment_compose.py loopback
docker compose -f compose.proxy.yaml config --format json |
  python3 scripts/check_deployment_compose.py private-ingress

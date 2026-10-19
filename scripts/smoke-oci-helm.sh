#!/usr/bin/env bash
# Experimental native Helm 3 OCI push/pull/template acceptance (no Kubernetes required).
# This is NOT a Helm release or Kubernetes deployment certification.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
HELM_VERSION="v3.22.0"
# SHA-256 per official helm/helm v3.22.0 release checksum (linux-amd64).
HELM_SHA256="1e4ab49e429626cf6c6958d914248b78c9730803c2751b87627e171dc800e7bb"
BASE="http://127.0.0.1:18083"
REGISTRY="127.0.0.1:18083"
TOKEN="$(openssl rand -hex 32)"
PID=""
export HELM_REGISTRY_CONFIG="$WORK/registry-config.json"
export HELM_CACHE_HOME="$WORK/cache"
export HELM_CONFIG_HOME="$WORK/config"
export HELM_DATA_HOME="$WORK/data-home"

cleanup() {
  status=$?
  trap - EXIT
  if [ "$status" -ne 0 ]; then
    echo "Helm OCI native smoke failed; limited synthetic diagnostic log:" >&2
    if test -f "$WORK/server.log"; then tail -n 70 "$WORK/server.log" >&2; fi
  fi
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT

test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
for command in dotnet curl openssl python3 tar sha256sum; do command -v "$command" >/dev/null; done
curl --fail --silent --show-error --location \
  "https://get.helm.sh/helm-$HELM_VERSION-linux-amd64.tar.gz" -o "$WORK/helm.tar.gz"
printf '%s  %s\n' "$HELM_SHA256" "$WORK/helm.tar.gz" | sha256sum -c -
tar -xzOf "$WORK/helm.tar.gz" linux-amd64/helm >"$WORK/helm"
chmod 700 "$WORK/helm"
"$WORK/helm" version --short | grep -F "$HELM_VERSION"
PACKORY_DATA_DIRECTORY="$WORK/packory-data" \
PACKORY_PUBLIC_BASE_URL="$BASE" \
PACKORY_ADMIN_TOKEN="$TOKEN" \
PACKORY_ANONYMOUS_READ=false \
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

printf '%s' "$TOKEN" | "$WORK/helm" registry login "$REGISTRY" \
  --plain-http --username packory --password-stdin >/dev/null
"$WORK/helm" create "$WORK/synthetic-chart" >/dev/null
"$WORK/helm" package "$WORK/synthetic-chart" -d "$WORK" >/dev/null
"$WORK/helm" push "$WORK/synthetic-chart-0.1.0.tgz" \
  "oci://$REGISTRY/packory-smoke" --plain-http >"$WORK/push.txt" 2>&1
grep -q "Pushed:" "$WORK/push.txt"

# Pull with a fresh client cache: bytes must match the chart we uploaded.
rm -rf "$HELM_CACHE_HOME"
mkdir -p "$HELM_CACHE_HOME" "$WORK/pulled"
"$WORK/helm" pull "oci://$REGISTRY/packory-smoke/synthetic-chart" \
  --version 0.1.0 --plain-http --destination "$WORK/pulled" >/dev/null
cmp "$WORK/synthetic-chart-0.1.0.tgz" "$WORK/pulled/synthetic-chart-0.1.0.tgz"
"$WORK/helm" show chart "oci://$REGISTRY/packory-smoke/synthetic-chart" \
  --version 0.1.0 --plain-http | grep -q "version: 0.1.0"
"$WORK/helm" template synthetic-release \
  "oci://$REGISTRY/packory-smoke/synthetic-chart" \
  --version 0.1.0 --plain-http >"$WORK/rendered.yaml"
grep -q 'kind: Deployment' "$WORK/rendered.yaml"

# Private registry access remains challenged, unrelated repositories cannot read the manifest.
unauthorized="$(curl -s -o /dev/null -w '%{http_code}' \
  "$BASE/v2/packory-smoke/synthetic-chart/manifests/0.1.0")"
test "$unauthorized" = "401"
wrong_repo="$(curl -s -o /dev/null -w '%{http_code}' --user "packory:$TOKEN" \
  "$BASE/v2/other/synthetic-chart/manifests/0.1.0")"
test "$wrong_repo" = "404"
echo 'Helm 3 OCI native push/pull/show/template and private access tests passed.'

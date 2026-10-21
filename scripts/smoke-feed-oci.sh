#!/usr/bin/env bash
# Private OCI E0: pinned Helm native push/pull/template against independent feeds.
# HTTP binds only to loopback. This does not claim universal OCI/Helm conformance.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
BASE="http://127.0.0.1:18097"
REGISTRY="127.0.0.1:18097"
HELM_VERSION="v3.22.0"
HELM_SHA256="1e4ab49e429626cf6c6958d914248b78c9730803c2751b87627e171dc800e7bb"
ADMIN="$(openssl rand -hex 32)"
PID=""
cleanup() {
  status=$?
  trap - EXIT
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  if [ "$status" -ne 0 ]; then
    echo 'Private OCI native acceptance failed; synthetic diagnostics:' >&2
    for log in "$WORK"/helm-*.log; do
      test -f "$log" || continue
      grep -Ei 'error|unauthorized|forbidden|not found|status' "$log" | tail -n 8 >&2 || true
    done
  fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT
for command in dotnet curl openssl python3 tar sha256sum cmp; do command -v "$command" >/dev/null; done
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
HELM="${PACKORY_HELM_BIN:-}"
if [ -z "$HELM" ]; then
  curl --fail --silent --show-error --location \
    "https://get.helm.sh/helm-$HELM_VERSION-linux-amd64.tar.gz" -o "$WORK/helm.tar.gz"
  printf '%s  %s\n' "$HELM_SHA256" "$WORK/helm.tar.gz" | sha256sum -c -
  tar -xzOf "$WORK/helm.tar.gz" linux-amd64/helm >"$WORK/helm"
  chmod 700 "$WORK/helm"
  HELM="$WORK/helm"
fi
"$HELM" version --short | grep -F "$HELM_VERSION"
export HELM_CACHE_HOME="$WORK/helm-cache"
export HELM_CONFIG_HOME="$WORK/helm-config"
export HELM_DATA_HOME="$WORK/helm-data"

start() {
  PACKORY_DATA_DIRECTORY="$WORK/data" \
  PACKORY_PUBLIC_BASE_URL="$BASE" \
  PACKORY_ADMIN_TOKEN="$ADMIN" \
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
  test "$ready" = true || { echo 'Private OCI server did not become ready' >&2; exit 1; }
}
admin_post() {
  curl --fail-with-body -sS -H "Authorization: Bearer $ADMIN" \
    -H 'Content-Type: application/json' --data-binary "$2" "$BASE$1"
}
grant() {
  admin_post '/api/admin/tokens' \
    "{\"role\":\"$1\",\"namespace\":\"feed:$2:$3\",\"expiresInHours\":1}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])'
}
code() { curl -sS -o "$WORK/response" -w '%{http_code}' "$@"; }
login() {
  printf '%s' "$2" | "$HELM" registry login "$REGISTRY" --plain-http \
    --username packory --password-stdin --registry-config "$WORK/$1.json" >"$WORK/helm-login.log" 2>&1
}
start
for feed in alpha-oci beta-oci; do
  admin_post '/api/v1/admin/feeds/drafts' \
    "{\"id\":\"$feed\",\"kind\":\"oci\",\"mode\":\"hosted\",\"displayName\":\"Synthetic OCI\"}" >/dev/null
  admin_post "/api/v1/admin/feeds/$feed/activate" '' >/dev/null
done
A_WRITE="$(grant write alpha-oci publish)"
B_WRITE="$(grant write beta-oci publish)"
A_READ="$(grant read alpha-oci read)"
B_READ="$(grant read beta-oci read)"
for feed in alpha-oci beta-oci; do
  mkdir -p "$WORK/$feed/isolated-chart/templates"
  cat >"$WORK/$feed/isolated-chart/Chart.yaml" <<'CHART'
apiVersion: v2
name: isolated-chart
version: 1.0.0
description: Synthetic private feed isolation fixture
type: application
CHART
  cat >"$WORK/$feed/isolated-chart/templates/proof.yaml" <<CHART
apiVersion: v1
kind: ConfigMap
metadata:
  name: synthetic-isolation-proof
data:
  feed: "$feed"
CHART
  "$HELM" package "$WORK/$feed/isolated-chart" -d "$WORK/$feed" >/dev/null
  if [ "$feed" = alpha-oci ]; then TOKEN="$A_WRITE"; else TOKEN="$B_WRITE"; fi
  login "$feed-write" "$TOKEN"
  "$HELM" push "$WORK/$feed/isolated-chart-1.0.0.tgz" \
    "oci://$REGISTRY/feeds/$feed/charts" --plain-http \
    --registry-config "$WORK/$feed-write.json" >"$WORK/helm-push-$feed.log" 2>&1
done

# A real process restart must retain both tag mappings, manifest graphs and bytes.
kill "$PID"
wait "$PID" 2>/dev/null || true
PID=""
start
for feed in alpha-oci beta-oci; do
  if [ "$feed" = alpha-oci ]; then TOKEN="$A_READ"; else TOKEN="$B_READ"; fi
  login "$feed-read" "$TOKEN"
  rm -rf "$HELM_CACHE_HOME"
  mkdir -p "$HELM_CACHE_HOME" "$WORK/pull-$feed"
  "$HELM" pull "oci://$REGISTRY/feeds/$feed/charts/isolated-chart" --version 1.0.0 \
    --plain-http --registry-config "$WORK/$feed-read.json" --destination "$WORK/pull-$feed" \
    >"$WORK/helm-pull-$feed.log" 2>&1
  cmp "$WORK/$feed/isolated-chart-1.0.0.tgz" "$WORK/pull-$feed/isolated-chart-1.0.0.tgz"
  "$HELM" template synthetic "oci://$REGISTRY/feeds/$feed/charts/isolated-chart" --version 1.0.0 \
    --plain-http --registry-config "$WORK/$feed-read.json" >"$WORK/template-$feed.yaml"
  grep -Fq "feed: \"$feed\"" "$WORK/template-$feed.yaml"
done

URL="$BASE/v2/feeds/alpha-oci/charts/isolated-chart/manifests/1.0.0"
test "$(code "$URL")" = 401
test "$(code -H "Authorization: Bearer $B_READ" "$URL")" = 403
test "$(code -H "Authorization: Bearer $A_READ" \
  "$BASE/v2/feeds/alpha-oci/charts/absent-chart/manifests/1.0.0")" = 404
test "$(code -H "Authorization: Bearer $ADMIN" \
  "$BASE/v2/charts/isolated-chart/manifests/1.0.0")" = 404
if "$HELM" pull "oci://$REGISTRY/feeds/beta-oci/charts/isolated-chart" --version 1.0.0 \
  --plain-http --registry-config "$WORK/alpha-oci-read.json" --destination "$WORK" \
  >"$WORK/helm-denied.log" 2>&1; then
  echo 'Native Helm crossed a feed grant' >&2
  exit 1
fi
if "$HELM" pull "oci://$REGISTRY/feeds/alpha-oci/charts/absent-chart" --version 1.0.0 \
  --plain-http --registry-config "$WORK/alpha-oci-read.json" --destination "$WORK" \
  >"$WORK/helm-missing.log" 2>&1; then
  echo 'Native Helm did not fail closed on missing content' >&2
  exit 1
fi
admin_post '/api/v1/admin/feeds/alpha-oci/deactivate' '' >/dev/null
test "$(code -H "Authorization: Bearer $A_READ" "$URL")" = 404
test "$(code -H "Authorization: Bearer $B_READ" \
  "$BASE/v2/feeds/beta-oci/charts/isolated-chart/manifests/1.0.0")" = 200
echo 'E0 private OCI: pinned Helm push/pull/template, two-feed isolation, restart and 401/403/404 passed.'

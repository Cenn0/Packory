#!/usr/bin/env bash
# Independent official TypeScript MCP SDK 2.3.1 interoperability smoke.
# The Packory endpoint is loopback-only; runtime credentials are ephemeral.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
BASE_URL="http://127.0.0.1:18087"
TOKEN="$(openssl rand -hex 32)"
SERVER_PID=""
cleanup() {
  status=$?
  trap - EXIT
  if [ -n "$SERVER_PID" ]; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT

if [ ! -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" ]; then
  echo "Build the Packory Release binaries before the MCP TypeScript smoke." >&2
  exit 1
fi
PACKORY_DATA_DIRECTORY="$WORK/data" \
PACKORY_PUBLIC_BASE_URL="$BASE_URL" \
PACKORY_ADMIN_TOKEN="$TOKEN" \
PACKORY_ANONYMOUS_READ=false \
PACKORY_MCP_ENABLED=true \
ASPNETCORE_URLS="$BASE_URL" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
SERVER_PID=$!
ready=false
for _ in $(seq 1 60); do
  if curl -fsS "$BASE_URL/health/ready" >/dev/null 2>&1; then
    ready=true
    break
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then break; fi
  sleep 0.2
done
if [ "$ready" != true ]; then
  echo "Packory MCP test server did not become ready." >&2
  exit 1
fi

# Isolated, no package scripts, exact SDK version. No workspace installs.
mkdir -p "$WORK/sdk"
printf '{"name":"packory-mcp-conformance","private":true,"version":"0.0.0","type":"module"}\n' >"$WORK/sdk/package.json"
npm install --prefix "$WORK/sdk" --ignore-scripts --no-audit --no-fund --save-exact \
  @modelcontextprotocol/client@2.3.1 >/dev/null
cp "$ROOT/scripts/smoke-mcp-typescript-client.mjs" "$WORK/sdk/client.mjs"
PACKORY_TEST_MCP_URL="$BASE_URL/mcp" PACKORY_TEST_TOKEN="$TOKEN" \
  node "$WORK/sdk/client.mjs"
printf 'Official TypeScript MCP v2.3.1 legacy/current HTTP smoke passed.\n'

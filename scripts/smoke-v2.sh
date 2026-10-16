#!/usr/bin/env bash
# Real native-client smoke: Packory V2 is the only configured package source.
set -euo pipefail
BASE_URL="${PACKORY_PUBLIC_BASE_URL:-http://localhost:8080}"
TOKEN="${PACKORY_ADMIN_TOKEN:?PACKORY_ADMIN_TOKEN must be configured}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

curl -fsS -X POST "$BASE_URL/api/v1/repositories" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"slug":"nuget-v2","protocol":"nuget","mode":"proxy","visibility":"public","upstream":"https://api.nuget.org/v3-flatcontainer/"}' >/dev/null
curl -fsS -X POST "$BASE_URL/api/v1/repositories" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"slug":"npm-v2","protocol":"npm","mode":"proxy","visibility":"public","upstream":"https://registry.npmjs.org/"}' >/dev/null

mkdir -p "$WORK/dotnet" && cd "$WORK/dotnet"
dotnet new classlib --framework net10.0 --no-restore -o Client >/dev/null
cd Client
cat > NuGet.Config <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="packory-v2" value="$BASE_URL/r/nuget/nuget-v2/v3/index.json" allowInsecureConnections="true"/></packageSources></configuration>
CONFIG
dotnet package add Newtonsoft.Json --version 13.0.3 --no-restore
NUGET_PACKAGES="$WORK/nuget-cache-1" dotnet restore --configfile NuGet.Config --force --no-cache
NUGET_PACKAGES="$WORK/nuget-cache-2" dotnet restore --configfile NuGet.Config --force --no-cache
curl -fsS "$BASE_URL/r/nuget/nuget-v2/v3/flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg" -o "$WORK/dotnet-package.nupkg"
test -s "$WORK/dotnet-package.nupkg"

mkdir -p "$WORK/npm" && cd "$WORK/npm"
printf '{"name":"packory-v2-smoke","version":"1.0.0","private":true}\n' > package.json
npm install is-number@7.0.0 --package-lock-only --ignore-scripts --no-audit --no-fund \
  --registry="$BASE_URL/r/npm/npm-v2/" --cache "$WORK/npm-metadata-cache"
npm ci --ignore-scripts --no-audit --no-fund --cache "$WORK/npm-client-1" --registry="$BASE_URL/r/npm/npm-v2/"
rm -rf node_modules
npm ci --ignore-scripts --no-audit --no-fund --cache "$WORK/npm-client-2" --registry="$BASE_URL/r/npm/npm-v2/"
node -e '
  const f=require("fs"); const lock=JSON.parse(f.readFileSync("package-lock.json","utf8"));
  if(!lock.packages["node_modules/is-number"].resolved.startsWith(process.env.PACKORY_PUBLIC_BASE_URL+"/r/npm/npm-v2/"))
    throw Error("npm escaped the Packory V2 feed URL");
  if(!require("is-number")(42))throw Error("installed npm package did not run");
'
printf 'Packory V2 smoke finished: dotnet restore and npm ci use Packory-only feeds\n'

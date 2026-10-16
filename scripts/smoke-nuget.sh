#!/usr/bin/env bash
# Verifies dotnet restore truly resolves Newtonsoft.Json through Packory only.
set -euo pipefail
BASE_URL="${PACKORY_PUBLIC_BASE_URL:-http://localhost:8080}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"
dotnet new classlib --framework net10.0 --no-restore -o Probe >/dev/null
cd Probe
cat > NuGet.Config <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="packory" value="${BASE_URL}/nuget/v3/index.json" allowInsecureConnections="true" /></packageSources></configuration>
CONFIG
dotnet package add Newtonsoft.Json --version 13.0.3 --no-restore
dotnet restore --configfile NuGet.Config --force --no-cache
curl --fail --silent --show-error "${BASE_URL}/nuget/v3/flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg" --output "$WORK/actual.nupkg"
test -s "$WORK/actual.nupkg"
printf 'NuGet live mirror smoke succeeded (Newtonsoft.Json 13.0.3)\n'

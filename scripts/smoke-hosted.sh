#!/usr/bin/env bash
# Deterministic end-to-end publish and restore/install using real clients.
# All traffic uses Packory on loopback; no external registry, credentials or private data.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
BASE_URL="http://127.0.0.1:18080"
TOKEN="$(openssl rand -hex 32)"
SERVER_PID=""
cleanup() {
  status=$?
  trap - EXIT
  if [ "$status" -ne 0 ] && [ -f "$WORK/server.log" ]; then
    echo "Local Packory server log for failed smoke run:" >&2
    tail -n 80 "$WORK/server.log" >&2
  fi
  if [ -n "$SERVER_PID" ]; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT

test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" || {
  echo "Run dotnet build Packory.slnx -c Release before the hosted smoke." >&2
  exit 1
}
PACKORY_DATA_DIRECTORY="$WORK/data" \
PACKORY_PUBLIC_BASE_URL="$BASE_URL" \
PACKORY_RESERVED_NUGET_PREFIXES="packory.smoke." \
PACKORY_RESERVED_NPM_SCOPES="@packory-smoke" \
PACKORY_ANONYMOUS_READ=true \
PACKORY_ADMIN_TOKEN="$TOKEN" \
ASPNETCORE_URLS="$BASE_URL" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
SERVER_PID=$!

ready=false
for _ in $(seq 1 50); do
  if curl -fsS "$BASE_URL/health/ready" >/dev/null 2>&1; then
    ready=true
    break
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then break; fi
  sleep 0.2
done
if [ "$ready" != true ]; then
  echo "Packory did not become ready" >&2
  exit 1
fi

# Clear all NuGet sources to prove that the consumer cannot bypass Packory.
cat >"$WORK/NuGet.Config" <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packory" value="$BASE_URL/nuget/v3/index.json" allowInsecureConnections="true" />
  </packageSources>
</configuration>
CONFIG

mkdir -p "$WORK/nuget-library" "$WORK/nuget-consumer"
cat >"$WORK/nuget-library/Packory.Smoke.Hosted.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Packory.Smoke.Hosted</PackageId>
    <Version>1.0.0</Version>
    <Authors>Packory CI</Authors>
    <Description>Packory hosted client compatibility probe</Description>
  </PropertyGroup>
</Project>
PROJECT
printf 'namespace HostedProbe; public static class Marker { public const int Value = 42; }\n' >"$WORK/nuget-library/Marker.cs"
dotnet restore "$WORK/nuget-library/Packory.Smoke.Hosted.csproj" --configfile "$WORK/NuGet.Config" --verbosity quiet
dotnet pack "$WORK/nuget-library/Packory.Smoke.Hosted.csproj" -c Release --no-restore -o "$WORK/packages" --verbosity quiet
dotnet nuget push "$WORK/packages/Packory.Smoke.Hosted.1.0.0.nupkg" \
  --source packory --api-key "$TOKEN" --configfile "$WORK/NuGet.Config" --skip-duplicate
cat >"$WORK/nuget-consumer/Consumer.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Packory.Smoke.Hosted" Version="1.0.0" />
  </ItemGroup>
</Project>
PROJECT
printf 'public static class App { public static int Check() => HostedProbe.Marker.Value; }\n' >"$WORK/nuget-consumer/App.cs"
dotnet restore "$WORK/nuget-consumer/Consumer.csproj" \
  --configfile "$WORK/NuGet.Config" --packages "$WORK/nuget-cache" --force --no-cache --verbosity normal
dotnet build "$WORK/nuget-consumer/Consumer.csproj" \
  -c Release --no-restore --verbosity quiet -p:RestorePackagesPath="$WORK/nuget-cache"
test -s "$WORK/nuget-cache/packory.smoke.hosted/1.0.0/packory.smoke.hosted.1.0.0.nupkg"

# npm publish uses a disposable credential file. The consumer has no credentials.
mkdir -p "$WORK/npm-library" "$WORK/npm-consumer"
cat >"$WORK/npm-library/package.json" <<'JSON'
{"name":"@packory-smoke/demo","version":"1.0.0","description":"Packory native hosted smoke","license":"UNLICENSED","main":"index.js"}
JSON
printf 'module.exports = () => 42;\n' >"$WORK/npm-library/index.js"
printf 'registry=%s/npm/\n//127.0.0.1:18080/npm/:_authToken=%s\n' "$BASE_URL" "$TOKEN" >"$WORK/npm-authrc"
chmod 600 "$WORK/npm-authrc"
: >"$WORK/npm-cleanrc"
npm publish "$WORK/npm-library" \
  --registry="$BASE_URL/npm/" --userconfig="$WORK/npm-authrc" \
  --ignore-scripts --access public --loglevel warn
cat >"$WORK/npm-consumer/package.json" <<'JSON'
{"name":"packory-native-consumer","version":"1.0.0","private":true,"dependencies":{"@packory-smoke/demo":"1.0.0"}}
JSON
npm install --prefix "$WORK/npm-consumer" \
  --registry="$BASE_URL/npm/" --userconfig="$WORK/npm-cleanrc" \
  --ignore-scripts --no-audit --no-fund --cache "$WORK/npm-cache" --loglevel warn
node -e 'if (require(process.argv[1])() !== 42) process.exit(1)' "$WORK/npm-consumer/node_modules/@packory-smoke/demo"
npm ci --prefix "$WORK/npm-consumer" \
  --registry="$BASE_URL/npm/" --userconfig="$WORK/npm-cleanrc" \
  --ignore-scripts --no-audit --no-fund --cache "$WORK/npm-cache-second" --loglevel warn
node -e 'if (require(process.argv[1])() !== 42) process.exit(1)' "$WORK/npm-consumer/node_modules/@packory-smoke/demo"

printf 'Native hosted NuGet and npm smoke tests passed.\n'

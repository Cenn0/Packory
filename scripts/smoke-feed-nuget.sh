#!/usr/bin/env bash
# Native NuGet multi-feed isolation test. Disposable local data + credentials.
set -euo pipefail
umask 077

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
BASE="http://127.0.0.1:18086"
ADMIN="$(openssl rand -hex 32)"
SERVER_PID=""
cleanup() {
  status=$?
  trap - EXIT
  if [ "$status" -ne 0 ] && [ -f "$WORK/server.log" ]; then
    echo "Private NuGet feed smoke failed. Redacted server diagnostics:" >&2
    grep -E '(fail:|error:|Exception)' "$WORK/server.log" | tail -n 30 >&2 || true
  fi
  if [ -n "$SERVER_PID" ]; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT

test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
dotnet --version
PACKORY_ADMIN_TOKEN="$ADMIN" \
PACKORY_ANONYMOUS_READ=false \
PACKORY_DATA_DIRECTORY="$WORK/data" \
PACKORY_PUBLIC_BASE_URL="$BASE" \
ASPNETCORE_URLS="$BASE" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/server.log" 2>&1 &
SERVER_PID=$!
ready=false
for _ in $(seq 1 80); do
  if curl -fsS "$BASE/health/ready" >/dev/null 2>&1; then ready=true; break; fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then break; fi
  sleep 0.2
done
test "$ready" = true

for name in alpha-feed beta-feed; do
  curl -fsS -o /dev/null \
    -H "Authorization: Bearer $ADMIN" -H "Content-Type: application/json" \
    -d "{\"id\":\"$name\",\"kind\":\"nuget\",\"mode\":\"hosted\",\"displayName\":\"Synthetic\"}" \
    "$BASE/api/v1/admin/feeds/drafts"
  curl -fsS -o /dev/null -X POST \
    -H "Authorization: Bearer $ADMIN" \
    "$BASE/api/v1/admin/feeds/$name/activate"
done

new_token() {
  local role="$1" grant="$2"
  curl -fsS -H "Authorization: Bearer $ADMIN" -H "Content-Type: application/json" \
    -d "{\"role\":\"$role\",\"namespace\":\"$grant\",\"expiresInHours\":1}" \
    "$BASE/api/admin/tokens" | python3 -c 'import sys,json; print(json.load(sys.stdin)["token"])'
}
ALPHA_PUBLISH="$(new_token write feed:alpha-feed:publish)"
ALPHA_READ="$(new_token read feed:alpha-feed:read)"
BETA_PUBLISH="$(new_token write feed:beta-feed:publish)"
BETA_READ="$(new_token read feed:beta-feed:read)"

cat >"$WORK/Empty.Config" <<'CFG'
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /></packageSources></configuration>
CFG

for name in alpha beta; do
  dir="$WORK/$name"
  mkdir -p "$dir/project" "$dir/packages" "$dir/consumer"
  cat >"$dir/project/Synthetic.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Packory.Private.Sample</PackageId>
    <Version>1.0.0</Version>
    <Authors>Packory CI</Authors>
    <Description>Private feed isolation fixture</Description>
  </PropertyGroup>
</Project>
PROJECT
  if [ "$name" = alpha ]; then number=111; else number=222; fi
  printf 'namespace PrivatePackage; public static class Marker { public const int Value = %s; }\n' \
    "$number" >"$dir/project/Marker.cs"
  dotnet restore "$dir/project/Synthetic.csproj" --configfile "$WORK/Empty.Config" --verbosity quiet
  dotnet pack "$dir/project/Synthetic.csproj" --no-restore -c Release \
    -o "$dir/packages" --verbosity quiet
  if [ "$name" = alpha ]; then credential="$ALPHA_PUBLISH"; else credential="$BETA_PUBLISH"; fi
  # The NuGet API key authenticates the push operation, not the initial
  # GET of the private V3 service index. Supply scoped HTTP credentials for
  # discovery as well. Keep the disposable config private and outside git.
  cat >"$dir/NuGet.Push.Config" <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packory" value="$BASE/feeds/$name-feed/nuget/v3/index.json" allowInsecureConnections="true" />
  </packageSources>
  <packageSourceCredentials>
    <packory>
      <add key="Username" value="synthetic" />
      <add key="ClearTextPassword" value="$credential" />
      <add key="ValidAuthenticationTypes" value="basic" />
    </packory>
  </packageSourceCredentials>
</configuration>
CONFIG
  chmod 600 "$dir/NuGet.Push.Config"
  dotnet nuget push "$dir/packages/Packory.Private.Sample.1.0.0.nupkg" \
    --source packory --configfile "$dir/NuGet.Push.Config" \
    --api-key "$credential" --skip-duplicate
done

# Different bytes for an IDENTICAL package identity stay feed-local.
a="$(sha256sum "$WORK/alpha/packages/Packory.Private.Sample.1.0.0.nupkg" | cut -d' ' -f1)"
b="$(sha256sum "$WORK/beta/packages/Packory.Private.Sample.1.0.0.nupkg" | cut -d' ' -f1)"
test "$a" != "$b"
path="nuget/v3/flatcontainer/packory.private.sample/1.0.0/packory.private.sample.1.0.0.nupkg"
curl -fsS -H "Authorization: Bearer $ALPHA_READ" \
  "$BASE/feeds/alpha-feed/$path" -o "$WORK/from-alpha.nupkg"
curl -fsS -H "Authorization: Bearer $BETA_READ" \
  "$BASE/feeds/beta-feed/$path" -o "$WORK/from-beta.nupkg"
cmp "$WORK/alpha/packages/Packory.Private.Sample.1.0.0.nupkg" "$WORK/from-alpha.nupkg"
cmp "$WORK/beta/packages/Packory.Private.Sample.1.0.0.nupkg" "$WORK/from-beta.nupkg"
status="$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $ALPHA_READ" \
  "$BASE/feeds/beta-feed/$path")"
test "$status" = 403

for name in alpha beta; do
  if [ "$name" = alpha ]; then credential="$ALPHA_READ"; else credential="$BETA_READ"; fi
  dir="$WORK/$name"
  cat >"$dir/NuGet.Config" <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packory" value="$BASE/feeds/$name-feed/nuget/v3/index.json" allowInsecureConnections="true" />
  </packageSources>
  <packageSourceCredentials>
    <packory>
      <add key="Username" value="synthetic" />
      <add key="ClearTextPassword" value="$credential" />
      <add key="ValidAuthenticationTypes" value="basic" />
    </packory>
  </packageSourceCredentials>
</configuration>
CONFIG
  chmod 600 "$dir/NuGet.Config"
  cat >"$dir/consumer/Consumer.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Packory.Private.Sample" Version="1.0.0" />
  </ItemGroup>
</Project>
PROJECT
  dotnet restore "$dir/consumer/Consumer.csproj" --configfile "$dir/NuGet.Config" \
    --packages "$dir/package-cache" --force --no-cache --verbosity quiet
  cmp "$dir/packages/Packory.Private.Sample.1.0.0.nupkg" \
    "$dir/package-cache/packory.private.sample/1.0.0/packory.private.sample.1.0.0.nupkg"
done

# Every denial runs the native restore client with an explicit source-cleared
# config and new package, HTTP and plugin caches. The successful reads above
# cannot mask a missing credential, a foreign grant or a disabled source.
native_rejection() {
  local label="$1" expected="$2" credential="$3" feed="${4:-alpha-feed}"
  local directory="$WORK/negative-$label"
  mkdir -p "$directory/consumer" "$directory/packages" "$directory/http-cache" \
    "$directory/plugin-cache" "$directory/providers"
  cp "$WORK/alpha/consumer/Consumer.csproj" "$directory/consumer/Consumer.csproj"
  cat >"$directory/NuGet.Config" <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packory" value="$BASE/feeds/$feed/nuget/v3/index.json" allowInsecureConnections="true" />
  </packageSources>
CONFIG
  if [ -n "$credential" ]; then
    cat >>"$directory/NuGet.Config" <<CONFIG
  <packageSourceCredentials><packory>
    <add key="Username" value="synthetic" />
    <add key="ClearTextPassword" value="$credential" />
    <add key="ValidAuthenticationTypes" value="basic" />
  </packory></packageSourceCredentials>
CONFIG
  fi
  printf '</configuration>\n' >>"$directory/NuGet.Config"
  if NUGET_PACKAGES="$directory/packages" NUGET_HTTP_CACHE_PATH="$directory/http-cache" \
    NUGET_PLUGINS_CACHE_PATH="$directory/plugin-cache" NUGET_CREDENTIALPROVIDERS_PATH="$directory/providers" \
    dotnet restore "$directory/consumer/Consumer.csproj" --configfile "$directory/NuGet.Config" \
      --packages "$directory/packages" --force --no-cache --disable-parallel \
      -p:NuGetAudit=false --verbosity minimal >"$directory/client.log" 2>&1; then
    echo "Native NuGet $label unexpectedly succeeded" >&2
    exit 1
  fi
  if ! grep -Eq "(^|[^[:digit:]])$expected([^[:digit:]]|$)" "$directory/client.log"; then
    echo "Native NuGet $label did not return expected HTTP $expected" >&2
    exit 1
  fi
  grep -q 'NU1301\|Unable to load the service index' "$directory/client.log"
}
native_rejection unauthenticated 401 ''
native_rejection foreign-feed 403 "$BETA_READ"
native_rejection missing-feed 404 "$ADMIN" missing-feed
curl -fsS -o /dev/null -X POST -H "Authorization: Bearer $ADMIN" \
  "$BASE/api/v1/admin/feeds/alpha-feed/deactivate"
native_rejection deactivated-feed 404 "$ALPHA_READ"
curl -fsS -H "Authorization: Bearer $BETA_READ" \
  "$BASE/feeds/beta-feed/$path" -o "$WORK/still-beta.nupkg"
cmp "$WORK/beta/packages/Packory.Private.Sample.1.0.0.nupkg" "$WORK/still-beta.nupkg"
printf 'Private hosted NuGet: native publish/restore, independent bytes and fresh-cache native 401/403/404 passed.\n'

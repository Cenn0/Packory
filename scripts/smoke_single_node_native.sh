#!/usr/bin/env bash
# Sourced by smoke_single_node_compose.sh on an isolated disposable CI host.
# Native NuGet and npm clients prove published bytes survive container recreation.
# The caller supplies BASE and a process-local TLS trust bundle for HTTPS.
# This is NOT host reboot, schema upgrade or production recovery evidence.

native_prepare() {
  NATIVE_WORK="$WORK/native"
  mkdir -p "$NATIVE_WORK/nuget-package" "$NATIVE_WORK/nuget-consumer" \
    "$NATIVE_WORK/nuget-output" "$NATIVE_WORK/npm-package" \
    "$NATIVE_WORK/npm-consumer" "$NATIVE_WORK/npm-before" "$NATIVE_WORK/npm-after"
  cat >"$NATIVE_WORK/NuGet.Empty.Config" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /></packageSources></configuration>
XML
  cat >"$NATIVE_WORK/nuget-package/Synthetic.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Packory.Compose.Smoke</PackageId>
    <Version>1.0.0</Version>
    <Authors>Packory CI</Authors>
    <Description>Disposable single-node container smoke</Description>
  </PropertyGroup>
</Project>
XML
  printf 'namespace PackoryCompose; public static class Marker { public const int Value = 42; }\n' >"$NATIVE_WORK/nuget-package/Marker.cs"
  cat >"$NATIVE_WORK/nuget-consumer/Consumer.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="Packory.Compose.Smoke" Version="1.0.0" /></ItemGroup>
</Project>
XML
  dotnet restore "$NATIVE_WORK/nuget-package/Synthetic.csproj" \
    --configfile "$NATIVE_WORK/NuGet.Empty.Config" --verbosity quiet
  dotnet pack "$NATIVE_WORK/nuget-package/Synthetic.csproj" \
    --no-restore -c Release -o "$NATIVE_WORK/nuget-output" --verbosity quiet
  NATIVE_NUPKG="$NATIVE_WORK/nuget-output/Packory.Compose.Smoke.1.0.0.nupkg"
  # NuGet push requires a separate API key argument. Keep a runner-owned,
  # synthetic throwaway copy before the bind-mounted source becomes root-owned.
  cp "$WORK/write" "$NATIVE_WORK/nuget-api-key"
  chmod 600 "$NATIVE_WORK/nuget-api-key"
  test -s "$NATIVE_NUPKG"
  NATIVE_NUGET_SHA="$(sha256sum "$NATIVE_NUPKG" | cut -d' ' -f1)"
  for role in write read; do
    # NuGet v3 discovery authenticates via Basic; push also uses an API key.
    # These are one-run synthetic file secrets, never copied into Git.
    cat >"$NATIVE_WORK/NuGet.$role.Config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="packory" value="$BASE/nuget/v3/index.json" allowInsecureConnections="false" /></packageSources>
  <packageSourceCredentials><packory>
    <add key="Username" value="synthetic" />
    <add key="ClearTextPassword" value="$(cat "$WORK/$role")" />
    <add key="ValidAuthenticationTypes" value="basic" />
  </packory></packageSourceCredentials>
</configuration>
XML
    chmod 600 "$NATIVE_WORK/NuGet.$role.Config"
  done
  cat >"$NATIVE_WORK/npm-package/package.json" <<'JSON'
{"name":"@packory-compose/demo","version":"1.0.0","main":"index.js","license":"UNLICENSED"}
JSON
  printf 'module.exports = () => "compose";\n' >"$NATIVE_WORK/npm-package/index.js"
  cat >"$NATIVE_WORK/npm-consumer/package.json" <<'JSON'
{"name":"packory-compose-consumer","version":"1.0.0","private":true,"dependencies":{"@packory-compose/demo":"1.0.0"}}
JSON
  : >"$NATIVE_WORK/empty.npmrc"
  for role in write read; do
    printf 'registry=%s/npm/\n//%s/npm/:_authToken=%s\nstrict-ssl=true\ncafile=%s\n' \
      "$BASE" "${BASE#*://}" "$(cat "$WORK/$role")" "$SSL_CERT_FILE" >"$NATIVE_WORK/$role.npmrc"
    chmod 600 "$NATIVE_WORK/$role.npmrc"
  done
}

native_publish() {
  dotnet nuget push "$NATIVE_NUPKG" --source packory \
    --configfile "$NATIVE_WORK/NuGet.write.Config" --api-key "$(cat "$NATIVE_WORK/nuget-api-key")" \
    >"$NATIVE_WORK/nuget-publish.log" 2>&1 || {
      # Never print raw client logs, package paths or synthetic credentials.
      python3 - "$NATIVE_WORK/nuget-publish.log" <<'PYCODE' >&2
import re,sys
from pathlib import Path
log=Path(sys.argv[1]).read_text(encoding='utf-8', errors='replace')
status=sorted(set(re.findall(r'(?<![0-9])(?:HTTP +)?[1-5][0-9]{2}(?![0-9])|NU[0-9]{4}', log)))
print('Disposable native NuGet publication failed; error codes:', ','.join(status) or 'unclassified')
print('NuGet stage:', 'push_attempted' if 'Pushing ' in log or 'PUT ' in log else 'source_discovery')
PYCODE
      return 1;
    }
  npm publish "$NATIVE_WORK/npm-package" --registry="$BASE/npm/" \
    --userconfig="$NATIVE_WORK/write.npmrc" --globalconfig="$NATIVE_WORK/empty.npmrc" \
    --cache="$NATIVE_WORK/npm-publish-cache" --access public --provenance=false \
    --ignore-scripts --fetch-retries=0 --loglevel error \
    >"$NATIVE_WORK/npm-publish.log" 2>&1 || {
      printf 'Disposable native npm publication failed.\n' >&2; return 1;
    }
}

native_consume() {
  local revision="$1" action=install
  [[ "$revision" != before ]] && action=ci
  mkdir -p "$NATIVE_WORK/npm-$revision"
  local cache="$NATIVE_WORK/nuget-cache-$revision"
  dotnet restore "$NATIVE_WORK/nuget-consumer/Consumer.csproj" \
    --configfile "$NATIVE_WORK/NuGet.read.Config" --packages "$cache" \
    --force --no-cache --verbosity quiet
  local restored="$cache/packory.compose.smoke/1.0.0/packory.compose.smoke.1.0.0.nupkg"
  cmp "$NATIVE_NUPKG" "$restored"
  test "$(sha256sum "$restored" | cut -d' ' -f1)" = "$NATIVE_NUGET_SHA"

  npm "$action" --prefix "$NATIVE_WORK/npm-consumer" \
    --registry="$BASE/npm/" \
    --userconfig="$NATIVE_WORK/read.npmrc" --globalconfig="$NATIVE_WORK/empty.npmrc" \
    --cache="$NATIVE_WORK/npm-cache-$revision" --ignore-scripts --no-audit --no-fund \
    --fetch-retries=0 --loglevel error >"$NATIVE_WORK/npm-$revision.log" 2>&1 || {
      printf 'Disposable native npm install/ci failed.\n' >&2; return 1;
    }
  node -e 'if (require(process.argv[1])() !== "compose") process.exit(1)' \
    "$NATIVE_WORK/npm-consumer/node_modules/@packory-compose/demo"
  npm pack '@packory-compose/demo@1.0.0' \
    --pack-destination "$NATIVE_WORK/npm-$revision" \
    --registry="$BASE/npm/" \
    --userconfig="$NATIVE_WORK/read.npmrc" --globalconfig="$NATIVE_WORK/empty.npmrc" \
    --cache="$NATIVE_WORK/npm-pack-cache-$revision" --ignore-scripts --no-audit \
    --no-fund --fetch-retries=0 --loglevel error \
    >"$NATIVE_WORK/npm-pack-$revision.log" 2>&1 || {
      printf 'Disposable native npm package download failed.\n' >&2; return 1;
    }
  local archive="$NATIVE_WORK/npm-$revision/packory-compose-demo-1.0.0.tgz"
  test -s "$archive"
  # The lock must retain HTTPS Packory origin and exact tarball integrity;
  # a successful install from an unrelated cache or public fallback is no pass.
  node - "$NATIVE_WORK/npm-consumer/package-lock.json" "$archive" "$BASE" <<'JS'
const fs = require('node:fs');
const crypto = require('node:crypto');
const [lockFile, archive, base] = process.argv.slice(2);
const locked = JSON.parse(fs.readFileSync(lockFile, 'utf8'))
  .packages['node_modules/@packory-compose/demo'];
const integrity = 'sha512-' + crypto.createHash('sha512')
  .update(fs.readFileSync(archive)).digest('base64');
if (locked.version !== '1.0.0' || !locked.resolved.startsWith(base + '/npm/') ||
    locked.integrity !== integrity) {
  throw new Error('Synthetic npm lock origin or integrity mismatch');
}
JS
  if [[ "$revision" == before ]]; then
    NATIVE_NPM_SHA="$(sha256sum "$archive" | cut -d' ' -f1)"
  else
    test "$(sha256sum "$archive" | cut -d' ' -f1)" = "$NATIVE_NPM_SHA"
    cmp "$NATIVE_WORK/npm-before/packory-compose-demo-1.0.0.tgz" "$archive"
  fi
}

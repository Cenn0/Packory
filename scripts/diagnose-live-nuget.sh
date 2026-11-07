#!/usr/bin/env bash
# Diagnostic-only comparison of the fixed public NuGet smoke artifact and Packory.
# Intentionally emits status codes only: never response bodies, headers, or URLs.
set -euo pipefail

readonly PACKAGE='newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg'
readonly PUBLIC_BASE='https://api.nuget.org/v3-flatcontainer'
readonly LOCAL_BASE='http://127.0.0.1:8080/nuget/v3/flatcontainer'

probe_status() {
  local label="$1" url="$2" protocol="$3" status
  status="$(curl --proto "=$protocol" --connect-timeout 5 --max-time 15 \
    --retry 0 --max-redirs 0 --silent --output /dev/null \
    --write-out '%{http_code}' "$url" 2>/dev/null)" || status='000'
  if [[ ! "$status" =~ ^[0-9]{3}$ ]]; then status='000'; fi
  printf '%s: HTTP %s\n' "$label" "$status"
}

probe_status 'Public NuGet artifact' "$PUBLIC_BASE/$PACKAGE" https
probe_status 'Public NuGet checksum' "$PUBLIC_BASE/$PACKAGE.sha512" https
probe_status 'Packory loopback artifact' "$LOCAL_BASE/$PACKAGE" http
printf 'Diagnostic only: status codes do not establish root cause.\n'

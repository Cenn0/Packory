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
probe_status 'Packory loopback checksum' "$LOCAL_BASE/$PACKAGE.sha512" http
printf 'Diagnostic only: status codes do not establish root cause.\n'

# Compare official catalog evidence with the actual signed artifact without
# exposing untrusted response text, URLs, hashes, or exception messages.
python3 - <<'PY'
import base64
import hashlib
import gzip
import json
import ssl
import urllib.error
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


opener = urllib.request.build_opener(
    urllib.request.HTTPSHandler(context=ssl.create_default_context()), NoRedirect())
host = "https://api.nuget.org"
artifact = host + "/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"
leaf = host + "/v3/registration5-gz-semver2/newtonsoft.json/13.0.3.json"


def read(url, label, limit):
    try:
        with opener.open(url, timeout=10) as response:
            if response.headers.get('Content-Encoding', '').lower() == 'gzip':
                with gzip.GzipFile(fileobj=response) as decoded:
                    data = decoded.read(limit + 1)
            else:
                data = response.read(limit + 1)
            print(f"{label}: HTTP {response.status}")
            if len(data) > limit:
                print(f"{label}: oversized")
                return None
            return data
    except urllib.error.HTTPError as error:
        print(f"{label}: HTTP {error.code}")
    except Exception:
        print(f"{label}: unavailable")
    return None


try:
    leaf_data = read(leaf, "Official registration leaf", 4 * 1024 * 1024)
    if leaf_data is not None:
        ref = json.loads(leaf_data).get("catalogEntry")
        if isinstance(ref, dict):
            ref = ref.get("@id")
        uri = urllib.parse.urlsplit(ref) if isinstance(ref, str) else None
        if (uri is not None and uri.scheme == "https" and
                uri.netloc.lower() == "api.nuget.org" and
                uri.path.startswith("/v3/catalog0/data/") and
                not uri.query and not uri.fragment):
            catalog_data = read(ref, "Official catalog leaf", 4 * 1024 * 1024)
            package = read(artifact, "Official package bytes", 16 * 1024 * 1024)
            if catalog_data is not None and package is not None:
                catalog = json.loads(catalog_data)
                size_matches = catalog.get("packageSize") == len(package)
                hash_matches = (catalog.get("packageHashAlgorithm", "").upper() == "SHA512"
                    and catalog.get("packageHash") ==
                    base64.b64encode(hashlib.sha512(package).digest()).decode("ascii"))
                identity_matches = (catalog.get("id", "").lower() == "newtonsoft.json"
                                    and catalog.get("version") == "13.0.3")
                print(f"Catalog package identity matches: {identity_matches}")
                print(f"Catalog package size matches: {size_matches}")
                print(f"Catalog package SHA512 matches: {hash_matches}")
        else:
            print("Official catalog reference: absent or outside allowed origin")
except Exception:
    print("Official catalog comparison: invalid metadata")
PY

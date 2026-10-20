#!/usr/bin/env bash
# Two private Go hosted feeds: real native Go CLI and exact per-feed tokens.
# No external origins, credentials or package names are persisted in the repo.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
BASE="http://127.0.0.1:18093"
TLSBASE="https://127.0.0.1:18094"
TLSPID=""
ADMIN="$(openssl rand -hex 32)"
MODULE="example.com/e0-private"
VERSION="v1.2.3"
PID=""
cleanup() {
  code=$?
  trap - EXIT
  if [ -n "$TLSPID" ]; then kill "$TLSPID" 2>/dev/null || true; wait "$TLSPID" 2>/dev/null || true; fi
  if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi
  chmod -R u+w "$WORK" 2>/dev/null || true
  rm -rf "$WORK"
  exit "$code"
}
trap cleanup EXIT
for cmd in go dotnet python3 curl openssl cmp; do
  command -v "$cmd" >/dev/null || { echo "Missing required $cmd" >&2; exit 1; }
done
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
mkdir -p "$WORK/data" "$WORK/module" "$WORK/consumer" "$WORK/home"
printf 'module %s\n\ngo 1.23\n' "$MODULE" > "$WORK/module/go.mod"
python3 - "$WORK/module" "$MODULE" "$VERSION" <<'PY'
from pathlib import Path
import sys
import zipfile
directory, module, version = Path(sys.argv[1]), sys.argv[2], sys.argv[3]
prefix = f"{module}@{version}/"
for flavor in ("alpha", "beta"):
    with zipfile.ZipFile(directory / f"{flavor}.zip", "w") as archive:
        archive.writestr(prefix + "go.mod", (directory / "go.mod").read_bytes())
        archive.writestr(prefix + "hello/hello.go",
                         f'package hello\nfunc Message() string {{ return "{flavor}" }}\n')
PY
PACKORY_DATA_DIRECTORY="$WORK/data" \
PACKORY_ADMIN_TOKEN="$ADMIN" \
PACKORY_PUBLIC_BASE_URL="$BASE" \
PACKORY_ANONYMOUS_READ=true \
ASPNETCORE_URLS="$BASE" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" > "$WORK/server.log" 2>&1 &
PID="$!"
ready=false
for n in $(seq 1 90); do
  if curl -fsS "$BASE/health/ready" >/dev/null 2>&1; then ready=true; break; fi
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
  sleep 0.2
done
test "$ready" = true || { echo "Go feed server failed readiness" >&2; exit 1; }
# Go only transmits GOAUTH/.netrc credentials to an HTTPS proxy. Build an
# ephemeral loopback certificate with a validated IP SAN.
openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1 \
  -keyout "$WORK/localhost.key" -out "$WORK/localhost.pem" \
  -subj "/CN=127.0.0.1" -addext "subjectAltName=IP:127.0.0.1" \
  >"$WORK/certificate.log" 2>&1
cat >"$WORK/tls-proxy.py" <<'PY'
import http.client
import http.server
import ssl
import sys

certificate, key = sys.argv[1], sys.argv[2]

class Forward(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.0"
    def do_GET(self):
        try:
            upstream = http.client.HTTPConnection("127.0.0.1", 18093, timeout=30)
            headers = {k: v for k, v in self.headers.items()
                       if k.lower() not in ("host", "connection", "transfer-encoding")}
            upstream.request("GET", self.path, headers=headers)
            response = upstream.getresponse()
            body = response.read()
            self.send_response(response.status)
            for name, value in response.getheaders():
                if name.lower() not in ("connection", "transfer-encoding",
                                        "content-length", "server", "date"):
                    self.send_header(name, value)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            upstream.close()
        except Exception:
            self.send_error(502)
    def log_message(self, fmt, *args):
        pass

server = http.server.ThreadingHTTPServer(("127.0.0.1", 18094), Forward)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.minimum_version = ssl.TLSVersion.TLSv1_2
context.load_cert_chain(certificate, key)
server.socket = context.wrap_socket(server.socket, server_side=True)
server.serve_forever()
PY
python3 "$WORK/tls-proxy.py" "$WORK/localhost.pem" "$WORK/localhost.key" \
  >"$WORK/tls-proxy.log" 2>&1 &
TLSPID="$!"
tls_ready=false
for n in $(seq 1 75); do
  if curl -fsS --cacert "$WORK/localhost.pem" "$TLSBASE/health/ready" >/dev/null 2>&1; then
    tls_ready=true
    break
  fi
  if ! kill -0 "$TLSPID" 2>/dev/null; then break; fi
  sleep 0.2
done
test "$tls_ready" = true || { echo "Private Go TLS fixture failed readiness" >&2; exit 1; }
admin_post() {
  curl --fail-with-body -sS -H "Authorization: Bearer $ADMIN" \
    -H "Content-Type: application/json" --data-binary "$2" "$BASE$1"
}
grant() {
  admin_post "/api/admin/tokens" \
    "{\"role\":\"$1\",\"namespace\":\"feed:$2:$3\",\"expiresInHours\":1}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])'
}
for feed in go-alpha go-beta; do
  admin_post "/api/v1/admin/feeds/drafts" \
    "{\"id\":\"$feed\",\"kind\":\"go\",\"mode\":\"hosted\",\"displayName\":\"Synthetic Go\"}" >/dev/null
  admin_post "/api/v1/admin/feeds/$feed/activate" '' >/dev/null
done
A_READ="$(grant read go-alpha read)"
B_READ="$(grant read go-beta read)"
A_WRITE="$(grant write go-alpha publish)"
B_WRITE="$(grant write go-beta publish)"
status() {
  curl -sS -o "$WORK/response" -w '%{http_code}' "$@"
}
for feed in go-alpha go-beta; do
  if [ "$feed" = go-alpha ]; then
    WRITE="$A_WRITE"; ZIP="$WORK/module/alpha.zip"
  else
    WRITE="$B_WRITE"; ZIP="$WORK/module/beta.zip"
  fi
  code="$(status -X POST -H "Authorization: Bearer $WRITE" \
    -F "module=$MODULE" -F "version=$VERSION" \
    -F "time=2026-10-20T08:00:00Z" \
    -F "mod=@$WORK/module/go.mod;type=text/plain" \
    -F "zip=@$ZIP;type=application/zip" \
    "$BASE/feeds/$feed/api/go/modules")"
  test "$code" = 201 || {
    echo "Private Go publish failed for $feed: HTTP $code" >&2
    exit 1
  }
done
A_URL="$BASE/feeds/go-alpha/go/$MODULE/@v/$VERSION.zip"
B_URL="$BASE/feeds/go-beta/go/$MODULE/@v/$VERSION.zip"
test "$(status "$A_URL")" = 401
test "$(status -H "Authorization: Bearer $B_READ" "$A_URL")" = 403
test "$(status -H "Authorization: Bearer $A_READ" "$B_URL")" = 403
test "$(status "$BASE/go/$MODULE/@v/$VERSION.zip")" = 404
test "$(status -H "Authorization: Bearer $A_READ" "$A_URL")" = 200
test "$(status -X POST -H "Authorization: Bearer $A_WRITE" \
  -F "module=$MODULE" -F "version=$VERSION" \
  -F "mod=@$WORK/module/go.mod" -F "zip=@$WORK/module/beta.zip" \
  "$BASE/feeds/go-alpha/api/go/modules")" = 409
cat >"$WORK/consumer/go.mod" <<MOD
module example.com/consumer
go 1.23
require $MODULE $VERSION
MOD
cat >"$WORK/consumer/main.go" <<GO
package main
import (
    "fmt"
    "$MODULE/hello"
)
func main() { fmt.Println(hello.Message()) }
GO
# Go's standard netrc credential lookup handles the private proxy's Basic challenge.
# Caches and HOME must be independent to detect cross-feed cache contamination.
for feed in go-alpha go-beta; do
  if [ "$feed" = go-alpha ]; then READ="$A_READ"; EXPECT=alpha; else READ="$B_READ"; EXPECT=beta; fi
  mkdir -p "$WORK/$feed/home" "$WORK/$feed/modcache"
  cp -a "$WORK/consumer" "$WORK/$feed/consumer"
  chmod 700 "$WORK/$feed/home"
  printf 'machine 127.0.0.1 login packory password %s\n' "$READ" >"$WORK/$feed/home/.netrc"
  chmod 600 "$WORK/$feed/home/.netrc"
  (
    cd "$WORK/$feed/consumer"
    HOME="$WORK/$feed/home" NETRC="$WORK/$feed/home/.netrc" \
      SSL_CERT_FILE="$WORK/localhost.pem" GOAUTH=netrc \
      GOPROXY="$TLSBASE/feeds/$feed/go" \
      GOPRIVATE="example.com" GONOPROXY=none GOSUMDB=off \
      GOMODCACHE="$WORK/$feed/modcache" GOTOOLCHAIN=local \
      go mod download "$MODULE@$VERSION" >"$WORK/$feed-download.log" 2>&1
    HOME="$WORK/$feed/home" NETRC="$WORK/$feed/home/.netrc" \
      SSL_CERT_FILE="$WORK/localhost.pem" GOAUTH=netrc \
      GOPROXY="$TLSBASE/feeds/$feed/go" \
      GOPRIVATE="example.com" GONOPROXY=none GOSUMDB=off \
      GOMODCACHE="$WORK/$feed/modcache" GOTOOLCHAIN=local \
      go build -o "$WORK/$feed-client" ./... >"$WORK/$feed-build.log" 2>&1
  ) || {
    echo "Native Go client rejected $feed" >&2
    grep -Ei 'error|fail|unrecognized|status|unauthorized|forbidden' "$WORK/$feed-"*.log | tail -n 12 >&2 || true
    exit 1
  }
  test "$("$WORK/$feed-client")" = "$EXPECT"
done
admin_post "/api/v1/admin/feeds/go-alpha/deactivate" '' >/dev/null
test "$(status -H "Authorization: Bearer $A_READ" "$A_URL")" = 404
test "$(status -H "Authorization: Bearer $B_READ" "$B_URL")" = 200
echo "E0 private Go: two-feed CLI download/build isolation, immutable bundles and auth passed."

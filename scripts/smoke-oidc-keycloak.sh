#!/usr/bin/env bash
# End-to-end OIDC JWT validation against a disposable, TLS-enabled Keycloak realm.
# Requires Docker, .NET 10, curl, Python 3, OpenSSL and sudo trust-store update.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
KEYCLOAK_CONTAINER="packory-keycloak-oidc-smoke-$$"
PACKORY_PID=""
cleanup() {
  status=$?
  trap - EXIT
  if [ "$status" -ne 0 ]; then
    echo "Keycloak integration failed; server diagnostic logs:" >&2
    test ! -f "$WORK/packory.log" || tail -n 80 "$WORK/packory.log" >&2
    docker logs --tail 80 "$KEYCLOAK_CONTAINER" 2>/dev/null || true
  fi
  if [ -n "$PACKORY_PID" ]; then kill "$PACKORY_PID" 2>/dev/null || true; wait "$PACKORY_PID" 2>/dev/null || true; fi
  docker rm -f "$KEYCLOAK_CONTAINER" >/dev/null 2>&1 || true
  sudo rm -f /usr/local/share/ca-certificates/packory-test-keycloak.crt
  sudo update-ca-certificates >/dev/null 2>&1 || true
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT
test -f "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll"
mkdir -p "$WORK/tls" "$WORK/realm"
# Keycloak runs as an unprivileged container user and must be able to traverse mounts.
chmod 755 "$WORK" "$WORK/tls" "$WORK/realm"
CLIENT_SECRET="$(openssl rand -hex 32)"
openssl req -x509 -newkey rsa:3072 -nodes -sha256 -days 1 \
  -keyout "$WORK/tls/server.key" -out "$WORK/tls/server.pem" \
  -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" \
  >/dev/null 2>&1
chmod 644 "$WORK/tls/server.key"
sudo cp "$WORK/tls/server.pem" /usr/local/share/ca-certificates/packory-test-keycloak.crt
sudo update-ca-certificates >/dev/null

# All test credentials are ephemeral. No real identities or secrets enter the repository.
python3 - "$WORK/realm/packory-test-realm.json" "$CLIENT_SECRET" <<'PY'
import json
import sys
path, secret = sys.argv[1:]
realm = {
    "realm": "packory-test", "enabled": True,
    "clientScopes": [
        {"name": "packory.read", "protocol": "openid-connect",
         "attributes": {"include.in.token.scope": "true"}},
        {"name": "packory.write", "protocol": "openid-connect",
         "attributes": {"include.in.token.scope": "true"}},
        {"name": "packory.validation.read", "protocol": "openid-connect",
         "attributes": {"include.in.token.scope": "true"}}
    ],
    "clients": [{
        "clientId": "packory-ci", "enabled": True, "protocol": "openid-connect",
        "secret": secret, "clientAuthenticatorType": "client-secret",
        "publicClient": False, "serviceAccountsEnabled": True,
        "standardFlowEnabled": False, "directAccessGrantsEnabled": False,
        "defaultClientScopes": ["packory.read", "packory.write"],
        "protocolMappers": [
            {"name": "packory-api-audience", "protocol": "openid-connect",
             "protocolMapper": "oidc-audience-mapper",
             "config": {"included.client.audience": "packory-api",
                        "access.token.claim": "true"}}
        ]
    }, {
        "clientId": "packory-validation-ci", "enabled": True, "protocol": "openid-connect",
        "secret": secret, "clientAuthenticatorType": "client-secret",
        "publicClient": False, "serviceAccountsEnabled": True,
        "standardFlowEnabled": False, "directAccessGrantsEnabled": False,
        "defaultClientScopes": ["packory.read", "packory.validation.read"],
        "protocolMappers": [
            {"name": "packory-validation-api-audience", "protocol": "openid-connect",
             "protocolMapper": "oidc-audience-mapper",
             "config": {"included.client.audience": "packory-api",
                        "access.token.claim": "true"}}
        ]
    }]
}
with open(path, "w", encoding="utf-8") as f:
    json.dump(realm, f)
PY

docker run -d --name "$KEYCLOAK_CONTAINER" --network host \
  -v "$WORK/realm:/opt/keycloak/data/import:ro" \
  -v "$WORK/tls:/tls:ro" \
  quay.io/keycloak/keycloak:26.7.3 \
  start-dev --import-realm --hostname=https://localhost:8443 --http-enabled=false \
  --https-port=8443 --https-certificate-file=/tls/server.pem \
  --https-certificate-key-file=/tls/server.key >/dev/null
DISCOVERY="https://localhost:8443/realms/packory-test/.well-known/openid-configuration"
READY=false
for n in $(seq 1 120); do
  if curl -fsS "$DISCOVERY" -o "$WORK/discovery.json" 2>/dev/null; then READY=true; break; fi
  sleep 1
done
if [ "$READY" != true ]; then exit 1; fi

TOKEN_RESPONSE="$(curl -fsS --data-urlencode 'grant_type=client_credentials' \
  --data-urlencode 'client_id=packory-ci' \
  --data-urlencode "client_secret=$CLIENT_SECRET" \
  https://localhost:8443/realms/packory-test/protocol/openid-connect/token)"
ACCESS_TOKEN="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])' <<<"$TOKEN_RESPONSE")"
# Validate the test fixture actually produced scoped and audience-matched claims.
python3 - "$ACCESS_TOKEN" <<'PY'
import base64, json, sys
part = sys.argv[1].split(".")[1]
payload = json.loads(base64.urlsafe_b64decode(part + "=" * (-len(part) % 4)))
assert "packory.write" in payload.get("scope", "").split(), "scope mapper missing"
aud = payload.get("aud", [])
assert "packory-api" in ([aud] if isinstance(aud, str) else aud), "audience mapper missing"
PY

# Second synthetic client receives the dedicated validation scope; the original
# writer has no such scope and MUST be denied validation findings.
VALIDATION_TOKEN_RESPONSE="$(curl -fsS --data-urlencode 'grant_type=client_credentials' \
  --data-urlencode 'client_id=packory-validation-ci' \
  --data-urlencode "client_secret=$CLIENT_SECRET" \
  https://localhost:8443/realms/packory-test/protocol/openid-connect/token)"
VALIDATION_TOKEN="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])' <<<"$VALIDATION_TOKEN_RESPONSE")"
python3 - "$ACCESS_TOKEN" "$VALIDATION_TOKEN" <<'PY'
import base64, json, sys
def scopes(token):
    fragment = token.split(".")[1]
    payload = json.loads(base64.urlsafe_b64decode(fragment + "=" * (-len(fragment) % 4)))
    return set(payload.get("scope", "").split())
assert "packory.validation.read" not in scopes(sys.argv[1])
assert {"packory.read", "packory.validation.read"} <= scopes(sys.argv[2])
PY

mkdir -p "$WORK/validation"
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 \
  -out "$WORK/validation/signer-private.pem" >/dev/null 2>&1
openssl pkey -in "$WORK/validation/signer-private.pem" -pubout \
  -out "$WORK/validation/reviewer-public.pem" >/dev/null 2>&1
python3 - "$WORK/validation/synthetic-run.json" <<'PY'
import datetime, json, sys
now = datetime.datetime.now(datetime.timezone.utc)
fmt = "%Y-%m-%dT%H:%M:%SZ"
value = {
  "schema_version": 1, "alias": "synthetic-run",
  "audience": "packory-validation", "tenant": "local",
  "source_digest": "sha256:" + "a" * 64,
  "run_binding_digest": "sha256:" + "b" * 64,
  "evidence_digest": "sha256:" + "c" * 64,
  "verifier_key_id": "ephemeral-test-verifier",
  "issued_at": (now - datetime.timedelta(minutes=1)).strftime(fmt),
  "expires_at": (now + datetime.timedelta(minutes=30)).strftime(fmt),
  "status": "passed",
  "findings": [{"code": "SYNTHETIC_PASS", "severity": "info"}],
}
with open(sys.argv[1], "w", encoding="utf-8") as out:
    json.dump(value, out, separators=(",", ":"))
PY
openssl dgst -sha256 -sign "$WORK/validation/signer-private.pem" \
  -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:digest \
  -out "$WORK/validation/synthetic-run.sig" "$WORK/validation/synthetic-run.json"
VALIDATION_DIGEST="$(sha256sum "$WORK/validation/synthetic-run.json" | cut -d' ' -f1)"

BASE="http://127.0.0.1:18083"
PACKORY_OIDC_AUTHORITY="https://localhost:8443/realms/packory-test" \
PACKORY_OIDC_AUDIENCE="packory-api" \
PACKORY_VALIDATION_ENABLED=true \
PACKORY_VALIDATION_EXPORT_DIRECTORY="$WORK/validation" \
PACKORY_VALIDATION_VERIFIER_PUBLIC_KEY_PATH="$WORK/validation/reviewer-public.pem" \
PACKORY_VALIDATION_VERIFIER_KEY_ID=ephemeral-test-verifier \
PACKORY_VALIDATION_APPROVED_ALIASES=synthetic-run \
PACKORY_VALIDATION_APPROVED_EXPORTS="synthetic-run=sha256:$VALIDATION_DIGEST" \
PACKORY_ANONYMOUS_READ=false \
PACKORY_DATA_DIRECTORY="$WORK/packory-data" \
PACKORY_PUBLIC_BASE_URL="$BASE" \
ASPNETCORE_URLS="$BASE" \
dotnet "$ROOT/src/Packory/bin/Release/net10.0/Packory.dll" >"$WORK/packory.log" 2>&1 &
PACKORY_PID="$!"
READY=false
for n in $(seq 1 75); do
  if curl -fsS "$BASE/health/ready" >/dev/null 2>&1; then READY=true; break; fi
  if ! kill -0 "$PACKORY_PID" 2>/dev/null; then break; fi
  sleep 0.2
done
if [ "$READY" != true ]; then exit 1; fi
STATUS="$(curl -s -o /dev/null -w '%{http_code}' "$BASE/api/packages")"
test "$STATUS" = 401
STATUS="$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $ACCESS_TOKEN" "$BASE/api/packages")"
test "$STATUS" = 200
STATUS="$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $ACCESS_TOKEN" "$BASE/api/admin/audit")"
test "$STATUS" = 403
# Authorization must reach the Cargo parser: empty cargo publish is a bad request,
# not an authentication failure.
STATUS="$(curl -s -o /dev/null -w '%{http_code}' -X PUT \
  -H "Authorization: Bearer $ACCESS_TOKEN" --data-binary '' "$BASE/cargo/api/v1/crates/new")"
test "$STATUS" = 400
printf 'TLS Keycloak issuer discovery, JWT signature and scope authorization passed.\n'

# Exercise the actual stateful Streamable HTTP endpoint, not a mocked token parser.
PACKORY_TEST_MCP_BASE="$BASE" \
PACKORY_TEST_MCP_REGULAR_TOKEN="$ACCESS_TOKEN" \
PACKORY_TEST_MCP_VALIDATION_TOKEN="$VALIDATION_TOKEN" \
python3 "$ROOT/scripts/smoke-mcp-validation-oidc.py"

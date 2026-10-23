#!/usr/bin/env bash
# Sourced by the disposable single-node CI fixture. No host trust-store writes.
# WORK must be a private mktemp directory; all keys expire with the fixture.

tls_prepare() {
  mkdir -p "$WORK/tls/roots"
  openssl req -x509 -newkey rsa:2048 -nodes -days 1 \
    -subj '/CN=Packory disposable CI root' \
    -addext 'basicConstraints=critical,CA:TRUE' \
    -addext 'keyUsage=critical,keyCertSign,cRLSign' \
    -keyout "$WORK/tls/ca.key" -out "$WORK/tls/ca.pem" >/dev/null 2>&1
  openssl req -newkey rsa:2048 -nodes -subj '/CN=Packory disposable CI server' \
    -keyout "$WORK/tls/server.key" -out "$WORK/tls/server.csr" >/dev/null 2>&1
  cat >"$WORK/tls/server.ext" <<'EXT'
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=IP:127.0.0.1
EXT
  openssl x509 -req -days 1 -in "$WORK/tls/server.csr" \
    -CA "$WORK/tls/ca.pem" -CAkey "$WORK/tls/ca.key" -CAcreateserial \
    -extfile "$WORK/tls/server.ext" -out "$WORK/tls/server.pem" >/dev/null 2>&1
  openssl pkcs12 -export -passout pass: -in "$WORK/tls/server.pem" \
    -inkey "$WORK/tls/server.key" -certfile "$WORK/tls/ca.pem" \
    -out "$WORK/tls/server.pfx" >/dev/null 2>&1
  # Linux .NET/OpenSSL and Node trust only this throwaway CA for this fixture.
  # Certificate verification remains enabled in NuGet, npm and curl.
  export SSL_CERT_FILE="$WORK/tls/ca.pem"
  export SSL_CERT_DIR="$WORK/tls/roots"
  export NODE_EXTRA_CA_CERTS="$WORK/tls/ca.pem"
}

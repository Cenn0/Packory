#!/usr/bin/env bash
# Disposable Linux/amd64 qualification. All credentials and certificates are
# generated for this invocation, removed on exit and never published as evidence.
set -euo pipefail
umask 077

provider="${1:-}"
case "$provider" in postgresql|mysql|mariadb|sqlserver|oracle) ;; *)
  echo 'Usage: qualify-database-connectors.sh {postgresql|mysql|mariadb|sqlserver|oracle}' >&2
  exit 2 ;;
esac
for dependency in docker openssl python3 dotnet sudo; do
  command -v "$dependency" >/dev/null || { echo "Missing dependency: $dependency" >&2; exit 2; }
done
sudo -n true
docker info >/dev/null
repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_dir"
fixture_dir="$(mktemp -d "${TMPDIR:-/tmp}/packory-db-qualification.XXXXXXXX")"
fixture_suffix="$(openssl rand -hex 6)"
fixture_name="packory-db-$provider-$fixture_suffix"
fixture_network="$fixture_name-net"
fixture_trust="/usr/local/share/ca-certificates/$fixture_name.crt"
fixture_image="$(python3 - "$provider" <<'PY'
import json, re, sys
entry = json.load(open('tests/fixtures/database-connectors/images.json'))[sys.argv[1]]
image = entry['image']
assert re.fullmatch(r'[a-z0-9./:-]+@sha256:[0-9a-f]{64}', image), 'Fixture image must be digest pinned'
print(image)
PY
)"
fixture_output="${PACKORY_DB_QUALIFICATION_OUTPUT:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/$fixture_name.json}"
fixture_print_log() {
  python3 - "$1" "$fixture_dir/redact.txt" <<'PY'
from pathlib import Path
import sys
path, redactions = map(Path, sys.argv[1:])
if path.is_file():
    text = path.read_text(errors='replace')
    if redactions.is_file():
        for secret in redactions.read_text().splitlines():
            if secret:
                text = text.replace(secret, '[redacted fixture credential]')
    print(text[-24000:])
PY
}
cleanup() {
  local result=$?
  trap - EXIT
  if (( result != 0 )); then
    # Only this script's synthetic fixture is inspected. Redact every generated
    # credential before emitting any diagnostic output; never publish files.
    docker logs --tail 100 "$fixture_name" >"$fixture_dir/container.log" 2>&1 || true
    for log in pull.log provision.log oracle-tls.log container-trust.log container.log; do
      fixture_print_log "$fixture_dir/$log" || true
    done
  fi
  docker rm -fv "$fixture_name" >/dev/null 2>&1 || true
  docker network rm "$fixture_network" >/dev/null 2>&1 || true
  if sudo -n test -f "$fixture_trust"; then
    sudo -n rm -f -- "$fixture_trust"
    sudo -n update-ca-certificates >/dev/null 2>&1 || true
  fi
  sudo -n rm -rf -- "$fixture_dir"
  exit "$result"
}
trap cleanup EXIT

fixture_admin_password="PkA1$(openssl rand -hex 24)"
fixture_reader_password="PkR1$(openssl rand -hex 24)"
fixture_wallet_password="PkW1$(openssl rand -hex 24)"
if [[ "${GITHUB_ACTIONS:-}" == true ]]; then
  printf '::add-mask::%s\n' "$fixture_admin_password" "$fixture_reader_password" "$fixture_wallet_password"
fi
printf '%s\n' "$fixture_admin_password" "$fixture_reader_password" "$fixture_wallet_password" >"$fixture_dir/redact.txt"

mkdir -p "$fixture_dir/tls" "$fixture_dir/init" "$fixture_dir/results"
chmod 755 "$fixture_dir/tls" "$fixture_dir/init"
openssl req -x509 -newkey rsa:3072 -nodes -days 2 -sha256 \
  -subj '/CN=Packory disposable fixture CA' \
  -addext 'basicConstraints=critical,CA:TRUE' \
  -addext 'keyUsage=critical,keyCertSign,cRLSign' \
  -keyout "$fixture_dir/ca.key" -out "$fixture_dir/tls/ca.crt" >"$fixture_dir/openssl.log" 2>&1
openssl genrsa -traditional -out "$fixture_dir/tls/server.key" 2048 >>"$fixture_dir/openssl.log" 2>&1
openssl req -new -key "$fixture_dir/tls/server.key" -subj '/CN=db.example.invalid' \
  -out "$fixture_dir/server.csr" >>"$fixture_dir/openssl.log" 2>&1
cat >"$fixture_dir/server.ext" <<'EXT'
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:db.example.invalid
EXT
openssl x509 -req -in "$fixture_dir/server.csr" -CA "$fixture_dir/tls/ca.crt" \
  -CAkey "$fixture_dir/ca.key" -CAcreateserial -days 2 -sha256 \
  -extfile "$fixture_dir/server.ext" -out "$fixture_dir/tls/server.crt" >>"$fixture_dir/openssl.log" 2>&1
chmod 644 "$fixture_dir/tls/ca.crt" "$fixture_dir/tls/server.crt"
if [[ "$provider" == oracle ]]; then
  printf '%s' "$fixture_wallet_password" >"$fixture_dir/tls/wallet-password.txt"
  openssl pkcs12 -export -inkey "$fixture_dir/tls/server.key" -in "$fixture_dir/tls/server.crt" \
    -certfile "$fixture_dir/tls/ca.crt" -out "$fixture_dir/tls/server.p12" \
    -passout "file:$fixture_dir/tls/wallet-password.txt" >"$fixture_dir/pkcs12.log" 2>&1
fi
sudo -n install -m 644 "$fixture_dir/tls/ca.crt" "$fixture_trust"
sudo -n update-ca-certificates >"$fixture_dir/trust.log" 2>&1

echo "Starting disposable $provider TLS fixture from $fixture_image"
docker pull --platform linux/amd64 "$fixture_image" >"$fixture_dir/pull.log" 2>&1
docker network create --internal "$fixture_network" >/dev/null
fixture_user=postgres
fixture_port=5432
fixture_memory=1g
case "$provider" in
  mysql|mariadb) fixture_user=mysql; fixture_port=3306; fixture_memory=1g ;;
  sqlserver) fixture_user=mssql; fixture_port=1433; fixture_memory=3g ;;
  oracle) fixture_user=oracle; fixture_port=2484; fixture_memory=4g ;;
esac
fixture_uid="$(docker run --rm --network none --platform linux/amd64 --entrypoint id "$fixture_image" -u "$fixture_user")"
[[ "$fixture_uid" =~ ^[0-9]+$ ]] || { echo 'Invalid fixture user identity' >&2; exit 1; }
sudo -n chown "$fixture_uid:$fixture_uid" "$fixture_dir/tls/server.key"
sudo -n chmod 600 "$fixture_dir/tls/server.key"
if [[ "$provider" == oracle ]]; then
  sudo -n chown "$fixture_uid:$fixture_uid" "$fixture_dir/tls/server.p12" "$fixture_dir/tls/wallet-password.txt"
fi

# All provisioning uses a local Unix socket or a CA-validated TLS connection.
# Database users offered to the runtime own no tables and have SELECT only.
case "$provider" in
  postgresql)
    printf 'POSTGRES_USER=packory_admin\nPOSTGRES_PASSWORD=%s\nPOSTGRES_DB=packory_fixture\n' \
      "$fixture_admin_password" >"$fixture_dir/server.env"
    cat >"$fixture_dir/init/fixture.sql" <<SQL
REVOKE ALL ON DATABASE packory_fixture FROM PUBLIC;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
CREATE ROLE packory_reader LOGIN PASSWORD '$fixture_reader_password';
GRANT CONNECT ON DATABASE packory_fixture TO packory_reader;
GRANT USAGE ON SCHEMA public TO packory_reader;
CREATE TABLE public.packory_connector_fixture (id INTEGER PRIMARY KEY, name VARCHAR(100), amount DECIMAL(12,2));
INSERT INTO public.packory_connector_fixture VALUES (1,'alpha',12.50),(2,'beta',7.25),(3,'gamma',0);
GRANT SELECT ON public.packory_connector_fixture TO packory_reader;
SQL
    chmod 644 "$fixture_dir/init/fixture.sql"
    docker run -d --name "$fixture_name" --platform linux/amd64 --network "$fixture_network" \
      --memory "$fixture_memory" --cpus 2 --pids-limit 512 \
      --security-opt no-new-privileges --env-file "$fixture_dir/server.env" \
      -v "$fixture_dir/tls:/run/packory-tls:ro" -v "$fixture_dir/init:/docker-entrypoint-initdb.d:ro" \
      "$fixture_image" postgres -c ssl=on -c ssl_ca_file=/run/packory-tls/ca.crt \
      -c ssl_cert_file=/run/packory-tls/server.crt -c ssl_key_file=/run/packory-tls/server.key \
      -c log_statement=none >"$fixture_dir/container.id"
    fixture_ready() {
      # The entrypoint's temporary bootstrap server must have exited first.
      docker exec "$fixture_name" sh -c '[ "$(cat /proc/1/comm)" = postgres ] && exec psql -U packory_admin -d packory_fixture -Atqc "SELECT count(*) FROM packory_connector_fixture"' >/dev/null 2>&1
    }
    ;;
  mysql|mariadb)
    if [[ "$provider" == mysql ]]; then
      printf 'MYSQL_ROOT_PASSWORD=%s\nMYSQL_DATABASE=packory_fixture\n' "$fixture_admin_password" >"$fixture_dir/server.env"
      fixture_client=mysql
      fixture_process=mysqld
    else
      printf 'MARIADB_ROOT_PASSWORD=%s\nMARIADB_DATABASE=packory_fixture\n' "$fixture_admin_password" >"$fixture_dir/server.env"
      fixture_client=mariadb
      fixture_process=mariadbd
    fi
    cat >"$fixture_dir/init/fixture.sql" <<SQL
USE packory_fixture;
CREATE TABLE packory_connector_fixture (id INTEGER PRIMARY KEY, name VARCHAR(100), amount DECIMAL(12,2));
INSERT INTO packory_connector_fixture VALUES (1,'alpha',12.50),(2,'beta',7.25),(3,'gamma',0);
CREATE USER 'packory_reader'@'%' IDENTIFIED BY '$fixture_reader_password' REQUIRE SSL;
GRANT SELECT ON packory_fixture.packory_connector_fixture TO 'packory_reader'@'%';
SQL
    chmod 644 "$fixture_dir/init/fixture.sql"
    docker run -d --name "$fixture_name" --platform linux/amd64 --network "$fixture_network" \
      --memory "$fixture_memory" --cpus 2 --pids-limit 512 \
      --security-opt no-new-privileges --env-file "$fixture_dir/server.env" \
      -v "$fixture_dir/tls:/run/packory-tls:ro" -v "$fixture_dir/init:/docker-entrypoint-initdb.d:ro" \
      "$fixture_image" --ssl-ca=/run/packory-tls/ca.crt --ssl-cert=/run/packory-tls/server.crt \
      --ssl-key=/run/packory-tls/server.key --require-secure-transport=ON --local-infile=OFF \
      --skip-name-resolve \
      >"$fixture_dir/container.id"
    fixture_ready() {
      docker exec "$fixture_name" sh -c '[ "$(cat /proc/1/comm)" = "$2" ] && MYSQL_PWD="${MYSQL_ROOT_PASSWORD:-${MARIADB_ROOT_PASSWORD:-}}" "$1" --protocol=socket -uroot --batch --skip-column-names -e "SELECT COUNT(*) FROM packory_fixture.packory_connector_fixture"' sh "$fixture_client" "$fixture_process" >/dev/null 2>&1
    }
    ;;
  sqlserver)
    printf 'ACCEPT_EULA=Y\nMSSQL_PID=Developer\nMSSQL_SA_PASSWORD=%s\n' "$fixture_admin_password" >"$fixture_dir/server.env"
    cat >"$fixture_dir/init/mssql.conf" <<'CONF'
[network]
tlscert = /run/packory-tls/server.crt
tlskey = /run/packory-tls/server.key
forceencryption = 1
tlsprotocols = 1.2
[memory]
memorylimitmb = 2048
CONF
    chmod 644 "$fixture_dir/init/mssql.conf"
    docker run -d --name "$fixture_name" --platform linux/amd64 --network "$fixture_network" \
      --memory "$fixture_memory" --cpus 2 --pids-limit 1024 \
      --security-opt no-new-privileges --env-file "$fixture_dir/server.env" \
      --add-host db.example.invalid:127.0.0.1 \
      -v "$fixture_dir/tls:/run/packory-tls:ro" -v "$fixture_dir/init/mssql.conf:/var/opt/mssql/mssql.conf:ro" \
      "$fixture_image" >"$fixture_dir/container.id"
    docker exec --user 0 "$fixture_name" sh -c \
      'cp /run/packory-tls/ca.crt /usr/local/share/ca-certificates/packory-fixture.crt && update-ca-certificates' \
      >"$fixture_dir/container-trust.log" 2>&1
    fixture_sqlcmd() {
      docker exec -i "$fixture_name" sh -c \
        'export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; exec /opt/mssql-tools18/bin/sqlcmd -S tcp:db.example.invalid,1433 -U sa -N -b -r1' \
        >"$fixture_dir/provision.log" 2>&1
    }
    fixture_ready() { printf 'SELECT 1;\nGO\n' | fixture_sqlcmd; }
    ;;
  oracle)
    # The full image retains Java/orapki required to import our temporary server
    # key. The slim/regular images remove those components.
    printf 'ORACLE_PASSWORD=%s\n' "$fixture_admin_password" >"$fixture_dir/server.env"
    docker run -d --name "$fixture_name" --platform linux/amd64 --network "$fixture_network" \
      --memory "$fixture_memory" --cpus 2 --pids-limit 2048 --shm-size 1g \
      --security-opt no-new-privileges --env-file "$fixture_dir/server.env" \
      -v "$fixture_dir/tls:/run/packory-tls:ro" \
      "$fixture_image" >"$fixture_dir/container.id"
    fixture_sqlplus() {
      docker exec -i "$fixture_name" sqlplus -s '/ as sysdba' >"$fixture_dir/provision.log" 2>&1
    }
    fixture_ready() {
      docker exec "$fixture_name" healthcheck.sh >/dev/null 2>&1
    }
    ;;
esac

fixture_deadline=$((SECONDS + 600))
while ! fixture_ready; do
  if (( SECONDS >= fixture_deadline )); then
    echo "The $provider fixture did not become ready within 600 seconds" >&2
    exit 1
  fi
  if [[ "$(docker inspect -f '{{.State.Running}}' "$fixture_name")" != true ]]; then
    echo "The $provider fixture exited before readiness" >&2
    exit 1
  fi
  sleep 2
done

if [[ "$provider" == sqlserver ]]; then
  fixture_sqlcmd <<SQL
CREATE DATABASE packory_fixture;
GO
CREATE LOGIN packory_reader WITH PASSWORD = '$fixture_reader_password', CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;
GO
USE packory_fixture;
CREATE USER packory_reader FOR LOGIN packory_reader;
CREATE TABLE dbo.packory_connector_fixture (id INTEGER PRIMARY KEY, name VARCHAR(100), amount DECIMAL(12,2));
INSERT INTO dbo.packory_connector_fixture VALUES (1,'alpha',12.50),(2,'beta',7.25),(3,'gamma',0);
GRANT SELECT ON dbo.packory_connector_fixture TO packory_reader;
GO
SQL
elif [[ "$provider" == oracle ]]; then
  fixture_sqlplus <<SQL
WHENEVER SQLERROR EXIT FAILURE
WHENEVER OSERROR EXIT FAILURE
ALTER SESSION SET CONTAINER=FREEPDB1;
CREATE USER packory_owner NO AUTHENTICATION DEFAULT TABLESPACE users QUOTA 2M ON users;
CREATE TABLE packory_owner.packory_connector_fixture (id INTEGER PRIMARY KEY, name VARCHAR2(100), amount NUMBER(12,2));
INSERT INTO packory_owner.packory_connector_fixture VALUES (1,'alpha',12.50);
INSERT INTO packory_owner.packory_connector_fixture VALUES (2,'beta',7.25);
INSERT INTO packory_owner.packory_connector_fixture VALUES (3,'gamma',0);
CREATE USER packory_reader IDENTIFIED BY "$fixture_reader_password";
GRANT CREATE SESSION TO packory_reader;
GRANT SELECT ON packory_owner.packory_connector_fixture TO packory_reader;
CREATE SYNONYM packory_reader.packory_connector_fixture FOR packory_owner.packory_connector_fixture;
COMMIT;
EXIT SUCCESS
SQL
  docker exec -i "$fixture_name" bash -s >"$fixture_dir/oracle-tls.log" 2>&1 <<'ORACLE'
set -euo pipefail
wallet=/opt/oracle/packory-fixture-wallet
wallet_password="$(cat /run/packory-tls/wallet-password.txt)"
mkdir -p "$wallet"
orapki wallet create -wallet "$wallet" -pwd "$wallet_password" -auto_login
orapki wallet import_pkcs12 -wallet "$wallet" -pwd "$wallet_password" \
  -pkcs12file /run/packory-tls/server.p12 -pkcs12pwd "$wallet_password"
orapki wallet create -wallet "$wallet" -pwd "$wallet_password" -auto_login
network_admin="${TNS_ADMIN:-${ORACLE_BASE_HOME:-$ORACLE_HOME}/network/admin}"
cat >"$network_admin/listener.ora" <<'LISTENER'
LISTENER=(DESCRIPTION_LIST=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=0.0.0.0)(PORT=1521))(ADDRESS=(PROTOCOL=TCPS)(HOST=0.0.0.0)(PORT=2484))(ADDRESS=(PROTOCOL=IPC)(KEY=EXTPROC_FOR_FREE))))
WALLET_LOCATION=(SOURCE=(METHOD=FILE)(METHOD_DATA=(DIRECTORY=/opt/oracle/packory-fixture-wallet)))
SSL_CLIENT_AUTHENTICATION=FALSE
LISTENER
cat >"$network_admin/sqlnet.ora" <<'SQLNET'
WALLET_LOCATION=(SOURCE=(METHOD=FILE)(METHOD_DATA=(DIRECTORY=/opt/oracle/packory-fixture-wallet)))
SSL_CLIENT_AUTHENTICATION=FALSE
SSL_VERSION=1.2
DISABLE_OOB=ON
SQLNET
lsnrctl stop
lsnrctl start
sqlplus -s '/ as sysdba' <<'SQL'
WHENEVER SQLERROR EXIT FAILURE
ALTER SYSTEM SET local_listener='(ADDRESS=(PROTOCOL=TCP)(HOST=127.0.0.1)(PORT=1521))' SCOPE=MEMORY;
ALTER SYSTEM REGISTER;
EXIT SUCCESS
SQL
ORACLE
fi

# On a Linux Docker host, an internal bridge is reachable directly from the
# host. Keep the container isolated and do not publish ports: qualify only the
# inspected IPv4 of this container on the single network created above.
docker inspect --format '{{json .NetworkSettings.Networks}}' "$fixture_name" >"$fixture_dir/container-networks.json"
fixture_address="$(python3 - "$fixture_network" "$fixture_dir/container-networks.json" <<'PY'
import ipaddress, json, sys
network_name, path = sys.argv[1:]
with open(path) as source:
    networks = json.load(source)
assert set(networks) == {network_name}, 'Fixture must use only its own internal network'
address = ipaddress.IPv4Address(networks[network_name]['IPAddress'])
private_ranges = map(ipaddress.IPv4Network, ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16'))
assert any(address in network for network in private_ranges), 'Fixture address must be private IPv4'
print(address)
PY
)"
export PACKORY_TEST_DB_PROVIDER="$provider"
export PACKORY_TEST_DB_HOST="$fixture_address" PACKORY_TEST_DB_ADDRESS="$fixture_address"
export PACKORY_TEST_DB_TLS_SERVER_NAME=db.example.invalid
export PACKORY_TEST_DB_PORT="$fixture_port"
export PACKORY_TEST_DB_DATABASE=packory_fixture PACKORY_TEST_DB_USER=packory_reader
export PACKORY_TEST_DB_PASSWORD="$fixture_reader_password"
export PACKORY_TEST_DB_EXPECTED_DN=CN=db.example.invalid
export PACKORY_TEST_DB_CA_FILE="$fixture_dir/tls/ca.crt"
if [[ "$provider" == oracle ]]; then export PACKORY_TEST_DB_DATABASE=FREEPDB1; fi

# No general database connection strings or privileged fixture credentials go
# into the test process. Do not print raw container logs or upload TRX files.
fixture_run_tests() {
  local filter="$1" phase="$2" minimum="$3" result=0
  dotnet test tests/Packory.Tests/Packory.Tests.csproj -c Release --no-build \
    --filter "$filter" --logger "trx;LogFileName=$phase.trx" \
    --results-directory "$fixture_dir/results" >"$fixture_dir/$phase.log" 2>&1 || result=$?
  fixture_print_log "$fixture_dir/$phase.log"
  (( result == 0 )) || return "$result"
  python3 - "$fixture_dir/results/$phase.trx" "$minimum" <<'PY'
import sys
import xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
results = root.findall('.//t:UnitTestResult', ns)
assert len(results) >= int(sys.argv[2]), 'Required native database tests did not run'
assert all(result.attrib.get('outcome') == 'Passed' for result in results), 'Native database test failed or was skipped'
PY
}
fixture_run_tests 'FullyQualifiedName~Packory.Tests.DatabaseConnectorNativeProviderTests' trusted 6

# A wrong hostname alone does not prove that the certificate chain is checked.
# Drop only this fixture's CA and use a fresh test process so no TLS session or
# in-process trust cache can turn this into a misleading successful connection.
sudo -n rm -f -- "$fixture_trust"
sudo -n update-ca-certificates >"$fixture_dir/trust-untrusted.log" 2>&1
export PACKORY_TEST_DB_EXPECT_UNTRUSTED=true
fixture_run_tests 'FullyQualifiedName~Packory.Tests.DatabaseConnectorUntrustedCertificateTests' untrusted 1
unset PACKORY_TEST_DB_EXPECT_UNTRUSTED
sudo -n install -m 644 "$fixture_dir/tls/ca.crt" "$fixture_trust"
sudo -n update-ca-certificates >"$fixture_dir/trust-restored.log" 2>&1

# The positive control after restoring trust rules out an unavailable fixture
# masquerading as certificate rejection in the previous phase.
fixture_run_tests 'FullyQualifiedName~Packory.Tests.DatabaseConnectorNativeProviderTests.Native_probe_and_typed_parameter_query_use_real_tls_and_return_fixture_data' restored 1
python3 - "$provider" "$fixture_image" "$fixture_dir/results" "$fixture_output" <<'PY'
from pathlib import Path
from datetime import datetime, timezone
import json, os, re, subprocess, sys
import xml.etree.ElementTree as ET
provider, image, reports, output = sys.argv[1:]
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
phases = {}
for phase, minimum in [('trusted', 6), ('untrusted', 1), ('restored', 1)]:
    root = ET.parse(Path(reports) / (phase + '.trx')).getroot()
    results = root.findall('.//t:UnitTestResult', ns)
    assert len(results) >= minimum, 'Required native database tests did not run'
    assert all(result.attrib.get('outcome') == 'Passed' for result in results), 'Native database test failed or was skipped'
    phases[phase] = len(results)
checked_out_sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
sha = os.environ.get('SOURCE_SHA') or checked_out_sha
assert re.fullmatch(r'[0-9a-f]{40}', sha), 'Invalid source identity'
assert sha == checked_out_sha, 'Evidence source identity differs from the tested checkout'
evidence = {'schema': 1, 'provider': provider, 'sourceSha': sha, 'image': image,
            'completedUtc': datetime.now(timezone.utc).isoformat(),
            'passed': sum(phases.values()), 'failed': 0, 'skipped': 0, 'phases': phases,
            'fixture': 'ephemeral-ca-validated-tls-select-only',
            'scope': 'native connector tests against one disposable Linux/amd64 database; no production certification'}
destination = Path(output)
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_text(json.dumps(evidence, indent=2) + '\n')
print(f'Qualified {provider}: {sum(phases.values())} native test executions, no skipped tests; sanitized evidence: {destination}')
PY

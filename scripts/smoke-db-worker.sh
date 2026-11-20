#!/usr/bin/env bash
# Actual standalone worker container, with no database or external route.
# Credentials, key material and socket volume are disposable, never uploaded.
set -euo pipefail
umask 077
for tool in docker sudo python3 openssl; do
  command -v "$tool" >/dev/null || { echo "Missing dependency: $tool" >&2; exit 2; }
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
tmp="$(mktemp -d)"
suffix="$(openssl rand -hex 6)"
container="packory-db-worker-smoke-$suffix"
volume="packory-db-worker-ipc-$suffix"
network="packory-db-worker-net-$suffix"
cleanup() {
  docker rm -f "$container" >/dev/null 2>&1 || true
  docker volume rm -f "$volume" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  sudo -n rm -rf "$tmp"
}
trap cleanup EXIT

openssl rand -out "$tmp/worker.key" 32
sudo -n chown 1654:1654 "$tmp/worker.key"
sudo -n chmod 0400 "$tmp/worker.key"
docker volume create "$volume" >/dev/null
# Docker Engine 28+ isolated gateway prevents internal bridge access to host
# listeners (plain --internal still permits access via its bridge gateway).
docker network create --driver bridge --internal \
  -o com.docker.network.bridge.gateway_mode_ipv4=isolated "$network" >/dev/null
docker network inspect "$network" | python3 -c '
import json,sys
net=json.load(sys.stdin)[0]
assert net["Internal"] is True
assert net["Options"]["com.docker.network.bridge.gateway_mode_ipv4"] == "isolated"
'

docker run -d --name "$container" --network "$network" \
  --user 1654:1654 --read-only --cap-drop ALL \
  --security-opt no-new-privileges --pids-limit 64 \
  --memory 768m --cpus 1.0 \
  --tmpfs /tmp:rw,noexec,nosuid,size=64m \
  --mount "type=volume,src=$volume,dst=/run/packory-db-ipc" \
  --mount "type=bind,src=$tmp/worker.key,dst=/run/secrets/packory-db-worker-key,readonly" \
  -e PACKORY_DB_CONNECTORS_ENABLED=true \
  -e PACKORY_DB_CONNECTOR_ALLOWED_TARGETS=postgresql://8.8.8.8:5432 \
  -e PACKORY_DB_WORKER_SOCKET=/run/packory-db-ipc/worker.sock \
  -e PACKORY_DB_WORKER_IPC_KEY_FILE=/run/secrets/packory-db-worker-key \
  packory:db-worker-ci >/dev/null

ready=false
for attempt in $(seq 1 35); do
  if docker exec "$container" /bin/sh -c \
    'test -S /run/packory-db-ipc/worker.sock' >/dev/null 2>&1; then
    ready=true
    break
  fi
  if [[ "$(docker inspect --format '{{.State.Running}}' "$container")" != true ]]; then
    echo "Database worker exited during startup." >&2
    exit 1
  fi
  sleep 1
done
[[ "$ready" == true ]] || { echo "Database worker UDS not available." >&2; exit 1; }

docker inspect "$container" | python3 -c '
import json,sys
c=json.load(sys.stdin)[0]
h=c["HostConfig"]
assert c["Config"]["User"]=="1654:1654"
assert h["ReadonlyRootfs"] and not h["Privileged"]
assert h["NetworkMode"].startswith("packory-db-worker-net-") and h["CapDrop"]==["ALL"]
assert "no-new-privileges" in h["SecurityOpt"]
assert h["Memory"] > 0 and h["PidsLimit"] == 64
assert not c["NetworkSettings"]["Ports"]
mounts=c["Mounts"]
assert len(mounts)==2
assert not any(x["Destination"].startswith("/data") or "mcp-connector-keyring" in x["Destination"] for x in mounts)
assert all(x["Destination"] in ("/run/secrets/packory-db-worker-key", "/run/packory-db-ipc") for x in mounts)
'
mountpoint="$(docker volume inspect -f '{{ .Mountpoint }}' "$volume")"
sudo -n python3 scripts/smoke-db-worker-ipc.py "$mountpoint/worker.sock" "$tmp/worker.key"
[[ "$(docker inspect --format '{{.State.Running}}' "$container")" == true ]] ||
  { echo "Database worker crashed after negative IPC probes." >&2; exit 1; }
echo "DB worker process/container security smoke succeeded."

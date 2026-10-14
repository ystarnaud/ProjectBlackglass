#!/usr/bin/env bash
# Local exposure check for one Compose project on bg-dev-services01 (spec §4.5). Read-only.
# FAIL if: Docker Engine < 28; a container publishes on a non-loopback address; a project network is neither internal
# nor pinned to host_binding_ipv4=127.0.0.1; anything listens on 5432; 5080 or 55432 listen off loopback.
# The off-host scans (IPv4/IPv6, TCP/UDP, from the PC and the peer VM) are Stage 17.
set -euo pipefail
project="${BLACKGLASS_COMPOSE_PROJECT:-blackglass-accounts}"
fail=0
bad() { echo "FAIL: $*"; fail=1; }

version="$(docker version --format '{{.Server.Version}}')"
echo "docker engine $version"
(( ${version%%.*} >= 28 )) || bad "Docker Engine $version is older than 28"

echo "-- published ports"
while read -r name ports; do
  echo "$name ${ports:-<none>}"
  for mapping in ${ports//,/ }; do
    [[ "$mapping" == *'->'* ]] || continue
    [[ "$mapping" == 127.0.0.1:* ]] || bad "$name publishes $mapping"
  done
done < <(docker ps --filter "label=com.docker.compose.project=$project" --format '{{.Names}} {{.Ports}}')

echo "-- networks"
while read -r net; do
  internal="$(docker network inspect -f '{{.Internal}}' "$net")"
  binding="$(docker network inspect -f '{{index .Options "com.docker.network.bridge.host_binding_ipv4"}}' "$net")"
  echo "$net internal=$internal host_binding_ipv4=$binding"
  [[ "$internal" == true || "$binding" == 127.0.0.1 ]] || bad "network $net can publish off loopback"
done < <(docker network ls --filter "label=com.docker.compose.project=$project" --format '{{.Name}}')

echo "-- TCP listeners"
ss -H -ltn
while read -r addr; do
  case "$addr" in
    *:5432) bad "something listens on $addr (PostgreSQL must not be published)" ;;
    127.0.0.1:5080|127.0.0.1:55432) ;;
    *:5080|*:55432) bad "$addr is not loopback" ;;
  esac
done < <(ss -H -ltn | awk '{print $4}')
echo "-- UDP listeners"
ss -H -lun

if (( fail )); then echo "EXPOSURE FAIL"; exit 1; fi
echo "EXPOSURE PASS"

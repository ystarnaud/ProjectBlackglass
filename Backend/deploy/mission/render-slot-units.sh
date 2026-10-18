#!/usr/bin/env bash
# Renders the mission slot templates (security correction C1): blackglass-mission-NN@.service for NN = 00 .. slots-1, each binding
# only port portBase + NN, from blackglass-mission-slot.service.in. install.sh runs it into /etc/systemd/system; the tests into a
# temporary directory. portBase and slots must be the agent's (agent.json portBase and localCapacity) and the backend's
# registration (port_base, capacity no larger than slots).
#   render-slot-units.sh <portBase 1024..65535> <slots 1..64> <out-dir>
set -euo pipefail
port_base="${1:-}" slots="${2:-}" out="${3:-}"
[[ "$port_base" =~ ^[1-9][0-9]{3,4}$ && "$slots" =~ ^[1-9][0-9]?$ && -d "$out" ]] || { echo "usage: render-slot-units.sh <portBase> <slots> <out-dir>" >&2; exit 64; }
(( port_base >= 1024 && slots <= 64 && port_base + slots <= 65536 )) || { echo "portBase must be 1024 or more, slots 1..64, and every port at most 65535" >&2; exit 64; }
here="$(cd "$(dirname "$0")" && pwd)"
template="$here/blackglass-mission-slot.service.in"
for ((n = 0; n < slots; n++)); do
  slot="$(printf '%02d' "$n")"
  sed -e "s/@SLOT@/$slot/g" -e "s/@PORT@/$((port_base + n))/g" "$template" > "$out/blackglass-mission-$slot@.service"
done

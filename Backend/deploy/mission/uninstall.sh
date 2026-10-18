#!/usr/bin/env bash
# Rolls the mission host back (run as root on bg-dev-mission01, --yes required). It stops and disables the agent and the tunnel and
# removes the units, the polkit rule and the stub link. It NEVER deletes result evidence: /var/lib/blackglass/{outbox,exits,relay,agent}
# and /var/log/blackglass stay, as do the users that own them and the agent releases (a previous release is restored with
#   ln -sfn /opt/blackglass/agent/releases/<rev> /opt/blackglass/agent/current && systemctl restart blackglass-agent).
# Running mission units are left alone unless --stop-missions is given (then each is stopped through systemd).
#   uninstall.sh --yes [--stop-missions]
set -euo pipefail
yes=0 stop_missions=0
for a in "$@"; do case "$a" in --yes) yes=1 ;; --stop-missions) stop_missions=1 ;; *) echo "unknown argument: $a" >&2; exit 64 ;; esac; done
[[ "$(id -u)" == 0 ]] || { echo "run as root" >&2; exit 77; }
[[ "$yes" == 1 ]] || { echo "refusing without --yes" >&2; exit 64; }
systemctl disable --now blackglass-agent.service blackglass-tunnel.service 2>/dev/null || true
if [[ "$stop_missions" == 1 ]]; then
  for unit in $(systemctl list-units --all --plain --no-legend 'blackglass-mission@*.service' | awk '{print $1}'); do systemctl stop -- "$unit"; done
fi
rm -f /etc/systemd/system/blackglass-agent.service /etc/systemd/system/blackglass-tunnel.service /etc/systemd/system/blackglass-mission@.service
rm -f /etc/polkit-1/rules.d/60-blackglass-mission.rules
[[ "$(readlink -f /opt/blackglass/mission/current 2>/dev/null || true)" == /opt/blackglass/mission/releases/stub ]] && rm -f /opt/blackglass/mission/current
systemctl daemon-reload
echo "uninstalled; evidence kept under /var/lib/blackglass and /var/log/blackglass"

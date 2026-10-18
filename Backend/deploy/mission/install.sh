#!/usr/bin/env bash
# Installs the mission host on bg-dev-mission01 (run as root, from a copy of Backend/deploy/mission). NOT RUN until the owner
# authorizes the Stage 10 infrastructure checkpoint. Idempotent; changes nothing without --yes.
#   install.sh --yes --release <agent-release-dir> --services-host <address> --known-hosts <file> --port-base <n> --slots <n>
#              [--stub] [--start]
# --port-base and --slots must equal the backend's registration of this host (port_base; capacity no larger than slots) and
# agent.json's portBase and localCapacity.
# It:
#   1. creates the users (sysusers.d: the agent and the tunnel; mission units use per-slot dynamic users) and directories (tmpfiles.d);
#   2. installs the agent release (verified against its RELEASE hashes) under /opt/blackglass/agent/releases/<rev>, root-owned,
#      and points /opt/blackglass/agent/current at it;
#   3. with --stub, installs the stub mission release under /opt/blackglass/mission/releases/stub and points
#      /opt/blackglass/mission/current at it (only if current is absent or already the stub); /opt/blackglass/server is never touched;
#   4. renders and installs one mission template per slot (blackglass-mission-NN@.service, security correction C1), the credential
#      broker's socket and service (I3), the agent and tunnel units, the polkit rule, /etc/blackglass/agent/agent.json (if absent)
#      and the tunnel configuration with the pinned host key; creates the tunnel key pair if absent and prints its public key;
#   5. reloads systemd and starts the broker's socket; with --start, enables and starts the tunnel and the agent (the host secret
#      must be installed first).
set -euo pipefail
yes=0 release="" services_host="" known_hosts="" stub=0 start=0 port_base="" slots=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes) yes=1; shift ;;
    --release) release="${2:?}"; shift 2 ;;
    --services-host) services_host="${2:?}"; shift 2 ;;
    --known-hosts) known_hosts="${2:?}"; shift 2 ;;
    --port-base) port_base="${2:?}"; shift 2 ;;
    --slots) slots="${2:?}"; shift 2 ;;
    --stub) stub=1; shift ;;
    --start) start=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 64 ;;
  esac
done
[[ "$(id -u)" == 0 ]] || { echo "run as root" >&2; exit 77; }
[[ "$yes" == 1 ]] || { echo "refusing without --yes (the infrastructure checkpoint must be authorized)" >&2; exit 64; }
[[ -d "$release" && -f "$release/RELEASE" ]] || { echo "--release <dir with RELEASE> is required" >&2; exit 64; }
[[ -n "$services_host" && -s "$known_hosts" ]] || { echo "--services-host and a pinned --known-hosts file are required" >&2; exit 64; }
[[ "$services_host" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$ ]] || { echo "--services-host must be a host name or IPv4 address" >&2; exit 64; }
here="$(cd "$(dirname "$0")" && pwd)"
! grep -l $'\r' "$here"/*.sh "$here/stub/run-mission" "$here/render-slot-units.sh" || { echo "refusing: CRLF line endings in a script (copy the deploy files with LF)" >&2; exit 78; }
units="$(mktemp -d)"
trap 'rm -rf "$units"' EXIT
"$here/render-slot-units.sh" "$port_base" "$slots" "$units" || { echo "--port-base and --slots are required and must be valid" >&2; exit 64; }
if ls -d /etc/systemd/system/blackglass-mission*.d /etc/systemd/system/blackglass-mission-.service.d /run/systemd/system/blackglass-mission* 2>/dev/null | grep -q .; then
  echo "refusing: a drop-in or runtime unit changes the mission templates" >&2; exit 78
fi
[[ -e /etc/systemd/system/blackglass-mission@.service ]] && { echo "refusing: the retired shared-uid template is installed; run uninstall.sh first" >&2; exit 78; }
grep -Eq '^blackglass-m[0-9]{2}:' /etc/passwd && { echo "refusing: a static blackglass-mNN user exists (the slots' users must be dynamic)" >&2; exit 78; }
if [[ -e /etc/blackglass/agent/agent.json ]]; then
  grep -Eq "\"portBase\": *$port_base[,}[:space:]]" /etc/blackglass/agent/agent.json && grep -Eq "\"localCapacity\": *$slots[,}[:space:]]" /etc/blackglass/agent/agent.json \
    || { echo "refusing: agent.json's portBase and localCapacity differ from --port-base and --slots" >&2; exit 78; }
fi

# 1. users and directories (a minimal system may have neither configuration directory yet)
install -d -m 0755 -o root -g root /etc/sysusers.d /etc/tmpfiles.d /etc/polkit-1/rules.d
install -m 0644 -o root -g root "$here/sysusers.d/blackglass.conf" /etc/sysusers.d/blackglass.conf
systemd-sysusers /etc/sysusers.d/blackglass.conf
install -m 0644 -o root -g root "$here/tmpfiles.d/blackglass.conf" /etc/tmpfiles.d/blackglass.conf
systemd-tmpfiles --create /etc/tmpfiles.d/blackglass.conf

# 2. the agent release, verified, root-owned
(cd "$release" && sed -n '/^[0-9a-f]\{64\} /p' RELEASE | sha256sum --check --strict --quiet) || { echo "release hashes do not match" >&2; exit 78; }
# Every file is listed and every listed file exists: nothing unhashed is installed.
listed="$(sed -n 's/^[0-9a-f]\{64\}  //p' "$release/RELEASE" | sort)"
present="$(cd "$release" && find . -type f ! -name RELEASE -printf '%P\n' | sort)"
[[ "$listed" == "$present" ]] || { echo "the release holds files RELEASE does not list (or the reverse)" >&2; exit 78; }
for required in blackglass-agent blackglass-credential-broker record-exit slot-guard; do
  grep -qx "$required" <<<"$listed" || { echo "the release lacks $required" >&2; exit 78; }
done
revision="$(sed -n 's/^revision=//p' "$release/RELEASE")"
[[ "$revision" =~ ^[0-9a-f]{7,40}$ ]] || { echo "bad release revision" >&2; exit 78; }
target="/opt/blackglass/agent/releases/$revision"
[[ ! -e "$target" ]] || { echo "release $revision is already installed; switch to it with ln -sfn instead" >&2; exit 78; }
install -d -m 0755 -o root -g root /opt/blackglass/agent/releases "$target"
cp -a "$release/." "$target/"
chown -R root:root "$target"
chmod -R u=rwX,go=rX "$target"
chmod 0755 "$target/blackglass-agent" "$target/blackglass-credential-broker" "$target/record-exit" "$target/slot-guard"
ln -sfn "$target" /opt/blackglass/agent/current.new && mv -Tf /opt/blackglass/agent/current.new /opt/blackglass/agent/current

# 3. the stub mission release (A10-D7), only when asked
if [[ "$stub" == 1 ]]; then
  install -d -m 0755 -o root -g root /opt/blackglass/mission/releases/stub /etc/blackglass-stub /etc/blackglass-stub/plans
  install -m 0755 -o root -g root "$here/stub/run-mission" /opt/blackglass/mission/releases/stub/run-mission
  current="$(readlink -e /opt/blackglass/mission/current 2>/dev/null || true)" # -e: empty when absent
  if [[ -z "$current" || "$current" == /opt/blackglass/mission/releases/stub ]]; then
    ln -sfn /opt/blackglass/mission/releases/stub /opt/blackglass/mission/current.new && mv -Tf /opt/blackglass/mission/current.new /opt/blackglass/mission/current
  else
    echo "note: /opt/blackglass/mission/current points at $current; left unchanged"
  fi
fi

# 4. units, polkit, configuration, tunnel
for unit in blackglass-agent.service blackglass-credential.socket blackglass-credential@.service; do install -m 0644 -o root -g root "$here/$unit" "/etc/systemd/system/$unit"; done
for file in "$units"/blackglass-mission-*@.service; do install -m 0644 -o root -g root "$file" "/etc/systemd/system/$(basename "$file")"; done
# A slot beyond --slots keeps no template (only when none of its units is live).
for file in /etc/systemd/system/blackglass-mission-[0-9][0-9]@.service; do
  slot="$(basename "$file" | sed 's/^blackglass-mission-\([0-9][0-9]\)@.service$/\1/')"
  if (( 10#$slot >= slots )) && ! systemctl list-units --no-legend --plain "blackglass-mission-$slot@*.service" --state=active,activating,deactivating | grep -q .; then rm -f "$file"; fi
done
install -m 0644 -o root -g root "$here/tunnel/blackglass-tunnel.service" /etc/systemd/system/blackglass-tunnel.service
install -m 0644 -o root -g root "$here/60-blackglass-mission.rules" /etc/polkit-1/rules.d/60-blackglass-mission.rules
install -d -m 0750 -o root -g blackglass-agent /etc/blackglass/agent
if [[ ! -e /etc/blackglass/agent/agent.json ]]; then
  sed -e "s/\"portBase\": 7000/\"portBase\": $port_base/" -e "s/\"localCapacity\": 64/\"localCapacity\": $slots/" "$here/agent.json.example" > "$units/agent.json"
  install -m 0640 -o root -g blackglass-agent "$units/agent.json" /etc/blackglass/agent/agent.json
fi
install -d -m 0755 -o root -g root /etc/blackglass /etc/blackglass/tunnel
sed "s/SERVICES_HOST/$services_host/" "$here/tunnel/ssh_config" > /etc/blackglass/tunnel/ssh_config.new
install -m 0644 -o root -g root /etc/blackglass/tunnel/ssh_config.new /etc/blackglass/tunnel/ssh_config && rm -f /etc/blackglass/tunnel/ssh_config.new
install -m 0644 -o root -g root "$known_hosts" /etc/blackglass/tunnel/known_hosts
if [[ ! -e /etc/blackglass/tunnel/id_ed25519 ]]; then
  ssh-keygen -q -t ed25519 -N '' -C 'bg-api-tunnel@bg-dev-mission01' -f /etc/blackglass/tunnel/id_ed25519
fi
chown blackglass-tunnel:blackglass-tunnel /etc/blackglass/tunnel/id_ed25519
chmod 0600 /etc/blackglass/tunnel/id_ed25519
echo "tunnel public key (for /etc/ssh/authorized_keys.d/bg-api-tunnel on the services VM):"
cat /etc/blackglass/tunnel/id_ed25519.pub

# 5. systemd
systemctl daemon-reload
# systemd's own view: no drop-in of any kind (prefix, top-level, runtime, control) may change a slot unit (review 2, M-8).
probe_unit="blackglass-mission-00@00000000-0000-4000-8000-000000000000.service"
[[ -z "$(systemctl show -p DropInPaths --value "$probe_unit")" ]] || { echo "refusing: a drop-in changes the slot units: $(systemctl show -p DropInPaths --value "$probe_unit")" >&2; exit 78; }
systemctl enable --now blackglass-credential.socket
if [[ "$start" == 1 ]]; then
  [[ -s /etc/blackglass/agent/host-secret ]] || { echo "install the host secret first (blackglass-agent 0600)" >&2; exit 78; }
  systemctl enable --now blackglass-tunnel.service blackglass-agent.service
fi
echo "installed agent release $revision; slots 00..$(printf '%02d' $((slots - 1))) on ports $port_base..$((port_base + slots - 1))"

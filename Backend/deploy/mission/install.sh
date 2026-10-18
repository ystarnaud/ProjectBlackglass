#!/usr/bin/env bash
# Installs the mission host on bg-dev-mission01 (run as root, from a copy of Backend/deploy/mission). NOT RUN until the owner
# authorizes the Stage 10 infrastructure checkpoint. Idempotent; changes nothing without --yes.
#   install.sh --yes --release <agent-release-dir> --services-host <address> --known-hosts <file> [--stub] [--start]
# It:
#   1. creates the users (sysusers.d) and directories (tmpfiles.d);
#   2. installs the agent release (verified against its RELEASE hashes) under /opt/blackglass/agent/releases/<rev>, root-owned,
#      and points /opt/blackglass/agent/current at it;
#   3. with --stub, installs the stub mission release under /opt/blackglass/mission/releases/stub and points
#      /opt/blackglass/mission/current at it (only if current is absent or already the stub); /opt/blackglass/server is never touched;
#   4. installs the units, the polkit rule, /etc/blackglass/agent/agent.json (if absent) and the tunnel configuration with the
#      pinned host key; creates the tunnel key pair if absent and prints its public key for the services VM;
#   5. reloads systemd; with --start, enables and starts the tunnel and the agent (the host secret must be installed first).
set -euo pipefail
yes=0 release="" services_host="" known_hosts="" stub=0 start=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes) yes=1; shift ;;
    --release) release="${2:?}"; shift 2 ;;
    --services-host) services_host="${2:?}"; shift 2 ;;
    --known-hosts) known_hosts="${2:?}"; shift 2 ;;
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
[[ -e /etc/systemd/system/blackglass-mission@.service.d ]] && { echo "refusing: a drop-in directory for the mission template exists" >&2; exit 78; }

# 1. users and directories
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
grep -qx 'blackglass-agent' <<<"$listed" && grep -qx 'record-exit' <<<"$listed" || { echo "the release lacks blackglass-agent or record-exit" >&2; exit 78; }
revision="$(sed -n 's/^revision=//p' "$release/RELEASE")"
[[ "$revision" =~ ^[0-9a-f]{7,40}$ ]] || { echo "bad release revision" >&2; exit 78; }
target="/opt/blackglass/agent/releases/$revision"
[[ ! -e "$target" ]] || { echo "release $revision is already installed; switch to it with ln -sfn instead" >&2; exit 78; }
install -d -m 0755 -o root -g root /opt/blackglass/agent/releases "$target"
cp -a "$release/." "$target/"
chown -R root:root "$target"
chmod -R u=rwX,go=rX "$target"
chmod 0755 "$target/blackglass-agent" "$target/record-exit"
ln -sfn "$target" /opt/blackglass/agent/current.new && mv -Tf /opt/blackglass/agent/current.new /opt/blackglass/agent/current

# 3. the stub mission release (A10-D7), only when asked
if [[ "$stub" == 1 ]]; then
  install -d -m 0755 -o root -g root /opt/blackglass/mission/releases/stub /etc/blackglass-stub /etc/blackglass-stub/plans
  install -m 0755 -o root -g root "$here/stub/run-mission" /opt/blackglass/mission/releases/stub/run-mission
  current="$(readlink -f /opt/blackglass/mission/current 2>/dev/null || true)"
  if [[ -z "$current" || "$current" == /opt/blackglass/mission/releases/stub ]]; then
    ln -sfn /opt/blackglass/mission/releases/stub /opt/blackglass/mission/current.new && mv -Tf /opt/blackglass/mission/current.new /opt/blackglass/mission/current
  else
    echo "note: /opt/blackglass/mission/current points at $current; left unchanged"
  fi
fi

# 4. units, polkit, configuration, tunnel
for unit in blackglass-agent.service blackglass-mission@.service; do install -m 0644 -o root -g root "$here/$unit" "/etc/systemd/system/$unit"; done
install -m 0644 -o root -g root "$here/tunnel/blackglass-tunnel.service" /etc/systemd/system/blackglass-tunnel.service
install -m 0644 -o root -g root "$here/60-blackglass-mission.rules" /etc/polkit-1/rules.d/60-blackglass-mission.rules
install -d -m 0750 -o root -g blackglass-agent /etc/blackglass/agent
[[ -e /etc/blackglass/agent/agent.json ]] || install -m 0640 -o root -g blackglass-agent "$here/agent.json.example" /etc/blackglass/agent/agent.json
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
if [[ "$start" == 1 ]]; then
  [[ -s /etc/blackglass/agent/host-secret ]] || { echo "install the host secret first (blackglass-agent 0600)" >&2; exit 78; }
  systemctl enable --now blackglass-tunnel.service blackglass-agent.service
fi
echo "installed agent release $revision"

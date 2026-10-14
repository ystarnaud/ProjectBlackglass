#!/usr/bin/env bash
# E1.1 read-only inventory of bg-dev-services01. Changes nothing.
set -u
if [[ $EUID -eq 0 ]]; then SUDO=""; else SUDO="sudo -n"; fi
export PATH="$PATH:/usr/sbin:/sbin"
s() { printf '\n===== %s\n' "$1"; }
s "os";         cat /etc/os-release; uname -srm; nproc; free -h; swapon --show; df -h / /var /opt 2>/dev/null
s "time";       timedatectl 2>&1 | head -n 12
s "account";    id; if [[ -z "$SUDO" ]]; then echo "running as root"; elif $SUDO true 2>/dev/null; then echo "passwordless sudo"; else echo "sudo needs a password or is absent"; fi
s "admin candidates (uid>=1000, sudo group, sudoers.d)"
getent passwd | awk -F: '$3 >= 1000 && $3 < 65534 { print $1, $3, $6, $7 }'
getent group sudo; getent group docker
ls -l /etc/sudoers.d/ 2>&1
command -v sudo || echo "sudo not installed"
s "ssh daemon (effective settings)"
$SUDO sshd -T 2>/dev/null | grep -Ei '^(port|listenaddress|permitrootlogin|passwordauthentication|kbdinteractiveauthentication|pubkeyauthentication|allowusers|allowgroups|authenticationmethods) '
ls -l /etc/ssh/sshd_config.d/ 2>&1
for f in /root/.ssh/authorized_keys /home/*/.ssh/authorized_keys; do [[ -f "$f" ]] && { echo "$f:"; ssh-keygen -lf "$f" 2>&1; }; done
echo "host keys:"; for k in /etc/ssh/ssh_host_*_key.pub; do ssh-keygen -lf "$k"; done
s "addresses";  ip -br addr; echo; ip route; echo; ip -6 route
s "ssh session"; echo "client: ${SSH_CONNECTION%% *}"
s "listeners";  $SUDO ss -H -ltnup; echo; $SUDO ss -H -lunp
s "docker";     command -v docker && docker version 2>&1; command -v docker >/dev/null && docker compose version 2>&1; $SUDO cat /etc/docker/daemon.json 2>&1
s "packages";   dpkg-query -W -f='${Package} ${Version} ${db:Status-Abbrev}\n' docker.io docker-ce docker-ce-cli containerd containerd.io runc podman podman-docker docker-compose docker-compose-plugin docker-buildx-plugin iptables nftables ufw firewalld systemd-resolved apparmor postgresql-17 curl ca-certificates gpg 2>&1 | sort
s "apt";        apt-cache policy docker.io 2>&1 | head -n 5; ls -l /etc/apt/sources.list.d/ 2>&1; grep -rhv '^\s*#' /etc/apt/sources.list /etc/apt/sources.list.d/ 2>/dev/null | grep -v '^\s*$'
s "docker repo for this suite (read-only HTTP fetch)"
codename="$(. /etc/os-release && echo "$VERSION_CODENAME")"; arch="$(dpkg --print-architecture)"
if command -v curl >/dev/null; then
  curl -fsS "https://download.docker.com/linux/debian/dists/$codename/stable/binary-$arch/Packages" 2>&1 \
    | awk '/^Package: /{p=$2} /^Version: /{ if (p=="docker-ce" || p=="docker-compose-plugin") print p, $2 }' | sort -V | tail -n 6
else
  echo "curl not installed"
fi
s "firewall";   $SUDO ufw status verbose 2>&1; echo "-- nft"; $SUDO nft list ruleset 2>&1 | head -n 150; echo "-- iptables"; $SUDO iptables -S 2>&1 | head -n 60; echo "-- ip6tables"; $SUDO ip6tables -S 2>&1 | head -n 60; update-alternatives --query iptables 2>&1 | grep -E '^(Value|Link):'
systemctl is-enabled nftables 2>&1; systemctl is-active nftables 2>&1
s "forwarding and bridge"; sysctl net.ipv4.ip_forward net.ipv6.conf.all.forwarding net.ipv6.conf.all.disable_ipv6 2>&1; lsmod | grep -E '^(br_netfilter|overlay|nf_tables|ip_tables) ' ; ls /etc/sysctl.d/ 2>&1
s "cgroups and apparmor"; stat -fc %T /sys/fs/cgroup; (aa-status --enabled 2>/dev/null && echo "apparmor enabled") || cat /sys/module/apparmor/parameters/enabled 2>&1
s "resolver";   systemctl is-active systemd-resolved 2>&1; ls -l /etc/resolv.conf; grep -v '^#' /etc/resolv.conf
s "network management"; for u in networking systemd-networkd NetworkManager cloud-init; do printf '%s: ' "$u"; systemctl is-active "$u" 2>&1; done
s "services";   systemctl list-units --type=service --state=running --no-legend --no-pager | awk '{print $1}'
s "docker default address pools (172.17-31.x, 192.168.x) versus this host"
client="${SSH_CONNECTION%% *}"
for p in 172.17. 172.18. 172.19. 172.2 172.30. 172.31. 192.168.; do
  ip -br addr | grep -q " $p" && echo "an interface address starts with $p"
  ip route | grep -q "$p" && echo "a route mentions $p"
  [[ "$client" == "$p"* ]] && echo "the SSH client address starts with $p"
done
echo "(no lines above: no overlap)"

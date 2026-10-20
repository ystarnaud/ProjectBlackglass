# B24 Address Half — Off-Host Reachability Procedure for `bg-dev-mission01`

**Status (2026-10-20): revision 2, prepared, not executed. It exposes a port only after the owner explicitly approves this procedure.**
- It changes no firewall, systemd unit file, SSH or Docker configuration on either VM.
- It is part of the Stage 12 plan (`2026-10-20-phase-13-stage12-server-mission.md`, "B24 security design", Task 10; decision S12-D8).
- Revision 2 applies security review 2 (M15): an allow-listed probe source, A–B–A ordering, own-address IPv6 and IPv4-mapped cases, exit codes that tell a timeout from a refusal, the driver verbatim, and `iptables-legacy-save` in the records.

## Question

Can a TCP listener on the mission VM be reached from the development network when systemd's address filter allows it? And is the same listener unreachable when the filter (`IPAddressDeny=any`, `IPAddressAllow=localhost`) doesn't allow it?

Only that pair is causal evidence that the filter is enforced. A failure that would happen anyway (the network, the hypervisor, a firewall) proves nothing.

## Hosts, interface, port

| Item | Value |
|---|---|
| Test host | `bg-dev-mission01`, interface `eth0`. Per the Stage 10 records (2026-10-18) it has IPv4 and a global IPv6 address, `ufw` is inactive and nftables is empty. All of this is re-read in step 1. The VM refused SSH on port 22 on 2026-10-20 when this procedure was written; nothing was read then |
| Probe source | `bg-dev-services01`, interface `eth0` (read-only check, 2026-10-20: `eth0` up, `nc` and `bash` present), through its existing admin alias. Its addresses are read at run time and passed to the mission VM as arguments; they are never written to the repository |
| Port and families | TCP **7899** (outside the managed 7800–7807 and the ephemeral 32768–60999; nothing listened there per the Stage 10 survey), on three sockets: IPv4 `0.0.0.0`, IPv6 `[::]` with `IPV6_V6ONLY`, and a dual-stack IPv6 `[::]:7898` without `V6ONLY` for the IPv4-mapped case |
| Maximum exposure | each listener unit at most **120 s** (`RuntimeMaxSec=120`); three units in sequence; the whole procedure at most 10 minutes |

## The three observations (A–B–A)

| Run | Unit | Address filter | Exposure if the filter is enforced | Exposure if it isn't |
|---|---|---|---|---|
| **A1** | `blackglass-reach-allowed` | `IPAddressDeny=any`, `IPAddressAllow=localhost <services v4> <services v6>` | localhost and the services VM only | the whole network that can route to `eth0`, for at most 120 s |
| **B** | `blackglass-reach-filtered` | `IPAddressDeny=any`, `IPAddressAllow=localhost` (the Stage 12 template's) | localhost only | as above |
| **A2** | `blackglass-reach-allowed` again | as A1 | as A1 | as above |

- **Why A1 and A2:** they are the positive control. The same listener is reachable from the probe source when the filter allows it, so a later timeout can't be blamed on the network.
- **Why allow-listed rather than fully unfiltered:** this limits the exposure to the probe source while still being causal, since the only difference between A and B is one allow entry.
- **Why repeat A:** A2 excludes a network change between A1 and B.
- **Option (owner's choice):** A0 instead of A1, a fully unfiltered unit with no `IPAddress*` lines. It exposes the port to everything that can reach `eth0` (including over IPv6, if the global prefix is routable and not filtered upstream) for at most 120 s.

## The minimal temporary listener

Python 3 is the VM's system interpreter (nothing is installed). The listener accepts each connection and closes it at once, sending nothing and reading nothing. It writes one journal line per accepted connection with its socket's family only, never the peer address, and exits at 110 s.

## The driver (verbatim; run as root on the mission VM over `ssh … bash -s -- <src4> <src6>`)

```bash
#!/usr/bin/env bash
set -u
src4="${1:?services VM IPv4}"; src6="${2:?services VM IPv6}"
units=(blackglass-reach-allowed blackglass-reach-filtered)
cleanup() { for u in "${units[@]}"; do systemctl stop "$u" 2>/dev/null; systemctl reset-failed "$u" 2>/dev/null; done; }
trap cleanup EXIT INT TERM HUP
LISTENER='import socket,select,time
def tcp(fam, addr, port, v6only):
    s = socket.socket(fam, socket.SOCK_STREAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    if fam == socket.AF_INET6:
        s.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, v6only)
    s.bind((addr, port)); s.listen(4); return s
socks = {tcp(socket.AF_INET, "0.0.0.0", 7899, 0): "ipv4", tcp(socket.AF_INET6, "::", 7899, 1): "ipv6", tcp(socket.AF_INET6, "::", 7898, 0): "dual"}
print("listening", flush=True); end = time.time() + 110
while time.time() < end:
    for s in select.select(list(socks), [], [], 1)[0]:
        c, _ = s.accept(); print("accepted", socks[s], flush=True); c.close()'
common=(-p DynamicUser=yes -p NoNewPrivileges=yes -p CapabilityBoundingSet= -p ProtectSystem=strict -p ProtectHome=yes
  -p PrivateTmp=yes -p PrivateDevices=yes -p "RestrictAddressFamilies=AF_INET AF_INET6" -p MemoryMax=64M -p TasksMax=8
  -p RuntimeMaxSec=120 -p "InaccessiblePaths=-/opt/blackglass -/etc/blackglass -/var/lib/blackglass -/run/blackglass")
start() { # start <unit> <allow list>
  systemd-run --quiet --collect --unit="$1" "${common[@]}" -p IPAddressDeny=any -p "IPAddressAllow=$2" python3 -c "$LISTENER"
  for _ in $(seq 1 20); do journalctl -u "$1" --since=-30s -o cat --no-pager | grep -qx listening && return 0; sleep 0.5; done
  echo "STOP: $1 did not start"; exit 1
}
local_probe() { # local_probe <label> <host> <port>: 0 open, 1 refused, 124 timeout
  timeout 5 bash -c "exec 3<>/dev/tcp/$2/$3" 2>/dev/null; echo "local $1 exit=$?"
}
own4="$(ip -4 -o addr show dev eth0 scope global | awk '{print $4}' | cut -d/ -f1 | head -1)"
own6="$(ip -6 -o addr show dev eth0 scope global | awk '{print $4}' | cut -d/ -f1 | head -1)"
observe() { # observe <run label> <unit> <allow list>
  start "$2" "$3"
  echo "== $1 ready"   # the workstation then runs the services-VM probes (below) and answers with a line on stdin
  read -r _
  local_probe own-ipv4 "$own4" 7899; local_probe own-ipv6 "$own6" 7899; local_probe own-mapped "$own4" 7898
  local_probe loopback-ipv4 127.0.0.1 7899; local_probe loopback-ipv6 ::1 7899
  systemctl stop "$2"
  echo "== $1 accepted: $(journalctl -u "$2" --since=-150s -o cat --no-pager | grep -c '^accepted')"
  echo "== $1 listening-after-stop: $(ss -tlnH '( sport = :7899 or sport = :7898 )' | wc -l)"
}
observe A1 blackglass-reach-allowed "localhost $src4 $src6"
observe B  blackglass-reach-filtered "localhost"
observe A2 blackglass-reach-allowed "localhost $src4 $src6"
echo "== firewalling journal since boot: $(journalctl -b -g 'firewalling' -o cat --no-pager | wc -l) line(s)"
```

- **The services-VM probes** (read-only there), run by the workstation whenever the driver prints `== <run> ready`:
  - `timeout 5 bash -c 'exec 3<>/dev/tcp/<mission v4>/7899'`
  - the same for `<mission v6>` on 7899
  - the same for `<mission v4>` on 7898 (the mapped case)

  Each prints `exit=0` (open), `1` (refused) or `124` (timeout).
- **The workstation's orchestration** reads the addresses, opens both SSH sessions and pipes everything through the IPv4 and IPv6 address-redaction `sed`. It is written into the Task 10 manifest verbatim, with the exact `sed` expressions.

## Records before and after (read-only)

Each record is saved, and "after" must equal "before":
- `ufw status verbose`;
- `nft list ruleset | sha256sum`;
- `iptables-save | sha256sum` and `iptables-legacy-save | sha256sum` (if present), and `ip6tables-save | sha256sum`;
- `ip -br link`, and `ip -br addr` (redacted, and whether the IPv6 address is scope global);
- `ss -tlnH '( sport = :7899 or sport = :7898 )' | wc -l`, which must be 0;
- `systemctl show blackglass-tunnel -p MainPID -p NRestarts`;
- `sysctl kernel.yama.ptrace_scope`;
- `awk '{print $1}' /proc/modules | sort | sha256sum`.

## Comparison

| Path | A1 and A2 must be | B must be | Shows |
|---|---|---|---|
| services VM → mission IPv4 :7899 | `exit=0` | `exit=124` | the IPv4 filter on the network path |
| services VM → mission IPv6 :7899 | `exit=0` | `exit=124` | the IPv6 filter |
| services VM → mission IPv4 :7898 (dual-stack, mapped) | `exit=0` | `exit=124` | the filter on IPv4-mapped traffic |
| mission VM → own IPv4 and IPv6 :7899, own IPv4 :7898 | `exit=124` (the own address isn't localhost or the services VM) | `exit=124` | the filter is independent of the network path (both runs deny it) |
| mission VM → 127.0.0.1 and ::1 :7899 | `exit=0` | `exit=0` | `localhost` is allowed |
| the listener's `accepted` count | the open probes | the loopback probes only | the journal agrees with the probes |

The address half of B24 counts as proven only if every cell holds and the firewalling journal shows 0 lines. Any of these makes the result non-informative, and the address half stays unproven:
- A1 or A2 closed from the services VM (the network blocks it anyway);
- B open from the services VM (the filter isn't enforced);
- a `firewalling` warning since boot.

`exit=1` (refused) anywhere in B means something answered with a reset rather than a drop, and is investigated before any conclusion.

## Rollback

The driver's `trap` stops and resets both units on exit, interrupt, hang-up or termination, and `RuntimeMaxSec=120` ends each unit even if the SSH session drops before the trap runs. By hand:

```sh
systemctl stop blackglass-reach-allowed blackglass-reach-filtered 2>/dev/null
systemctl reset-failed blackglass-reach-allowed blackglass-reach-filtered 2>/dev/null
ss -tlnH '( sport = :7899 or sport = :7898 )' | wc -l      # 0
```

Nothing else exists to remove: no file, unit file, user, package or firewall rule. The journal keeps the units' lines (harmless).

## For the owner's approval

- Exposing TCP 7899 (IPv4 and IPv6) and 7898 (dual-stack) on `eth0` for at most 120 s per run, three runs. With the filter enforced, this reaches only the services VM; if it isn't enforced, it reaches everything that can route to `eth0`. The listener serves nothing.
- A1 (allow-listed, recommended) or A0 (fully unfiltered).
- Probing from `bg-dev-services01` with bash's `/dev/tcp` (read-only there).

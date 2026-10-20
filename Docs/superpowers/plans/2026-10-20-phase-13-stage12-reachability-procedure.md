# B24 Address Half — Off-Host Reachability Procedure for `bg-dev-mission01`

**Status (2026-10-20): prepared, not executed. It exposes a port only after the owner explicitly approves this procedure.** It changes no firewall, systemd unit file, SSH or Docker configuration on either VM. Part of the Stage 12 plan (`2026-10-20-phase-13-stage12-server-mission.md`, "B24 security design", Task 10; decision S12-D8).

## Question

Can an unfiltered TCP listener on the mission VM be reached from the development network? Only if it can does the same listener being unreachable under `IPAddressDeny=any`/`IPAddressAllow=localhost` prove that systemd's filter is enforced. A failure that would happen anyway (the network, the hypervisor or a firewall) proves nothing.

## Hosts, interface, port

| Item | Value |
|---|---|
| Test host | `bg-dev-mission01`, interface `eth0`. Per the Stage 10 records (2026-10-18) it has IPv4 and a global IPv6 address, `ufw` is inactive and the nftables ruleset is empty; all re-read in step 1 |
| Probe source | `bg-dev-services01` only, interface `eth0`, through its existing admin alias. Nothing else is used as a probe |
| Port and families | TCP **7899** (outside the managed 7800–7807 and the ephemeral 32768–60999; nothing listens there per the Stage 10 port survey), IPv4 `0.0.0.0` and IPv6 `[::]` (a separate `IPV6_V6ONLY` socket) |
| Maximum exposure | each listener unit at most **120 s** (`RuntimeMaxSec=120`); the whole procedure at most 10 minutes |

## The minimal temporary listener

A transient unit running Python 3 (the VM's system interpreter; nothing installed). It:
- binds both sockets;
- accepts connections and closes each at once, sending nothing and reading nothing;
- writes one journal line per accepted connection with the family only, never the peer address;
- exits at 120 s whatever happens.

```sh
LISTENER='import socket,select,time
s4=socket.socket(socket.AF_INET,socket.SOCK_STREAM); s4.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1); s4.bind(("0.0.0.0",7899)); s4.listen(4)
s6=socket.socket(socket.AF_INET6,socket.SOCK_STREAM); s6.setsockopt(socket.IPPROTO_IPV6,socket.IPV6_V6ONLY,1); s6.bind(("::",7899)); s6.listen(4)
print("listening",flush=True); end=time.time()+110
while time.time()<end:
  for s in select.select([s4,s6],[],[],1)[0]:
    c,_=s.accept(); print("accepted", "ipv6" if s is s6 else "ipv4", flush=True); c.close()'
COMMON=(-p DynamicUser=yes -p NoNewPrivileges=yes -p CapabilityBoundingSet= -p ProtectSystem=strict -p ProtectHome=yes
  -p PrivateTmp=yes -p PrivateDevices=yes -p RestrictAddressFamilies="AF_INET AF_INET6" -p MemoryMax=64M -p TasksMax=8
  -p RuntimeMaxSec=120 -p "InaccessiblePaths=-/opt/blackglass -/etc/blackglass -/var/lib/blackglass -/run/blackglass")
```

- **Unfiltered:** `systemd-run --quiet --collect --unit=blackglass-reach-unfiltered "${COMMON[@]}" python3 -c "$LISTENER"`
- **Filtered:** the same with `--unit=blackglass-reach-filtered -p IPAddressDeny=any -p IPAddressAllow=localhost`.

## Safety restrictions

- No data is served or read; the listener only accepts and closes.
- It runs as a dynamic, unprivileged user with no capabilities, a read-only system and the Blackglass paths invisible.
- One port, two families, at most 120 s per unit; the units are never run at the same time.
- **Automatic cleanup after a disconnect or an interruption:**
  - `RuntimeMaxSec=120` ends each unit even if the SSH session drops;
  - the driver script on the VM also traps `EXIT INT TERM HUP` to stop both units;
  - `--collect` removes the units' state.
- The mission VM's agent stays disabled and the tunnel up. No mission unit or template is used.

## Steps (as root on the mission VM over `ssh … bash -s`; probes from the services VM; all output address-redacted)

1. **Before** (read-only), saved:
   - `ufw status verbose`, `nft list ruleset | sha256sum`, `ip -br link`, `ip -br addr` (redacted);
   - `ss -tlnH 'sport = :7899' | wc -l` (must be 0);
   - `systemctl show blackglass-tunnel -p MainPID -p NRestarts`;
   - `sysctl kernel.yama.ptrace_scope`.
2. **Unfiltered observation** (≤ 120 s): start the unfiltered unit; wait for `listening`. Then:
   - (a) from the services VM, `nc -z -w 3 <mission IPv4> 7899` and `nc -6 -z -w 3 <mission IPv6> 7899`;
   - (b) on the mission VM, with bash only (no `nc` dependency there), `timeout 3 bash -c 'exec 3<>/dev/tcp/<own IPv4>/7899'`, then the same for `127.0.0.1` and `::1`.

   Then stop the unit, and show `ss -tlnH 'sport = :7899' | wc -l` = 0.
3. **Filtered observation** (≤ 120 s): the same with the filtered unit, plus:
   - `systemctl show blackglass-reach-filtered -p IPAddressDeny -p IPAddressAllow`;
   - `journalctl -u blackglass-reach-filtered | grep -ci "bpf"`, to find any "BPF firewalling not supported" line.

   Then stop it.
4. **After** (read-only): step 1 again. Every value must equal the "before" one, and the tunnel's PID and restart count must be unchanged.

## Comparison

| Path | Unfiltered must be | Filtered must be | Shows |
|---|---|---|---|
| services VM → mission IPv4 :7899 | open | timeout (`nc` exit 1 after 3 s) | the IPv4 filter on the network path |
| services VM → mission IPv6 :7899 | open | timeout | the IPv6 filter |
| mission VM → own IPv4 :7899 | open | timeout | the filter independent of the network path |
| mission VM → 127.0.0.1 / ::1 :7899 | open | open | `localhost` is allowed |

The address half of B24 counts as proven only if every "Unfiltered" cell is open and every "Filtered" cell is as listed. Any of the following makes the result non-informative, and B24's address half stays unproven:
- an unfiltered path is closed (the network or a firewall blocks it anyway);
- a filtered path is open (the filter isn't enforced);
- the journal shows a BPF warning.

The listener's journal "accepted" lines must match the probes. In the filtered run, the remote probes produce none.

## Rollback

```sh
systemctl stop blackglass-reach-unfiltered blackglass-reach-filtered 2>/dev/null
systemctl reset-failed blackglass-reach-unfiltered blackglass-reach-filtered 2>/dev/null
ss -tlnH 'sport = :7899' | wc -l          # 0
```

Nothing else exists to remove: no file, unit file, user, package or firewall rule. The journal keeps the units' lines (harmless).

## For the owner's approval

- Exposing TCP 7899 on `eth0` (IPv4 and IPv6) to the development network for at most 120 s, serving nothing.
- Probing from `bg-dev-services01` with `nc` (read-only there).

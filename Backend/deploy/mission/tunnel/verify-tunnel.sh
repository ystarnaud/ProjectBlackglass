#!/usr/bin/env bash
# verify-tunnel.sh: run as root on the mission VM while blackglass-tunnel.service runs. Prints ok/FAIL/note lines; exit 0 only if
# every check passed. A10-D3, the Phase C procedure.
#   1. the tunnel: /usr/bin/ssh as blackglass-tunnel, no capabilities, exactly one listener at 127.0.0.1:5081 held by the unit's
#      main pid (systemctl, ss and /proc), connected to the services VM;
#   2. the negative tests, as blackglass-tunnel with the tunnel's own key, pinned host key and settings. They use a temporary copy
#      of the tunnel's ssh_config minus its LocalForward (the live tunnel holds 127.0.0.1:5081). Never ClearAllForwardings: it
#      also clears the -L, -R and -D a test asks for, so the request would never reach the server. A forwarding test passes only
#      when its request reached the server and the server refused it ("administratively prohibited", "remote port forwarding
#      failed"); a missing local listener is a failure, not a pass;
#   3. afterwards: the live tunnel's pid and restart count are unchanged, and no test process or test port remains.
# Every test process has a timeout and is stopped by its own recorded pid, never by a pattern. The services VM's side of the
# denials is checked there with services-vm-check.sh --since <the "since" printed below>.
set -uo pipefail
[[ "$(id -u)" == 0 ]] || { echo "run as root" >&2; exit 77; }
CFG=/etc/blackglass/tunnel/ssh_config
UNIT=blackglass-tunnel.service
PORTS=(15081 15082 15083 15084 15085 15086 15087 15088)
AS=(runuser -u blackglass-tunnel --)
fail=0
ok() { echo "ok   - $*"; }
bad() { echo "FAIL - $*"; fail=1; }
note() { echo "note - $*"; }
check() { local what="$1"; shift; if "$@"; then ok "$what"; else bad "$what"; fi; }
has() { grep -qF -- "$2" "$1"; }

tmp="$(mktemp -d)"; chmod 0755 "$tmp"
pids=()
main="$(systemctl show -p MainPID --value "$UNIT")"
cleanup() {
  local p
  for p in "${pids[@]}"; do [[ "$p" != "$main" ]] && kill "$p" 2>/dev/null; done
  wait 2>/dev/null
  rm -rf "$tmp"
}
trap cleanup EXIT
echo "since=@$(date +%s)"   # an absolute instant for services-vm-check.sh --since (no time-zone ambiguity)

# 1. the tunnel
restarts="$(systemctl show -p NRestarts --value "$UNIT")"
tu="$(id -u blackglass-tunnel)"; tg="$(id -g blackglass-tunnel)"
check "$UNIT is active (main pid $main)" [ "$(systemctl is-active "$UNIT")" = active ]
check "the main process is /usr/bin/ssh" [ "$(readlink "/proc/$main/exe")" = /usr/bin/ssh ]
check "its command line is exactly the unit's" [ "$(tr '\0' ' ' < "/proc/$main/cmdline")" = "/usr/bin/ssh -N -F $CFG bg-api-tunnel " ]
check "it runs as blackglass-tunnel (uid and gid $tu:$tg, every field)" [ "$(awk '/^Uid:/ { print $2, $3, $4, $5 } /^Gid:/ { print $2, $3, $4, $5 }' "/proc/$main/status" | tr '\n' ' ')" = "$tu $tu $tu $tu $tg $tg $tg $tg " ]
check "it has no capabilities" grep -Eq '^CapEff:[[:space:]]+0+$' "/proc/$main/status"
check "exactly one TCP listener on port 5081, at 127.0.0.1" [ "$(ss -H -tln '( sport = :5081 )' | awk '{ print $4 }')" = 127.0.0.1:5081 ]
check "no IPv6 listener on 5081" [ -z "$(awk 'NR > 1 && $2 ~ /:13D9$/ && $4 == "0A"' /proc/net/tcp6)" ]
row="$(awk 'NR > 1 && $2 ~ /:13D9$/ && $4 == "0A" { print $2, $8, $10 }' /proc/net/tcp)"
check "/proc/net/tcp: the only 5081 listener is 0100007F:13D9, owned by uid $tu" [ "$(cut -d' ' -f1,2 <<<"$row")" = "0100007F:13D9 $tu" ]
inode="$(cut -d' ' -f3 <<<"$row")"
check "the listener's socket (inode ${inode:-none}) belongs to the main pid" bash -c "[ -n '$inode' ] && ls -l /proc/$main/fd 2>/dev/null | grep -qF 'socket:[$inode]'"
check "the main pid holds an established connection to the services VM's SSH" bash -c "ss -H -tnp state established '( dport = :22 )' | grep -qF 'pid=$main,'"

# 2. the negative tests
TCFG="$tmp/ssh_config"
grep -v -E '^[[:space:]]*LocalForward[[:space:]]' "$CFG" > "$TCFG"; chmod 0644 "$TCFG"
check "the test configuration is the tunnel's minus exactly its LocalForward line" [ "$(diff "$CFG" "$TCFG" | grep -c '^[<>]')" = 1 -a "$(diff "$CFG" "$TCFG" | grep -c '^< *LocalForward ')" = 1 ]
busy=""; for p in "${PORTS[@]}"; do [[ -n "$(ss -H -tln "( sport = :$p )")" ]] && busy+=" $p"; done
[[ -z "$busy" ]] || { bad "test ports already in use:$busy"; exit 1; }
SSH=(ssh -F "$TCFG")

timeout 8 "${AS[@]}" "${SSH[@]}" -v -N bg-api-tunnel < /dev/null 2> "$tmp/pos.log"; rc=$?
check "positive: the pinned host key matches and the key authenticates (rc $rc, 124 = still connected at the timeout)" \
  bash -c "[ $rc = 124 ] && grep -qF 'is known and matches the ED25519 host key' '$tmp/pos.log' && grep -qF 'Authenticated to' '$tmp/pos.log'"

timeout 15 "${AS[@]}" "${SSH[@]}" -o UserKnownHostsFile=/dev/null bg-api-tunnel true < /dev/null > "$tmp/t1.log" 2>&1; rc=$?
check "an unknown host key is refused (rc $rc)" bash -c "[ $rc = 255 ] && grep -qF 'Host key verification failed' '$tmp/t1.log'"
printf '%s %s\n' "$(awk '{ print $1 }' /etc/blackglass/tunnel/known_hosts)" "$(awk '{ print $1, $2 }' /etc/ssh/ssh_host_ed25519_key.pub)" > "$tmp/wrong_known_hosts"
chmod 0644 "$tmp/wrong_known_hosts"
timeout 15 "${AS[@]}" "${SSH[@]}" -o UserKnownHostsFile="$tmp/wrong_known_hosts" bg-api-tunnel true < /dev/null > "$tmp/t2.log" 2>&1; rc=$?
check "a different host key is refused (rc $rc)" bash -c "[ $rc = 255 ] && grep -qF 'REMOTE HOST IDENTIFICATION HAS CHANGED' '$tmp/t2.log'"

timeout 15 "${AS[@]}" "${SSH[@]}" -o SessionType=default -T bg-api-tunnel 'id; echo COMMAND-RAN' < /dev/null > "$tmp/t3.log" 2>&1; rc=$?
check "no command runs: the session ends with the forced command's failure (rc $rc = 1, not a connection failure) and no output from the command" \
  bash -c "[ $rc = 1 ] && ! grep -qE 'uid=|COMMAND-RAN' '$tmp/t3.log'"

timeout 20 "${AS[@]}" "${SSH[@]}" -o ExitOnForwardFailure=yes -R 127.0.0.1:15082:127.0.0.1:22 -N bg-api-tunnel < /dev/null > "$tmp/t4.log" 2>&1; rc=$?
check "a remote forward is refused by the server (rc $rc)" bash -c "[ $rc = 255 ] && grep -qF 'remote port forwarding failed for listen port 15082' '$tmp/t4.log'"

# forward <what> <local port> <-L target | D> <expect: denied | streamlocal | allowed>
refused_and_empty() { grep -qF -- "$1" "$log" && [[ -z "$got" ]]; } # reads forward's log and got
forward() {
  local what="$1" port="$2" target="$3" expect="$4" wrapper pid="" i log="$tmp/fwd-$2.log" got
  if [[ "$target" == D ]]; then
    timeout 30 "${AS[@]}" "${SSH[@]}" -v -o ExitOnForwardFailure=yes -D "127.0.0.1:$port" -N bg-api-tunnel < /dev/null 2> "$log" &
  else
    timeout 30 "${AS[@]}" "${SSH[@]}" -v -o ExitOnForwardFailure=yes -L "127.0.0.1:$port:$target" -N bg-api-tunnel < /dev/null 2> "$log" &
  fi
  wrapper=$!; pids+=("$wrapper")
  for i in $(seq 1 60); do
    pid="$(ss -H -ltnp "( sport = :$port )" | grep -o 'pid=[0-9]*' | head -1 | cut -d= -f2)"
    [[ -n "$pid" ]] && break; sleep 0.25
  done
  if [[ -z "$pid" || "$pid" == "$main" || "$(ps -o user= -p "$pid")" != blackglass-tunnel ]]; then
    bad "$what: the test forward never connected, so nothing reached the server"
  else
    pids+=("$pid")
    if [[ "$target" == D ]]; then # SOCKS4 CONNECT 127.0.0.1:22
      got="$(timeout 6 bash -c "exec 3<>/dev/tcp/127.0.0.1/$port; printf '\x04\x01\x00\x16\x7f\x00\x00\x01\x00' >&3; head -c 64 <&3" 2>/dev/null | tr -cd '[:print:]')"
    else
      got="$(timeout 6 bash -c "exec 3<>/dev/tcp/127.0.0.1/$port; head -c 64 <&3" 2>/dev/null | tr -cd '[:print:]')"
    fi
    sleep 1
    case "$expect" in
      denied) check "$what: refused by the server (\"administratively prohibited\"), nothing came through" \
                refused_and_empty 'open failed: administratively prohibited' ;;
      streamlocal) check "$what: the open failed and nothing came through (the server logs \"refused streamlocal port forward\")" \
                refused_and_empty 'open failed' ;;
      allowed) check "$what: requested and permitted by the server (not \"administratively prohibited\")" \
                bash -c "grep -qF 'forwarding to 127.0.0.1 port 5080 requested' '$log' && ! grep -qF 'administratively prohibited' '$log'"
               note "$what: $(grep -oE 'open failed: .*' "$log" | head -1 || true)${got:+ received: ${got:0:20}}" ;;
    esac
    kill "$pid" 2>/dev/null
  fi
  wait "$wrapper" 2>/dev/null
  pids=() # reaped: a recorded pid is never signalled again (it could have been reused)
}
forward "local forward to 127.0.0.1:22 (the services VM's SSH)" 15081 127.0.0.1:22 denied
forward "local forward to 127.0.0.1:55432 (the test PostgreSQL)" 15083 127.0.0.1:55432 denied
forward "local forward to localhost:5080 (the allowed port under another name)" 15084 localhost:5080 denied
forward "local forward to 192.0.2.1:80 (an off-host address)" 15085 192.0.2.1:80 denied
forward "dynamic (SOCKS) forward, CONNECT 127.0.0.1:22" 15088 D denied
forward "local forward to /var/run/docker.sock (stream-local)" 15087 /var/run/docker.sock streamlocal
forward "local forward to 127.0.0.1:5080 (the one allowed destination)" 15086 127.0.0.1:5080 allowed

timeout 15 "${AS[@]}" "${SSH[@]}" -o SessionType=default -o RequestTTY=force bg-api-tunnel < /dev/null > "$tmp/t5.log" 2>&1; rc=$?
check "a TTY is refused (rc $rc)" has "$tmp/t5.log" 'PTY allocation request failed'
timeout 15 "${AS[@]}" env DISPLAY=:0 "${SSH[@]}" -v -o SessionType=default -o ForwardX11=yes -o ForwardX11Trusted=yes -T bg-api-tunnel < /dev/null > "$tmp/t6.log" 2>&1; rc=$?
check "X11 forwarding is refused by the server (rc $rc)" has "$tmp/t6.log" 'X11 forwarding request failed'
install -d -m 0700 -o blackglass-tunnel -g blackglass-tunnel "$tmp/agent"
timeout 60 "${AS[@]}" ssh-agent -D -a "$tmp/agent/sock" > /dev/null 2>&1 &
agent=$!; pids+=("$agent")
for i in $(seq 1 20); do [[ -S "$tmp/agent/sock" ]] && break; sleep 0.25; done
timeout 15 "${AS[@]}" env SSH_AUTH_SOCK="$tmp/agent/sock" "${SSH[@]}" -v -o SessionType=default -o ForwardAgent=yes -T bg-api-tunnel < /dev/null > "$tmp/t7.log" 2>&1; rc=$?
kill "$agent" 2>/dev/null; wait "$agent" 2>/dev/null; pids=()
if has "$tmp/t7.log" 'Requesting authentication agent forwarding'; then
  note "agent forwarding was requested; the protocol asks for no reply, so the refusal is the server's allowagentforwarding no and the key's restrict (services-vm-check.sh)"
else
  bad "the agent-forwarding request was never sent (rc $rc)"
fi
timeout 15 "${AS[@]}" "${SSH[@]}" -o ExitOnForwardFailure=yes -o Tunnel=point-to-point -w any -N bg-api-tunnel < /dev/null > "$tmp/t8.log" 2>&1; rc=$?
note "tunnel device: $(head -1 "$tmp/t8.log") (rc $rc); the unit has no capabilities and PrivateDevices; the server side is permittunnel no (services-vm-check.sh)"

# 3. afterwards
check "the live tunnel is untouched (pid $main, restarts $restarts)" \
  [ "$(systemctl show -p MainPID --value "$UNIT") $(systemctl show -p NRestarts --value "$UNIT")" = "$main $restarts" ]
check "no test process remains" [ "$(pgrep -u blackglass-tunnel | tr '\n' ' ')" = "$main " ]
busy=""; for p in "${PORTS[@]}"; do [[ -n "$(ss -H -tln "( sport = :$p )")" ]] && busy+=" $p"; done
check "no test port remains${busy:+ (busy:$busy)}" [ -z "$busy" ]
check "the 5081 listener is still the main pid's" bash -c "ss -H -tlnp '( sport = :5081 )' | grep -qF 'pid=$main,'"
exit "$fail"

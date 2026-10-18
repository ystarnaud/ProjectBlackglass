#!/usr/bin/env bash
# The Stage 10 VM checks, after install.sh --stub (run as root; NOT RUN on bg-dev-mission01 until the owner authorizes the
# infrastructure checkpoint; first run in a disposable systemd container). Test attempts run the stub under the real slot templates
# and the real credential broker; nothing touches /opt/blackglass/server, and their outboxes and exit records are kept as evidence.
#   verify.sh --yes [--report <file>]                 the checks; writes the persistence state for the second part
#   verify.sh --yes --after-restart <state-file>      after a reboot (or container restart): the results are all still there
# Expected: every line "ok" (a "note" line records a limit of this host, never a pass); any "FAIL" makes the exit status 1.
set -uo pipefail
yes=0 report=/var/log/blackglass/verify-$(date -u +%Y%m%dT%H%M%SZ).txt after=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes) yes=1; shift ;;
    --report) report="${2:?}"; shift 2 ;;
    --after-restart) after="${2:?}"; shift 2 ;;
    *) echo "unknown: $1" >&2; exit 64 ;;
  esac
done
[[ "$(id -u)" == 0 ]] || { echo "run as root" >&2; exit 77; }
[[ "$yes" == 1 ]] || { echo "refusing without --yes" >&2; exit 64; }
[[ "$(readlink -f /opt/blackglass/mission/current)" == /opt/blackglass/mission/releases/stub ]] || { echo "the stub release is not selected" >&2; exit 78; }
# systemd delivers LoadCredential through mount propagation: a host whose / is not shared (a container left private) can't run units
# with credentials at all, and its results would say nothing about this design.
[[ "$(findmnt -n -o PROPAGATION /)" == shared* ]] || { echo "/ is not a shared mount (findmnt -o PROPAGATION /): not a host these checks describe" >&2; exit 78; }
exec > >(tee "$report") 2>&1
fails=0
check() { if eval "$2"; then echo "ok   - $1"; else echo "FAIL - $1"; fails=$((fails + 1)); fi; }
note() { echo "note - $1"; }
as_agent() { runuser -u blackglass-agent -- "$@"; }
unit() { echo "blackglass-mission-$1@$2.service"; }
# Reads a file as the agent's unit sees the outbox (its read-only bind of the private state directories).
as_agent_unit() {
  systemd-run --quiet --wait --pipe --collect -p User=blackglass-agent -p ProtectSystem=strict -p PrivateTmp=yes \
    -p BindReadOnlyPaths=/var/lib/private/blackglass-outbox:/var/lib/blackglass/outbox -- "$@"
}
probe_of() { cat "/var/log/private/blackglass-mission/$1/probe.txt" 2>/dev/null; }
has() { probe_of "$1" | grep -qx -- "$2"; }
sha() { sha256sum | cut -d' ' -f1; }

if [[ -n "$after" ]]; then
  # Part 2: after the restart, every recorded result is still there, unchanged, and still readable by the agent's unit.
  [[ -s "$after" ]] || { echo "no state file $after" >&2; exit 64; }
  while read -r attempt digest; do
    check "after the restart, $attempt's result is unchanged" "[ \"\$(sha < /var/lib/private/blackglass-outbox/$attempt/probe.ready)\" = $digest ]"
    check "after the restart, the agent's unit reads $attempt's result" "as_agent_unit cat /var/lib/blackglass/outbox/$attempt/probe.ready >/dev/null"
    check "after the restart, $attempt's exit record is there" "[ -s /var/lib/blackglass/exits/$attempt.exit ]"
  done < "$after"
  echo "verify (after restart): $fails failure(s); report $report"
  exit $(( fails > 0 ))
fi

# The broker as the agent uses it: one request, the answer printed (the credential itself is never printed).
broker_as() { # broker_as <user|root> <request file>
  local run=(runuser -u "$1" --)
  [[ "$1" == root ]] && run=()
  "${run[@]}" python3 -c '
import socket, sys
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
try:
    s.connect("/run/blackglass/credential.sock")
except OSError as e:
    print("connect_refused"); sys.exit(0)
s.sendall(open(sys.argv[1], "rb").read()); s.shutdown(socket.SHUT_WR)
print(s.recv(128).decode().strip())' "$2"
}
new_credential() { python3 -c 'import base64, secrets; print("pc1_" + base64.urlsafe_b64encode(secrets.token_bytes(32)).decode().rstrip("="))'; }
work="$(mktemp -d /run/blackglass-verify.XXXXXX)" # root 0700: the test credentials, removed at the end
chmod 0711 "$work"
prepare() { # prepare <attempt> <generation> [credential] -> the broker's answer; the credential's sha256 kept in $work/<attempt>.sha
  local credential="${3:-$(new_credential)}" request
  request="$(mktemp "$work/request.XXXXXX")" # one per call: concurrent preparations never share it
  printf 'prepare %s %s\n%s\n' "$1" "$2" "$credential" > "$request" && chmod 0644 "$request"
  printf '%s' "$credential" | sha > "$work/$1.sha.$2"
  broker_as blackglass-agent "$request"
  rm -f "$request"
}
A="$(cat /proc/sys/kernel/random/uuid)" B="$(cat /proc/sys/kernel/random/uuid)" C="$(cat /proc/sys/kernel/random/uuid)"
D="$(cat /proc/sys/kernel/random/uuid)" R="$(cat /proc/sys/kernel/random/uuid)" X="$(cat /proc/sys/kernel/random/uuid)"
port_base="$(sed -n 's/^SocketBindAllow=udp:\([0-9]*\)$/\1/p' /etc/systemd/system/blackglass-mission-00@.service)"
cleanup() {
  for pair in "00 $A" "01 $B" "02 $C" "01 $D" "03 $R" "04 $X"; do systemctl stop "$(unit $pair)" 2>/dev/null; done
  for id in "$A" "$B" "$C" "$D" "$R" "$X"; do rm -rf "/etc/blackglass-stub/plans/$id"; done
  rm -rf "$work"
}
trap cleanup EXIT

# ---- the installation itself ----
check "slot templates are installed for slots 00 and 01 at least" "[ -f /etc/systemd/system/blackglass-mission-00@.service ] && [ -f /etc/systemd/system/blackglass-mission-01@.service ]"
check "the shared-uid template is gone" "[ ! -e /etc/systemd/system/blackglass-mission@.service ]"
probe_unit="blackglass-mission-00@$(cat /proc/sys/kernel/random/uuid).service"
check "no drop-in of any kind changes a slot unit (systemd's own view)" "[ -z \"\$(systemctl show -p DropInPaths --value $probe_unit)\" ]"
check "the slot unit comes from the installed template" "[ \"\$(systemctl show -p FragmentPath --value $probe_unit)\" = /etc/systemd/system/blackglass-mission-00@.service ]"
check "no static blackglass-mNN user exists (each slot's user is dynamic)" "! grep -Eq '^blackglass-m[0-9]{2}:' /etc/passwd"
check "/var/lib/private is root 0700" "[ \"\$(stat -c '%U %a' /var/lib/private)\" = 'root 700' ]"
check "the credential store is root 0700" "[ \"\$(stat -c '%U %a' /run/blackglass/credentials)\" = 'root 700' ]"
check "the broker socket is root:blackglass-agent 0660" "[ \"\$(stat -c '%U:%G %a' /run/blackglass/credential.sock)\" = 'root:blackglass-agent 660' ]"

# ---- plans: B and A attack each other; both probe after A has started ----
for id in "$A" "$B" "$C" "$R"; do install -d -m 0755 "/etc/blackglass-stub/plans/$id"; done
printf 'probe=yes\nprobedelay=3\nother=%s\notherslot=00\nport=%s\notherport=%s\nsleep=10\nexit=kill\n' "$A" "$((port_base + 1))" "$port_base" > "/etc/blackglass-stub/plans/$B/plan"
printf 'probe=yes\nprobedelay=2\nother=%s\notherslot=01\nport=%s\notherport=%s\nsleep=3\nexit=3\n' "$B" "$port_base" "$((port_base + 1))" > "/etc/blackglass-stub/plans/$A/plan"

# ---- I3: credentials only through the broker ----
check "the broker stores A's credential for the agent" "[ \"\$(prepare $A 1)\" = ok ]"
check "the broker stores B's credential for the agent" "[ \"\$(prepare $B 1)\" = ok ]"
check "the stored credential is root 0400 in a root 0700 directory" "[ \"\$(stat -c '%U %a' /run/blackglass/credentials/$A/process) \$(stat -c '%U %a' /run/blackglass/credentials/$A)\" = 'root 400 root 700' ]"
check "the agent cannot write in the store" "! as_agent sh -c 'echo x > /run/blackglass/credentials/x' 2>/dev/null"
check "the agent cannot plant a link in the store" "! as_agent ln -s /etc/shadow /run/blackglass/credentials/$X 2>/dev/null"
check "the agent cannot read a stored credential" "! as_agent cat /run/blackglass/credentials/$A/process >/dev/null 2>&1"
printf 'prepare ../../etc/shadow 1\npc1_%s\n' "$(head -c 43 /dev/zero | tr '\0' A)" > "$work/req" && chmod 0644 "$work/req"
check "a traversal attempt id is refused" "[ \"\$(broker_as blackglass-agent $work/req)\" = 'error bad_attempt' ]"
printf 'prepare %s 1\n/etc/shadow\n' "$X" > "$work/req"
check "a path in place of the credential is refused" "[ \"\$(broker_as blackglass-agent $work/req)\" = 'error bad_credential' ]"
printf 'prepare %s 1\nhs1_%s\n' "$X" "$(head -c 43 /dev/zero | tr '\0' A)" > "$work/req"
check "a host secret in place of the credential is refused" "[ \"\$(broker_as blackglass-agent $work/req)\" = 'error bad_credential' ]"
head -c 4096 /dev/zero | tr '\0' a > "$work/req"
check "an oversized request is refused" "[ \"\$(broker_as blackglass-agent $work/req)\" = 'error too_large' ]"
printf 'copy /etc/shadow /run/blackglass/credentials/%s/process\n' "$X" > "$work/req"
check "no other verb exists" "[ \"\$(broker_as blackglass-agent $work/req)\" = 'error malformed' ]"
printf 'prepare %s 1\n%s\n' "$X" "$(new_credential)" > "$work/req"
check "another user cannot even connect" "[ \"\$(broker_as nobody $work/req)\" = connect_refused ]"
check "root (not the agent) is refused by the broker's peer check" "[ \"\$(broker_as root $work/req)\" = 'error peer_refused' ]"
check "nothing was stored for the refused requests" "[ ! -e /run/blackglass/credentials/$X ]"
check "generation 2 replaces generation 1" "[ \"\$(prepare $C 2)\" = ok ]"
check "a lower generation is stale" "[ \"\$(prepare $C 1)\" = 'error stale' ]"
check "the same generation with other bytes is a conflict" "[ \"\$(prepare $C 2)\" = 'error conflict' ]"
pids=()
for g in $(seq 3 10); do (prepare "$C" "$g" > "$work/concurrent.$g") & pids+=($!); done # 8: the socket's MaxConnections
wait "${pids[@]}" # only these: a bare wait would also wait for the report's tee
check "concurrent preparations each answer ok or stale and end at the highest generation" "[ \"\$(cat /run/blackglass/credentials/$C/generation)\" = 10 ] && [ \"\$(cat $work/concurrent.* | grep -cx -e ok -e 'error stale')\" = 8 ]"
check "the stored credential is the highest generation's" "[ \"\$(sha < /run/blackglass/credentials/$C/process)\" = \"\$(cat $work/$C.sha.10)\" ]"
check "a unit without a prepared credential never starts" "! as_agent systemctl --no-ask-password start $(unit 04 "$X") 2>/dev/null"

# ---- polkit (A10-D4) and the start ----
check "the agent starts B (slot 01) through polkit" "as_agent systemctl --no-ask-password start $(unit 01 "$B")"
check "the agent starts A (slot 00) through polkit" "as_agent systemctl --no-ask-password start $(unit 00 "$A")"
pidA="$(systemctl show -p MainPID --value "$(unit 00 "$A")")" pidB="$(systemctl show -p MainPID --value "$(unit 01 "$B")")"
printf 'otherpid=%s\n' "$pidA" >> "/etc/blackglass-stub/plans/$B/plan"
printf 'otherpid=%s\n' "$pidB" >> "/etc/blackglass-stub/plans/$A/plan"
check "polkit refuses enable" "! as_agent systemctl --no-ask-password enable $(unit 00 "$A") 2>/dev/null"
check "polkit refuses mask" "! as_agent systemctl --no-ask-password mask $(unit 00 "$A") 2>/dev/null"
check "polkit refuses restart" "! as_agent systemctl --no-ask-password restart $(unit 00 "$A") 2>/dev/null"
check "polkit refuses kill" "! as_agent systemctl --no-ask-password kill $(unit 00 "$A") 2>/dev/null"
check "polkit refuses set-property" "! as_agent systemctl --no-ask-password set-property $(unit 00 "$A") MemoryMax=8G 2>/dev/null"
check "polkit refuses the retired shared-uid name" "! as_agent systemctl --no-ask-password start blackglass-mission@$X.service 2>/dev/null"
check "polkit refuses slot 64" "! as_agent systemctl --no-ask-password start blackglass-mission-64@$X.service 2>/dev/null"
check "polkit refuses another unit" "! as_agent systemctl --no-ask-password stop ssh.service 2>/dev/null"
check "polkit refuses the broker's units" "! as_agent systemctl --no-ask-password stop blackglass-credential.socket 2>/dev/null"
check "polkit refuses a transient unit" "! as_agent systemd-run --no-ask-password --unit=$(unit 05 "$X") /bin/true 2>/dev/null"
sleep 5

# ---- C1: distinct identities, no reach between live attempts ----
uidA="$(systemctl show -p UID --value "$(unit 00 "$A")")" uidB="$(systemctl show -p UID --value "$(unit 01 "$B")")"
check "A and B run as different uids ($uidA, $uidB)" "[ -n \"$uidA\" ] && [ -n \"$uidB\" ] && [ \"$uidA\" != \"$uidB\" ]"
check "A runs as blackglass-m00 and B as blackglass-m01" "[ \"\$(systemctl show -p User --value $(unit 00 "$A"))\" = blackglass-m00 ] && [ \"\$(systemctl show -p User --value $(unit 01 "$B"))\" = blackglass-m01 ]"
check "neither runs as the agent" "[ \"$uidA\" != \"\$(id -u blackglass-agent)\" ] && [ \"$uidB\" != \"\$(id -u blackglass-agent)\" ]"
for pair in "A $A B" "B $B A"; do
  set -- $pair
  self="$2" peer="$3"
  check "$1 read exactly its own credential (LoadCredential)" "has $self credential=readable && [ \"\$(probe_of $self | sed -n 's/^credential_sha256=//p')\" = \"\$(cat $work/$self.sha.1)\" ]"
  check "$1 cannot see /run/blackglass, the host secret or the agent's state" "has $self run_blackglass=invisible && has $self host_secret=invisible && has $self agent_state=invisible"
  check "$1 sees only its own private outbox" "has $self private_outboxes=$self,"
  check "$1 cannot read, write, rename or delete $peer's results" "has $self other_outbox=denied"
  check "$1 cannot read $peer's environment, memory or descriptors" "has $self other_environ=denied && has $self other_mem=denied && has $self other_fd=denied"
  check "$1 cannot reach $peer's files through /proc/<pid>/root" "has $self other_proc_root=denied"
  check "$1 cannot signal $peer's process" "has $self other_signal=denied"
  check "$1 cannot read $peer's credential or logs" "has $self other_credential=denied && has $self other_logs=denied"
  check "$1 cannot listen on TCP 127.0.0.1:5081 (the tunnel's port; seccomp)" "has $self tcp_listen_5081=denied"
  check "$1 can bind its own UDP port" "has $self udp_own_port=allowed"
  check "$1 can write its own outbox and not /etc" "has $self own_write=allowed && has $self etc_write=denied"
done
if systemctl --version | grep -q '+BPF_FRAMEWORK'; then
  check "A cannot bind B's UDP port (BPF socket-bind)" "has $A udp_other_port=denied"
  check "A cannot even bind TCP 5081 (BPF socket-bind)" "has $A tcp_bind_5081=denied"
else
  note "this systemd is built without BPF_FRAMEWORK: SocketBindAllow/Deny are not enforced (A: udp_other_port=$(probe_of "$A" | sed -n 's/^udp_other_port=//p'), tcp_bind_5081=$(probe_of "$A" | sed -n 's/^tcp_bind_5081=//p')); a TCP listener is still impossible (seccomp), and per-slot UDP ports are Stage 12's blocking acceptance requirement"
fi

# ---- the agent reads every result and changes none ----
check "the agent's unit reads A's result" "as_agent_unit cat /var/lib/blackglass/outbox/$A/probe.ready >/dev/null"
check "the agent's unit reads B's result" "as_agent_unit cat /var/lib/blackglass/outbox/$B/probe.ready >/dev/null"
check "the agent's unit cannot write in an outbox" "! as_agent_unit sh -c 'echo x > /var/lib/blackglass/outbox/$A/agent-write' 2>/dev/null"
check "the agent's unit cannot rename a result" "! as_agent_unit mv /var/lib/blackglass/outbox/$A/probe.ready /var/lib/blackglass/outbox/$A/moved 2>/dev/null"
check "the agent's unit cannot delete a result" "! as_agent_unit rm -f /var/lib/blackglass/outbox/$A/probe.ready 2>/dev/null; [ -e /var/lib/private/blackglass-outbox/$A/probe.ready ]"
check "outside its unit the agent cannot reach the outboxes at all" "! as_agent ls /var/lib/private/blackglass-outbox 2>/dev/null"

# ---- one live unit per slot; an attempt runs at most once ----
install -d -m 0755 "/etc/blackglass-stub/plans/$D"
printf 'probe=yes\nsleep=1\n' > "/etc/blackglass-stub/plans/$D/plan"
prepare "$D" 1 >/dev/null
check "a second unit in B's live slot is refused (slot-guard)" "! as_agent systemctl --no-ask-password start $(unit 01 "$D") 2>/dev/null"
check "and it never ran" "[ -z \"\$(probe_of $D)\" ]"
sleep 9 # A exits 3, B is killed

check "A's exit record says exited 3" "grep -qx 'code=exited' /var/lib/blackglass/exits/$A.exit && grep -qx 'status=3' /var/lib/blackglass/exits/$A.exit"
check "B's exit record says killed" "grep -qx 'code=killed' /var/lib/blackglass/exits/$B.exit"
check "exit records are root:blackglass-agent 0640" "[ \"\$(stat -c '%U:%G %a' /var/lib/blackglass/exits/$A.exit)\" = 'root:blackglass-agent 640' ]"
check "results survive the unit's exit" "[ -s /var/lib/private/blackglass-outbox/$A/probe.ready ] && as_agent_unit cat /var/lib/blackglass/outbox/$A/probe.ready >/dev/null"
prepare "$A" 1 >/dev/null
check "an ended attempt's unit never starts again" "! as_agent systemctl --no-ask-password start $(unit 00 "$A") 2>/dev/null"

# ---- C1: a later attempt handed A's recycled uid cannot reach A's files ----
# systemd first tries the uid that owns one of a new unit's directories: a logs directory owned by A's old uid hands R that uid.
install -d -m 0750 -o "$uidA" -g "$uidA" "/var/log/private/blackglass-mission/$R"
printf 'probe=yes\nhistoric=%s\nsleep=1\n' "$A" > "/etc/blackglass-stub/plans/$R/plan"
prepare "$R" 1 >/dev/null
systemctl start "$(unit 03 "$R")" # as root: the slot-03 unit, started like any other
sleep 3
check "R was handed A's old uid $uidA" "[ \"\$(stat -c %u /var/lib/private/blackglass-outbox/$R/probe.ready)\" = $uidA ]"
check "R cannot read, write, rename or delete A's results" "has $R historic_outbox=denied"
check "A's results are unchanged" "[ \"\$(cat /var/lib/private/blackglass-outbox/$A/probe.ready)\" = own ] && [ ! -e /var/lib/private/blackglass-outbox/$A/intrusion ]"

# ---- I3: the delivered credential is exactly the prepared one; the store never leaks into a sandbox ----
install -d -m 0755 "/etc/blackglass-stub/plans/$C"
printf 'probe=yes\nsleep=1\n' > "/etc/blackglass-stub/plans/$C/plan"
check "C (slot 02) starts with its generation-10 credential" "as_agent systemctl --no-ask-password start $(unit 02 "$C")"
sleep 2
check "C's unit received exactly the generation-10 credential" "[ \"\$(probe_of $C | sed -n 's/^credential_sha256=//p')\" = \"\$(cat $work/$C.sha.10)\" ]"

# ---- systemd's own assessment, recorded ----
systemd-analyze security --no-pager "$(unit 02 "$C")" 2>/dev/null | tail -n 1
systemd-analyze security --no-pager blackglass-agent.service | tail -n 1
check "/opt/blackglass/server was not touched" "! find /opt/blackglass/server -newer /etc/blackglass-stub/plans/$A/plan -print -quit 2>/dev/null | grep -q ."

state="/var/log/blackglass/verify-state-$(date -u +%Y%m%dT%H%M%SZ)"
for id in "$A" "$B" "$R"; do echo "$id $(sha < "/var/lib/private/blackglass-outbox/$id/probe.ready")"; done > "$state"
echo "evidence kept: /var/lib/private/blackglass-outbox/{$A,$B,$C,$R} /var/lib/blackglass/exits/*.exit /var/log/private/blackglass-mission/*"
echo "after a reboot, run: verify.sh --yes --after-restart $state"
echo "verify: $fails failure(s); report $report"
exit $(( fails > 0 ))

#!/usr/bin/env bash
# The Stage 10 VM checks (run as root on bg-dev-mission01 after install.sh --stub; NOT RUN until the owner authorizes the
# infrastructure checkpoint). Two isolated test attempts A and B run the stub under the real template; nothing touches
# /opt/blackglass/server, and the test attempts' outbox and exit records are kept as evidence (listed at the end).
#   verify.sh --yes [--report <file>]
# Expected: every line "ok"; any "FAIL" makes the exit status 1.
set -uo pipefail
yes=0 report=/var/log/blackglass/verify-$(date -u +%Y%m%dT%H%M%SZ).txt
while [[ $# -gt 0 ]]; do case "$1" in --yes) yes=1; shift ;; --report) report="${2:?}"; shift 2 ;; *) echo "unknown: $1" >&2; exit 64 ;; esac; done
[[ "$(id -u)" == 0 ]] || { echo "run as root" >&2; exit 77; }
[[ "$yes" == 1 ]] || { echo "refusing without --yes" >&2; exit 64; }
[[ "$(readlink -f /opt/blackglass/mission/current)" == /opt/blackglass/mission/releases/stub ]] || { echo "the stub release is not selected" >&2; exit 78; }
exec > >(tee "$report") 2>&1
fails=0
check() { if eval "$2"; then echo "ok   - $1"; else echo "FAIL - $1"; fails=$((fails + 1)); fi; }
as_agent() { runuser -u blackglass-agent -- "$@"; }
A="$(cat /proc/sys/kernel/random/uuid)" B="$(cat /proc/sys/kernel/random/uuid)" C="$(cat /proc/sys/kernel/random/uuid)"
cleanup() {
  for id in "$A" "$B" "$C"; do systemctl stop "blackglass-mission@$id.service" 2>/dev/null; rm -rf "/run/blackglass/launch/$id" "/etc/blackglass-stub/plans/$id"; done
}
trap cleanup EXIT

# Plans: A probes the sandbox (B is its "other"); B publishes a probe file and waits.
for id in "$A" "$B"; do install -d -m 0755 "/etc/blackglass-stub/plans/$id"; done
printf 'probe=yes\nother=%s\nsleep=4\nexit=3\n' "$B" > "/etc/blackglass-stub/plans/$A/plan"
printf 'probe=yes\nsleep=8\nexit=kill\n' > "/etc/blackglass-stub/plans/$B/plan"
# A process credential for each, as the agent hands it over.
for id in "$A" "$B"; do
  as_agent install -d -m 0700 "/run/blackglass/launch/$id"
  as_agent sh -c "printf 'pc1_%s' \"\$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=')\" > /run/blackglass/launch/$id/process && chmod 0400 /run/blackglass/launch/$id/process"
done

# polkit (A10-D4): the agent may start and stop mission units only.
check "the agent starts B through polkit" "as_agent systemctl --no-ask-password start blackglass-mission@$B.service"
sleep 1
check "the agent starts A through polkit" "as_agent systemctl --no-ask-password start blackglass-mission@$A.service"
check "polkit refuses enable" "! as_agent systemctl --no-ask-password enable blackglass-mission@$A.service 2>/dev/null"
check "polkit refuses mask" "! as_agent systemctl --no-ask-password mask blackglass-mission@$A.service 2>/dev/null"
check "polkit refuses restart" "! as_agent systemctl --no-ask-password restart blackglass-mission@$A.service 2>/dev/null"
check "polkit refuses another unit" "! as_agent systemctl --no-ask-password stop ssh.service 2>/dev/null"
check "polkit refuses a malformed mission name" "! as_agent systemctl --no-ask-password start 'blackglass-mission@NOT-A-UUID.service' 2>/dev/null"
check "polkit refuses the agent's own unit" "! as_agent systemctl --no-ask-password restart blackglass-agent.service 2>/dev/null"
check "polkit refuses kill" "! as_agent systemctl --no-ask-password kill blackglass-mission@$A.service 2>/dev/null"
check "polkit refuses reset-failed" "! as_agent systemctl --no-ask-password reset-failed blackglass-mission@$A.service 2>/dev/null"
check "polkit refuses set-property" "! as_agent systemctl --no-ask-password set-property blackglass-mission@$A.service MemoryMax=8G 2>/dev/null"
check "polkit refuses a transient unit" "! as_agent systemd-run --no-ask-password --unit=blackglass-mission@$C.service /bin/true 2>/dev/null"
sleep 6

# Isolation (spec §9.2): what A saw from inside its sandbox.
probe="/var/log/blackglass/$A/probe.txt"
check "A read its credential through LoadCredential" "grep -qx 'credential=readable' $probe"
check "A cannot see the launch directory" "grep -qx 'launch_dir=invisible' $probe"
check "A cannot see the host secret" "grep -qx 'host_secret=invisible' $probe"
check "A sees only its own outbox directory" "grep -qx 'outbox_entries=$A,' $probe"
for op in read write rename delete; do check "A cannot $op in B's outbox" "grep -qx 'other_$op=denied' $probe"; done
check "A can write its own outbox" "grep -qx 'own_write=allowed' $probe"
# Through B's process (security review C1). These FAIL with one shared mission uid: they pass only once each attempt runs as its
# own uid (the owner's checkpoint decision).
check "A cannot read through B's /proc root" "grep -qx 'other_proc_root_read=denied' $probe || grep -qx 'other_process=invisible' $probe"
check "A cannot write through B's /proc root" "grep -qx 'other_proc_root_write=denied' $probe || grep -qx 'other_process=invisible' $probe"
check "A cannot signal B" "grep -qx 'other_signal=denied' $probe || grep -qx 'other_process=invisible' $probe"
check "A cannot read B's credential" "grep -qx 'other_credential=denied' $probe"
check "A cannot read B's logs" "grep -qx 'other_logs=denied' $probe"
check "A cannot bind the tunnel's port 127.0.0.1:5081" "grep -qx 'tcp_bind_5081=denied' $probe"
check "A's credential is the pc1 secret the agent handed over" "grep -qx 'credential_shape=pc1' $probe"
check "A cannot write /etc" "grep -qx 'etc_write=denied' $probe"
# The agent reads both outboxes and writes neither.
check "the agent reads A's result" "as_agent cat /var/lib/blackglass/outbox/$A/probe.ready >/dev/null"
check "the agent reads B's result" "as_agent cat /var/lib/blackglass/outbox/$B/probe.ready >/dev/null"
check "the agent cannot write in an outbox" "! as_agent sh -c 'echo x > /var/lib/blackglass/outbox/$A/agent-write' 2>/dev/null"
check "the agent cannot rename a result" "! as_agent mv /var/lib/blackglass/outbox/$A/probe.ready /var/lib/blackglass/outbox/$A/moved 2>/dev/null"
check "the agent cannot delete a result" "! as_agent rm -f /var/lib/blackglass/outbox/$A/probe.ready 2>/dev/null || [ -e /var/lib/blackglass/outbox/$A/probe.ready ]"
check "the mission user cannot read the agent's state" "! runuser -u blackglass-mission -- ls /var/lib/blackglass/agent 2>/dev/null"

# Exit records (record-exit as root) after A exits 3 and B is killed.
sleep 4
check "A's exit record says exited 3" "grep -qx 'code=exited' /var/lib/blackglass/exits/$A.exit && grep -qx 'status=3' /var/lib/blackglass/exits/$A.exit"
check "B's exit record says killed" "grep -qx 'code=killed' /var/lib/blackglass/exits/$B.exit"
check "exit records are root:blackglass-agent 0640" "[ \"\$(stat -c '%U:%G %a' /var/lib/blackglass/exits/$A.exit)\" = 'root:blackglass-agent 640' ]"

# A symlinked credential source (security review I3): systemd must never deliver another file. FAILS while LoadCredential reads an
# agent-writable path as root (the owner's checkpoint decision).
as_agent install -d -m 0700 "/run/blackglass/launch/$C"
as_agent ln -s /etc/shadow "/run/blackglass/launch/$C/process"
install -d -m 0755 "/etc/blackglass-stub/plans/$C"
printf 'probe=yes\nsleep=1\nexit=0\n' > "/etc/blackglass-stub/plans/$C/plan"
as_agent systemctl --no-ask-password start "blackglass-mission@$C.service" 2>/dev/null
sleep 3
check "a symlinked credential source never reaches the unit" "! grep -qx 'credential_shape=other' /var/log/blackglass/$C/probe.txt 2>/dev/null || [ ! -e /var/log/blackglass/$C/probe.txt ]"
check "and the unit got no copy of the link's target" "! grep -qx 'credential=readable' /var/log/blackglass/$C/probe.txt 2>/dev/null"

# systemd's own assessment, recorded.
systemd-analyze security --no-pager "blackglass-mission@$A.service" | tail -n 3
systemd-analyze security --no-pager blackglass-agent.service | tail -n 3
check "/opt/blackglass/server was not touched" "! find /opt/blackglass/server -newer $probe -print -quit 2>/dev/null | grep -q ."
echo "evidence kept: /var/lib/blackglass/outbox/{$A,$B,$C} /var/lib/blackglass/exits/{$A,$B,$C}.exit /var/log/blackglass/{$A,$B,$C}"
echo "verify: $fails failure(s); report $report"
exit $(( fails > 0 ))

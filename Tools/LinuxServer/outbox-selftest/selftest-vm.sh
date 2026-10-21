#!/usr/bin/env bash
# The Linux Unity outbox self-test on bg-dev-mission01 (Stage 12 plan, Task 1; host manifest
# Docs/superpowers/plans/2026-10-20-phase-13-stage12-outbox-selftest-manifest.md). Run only under that approved manifest, from
# the workstation, one step at a time, as root through the existing alias:
#   ssh bg-dev-mission01 'bash -s' -- <step> [args] < Tools/LinuxServer/outbox-selftest/selftest-vm.sh | sed -E '<address redaction>'
# Steps: preflight | install <rev40> <archive-sha256> <files-sha256> | publish | hold | resume | location | after | cleanup
# Every phase is a transient unit (systemd-run): a dynamic user, no network, a read-only system, the installed Blackglass files
# invisible, a disposable state directory under /var/lib/private/blackglass-selftest, five minutes at most. Nothing persistent is
# installed; cleanup removes every path this script creates. It never touches /opt/blackglass, the installed units, the agent,
# the tunnel or the SSH configuration.
set -u
export LC_ALL=C

step="${1:-}"
rev_file=/root/selftest-rev
base_root=/opt/blackglass-selftest
state_private=/var/lib/private/blackglass-selftest
state_link=/var/lib/blackglass-selftest
before=/root/selftest-before.txt
id1=019a0000-0000-7000-8000-00000000a001
id2=019a0000-0000-7000-8000-00000000a002
id3=019a0000-0000-7000-8000-00000000a003
units=(publish hold resume location)

props=(
  -p DynamicUser=yes -p User=blackglass-selftest -p UMask=0022 -p StateDirectoryMode=0755
  -p ProtectSystem=strict -p ProtectHome=yes -p PrivateTmp=yes -p PrivateDevices=yes -p PrivateNetwork=yes -p PrivateIPC=yes
  -p NoNewPrivileges=yes -p CapabilityBoundingSet= -p AmbientCapabilities=
  -p "RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6" -p Environment=HOME=/tmp
  -p RestrictNamespaces=yes -p RestrictRealtime=yes -p RestrictSUIDSGID=yes -p LockPersonality=yes
  -p ProtectKernelTunables=yes -p ProtectKernelModules=yes -p ProtectKernelLogs=yes -p ProtectControlGroups=yes
  -p ProtectClock=yes -p ProtectHostname=yes -p ProtectProc=invisible -p SystemCallArchitectures=native
  -p LimitCORE=0 -p MemoryMax=2G -p CPUQuota=200% -p TasksMax=256 -p RuntimeMaxSec=300
  -p "InaccessiblePaths=-/opt/blackglass -/etc/blackglass -/var/lib/blackglass -/run/blackglass -/var/log/blackglass -/var/lib/private/blackglass-outbox"
)

fail() { echo "STOP: $*" >&2; exit 1; } # stderr, so it survives $(...); the workstation merges it (2>&1)

binary() {
  local rev
  rev="$(cat "$rev_file" 2>/dev/null)" || fail "not installed (run install first)"
  echo "$base_root/${rev:0:7}/BlackglassServer.x86_64"
}

baseline() { # the integrity record: hashes stay on the VM, only counts and the tunnel's identity are printed
  {
    find /opt/blackglass/server /opt/blackglass/agent /opt/blackglass/mission /etc/blackglass -type f -print0 2>/dev/null | sort -z | xargs -0 -r sha256sum
    # every blackglass unit file and drop-in, and every link to one (.wants)
    find /etc/systemd/system -path '*blackglass-*' -type f -print0 | sort -z | xargs -0 -r sha256sum
    find /etc/systemd/system -path '*blackglass-*' -type l -printf '%p -> %l\n' | LC_ALL=C sort
    readlink /opt/blackglass/agent/current /opt/blackglass/mission/current
    # the SSH daemon's configuration: the main file and every drop-in (e.g. sshd_config.d/50-cloud-init.conf)
    find /etc/ssh/sshd_config /etc/ssh/sshd_config.d -type f -print0 2>/dev/null | sort -z | xargs -0 -r sha256sum
    # the kernel's loaded modules (names) and module loading: nothing here may load one
    awk '{print "module", $1}' /proc/modules | LC_ALL=C sort
    echo "modules_disabled=$(sysctl -n kernel.modules_disabled)"
  }
}

tunnel_identity() { systemctl show blackglass-tunnel -p MainPID -p ActiveEnterTimestamp -p NRestarts | LC_ALL=C sort | tr '\n' ' '; echo; }

# files <id>: the state directory as root sees it, never following a link: every entry's type, mode, owner uid, size and name,
# then the SHA-256 of the regular files only (a planted link or FIFO is listed, never opened).
files() {
  local d="$state_private/$1"
  find "$d" -mindepth 1 -maxdepth 1 -printf 'entry type=%y mode=%m uid=%U size=%s name=%f\n' | LC_ALL=C sort
  find "$d" -mindepth 1 -maxdepth 1 -type f -printf '%f\0' | (cd "$d" && LC_ALL=C sort -z | xargs -0 -r sha256sum --)
}

# owner_check <id> <token>: the uid that owns what the process wrote (the kernel's record, independent of the process's
# own output) must equal the euid the self-test reported in that run.
owner_check() {
  local euid owners
  euid="$(journalctl "BLACKGLASS_SELFTEST=$2" -o cat --no-pager | sed -n 's/^\[Selftest\] uid ok euid=\([0-9]*\)$/\1/p' | head -1)"
  owners="$(find "$state_private/$1" -mindepth 1 -maxdepth 1 -printf '%U\n' | LC_ALL=C sort -u | tr '\n' ' ')"
  if [ -n "$euid" ] && [ "$euid" != 0 ] && [ "$owners" = "$euid " ]; then
    echo "owner_matches_euid=yes uid=$euid"
  else
    echo "owner_matches_euid=NO euid=${euid:-none} owners=${owners:-none}"
  fi
}

new_token() { cat /proc/sys/kernel/random/uuid; }

# report_run <token>: the run's journal lines (every line of a run carries BLACKGLASS_SELFTEST=<token>), then the credentials
# journald holds for the unit's stdout stream (_TRANSPORT=stdout only, so PID 1's own lines about the unit, which carry
# _UID=0, are excluded). Those are the credentials systemd connected the stream with, after switching to the unit's user and
# before exec: corroboration that the unit ran under its dynamic user, not proof of the running process's uid. That proof is
# the owner of the files it wrote (owner_check) and, for the held run, /proc.
report_run() {
  journalctl --sync 2>/dev/null
  journalctl "BLACKGLASS_SELFTEST=$1" -o cat --no-pager | grep -E '^\[(Server|Selftest)\]'
  echo "stdout_stream_identity=$(journalctl "BLACKGLASS_SELFTEST=$1" _TRANSPORT=stdout -o export --no-pager | grep -aE '^_(UID|GID)=' | LC_ALL=C sort -u | tr '\n' ' ')"
}

run_phase() { # run_phase <unit> <state id> <phase> <selftest id>: in the foreground; the exit status, then the journal report
  local bin code
  bin="$(binary)" || exit 1
  last_token="$(new_token)"
  systemd-run --quiet --unit="blackglass-selftest-$1" --wait --collect "${props[@]}" -p "LogExtraFields=BLACKGLASS_SELFTEST=$last_token" \
    -p "StateDirectory=blackglass-selftest/$2" "$bin" -batchmode -nographics -logFile - -outboxSelftest "$3" -selftestId "$4"
  code=$?
  report_run "$last_token"
  echo "exit=$code"
}

case "$step" in
  preflight)
    echo "kernel=$(uname -r)"
    echo "systemd=$(systemctl --version | head -1)"
    echo "tunnel=$(systemctl is-active blackglass-tunnel)"
    echo "agent_enabled=$(systemctl is-enabled blackglass-agent 2>&1)"
    echo "agent_active=$(systemctl is-active blackglass-agent 2>&1)"
    echo "mission_units=$(systemctl list-units 'blackglass-mission-*' --all --no-legend | wc -l)"
    echo "selftest_units=$(systemctl list-units 'blackglass-selftest-*' --all --no-legend | wc -l)"
    for p in "$base_root" "$state_private" "$state_link" "$before" "$rev_file"; do [ -e "$p" ] && echo "present=$p"; done
    echo "tunnel_identity=$(tunnel_identity)"
    echo "disk=$(df -h / | tail -1 | awk '{print $4}') free"
    [ -e "$before" ] || baseline > "$before"
    echo "baseline_lines=$(wc -l < "$before")"
    echo "preflight-done"
    ;;
  install)
    rev="${2:?rev}"; archive_sha="${3:?archive sha256}"; files_sha="${4:?files.sha256 sha256}"
    [[ "$rev" =~ ^[0-9a-f]{40}$ ]] || fail "the revision must be 40 hex characters"
    archive="/root/blackglass-selftest-${rev:0:7}.tar.gz"
    dir="$base_root/${rev:0:7}"
    [ -f "$archive" ] || fail "no upload at $archive"
    chmod 0600 "$archive"
    echo "$archive_sha  $archive" | sha256sum -c - || fail "archive hash"
    mkdir -p "$dir"
    tar -xzf "$archive" -C "$dir" --strip-components=1 --no-same-owner || fail "extract"
    rm -f "$archive"
    chown -R root:root "$base_root"
    chmod -R u=rwX,go=rX "$base_root"
    chmod 0755 "$dir/BlackglassServer.x86_64"
    # every shipped file against the package's own list, and the list against the manifest's hash
    echo "$files_sha  $dir/files.sha256" | sha256sum -c - || fail "files.sha256 hash"
    (cd "$dir" && sha256sum -c --quiet files.sha256) || fail "a shipped file differs from files.sha256"
    echo "files_verified=$(wc -l < "$dir/files.sha256")"
    [ "$(find "$dir" -type f ! -name files.sha256 | wc -l)" = "$(wc -l < "$dir/files.sha256")" ] || fail "files outside the list"
    grep -qx "provenance=verified" "$dir/build-info.txt" || fail "build-info provenance"
    grep -qx "revision=$rev" "$dir/build-info.txt" || fail "build-info revision"
    grep -qx "dirty=false" "$dir/build-info.txt" || fail "build-info dirty"
    grep -qx "variant=release" "$dir/build-info.txt" || fail "build-info variant"
    echo "$rev" > "$rev_file"
    echo "installed=$dir"
    ;;
  publish)
    run_phase publish "$id1" publish "$id1"
    files "$id1"
    owner_check "$id1" "$last_token"
    ;;
  hold)
    bin="$(binary)" || exit 1
    token="$(new_token)"
    systemd-run --quiet --unit=blackglass-selftest-hold --collect "${props[@]}" -p "LogExtraFields=BLACKGLASS_SELFTEST=$token" \
      -p "StateDirectory=blackglass-selftest/$id2" "$bin" -batchmode -nographics -logFile - -outboxSelftest hold -selftestId "$id2"
    held=no
    for _ in $(seq 1 120); do
      if journalctl "BLACKGLASS_SELFTEST=$token" -o cat --no-pager | grep -q '^\[Selftest\] held$'; then held=yes; break; fi
      systemctl is-active -q blackglass-selftest-hold || break
      sleep 1
    done
    echo "held=$held"
    for prop in User DynamicUser PrivateNetwork NoNewPrivileges; do
      echo "unit_$prop=$(systemctl show -p "$prop" --value blackglass-selftest-hold)"
    done
    pid="$(systemctl show -p MainPID --value blackglass-selftest-hold)"
    if [ "$pid" != "0" ]; then
      echo "process_uid=$(ps -o uid= -p "$pid" | tr -d ' ')"
      echo "process_gid=$(ps -o gid= -p "$pid" | tr -d ' ')"
      echo "process_user=$(ps -o user:32= -p "$pid" | tr -d ' ')"
      for key in Uid Gid NoNewPrivs CapEff Seccomp; do
        echo "proc_$key=$(awk -v k="$key:" '$1 == k { $1 = ""; sub(/^ /, ""); print }' "/proc/$pid/status")"
      done
    fi
    systemctl kill --signal=SIGKILL blackglass-selftest-hold
    for _ in $(seq 1 30); do systemctl is-active -q blackglass-selftest-hold || break; sleep 1; done
    echo "active_after_kill=$(systemctl is-active blackglass-selftest-hold)"
    report_run "$token"
    files "$id2"
    owner_check "$id2" "$token"
    ;;
  resume)
    run_phase resume "$id2" resume "$id2"
    files "$id2"
    owner_check "$id2" "$last_token"
    ;;
  location)
    run_phase location "$id3" publish "$id1"
    echo "entries=$(find "$state_private/$id3" -mindepth 1 -maxdepth 1 | wc -l)"
    ;;
  after)
    [ -e "$before" ] || fail "no baseline"
    if baseline | cmp -s - "$before"; then echo "integrity=unchanged"; else echo "integrity=CHANGED"; fi
    echo "tunnel=$(systemctl is-active blackglass-tunnel)"
    echo "tunnel_identity=$(tunnel_identity)"
    echo "agent_enabled=$(systemctl is-enabled blackglass-agent 2>&1)"
    echo "selftest_units=$(systemctl list-units 'blackglass-selftest-*' --all --no-legend | wc -l)"
    ;;
  cleanup)
    for u in "${units[@]}"; do
      systemctl stop "blackglass-selftest-$u" 2>/dev/null
      systemctl reset-failed "blackglass-selftest-$u" 2>/dev/null
    done
    rm -rf "$state_private" "$state_link" "$base_root" /root/blackglass-selftest-*.tar.gz "$before" "$rev_file"
    left=0
    for p in "$base_root" "$state_private" "$state_link" "$before" "$rev_file"; do [ -e "$p" ] && { echo "left=$p"; left=1; }; done
    [ "$left" = 0 ] && echo "cleaned"
    echo "tunnel=$(systemctl is-active blackglass-tunnel)"
    ;;
  *)
    echo "usage: selftest-vm.sh preflight | install <rev40> <archive-sha256> <files-sha256> | publish | hold | resume | location | after | cleanup"
    exit 64
    ;;
esac

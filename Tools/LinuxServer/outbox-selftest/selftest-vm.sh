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
    find /etc/systemd/system -maxdepth 1 -name 'blackglass-*' -type f -print0 | sort -z | xargs -0 -r sha256sum
    readlink /opt/blackglass/agent/current /opt/blackglass/mission/current
  }
}

tunnel_identity() { systemctl show blackglass-tunnel -p MainPID -p ActiveEnterTimestamp -p NRestarts | tr '\n' ' '; echo; }

files() { # files <id>: the state directory's entries as root sees them, with their hashes
  local d="$state_private/$1"
  ls -ln "$d" | tail -n +2 | awk '{print $1, $3, $5, $9}'
  (cd "$d" && sha256sum -- * 2>/dev/null)
}

run_phase() { # run_phase <unit> <state id> <phase> <selftest id>: in the foreground, its exit status last
  local bin
  bin="$(binary)" || exit 1
  systemd-run --quiet --unit="blackglass-selftest-$1" --wait --collect --pipe "${props[@]}" -p "StateDirectory=blackglass-selftest/$2" \
    "$bin" -batchmode -nographics -logFile - -outboxSelftest "$3" -selftestId "$4" < /dev/null | grep -E '^\[(Server|Selftest)\]'
  echo "exit=${PIPESTATUS[0]}"
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
    ;;
  hold)
    bin="$(binary)" || exit 1
    systemd-run --quiet --unit=blackglass-selftest-hold --collect "${props[@]}" -p "StateDirectory=blackglass-selftest/$id2" \
      "$bin" -batchmode -nographics -logFile - -outboxSelftest hold -selftestId "$id2"
    inv="$(systemctl show -p InvocationID --value blackglass-selftest-hold)"
    held=no
    for _ in $(seq 1 120); do
      if journalctl "_SYSTEMD_INVOCATION_ID=$inv" -o cat --no-pager | grep -q '^\[Selftest\] held'; then held=yes; break; fi
      systemctl is-active -q blackglass-selftest-hold || break
      sleep 1
    done
    echo "held=$held"
    echo "unit=$(systemctl show blackglass-selftest-hold -p User -p DynamicUser -p PrivateNetwork -p NoNewPrivileges | tr '\n' ' ')"
    pid="$(systemctl show -p MainPID --value blackglass-selftest-hold)"
    [ "$pid" != "0" ] && echo "process=$(ps -o uid=,user=,comm= -p "$pid")"
    systemctl kill --signal=SIGKILL blackglass-selftest-hold
    for _ in $(seq 1 30); do systemctl is-active -q blackglass-selftest-hold || break; sleep 1; done
    echo "active_after_kill=$(systemctl is-active blackglass-selftest-hold)"
    journalctl "_SYSTEMD_INVOCATION_ID=$inv" -o cat --no-pager | grep -E '^\[(Server|Selftest)\]'
    files "$id2"
    ;;
  resume)
    run_phase resume "$id2" resume "$id2"
    files "$id2"
    ;;
  location)
    run_phase location "$id3" publish "$id1"
    echo "entries=$(ls -A "$state_private/$id3" | wc -l)"
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

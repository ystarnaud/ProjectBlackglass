#!/usr/bin/env bash
# Runs the net-test client's harness roles against the networked server on the VM through an SSH tunnel (networking experiment).
# Usage: Tools/run-net-harness.sh [--denied] [host]    (default host: bg-dev-mission01; port: $NET_PORT or 7770)
#   default:  against the fixture build started with `NET_FIXTURE=1 run-net.sh start` (reads the FIXTURE token file,
#             $BLACKGLASS_FIXTURE_TOKENS, default /opt/blackglass/dev-tokens-fixture.txt): visibility-b and visibility-a at the
#             same time (B must see nothing of what A is shown), then the part 2 roles a, b, ticket and badtoken, then
#             visibility-fault alone (a failing visibility source must deny information): seven roles.
#   --denied: against the release build started with `run-net.sh start` (reads the RELEASE token file, $BLACKGLASS_TOKENS,
#             default /opt/blackglass/dev-tokens.txt, which has no +fixture identity): the fixture-denied role only.
# Needs Builds/NetTestClient/NetTestClient.exe (Tools/build-netclient.sh). Logs: Builds/NetHarnessLogs/<UTC>/ (git-ignored).
# The tokens are read from the VM into shell variables and never printed. Every client this script started and the tunnel are
# stopped on any exit, and the last line reports what is left: "leftover clients=<n> tunnel=<pid|none>".
# Exit status: 0 all roles passed and nothing left over; 1 a role failed; 2 setup failed; 3 a client or the tunnel was left
# running (only when nothing else failed; an earlier non-zero status is kept).
set -u
mode=full
if [ "${1:-}" = "--denied" ]; then
  mode=denied
  shift
fi
root="$(cd "$(dirname "$0")/.." && pwd)"
host="${1:-bg-dev-mission01}"
port="${NET_PORT:-7770}"
exe="$root/Builds/NetTestClient/NetTestClient.exe"
if [ "$mode" = "denied" ]; then
  tokenfile="${BLACKGLASS_TOKENS:-/opt/blackglass/dev-tokens.txt}"
else
  tokenfile="${BLACKGLASS_FIXTURE_TOKENS:-/opt/blackglass/dev-tokens-fixture.txt}"
fi
logs="$root/Builds/NetHarnessLogs/$(date -u +%Y%m%dT%H%M%SZ)"
[ -x "$exe" ] || { echo "build the client first: Tools/build-netclient.sh"; exit 2; }
mkdir -p "$logs"

text=$(ssh -o BatchMode=yes "$host" "cat $tokenfile") || { echo "cannot read the token file on $host"; exit 2; }
token_of() { printf '%s\n' "$text" | awk -v a="$1" '$1==a {print $2; exit}'; }
if [ "$mode" = "denied" ] && grep -q '+fixture' <<< "$text"; then
  echo "the release token file on $host carries a +fixture identity; run make-dev-tokens.sh there (it replaces such a file)"
  exit 2
fi
tok_a=$(token_of account-a)
tok_b=$(token_of account-b)
tok_t=$(token_of account-ticket)
if [ -z "$tok_a" ] || [ -z "$tok_b" ] || [ -z "$tok_t" ]; then
  echo "the token file lacks account-a, account-b or account-ticket"
  exit 2
fi

# Git Bash has no pgrep: find the tunnel by its command line (the shell running this script must not match, so anchor on "ssh").
find_tunnel() {
  local f
  for f in /proc/[0-9]*/cmdline; do
    if tr '\0' ' ' < "$f" 2>/dev/null | grep -q "^ssh -f -N .*-L $port:127.0.0.1:$port"; then
      basename "$(dirname "$f")"
      return
    fi
  done
}
count_clients() { tasklist //NH //FI "IMAGENAME eq NetTestClient.exe" 2>/dev/null | grep -c "NetTestClient.exe"; }

tunnel_pid=""
b_pid=""
cleanup() {
  local rc=$?
  if [ -n "$b_pid" ]; then
    kill "$b_pid" 2>/dev/null
    wait "$b_pid" 2>/dev/null
  fi
  if [ -n "$tunnel_pid" ]; then kill "$tunnel_pid" 2>/dev/null; fi
  sleep 1
  local left
  left=$(find_tunnel)
  local clients
  clients=$(count_clients)
  echo "leftover clients=$clients tunnel=${left:-none}"
  if [ "$rc" -eq 0 ] && { [ "${clients:-0}" -ne 0 ] || [ -n "$left" ]; }; then exit 3; fi
}
trap cleanup EXIT
trap 'exit 130' INT TERM

ssh -f -N -o ExitOnForwardFailure=yes -o BatchMode=yes -L "$port:127.0.0.1:$port" "$host" || { echo "the tunnel could not be opened (is port $port free here?)"; exit 2; }
tunnel_pid=$(find_tunnel)

status=0
summary() { grep -h 'BLACKGLASS NET HARNESS' "$1" 2>/dev/null | grep -v 'HARNESS READY' | tail -1; }
run_role() {
  local role="$1" token="$2" log="$logs/harness-$1.log"
  timeout 240 "$exe" -batchmode -nographics -logFile "$log" -server 127.0.0.1 -port "$port" -token "$token" -netharness "$role" > /dev/null 2>&1
  local code=$?
  printf 'role=%s exit=%s  %s\n' "$role" "$code" "$(summary "$log")"
  if [ "$code" -ne 0 ]; then status=1; fi
}

if [ "$mode" = "denied" ]; then
  run_role fixture-denied "$tok_a"
else
  blog="$logs/harness-visibility-b.log"
  timeout 240 "$exe" -batchmode -nographics -logFile "$blog" -server 127.0.0.1 -port "$port" -token "$tok_b" -netharness visibility-b > /dev/null 2>&1 &
  b_pid=$!
  ready=0
  for _ in $(seq 1 60); do
    if grep -q "BLACKGLASS NET HARNESS READY role=visibility-b" "$blog" 2>/dev/null; then
      ready=1
      break
    fi
    kill -0 "$b_pid" 2>/dev/null || break
    sleep 1
  done
  if [ "$ready" -eq 1 ]; then
    run_role visibility-a "$tok_a"
  else
    echo "visibility-b never became ready; visibility-a was not run"
    kill "$b_pid" 2>/dev/null
    status=1
  fi
  wait "$b_pid"
  bcode=$?
  b_pid=""
  printf 'role=%s exit=%s  %s\n' visibility-b "$bcode" "$(summary "$blog")"
  if [ "$bcode" -ne 0 ]; then status=1; fi
  run_role a "$tok_a"
  run_role b "$tok_b"
  run_role ticket "$tok_t"
  run_role badtoken "not-a-real-token"
  run_role visibility-fault "$tok_a"
fi
echo "logs=$logs"
exit $status

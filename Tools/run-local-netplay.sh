#!/usr/bin/env bash
# The local two-process demonstration (decision 052): the local fixture server (-attemptFixture fixture 03, -net on
# 127.0.0.1:7770, WebSockets) and the play client, both on this PC. A fresh development token file is made under
# Builds/LocalNetPlay/ (git-ignored); the token is passed to the client on its command line and never printed.
# Needs Builds/LocalFixtureServer/BlackglassServer.exe (Tools/build-local-fixture-server.sh) and
# Builds/NetPlayClient/NetPlayClient.exe (Tools/build-netplay-client.sh). Closing the client window stops the server.
# Last lines: the server's ATTEMPT FROZEN line (if the mission ended) and where the logs are.
set -u
root="$(cd "$(dirname "$0")/.." && pwd)"
port="${NET_PORT:-7770}"
server="$root/Builds/LocalFixtureServer/BlackglassServer.exe"
client="$root/Builds/NetPlayClient/NetPlayClient.exe"
work="$root/Builds/LocalNetPlay"
[ -x "$server" ] || { echo "build the server first: Tools/build-local-fixture-server.sh"; exit 2; }
[ -x "$client" ] || { echo "build the client first: Tools/build-netplay-client.sh"; exit 2; }
mkdir -p "$work"
tokens="$work/dev-tokens.txt"
BLACKGLASS_TOKENS="$tokens" "$root/Tools/LinuxServer/make-dev-tokens.sh" --force >/dev/null || { echo "could not make the token file"; exit 2; }
token="$(awk '$1=="account-a"{print $2}' "$tokens")"
[ -n "$token" ] || { echo "the token file has no account-a"; exit 2; }
fixture="$(cd "$root" && pwd -W)/Backend/contracts/attempt-v1/03-seeded-collect-and-use.json"
log="$work/server.log"
rm -f "$log"

"$server" -batchmode -nographics -logFile "$(cygpath -w "$log")" -attemptFixture "$fixture" \
  -net -netPort "$port" -netTokens "$(cygpath -w "$tokens")" -heartbeat 10 &
server_pid=$!
cleanup() { kill "$server_pid" 2>/dev/null; wait "$server_pid" 2>/dev/null; }
trap cleanup EXIT

for _ in $(seq 1 120); do
  grep -q "\[Net\] listening 127.0.0.1:$port" "$log" 2>/dev/null && break
  kill -0 "$server_pid" 2>/dev/null || { echo "the server stopped during startup; see $log"; exit 1; }
  sleep 1
done
grep -q "\[Net\] listening 127.0.0.1:$port" "$log" || { echo "the server did not listen within 120 s; see $log"; exit 1; }
grep -h "map projection" "$log"
echo "server listening; starting the play client (close its window to finish)"

"$client" -server 127.0.0.1 -port "$port" -token "$token" -screen-fullscreen 0 -screen-width 1600 -screen-height 900 \
  -logFile "$(cygpath -w "$work/client.log")"
grep -h "ATTEMPT FROZEN" "$log" || echo "no ATTEMPT FROZEN line: the mission did not end"
echo "logs: $log $work/client.log"

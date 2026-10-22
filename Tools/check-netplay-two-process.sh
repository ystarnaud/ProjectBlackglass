#!/usr/bin/env bash
# Owner ruling (decision 052): proves the largest supported map crosses the real two-process WebSocket transport. Runs the one
# PlayMode test NetPlayTwoProcessPlayTests, whose server runs in the test runner while the built play client runs as a separate
# process on 127.0.0.1. Local only. The Unity Editor must be closed; build the client first (Tools/build-netplay-client.sh).
# Usage: Tools/check-netplay-two-process.sh
set -u
root="$(cd "$(dirname "$0")/.." && pwd)"
client="$root/Builds/NetPlayClient/NetPlayClient.exe"
[ -f "$client" ] || { echo "build the client first: Tools/build-netplay-client.sh"; echo "EXIT=2"; exit 2; }
export BLACKGLASS_NETPLAY_CLIENT_EXE="$(cygpath -w "$client" 2>/dev/null || echo "$client")"
"$root/Tools/run-tests.sh" PlayMode "Blackglass.Tests.NetPlayTwoProcessPlayTests"

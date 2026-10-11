#!/usr/bin/env bash
# Runs the server's bounded navigation and combat diagnostic on this machine (Checkpoint A).
# Usage: run-diagnostic.sh [timeoutSeconds]   (default 180; `timeout` ends a hung run with exit 124)
# Log: $BLACKGLASS_LOGS (default /opt/blackglass/logs)/<UTC>-<rev>-diagnostic.log. Exits with the server's exit code.
set -u
dir="$(cd "$(dirname "$0")" && pwd)"
logs="${BLACKGLASS_LOGS:-/opt/blackglass/logs}"
limit="${1:-180}"
rev=$(awk -F= '$1=="revision"{print substr($2,1,7)}' "$dir/build-info.txt" 2>/dev/null)
stamp=$(date -u +%Y%m%dT%H%M%SZ)
mkdir -p "$logs"
log="$logs/$stamp-${rev:-unknown}-diagnostic.log"
timeout --kill-after=10 "$limit" "$dir/BlackglassServer.x86_64" -batchmode -nographics -logFile - -diagnostic > "$log" 2>&1
code=$?
grep -E "\[Server\]|\[Diagnostic\]|BLACKGLASS" "$log"
echo "log=$log"
echo "exit=$code"
exit $code

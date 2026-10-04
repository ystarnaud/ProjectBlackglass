#!/usr/bin/env bash
# Runs this project's Unity tests in batch mode. The Unity Editor must be closed.
# Usage: Tools/run-tests.sh EditMode|PlayMode [test filter]
set -u
platform="${1:?usage: Tools/run-tests.sh EditMode|PlayMode [test filter]}"
filter="${2:-}"
root="$(cd "$(dirname "$0")/.." && (pwd -W 2>/dev/null || pwd))"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe}"
results="$root/Logs/TestResults-$platform.xml"
log="$root/Logs/TestRun-$platform.log"
mkdir -p "$root/Logs"
rm -f "$results"

args=(-batchmode -projectPath "$root" -runTests -testPlatform "$platform"
      -assemblyNames "Blackglass.Tests.$platform"
      -testResults "$results" -logFile "$log")
if [ -n "$filter" ]; then args+=(-testFilter "$filter"); fi

"$unity" "${args[@]}"
code=$?

if grep -q "another Unity instance is running" "$log" 2>/dev/null; then
  echo "The Unity Editor has this project open. Close it and run again."
fi
grep -h "error CS" "$log" 2>/dev/null | sort -u
if [ -f "$results" ]; then
  grep -o '<test-run [^>]*>' "$results" | grep -oE '(result|total|passed|failed|skipped)="[^"]*"' | tr '\n' ' '
  echo
  grep -oE '<test-case [^>]*result="Failed"[^>]*>' "$results" | grep -oE 'fullname="[^"]*"'
  grep -A6 '<failure>' "$results" | grep -vE '^\s*$' | head -60
else
  echo "No results file was written. See $log"
fi
echo "EXIT=$code"
exit $code

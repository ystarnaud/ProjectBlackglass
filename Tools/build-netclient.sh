#!/usr/bin/env bash
# Builds the Windows net-test client player (networking experiment). The Unity Editor must be closed.
# Usage: Tools/build-netclient.sh      Output: Builds/NetTestClient/NetTestClient.exe
set -u
root_unix="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$root_unix" && (pwd -W 2>/dev/null || pwd))"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe}"
log="$root/Logs/NetClientBuild.log"
mkdir -p "$root_unix/Logs" "$root_unix/Builds"
rm -rf "$root_unix/Builds/NetTestClient"

"$unity" -batchmode -quit -projectPath "$root" \
  -executeMethod Blackglass.EditorTools.NetClientBuild.BuildFromCommandLine -logFile "$log"
code=$?

if grep -q "another Unity instance is running" "$log" 2>/dev/null; then
  echo "The Unity Editor has this project open. Close it and run again."
fi
grep -h "error CS" "$log" 2>/dev/null | sort -u
grep -h "\[NetClientBuild\]" "$log" 2>/dev/null
if [ "$code" -ne 0 ] || [ ! -f "$root_unix/Builds/NetTestClient/NetTestClient.exe" ]; then
  echo "BUILD FAILED (Unity exit $code). See $log"
  echo "EXIT=1"
  exit 1
fi
echo "client=$root_unix/Builds/NetTestClient/NetTestClient.exe"
echo "EXIT=0"

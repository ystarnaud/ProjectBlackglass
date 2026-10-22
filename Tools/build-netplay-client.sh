#!/usr/bin/env bash
# Builds the Windows play client (decision 052): local, development-only, never deployed. The Unity Editor must be closed.
# Usage: Tools/build-netplay-client.sh      Output: Builds/NetPlayClient/NetPlayClient.exe
set -u
root_unix="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$root_unix" && (pwd -W 2>/dev/null || pwd))"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe}"
log="$root/Logs/NetPlayClientBuild.log"
mkdir -p "$root_unix/Logs" "$root_unix/Builds"
rm -rf "$root_unix/Builds/NetPlayClient"
echo "source: revision=$(git -C "$root_unix" rev-parse HEAD)"

"$unity" -batchmode -quit -projectPath "$root" \
  -executeMethod Blackglass.EditorTools.NetPlayClientBuild.BuildFromCommandLine -logFile "$log"
code=$?

if grep -q "another Unity instance is running" "$log" 2>/dev/null; then
  echo "The Unity Editor has this project open. Close it and run again."
fi
grep -h "error CS" "$log" 2>/dev/null | sort -u
grep -h "\[NetPlayClientBuild\]" "$log" 2>/dev/null
if [ "$code" -ne 0 ] || [ ! -f "$root_unix/Builds/NetPlayClient/NetPlayClient.exe" ]; then
  echo "BUILD FAILED (Unity exit $code). See $log"
  echo "EXIT=1"
  exit 1
fi
echo "client=$root_unix/Builds/NetPlayClient/NetPlayClient.exe"
echo "EXIT=0"

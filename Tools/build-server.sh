#!/usr/bin/env bash
# Builds the Linux Dedicated Server (Mono) from ServerMission.unity and packages it for a VM (Checkpoint A).
# The Unity Editor must be closed.
# Usage: Tools/build-server.sh [--fixture]   (--fixture: the release build plus the BLACKGLASS_NET_FIXTURE define, with the -netFixture test fixture)
# Output: Builds/LinuxServer/ (the build) and Builds/blackglass-server-linux-<sha>[-dirty].tar.gz (the package).
set -u
method=Blackglass.EditorTools.ServerBuild.BuildFromCommandLine
variant=release
if [ "${1:-}" = "--fixture" ]; then
  method=Blackglass.EditorTools.ServerBuild.BuildFixtureFromCommandLine
  variant=fixture
fi
root_unix="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$root_unix" && (pwd -W 2>/dev/null || pwd))"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe}"
out="$root_unix/Builds/LinuxServer"
log="$root/Logs/ServerBuild.log"
mkdir -p "$root_unix/Logs" "$root_unix/Builds"
# A refused or failed run must never leave an old build behind to be packaged.
rm -rf "$out"

"$unity" -batchmode -quit -projectPath "$root" \
  -executeMethod "$method" -logFile "$log"
code=$?

if grep -q "another Unity instance is running" "$log" 2>/dev/null; then
  echo "The Unity Editor has this project open. Close it and run again."
fi
grep -h "error CS" "$log" 2>/dev/null | sort -u
grep -h "\[ServerBuild\]" "$log" 2>/dev/null
if [ "$code" -ne 0 ] || [ ! -f "$out/BlackglassServer.x86_64" ] || [ ! -f "$out/build-info.txt" ]; then
  echo "BUILD FAILED (Unity exit $code); nothing was packaged. See $log"
  echo "EXIT=1"
  exit 1
fi

# The VM scripts travel with the build; strip any CR so bash on Linux can run them.
for script in "$root_unix"/Tools/LinuxServer/*.sh; do
  tr -d '\r' < "$script" > "$out/$(basename "$script")"
done

rev=$(awk -F= '$1=="revision"{print substr($2,1,7)}' "$out/build-info.txt")
dirty=$(awk -F= '$1=="dirty"{print $2}' "$out/build-info.txt")
suffix=""
if [ "$dirty" = "true" ]; then suffix="-dirty"; fi
if [ "$variant" = "fixture" ]; then suffix="${suffix}-fixture"; fi
archive="$root_unix/Builds/blackglass-server-linux-${rev}${suffix}.tar.gz"
rm -f "$archive"
# Unity's Burst debug information is for local debugging only (its folder is named *_DoNotShip).
tar -C "$root_unix/Builds" --exclude='LinuxServer/*_DoNotShip' -czf "$archive" LinuxServer
echo "build-info:"
sed 's/^/  /' "$out/build-info.txt"
echo "archive=$archive"
echo "sizeMB=$(du -m "$archive" | cut -f1)"
sha256sum "$archive"
echo "EXIT=0"

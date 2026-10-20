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

# Source provenance (Stage 12 rulings §1): Unity rewrites settings it switches and restores, so the commit, source tree and
# cleanliness are captured here, before Unity starts. Unity only stamps them (ServerBuild reads these variables); after it exits
# the source must be exactly as captured, or nothing is packaged.
provenance="$root_unix/Builds/source-provenance.txt"
if ! bash "$root_unix/Tools/build-provenance.sh" capture "$root_unix" "$provenance"; then
  echo "BUILD FAILED: the source state could not be captured; nothing was built"
  echo "EXIT=1"
  exit 1
fi
export BLACKGLASS_SOURCE_REVISION="$(sed -n 's/^revision=//p' "$provenance")"
export BLACKGLASS_SOURCE_TREE="$(sed -n 's/^sourceTree=//p' "$provenance")"
export BLACKGLASS_SOURCE_DIRTY="$(sed -n 's/^dirty=//p' "$provenance")"
echo "source: revision=$BLACKGLASS_SOURCE_REVISION tree=$BLACKGLASS_SOURCE_TREE dirty=$BLACKGLASS_SOURCE_DIRTY"

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

# The source after the build must be exactly as captured: a change Unity left behind (or a moved HEAD) fails the build.
if ! verdict="$(bash "$root_unix/Tools/build-provenance.sh" verify "$root_unix" "$provenance")"; then
  rm -rf "$out"
  echo "BUILD FAILED: $verdict"
  echo "nothing was packaged; the source must be restored by hand (git status shows the changes)"
  echo "EXIT=1"
  exit 1
fi
# The embedded stamp must be the captured one; then the binary's hash and the verification are recorded beside it.
for key in revision sourceTree dirty; do
  want="$(sed -n "s/^$key=//p" "$provenance")"
  got="$(awk -F= -v k="$key" '$1==k{print substr($0, length(k) + 2)}' "$out/build-info.txt")"
  if [ "$got" != "$want" ]; then
    rm -rf "$out"
    echo "BUILD FAILED: build-info.txt has $key=$got, the capture $key=$want; nothing was packaged"
    echo "EXIT=1"
    exit 1
  fi
done
{
  echo "binarySha256=$(sha256sum "$out/BlackglassServer.x86_64" | cut -d' ' -f1)"
  echo "provenance=verified"
} >> "$out/build-info.txt"
cp "$provenance" "$out/source-provenance.txt"

# The VM scripts travel with the build; strip any CR so bash on Linux can run them.
for script in "$root_unix"/Tools/LinuxServer/*.sh; do
  tr -d '\r' < "$script" > "$out/$(basename "$script")"
done

# Every shipped file's SHA-256: the executable is Unity's generic player (the same across builds); the game code is in
# *_Data/Managed. The list travels in the package (sha256sum -c on the VM) and its own hash is printed for the host manifest.
# Two-space separators (Git for Windows marks binary mode with '*'), sorted bytewise, the unshipped *_DoNotShip excluded.
(cd "$out" && find . -type f ! -path './*_DoNotShip/*' ! -name files.sha256 -print0 | LC_ALL=C sort -z | xargs -0 sha256sum) \
  | sed 's/ \*\./  ./' > "$root_unix/Builds/files.sha256.tmp" && mv "$root_unix/Builds/files.sha256.tmp" "$out/files.sha256"
echo "files.sha256 sha256=$(sha256sum "$out/files.sha256" | cut -d' ' -f1)"

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

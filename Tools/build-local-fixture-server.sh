#!/usr/bin/env bash
# Builds the loopback fixture checkpoint's local server: ServerMission as a Windows 64 player with the BLACKGLASS_NET_FIXTURE
# define (LocalFixtureServerBuild), to run -attemptFixture -net beside the net-test client on this PC. Local only: never deployed.
# The Unity Editor must be closed. Usage: Tools/build-local-fixture-server.sh
# Output: Builds/LocalFixtureServer/BlackglassServer.exe (run with -batchmode -nographics; see the checkpoint plan's procedure).
set -u
root_unix="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$root_unix" && (pwd -W 2>/dev/null || pwd))"
unity="${UNITY_EXE:-/c/Program Files/Unity/Hub/Editor/6000.3.25f1/Editor/Unity.exe}"
out="$root_unix/Builds/LocalFixtureServer"
log="$root/Logs/LocalFixtureServerBuild.log"
mkdir -p "$root_unix/Logs" "$root_unix/Builds"
rm -rf "$out"

# The source revision, read before Unity starts and stamped into build-info.txt. Local only, so a plain git stamp (dirty when
# anything differs from the commit) rather than Tools/build-provenance.sh's verified capture for deployable builds.
export BLACKGLASS_SOURCE_REVISION="$(git -C "$root_unix" rev-parse HEAD)"
export BLACKGLASS_SOURCE_TREE="$(git -C "$root_unix" rev-parse 'HEAD^{tree}')"
if [ -n "$(git -C "$root_unix" status --porcelain=v1 --untracked-files=no)" ]; then
  export BLACKGLASS_SOURCE_DIRTY=true
else
  export BLACKGLASS_SOURCE_DIRTY=false
fi
echo "source: revision=$BLACKGLASS_SOURCE_REVISION dirty=$BLACKGLASS_SOURCE_DIRTY"

"$unity" -batchmode -quit -projectPath "$root" \
  -executeMethod Blackglass.EditorTools.LocalFixtureServerBuild.BuildFromCommandLine -logFile "$log"
code=$?

if grep -q "another Unity instance is running" "$log" 2>/dev/null; then
  echo "The Unity Editor has this project open. Close it and run again."
fi
grep -h "error CS" "$log" 2>/dev/null | sort -u
grep -h "\[LocalFixtureServerBuild\]" "$log" 2>/dev/null
if [ "$code" -ne 0 ] || [ ! -f "$out/BlackglassServer.exe" ] || [ ! -f "$out/build-info.txt" ]; then
  echo "BUILD FAILED (Unity exit $code). See $log"
  echo "EXIT=1"
  exit 1
fi
echo "server=$out/BlackglassServer.exe"
echo "EXIT=0"

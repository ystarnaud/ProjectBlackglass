#!/usr/bin/env bash
# Runs inside the disposable test container started by vm-test.sh (never by hand on a workstation).
#   run-tests-in-container.sh full|filter|agent [<xunit filter>]
#   run-tests-in-container.sh build          sync and build only (the crash drill builds here, with the package feed reachable)
#   run-tests-in-container.sh unity-core-build   Stage 11: sync Backend/ and Unity's backend core, restore and build only
#                                            tests/UnityCore.Linux.Tests (no database secrets are mounted)
#   run-tests-in-container.sh unity-core-test    run it from that build (--network none: no restore, no build)
#   run-tests-in-container.sh drill          the crash drill's driver only, from the cached build (no restore, no build: it runs on
#                                            the drill's internal network, which has no route out)
# /src is the read-only source snapshot, /work the cached build tree, /out the results. Test-cluster passwords are read
# by the tests from /secrets; this script never reads, copies or prints them.
set -euo pipefail
mode="$1" filter="${2:-}"
src=/src/Backend work=/work/src/Backend out=/out
mkdir -p "$work" "$HOME"
t0=$(date +%s)

# Bring the cached tree to exactly the snapshot. Only files whose content differs are copied, with a fresh timestamp, so
# MSBuild rebuilds what changed; files the snapshot no longer has are deleted (bin/ and obj/ excepted). A cached build
# therefore never tests an old revision.
changed=0 removed=0
sync_tree() { # sync_tree <snapshot dir> <cached dir>
  local from="$1" to="$2"
  mkdir -p "$to"
  while IFS= read -r -d '' f; do
    if ! cmp -s "$from/$f" "$to/$f"; then mkdir -p "$to/$(dirname "$f")"; cp "$from/$f" "$to/$f"; changed=$((changed + 1)); fi
  done < <(cd "$from" && find . -type f -print0)
  while IFS= read -r -d '' f; do
    if [[ ! -e "$from/$f" ]]; then rm -f "$to/$f"; removed=$((removed + 1)); fi
  done < <(cd "$to" && find . \( -name bin -o -name obj \) -prune -o -type f -print0)
}
if [[ "$mode" == unity-core-test ]]; then
  echo "$(cat "$src/TREE"); testing the cached unity-core build"
else
  sync_tree "$src" "$work"
  # Stage 11: Unity's backend core, at its repo-relative path so the test project's links resolve.
  core=Assets/_Project/Scripts/Backend/Core
  if [[ "$mode" == unity-core-build ]]; then sync_tree "/src/$core" "/work/src/$core"; fi
  echo "$(cat "$src/TREE"); files changed=$changed removed=$removed"
fi

cd "$work"
if [[ "$mode" == unity-core-build ]]; then
  code=0
  dotnet build tests/UnityCore.Linux.Tests -nologo -v q -clp:ErrorsOnly || code=$?
  echo "unity-core build $(( $(date +%s) - t0 )) s, exit $code"
  exit "$code"
fi
if [[ "$mode" == unity-core-test ]]; then
  rm -rf "$BLACKGLASS_TEST_TMP" && mkdir -p "$BLACKGLASS_TEST_TMP"
  code=0
  dotnet test tests/UnityCore.Linux.Tests --no-build --no-restore -nologo --logger "trx;LogFileName=unity-core.trx" --results-directory "$out" || code=$?
  rm -rf "$BLACKGLASS_TEST_TMP"
  echo "unity-core tests $(( $(date +%s) - t0 )) s, exit $code"
  exit "$code"
fi
if [[ "$mode" == drill ]]; then
  code=0
  dotnet test tests/Accounts.Integration.Tests --no-build --no-restore -nologo --filter "FullyQualifiedName~CrashDrillTests"     --logger "trx;LogFileName=drill.trx" --results-directory "$out" || code=$?
  echo "drill tests $(( $(date +%s) - t0 )) s, exit $code"
  exit "$code"
fi
dotnet build Backend.sln -nologo -v q -clp:ErrorsOnly
t1=$(date +%s); echo "build $((t1 - t0)) s"
[[ "$mode" == build ]] && exit 0
code=0
if [[ "$mode" == full ]]; then
  dotnet test tests/Accounts.Domain.Tests --no-build -nologo --logger "trx;LogFileName=domain.trx" --results-directory "$out" || code=$?
  dotnet test tests/MissionHost.Agent.Tests --no-build -nologo --logger "trx;LogFileName=agent.trx" --results-directory "$out" || code=$?
  dotnet test tests/Accounts.Integration.Tests --no-build -nologo --logger "trx;LogFileName=integration.trx" --results-directory "$out" || code=$?
elif [[ "$mode" == agent ]]; then
  dotnet test tests/MissionHost.Agent.Tests --no-build -nologo ${filter:+--filter "$filter"} --logger "trx;LogFileName=agent.trx" --results-directory "$out" || code=$?
else
  dotnet test tests/Accounts.Integration.Tests --no-build -nologo --filter "$filter" --logger "trx;LogFileName=integration.trx" --results-directory "$out" || code=$?
fi
t2=$(date +%s); echo "tests $((t2 - t1)) s, total $((t2 - t0)) s, exit $code"
exit "$code"

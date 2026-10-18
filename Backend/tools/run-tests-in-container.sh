#!/usr/bin/env bash
# Runs inside the disposable test container started by vm-test.sh (never by hand on a workstation).
#   run-tests-in-container.sh full|filter|agent [<xunit filter>]
#   run-tests-in-container.sh build          sync and build only (the crash drill builds here, with the package feed reachable)
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
changed=0
while IFS= read -r -d '' f; do
  if ! cmp -s "$src/$f" "$work/$f"; then mkdir -p "$work/$(dirname "$f")"; cp "$src/$f" "$work/$f"; changed=$((changed + 1)); fi
done < <(cd "$src" && find . -type f -print0)
removed=0
while IFS= read -r -d '' f; do
  if [[ ! -e "$src/$f" ]]; then rm -f "$work/$f"; removed=$((removed + 1)); fi
done < <(cd "$work" && find . \( -name bin -o -name obj \) -prune -o -type f -print0)
echo "$(cat "$src/TREE"); files changed=$changed removed=$removed"

cd "$work"
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

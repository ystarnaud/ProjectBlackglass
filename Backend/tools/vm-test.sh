#!/usr/bin/env bash
# Runs the backend tests on bg-dev-services01 in a disposable .NET 10 SDK container next to the integration-test cluster
# (owner ruling B13). The container shares only the test cluster container's network namespace: no published port, no
# host network, no Docker socket, no daemon or firewall change.
#   vm-test.sh --filter <xunit filter>   Integration tests matching the filter (e.g. "FullyQualifiedName~ClaimTests")
#   vm-test.sh --full                    Domain, then every integration test
#   --evidence <dir>                     where the trx files and summary land (default .claude/scratchpad/vm-test/<tree>)
# The working tree's Backend/ (tracked plus untracked, .gitignore respected) is snapshotted into a git tree id, so
# uncommitted test-first work runs too and every summary names exactly the tree it tested.
# Exit codes: the tests' own, 64 usage, 75 another run holds the lock, 78 not the blackglass-test cluster,
# 79 a bg_t_* database was left behind.
set -euo pipefail
alias="${BLACKGLASS_SERVICES_HOST:-bg-dev-services01-admin}"
image="mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa"
mode="" filter="" evidence=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --filter) mode=filter; filter="${2:?--filter needs an expression}"; shift 2 ;;
    --full) mode=full; shift ;;
    --evidence) evidence="${2:?--evidence needs a directory}"; shift 2 ;;
    *) echo "usage: vm-test.sh --filter <expr> | --full [--evidence <dir>]" >&2; exit 64 ;;
  esac
done
[[ -n "$mode" ]] || { echo "usage: vm-test.sh --filter <expr> | --full [--evidence <dir>]" >&2; exit 64; }
[[ "$filter" != *"'"* ]] || { echo "the filter must not contain a single quote" >&2; exit 64; }

repo="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
index="$(mktemp)"; trap 'rm -f "$index"' EXIT
cp "$(git -C "$repo" rev-parse --path-format=absolute --git-path index)" "$index"
GIT_INDEX_FILE="$index" git -C "$repo" add -A -- Backend
tree="$(GIT_INDEX_FILE="$index" git -C "$repo" write-tree)"
tree="$(git -C "$repo" rev-parse "$tree:Backend")"   # the Backend/ subtree: identical content gives the identical id
head="$(git -C "$repo" rev-parse --short HEAD)"
evidence="${evidence:-$repo/.claude/scratchpad/vm-test/${tree:0:12}-$(date +%H%M%S)}"
mkdir -p "$evidence"

vm=/opt/blackglass/backend
# The sync, the run and the copy-back each hold the VM-side lock, so a second invocation never replaces a running test's
# sources or results (it waits for the lock instead).
git -C "$repo" -c core.autocrlf=false -c core.eol=lf archive --format=tar --prefix=Backend/ "$tree" | ssh "$alias" "sudo -n flock -w 3600 /run/lock/bg-test-runner.lock sh -c 'set -e
  rm -rf $vm/test-src.new && mkdir -p $vm/test-src.new && chmod 755 $vm/test-src.new
  tar -x -C $vm/test-src.new
  echo \"tree=$tree head=$head\" > $vm/test-src.new/Backend/TREE
  rm -rf $vm/test-src && mv $vm/test-src.new $vm/test-src'"

set +e
ssh "$alias" "sudo -n bash -s -- '$mode' '$filter' '$image'" <<'VM' | tee "$evidence/transcript.txt"
set -euo pipefail
mode="$1" filter="$2" image="$3"
vm=/opt/blackglass/backend pg=blackglass-testdb-postgres-1 secrets=/opt/blackglass/backend/testdb-secrets/postgres
exec 9>/run/lock/bg-test-runner.lock
flock -w 3600 9 || { echo "another test run held the lock for an hour" >&2; exit 75; }
psql() { docker exec "$pg" psql -U postgres -d postgres -Atc "$1"; }
cluster="$(psql "select current_setting('cluster_name')")"
[[ "$cluster" == blackglass-test ]] || { echo "refusing: cluster_name is '$cluster', not 'blackglass-test'" >&2; exit 78; }
# The tests connect to the cluster container's own address, never its loopback: the image's pg_hba trusts 127.0.0.1, which
# would skip password authentication; the container address meets the scram-sha-256 rule, as the tunnel path does.
pgip="$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$pg")"
[[ "$pgip" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "refusing: no IPv4 address for $pg" >&2; exit 78; }
docker rm -f bg-test-runner >/dev/null 2>&1 || true   # an orphan from a dropped session; we hold the lock
docker volume create bg-test-nuget >/dev/null && docker volume create bg-test-work >/dev/null
docker run --rm --network none --cap-drop ALL --cap-add CHOWN -v bg-test-nuget:/n -v bg-test-work:/w "$image" chown 999:999 /n /w
rm -rf "$vm/test-out" && install -d -o 999 -g 999 -m 755 "$vm/test-out"
cat "$vm/test-src/Backend/TREE"
start=$(date +%s)
set +e
docker run --rm --name bg-test-runner \
  --user 999:999 --cpus 2 --memory 3g --memory-swap 3g --pids-limit 1024 \
  --cap-drop ALL --security-opt no-new-privileges \
  --network "container:$pg" \
  -e BLACKGLASS_TEST_PG_HOST="$pgip" -e BLACKGLASS_TEST_PG_PORT=5432 -e BLACKGLASS_TEST_PG_SECRETS=/secrets \
  -e HOME=/work/home -e DOTNET_CLI_HOME=/work/home -e NUGET_PACKAGES=/nuget \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  -v "$vm/test-src:/src:ro" \
  -v "$secrets/superuser_password:/secrets/pg_superuser_password:ro" \
  -v "$secrets/migrator_password:/secrets/pg_migrator_password:ro" \
  -v "$secrets/app_password:/secrets/pg_app_password:ro" \
  -v bg-test-nuget:/nuget -v bg-test-work:/work -v "$vm/test-out:/out" \
  "$image" timeout 3600 bash /src/Backend/tools/run-tests-in-container.sh "$mode" "$filter"
code=$?
set -e
echo "container exit $code after $(( $(date +%s) - start )) s"
left="$(psql "select count(*) from pg_database where datname like 'bg\_t\_%'")"
if [[ "$left" != 0 ]]; then echo "FAIL: $left bg_t_* database(s) left behind" >&2; exit 79; fi
if docker ps -a --format '{{.Names}}' | grep -qx bg-test-runner; then echo "FAIL: the runner container still exists" >&2; exit 79; fi
echo "cleanup verified: no bg_t_* database, no runner container"
exit $code
VM
code=${PIPESTATUS[0]}
set -e
ssh "$alias" "sudo -n flock -w 3600 /run/lock/bg-test-runner.lock tar -c -C $vm/test-out ." | tar -x -C "$evidence"
echo "evidence: $evidence (exit $code)"
exit "$code"

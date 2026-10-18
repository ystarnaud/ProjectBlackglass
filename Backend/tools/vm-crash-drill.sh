#!/usr/bin/env bash
# The Stage 9 crash drill (S9-B17; plan revision 3): settlements and exit-evidence voids against a throwaway PostgreSQL that is
# killed mid-load and restarted, then the invariants and a resubmission pass. It never touches the shared test cluster beyond
# reading its container id and start time (to prove it was left alone).
#   vm-crash-drill.sh [--evidence <dir>] [--negative-skip-kill]
# On bg-dev-services01 it:
#   1. refuses unless no container named blackglass-crashdb exists, records the shared test container's id and start time and
#      the host's listening sockets;
#   2. creates a temporary internal Docker network (no route out, nothing published), a fresh volume and throwaway passwords
#      (a tmpfs directory, never printed), and starts blackglass-crashdb on that network (postgres:17, cluster_name
#      blackglass-crashdrill, the same init script as the test cluster), with no published port;
#   3. builds the backend in the usual runner (the package feed reachable), then runs the drill driver (CrashDrillTests) in a
#      runner attached to the internal network only, reaching the database by name, so restarting the database never strands
#      the runner in a dead network namespace;
#   4. when the driver signals its load, kills blackglass-crashdb (docker kill), verifies that exactly that container (by id,
#      never the shared cluster's) stopped with SIGKILL's status, starts it again and verifies it runs with a new start time;
#   5. waits for the driver, which itself proves the restart (PostgreSQL's postmaster start time changed) before its invariant
#      and resubmission checks, and keeps its transcript and results;
#   6. removes the runners, the database container, its volume, the network and the passwords (also after a failed command,
#      SIGINT, SIGTERM or SIGHUP), and verifies they are gone, that the shared test container's id and start time are unchanged,
#      and that the host's listening sockets are as before.
# The drill passes only when the kill and the restart were verified, the driver passed and its result says it saw the restart.
# --negative-skip-kill is the negative test: everything runs except the kill, and the drill must fail (exit 80).
# No host-network exposure, no published port and no Docker daemon configuration change. Exit: the driver's, 64 usage,
# 75 another run holds the lock, 78 a refusal, 79 cleanup failed, 80 the kill or restart was not verified (the drill proved nothing).
set -euo pipefail
alias="${BLACKGLASS_SERVICES_HOST:-bg-dev-services01-admin}"
image="mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa"
evidence=""
skip_kill=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --evidence) evidence="${2:?--evidence needs a directory}"; shift 2 ;;
    --negative-skip-kill) skip_kill=1; shift ;;
    *) echo "usage: vm-crash-drill.sh [--evidence <dir>] [--negative-skip-kill]" >&2; exit 64 ;;
  esac
done
repo="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
index="$(mktemp)"; trap 'rm -f "$index"' EXIT
cp "$(git -C "$repo" rev-parse --path-format=absolute --git-path index)" "$index"
GIT_INDEX_FILE="$index" git -C "$repo" add -A -- Backend
tree="$(GIT_INDEX_FILE="$index" git -C "$repo" write-tree)"
tree="$(git -C "$repo" rev-parse "$tree:Backend")"
head="$(git -C "$repo" rev-parse --short HEAD)"
evidence="${evidence:-$repo/.claude/scratchpad/phase13/stage-09/crash-drill-${tree:0:12}-$(date +%H%M%S)}"
mkdir -p "$evidence"
vm=/opt/blackglass/backend
git -C "$repo" -c core.autocrlf=false -c core.eol=lf archive --format=tar --prefix=Backend/ "$tree" | ssh "$alias" "sudo -n flock -w 3600 /run/lock/bg-test-runner.lock sh -c 'set -e
  rm -rf $vm/test-src.new && mkdir -p $vm/test-src.new && chmod 755 $vm/test-src.new
  tar -x -C $vm/test-src.new
  echo \"tree=$tree head=$head\" > $vm/test-src.new/Backend/TREE
  rm -rf $vm/test-src && mv $vm/test-src.new $vm/test-src'"

set +e
ssh "$alias" "sudo -n bash -s -- '$image' '$skip_kill'" <<'VM' | tee "$evidence/transcript.txt"
set -uo pipefail
image="$1" skip_kill="$2"
vm=/opt/blackglass/backend shared=blackglass-testdb-postgres-1 db=blackglass-crashdb runner=bg-crash-runner
exec 9>/run/lock/bg-test-runner.lock
flock -w 3600 9 || { echo "another run held the lock for an hour" >&2; exit 75; }
if docker ps -a --format '{{.Names}}' | grep -qx "$db"; then echo "refusing: a container named $db already exists" >&2; exit 78; fi
net="bg-crashdrill-$$" vol="bg-crashdb-$$" sec="$(mktemp -d /run/bg-crashdrill.XXXXXX)"
shared_before="$(docker inspect -f '{{.Id}} {{.State.StartedAt}}' "$shared")"
listen_before="$(ss -H -ltn | awk '{print $4}' | sort -u)"
cleanup() {
  docker rm -f "$runner" "$runner-build" "$db" >/dev/null 2>&1
  docker volume rm -f "$vol" >/dev/null 2>&1
  docker network rm "$net" >/dev/null 2>&1
  rm -rf "$sec"
}
# Cleanup on every way out: a normal end or a failed command (EXIT), and an interruption (each signal exits, which runs EXIT).
# Long commands run in the background and are waited for, so a signal is handled at once, not after the command ends.
trap cleanup EXIT
trap 'echo "interrupted (SIGINT)" >&2; exit 130' INT
trap 'echo "terminated (SIGTERM)" >&2; exit 143' TERM
trap 'echo "hung up (SIGHUP)" >&2; exit 129' HUP
for name in superuser migrator app; do head -c 24 /dev/urandom | base64 | tr -d '/+=' > "$sec/$name"; done
chmod 755 "$sec"; chmod 444 "$sec"/*
install -d -m 755 "$sec/client"
cp "$sec/superuser" "$sec/client/pg_superuser_password"; cp "$sec/migrator" "$sec/client/pg_migrator_password"; cp "$sec/app" "$sec/client/pg_app_password"
chmod 444 "$sec/client"/*
docker network create --internal "$net" >/dev/null
docker volume create "$vol" >/dev/null
docker run -d --name "$db" --network "$net" --network-alias "$db" -v "$vol:/var/lib/postgresql/data" \
  -v "$sec/superuser:/run/secrets/pg_superuser_password:ro" -v "$sec/migrator:/run/secrets/pg_init_migrator_password:ro" -v "$sec/app:/run/secrets/pg_init_app_password:ro" \
  -v "$vm/test-src/Backend/deploy/services/initdb:/docker-entrypoint-initdb.d:ro" -e POSTGRES_PASSWORD_FILE=/run/secrets/pg_superuser_password \
  postgres:17 postgres -c cluster_name=blackglass-crashdrill >/dev/null
published="$(docker port "$db" | wc -l)"
[[ "$published" == 0 ]] || { echo "refusing: $db publishes a port" >&2; exit 78; }
for _ in $(seq 1 60); do docker exec "$db" pg_isready -h 127.0.0.1 -U postgres -d accounts >/dev/null 2>&1 && break; sleep 1; done
echo "crash cluster: $(docker exec "$db" psql -U postgres -d postgres -Atc "select current_setting('cluster_name')")"

# Build (package feed reachable, as vm-test.sh's runner), then the driver on the internal network only.
pg="$shared"
docker volume create bg-test-nuget >/dev/null && docker volume create bg-test-work >/dev/null
rm -rf "$vm/test-out" && install -d -o 999 -g 999 -m 755 "$vm/test-out"
docker run --rm --name "$runner-build" --user 999:999 --cpus 2 --memory 3g --memory-swap 3g --cap-drop ALL --security-opt no-new-privileges \
  --network "container:$pg" -e HOME=/work/home -e DOTNET_CLI_HOME=/work/home -e NUGET_PACKAGES=/nuget -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -v "$vm/test-src:/src:ro" -v bg-test-nuget:/nuget -v bg-test-work:/work -v "$vm/test-out:/out" \
  "$image" timeout 1800 bash /src/Backend/tools/run-tests-in-container.sh build &
wait $! || { echo "build failed" >&2; exit 1; }
start=$(date +%s)
docker run -d --name "$runner" --user 999:999 --cpus 2 --memory 3g --memory-swap 3g --pids-limit 1024 --cap-drop ALL --security-opt no-new-privileges \
  --network "$net" -e BLACKGLASS_TEST_PG_HOST="$db" -e BLACKGLASS_TEST_PG_PORT=5432 -e BLACKGLASS_TEST_PG_SECRETS=/secrets -e BLACKGLASS_CRASH_DRILL=1 \
  -e HOME=/work/home -e DOTNET_CLI_HOME=/work/home -e NUGET_PACKAGES=/nuget -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -v "$vm/test-src:/src:ro" -v "$sec/client:/secrets:ro" -v bg-test-nuget:/nuget -v bg-test-work:/work -v "$vm/test-out:/out" \
  "$image" timeout 1800 bash /src/Backend/tools/run-tests-in-container.sh drill >/dev/null
for _ in $(seq 1 600); do [[ -f "$vm/test-out/drill-loaded" ]] && break; docker ps --format '{{.Names}}' | grep -qx "$runner" || break; sleep 0.5; done
killed=0 restarted=0
db_id="$(docker inspect -f '{{.Id}}' "$db")"
[[ "$db_id" != "$(docker inspect -f '{{.Id}}' "$shared")" ]] || { echo "refusing: the drill database is the shared cluster" >&2; exit 78; }
if [[ -f "$vm/test-out/drill-loaded" ]]; then
  sleep 0.8
  started_before="$(docker inspect -f '{{.State.StartedAt}}' "$db")"
  if [[ "$skip_kill" == 1 ]]; then
    echo "negative test: the kill is skipped on purpose; the drill must fail"
  else
    docker kill "$db" >/dev/null
    # Exactly the drill's container (by id) stopped, with SIGKILL's status.
    if [[ "$(docker inspect -f '{{.Id}} {{.State.Running}} {{.State.ExitCode}}' "$db")" == "$db_id false 137" ]]; then
      killed=1; echo "killed $db (verified: stopped, exit 137) mid-load at $(( $(date +%s) - start )) s"
    else
      echo "FAIL: $db did not stop as killed" >&2
    fi
    sleep 3
    docker start "$db" >/dev/null
    if [[ "$(docker inspect -f '{{.Id}} {{.State.Running}}' "$db")" == "$db_id true" && "$(docker inspect -f '{{.State.StartedAt}}' "$db")" != "$started_before" ]]; then
      restarted=1; echo "restarted $db (verified: running, new start time)"
    else
      echo "FAIL: $db did not restart" >&2
    fi
  fi
  for _ in $(seq 1 60); do docker exec "$db" pg_isready -h 127.0.0.1 -U postgres -d accounts >/dev/null 2>&1 && break; sleep 1; done
else
  echo "FAIL: the driver never signalled its load" >&2
fi
docker wait "$runner" > "$sec/driver-exit" &
wait $!
code="$(cat "$sec/driver-exit" 2>/dev/null || echo 1)"
docker logs "$runner" 2>&1 | tail -n 40
echo "driver exit $code after $(( $(date +%s) - start )) s"
cat "$vm/test-out/drill-result.txt" 2>/dev/null || true
cleanup
trap - EXIT
fail=0
docker ps -a --format '{{.Names}}' | grep -qE "^($db|$runner|$runner-build)$" && { echo "FAIL: a drill container remains"; fail=1; }
docker volume ls -q | grep -qx "$vol" && { echo "FAIL: the drill volume remains"; fail=1; }
docker network ls --format '{{.Name}}' | grep -qx "$net" && { echo "FAIL: the drill network remains"; fail=1; }
[[ -e "$sec" ]] && { echo "FAIL: the drill passwords remain"; fail=1; }
[[ "$(docker inspect -f '{{.Id}} {{.State.StartedAt}}' "$shared")" == "$shared_before" ]] || { echo "FAIL: the shared test cluster changed"; fail=1; }
[[ "$(ss -H -ltn | awk '{print $4}' | sort -u)" == "$listen_before" ]] || { echo "FAIL: the host's listening sockets changed"; fail=1; }
(( fail == 0 )) && echo "cleanup verified: no drill container, volume, network or password; shared test cluster untouched; listeners unchanged" || exit 79
# The drill proves something only if the kill and the restart happened and the driver saw the restart.
if [[ "$killed" != 1 || "$restarted" != 1 ]] || ! grep -q "restarted=true" "$vm/test-out/drill-result.txt" 2>/dev/null; then
  echo "FAIL: the crash was not verified (killed=$killed restarted=$restarted, driver exit $code): the drill proved nothing" >&2
  exit 80
fi
exit "$code"
VM
code=${PIPESTATUS[0]}
set -e
ssh "$alias" "sudo -n flock -w 3600 /run/lock/bg-test-runner.lock tar -c -C $vm/test-out ." | tar -x -C "$evidence"
echo "evidence: $evidence (exit $code)"
exit "$code"

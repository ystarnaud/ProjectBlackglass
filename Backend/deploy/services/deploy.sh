#!/usr/bin/env bash
# Deploys the accounts stack in the required order (spec §15.4): build, database up, backup, migrate, API up, health,
# exposure check. Stops at the first failure, so the API never starts on an unmigrated schema and no migration runs
# without a fresh backup.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
project="${BLACKGLASS_COMPOSE_PROJECT:-blackglass-accounts}"
dc() { docker compose -p "$project" -f "$here/compose.yaml" "$@"; }

echo "== build";    dc build api
echo "== database"; dc up -d --wait postgres
echo "== backup";   bash "$here/backup.sh"
echo "== migrate";  dc --profile tools run --rm migrate
echo "== api";      dc up -d api
echo "== health"
body=""
for _ in $(seq 1 30); do
  if body="$(curl -sf http://127.0.0.1:5080/health)"; then break; fi
  body=""; sleep 1
done
[[ -n "$body" ]] || { echo "health check failed; see: docker compose -p $project logs api" >&2; exit 1; }
echo "$body"
echo "== exposure"; bash "$here/check-exposure.sh"

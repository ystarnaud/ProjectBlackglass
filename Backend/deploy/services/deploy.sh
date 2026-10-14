#!/usr/bin/env bash
# Deploys the accounts stack in the required order (spec §15.4): build, database up, stop the running API, backup,
# migrate, API up, health, exposure check. Stops at the first failure, so the API never starts on an unmigrated schema
# and no migration runs without a fresh backup.
# Migration policy (Stage 1): no zero-downtime compatibility. The running API is stopped first (docker stop lets it
# finish in-flight requests), so nothing writes during the backup and an incompatible migration never meets the old
# API. If backup or migrate fails, the API stays stopped: restore from the backup or fix and deploy again.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
project="${BLACKGLASS_COMPOSE_PROJECT:-blackglass-accounts}"
dc() { docker compose -p "$project" -f "$here/compose.yaml" "$@"; }

echo "== build";    dc build api
echo "== database"; dc up -d --wait postgres
echo "== stop api"; dc stop --timeout 30 api
echo "== backup";   bash "$here/backup.sh"
echo "== migrate";  dc --profile tools run --rm migrate
echo "== api";      dc up -d api
echo "== health"
body=""
for _ in $(seq 1 30); do
  if body="$(curl -sf --max-time 5 http://127.0.0.1:5080/health)"; then break; fi
  body=""; sleep 1
done
[[ -n "$body" ]] || { echo "health check failed; see: docker compose -p $project logs api" >&2; exit 1; }
echo "$body"
echo "== exposure"; bash "$here/check-exposure.sh"

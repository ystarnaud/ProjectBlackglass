#!/usr/bin/env bash
# Restore drill (B19): restores a backup into a scratch database, compares exact row counts with the counts recorded at
# backup time, runs the invariant queries (each returns violating rows), then drops the scratch database.
#   restore-drill.sh <file.dump>          expects <file>.counts next to it
# Environment: BLACKGLASS_COMPOSE_PROJECT, BLACKGLASS_INVARIANTS_DIR (default ../../db/invariants), BLACKGLASS_DRILL_KEEP=1
# Exit 0 with "DRILL PASS: ..." or exit 1 with "DRILL FAIL: <reason>".
set -euo pipefail
shopt -s nullglob
here="$(cd "$(dirname "$0")" && pwd)"
project="${BLACKGLASS_COMPOSE_PROJECT:-blackglass-accounts}"
dump="${1:?usage: restore-drill.sh <file.dump>}"
counts="${dump%.dump}.counts"
invariants="${BLACKGLASS_INVARIANTS_DIR:-$here/../../db/invariants}"
dc() { docker compose -p "$project" -f "$here/compose.yaml" "$@"; }
psql_su() { dc exec -T postgres psql -X -q -v ON_ERROR_STOP=1 -A -t -U postgres "$@"; }
fail() { echo "DRILL FAIL: $*"; exit 1; }

[[ -f "$dump" && -f "$counts" ]] || fail "missing $dump or $counts"
scratch="drill_$(date -u +%Y%m%d%H%M%S)_$$"
cleanup() {
  if [[ "${BLACKGLASS_DRILL_KEEP:-0}" != 1 ]]; then
    psql_su -d postgres -c "drop database if exists \"$scratch\" with (force)" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

psql_su -d postgres -c "create database \"$scratch\" owner accounts_migrator" >/dev/null
dc exec -T postgres pg_restore -U postgres -d "$scratch" --exit-on-error --single-transaction < "$dump" \
  || fail "pg_restore failed"
restored="$(psql_su -d "$scratch" < "$here/sql/row-counts.sql")" || fail "row counts could not be read"
diff -u "$counts" <(printf '%s\n' "$restored") >&2 || fail "row counts differ from the backup"
files=("$invariants"/*.sql)
for f in "${files[@]}"; do
  out="$(psql_su -d "$scratch" < "$f")" || fail "invariant $(basename "$f") errored"
  [[ -z "$out" ]] || fail "invariant $(basename "$f") reported: $(head -c 200 <<<"$out")"
done
echo "DRILL PASS: $(wc -l < "$counts") count lines matched, ${#files[@]} invariant(s) held, scratch database $scratch"

#!/usr/bin/env bash
# Restore drill (B19): restores a backup into a scratch database, compares exact row counts with the counts recorded at
# backup time, runs the invariant queries (each returns violating rows; judged by row count) that apply at the restored
# schema version (NNNN_ files numbered above it are skipped), then drops the scratch database.
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
(( ${#files[@]} > 0 )) || fail "no invariant files in $invariants"
# The schema version the backup was taken at (a backup is taken before a deploy migrates, so it can be older than the
# invariants in the tree). Same rule as InvariantRunner.VersionPrefix: NNNN_<name>.sql holds from version NNNN onward.
if [[ "$(psql_su -d "$scratch" -c "select to_regclass('public.schema_migrations') is not null")" == t ]]; then
  version="$(psql_su -d "$scratch" -c "select coalesce(max(version), -1) from schema_migrations")" || fail "schema version could not be read"
else
  version=-1
fi
[[ "$version" =~ ^-?[0-9]+$ ]] || fail "unreadable schema version '$version'"
held=0
for f in "${files[@]}"; do
  name="$(basename "$f")"
  if [[ "$name" =~ ^([0-9]{4})_ ]] && (( 10#${BASH_REMATCH[1]} > version )); then continue; fi
  # Judge by the row count psql reports, not by the text: a NULL or empty violating row prints nothing visible.
  out="$({ cat "$f"; printf '\n;\n%s\n' '\echo BG_ROWS :ROW_COUNT'; } | psql_su -d "$scratch")" || fail "invariant $name errored"
  rows="$(grep -E '^BG_ROWS [0-9]+$' <<<"$out" | tail -n 1 | cut -d' ' -f2 || true)"
  [[ -n "$rows" ]] || fail "invariant $name did not report a row count"
  (( rows == 0 )) || fail "invariant $name reported: $rows row(s): $(grep -vE '^BG_ROWS ' <<<"$out" | head -c 200)"
  held=$((held + 1))
done
echo "DRILL PASS: $(wc -l < "$counts") count lines matched, $held invariant(s) held at schema version $version, scratch database $scratch"

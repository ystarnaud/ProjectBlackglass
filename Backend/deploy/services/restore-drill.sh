#!/usr/bin/env bash
# Restore drill (B19): restores a backup into a scratch database, compares exact row counts with the counts recorded at
# backup time, runs the invariant queries (each returns violating rows; judged by row count) that apply at the restored
# schema version (NNNN_ files numbered above it are skipped), then drops the scratch database.
#   restore-drill.sh <file.dump>          expects <file>.counts next to it
# Environment: BLACKGLASS_COMPOSE_PROJECT, BLACKGLASS_INVARIANTS_DIR (default ../../db/invariants), BLACKGLASS_DRILL_KEEP=1
# Exit 0 with "DRILL PASS: ..." or exit 1 with "DRILL FAIL: <reason>". It never passes when a step or an invariant query
# fails, returns something unreadable, or is skipped without the restored schema version explaining why; any other
# unexpected error also ends as "DRILL FAIL" with exit 1.
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
  local rc=$?
  if [[ "${BLACKGLASS_DRILL_KEEP:-0}" != 1 ]]; then
    psql_su -d postgres -c "drop database if exists \"$scratch\" with (force)" >/dev/null 2>&1 || true
  fi
  # A command that failed without going through fail() (set -e) still ends as a drill failure with exit 1.
  if (( rc != 0 && rc != 1 )); then echo "DRILL FAIL: unexpected error (exit status $rc)"; exit 1; fi
}
trap cleanup EXIT

psql_su -d postgres -c "create database \"$scratch\" owner accounts_migrator" >/dev/null \
  || fail "scratch database could not be created"
dc exec -T postgres pg_restore -U postgres -d "$scratch" --exit-on-error --single-transaction < "$dump" \
  || fail "pg_restore failed"
restored="$(psql_su -d "$scratch" < "$here/sql/row-counts.sql")" || fail "row counts could not be read"
diff -u "$counts" <(printf '%s\n' "$restored") >&2 || fail "row counts differ from the backup"
files=("$invariants"/*.sql)
(( ${#files[@]} > 0 )) || fail "no invariant files in $invariants"

# The schema version the backup was taken at (a backup is taken before a deploy migrates, so it can be older than the
# invariants in the tree). Same rule as InvariantRunner.VersionPrefix: NNNN_<name>.sql holds from version NNNN onward.
# Only an empty database (a backup taken before the first migrate) may lack schema_migrations; anything else would make
# every numbered invariant skip silently.
present="$(psql_su -d "$scratch" -c "select to_regclass('public.schema_migrations') is not null")" \
  || fail "schema_migrations could not be probed"
case "$present" in
  t) version="$(psql_su -d "$scratch" -c "select coalesce(max(version), -1) from schema_migrations")" \
       || fail "schema version could not be read" ;;
  f) tables="$(psql_su -d "$scratch" -c "select count(*) from information_schema.tables where table_schema = 'public'")" \
       || fail "the restored tables could not be counted"
     [[ "$tables" == 0 ]] || fail "the restored database has $tables table(s) but no schema_migrations, so its invariants can't be selected"
     version=-1 ;;
  *) fail "unreadable schema_migrations probe result '$present'" ;;
esac
[[ "$version" =~ ^-?[0-9]+$ ]] || fail "unreadable schema version '$version'"

held=0
skipped=0
for f in "${files[@]}"; do
  name="$(basename "$f")"
  if [[ "$name" =~ ^([0-9]{4})_ ]] && (( 10#${BASH_REMATCH[1]} > version )); then skipped=$((skipped + 1)); continue; fi
  # Judge by the row count psql reports, not by the text: a NULL or empty violating row prints nothing visible.
  # The echo must follow a terminated statement (an extra ';' would reset ROW_COUNT); the trailing select makes a file
  # that lacks its final ';' a syntax error instead of a stale count, and its BG_END line proves psql reached the end.
  out="$({ cat "$f"; printf '\n%s\n%s\n' '\echo BG_ROWS :ROW_COUNT' "select 'BG_END';"; } | psql_su -d "$scratch")" \
    || fail "invariant $name errored (psql exit status $?)"
  [[ "$(tail -n 1 <<<"$out")" == BG_END ]] || fail "invariant $name did not run to its end"
  rows="$(grep -E '^BG_ROWS [0-9]+$' <<<"$out" | tail -n 1 | cut -d' ' -f2 || true)"
  [[ -n "$rows" ]] || fail "invariant $name did not report a row count"
  (( rows == 0 )) || fail "invariant $name reported: $rows row(s): $(grep -vE '^(BG_ROWS [0-9]+|BG_END)$' <<<"$out" | head -c 200)"
  held=$((held + 1))
done
(( version < 0 || held > 0 )) || fail "no invariant applies at schema version $version (all $skipped skipped)"
echo "DRILL PASS: $(wc -l < "$counts") count lines matched, $held invariant(s) held, $skipped skipped as newer than schema version $version, scratch database $scratch"

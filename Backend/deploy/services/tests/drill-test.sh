#!/usr/bin/env bash
# Stage 1 drill test (bg-dev-services01): a backup taken while a writer runs records counts from its own snapshot, the
# restore drill passes, and broken inputs (dumps, counts, invariants, a database without migration history) make it
# FAIL with exit 1 and a stated reason. Uses the throwaway Compose project bg-drill and removes it.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
export BLACKGLASS_COMPOSE_PROJECT=bg-drill BLACKGLASS_API_TAG=drill
work="$(mktemp -d)"
export BLACKGLASS_SECRETS_DIR="$work/secrets" BLACKGLASS_BACKUP_DIR="$work/backups"
dc() { docker compose -p bg-drill -f "$here/compose.yaml" "$@"; }
psql_su() { dc exec -T postgres psql -X -q -v ON_ERROR_STOP=1 -A -t -U postgres -d accounts "$@"; }
cleanup() { dc --profile tools down -v --remove-orphans >/dev/null 2>&1 || true; docker image rm -f "blackglass-accounts-api:$BLACKGLASS_API_TAG" >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT
failures=0
check() { local desc="$1"; shift; if "$@"; then echo "ok   - $desc"; else echo "FAIL - $desc"; failures=$((failures + 1)); fi; }
# drill_fails <reason> <file.dump> [VAR=value ...]: the drill must exit exactly 1 and print "DRILL FAIL: ...<reason>...".
drill_fails() {
  local reason="$1" dump_file="$2" out rc=0
  shift 2
  out="$(env "$@" bash "$here/restore-drill.sh" "$dump_file" 2>&1)" || rc=$?
  (( rc == 1 )) && grep -q '^DRILL FAIL: ' <<<"$out" && grep -qF -- "$reason" <<<"$out"
}
# invariants_with <dir> <file> <sql>: the real invariants plus one extra file.
invariants_with() { mkdir -p "$1"; cp "$here/../../db/invariants/"*.sql "$1/"; printf '%s' "$3" > "$1/$2"; }

bash "$here/make-secrets.sh" "$BLACKGLASS_SECRETS_DIR" >/dev/null
dc build api >/dev/null
dc up -d --wait postgres
dc --profile tools run --rm migrate
psql_su <<'SQL'
create table drill_probe (id bigint generated always as identity primary key, payload bytea not null, note text not null, at timestamptz not null default now());
insert into drill_probe (payload, note, at)
  select sha256(g::text::bytea), 'ligne ' || g || ' — é ü 漢', now() - g * interval '1 minute' from generate_series(1, 1000) g;
SQL
# The backup's schema version, so the version-gate cases don't depend on how many migrations exist.
version="$(psql_su -c 'select max(version) from schema_migrations')"
current="$(printf '%04d' "$version")"
newer="$(printf '%04d' $((version + 1)))"

# A writer that keeps inserting while the backup holds its snapshot open for 3 s.
( for _ in $(seq 1 60); do psql_su -c "insert into drill_probe (payload, note) values ('\x00', 'during backup')"; sleep 0.1; done ) &
writer=$!
dump="$(BLACKGLASS_BACKUP_PAUSE=3 bash "$here/backup.sh" | tail -n 1)"
live="$(psql_su -c 'select count(*) from drill_probe')"   # read right after the backup returns, while rows may still arrive
wait "$writer"
counts="${dump%.dump}.counts"
recorded="$(awk '$1 == "drill_probe" { print $2 }' "$counts")"
check "rows were written after the snapshot (recorded $recorded < live $live)" test "$recorded" -lt "$live"
check "backup files are private (600)" test "$(stat -c %a "$dump")" = 600 -a "$(stat -c %a "$counts")" = 600
check "restore drill passes" bash "$here/restore-drill.sh" "$dump"

head -c 2000 "$dump" > "$work/backups/truncated.dump"; cp "$counts" "$work/backups/truncated.counts"
check "a truncated dump fails the drill" drill_fails "pg_restore failed" "$work/backups/truncated.dump"

head -c $(( $(stat -c %s "$dump") * 9 / 10 )) "$dump" > "$work/backups/cut.dump"; cp "$counts" "$work/backups/cut.counts"
check "a dump truncated in its data section fails the drill" drill_fails "pg_restore failed" "$work/backups/cut.dump"

cp "$dump" "$work/backups/tampered.dump"; sed 's/^drill_probe .*/drill_probe 1/' "$counts" > "$work/backups/tampered.counts"
check "a count mismatch fails the drill" drill_fails "row counts differ" "$work/backups/tampered.dump"

invariants_with "$work/violated" always_fails.sql "select 'always fails';"
check "a violated invariant fails the drill" drill_fails "invariant always_fails.sql reported: 1 row(s)" "$dump" BLACKGLASS_INVARIANTS_DIR="$work/violated"

invariants_with "$work/null-inv" null_row.sql "select null::int;"
check "an invariant returning a NULL row fails the drill" drill_fails "invariant null_row.sql reported: 1 row(s)" "$dump" BLACKGLASS_INVARIANTS_DIR="$work/null-inv"

invariants_with "$work/broken" broken.sql "select * from no_such_table;"
check "an invariant query that errors fails the drill" drill_fails "invariant broken.sql errored" "$dump" BLACKGLASS_INVARIANTS_DIR="$work/broken"

invariants_with "$work/unterminated" unterminated.sql "select 1 where false"
check "an invariant without its final ';' fails the drill" drill_fails "invariant unterminated.sql" "$dump" BLACKGLASS_INVARIANTS_DIR="$work/unterminated"

mkdir -p "$work/empty-inv"
check "an empty invariant directory fails the drill" drill_fails "no invariant files" "$dump" BLACKGLASS_INVARIANTS_DIR="$work/empty-inv"

invariants_with "$work/current-inv" "${current}_violated_at_backup_version.sql" "select 1;"
check "an invariant numbered at the backup's version runs (its violation fails the drill)" \
  drill_fails "invariant ${current}_violated_at_backup_version.sql reported" "$dump" BLACKGLASS_INVARIANTS_DIR="$work/current-inv"

invariants_with "$work/future-inv" "${newer}_needs_future_table.sql" "select 1 from no_such_table;"
check "an invariant numbered above the backup's version is skipped" env BLACKGLASS_INVARIANTS_DIR="$work/future-inv" bash "$here/restore-drill.sh" "$dump"

# Tables but no schema_migrations: every numbered invariant would be skipped, so the drill must refuse.
psql_su -d postgres -c "create database no_history"
psql_su -d no_history -c "create table orphan (id int); insert into orphan values (1);"
orphan_dump="$(BLACKGLASS_DB=no_history bash "$here/backup.sh" | tail -n 1)"
check "a database with tables but no schema_migrations fails the drill" drill_fails "but no migration history" "$orphan_dump"

# The same with an empty schema_migrations next to application data.
psql_su -d postgres -c "create database empty_history"
psql_su -d empty_history -c "create table schema_migrations (version integer primary key); create table orphan (id int); insert into orphan values (1);"
empty_history_dump="$(BLACKGLASS_DB=empty_history bash "$here/backup.sh" | tail -n 1)"
check "a database with tables and an empty schema_migrations fails the drill" drill_fails "but no migration history" "$empty_history_dump"

check "no scratch database is left behind" test -z "$(psql_su -d postgres -c "select datname from pg_database where datname like 'drill\_%'")"
echo "drill-test: $failures failure(s)"
exit $(( failures > 0 ))

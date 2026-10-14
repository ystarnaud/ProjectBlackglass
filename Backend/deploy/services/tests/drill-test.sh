#!/usr/bin/env bash
# Stage 1 drill test (bg-dev-services01): a backup taken while a writer runs records counts from its own snapshot, the
# restore drill passes, and three broken inputs make it FAIL. Uses the throwaway Compose project bg-drill and removes it.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
export BLACKGLASS_COMPOSE_PROJECT=bg-drill
work="$(mktemp -d)"
export BLACKGLASS_SECRETS_DIR="$work/secrets" BLACKGLASS_BACKUP_DIR="$work/backups"
dc() { docker compose -p bg-drill -f "$here/compose.yaml" "$@"; }
psql_su() { dc exec -T postgres psql -X -q -v ON_ERROR_STOP=1 -A -t -U postgres -d accounts "$@"; }
cleanup() { dc --profile tools down -v --remove-orphans >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT
failures=0
check() { local desc="$1"; shift; if "$@"; then echo "ok   - $desc"; else echo "FAIL - $desc"; failures=$((failures + 1)); fi; }
not() { ! "$@"; }

bash "$here/make-secrets.sh" "$BLACKGLASS_SECRETS_DIR" >/dev/null
dc build api >/dev/null
dc up -d --wait postgres
dc --profile tools run --rm migrate
psql_su <<'SQL'
create table drill_probe (id bigint generated always as identity primary key, payload bytea not null, note text not null, at timestamptz not null default now());
insert into drill_probe (payload, note, at)
  select sha256(g::text::bytea), 'ligne ' || g || ' — é ü 漢', now() - g * interval '1 minute' from generate_series(1, 1000) g;
SQL

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
check "a truncated dump fails the drill" not bash "$here/restore-drill.sh" "$work/backups/truncated.dump"

head -c $(( $(stat -c %s "$dump") * 9 / 10 )) "$dump" > "$work/backups/cut.dump"; cp "$counts" "$work/backups/cut.counts"
check "a dump truncated in its data section fails the drill" not bash "$here/restore-drill.sh" "$work/backups/cut.dump"

cp "$dump" "$work/backups/tampered.dump"; sed 's/^drill_probe .*/drill_probe 1/' "$counts" > "$work/backups/tampered.counts"
check "a count mismatch fails the drill" not bash "$here/restore-drill.sh" "$work/backups/tampered.dump"

mkdir -p "$work/invariants"; cp "$here/../../db/invariants/"*.sql "$work/invariants/"
echo "select 'always fails';" > "$work/invariants/9999_always_fails.sql"
check "a violated invariant fails the drill" not env BLACKGLASS_INVARIANTS_DIR="$work/invariants" bash "$here/restore-drill.sh" "$dump"

check "no scratch database is left behind" test -z "$(psql_su -d postgres -c "select datname from pg_database where datname like 'drill\_%'")"
echo "drill-test: $failures failure(s)"
exit $(( failures > 0 ))

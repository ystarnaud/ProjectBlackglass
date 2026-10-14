#!/usr/bin/env bash
# Backs up one database of a Blackglass Compose stack (spec §15.4, B19): pg_dump -Fc plus exact row counts taken in the
# same snapshot, so the restore drill can compare them even while the API writes. Runs on bg-dev-services01.
# Environment:
#   BLACKGLASS_COMPOSE_PROJECT  Compose project (default blackglass-accounts)
#   BLACKGLASS_DB               database (default accounts)
#   BLACKGLASS_BACKUP_DIR       host directory (default /opt/blackglass/accounts/backups)
#   BLACKGLASS_BACKUP_PAUSE     seconds to keep the snapshot open after the dump (tests only; default 0)
# Writes <db>-<UTC>.dump, .counts and .dump.sha256 (mode 600) and prints the .dump path last.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
project="${BLACKGLASS_COMPOSE_PROJECT:-blackglass-accounts}"
db="${BLACKGLASS_DB:-accounts}"
dir="${BLACKGLASS_BACKUP_DIR:-/opt/blackglass/accounts/backups}"
pause="${BLACKGLASS_BACKUP_PAUSE:-0}"
[[ "$pause" =~ ^[0-9]+$ ]] || { echo "BLACKGLASS_BACKUP_PAUSE must be a whole number" >&2; exit 2; }
[[ "$db" =~ ^[a-z_][a-z0-9_]*$ ]] || { echo "invalid database name" >&2; exit 2; }
dc() { docker compose -p "$project" -f "$here/compose.yaml" "$@"; }

umask 077
mkdir -p "$dir"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
base="$dir/$db-$stamp"
remote_counts="/tmp/bg-backup-$stamp-$$.counts"

# One psql session holds a REPEATABLE READ snapshot. pg_dump, started from inside the session with \!, dumps exactly
# that snapshot to stdout (psql itself prints nothing to stdout here); the counts are then read in the same snapshot.
dc exec -T postgres psql -X -q -v ON_ERROR_STOP=1 -U postgres -d "$db" > "$base.dump.partial" <<SQL
\pset format unaligned
\pset tuples_only on
begin transaction isolation level repeatable read read only;
select pg_export_snapshot() as snap \gset
\setenv BG_SNAP :snap
\! pg_dump -U postgres -Fc --snapshot="\$BG_SNAP" "$db"
\! sleep $pause
\o $remote_counts
$(cat "$here/sql/row-counts.sql")
\o
commit;
SQL
dc exec -T postgres sh -c "cat '$remote_counts' && rm -f '$remote_counts'" > "$base.counts"
dc exec -T postgres pg_restore --list < "$base.dump.partial" > /dev/null   # the archive is readable
mv "$base.dump.partial" "$base.dump"
(cd "$dir" && sha256sum "$(basename "$base.dump")" > "$base.dump.sha256")
echo "backup: $(wc -l < "$base.counts") count lines, $(stat -c %s "$base.dump") bytes" >&2
echo "$base.dump"

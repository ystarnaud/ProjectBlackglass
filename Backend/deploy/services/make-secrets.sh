#!/usr/bin/env bash
# Creates one stack's database secret files, once (existing values are never replaced). Every consumer gets its own
# file, mode 0400, owned by the uid of the one in-container user that reads it (Stage 1 ruling 5):
#   postgres/{superuser,migrator,app}_password   uid BLACKGLASS_PG_UID  (default 999, "postgres" in postgres:17)
#   migrate/db_password  (= migrator password)    uid BLACKGLASS_APP_UID (default 1654, "app" in the API image)
#   api/db_password      (= app password)         uid BLACKGLASS_APP_UID
# Directories are root-owned 0700. Run as root (sudo).   Usage: make-secrets.sh <directory>
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "make-secrets.sh must run as root (sudo)" >&2; exit 1; }
dir="${1:?usage: make-secrets.sh <directory>}"
pg_uid="${BLACKGLASS_PG_UID:-999}"
app_uid="${BLACKGLASS_APP_UID:-1654}"
umask 077
install -d -m 0700 -o root -g root "$dir" "$dir/postgres" "$dir/migrate" "$dir/api"

random_password() {
  local pw
  pw="$(head -c 300 /dev/urandom | LC_ALL=C tr -dc 'A-Za-z0-9')"
  (( ${#pw} >= 40 )) || { echo "not enough random characters; run again" >&2; return 1; }
  printf '%s' "${pw:0:40}"
}
existing_or_new() { if [[ -e "$1" ]]; then cat "$1"; else random_password; fi; }
place() {  # place <file> <uid> <value>: write once, then enforce owner and mode
  [[ -e "$1" ]] || printf '%s' "$3" > "$1"
  chown "$2:$2" "$1"
  chmod 0400 "$1"
}

superuser="$(existing_or_new "$dir/postgres/superuser_password")"
migrator="$(existing_or_new "$dir/postgres/migrator_password")"
app="$(existing_or_new "$dir/postgres/app_password")"
place "$dir/postgres/superuser_password" "$pg_uid" "$superuser"
place "$dir/postgres/migrator_password" "$pg_uid" "$migrator"
place "$dir/postgres/app_password" "$pg_uid" "$app"
place "$dir/migrate/db_password" "$app_uid" "$migrator"
place "$dir/api/db_password" "$app_uid" "$app"
echo "secrets ready in $dir"

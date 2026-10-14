#!/usr/bin/env bash
# Stage 1 smoke test (bg-dev-services01), on a fresh volume: the API refuses an unmigrated database, deploy.sh runs
# backup -> migrate -> start in order, health answers on loopback, a second deploy migrates nothing, a failed backup
# stops the deploy, and the exposure check passes. Throwaway project bg-smoke; removed at the end.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
export BLACKGLASS_COMPOSE_PROJECT=bg-smoke
work="$(mktemp -d)"
export BLACKGLASS_SECRETS_DIR="$work/secrets" BLACKGLASS_BACKUP_DIR="$work/backups"
dc() { docker compose -p bg-smoke -f "$here/compose.yaml" "$@"; }
cleanup() { dc --profile tools down -v --remove-orphans >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT
failures=0
check() { local desc="$1"; shift; if "$@"; then echo "ok   - $desc"; else echo "FAIL - $desc"; failures=$((failures + 1)); fi; }
not() { ! "$@"; }

if ss -H -ltn '( sport = :5080 )' | grep -q .; then echo "port 5080 is already in use; stop the other stack first" >&2; exit 2; fi
bash "$here/make-secrets.sh" "$BLACKGLASS_SECRETS_DIR" >/dev/null
dc build api >/dev/null
dc up -d --wait postgres

set +e; dc run --rm --no-deps api serve > "$work/behind.log" 2>&1; code=$?; set -e
check "the API refuses an unmigrated database with exit 3" test "$code" -eq 3
check "and says why" grep -q "Refusing to start: schema Behind" "$work/behind.log"

check "deploy.sh succeeds" bash -c "bash '$here/deploy.sh' > '$work/deploy1.log' 2>&1"
check "deploy backed up before migrating" awk '/^== backup/ { b = NR } /^== migrate/ { m = NR } END { exit !(b && m && b < m) }' "$work/deploy1.log"
check "a dump exists" compgen -G "$BLACKGLASS_BACKUP_DIR/accounts-*.dump"
check "health answers on loopback at schema 0000" test "$(curl -sf http://127.0.0.1:5080/health)" = '{"status":"ok","schemaVersion":0}'
check "the exposure check passes" bash "$here/check-exposure.sh"
n=0
for ip in $(hostname -I); do
  n=$((n + 1))
  check "5432 refused on host address #$n" not timeout 2 bash -c "exec 2>/dev/null; </dev/tcp/$ip/5432"
  [[ "$ip" == 127.* ]] || check "5080 refused on host address #$n" not timeout 2 bash -c "exec 2>/dev/null; </dev/tcp/$ip/5080"
done

check "a second deploy applies nothing and passes health and exposure" bash -c "bash '$here/deploy.sh' > '$work/deploy2.log' 2>&1 && grep -q 'nothing applied' '$work/deploy2.log' && grep -q 'EXPOSURE PASS' '$work/deploy2.log'"
check "a failed backup stops the deploy at the backup step" bash -c "! BLACKGLASS_BACKUP_DIR=/proc/forbidden bash '$here/deploy.sh' > '$work/deploy3.log' 2>&1 && grep -q '^== backup' '$work/deploy3.log' && ! grep -q '^== migrate' '$work/deploy3.log' && ! grep -q '^== api' '$work/deploy3.log'"

# Keeps the stack up so the PC can run tools/offhost-probe.sh against it (default 0: no hold).
hold="${BLACKGLASS_SMOKE_HOLD:-0}"
if (( hold > 0 )); then echo "holding the stack for $hold s for the off-host probe"; sleep "$hold"; fi

echo "smoke-test: $failures failure(s)"
exit $(( failures > 0 ))

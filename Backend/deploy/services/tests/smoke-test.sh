#!/usr/bin/env bash
# Stage 1 smoke test (bg-dev-services01), on a fresh volume: the API refuses an unmigrated database, deploy.sh runs
# stop API -> backup -> migrate -> start in order, health answers on loopback, a second deploy stops and restarts the
# API and migrates nothing, a failed backup stops the deploy and leaves the API stopped, and the exposure check passes.
# Stage 3: dev login and admin routes are off by default, /v1/me needs a token, and the admin CLI works in the image.
# Throwaway project bg-smoke; removed at the end.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
export BLACKGLASS_COMPOSE_PROJECT=bg-smoke BLACKGLASS_API_TAG=smoke
work="$(mktemp -d)"
export BLACKGLASS_SECRETS_DIR="$work/secrets" BLACKGLASS_BACKUP_DIR="$work/backups"
dc() { docker compose -p bg-smoke -f "$here/compose.yaml" "$@"; }
cleanup() { dc --profile tools down -v --remove-orphans >/dev/null 2>&1 || true; docker image rm -f "blackglass-accounts-api:$BLACKGLASS_API_TAG" >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT
failures=0
check() { local desc="$1"; shift; if "$@"; then echo "ok   - $desc"; else echo "FAIL - $desc"; failures=$((failures + 1)); fi; }
not() { ! "$@"; }
api_started_at() { docker inspect -f '{{.State.StartedAt}}' "$(dc ps -aq api)"; }
# The schema version this tree deploys: the highest migration number (not hard-coded, so new migrations don't break it).
latest="$(find "$here/../../db/migrations" -name '[0-9][0-9][0-9][0-9]_*.sql' -printf '%f\n' | cut -c1-4 | sort | tail -n 1)"
order='/^== stop api/ { s = NR } /^== backup/ { b = NR } /^== migrate/ { m = NR } /^== api/ { a = NR } END { exit !(s && b && m && a && s < b && b < m && m < a) }'
# The second deploy logged the full order and the API container was started again (it was stopped, not left serving).
restarted_in_order() { awk "$order" "$work/deploy2.log" && [[ "$before" != "$(api_started_at)" ]]; }

if ss -H -ltn '( sport = :5080 )' | grep -q .; then echo "port 5080 is already in use; stop the other stack first" >&2; exit 2; fi
bash "$here/make-secrets.sh" "$BLACKGLASS_SECRETS_DIR" >/dev/null
dc build api >/dev/null
dc up -d --wait postgres

set +e; dc run --rm --no-deps api serve > "$work/behind.log" 2>&1; code=$?; set -e
check "the API refuses an unmigrated database with exit 3" test "$code" -eq 3
check "and says why" grep -q "Refusing to start: schema Behind" "$work/behind.log"

check "deploy.sh succeeds" bash -c "bash '$here/deploy.sh' > '$work/deploy1.log' 2>&1"
check "deploy stopped the API, backed up, migrated, then started the API" awk "$order" "$work/deploy1.log"
check "a dump exists" compgen -G "$BLACKGLASS_BACKUP_DIR/accounts-*.dump"
check "health answers on loopback at schema $latest" test "$(curl -sf --max-time 5 http://127.0.0.1:5080/health)" = "{\"status\":\"ok\",\"schemaVersion\":$((10#$latest))}"
check "the exposure check passes" bash "$here/check-exposure.sh"
# Stage 3, with the stack's defaults: dev login and the admin routes are not mapped; the bearer challenge works.
http_code() { curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$@"; }
check "dev login is off by default (404)" test "$(http_code -X POST -H 'Content-Type: application/json' -d '{}' http://127.0.0.1:5080/v1/auth/dev/login)" = 404
check "the admin route is off by default (404)" test "$(http_code -X POST -H 'Content-Type: application/json' -d '{}' http://127.0.0.1:5080/dev/v1/accounts)" = 404
check "GET /v1/me without a token is 401" test "$(http_code http://127.0.0.1:5080/v1/me)" = 401
# The operator CLI in the real image: the first account gets id 1, printed in Base58 as "2". The key line is redacted
# before anything is written, so no key is kept even in the throwaway work directory.
admin_cli() { dc --profile tools run --rm -T admin "$@" 2>&1 | sed -E 's/^accountKey=dk1_.*/accountKey=dk1_[redacted]/'; return "${PIPESTATUS[0]}"; }
admin_creates_the_first_account() {
  admin_cli account create --handle smoke --operator smoke-test --reason smoke > "$work/admin.out" \
    && grep -qx 'accountId=2' "$work/admin.out" && grep -qx 'accountKey=dk1_\[redacted\]' "$work/admin.out" \
    && grep -qx 'starterGranted=granted' "$work/admin.out" \
    && grep -Eqx 'starterOperatives=[1-9][0-9]*' "$work/admin.out" && grep -Eqx 'starterInstances=[1-9][0-9]*' "$work/admin.out" \
    && grep -Eqx 'starterStacks=[1-9][0-9]*' "$work/admin.out"
}
# Account creation provisioned the starter, so the explicit backfill finds it already granted (exit 0, nothing granted again).
admin_backfill_finds_the_starter_already() {
  admin_cli starter backfill --account-base58 2 --operator smoke-test --reason smoke --yes > "$work/backfill.out" \
    && grep -qx 'accountId=2' "$work/backfill.out" && grep -qx 'starterGranted=already' "$work/backfill.out" \
    && grep -qx 'target.starterGranted=true' "$work/backfill.out"
}
admin_refuses_the_old_flag() {
  local code=0; admin_cli account suspend --account 2 --operator smoke-test --reason smoke > "$work/old-flag.out" || code=$?
  test "$code" -eq 64 && grep -q -- '--account-base58' "$work/old-flag.out"
}
admin_shows_the_target_and_changes_nothing_without_yes() {
  local code=0; admin_cli account suspend --account-base58 2 --operator smoke-test --reason smoke > "$work/preview.out" || code=$?
  test "$code" -eq 64 && grep -qx 'target.accountId=2' "$work/preview.out" && grep -qx 'target.status=active' "$work/preview.out" \
    && grep -qx 'target.handle=smoke' "$work/preview.out" && ! grep -q '^correlationId=' "$work/preview.out"
}
admin_suspends_with_yes() {
  admin_cli account suspend --account-base58 2 --operator smoke-test --reason smoke --yes > "$work/suspend.out" \
    && grep -qx 'accountId=2' "$work/suspend.out" && grep -qx 'status=suspended' "$work/suspend.out"
}
admin_unknown_account_is_65() {
  local code=0; admin_cli account suspend --account-base58 zz --operator smoke-test --reason smoke --yes > /dev/null || code=$?
  test "$code" -eq 65
}
check "the admin CLI creates the first account, prints its Base58 id and the granted starter counts" admin_creates_the_first_account
check "starter backfill on that account exits 0 and says already" admin_backfill_finds_the_starter_already
check "the old --account flag is refused (64): ids are named by --account-base58" admin_refuses_the_old_flag
check "suspend without --yes and without a terminal shows the target and changes nothing (64)" admin_shows_the_target_and_changes_nothing_without_yes
check "suspend with --yes acts on the account the Base58 id names" admin_suspends_with_yes
check "an unknown Base58 account id is exit 65" admin_unknown_account_is_65
n=0
for ip in $(hostname -I); do
  n=$((n + 1))
  check "5432 refused on host address #$n" not timeout 2 bash -c "exec 2>/dev/null; </dev/tcp/$ip/5432"
  [[ "$ip" == 127.* ]] || check "5080 refused on host address #$n" not timeout 2 bash -c "exec 2>/dev/null; </dev/tcp/$ip/5080"
done

before="$(api_started_at)"
check "a second deploy applies nothing and passes health and exposure" bash -c "bash '$here/deploy.sh' > '$work/deploy2.log' 2>&1 && grep -q 'nothing applied' '$work/deploy2.log' && grep -q 'EXPOSURE PASS' '$work/deploy2.log'"
check "the second deploy stopped the running API before the backup and restarted it" restarted_in_order

# Keeps the stack up, with the API serving, so the PC can run tools/offhost-probe.sh against it (default 0: no hold).
# This comes before the failed-backup case below, which leaves the API stopped.
hold="${BLACKGLASS_SMOKE_HOLD:-0}"
if (( hold > 0 )); then
  echo "holding the stack for $hold s for the off-host probe (API serving on 127.0.0.1:5080)"; sleep "$hold"
  check "the API was serving throughout the hold" curl -sf --max-time 5 http://127.0.0.1:5080/health -o /dev/null
fi

check "a failed backup stops the deploy at the backup step" bash -c "! BLACKGLASS_BACKUP_DIR=/proc/forbidden bash '$here/deploy.sh' > '$work/deploy3.log' 2>&1 && grep -q '^== backup' '$work/deploy3.log' && ! grep -q '^== migrate' '$work/deploy3.log' && ! grep -q '^== api' '$work/deploy3.log'"
check "and leaves the API stopped (fails closed)" not curl -sf --max-time 3 http://127.0.0.1:5080/health

echo "smoke-test: $failures failure(s)"
exit $(( failures > 0 ))

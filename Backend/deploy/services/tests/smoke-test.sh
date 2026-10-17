#!/usr/bin/env bash
# Stage 1 smoke test (bg-dev-services01), on a fresh volume: the API refuses an unmigrated database, deploy.sh runs
# stop API -> backup -> migrate -> start in order, health answers on loopback, a second deploy stops and restarts the
# API and migrates nothing, a failed backup stops the deploy and leaves the API stopped, and the exposure check passes.
# Stage 3: dev login and admin routes are off by default, /v1/me needs a token, and the admin CLI works in the image.
# Stage 5: with dev login enabled only for that section, a new account's starter weapon is unequipped and re-equipped in
# one POST /v1/inventory/operations batch (200, version up by one), and replaying the same body with the old version is
# 409 VersionMismatch, once, never retried. The account key and the access token stay in shell variables: never printed,
# never on a command line, never in a file.
# Stage 6: account creation opens a full energy wallet; GET /v1/me shows {balance:50, cap:50, nextRegenAt:null}; a
# development grant with a fresh idempotency key credits 5 once and its replay reports the stored result without
# crediting again; /v1/me then shows 55 above the cap of 50; and an on-demand reconciliation finds no mismatch.
# Stage 7: with no verified capacity a deploy is 503 and touches no energy; with the development host fixture enabled for
# that section only (BLACKGLASS_DEV_HOSTS=enabled on api and admin; never the stack default), a registered and heartbeating
# fixture admits a deploy (201, energy 55 -> 45), the same key and body replay the identical body, the same key with another
# body is 409, the inventory is locked (an edit is 409 InventoryLocked, reads show locked:true), and the pre-start cancel
# releases the energy once (back to 55) and unlocks the inventory. No seed or salt appears in any response.
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
    && grep -Eqx 'starterStacks=[1-9][0-9]*' "$work/admin.out" \
    && grep -qx 'energyOpened=opened' "$work/admin.out" && grep -qx 'energyBalance=50' "$work/admin.out"
}
# Account creation provisioned the starter, so the explicit backfill finds it already granted (exit 0, nothing granted again).
# It reports the recorded catalogue version and no counts: current holdings are never printed as the starter.
admin_backfill_finds_the_starter_already() {
  admin_cli starter backfill --account-base58 2 --operator smoke-test --reason smoke --yes > "$work/backfill.out" \
    && grep -qx 'accountId=2' "$work/backfill.out" && grep -qx 'starterGranted=already' "$work/backfill.out" \
    && grep -qx 'target.starterGranted=true' "$work/backfill.out" \
    && grep -Eqx 'catalogueVersion=sha256:.+' "$work/backfill.out" \
    && ! grep -Eq '^(operatives|instances|stacks)=' "$work/backfill.out"
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
check "the admin CLI creates the first account, prints its Base58 id, the granted starter counts and a full energy wallet" admin_creates_the_first_account
check "starter backfill on that account exits 0, says already, and reports the recorded version and no counts" admin_backfill_finds_the_starter_already
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

# Stage 5: the inventory batch round trip. Dev login is enabled by recreating the API for this section only, then put back.
# Secrets rule: the account key (dk1_) and the access token (at1_) live in shell variables alone. They are never echoed,
# never an argument of any command (argv shows in ps; printf is a builtin, so a pipe carries them), and never in a file.
api_url=http://127.0.0.1:5080
inv_key= inv_token= inv_body= inv_status= inv_version= inv_operative= inv_weapon= inv_account= grant_key=
wait_for_health() { local i; for i in $(seq 1 30); do curl -sf --max-time 2 "$api_url/health" -o /dev/null && return 0; sleep 1; done; return 1; }
# Recreates the API from the current environment (the compose default for BLACKGLASS_DEV_AUTH is disabled).
api_recreate() { dc up -d --wait api > "$work/api-recreate.log" 2>&1 && wait_for_health; }
api_with_dev_auth_enabled() { BLACKGLASS_DEV_AUTH=enabled api_recreate; }
# The unredacted create output stays in a variable; only the key line is kept, in inv_key.
inv_creates_an_account() {
  local out
  out="$(dc --profile tools run --rm -T admin account create --handle inventory --operator smoke-test --reason smoke 2>&1)" || return 1
  inv_key="$(printf '%s\n' "$out" | sed -n 's/^accountKey=\(dk1_.*\)$/\1/p')"
  inv_account="$(printf '%s\n' "$out" | sed -n 's/^accountId=\([1-9A-HJ-NP-Za-km-z]*\)$/\1/p')"
  out=
  [[ "$inv_key" == dk1_* && -n "$inv_account" ]]
}
# The login body travels on stdin; the token is taken from the response and kept in inv_token.
inv_logs_in() {
  local resp
  resp="$(printf '{"accountKey":"%s"}' "$inv_key" | curl -s --max-time 5 -X POST -H 'Content-Type: application/json' --data-binary @- "$api_url/v1/auth/dev/login")" || return 1
  inv_token="$(printf '%s' "$resp" | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p')"
  resp=
  [[ "$inv_token" == at1_* ]]
}
# An authenticated request: the bearer header and the url come from a config on stdin, never from the command line. The
# first argument is the path, the rest are curl options (a data file for a POST). Sets inv_status and inv_body.
inv_call() {
  local path="$1" resp; shift
  resp="$({ printf 'header = "Authorization: Bearer %s"\n' "$inv_token"; printf 'url = "%s%s"\n' "$api_url" "$path"; } \
    | curl -s --max-time 5 --config - -w '\n%{http_code}' "$@")" || resp=$'\n000'
  inv_status="${resp##*$'\n'}"; inv_body="${resp%$'\n'*}"
}
# GET /v1/inventory: the version, and the first operative that has a weapon equipped, with that weapon's item id. Compact
# JSON, split at each operative: {"version":N,...,"operatives":[{"operativeId":"..","bag":{..},"equipment":{"weapon":"..",..}}]}.
inv_reads_the_starter_weapon() {
  inv_call /v1/inventory && [[ "$inv_status" == 200 ]] || return 1
  local bag
  bag="$(printf '%s' "$inv_body" | sed 's/{"operativeId"/\n&/g' \
    | grep -E -m 1 '^\{"operativeId":"[1-9A-HJ-NP-Za-km-z]+".*"equipment":\{"weapon":"[1-9A-HJ-NP-Za-km-z]+"')" || return 1
  inv_version="$(printf '%s' "$inv_body" | sed -n 's/^{"version":\([0-9][0-9]*\),.*/\1/p')"
  inv_operative="$(printf '%s' "$bag" | sed -n 's/^{"operativeId":"\([^"]*\)".*/\1/p')"
  inv_weapon="$(printf '%s' "$bag" | sed -n 's/.*"equipment":{"weapon":"\([^"]*\)".*/\1/p')"
  [[ -n "$inv_version" && -n "$inv_operative" && -n "$inv_weapon" ]]
}
# One batch: unequip the weapon slot, then equip the same item. The body holds ids and a version only, so it may be a file.
inv_batch_round_trips() {
  printf '{"expectedVersion":%s,"operations":[{"op":"unequip","operativeId":"%s","slot":"weapon"},{"op":"equip","operativeId":"%s","itemId":"%s"}]}' \
    "$inv_version" "$inv_operative" "$inv_operative" "$inv_weapon" > "$work/inventory-batch.json"
  inv_call /v1/inventory/operations -H 'Content-Type: application/json' --data-binary "@$work/inventory-batch.json" \
    && [[ "$inv_status" == 200 ]] \
    && [[ "$(printf '%s' "$inv_body" | sed -n 's/^{"version":\([0-9][0-9]*\),.*/\1/p')" == "$((inv_version + 1))" ]]
}
# The same file again: its expectedVersion is now one behind. One attempt; no refresh of the version, no retry.
inv_replay_is_a_version_mismatch() {
  inv_call /v1/inventory/operations -H 'Content-Type: application/json' --data-binary "@$work/inventory-batch.json" \
    && [[ "$inv_status" == 409 ]] && printf '%s' "$inv_body" | grep -q '"code":"VersionMismatch"'
}
check "the API is recreated with dev login enabled for the inventory checks" api_with_dev_auth_enabled
check "the admin CLI creates a second account and prints its key once (kept in memory only)" inv_creates_an_account
check "that account logs in and receives an access token (kept in memory only)" inv_logs_in
check "GET /v1/inventory shows the starter weapon equipped, and the version" inv_reads_the_starter_weapon
check "one batch unequips and re-equips the starter weapon: 200 and the version is up by one" inv_batch_round_trips
check "replaying that body with the old version is 409 VersionMismatch (once, not retried)" inv_replay_is_a_version_mismatch
# Stage 6: energy. The wallet opened with the account; a GET projects it without writing.
me_energy_is() { inv_call /v1/me && [[ "$inv_status" == 200 ]] && printf '%s' "$inv_body" | grep -qF "\"energy\":$1"; }
# A development grant through the operator CLI (confirmed with --yes), keyed by a fresh UUID. The key is not a secret.
energy_grant() { admin_cli energy grant --account-base58 "$inv_account" --amount 5 --idempotency-key "$grant_key" --operator smoke-test --reason smoke --yes; }
energy_grant_credits_once() {
  grant_key="$(cat /proc/sys/kernel/random/uuid)"
  energy_grant > "$work/grant1.out" && grep -qx 'amount=5' "$work/grant1.out" && grep -qx 'balanceAfter=55' "$work/grant1.out" \
    && grep -qx 'replayed=false' "$work/grant1.out"
}
energy_grant_replay_is_stored() {
  energy_grant > "$work/grant2.out" && grep -qx 'balanceAfter=55' "$work/grant2.out" && grep -qx 'replayed=true' "$work/grant2.out"
}
energy_reconcile_is_clean() {
  admin_cli energy reconcile --operator smoke-test --reason smoke > "$work/reconcile.out" \
    && grep -Eqx 'walletsChecked=([2-9]|[1-9][0-9]+)' "$work/reconcile.out" && grep -qx 'mismatches=0' "$work/reconcile.out" \
    && ! grep -q '^mismatch=' "$work/reconcile.out"
}
check "GET /v1/me shows the full wallet opened with the account (50, cap 50, no next regeneration)" me_energy_is '{"balance":50,"cap":50,"nextRegenAt":null}'
check "energy grant with a fresh key credits 5 once (balanceAfter=55, replayed=false)" energy_grant_credits_once
check "the same key again reports the stored result and credits nothing (replayed=true, balanceAfter=55)" energy_grant_replay_is_stored
check "GET /v1/me shows 55, above the cap of 50, with regeneration paused" me_energy_is '{"balance":55,"cap":50,"nextRegenAt":null}'
check "energy reconcile checks every wallet and finds no mismatch" energy_reconcile_is_clean
# Stage 7: admission. The deploy body names one owned operative; the key is a fresh UUID (not a secret).
deploy_key= deploy_attempt= deploy_first=
deploy_body() { printf '{"missionTemplate":"%s","operativeIds":["%s"]}' "$1" "$inv_operative" > "$work/deploy.json"; }
deploy_call() { inv_call /v1/attempts -H 'Content-Type: application/json' -H "Idempotency-Key: $deploy_key" --data-binary "@$work/deploy.json"; }
no_secret_in() { ! printf '%s' "$1" | grep -qiE '"seed"|"retentionSalt"|"salt"'; }
deploy_without_capacity_is_503() {
  deploy_key="$(cat /proc/sys/kernel/random/uuid)"; deploy_body procedural-standard
  deploy_call && [[ "$inv_status" == 503 ]] && printf '%s' "$inv_body" | grep -q '"code":"ServiceUnavailable"'
}
api_with_dev_hosts() { BLACKGLASS_DEV_AUTH=enabled BLACKGLASS_DEV_HOSTS=enabled api_recreate; }
dev_host_ready() {
  BLACKGLASS_DEV_HOSTS=enabled admin_cli host register-dev --host-id smoke-fixture --capacity 2 --operator smoke-test --reason smoke --yes > "$work/host.out" \
    && grep -qx 'kind=dev_fixture' "$work/host.out" \
    && BLACKGLASS_DEV_HOSTS=enabled admin_cli host heartbeat-dev --host-id smoke-fixture --operator smoke-test --reason smoke > "$work/heartbeat.out"
}
deploy_admits() {
  deploy_call && [[ "$inv_status" == 201 ]] && no_secret_in "$inv_body" \
    && printf '%s' "$inv_body" | grep -qE '"balanceAfter": ?45' || return 1
  deploy_first="$inv_body"
  deploy_attempt="$(printf '%s' "$inv_body" | sed -n 's/.*"attemptId": *"\([0-9a-f-]*\)".*/\1/p')"
  [[ -n "$deploy_attempt" ]]
}
deploy_replays_identically() { deploy_call && [[ "$inv_status" == 201 && "$inv_body" == "$deploy_first" ]]; }
deploy_other_body_is_409() {
  deploy_body other-template
  deploy_call && [[ "$inv_status" == 409 ]] && printf '%s' "$inv_body" | grep -q '"code":"IdempotencyKeyReused"'
}
inventory_is_locked() {
  inv_call /v1/inventory && [[ "$inv_status" == 200 ]] && printf '%s' "$inv_body" | grep -q '"locked":true' \
    && inv_reads_the_starter_weapon && inv_call /v1/inventory/operations -H 'Content-Type: application/json' \
       --data-binary "$(printf '{"expectedVersion":%s,"operations":[{"op":"unequip","operativeId":"%s","slot":"weapon"}]}' "$inv_version" "$inv_operative")" \
    && [[ "$inv_status" == 409 ]] && printf '%s' "$inv_body" | grep -q '"code":"InventoryLocked"'
}
attempt_read_has_no_secret() { inv_call "/v1/attempts/$deploy_attempt" && [[ "$inv_status" == 200 ]] && no_secret_in "$inv_body"; }
cancel_releases() { inv_call "/v1/attempts/$deploy_attempt/cancel" -X POST && [[ "$inv_status" == 200 ]] && no_secret_in "$inv_body"; }
inventory_is_unlocked() { inv_call /v1/inventory && [[ "$inv_status" == 200 ]] && printf '%s' "$inv_body" | grep -q '"locked":false'; }
check "without verified capacity a deploy is 503 ServiceUnavailable" deploy_without_capacity_is_503
check "and the energy is untouched (55)" me_energy_is '{"balance":55,"cap":50,"nextRegenAt":null}'
check "the API is recreated with the development host fixture enabled for this section only" api_with_dev_hosts
check "a development host fixture is registered and heartbeats (admin, gated, audited)" dev_host_ready
check "a deploy is admitted: 201, energy 55 -> 45, no seed or salt in the body" deploy_admits
check "the same key and body replay the identical body (no second reservation)" deploy_replays_identically
check "the same key with another body is 409 IdempotencyKeyReused" deploy_other_body_is_409
check "the energy was reserved once (45)" me_energy_is '{"balance":45,"cap":50,"nextRegenAt":"'
check "the inventory is locked: reads show locked:true, an edit is 409 InventoryLocked" inventory_is_locked
check "GET /v1/attempts/{id} shows the attempt without seed or salt" attempt_read_has_no_secret
check "the pre-start cancel releases (200)" cancel_releases
check "the energy is released once (back to 55)" me_energy_is '{"balance":55,"cap":50,"nextRegenAt":null}'
check "the inventory is unlocked after the release" inventory_is_unlocked
unset inv_key inv_token inv_body
api_with_default_auth() { ( unset BLACKGLASS_DEV_AUTH BLACKGLASS_DEV_HOSTS; api_recreate ); }
check "the API is recreated with the stack's default again" api_with_default_auth
check "dev login is off again by default (404)" test "$(http_code -X POST -H 'Content-Type: application/json' -d '{}' "$api_url/v1/auth/dev/login")" = 404

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

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
# Stage 8: an agent host registered by the operator (its secret printed once), an allowlisted build and its heartbeat admit a
# deploy; the agent claims and launches it, the process registers, reports ready, redeems a ticket minted by POST and starts
# the participant: running, charged once; a second account cancels before start and is released once. No secret is printed.
# Stage 7: with no verified capacity a deploy is 503 and touches no energy; with the development host fixture enabled for
# that section only (BLACKGLASS_DEV_HOSTS=enabled on api and admin; never the stack default), a registered and heartbeating
# fixture admits a deploy (201, energy 55 -> 45), the same key and body replay the identical body, the same key with another
# body is 409, the inventory is locked (an edit is 409 InventoryLocked, reads show locked:true), and the pre-start cancel
# releases the energy once (back to 55) and unlocks the inventory. No seed or salt appears in any response.
# Stage 9: the running participant's manifest (built from its snapshot, never the seed) is relayed and settles (200), its receipt
# reads 200 and the energy stays charged once; the agent's exit report frees the slot; a second attempt's exit evidence (exit
# status, complete outbox scan, participant absent) voids it and refunds its energy; a third attempt's invalid manifest is
# quarantined (202) and its receipt reads under_review. No secret is printed.
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
# Stage 8: the lifecycle to running, with an agent host (development host fixtures off: the API counts only the agent host
# and its allowlisted build). The host secret, the process credential and the join ticket are secrets: like the account key
# and token they live in shell variables only, travel to curl in a config on stdin (header and body), and are never echoed,
# on a command line or in a file. The seed is checked present in the register response and never printed.
s8_cat="sha256:$(sha256sum "$here/../../catalogue/catalogue.json" | cut -d' ' -f1)"
s8_host_secret= s8_credential= s8_ticket= s8_attempt= s8_session= s8_generation=
# A POST with the bearer in the variable named by $1 and a JSON body ($3, escaped into the config). Sets s8_status, s8_body.
s8_call() {
  local token="${!1}" path="$2" body="${3//\\/\\\\}" resp; body="${body//\"/\\\"}"
  resp="$({ printf 'header = "Authorization: Bearer %s"\n' "$token"; printf 'header = "Content-Type: application/json"\n';
            printf 'url = "%s%s"\n' "$api_url" "$path"; printf 'data-binary = "%s"\n' "$body"; } \
    | curl -s --max-time 30 --config - -w '\n%{http_code}' "${@:4}")" || resp=$'\n000'
  s8_status="${resp##*$'\n'}"; s8_body="${resp%$'\n'*}"
}
s8_field() { printf '%s' "$s8_body" | sed -n "s/.*\"$1\": *\"\{0,1\}\([^\",}]*\)\"\{0,1\}.*/\1/p"; }
s8_host_registers() {
  local out
  out="$(dc --profile tools run --rm -T admin host register --host-id smoke-agent --capacity 2 --port-base 7700 --operator smoke-test --reason smoke --yes 2>&1)" || return 1
  s8_host_secret="$(printf '%s\n' "$out" | sed -n 's/^hostSecret=\(hs1_.*\)$/\1/p')"
  printf '%s\n' "$out" | grep -qx 'kind=agent' && printf '%s\n' "$out" | grep -qx 'portBase=7700' || return 1
  out=
  [[ "$s8_host_secret" == hs1_* ]]
}
s8_build_allowed() {
  admin_cli build allow --revision smoke-r1 --catalogue-version "$s8_cat" --templates procedural-standard:1 --operator smoke-test --reason smoke --yes > "$work/build.out" \
    && grep -qx 'enabled=true' "$work/build.out"
}
s8_heartbeat() {
  s8_call s8_host_secret /agent/v1/heartbeat "{\"buildRevision\":\"smoke-r1\",\"catalogueVersion\":\"$s8_cat\",\"inventory\":{\"complete\":true,\"units\":[]},\"outboxPending\":0}"
  [[ "$s8_status" == 200 ]] && printf '%s' "$s8_body" | grep -q '"drained":false'
}
s8_deploys() {
  deploy_key="$(cat /proc/sys/kernel/random/uuid)"; deploy_body procedural-standard
  deploy_call && [[ "$inv_status" == 201 ]] || return 1
  s8_attempt="$(printf '%s' "$inv_body" | sed -n 's/.*"attemptId": *"\([0-9a-f-]*\)".*/\1/p')"
  [[ -n "$s8_attempt" ]]
}
s8_claims() {
  s8_call s8_host_secret /agent/v1/claim '{}' -H "Idempotency-Key: $(cat /proc/sys/kernel/random/uuid)"
  [[ "$s8_status" == 200 && "$(s8_field attemptId)" == "$s8_attempt" && "$(s8_field missionPort)" == 7700 ]] || return 1
  s8_credential="$(s8_field processCredential)"; s8_generation="$(s8_field credentialGeneration)"; s8_body=
  [[ "$s8_credential" == pc1_* && "$s8_generation" == 1 ]]
}
s8_launching() { s8_call s8_host_secret "/agent/v1/attempts/$s8_attempt/launching" "{\"credentialGeneration\":$s8_generation}" && [[ "$s8_status" == 200 ]]; }
s8_registers() {
  s8_call s8_credential "/server/v1/attempts/$s8_attempt/sessions" "{\"buildRevision\":\"smoke-r1\",\"catalogueVersion\":\"$s8_cat\"}"
  [[ "$s8_status" == 200 ]] && printf '%s' "$s8_body" | grep -qE '"seed": ?[0-9]+' && ! printf '%s' "$s8_body" | grep -qi 'salt' || return 1
  s8_session="$(s8_field processSessionId)"
  # Stage 9: only the participants' snapshots are kept (for the manifest), never the seed.
  printf '%s' "$s8_body" | python3 -c 'import json, sys; json.dump(json.load(sys.stdin)["participants"], open(sys.argv[1], "w"))' "$work/participants.json"
  s8_body=
  [[ -n "$s8_session" ]]
}
s8_ready() {
  local hash; hash="sha256:$(printf 'blackglass-loot-pool/1\ntable=1\n0,0,item.medkit,1\n' | sha256sum | cut -d' ' -f1)"
  s8_call s8_credential "/server/v1/attempts/$s8_attempt/ready" "{\"processSessionId\":\"$s8_session\",\"layoutHash\":\"cf2e14dece9cc2a6\",\"lootTableVersion\":1,\"lootHash\":\"$hash\",\"objectives\":[{\"objectiveId\":\"extract\",\"required\":true}],\"lootPool\":[{\"containerIndex\":0,\"line\":0,\"definitionId\":\"item.medkit\",\"quantity\":1}]}"
  [[ "$s8_status" == 200 ]]
}
s8_ticket_redeem_start() {
  s8_call inv_token "/v1/attempts/$s8_attempt/tickets" '{}' && [[ "$s8_status" == 201 ]] || return 1
  s8_ticket="$(s8_field ticket)"; s8_body=
  [[ "$s8_ticket" == jt1_* ]] || return 1
  s8_call s8_credential "/server/v1/attempts/$s8_attempt/tickets/redeem" "{\"processSessionId\":\"$s8_session\",\"ticket\":\"$s8_ticket\"}"
  [[ "$s8_status" == 200 && "$(s8_field accountId)" == "$inv_account" ]] || return 1
  s8_call s8_credential "/server/v1/attempts/$s8_attempt/participants/$inv_account/start" "{\"processSessionId\":\"$s8_session\"}"
  [[ "$s8_status" == 200 ]] && printf '%s' "$s8_body" | grep -q '"started":true'
}
s8_running() { inv_call "/v1/attempts/$s8_attempt" && [[ "$inv_status" == 200 ]] && printf '%s' "$inv_body" | grep -q '"state":"running"' && no_secret_in "$inv_body"; }
# A second account deploys and cancels before any start: released once.
s8_second_cancel_releases() {
  local out key token resp attempt
  out="$(dc --profile tools run --rm -T admin account create --operator smoke-test --reason smoke 2>&1)" || return 1
  key="$(printf '%s\n' "$out" | sed -n 's/^accountKey=\(dk1_.*\)$/\1/p')"; out=
  resp="$(printf '{"accountKey":"%s"}' "$key" | curl -s --max-time 5 -X POST -H 'Content-Type: application/json' --data-binary @- "$api_url/v1/auth/dev/login")" || return 1
  token="$(printf '%s' "$resp" | sed -n 's/.*"accessToken":"\([^"]*\)".*/\1/p')"; key= resp=
  local saved="$inv_token"; inv_token="$token"
  inv_call /v1/roster; local operative; operative="$(printf '%s' "$inv_body" | sed -n 's/.*"operativeId":"\([1-9A-HJ-NP-Za-km-z]*\)".*/\1/p' | head -n 1)"
  printf '{"missionTemplate":"procedural-standard","operativeIds":["%s"]}' "$operative" > "$work/deploy2.json"
  inv_call /v1/attempts -H 'Content-Type: application/json' -H "Idempotency-Key: $(cat /proc/sys/kernel/random/uuid)" --data-binary "@$work/deploy2.json"
  attempt="$(printf '%s' "$inv_body" | sed -n 's/.*"attemptId": *"\([0-9a-f-]*\)".*/\1/p')"
  local ok=1
  [[ "$inv_status" == 201 && -n "$attempt" ]] && inv_call "/v1/attempts/$attempt/cancel" -X POST && [[ "$inv_status" == 200 ]] \
    && printf '%s' "$inv_body" | grep -q '"released":10' && inv_call "/v1/attempts/$attempt/cancel" -X POST && [[ "$inv_status" == 200 ]] \
    && me_energy_is '{"balance":50' && ok=0
  inv_token="$saved"; token=
  return $ok
}
check "the API is recreated with dev login only (development host fixtures off)" api_with_dev_auth_enabled
check "host register creates an agent host and prints its secret once (kept in memory only)" s8_host_registers
check "build allow allowlists the build for procedural-standard v1 under the image's catalogue" s8_build_allowed
check "the agent heartbeats with its secret and build" s8_heartbeat
check "a deploy is admitted (the agent host is the verified capacity)" s8_deploys
check "the agent claims it: the attempt, port 7700, a process credential (in memory only), generation 1" s8_claims
check "the agent reports launching with that generation" s8_launching
check "the process registers: a session, the seed present (never printed), no salt" s8_registers
check "the process reports ready with a pool inside the table and its canonical hash" s8_ready
check "the player mints a ticket (POST), the process redeems it and starts the participant" s8_ticket_redeem_start
check "the attempt is running, without seed or salt in the read" s8_running
check "the energy was charged once (45: reserved, then charged, never released)" me_energy_is '{"balance":45,"cap":50,"nextRegenAt":"'
check "a second account's deploy is cancelled before start and released once" s8_second_cancel_releases
# ---- Stage 9 ----
# A format-v1 manifest for the current attempt's participant ($1 outcome, $2 integrity): its snapshot items, stacks and equipment
# held as they were, the pool line (0,0) collected and held. Written to $work/manifest.json; no secret is in it.
s9_manifest() {
  local hash; hash="sha256:$(printf 'blackglass-loot-pool/1\ntable=1\n0,0,item.medkit,1\n' | sha256sum | cut -d' ' -f1)"
  python3 - "$work/participants.json" "$s8_attempt" "$s8_session" "$s8_cat" "$hash" "$1" "$2" > "$work/manifest.json" <<'PY'
import json, sys
participants, attempt, session, catalogue, loot, outcome, integrity = json.load(open(sys.argv[1])), *sys.argv[2:]
p = participants[0]; o = p["loadout"]["operatives"][0]
held = [{"origin": "owned", "itemId": i["itemId"]} for i in o["bag"]["instances"]] + \
       [{"origin": "owned", "definitionId": s["definitionId"], "quantity": s["quantity"]} for s in o["bag"]["stacks"]] + \
       [{"origin": "loot", "containerIndex": 0, "line": 0, "quantity": 1}]
equipment = [{"slot": k, "itemId": v} for k, v in o["equipment"].items() if v]
print(json.dumps({"formatVersion": 1, "attemptId": attempt, "accountId": p["accountId"], "processSessionId": session, "buildRevision": "smoke-r1",
    "catalogueVersion": catalogue, "integrity": integrity, "outcome": outcome, "outcomeSequence": 1, "serverElapsedMs": 1000,
    "layoutHash": "cf2e14dece9cc2a6", "lootTableVersion": 1, "lootHash": loot,
    "objectives": [{"objectiveId": "extract", "state": "completed" if outcome == "success" else "active"}],
    "operatives": [{"operativeId": o["operativeId"], "slot": o["slot"], "alive": True, "consumed": [], "held": held,
                    "collected": [{"containerIndex": 0, "line": 0, "quantity": 1}], "equipment": equipment}]}))
PY
}
s9_relay() { s9_manifest "$1" "$2" && s8_call s8_host_secret /agent/v1/results "$(cat "$work/manifest.json")"; }
s9_settles() { s9_relay success ok && [[ "$s8_status" == 200 ]] && printf '%s' "$s8_body" | grep -q '"disposition":"settled"' && ! printf '%s' "$s8_body" | grep -q operativeId; }
s9_receipt_is() { inv_call "/v1/attempts/$s8_attempt/receipt" && [[ "$inv_status" == "$1" ]] && printf '%s' "$inv_body" | grep -qF "$2" && no_secret_in "$inv_body"; }
s9_exited() {
  s8_call s8_host_secret "/agent/v1/attempts/$s8_attempt/exited" "{\"exitCode\":$2,\"signal\":null,\"results\":[{\"accountId\":\"$inv_account\",\"present\":$1}],\"outbox\":{\"complete\":true}}"
  [[ "$s8_status" == 200 ]] && printf '%s' "$s8_body" | grep -qF "\"applied\":\"$3\""
}
s9_lifecycle() { s8_deploys && s8_claims && s8_launching && s8_registers && s8_ready && s8_ticket_redeem_start; }
s9_quarantined() { s9_relay defeat ledgerMismatch && [[ "$s8_status" == 202 ]] && printf '%s' "$s8_body" | grep -q '"disposition":"quarantined"' && printf '%s' "$s8_body" | grep -q 'V14.integrity'; }
check "the participant's manifest is relayed and settles the attempt (200 settled, minimal answer)" s9_settles
check "its receipt is 200 with the outcome, without seed or salt" s9_receipt_is 200 '"outcome":"success"'
check "the energy stays charged once after the settlement (45)" me_energy_is '{"balance":45,"cap":50,"nextRegenAt":"'
check "the agent's exit report (present, complete scan) frees the slot" s9_exited true 0 none
s9_second_runs() { s9_lifecycle && me_energy_is '{"balance":35,'; }
check "a second attempt runs to its start (energy 35)" s9_second_runs
check "its exit evidence (exit status, complete scan, participant absent) voids it" s9_exited false 137 voided
s9_void_refunds() { me_energy_is '{"balance":45,"cap":50,"nextRegenAt":"' && s9_receipt_is 200 '"outcome":"infra_void"'; }
check "the void refunds the energy (45) and the receipt says infra_void" s9_void_refunds
check "a third attempt runs to its start" s9_lifecycle
check "its invalid manifest is quarantined (202, V14.integrity) and never settles" s9_quarantined
check "its receipt reads under_review" s9_receipt_is 202 '"state":"under_review"'
unset inv_key inv_token inv_body s8_host_secret s8_credential s8_ticket s8_body
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

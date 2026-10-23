# Blackglass backend (Phase 13, Stages 1–8: foundation, catalogue, accounts and development authentication, operatives and inventory, inventory operations, energy accounting, insurance and admission, hosts, sessions, tickets and the start commit)

ASP.NET Core on .NET 10, PostgreSQL 17, Npgsql and Dapper. Every listener is loopback only; the VM is reached by SSH alias `bg-dev-services01-admin` (user `admin`, `sudo -n`; root SSH login is disabled).

## Layout
- `src/Accounts.Domain` the catalogue model, strict reader and validator (pure, no I/O). `src/Accounts.Persistence` DbSettings (password from a file) and the migrator. `src/Accounts.Api` the `serve`, `migrate` and `admin` commands, `GET /health` and the Stage 3 auth routes (exit codes: 0 ok, 1 failure, 2 `migrate` refused a drifted or ahead schema, 3 `serve` or `admin` refused a schema that is not current, 4 `serve` refused an invalid catalogue, or `admin account create` / `starter backfill` refused an invalid or missing one (the account-security verbs never load it), 64 usage, 65 `admin` found no such account or credential, 66 `admin` found the handle taken, 67 `admin` change not confirmed, 69 `admin` database unavailable, 75 `admin` database busy).
- `catalogue/catalogue.json` the catalogue exported from Unity; `contracts/catalogue/` the golden vector shared with Unity's tests.
- `db/migrations/NNNN_lower_snake_name.sql` forward-only history; `db/invariants/` invariant queries.
- `deploy/services/` Compose stacks (`compose.yaml` accounts, `compose.test.yaml` test cluster), `initdb/`, `sql/`, VM scripts and `tests/`. `tools/` PC-side helpers. `tests/Accounts.Domain.Tests` and `tests/Accounts.Integration.Tests` xUnit.

## Tests (run from `Backend/`)
- `UNIT` = `dotnet test Backend.sln --filter "Requires!=Postgres"`: no database needed.
- `ALL` = `dotnet test Backend.sln`: also runs the `Requires=Postgres` tests against the test cluster through the tunnel.
- `tools/testdb.sh up|tunnel|down|destroy` manages the test cluster (Compose project `blackglass-testdb`, `127.0.0.1:55432` on the VM). `up` syncs `Backend/` at HEAD, creates the secrets once and copies the passwords to `~/.blackglass/testdb-secrets`; `tunnel` (leave it running) forwards the port to the PC.
- The fixture refuses any cluster whose `cluster_name` is not `blackglass-test`, and creates and drops only `bg_t_*` databases.
- **Preferred: `tools/vm-test.sh --filter <expr> | --full`**, which runs the tests on `bg-dev-services01` itself. The tunnel path above stays as the fallback.
  - **Why:** the PC is about 46 ms from the VM, and every database round trip pays that.
  - **Measured on the 172-test sample:** 47 s of tests on the VM, against 10 m 54 s through the tunnel.
  - **How it runs:**
    - It snapshots the working tree's `Backend/` into a git tree id and copies that snapshot to the VM, so uncommitted work runs too.
    - The tests run in a disposable, digest-pinned .NET 10 SDK container, `bg-test-runner`. It runs as uid 999, with 2 CPUs, 3 GB, 1024 pids, all capabilities dropped and a one-hour cap.
    - The container shares only the test cluster container's network namespace: no published port, no host network, no Docker socket.
    - Only the three test-cluster password files are mounted, read-only.
    - Tests connect to the cluster container's own address (`BLACKGLASS_TEST_PG_HOST`), never its loopback. The image's `pg_hba` trusts 127.0.0.1, which would skip password checks; the container's address gets the same `scram-sha-256` authentication as the tunnel path.
    - Named volumes cache only NuGet packages and build output. The cached tree is brought to exactly the snapshot by content, so a stale build can't be tested.
  - **Safeguards:**
    - It refuses to run unless `cluster_name = blackglass-test`.
    - It runs one at a time, under a VM-side `flock`.
    - It fails if a `bg_t_*` database or the container remains.
  - **Results:** trx files and the transcript land in `.claude/scratchpad/vm-test/`.
  - Outage tests take only their own `bg_t_*` database offline (`allow_connections`), never the shared container.
  - **Accepted risk:** the test build effectively has test-cluster superuser rights. The superuser password is mounted, and the container's loopback is trusted. So code in a NuGet package's build targets could act as superuser on the disposable test cluster, as it already could on the tunnel path. No production database is reachable from the runner.

## Migrations
- Name: `NNNN_lower_snake_name.sql`, contiguous from `0000`. The checksum is SHA-256 over UTF-8 text, BOM removed, CRLF changed to LF.
- Never edit or rename an applied file: the migrator and the API stop on drift. A fix is a new migration.
- No `BEGIN`/`COMMIT` inside a file: each file runs in its own transaction with `lock_timeout` set.
- Deploys are not zero-downtime: `deploy.sh` stops the running API before the backup, so a migration never runs while the old API writes and may be incompatible with it. Expand and contract (add first, backfill, switch the code, drop in a later release) is still the way to change data safely across releases.
- A migration that transforms data adds `tests/Accounts.Integration.Tests/Seeds/NNNN.sql`, applied to the previous version before the upgrade test.
- An invariant file returns the violating rows (zero rows = holds). A real invariant named `NNNN_<lower_snake>.sql` holds from schema version NNNN: the upgrade harness skips later-numbered files when it tests an older version; the restore drill reads the restored schema version and applies the same rule.

## Catalogue
- `catalogue/catalogue.json` is exported from Unity (Blackglass > Backend > Export Catalogue, or `Tools/run-method.sh Blackglass.BackendTools.CatalogueExporter.ExportFromCommandLine` with the Editor closed). Never edit it by hand: Unity's `CatalogueExportTests` fails while it differs from a fresh export, and the backend refuses a file that isn't in the exporter's canonical form.
- `catalogueVersion` is `sha256:` plus the lowercase hex SHA-256 of the file's bytes; nothing inside the file states it. `src/Accounts.Domain` embeds the file, and `serve` refuses to start (exit 4) when it is invalid.
- Format 2 (Stage 4; format 1 is no longer accepted) holds ids and rules only (items, loot table, progression, operatives with role and ability ids, starter loadout, and `inventory.bagCapacity`); display text stays in Unity. Any other `formatVersion` is refused. **Bag capacity's single home is Unity's `InventoryRules` asset** (8 entries per operative, validated 1..64): it is exported as `inventory.bagCapacity`, the backend validates it and has no constant of its own, and an EditMode test compares the `ProceduralMission` scene's `SquadInventory.bagCapacity` with the asset. The format 1 to 2 change moved `catalogueVersion` to `sha256:e83352b1c9d8764a92281551a25bea42621da2b592bc30d35986a5f0a86c496b`, equal in Unity, the backend and `sha256sum`.
- Permanent backend-facing ids (owner, 2026-10-14): item ids, operative GUIDs, the role and ability ids, and the existing progression choice ids `ability`, `combat`, `mobility` and `survivability`. Once stored they are never renamed; a change is a new id plus a migration of stored data.
- The loot table's `containerCount` is 0 to 6 (Unity's `LootTable.MaxContainers`), a rule of the table and so of format 1. A mission may place fewer containers than the table asks for when its layout has no room (a logged shortfall); that per-mission count belongs to the attempt's registered loot pool, validated against the table at ready (Stage 8/9).
- `contracts/catalogue/golden-catalogue.json` and `.version` are the shared vector: Unity's and the backend's writers must both produce it byte for byte, and both hash it to the `.version` value.

## VM scripts (`deploy/services/`, run on `bg-dev-services01` with `sudo`; `tools/vm-sync.sh` copies HEAD there)
Order: `make-secrets.sh <dir>` (once), then `deploy.sh` = build, database up, stop the API (in-flight requests finish, up to 30 s), `backup.sh`, `migrate`, API up, health, `check-exposure.sh`. If the backup or the migration fails, the API stays stopped: restore from the backup or fix and deploy again. `serve` and `migrate` take no arguments (anything else is a usage error, exit 64), so no argument can change the listen address. It is set by the environment (`ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS`, or ASP.NET Core's own `Kestrel__*` settings) or defaults to `127.0.0.1:5080`; in the stack that environment comes from `compose.yaml`, and Docker publishes the API's port on `127.0.0.1` only.
- `backup.sh`: `pg_dump -Fc` inside one REPEATABLE READ snapshot plus exact row counts from the same snapshot. It fails if `pg_dump` fails and validates the whole archive with `pg_restore -f /dev/null`.
- `restore-drill.sh <file.dump>`: restores into a scratch database, compares the counts, runs the invariants that apply at the restored version (a violation is a nonzero row count, NULL rows included); `DRILL PASS` or `DRILL FAIL` (exit 1). It fails on any step or invariant query that errors or doesn't run to its end, on no invariant files, on a restored database with tables but no `schema_migrations`, and when no invariant applies at a version ≥ 0. Invariants are single queries ending in `;`.
- `tests/drill-test.sh` and `tests/smoke-test.sh` use the throwaway projects `bg-drill` and `bg-smoke`. `tools/offhost-probe.sh` checks from the PC that 5080, 5432 and 55432 are closed and 22 is open.
- `env-check.sh` is the read-only inventory: `ssh bg-dev-services01-admin 'sudo -n bash -s' < deploy/services/env-check.sh`.

## Bootstrap order and privileges
1. **First start of an empty data directory. This is the only privileged step.**
   - The official image runs `initdb` as the cluster superuser `postgres`, then `initdb/00-roles.sh` as that superuser over the local socket.
   - That script creates the `accounts_migrator` and `accounts_app` login roles, both `NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION`.
   - It creates the database `accounts`, **owned by `accounts_migrator`**, revokes `PUBLIC` access and grants `CONNECT` to `accounts_app`.
   - It runs once; a non-empty data directory skips it.
2. **`migrate`, as `accounts_migrator`.** As the database owner, and a member of `pg_database_owner` (which owns schema `public` on PostgreSQL 15 and later), it:
   - creates `schema_migrations`;
   - applies `0000_foundation`: database `CONNECT`, schema `USAGE`, revoking `PUBLIC`, and default privileges for its future tables and sequences;
   - grants `SELECT` on `schema_migrations` to the app.

   No superuser is involved.
3. **Later migrations, as `accounts_migrator`.** They create tables, and `accounts_app` gets `SELECT`, `INSERT`, `UPDATE` and `DELETE` through the default privileges. Append-only tables revoke `UPDATE` and `DELETE` in their own migration.
   - `0001_accounts_and_auth` narrows that for its tables. `accounts_app` gets column-level `INSERT` and `UPDATE` for exactly the columns it writes, no `DELETE`, and no privilege on the identity sequences. So it can never choose an account or audit id (not even with `OVERRIDING SYSTEM VALUE`), set a creation time, change a key or a stored hash, or re-point a row.
   - `0001` also revokes the global `PUBLIC` `EXECUTE` default on the migrator's future functions.
   - `0002_revocation_is_final` makes a revocation permanent. A trigger on `dev_credentials` and `auth_sessions` refuses any update that clears or changes a set `revoked_at` (23514, constraint `revocation_is_final`). `accounts_app` can't disable, drop or replace it (it owns nothing) or skip it with `session_replication_role` (superuser only).
   - `0003_operatives_and_picks` and `0004_inventory` (Stage 4) revoke the default privileges on their tables and grant `accounts_app` column-level `INSERT` only (never `operative_id` or `item_id`), plus, on `inventories`, `UPDATE` of `version` and the three `starter_*` columns. No `DELETE`, no `UPDATE` of operatives or choices, no privilege on the identity sequences.
   - `0005_inventory_edits` (Stage 5) grants exactly the inventory edits: `UPDATE (operative_id, equipped_slot)` on `item_instances`, and `UPDATE (quantity)` and `DELETE` on `item_stacks` (an emptied stack row is deleted; no zero row remains). Never `DELETE` on `item_instances`, never an update of an identity, definition, account or origin column; still nothing on the identity sequences. `InventoryPrivilegeTests` pins the exact effective privileges.
   - `0006_energy` (Stage 6): `SELECT` on the four energy tables; `INSERT (cap, opening_balance, regen_interval, regen_amount, void_refund_pct)` on `energy_policies` (never `effective_at`); `INSERT (account_id, balance, regen_anchor, regen_progress_us, policy_version)` and `UPDATE` of those but `account_id` on `energy_wallets`; `INSERT (account_id, attempt_id, kind, delta, balance_after, idempotency_key)` on `energy_ledger`; `INSERT (kind, account_id, host_id, build_revision, attempt_id, dedupe_key, detail)` on `anomaly_events`. No `UPDATE`/`DELETE` on the ledger, policies or anomalies, no `DELETE` on wallets, nothing on the identity sequences. `EnergyPrivilegeTests` pins them; the 0006 triggers make the ledger append-only and chained, policies immutable, wallet anchors and versions forward-only and progress bounded. `0006` adds `energy_open`, `energy_grant` and `energy_policy_publish` to the `operator_actions` action list.
   - The trigger `inventories_forward_only` (0004) makes a granted starter record final and keeps `version` from decreasing (constraints `starter_grant_is_final`, `inventory_version_never_decreases`); `0004` also adds `starter_backfill` to the `operator_actions` action list.
   - `AccountsSchemaTests` and `PlayerSchemaTests` pin the app's exact privilege set.
4. **The API runs as `accounts_app`.** It owns nothing and has no `CREATE` on the schema or `TEMPORARY` on the database, so it has **no DDL**: no `CREATE`, `ALTER`, `DROP` or `TRUNCATE`.
5. **The superuser is used only** by the image's init, and by `backup.sh` and `restore-drill.sh` through `docker exec` over the container's local socket. No service connects as it.

The API refuses to start (exit 3) when the schema is behind, drifted or ahead. An older API against a newer schema needs an explicit, tested compatibility declaration; there is none yet.

## Secrets
Passwords come from files named by `BLACKGLASS_DB_PASSWORD_FILE` (with `BLACKGLASS_DB_HOST`, `_PORT`, `_NAME`, `_USER`), never from inline values. Each consumer gets its own file, mode 0400, owned by the uid of the one in-container user that reads it, in root-owned 0700 directories. The live stack uses `/opt/blackglass/accounts/secrets/`; the test cluster `/opt/blackglass/backend/testdb-secrets/`. No file is shared or made more readable.

| File (under the stack's secrets directory) | Owner | Read by | Compose secret |
|---|---|---|---|
| `postgres/superuser_password` | 999 (`postgres` in the image) | the image's init (`POSTGRES_PASSWORD_FILE`) | `pg_superuser_password` |
| `postgres/migrator_password` | 999 | `initdb/00-roles.sh`, at first start only | `pg_init_migrator_password` |
| `postgres/app_password` | 999 | `initdb/00-roles.sh`, at first start only | `pg_init_app_password` |
| `migrate/db_password` | 1654 (`app` in the API image) | the `migrate` service | `migrate_db_password` |
| `api/db_password` | 1654 | the `api` service | `api_db_password` |
| `api/admin_key` | 1654 | the `api` service, only when `BLACKGLASS_ADMIN_ROUTES=enabled` | `api_admin_key` |
| `admin/db_password` (= the app password) | 1654 | the `admin` operator CLI service (profile `tools`) | `admin_db_password` |

A secrets directory created before Stage 3 lacks `api/admin_key` and `admin/db_password`, and `compose up api` then fails on the missing file. Re-run `make-secrets.sh` on it: it only adds missing files and never replaces a value.

## Accounts and development authentication (Stage 3)

**Account ids.** `accounts.account_id` is a `bigint generated always as identity`: PostgreSQL alone mints it (decision 048 amendment). Outside the database it is the canonical Bitcoin Base58 string (`123456789` is `"BukQL"`, at most 11 characters, no padding), always a JSON string. Parsing refuses any other spelling: other characters, whitespace, a leading `1`, zero, and values beyond `bigint`. An account id is public and sequential by design. It never authorizes anything: every client request takes its account from the session.

**Settings** (read and validated at start even when a feature is off; an unknown value refuses to start). All limits are provisional (spec §19).

| Variable | Values | Default |
|---|---|---|
| `BLACKGLASS_DEV_AUTH` | `enabled` or `disabled` (exactly) | `disabled`: `POST /v1/auth/dev/login` is not mapped (404) |
| `BLACKGLASS_ADMIN_ROUTES` | `enabled` or `disabled` | `disabled`: `/dev/v1/*` is not mapped |
| `BLACKGLASS_ADMIN_KEY_FILE` | a file of at least 32 characters, read once and kept as SHA-256 | required when admin routes are on |
| `BLACKGLASS_TOKEN_LIFETIME_MINUTES` | 1–1440 | 60 |
| `BLACKGLASS_RATE_LOGIN_PER_MINUTE` | 1–1000, per presented key | 10 |
| `BLACKGLASS_RATE_LOGIN_GLOBAL_PER_MINUTE` | 1–100000, all logins together | 60 |
| `BLACKGLASS_RATE_REQUESTS_PER_MINUTE` | 1–100000, per account | 120 |
| `BLACKGLASS_RATE_ADMIN_PER_MINUTE` | 1–1000, all admin requests together, before the key check | 10 |
| `BLACKGLASS_DB_LOCK_TIMEOUT_MS` | 1–60000, `lock_timeout` on every API and admin CLI connection; at most the statement bound | 5000 |
| `BLACKGLASS_INVENTORY_MAX_OPERATIONS` | 1–256, operations per inventory batch (Stage 5, provisional). A batch must also fit the body limit below: at 16 KiB, about 128 operations is the practical ceiling | 32 |
| `BLACKGLASS_INVENTORY_MAX_BODY_BYTES` | 1024–1048576, the inventory batch body limit (Stage 5, provisional) | 16384 |
| `BLACKGLASS_ENERGY_RECONCILE_SECONDS` | 0 = off, else 60–86400; the reconciliation sweep interval in `serve` (Stage 6, provisional) | 3600 |
| `BLACKGLASS_ENERGY_GRANT_MAX` | 1–1000000; the largest single new development grant (`admin energy grant`; a replay is matched before it; also validated by `serve`) | 1000 |
| `BLACKGLASS_DEV_HOSTS` | `enabled`/`disabled`; development host fixtures count as capacity (tests and the smoke stack only; never production; Stage 7) | disabled |
| `BLACKGLASS_HOST_LEASE_SECONDS` | 5–600; a host heartbeat older than this is not capacity | 30 |
| `BLACKGLASS_ASSIGN_DEADLINE_SECONDS` | 10–3600; an admitted attempt not assigned by then is cancelled and released (provisional) | 60 |
| `BLACKGLASS_ATTEMPT_SWEEP_SECONDS` | 1–3600; the assignment-deadline worker's interval (always on) | 15 |
| `BLACKGLASS_DEPLOY_MAX_BODY_BYTES` | 1024–65536; the deploy body limit | 4096 |
| `BLACKGLASS_RATE_DEPLOY_PER_MINUTE` | 1–1000, deploys per account | 6 |
| `BLACKGLASS_DB_STATEMENT_TIMEOUT_MS` | 1–600000, `statement_timeout` on the same connections (the migrator has neither: it sets its own `lock_timeout`) | 15000 |

**Routes.**
- `POST /v1/auth/dev/login {accountKey}` → `{accessToken, expiresAt, accountId}`, with `Cache-Control: no-store`.
- `GET /v1/me` → `{accountId, inventoryVersion, energy, activeAttemptId, insurance}` (Stage 7 adds the last two; Stage 4: `inventoryVersion` is the shared inventory version, null for an account without a starter). Stage 6: `energy` is `{balance, cap, nextRegenAt}` projected read-only (`nextRegenAt` UTC ISO-8601 with `Z`, null at or above the cap), or `null` for an account without a wallet, never zero.
- `POST /v1/auth/logout` → 204. It revokes the current session only. A repeat gets the same 401 as any unknown token.
- `POST /dev/v1/accounts` with header `X-Admin-Key` and body `{handle?, displayName?, operator, reason}` → 201 `{accountId, credentialId, accountKey}`, with `X-Correlation-Id`.

**Failures.**
- Every authentication failure (bad, unknown, malformed, expired or revoked key or token, suspended account) is one 401: no body and `WWW-Authenticate: Bearer`.
- Database failures are never 401:
  - **Unavailable** is 503. This covers any failure to open a connection, a broken connection, and SQLSTATE classes 08, 53 and 58, and 57P01–57P03.
  - **Busy** is 503 with `Retry-After: 1`. This covers a lock or statement timeout, a deadlock or a serialization failure, and nothing was committed.
  - **Any other SQL error** is 500 with an empty body.
- Known conflicts have their own answers: a taken handle is 409, and a display name or operator text the database refuses is 422.
- Logs name the SQLSTATE and constraint only, never the server's message.

**Secrets.** Account keys are `dk1_` and access tokens `at1_`, each followed by base64url of 32 random bytes. Only SHA-256 of the raw bytes is stored. A key is shown once, to the operator who issued it. Logs show at most an 8-hex hash prefix and name accounts by their Base58 id.

**Lifecycle.**
- Revocation is immediate: every request is checked in PostgreSQL, with no cache.
- Rotation revokes the old credential and its sessions and issues one new credential; the database allows only one live credential per account.
- Revoking a credential revokes its sessions.
- Suspension revokes every session. Reactivation restores none.
- Every lifecycle call locks the account row first, and login takes the same lock (`FOR SHARE`), so no ordering leaves a live session on a revoked credential or a suspended account.
- A revocation is permanent (`0002`).
- Revocation times, and the creation time of a rotated-in credential, come after every lock their call waited for. The rows are locked by an earlier statement, so `statement_timestamp()` is taken after the wait. Logout is a transaction too. (A handle's own `created_at` can precede a wait on a concurrent create of the same handle; nothing depends on it.)
- Every wait is bounded by the two timeouts above. A call that times out rolls back everything it did.
- Each change writes an `operator_actions` row (operator, reason, correlation id, target account and credential) in the same transaction.

**Operator CLI** (on `bg-dev-services01`). Accounts are named by `--account-base58`, always the Base58 id the CLI prints, even when it is all digits: `2` is account 1. The old `--account` is refused.
```
sudo -n docker compose -p blackglass-accounts -f compose.yaml --profile tools run --rm -T admin account create [--handle <h>] [--display-name <name>] --operator <name> --reason <text>
… admin credential rotate --account-base58 <id> | credential revoke --credential <uuid> | sessions revoke --account-base58 <id> | account suspend --account-base58 <id> | account reactivate --account-base58 <id>   (each with --operator and --reason, and --yes to confirm without a terminal)
```
- `create` and `rotate` print `accountId=`, `credentialId=`, `accountKey=` (once) and `correlationId=`. `create` also prints `starterGranted=granted` and `starterOperatives=`, `starterInstances=`, `starterStacks=` (the counts it provisioned), then `energyOpened=opened` and `energyBalance=` (Stage 6), before `correlationId=`.
- **Confirmation.** Every command but `create` (and `energy reconcile`, which changes nothing; `energy policy publish` shows `target.policy*` lines and is confirmed by typing `publish`: see Energy accounting) first prints its target as `target.*` lines: the canonical Base58 id, status, handle, display name, creation time, live credential and live sessions, plus the credential for `credential revoke`. It then acts only when confirmed: either type that Base58 id at a terminal (run without `-T`), or add `--yes`. Without either it changes nothing. Running it without `--yes` and without a terminal is a dry run that shows the target.
- Exit codes:
  - 0: ok.
  - 1: unexpected failure, or an invalid energy setting for an energy verb (`Configuration error`).
  - 3: schema not current (the same policy as `serve`; nothing is written).
  - 4: the embedded catalogue is invalid (checked before the database; nothing is touched; every verb, as for `serve`).
  - 64: usage error, including a malformed id, or not confirmed with no terminal; for energy verbs also an invalid grant amount, policy or balance limit.
  - 65: not found (for energy verbs also: no wallet; run `energy open`).
  - 66: handle taken, or a development-grant key reused with other parameters.
  - 67: the typed id didn't match.
  - 69: database unavailable.
  - 75: database busy (a lock or statement timeout); nothing was changed, retry.
- Output shows the SQLSTATE and constraint, never a server message.
- Typing a decimal id, or `--account-base58 <decimal>`, names a different account or none. That's why the target is shown first, with its handle and display name.

## Operatives, inventory and picks (Stage 4)

Schema version 4. Owned ids (operatives and item instances) are PostgreSQL `bigint` identities, shown outside as canonical Base58 JSON strings exactly like account ids; catalogue definition ids are unchanged, stacks have no id. Every route needs a session, takes its account from the session alone (no path, query, header or body member names one; an unknown body member is a 400), answers `Cache-Control: no-store` and is rate limited per account. A database failure is 503, busy 503 with `Retry-After`, or 500, as in Stage 3.

**Routes.**
- `GET /v1/roster` → `{version, operatives:[{operativeId, definitionId, definitionKnown, experience, rank, pendingPicks, choices:[choiceId]}]}`.
- `GET /v1/inventory` → `{version, locked, stash:{instances:[{itemId, definitionId, definitionKnown}], stacks:[{definitionId, definitionKnown, quantity}]}, operatives:[{operativeId, bag:{instances, stacks}, equipment:{weapon, armor, utility}}]}`. `bag.instances` includes the equipped items; `equipment` gives the item id per slot or null; `locked` is false until participations exist.
- `POST /v1/operatives/{operativeId}/choices {choiceId, expectedVersion}` → `{version, operative}`. It follows `PersistentOperativeState.TryPickChoice` (a pending pick and a known choice) under the inventory row lock and bumps the shared inventory version.
- Errors: 400 `InvalidOperativeId` or `InvalidBody`, 415 for a non-JSON content type (Stage 5; it was 400 in Stage 4), 413 over 4 KiB, 404 `OperativeNotFound` (unknown and another account's operative are the same answer), 409 `VersionMismatch`, `NotProvisioned` or `InventoryLocked` (Stage 5: checked under the inventory row lock after `NotProvisioned` and before the version), 422 `NoPendingPick` or `UnknownChoice`. Reads never write: an account without a starter gets 409 `NotProvisioned` (nobody is provisioned by looking). A pick is allowed for an operative whose definition the catalogue no longer knows.

**Provisioning.** `account create` (CLI and `POST /dev/v1/accounts`) creates the account, its credential and its starter (operatives, instances equipped as authored, stacks, the `inventories` row and the starter's catalogue version) in one transaction with its audit row: a failure anywhere leaves nothing. Every operative and item is minted by `INSERT … ON CONFLICT (origin, origin_ref) DO NOTHING RETURNING`, or found by that reference, so a retry or a concurrent mint never makes a second row. A later catalogue never re-grants or replaces a starter.

**Backfill** (accounts created before Stage 4):
```
… admin starter backfill --account-base58 <id> --operator <name> --reason <text> [--yes]
```
- It is confirmed like every lifecycle command (target lines, then `--yes` or typing the Base58 id) and the target lines include `target.starterGranted`.
- It locks the account row and then the `inventories` row, grants only when `starter_granted` is false (exactly once, even concurrently) and writes a `starter_backfill` audit row either way: granted true with the catalogue version and the grant's counts, or granted false with the recorded catalogue version and no counts (an account's current holdings are not the grant). A suspended account is backfilled too.
- It prints `accountId=`, `starterGranted=granted|already`, `catalogueVersion=` (the grant's, or the recorded one when already granted), then `operatives=`, `instances=` and `stacks=` only for a grant, and `correlationId=`. Exit codes are the CLI's: 0 ok (including `already`), 3, 64, 65 (no such account), 67, 69, 75. An invalid or missing catalogue refuses this verb and `account create` with exit 4, writing nothing. The account-security verbs (`credential rotate`, `credential revoke`, `sessions revoke`, `account suspend`, `account reactivate`) never load the catalogue, so they still work when a build ships with a bad one.

**Schema objects** (`0003`, `0004`): `operatives`, `operative_choices`, `inventories`, `item_instances`, `item_stacks`, the trigger `inventories_forward_only`, and the extended `operator_actions` action list. Invariants: `db/invariants/0004_inventory_consistency.sql` (one row per violation).

## Inventory operations (Stage 5)

Schema version 5 (`0005_inventory_edits`). Owner rulings S5-D1–S5-D8 (spec revision 2.5, §0.5).

**Route.** `POST /v1/inventory/operations {expectedVersion, operations:[…]}` → `{version, inventory}` (`inventory` is shaped like `GET /v1/inventory`). Session only, per-account rate limit, `Cache-Control: no-store`, the uniform 401. No member names an account.

| Operation | Members (exactly) |
|---|---|
| `equip`, `equipFromStash` | `operativeId`, `itemId` |
| `unequip` | `operativeId`, `slot` (`weapon`, `armor` or `utility`) |
| `toStash`, `toBag` | `operativeId`, and either `itemId` or `definitionId` plus `quantity` |

Ids are canonical Base58 strings; numbers are strict; an unknown or duplicated member is refused.

| Status | Body | When |
|---|---|---|
| 200 | `{version, inventory}` | the whole batch applied; the version went up by exactly one |
| 400 | `{code:"InvalidBody"}` | malformed JSON, a missing, unknown or duplicated member, a wrong type, an empty `operations` list |
| 400 | `{code:"TooManyOperations"}` | more than `BLACKGLASS_INVENTORY_MAX_OPERATIONS` (default 32) |
| 400 | `{code:"InvalidOperation", index}` | an operation with the wrong members, an unknown `op`, a bad slot or a non-canonical id |
| 413 / 415 | empty | a body over `BLACKGLASS_INVENTORY_MAX_BODY_BYTES` (16 KiB) / a non-JSON content type |
| 409 | `{code}` | `NotProvisioned`, `InventoryLocked`, `VersionMismatch` (checked in that order under the row lock) |
| 422 | `{code, index}` | the first refused operation: `UnknownOperative`, `NotFound`, `UnknownItem`, `NotEquippable`, `NothingEquipped`, `Equipped`, `DestinationFull`, `InvalidQuantity`, `InsufficientQuantity` |
| 500 | empty | a broken invariant (a bug) or a stack merge past 2,147,483,647 units: rolled back, one token-free log line on `Blackglass.Inventory` |
| 503 | empty | the database is unavailable, or busy (with `Retry-After`) |

**Rules.**
- Five operations only; moves between operatives go through the stash in one batch. Each operation sees the state the earlier ones left; the final state is validated (equipment consistency, one item per slot, no zero stacks, a bag grows only within the catalogue's `inventory.bagCapacity`, units and instance ids conserved).
- **Exact and atomic:** an operation moves exactly its quantity or the batch is refused, nothing written, version unchanged. Unity's offline partial transfers are deliberately not ported (they are unchanged in the game).
- Stacks have no ids: a stack operation names a location and a definition; part of a stack splits it, arriving units merge. Bag entries are `ceil(quantity / maxStack)` per definition.
- **Retired items** (definition no longer in the catalogue) can be unequipped and moved to the stash, never equipped or moved back to a bag (`UnknownItem`); their ids, origin and quantities are kept.
- **Information safety:** a foreign, nonexistent or misplaced item id is the same `422 NotFound`, byte for byte; a foreign or nonexistent operative the same `UnknownOperative`. Every statement is scoped to the session's account.
- **No replay.** The server never refreshes the version and re-applies a batch, and never retries one after a busy or unavailable database. A client re-reads `GET /v1/inventory` and decides.
- **Locks:** `accounts` `FOR KEY SHARE` (so the stack insert's foreign-key check never takes it after the next lock), then `inventories` `FOR UPDATE`, then owned rows. An edit can wait behind an operator lifecycle command on the same account, bounded by `lock_timeout` (503).
- **`InventoryLocked`** comes from `ParticipationLock`, the only `IInventoryLock`: a mandatory query, under the inventories row lock, for a participation of the account in an active state (`admitted`, `joined`, `left`, `result_pending`, `unresponsive`, `quarantined`; the `one_active_participation` predicate). Only a terminal transition (`released`, later `settled`) unlocks. Picks and `GET /v1/inventory`'s `locked` use the same query (`GET /v1/roster` has no `locked` field). It fails closed: a failed query is a 500 and a timed-out one a 503, never unlocked (S7-D4).

**Starter grant versus current holdings.** The `inventories.starter_*` columns, each instance's `origin`/`origin_ref` and the `account_create`/`starter_backfill` audit rows record the original grant and never change; instance locations and stack quantities are current holdings. No output labels current holdings as the starter.

**Contract vectors.** `contracts/inventory/vectors.json`: `shared` cases run in xUnit and in Unity EditMode against `InventorySession`; `backend` cases (strict quantities, partial room, unknown operatives on transfers, retired items, over-capacity bags) run in xUnit only.

## Energy accounting (Stage 6)

Schema version 6 (`0006_energy`). Owner rulings E1–E7 (spec revision 2.6, §0.6). Accounting only: no admission, reservations, charges, refunds or insurance.

**Model.**
- `energy_policies`: immutable, versioned. PostgreSQL mints `version` and sets `effective_at` itself (`greatest(clock_timestamp(), previous + 1 µs)`, under the publication lock). Version 1 is seeded: cap 50, opening balance 50, 1 per 10 minutes, void refund 100 % (all provisional).
- `energy_wallets`: one per account, with `balance` (≥ 0, may exceed the cap), `regen_anchor` (time accounted up to; never moves back), `regen_progress_us` (the partial interval, below the policy's interval) and `policy_version` (never moves back).
- `energy_ledger`: append-only, even for the owner (UPDATE, DELETE and TRUNCATE are refused).
  - Every entry continues its account's chain (`balance_after` = previous + `delta`; the first and only first entry is `open`).
  - At commit, every wallet equals its latest entry, so the balance is always the sum of the deltas.
  - Keys:
    - `open`;
    - `regen:<consumed anchor, µs since the epoch>`;
    - `dev_grant:<lower-case UUID>`, unique across all accounts.
- `anomaly_events`: one outstanding (unreviewed) row per `(kind, dedupe_key)`.

**Regeneration** is lazy, under the wallet row lock, from PostgreSQL's `clock_timestamp()` read after the locks.
- Time before a newer policy's `effective_at` is credited under the older one. Partial progress carries across proportionally (floored to the µs).
- At or above the cap, regeneration pauses and progress resets; a balance is never lowered.
- Materializing early or late gives the same result. Each gain writes one `regen` entry; a clock behind the anchor changes nothing.
- `GET /v1/me` projects the same computation without writing.

**Wallets.**
- Account creation opens a full wallet (the applicable policy's opening balance) in its own transaction, with an `open` entry; a failure rolls the account back.
- Accounts created before `0006` have no wallet until an operator runs `energy open`. Until then `GET /v1/me` shows `energy: null`, never zero, and nothing creates one implicitly.

**Operator CLI** (each needs `--operator`/`--reason`; `open`, `grant` and `publish` show their target and need `--yes` or typing the Base58 id, or `publish`):
```
… admin energy open --account-base58 <id>
… admin energy grant --account-base58 <id> --amount <n> --idempotency-key <uuid>
… admin energy policy publish --cap <n> --opening-balance <n> --interval-seconds <s> --amount <n> --void-refund-pct <p>
… admin energy reconcile
```
- **`open`** prints `energyOpened=opened|already`, `balance=` and `policyVersion=`, plus `advisory=true` when already opened (an unlocked read).
- **`grant`** prints `amount=`, `balanceAfter=`, `regenerated=` and `replayed=true|false`.
  - A grant is idempotent per key. The same key with the same account and amount replays the stored result and credits nothing; any other use is refused (66) with nothing written.
  - The amount is 1..`BLACKGLASS_ENERGY_GRANT_MAX` for a new key (below 1 is refused before connecting; above the maximum inside the transaction, 64, nothing written). A replay is matched before the limit and writes nothing, not even an audit row, so rerunning a grant after the maximum was lowered still replays it.
  - A grant may exceed the cap; a result above 2,147,483,647 is refused (64).
  - On an unknown outcome (69 or 1) it says to retry with the same key, never a new one. The granting audit row records the key.
- **`policy publish`** prints `policyVersion=`, `effectiveAt=` and the values.
  - The confirmation happens before its transaction. It holds the exclusive publication lock only for the insert and the audit.
  - Account creation, materializations and `GET /v1/me` wait behind it, bounded by `lock_timeout`.
- **`reconcile`** prints `walletsChecked=`, `mismatches=`, `anomaliesRecorded=` and one `mismatch=<Base58>:<rule>` per finding. It exits 0 even with mismatches.
- Exit codes as above: 65 no wallet (with a hint to run `energy open`) or no account; 64 invalid amount, policy or limit; 75 busy, nothing changed.

**Reconciliation** compares every wallet with its ledger in one read-only snapshot, using the six rules of `db/invariants/0006_energy_ledger.sql`.
- It records `energy_ledger_mismatch` anomalies keyed `<Base58>:<rule>`; the numbers go in `detail`. It never corrects a balance, and concurrent sweeps record one outstanding row per finding.
- It runs every `BLACKGLASS_ENERGY_RECONCILE_SECONDS` in the API process, the first sweep one interval after start (startup never waits), and on demand through `admin energy reconcile`.
- Failures are logged with the SQLSTATE only, and the next sweep still runs.

**Locks.**
- The order:
  - `energy grant`: `accounts` `FOR UPDATE` → `energy_wallets` `FOR UPDATE` → the policy advisory lock (shared) → clock;
  - `energy open`: `accounts` `FOR UPDATE` → the policy lock → clock → wallet `insert … on conflict do nothing` (no wallet row lock; an existing wallet is read unlocked, hence `advisory=true`);
  - account creation: new `accounts` row → `inventories` → the policy lock → wallet insert;
  - publication: the policy lock (exclusive) only;
  - `GET /v1/me`: the policy lock, then one statement;
  - reconciliation: no advisory lock; its anomaly write takes `accounts` `FOR KEY SHARE` (foreign key), ascending, all of a sweep's anomalies in one transaction.
- After the policy lock a transaction waits only on its own rows, policy rows, or a concurrent first use of one grant key (each grant inserts exactly one global key).
- Every energy transaction is READ COMMITTED; no energy transaction is ever retried automatically.
- A 23514 from an energy constraint is a bug (500, or exit 1), never "busy".

**Rate limits behind the tunnel.** Every request reaches the container from the Docker bridge address, so no limit is per IP. Login is limited per presented key, plus a global window that fabricated keys can't get around. Authenticated requests are limited per account. Per-IP limits wait for Phase 14's HAProxy.

**Admin loopback.** For the admin routes, "loopback only" means the API's own `127.0.0.1:5080` publication, plus the off-by-default flag, the admin key and the rate window. Inside the container every request arrives from the bridge gateway, so an in-app address check would prove nothing.

## Insurance, mission participants and admission (Stage 7)

Schema version 7 (`0007_attempts_and_insurance`). Owner rulings S7-D1–S7-D9 (spec revision 2.7, §0.7). **There is no gameplay, claim, ticket, start charge, settlement or payout:** energy is reserved at deployment, and the `reserved → charged` transition is Stage 8's.

**Routes** (session only, `Cache-Control: no-store`, the uniform 401; no request carries an account id):

| Route | Answer |
|---|---|
| `POST /v1/attempts`, header `Idempotency-Key: <uuid>`, body `{missionTemplate, operativeIds:[Base58…]}` (exactly these members; order kept and meaningful; no duplicates; 1..16 ids) | `201 {attemptId, state:"admitted", energy:{cost, balanceAfter}, insurance:{covered, policyId, policyVersion}}` |
| `GET /v1/attempts/{attemptId}` (canonical lower-case UUID) | `200 {attemptId, state, participantState, deadlines:{assign}, energy:{amount, state}, insurance:{covered, policyId, policyVersion}}` |
| `POST /v1/attempts/{attemptId}/cancel` (before start only) | `200 {participantState:"released", energy:{released, balanceAfter}}`; a repeat returns the stored outcome |

**Deploy responses:**
- **Refusals:**
  - 400: `InvalidBody`, `DuplicateOperative`, `InvalidOperativeId`, `InvalidIdempotencyKey`.
  - 413 over `BLACKGLASS_DEPLOY_MAX_BODY_BYTES`; 415.
  - 409: `IdempotencyKeyReused` (same key, different normalized body; nothing written), `NotProvisioned`, `AttemptActive`, `InsufficientEnergy` (also for a positive cost with no wallet).
  - 422 `{code, index?}`: `UnknownTemplate`, `UnknownOperative` (a foreign id is the same answer), `OperativeCount`, `RetiredOperative`, `UnknownItem`.
  - 503 `ServiceUnavailable` with `Retry-After` (no verified capacity; nothing locked or written); busy is 503 as elsewhere.
- **Replay.** The same key and body return the recorded response byte for byte, even after the attempt has been released (it still says `admitted`: it is the admission's record). There is no second reservation.
- **Body format.** Bodies are stored as `jsonb`, so their member order is PostgreSQL's (JSON objects are unordered).
- **Zero-cost templates** report `balanceAfter: null`, and so does a zero-amount cancel.

**Admission** (one READ COMMITTED transaction):
1. The early idempotency lookup.
2. Service health, before any lock: at least one enabled, undrained host with a fresh heartbeat (`BLACKGLASS_HOST_LEASE_SECONDS`) and capacity. A `dev_fixture` host counts only when `BLACKGLASS_DEV_HOSTS=enabled`.
3. `accounts` `FOR KEY SHARE` → `inventories` `FOR UPDATE`.
4. The idempotency re-read.
5. `InventoryLocked`/`AttemptActive`.
6. Template and operatives:
   - owned, ordered, within the template's count;
   - definitions and items known to the catalogue;
   - choices by ordinal.
7. Energy, for a positive cost only: wallet `FOR UPDATE` → materialize (Stage 6) → check → reserve. A zero-cost template takes no energy path and writes no ledger entry.
8. Inserts: attempt (UUIDv7; a CSPRNG seed and 32-byte retention salt, never in any response, log or audit) → participant (the immutable loadout snapshot: the selected operatives in the requested order, with owned id and definition id, advancement, bags and equipment, account, inventory version and catalogue version; no stash) → hold → `reserve:<attempt>` entry → insurance snapshot (complete terms, if the coverage is active at admission's database `now`) → the recorded response.

One active participation per account is enforced by the partial unique index `one_active_participation`.

**Release** (pre-start cancel, or the assignment-deadline worker):
- `accounts` `FOR KEY SHARE` → `mission_attempts` → `attempt_participants` → `energy_holds` → `inventories` → (positive amount only) wallet → the energy policy lock. The first step exists because the release's second wallet write re-checks the wallet's account foreign key; taken late, that check could deadlock with a same-account grant.
- It releases once: the `release:<attempt>` entry (+cost, may exceed the cap) and three guarded single-row transitions. A later state is never cancelled.
- The admission and insurance records remain.

**Worker.** The assignment-deadline worker runs in every `serve` process every `BLACKGLASS_ATTEMPT_SWEEP_SECONDS`. It cancels `admitted` attempts past `assign_deadline` (`no_capacity`) with `FOR UPDATE SKIP LOCKED`, and its first sweep comes one interval after start. Until Stage 8 claims attempts, every admitted attempt is released after the deadline.

**`InventoryLocked`** is now a mandatory, fail-closed participation query (`ParticipationLock`) for edits, picks and `GET /v1/inventory`'s `locked`. The always-unlocked fallback is deleted.

**`/v1/me`** adds `activeAttemptId` (or null) and `insurance: {active, policyId, expiresAt}` (`active` at the read's database `now`), read in the same single statement as the energy.

**Insurance:**
- Versions are immutable; `standard` v1 is seeded.
- **Activation:** the same policy extends to `max(expires, now) + duration`. A different policy while active is refused (`CrossTierDeferred`). The key is honoured once; a replay writes nothing.
- The admission snapshot is immutable and outlives the coverage. There is no payout in Stage 7.

**Operator CLI** (audited; confirmed, except `host heartbeat-dev`; never load the catalogue):
```
… admin insurance policy publish --policy-id <id> --terms-file <path>
… admin insurance activate --account-base58 <id> --policy-id <id> --duration-seconds <s> --idempotency-key <uuid>
… admin mission template publish --template-id <id> --energy-cost <n> --min-operatives <n> --max-operatives <n>
… admin host register-dev --host-id <id> --capacity <n>        (only with BLACKGLASS_DEV_HOSTS=enabled)
… admin host heartbeat-dev --host-id <id>                      (only with BLACKGLASS_DEV_HOSTS=enabled)
```
- Templates: `procedural-standard` v1 is seeded (cost 10, 1–3 operatives; provisional). Zero cost is allowed. A new version is a new row.
- `BLACKGLASS_DEV_HOSTS` is for isolated tests and the throwaway smoke stack only. Its compose default is `disabled`, `serve` warns when it is enabled, and it must never be set in production.

## Hosts, claim, process sessions, tickets and the start commit (Stage 8)

This section covers schema version 8 (`0008_hosts_sessions_tickets`), under owner rulings B1–B13 and design corrections R1–R7 (spec revision 2.8, §0.8). The attempt lifecycle now runs up to `running` over HTTP.

**Not in Stage 8:** settlement, voids, refunds, payouts, the agent, Unity code and deployment.

**Credentials.** Each is 256 bits, stored as SHA-256 only, and shown once:
- `hs1_`: the host secret, printed by the CLI at registration;
- `pc1_`: the process credential, in the claim response;
- `jt1_`: the join ticket, in the mint response.

**Schemes.** There are three authentication schemes, selected by the token's prefix. Every route requires exactly its own, and any other credential gets the uniform 401.
- **Bearer `at1_`:** the `/v1/*` routes.
- **Host `hs1_`:** the `/agent/v1/*` routes. Only an enabled agent host's current secret is accepted.
- **Process `pc1_`:** the `/server/v1/attempts/{id}/*` routes.
  - Accepted only on its own attempt's routes, and only until that attempt is `cancelled` or `settled`. The heartbeat has no exception (R4).
  - A database outage answers 503, never 401.

**Agent routes:**

| Route | Answer |
|---|---|
| `POST /agent/v1/heartbeat`, body `{buildRevision, catalogueVersion, inventory:{complete, units:[{attemptId, activeState}]}, outboxPending}` | `{drained, stop:[attemptId]}`. Only a `complete` inventory frees the slots of ended attempts that it no longer lists (R2). `stop` lists ended attempts whose units remain |
| `POST /agent/v1/claim` with `Idempotency-Key`, body `{}` | `200 {attemptId, missionPort, processCredential, credentialGeneration, launchDeadline, recovered, participants}` (Stage 10, A10-D2: `participants` lists the attempt's participant accounts as canonical Base58, immutable for the attempt, the agent's roster for complete exit reports), or `204` after the long poll. Errors: `409 HostUnavailable`, `InvalidState`. Retrying the key while the attempt is still `assigned` is a **recovery**: the same attempt with a new credential and the next generation (R1) |
| `POST /agent/v1/attempts/{id}/launching`, body `{credentialGeneration}` | `{state}`. Errors: `409 CredentialSuperseded`, `InvalidState`; `404` |
| `POST /agent/v1/attempts/{id}/exited`, body `{exitCode, signal, results:[{accountId, present}]}` | `{applied: "cancelled" \| "none"}`. Stores the report as evidence and frees the slot. Before any start it also cancels and releases. An identical repeat gets the same answer; a different report is `409 ConflictingReport` |

**Process routes:**

| Route | Answer |
|---|---|
| `POST …/sessions`, body `{buildRevision, catalogueVersion}` | `{processSessionId, seed, missionTemplate:{id, version}, catalogueVersion, participants:[{accountId, loadout}], deadlines:{launch}}`. The only response that carries the seed. Error: `409 BuildNotAllowed` |
| `POST …/ready`, body `{processSessionId, layoutHash, lootTableVersion, lootHash, objectives, lootPool}` | `{joinDeadline}`. A pool outside the table, or a `lootHash` that isn't its canonical hash, is `422 InvalidLootPool` and cancels the attempt |
| `POST …/heartbeat`, body `{processSessionId, phase, participants:[{accountId, connected}]}` | `{}`. Renews the lease, turns `unresponsive` back to `running`, and moves started participants between `joined` and `left` |
| `POST …/tickets/redeem`, body `{processSessionId, ticket}` | `{accountId, operativeIds}`. Single use; a repeat by the same session gets the same answer |
| `POST …/participants/{account}/start`, body `{processSessionId}` | `{started:true, startedAt}`. Charges exactly once. A repeat returns the stored start, even after the join deadline. Errors: `409 NotRedeemed`, `Cancelled`, `DeadlinePassed`, `WrongSession` |
| `POST …/failed`, body `{processSessionId, reason}` | `{applied}`: stores the report; before any start it also cancels |

**Client routes:**
- `POST /v1/attempts/{id}/tickets`, body `{}`, returns `201 {ticket, ticketExpiresAt}` and revokes the participant's earlier live tickets.
- `GET /v1/attempts/{id}` adds `deadlines.launch` and `deadlines.join`, and `join:{hostId, port}` once the attempt is ready or running. It never mints a ticket.
- `cancel` now works in every pre-start attempt state, for this participant only. The attempt is cancelled when no participant remains.

**Operator CLI.** Every verb is audited and confirmed (the confirmation word is noted below; `--yes` also confirms), and none loads the catalogue:
```
… admin host register --host-id <id> --capacity <1-64> --port-base <port>      (type: register; prints hostSecret once)
… admin host rotate-secret --host-id <id>                                     (type the host id; prints the new secret once)
… admin host drain|enable|disable --host-id <id>      … admin host capacity --host-id <id> --capacity <1-64>
… admin build allow --revision <r> --catalogue-version sha256:<hex> --templates <id>:<v>[,…]   (type the revision)
… admin build disable --revision <r> --catalogue-version sha256:<hex>
```

**Settings.** All provisional; leaving one empty uses its default:

| Setting | Default |
|---|---|
| `BLACKGLASS_LAUNCH_DEADLINE_SECONDS` | 120 |
| `BLACKGLASS_JOIN_DEADLINE_SECONDS` | 120 |
| `BLACKGLASS_PROCESS_LEASE_SECONDS` | 30 |
| `BLACKGLASS_TICKET_SECONDS` | 120 |
| `BLACKGLASS_CLAIM_WAIT_SECONDS` | 25 |
| `BLACKGLASS_CLAIM_POLL_MS` | 1000 |

The accounts PostgreSQL runs with `log_error_verbosity=terse` (B2). This takes effect at the next deployment the owner approves.

**Drain versus disable (R8).**
- **`host drain`** is planned maintenance: no new claims, while the agent and its running processes keep working.
- **`host disable`** is emergency isolation, in one audited transaction:
  - the host gets no claims;
  - its secret is replaced by an unknown one and marked revoked;
  - every live attempt of the host gets `credential_revoked_at`, so its process credential is refused for good.

  Nothing is cancelled, released, voided or refunded, and no slot is freed. It prints `revokedCredentials=N`.
- **Recovery:**
  1. `host rotate-secret` issues a new secret.
  2. Install it for the agent.
  3. `host enable`, which is refused (exit 66) until step 1.

  Revoked process credentials never come back. Their running units must be stopped by the host's operator (Stage 10).
- **On exit 75** (a lock timeout), the disable isolated nothing: re-run it.
- **Stale secrets:** every agent route re-checks the presented secret inside its transaction. A request that authenticated just before a rotation or a disable writes nothing after it commits.

**Slots (B6).** A slot and its port stay occupied until an exit report or a complete inventory confirms that the process is gone. A database trigger refuses any other release of a live slot. A host whose lease lapses gets no claims and keeps its occupied ports.

## Settlement, quarantine and recovery (Stage 9)

Plan: `Docs/superpowers/plans/2026-10-17-phase-13-stage9-settlement.md` (revision 3). Migration `0009_settlement`, schema version 9.

- **Results:** the agent relays one format-v1 manifest per participant to `POST /agent/v1/results` (host secret). Answers: 200 `settled` or `duplicate`; 202 `quarantined` (with V-rule codes) or `held`; 409 with a code (`Conflict`, `AlreadySettledAsVoid`, `AttemptRevoked`, `WrongAttempt`, `InvalidState`, `SubmissionLimit`); 422 for a body that is not a manifest; 401 and 503 commit nothing. The format and its golden vectors are in `contracts/manifest-v1/` (regenerate with `python3 tools/manifest_vectors.py`; a change is a format change).
- **Receipts:** players poll `GET /v1/attempts/{id}/receipt` (bearer); 202 `{state}` until settled.
- **Recorded hash:** the process sends `POST /server/v1/attempts/{aid}/participants/{acc}/recorded {manifestHash}` (supporting evidence only).
- **Settings (provisional):** `BLACKGLASS_RESULT_SUBMISSIONS_MAX` (16), `BLACKGLASS_RESULT_UNIDENTIFIED_MAX` (64 per host per 24 h), `BLACKGLASS_QUARANTINE_ESCALATION_HOURS` (24), `BLACKGLASS_MANIFEST_MAX_ELAPSED_MS` (7,500,000), `BLACKGLASS_MANIFEST_ELAPSED_TOLERANCE_MS` (60,000), and the monitoring values below.
- **Catalogue history:** `serve` registers its catalogue at startup and exits 5 when it would change an item id's identity kind; retiring a definition is allowed. Every attempt is settled under the catalogue it was admitted with.

**Operator runbook** (`docker compose run --rm admin …`, each with `--operator` and `--reason`; changes are confirmed by typing the attempt id, host id, anomaly id or `import`, or by `--yes`, and audited in `operator_actions`):
- **Inspect:** `attempt show --attempt <uuid> [--submission <id> --raw]` lists states, submissions (pre- or post-revocation, matching the recorded hash or not), exit evidence, settlements and actions. `anomaly list [--open]`, then `anomaly review --anomaly <id> --note <text>` once handled.
- **A quarantined participant** (never cleared automatically): fix the cause, then `attempt revalidate --attempt <uuid> --account-base58 <id> --submission <id>`. If no stored submission can be accepted, check the host's outbox and relay state, then `attempt void-participant … --evidence <findings> --outbox-checked --revalidated` (every distinct stored manifest needs a failed revalidation recorded after it was stored; a failure only for `catalogue_unavailable` doesn't count). Revalidation is refused for a participant still in its mission (only a quarantined one, or one of a revoked attempt) and re-checks the process session.
- **A disabled host's (revoked) attempt:** retrieve the outbox file and `attempt import-result … --file <path> --evidence <text> --host-trust-reviewed`, then `attempt revalidate … --host-trust-reviewed`. If the result is verified unrecoverable: `attempt void-participant … --evidence <findings> --outbox-checked --host-trust-reviewed --unrecoverable [--revalidated]`. The host can then be rotated and re-enabled; it need not be declared lost.
- **Evidence beyond the submission cap:** `attempt import-result` (never counted against the cap).
- **A missing admission catalogue** (`held`): `catalogue import --file <catalogue.json> [--catalogue-version sha256:<hex>]`, then `attempt revalidate`. Only a result settlement needs the admission catalogue: an exit report, an exit-evidence void, an operator void and `host declare-lost` never do, even for an attempt admitted before schema 9 under a catalogue the history never received (new attempts are checked against the history when inserted). If a catalogue was lost, re-export it from the Unity commit that produced it: `catalogue import` verifies its hash.
- **Confirmed permanent host or outbox loss:** `host disable`, wait 10 minutes without heartbeats, then `host declare-lost --host-id <id> --evidence <text> --outbox-unrecoverable`. It is permanent (a recovered machine registers under a new host id) and voids that host's started, unsettled, unquarantined participants; a re-run resumes.
- **Monitoring** (in the deadline worker; alerts only, never a void, release or refund): `quarantine_escalation` every `BLACKGLASS_QUARANTINE_ESCALATION_HOURS`; `unresponsive_attempt` every `BLACKGLASS_UNRESPONSIVE_ALERT_SECONDS` (600) of silence, counted from the last process heartbeat or, without one, the start; `revoked_attempt_unsettled` every `BLACKGLASS_REVOKED_ALERT_HOURS` (24); `ending_attempt_stalled` once an attempt has been in `ending` for `BLACKGLASS_ENDING_ALERT_SECONDS` (600), then every `BLACKGLASS_ENDING_ALERT_REPEAT_SECONDS` (3600) while it stays there, counted from the database's `ending_at` and carrying the host, the elapsed time and the unsettled participants (elapsed time alone proves nothing: no state changes). The auto-drain drains (never disables) a host with `BLACKGLASS_AUTO_DRAIN_ATTEMPTS` (3; 0 turns it off) distinct attempts voided by exit evidence within `BLACKGLASS_AUTO_DRAIN_WINDOW` (60 minutes), counting only voids after the host's last `host enable`, so a re-enable sticks.
- Exit codes: 64 a missing attestation or confirmation, 65 not found, 66 not allowed, 75 busy.
- **Replays:** a byte-identical or re-encoded retry gets its stored answer (past the submission cap too, without storing another copy), and `duplicate` once its manifest is the settled one (also after an operator revalidation). A result for a revoked attempt is refused (`409 AttemptRevoked`) before any replay answer; an attempt that settled before a disable was never revoked, so its settlement's answer stands.
- **Crash drill** (`tools/vm-crash-drill.sh`, on bg-dev-services01): passes only when the throwaway database container was verifiably killed (its id, exit 137) and restarted (a new start time) and the driver saw PostgreSQL restart; `--negative-skip-kill` must fail with 80. Cleanup also runs after a failed command, SIGINT, SIGTERM or SIGHUP on the VM, and within seconds of the ssh session ending (a local Ctrl-C or a dropped connection: a watchdog on the VM notices the session is gone).

## The mission host agent (Stage 10)
- `src/MissionHost.Agent` is the agent (`blackglass-agent run|status|emergency-stop`): .NET 10, self-contained `linux-x64`. It talks to the API over HTTP only (the loopback tunnel, `http://127.0.0.1:5081/`) and references only `Accounts.Domain`.
- **Tests:**
  - `tests/MissionHost.Agent.Tests` (no database): `tools/vm-test.sh --agent [filter]`, also part of `--full`;
  - `tests/Accounts.Integration.Tests/Agent` drives the agent against the real API.
- `src/MissionHost.CredentialBroker` (`blackglass-credential-broker`, security correction I3) is the only agent-facing code that runs as root. It is socket-activated per connection, has no project reference, and writes `/run/blackglass/credentials/<attempt>/process` for the mission unit's `LoadCredential`. Its tests are in `tests/MissionHost.Agent.Tests` (`CredentialBrokerTests`, `CredentialClientTests`).
- **Mission units (security correction C1):** one template per host slot, `blackglass-mission-NN@<attempt>.service` (`NN` = mission port minus `portBase`), each running as that slot's systemd dynamic user, with systemd's private per-unit outbox. The agent configuration's `portBase` and `localCapacity` must match the host's registration (`port_base`, `capacity` no larger).
- **Deployment** (`deploy/mission/`): the slot template and its renderer, the broker's socket and service, the agent unit, the polkit rule, users, directories, the root exit recorder and slot guard, the stub mission, the tunnel files, and the publish, install, verify and uninstall scripts. See `deploy/mission/README.md`. **Not installed** until the owner authorizes the Stage 10 infrastructure checkpoint; validated in a disposable systemd container.

## Stage 13 CP4: the Editor end-to-end run (owner-approved runs only)
The real API in a test process on the PC (127.0.0.1, an ephemeral port), one disposable database on the test cluster through the tunnel, and the Unity Editor's `-attempt` server as the mission process. Plan: `Docs/superpowers/plans/2026-10-23-phase-13-stage13-cp4-environment-manifest.md`.
- **Pieces:**
  - `tests/Accounts.Integration.Tests/E2E/BackendE2EHost.cs` plays the operator (seeding through the admin stores), the player (dev login, deploy, ticket, receipt), the agent (heartbeat, claim, launching, the relay to `POST /agent/v1/results`, exited), and the network between the server and the API (`LostResponseProxy`). Scenario A loses the first start answer after the backend committed it; scenario B stops the API after the start and restarts it once the process has published.
  - `Assets/_Project/Tests/PlayMode/BackendE2EPlayTests.cs` (`[Explicit]`) runs the server with the real `UnityWebRequest` transport and asserts the hold, the outbox order and the exit.
  - Both are opt-in: the host is skipped without `BLACKGLASS_E2E=1`; the Unity test ignores itself without `BLACKGLASS_E2E_DIR`.
- **The safe database path** (`Support/Cp4Database.cs`, not `TestCluster`): it checks the cluster's identity first (`cluster_name = blackglass-test`, PostgreSQL 17, the expected `system_identifier`, the superuser) and the two application roles. Then it creates one `bg_t_<time>_cp4_<hex>` database and writes its name and OID to `cp4-database.json`. It drops only a database matching both. It never removes other runs' databases and never creates or changes a role. `Unit/Cp4DatabaseSafetyTests` covers this without a database.
- **Procedure** (from `Backend/`; plain `dotnet` may need the CET wrapper on the owner's PC):
  1. Check nothing listens on `127.0.0.1:55432`, then open a tunnel used only by this run: `ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:55432:127.0.0.1:55432 <the services admin alias>`.
  2. Read the baseline (read-only): `BLACKGLASS_E2E=1 BLACKGLASS_CP4_BASELINE_OUT=<new file> dotnet test tests/Accounts.Integration.Tests --filter "FullyQualifiedName~Cp4_read_only_cluster_baseline"`. Take `identity.systemIdentifier` from it.
  3. Start the host: `BLACKGLASS_E2E=1 BLACKGLASS_E2E_DIR=<new private directory outside the repository> BLACKGLASS_CP4_SYSTEM_ID=<id> dotnet test tests/Accounts.Integration.Tests --filter "FullyQualifiedName~Cp4_a_lost_start_answer"`.
  4. Once `<dir>/host.json` exists, run Unity: `BLACKGLASS_E2E_DIR=<dir> Tools/run-tests.sh PlayMode "Blackglass.Tests.BackendE2EPlayTests"`.
  5. Keep `<dir>/evidence/`, which is redacted: the cluster baselines and differences, the transcript, the scenarios' database rows, receipts, hashes and the relay log. Then delete `<dir>`, repeat step 2 to a new file (no `_cp4_` database may remain), and close the tunnel.
- **Never:** `testdb.sh up/down/destroy`, `vm-sync.sh`, `vm-test.sh` or Docker for this run; a VM change; the installed accounts API or agent.

## Restore limits
Database-level privileges (`CONNECT`, `TEMPORARY`) are not inside a `pg_dump`. A real restore into a new cluster re-runs `initdb/00-roles.sh` first. Stage 17 and Phase 14 write the full recovery runbook.

## Required follow-ups (from the Stage 1 review)
- **Function privileges. Done in `0001_accounts_and_auth` (Stage 3), with `AccountsSchemaTests.Functions_the_migrator_creates_later_are_not_executable_by_the_app_or_public_until_granted`.** The original note follows. `0000_foundation` is applied and must not change. The first forward migration that suits it, and in any case before the first function or procedure is created (the stage plan's energy regeneration function is the first one planned), must run `alter default privileges for role accounts_migrator revoke execute on functions from public;` (global, with no `in schema` clause: a per-schema revoke can't remove PostgreSQL's built-in global `PUBLIC` `EXECUTE` default), grant `EXECUTE` explicitly only on the functions `accounts_app` needs, and come with a privilege test showing `accounts_app` can't execute an ungranted function the migrator creates.
- **Image pinning.** `postgres:17`, `mcr.microsoft.com/dotnet/sdk:10.0` and `mcr.microsoft.com/dotnet/aspnet:10.0` are floating tags. Before any stack holds persistent application data, pin each to an approved version and digest (`image@sha256:...`) and record the approval; upgrade them deliberately, never as a side effect of a build.
- **Invariant gating tests. Holding with the real `0001` (`0001_revocations_cascade.sql`); the next-version probes are derived from the catalog (`Catalogs.NextVersionFile`).** When `0001` and later migrations add invariants, the upgrade tests and the drill test must keep showing that invariants numbered at or below the database's version run and that higher-numbered ones are skipped. They derive versions from the catalog or the backup (`UpgradeTests.Invariants_are_gated_by_their_version_prefix`, `drill-test.sh`); don't hard-code version numbers.

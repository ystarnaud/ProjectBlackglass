# Blackglass backend (Phase 13, Stages 1–6: foundation, catalogue, accounts and development authentication, operatives and inventory, inventory operations, energy accounting)

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
| `BLACKGLASS_ENERGY_GRANT_MAX` | 1–1000000; the largest single development grant (`admin energy grant`; also validated by `serve`) | 1000 |
| `BLACKGLASS_DB_STATEMENT_TIMEOUT_MS` | 1–600000, `statement_timeout` on the same connections (the migrator has neither: it sets its own `lock_timeout`) | 15000 |

**Routes.**
- `POST /v1/auth/dev/login {accountKey}` → `{accessToken, expiresAt, accountId}`, with `Cache-Control: no-store`.
- `GET /v1/me` → `{accountId, inventoryVersion, energy}` (Stage 4; `inventoryVersion` is the shared inventory version, null for an account without a starter). Stage 6: `energy` is `{balance, cap, nextRegenAt}` projected read-only (`nextRegenAt` UTC ISO-8601 with `Z`, null at or above the cap), or `null` for an account without a wallet, never zero.
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
- **Confirmation.** Every command but `create` first prints its target as `target.*` lines: the canonical Base58 id, status, handle, display name, creation time, live credential and live sessions, plus the credential for `credential revoke`. It then acts only when confirmed: either type that Base58 id at a terminal (run without `-T`), or add `--yes`. Without either it changes nothing. Running it without `--yes` and without a terminal is a dry run that shows the target.
- Exit codes:
  - 0: ok.
  - 1: unexpected failure.
  - 3: schema not current (the same policy as `serve`; nothing is written).
  - 4: the embedded catalogue is invalid (checked before the database; nothing is touched; every verb, as for `serve`).
  - 64: usage error, including a malformed id, or not confirmed with no terminal.
  - 65: not found.
  - 66: handle taken.
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
- **`InventoryLocked`** comes from a seam (`IInventoryLock`) that always answers unlocked until Stage 7 connects it to participation records; picks and `GET /v1/inventory`'s `locked` use the same seam.

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
  - The amount is 1..`BLACKGLASS_ENERGY_GRANT_MAX`. A grant may exceed the cap; a result above 2,147,483,647 is refused (64).
  - On an unknown outcome (69 or 1) it says to retry with the same key, never a new one. The audit row records the key.
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
  - operator verbs: `accounts` `FOR UPDATE` → `energy_wallets` `FOR UPDATE` → the policy advisory lock (shared) → clock;
  - account creation: new `accounts` row → `inventories` → the policy lock → wallet insert;
  - publication: the policy lock (exclusive) only;
  - `GET /v1/me`: the policy lock, then one statement.
- Every energy transaction is READ COMMITTED; no energy transaction is ever retried automatically.
- A 23514 from an energy constraint is a bug (500, or exit 1), never "busy".

**Rate limits behind the tunnel.** Every request reaches the container from the Docker bridge address, so no limit is per IP. Login is limited per presented key, plus a global window that fabricated keys can't get around. Authenticated requests are limited per account. Per-IP limits wait for Phase 14's HAProxy.

**Admin loopback.** For the admin routes, "loopback only" means the API's own `127.0.0.1:5080` publication, plus the off-by-default flag, the admin key and the rate window. Inside the container every request arrives from the bridge gateway, so an in-app address check would prove nothing.

## Restore limits
Database-level privileges (`CONNECT`, `TEMPORARY`) are not inside a `pg_dump`. A real restore into a new cluster re-runs `initdb/00-roles.sh` first. Stage 17 and Phase 14 write the full recovery runbook.

## Required follow-ups (from the Stage 1 review)
- **Function privileges. Done in `0001_accounts_and_auth` (Stage 3), with `AccountsSchemaTests.Functions_the_migrator_creates_later_are_not_executable_by_the_app_or_public_until_granted`.** The original note follows. `0000_foundation` is applied and must not change. The first forward migration that suits it, and in any case before the first function or procedure is created (the stage plan's energy regeneration function is the first one planned), must run `alter default privileges for role accounts_migrator revoke execute on functions from public;` (global, with no `in schema` clause: a per-schema revoke can't remove PostgreSQL's built-in global `PUBLIC` `EXECUTE` default), grant `EXECUTE` explicitly only on the functions `accounts_app` needs, and come with a privilege test showing `accounts_app` can't execute an ungranted function the migrator creates.
- **Image pinning.** `postgres:17`, `mcr.microsoft.com/dotnet/sdk:10.0` and `mcr.microsoft.com/dotnet/aspnet:10.0` are floating tags. Before any stack holds persistent application data, pin each to an approved version and digest (`image@sha256:...`) and record the approval; upgrade them deliberately, never as a side effect of a build.
- **Invariant gating tests. Holding with the real `0001` (`0001_revocations_cascade.sql`); the next-version probes are derived from the catalog (`Catalogs.NextVersionFile`).** When `0001` and later migrations add invariants, the upgrade tests and the drill test must keep showing that invariants numbered at or below the database's version run and that higher-numbered ones are skipped. They derive versions from the catalog or the backup (`UpgradeTests.Invariants_are_gated_by_their_version_prefix`, `drill-test.sh`); don't hard-code version numbers.

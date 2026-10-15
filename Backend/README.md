# Blackglass backend (Phase 13, Stages 1–3: foundation, catalogue, accounts and development authentication)

ASP.NET Core on .NET 10, PostgreSQL 17, Npgsql and Dapper. Every listener is loopback only; the VM is reached by SSH alias `bg-dev-services01-admin` (user `admin`, `sudo -n`; root SSH login is disabled).

## Layout
- `src/Accounts.Domain` the catalogue model, strict reader and validator (pure, no I/O). `src/Accounts.Persistence` DbSettings (password from a file) and the migrator. `src/Accounts.Api` the `serve`, `migrate` and `admin` commands, `GET /health` and the Stage 3 auth routes (exit codes: 0 ok, 1 failure, 2 `migrate` refused a drifted or ahead schema, 3 `serve` or `admin` refused a schema that is not current, 4 `serve` refused an invalid catalogue, 64 usage, 65 `admin` found no such account or credential, 66 `admin` found the handle taken).
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
- Format 1 holds ids and rules only (items, loot table, progression, operatives with role and ability ids, starter loadout); display text stays in Unity. Any other `formatVersion` is refused. Bag capacity is not part of format 1 (Stage 4/5 establishes its authoritative, versioned configuration).
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
   - `AccountsSchemaTests` pins the app's exact privilege set.
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

**Routes.**
- `POST /v1/auth/dev/login {accountKey}` → `{accessToken, expiresAt, accountId}`, with `Cache-Control: no-store`.
- `GET /v1/me` → `{accountId}`.
- `POST /v1/auth/logout` → 204. It revokes the current session only. A repeat gets the same 401 as any unknown token.
- `POST /dev/v1/accounts` with header `X-Admin-Key` and body `{handle?, displayName?, operator, reason}` → 201 `{accountId, credentialId, accountKey}`, with `X-Correlation-Id`.

**Failures.** Every authentication failure (bad, unknown, malformed, expired or revoked key or token, suspended account) is one 401: no body and `WWW-Authenticate: Bearer`. A database outage during authentication or login is 503, never 401.

**Secrets.** Account keys are `dk1_` and access tokens `at1_`, each followed by base64url of 32 random bytes. Only SHA-256 of the raw bytes is stored. A key is shown once, to the operator who issued it. Logs show at most an 8-hex hash prefix and name accounts by their Base58 id.

**Lifecycle.**
- Revocation is immediate: every request is checked in PostgreSQL, with no cache.
- Rotation revokes the old credential and its sessions and issues one new credential; the database allows only one live credential per account.
- Revoking a credential revokes its sessions.
- Suspension revokes every session. Reactivation restores none.
- Every lifecycle call locks the account row first, and login takes the same lock (`FOR SHARE`), so no ordering leaves a live session on a revoked credential or a suspended account.
- Each change writes an `operator_actions` row (operator, reason, correlation id, target account and credential) in the same transaction.

**Operator CLI** (on `bg-dev-services01`; `<id>` is always the Base58 account id the CLI prints):
```
sudo -n docker compose -p blackglass-accounts -f compose.yaml --profile tools run --rm -T admin account create [--handle <h>] [--display-name <name>] --operator <name> --reason <text>
… admin credential rotate --account <id> | credential revoke --credential <uuid> | sessions revoke --account <id> | account suspend --account <id> | account reactivate --account <id>   (each with --operator and --reason)
```
- `create` and `rotate` print `accountId=`, `credentialId=`, `accountKey=` (once) and `correlationId=`.
- Exit codes: 0 ok, 3 schema not current (the same policy as `serve`; nothing is written), 64 usage (including a malformed id), 65 not found, 66 handle taken, 1 failure.
- **Hazard:** a decimal id with no `0` and no leading `1` (for example `23456789`) is also valid Base58 for a *different* account. Always paste the Base58 id; every command echoes the account it acted on.

**Rate limits behind the tunnel.** Every request reaches the container from the Docker bridge address, so no limit is per IP. Login is limited per presented key, plus a global window that fabricated keys can't get around. Authenticated requests are limited per account. Per-IP limits wait for Phase 14's HAProxy.

**Admin loopback.** For the admin routes, "loopback only" means the API's own `127.0.0.1:5080` publication, plus the off-by-default flag, the admin key and the rate window. Inside the container every request arrives from the bridge gateway, so an in-app address check would prove nothing.

## Restore limits
Database-level privileges (`CONNECT`, `TEMPORARY`) are not inside a `pg_dump`. A real restore into a new cluster re-runs `initdb/00-roles.sh` first. Stage 17 and Phase 14 write the full recovery runbook.

## Required follow-ups (from the Stage 1 review)
- **Function privileges. Done in `0001_accounts_and_auth` (Stage 3), with `AccountsSchemaTests.Functions_the_migrator_creates_later_are_not_executable_by_the_app_or_public_until_granted`.** The original note follows. `0000_foundation` is applied and must not change. The first forward migration that suits it, and in any case before the first function or procedure is created (the stage plan's energy regeneration function is the first one planned), must run `alter default privileges for role accounts_migrator revoke execute on functions from public;` (global, with no `in schema` clause: a per-schema revoke can't remove PostgreSQL's built-in global `PUBLIC` `EXECUTE` default), grant `EXECUTE` explicitly only on the functions `accounts_app` needs, and come with a privilege test showing `accounts_app` can't execute an ungranted function the migrator creates.
- **Image pinning.** `postgres:17`, `mcr.microsoft.com/dotnet/sdk:10.0` and `mcr.microsoft.com/dotnet/aspnet:10.0` are floating tags. Before any stack holds persistent application data, pin each to an approved version and digest (`image@sha256:...`) and record the approval; upgrade them deliberately, never as a side effect of a build.
- **Invariant gating tests. Holding with the real `0001` (`0001_revocations_cascade.sql`); the next-version probes are derived from the catalog (`Catalogs.NextVersionFile`).** When `0001` and later migrations add invariants, the upgrade tests and the drill test must keep showing that invariants numbered at or below the database's version run and that higher-numbered ones are skipped. They derive versions from the catalog or the backup (`UpgradeTests.Invariants_are_gated_by_their_version_prefix`, `drill-test.sh`); don't hard-code version numbers.

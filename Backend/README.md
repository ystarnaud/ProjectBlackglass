# Blackglass backend (Phase 13, Stage 1: foundation)

ASP.NET Core on .NET 10, PostgreSQL 17, Npgsql and Dapper. Every listener is loopback only; the VM is reached by SSH alias `bg-dev-services01-admin` (user `admin`, `sudo -n`; root SSH login is disabled).

## Layout
- `src/Accounts.Persistence` DbSettings (password from a file) and the migrator; `src/Accounts.Api` the `serve` and `migrate` commands and `GET /health` (exit codes: 0 ok, 1 failure, 2 `migrate` refused a drifted or ahead schema, 3 `serve` refused a schema that is not current, 64 usage).
- `db/migrations/NNNN_lower_snake_name.sql` forward-only history; `db/invariants/` invariant queries.
- `deploy/services/` Compose stacks (`compose.yaml` accounts, `compose.test.yaml` test cluster), `initdb/`, `sql/`, VM scripts and `tests/`. `tools/` PC-side helpers. `tests/Accounts.Integration.Tests` xUnit.

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

## VM scripts (`deploy/services/`, run on `bg-dev-services01` with `sudo`; `tools/vm-sync.sh` copies HEAD there)
Order: `make-secrets.sh <dir>` (once), then `deploy.sh` = build, database up, stop the API (in-flight requests finish, up to 30 s), `backup.sh`, `migrate`, API up, health, `check-exposure.sh`. If the backup or the migration fails, the API stays stopped: restore from the backup or fix and deploy again. `serve` and `migrate` take no arguments (anything else is a usage error, exit 64); the API's listen address comes only from `ASPNETCORE_URLS`/`ASPNETCORE_HTTP_PORTS` or the loopback default.
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

## Restore limits
Database-level privileges (`CONNECT`, `TEMPORARY`) are not inside a `pg_dump`. A real restore into a new cluster re-runs `initdb/00-roles.sh` first. Stage 17 and Phase 14 write the full recovery runbook.

## Required follow-ups (from the Stage 1 review)
- **Function privileges.** `0000_foundation` is applied and must not change. The first forward migration that suits it, and in any case before the first function or procedure is created (the stage plan's energy regeneration function is the first one planned), must run `alter default privileges for role accounts_migrator in schema public revoke execute on functions from public;`, grant `EXECUTE` explicitly only on the functions `accounts_app` needs, and come with a privilege test showing `accounts_app` can't execute an ungranted function the migrator creates.
- **Image pinning.** `postgres:17`, `mcr.microsoft.com/dotnet/sdk:10.0` and `mcr.microsoft.com/dotnet/aspnet:10.0` are floating tags. Before any stack holds persistent application data, pin each to an approved version and digest (`image@sha256:...`) and record the approval; upgrade them deliberately, never as a side effect of a build.
- **Invariant gating tests.** When `0001` and later migrations add invariants, the upgrade tests and the drill test must keep showing that invariants numbered at or below the database's version run and that higher-numbered ones are skipped. They derive versions from the catalog or the backup (`UpgradeTests.Invariants_are_gated_by_their_version_prefix`, `drill-test.sh`); don't hard-code version numbers.

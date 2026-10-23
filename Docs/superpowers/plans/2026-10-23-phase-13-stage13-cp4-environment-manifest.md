# Stage 13 CP4: environment and cleanup manifest (prepared, not executed)

**Status (2026-10-23): authorized by the owner and run once; see the plan's "As built: CP4".** It ran with the owner's mandatory correction: a CP4-only safe path to the cluster (`Support/Cp4Database.cs`) instead of `TestCluster`, and a separate tunnel. The .NET host, not Unity, plays the player, the agent and the relay, and secrets stay in its memory instead of `e2e.json`. The text below is the prepared manifest as it stood before authorization. The plan is `2026-10-23-phase-13-stage13-backend-lifecycle.md` (Tasks 12–13).

## 1. Purpose

One Unity Editor end-to-end run of the Stage 13 process lifecycle against the **real** API and a **disposable** PostgreSQL database: claim → register → generate → ready → ticket (real, from the player route) → redeem → start → play to extraction → freeze → outbox (`.written` → `recorded` → `.ready`) → relay by a test-side agent → `exited` → receipt. Plus two variants: a lost start answer (charged once) and the API stopped during play (settled once after restart, no void).

## 2. Architecture (reuses the existing, approved test infrastructure)

| Piece | Where it runs | How |
|---|---|---|
| PostgreSQL 17 | `bg-dev-services01`, the **existing** `blackglass-test` cluster (Compose project `blackglass-testdb`, `127.0.0.1:55432` on the VM) | Unchanged. Never stopped, restarted or reconfigured. |
| The tunnel | Workstation → VM | The **existing** documented fallback: `bash Backend/tools/testdb.sh tunnel` (`ssh -N -L 127.0.0.1:55432:127.0.0.1:55432`, the admin alias already used for the integration tests). Only while CP4 runs. |
| The disposable database | The test cluster | One `bg_t_*` database created by the **existing** integration fixture (`Support/TestCluster`, `TestDatabase`): it refuses any cluster whose `cluster_name` isn't `blackglass-test`, creates only `bg_t_*` databases, migrates with the real migrator, and drops the database on dispose. |
| The real API | Workstation, in-process, loopback only | The **existing** `Support/ApiServer` (`ServeCommand.StartAsync`; it binds `http://127.0.0.1:0`, an ephemeral loopback port, which the E2E host writes to `e2e.json`), never a container, never published. |
| The E2E host | Workstation | **New (CP4 change 1):** one `[Trait("Requires","Postgres")]`, `[Trait("Category","BackendE2E")]` xUnit test in `Backend/tests/Accounts.Integration.Tests/Unit/` that, only when `BLACKGLASS_E2E=1`, provisions the database, seeds it through the real admin stores (below), starts the API on an ephemeral loopback port, writes that base URL into `e2e.json` and waits for a stop file (bounded, 30 min); on any exit it stops the API and the fixture drops the database. Its `Requires=Postgres` trait keeps it out of `UNIT`, and it returns at once without `BLACKGLASS_E2E=1`, so a `vm-test.sh` run never executes it. |
| The Unity side | Workstation, the Editor | **New (CP4 change 2):** `Assets/_Project/Tests/PlayMode/BackendE2EPlayTests.cs`, `[Explicit]`, `[Category("BackendE2E")]`, `Assert.Ignore` without `BLACKGLASS_E2E_CONFIG`. It plays the agent (heartbeat, claim, launching, relay of the `.ready` file, `exited`) and the player (dev login, deploy, ticket, receipt) over `System.Net.Http`, and runs the server in `-attempt` mode in the same Editor through the bootstrap's seams (credential directory, portable outbox; the real `UnityWebRequestTransport` to the base URL from `e2e.json`). |

Memory: one Unity Editor plus one `dotnet test` process (the API in-process); the two never run a test suite at the same time. Unity runs stay sequential.

## 3. Seed data (through the real admin stores, inside the disposable database only)

The E2E host calls the stores behind these admin verbs directly (the integration tests' way); the verb names and flags are listed so the operator can read each step as its audited CLI equivalent.

| Step | Store / verb (as `Accounts.Api admin`) | Values |
|---|---|---|
| Energy policy | `energy policy publish --cap --opening-balance --interval-seconds --amount --void-refund-pct` | provisional test values (cap 100, opening 50, interval 3600, amount 10, refund 100) |
| Mission template | `mission template publish --template-id procedural-standard --energy-cost 10 --min-operatives 1 --max-operatives 3` | the supported template (Stage 12: exactly `procedural-standard:1`) |
| Catalogue | registered at startup by `serve` (S9-B20 D7 rule) | the repository's `Backend/catalogue/catalogue.json` (equal to the Editor's build catalogue) |
| Account | `account create` (starter provisioned in the same transaction) | dev key captured to `e2e.json` only |
| Wallet | `energy open`, `energy grant --amount 50 --idempotency-key <uuid>` | |
| Host | `host register --host-id e2e-host --capacity 1 --port-base 7800` | the host secret captured to `e2e.json` only |
| Build (S13-D4) | `build allow --revision editor --catalogue-version <the repository catalogue's version> --templates procedural-standard:1` | **only** in this disposable database; never in production; no bypass of the allowlist |

Every step records operator `stage13-cp4` and reason "Stage 13 CP4 end-to-end (disposable database)" (the CLI equivalents take `--operator`, `--reason` and `--yes`).

## 4. Secrets

- `e2e.json` (account key, host secret, base URL) is written to the session's temporary directory **outside the repository**, mode 600 where the platform allows; its path is passed as `BLACKGLASS_E2E_CONFIG`. Nothing is written under `.claude/` or anywhere git can see.
- The claimed process credential goes to a temporary credentials directory outside the repository (the bootstrap's `CredentialsDirectory` seam), deleted at teardown.
- The test-cluster passwords stay where the existing tunnel runner keeps them (`~/.blackglass/testdb-secrets`); nothing new is created.
- The run's transcript is scanned (as in `BackendAttemptPlayTests`) for the credential, the ticket, the host secret, the account key and the seed; any match fails the run.

## 5. Procedure (each step only after the owner's go-ahead)

1. Confirm the test cluster is up (read-only: the existing integration fixture's `cluster_name` check) — **no `testdb.sh up`/`down`/`destroy`**.
2. Open the tunnel: `bash Backend/tools/testdb.sh tunnel` (left running in its own terminal).
3. Start the E2E host: `BLACKGLASS_E2E=1 dotnet test Backend.sln --filter "Category=BackendE2E"` from `Backend/`. On this workstation plain `dotnet` aborts on the CET quirk, so it runs through the untracked local wrapper `.claude/scratchpad/phase13/tools/dt.sh` (same compiler, wrapper only; not part of the repository). It prints the config path and "ready".
4. Run Unity: `BLACKGLASS_E2E_CONFIG=<path> Tools/run-tests.sh PlayMode "Blackglass.Tests.BackendE2EPlayTests"` (explicit tests run when named by filter).
5. Variants run in the same Unity session (the dropping proxy and the API stop/start are driven by the two test processes through the stop/pause files).
6. Stop the E2E host (stop file) and close the tunnel (Ctrl-C).

## 6. Cleanup and verification

| What | How | Verified by |
|---|---|---|
| The `bg_t_*` database | dropped by `TestDatabase` on dispose (also on failure) | a read-only query over the tunnel listing `bg_t_%` databases returns none from this run |
| The API | in-process; ends with the test process | the port in `e2e.json` closed (a refused connect) |
| The tunnel | Ctrl-C | no listener on `127.0.0.1:55432` |
| `e2e.json`, the credentials directory, the outbox directory | deleted by the tests' teardown | listed absent |
| The repository | no file written | `git status --short` unchanged except the owner's two settings files and `.claude/` |

**Never touched:** `bg-dev-mission01`, the Stage 10 agent and its tunnel (`5081`), the services VM's SSH configuration, Docker daemon, Compose stacks, the accounts stack, any other database, and the owner's settings files.

## 7. Exact changes CP4 needs (not made in Stage 13's authorized Tasks 0–11)

1. `Backend/tests/Accounts.Integration.Tests/Unit/BackendE2EHost.cs` (the E2E host above; test code only; no backend route, store, migration or parser change; `ApiServer` unchanged: the ephemeral port is read from the started app).
2. `Assets/_Project/Tests/PlayMode/BackendE2EPlayTests.cs` and a small test-only `LostResponseProxy` (a loopback TCP relay that drops one `start` answer), `[Explicit]`.
3. A README section in `Backend/README.md` for the procedure (§5).

## 8. Expected evidence

The transcript (no secrets), the receipt JSON (`GET /v1/attempts/{id}/receipt` 200), the attempt and participant rows (`settled`, one `charge:<attempt>` ledger entry), the variant results (one charge despite a lost start answer; a real outcome settled after the API restart, no void), and the cleanup checks of §6.

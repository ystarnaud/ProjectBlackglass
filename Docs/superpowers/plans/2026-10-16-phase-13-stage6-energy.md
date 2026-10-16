# Phase 13 Stage 6: energy accounting — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Date: 2026-10-16. **Revision 1, proposed. Status: plan only; not authorized, nothing implemented.** It needs the owner's rulings on §A (E1–E7) and the go-ahead before Task 0. Base: `main` @ `62cbfcf` (Stage 5 merged and pushed; schema version 5; backend ALL 1250/0, Domain 460 and Integration 790). Branch: `phase13-stage6`. Execution (proposed): subagent-driven, with three independent review checkpoints (after Task 2, after Task 6, and over the whole branch).

**Goal:** every account has a persistent energy wallet that the server alone changes. The balance regenerates lazily against PostgreSQL's clock up to a versioned policy cap, and each regeneration is recorded once in an append-only ledger. A periodic, read-only reconciliation compares every wallet with its ledger and records an anomaly on a mismatch, never correcting anything. Operators can open wallets, publish a new policy version and make idempotent development grants. `GET /v1/me` shows the projected balance without writing. Stage 6 builds the accounting only: admission, reservations, charges, releases, void refunds and insurance belong to later stages.

**Architecture:**
- **Domain (pure).** `Accounts.Domain/Energy` gains `EnergyPolicy`, `WalletState` and `EnergyMath`. `Materialize(wallet, policy, now)` returns the regenerated wallet, the amount gained and the anchor consumed. All arithmetic uses whole microseconds (`long`), the resolution of PostgreSQL's `timestamptz`. It never moves the anchor backwards and never lowers a balance.
- **Persistence.**
  - Migration `0006_energy` adds `energy_policies` (identity versions; version 1 seeded with the §19 values), `energy_wallets`, the append-only `energy_ledger` and `anomaly_events`, plus triggers and narrow privileges.
  - `EnergyProvisioner` opens a wallet inside a caller's transaction: it is written with an `open` entry, at account creation and by the operator's `energy open`.
  - `EnergyLedgerWriter.MaterializeLockedAsync` regenerates a wallet the caller holds `FOR UPDATE`: it updates the wallet and inserts the `regen` entry, keyed by the consumed anchor, in that same transaction. Later stages call it from their admission, start and settlement transactions.
  - `EnergyAdminStore` handles the operator verbs (open, grant, publish a policy), each auditing to `operator_actions` in its own transaction.
  - `EnergyReader` projects a wallet inside a read-only snapshot.
  - `EnergyReconciler` checks every wallet against its ledger in one snapshot and records deduplicated anomalies.
- **API.** `GET /v1/me` gains `energy: {balance, cap, nextRegenAt}` (null when the account has no wallet), read in the same snapshot as `inventoryVersion`. A hosted service in the API process runs the reconciliation periodically (E4).
- **Admin CLI.** `energy open`, `energy grant`, `energy policy publish` and `energy reconcile`.

**Tech stack:** as Stage 5 (ASP.NET Core minimal APIs on .NET 10, PostgreSQL 17, Npgsql 10, Dapper, xUnit against the real test cluster). No Unity change.

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` revision 2.5, §3, §6.1, §6.3, §15.1–§15.4, §16.1 (`GET /v1/me`), §16.4, §18.1 (regeneration and refunds above the cap; the regeneration ledger), §18.3, §19 and decision 049 (energy). It becomes revision 2.6 in Task 0, from the owner's rulings in §A. Stage plan: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` §Stage 6.

---

## A. Decisions for the owner (proposed; each has a recommendation)

| # | Question | Recommendation | Alternative | Cost if the recommendation is wrong |
|---|---|---|---|---|
| E1 | A new wallet's opening balance | **The cap of the policy it opens under** (50 today), written as one `open` ledger entry | Open at 0 and let it regenerate | One constant in `EnergyProvisioner`, plus test values |
| E2 | Which policy governs regeneration after a new version is published | **The latest version, from the wallet's next materialization**, including the partial interval already elapsed since its anchor. `policy_version` records the version last applied. A balance above a lowered cap is kept and simply stops regenerating | Each wallet keeps its recorded version until its next materialization, then switches (the elapsed part is credited under the old policy) | Rework of `MaterializeLockedAsync` and its policy read; no schema change |
| E3 | Wallets for accounts created before `0006` | **An explicit, audited `energy open --account-base58` operator verb**, idempotent like `starter backfill` (mirrors ruling D4). The migration writes no rows except policy version 1. New accounts get their wallet inside account creation's transaction | `0006` opens a wallet for every existing account | One data migration later (forward-only) |
| E4 | Where the periodic reconciliation runs | **Now, as a hosted service in the API process** (spec §2.2's worker), every `BLACKGLASS_ENERGY_RECONCILE_SECONDS` (default 3600, 0 disables), and on demand via `energy reconcile` | CLI and invariant query only now; the periodic run waits for Stage 7's worker | Removing one small hosted service |
| E5 | How a development grant is made | **Admin CLI verb only** (`energy grant`). The spec's `POST /dev/v1/energy/grants` waits for a stage that needs it over HTTP | The admin route as well | One admin route later |
| E6 | Whether a development grant may exceed the cap | **Yes**, as refunds will (decision 049). It is the only Stage 6 way to produce, and test, an above-cap wallet. One grant is 1..1,000,000; a result above 2,147,483,647 is refused (`BalanceLimit`) with nothing written | Clamp grants at the cap | One validation rule |
| E7 | Where the provisional values live | **`0006` seeds policy version 1** (cap 50, interval 10 min, amount 1, void refund 100 %, from §19). Later versions come from `energy policy publish`, append-only, audited; no code change needed | No seed: an operator publishes version 1 before any wallet can open | Account creation fails until a policy exists |

Two rules follow from Stage 5's rulings rather than being new decisions:
- **No automatic retry.** Deadlock, serialization, lock and statement timeouts are reported (exit 75 or 503) with nothing written; a grant's idempotency key makes the operator's retry safe. The §15.3 retry stays deferred.
- **Reads never write.** `GET /v1/me` projects; it never materializes (spec §6.1).

Numbering: the spec's §15.1 sketch bundles energy and insurance as one migration. Stage 6's `0006_energy` holds energy only; insurance, attempts and participants move to Stage 7's `0007` (the stage plan's Stage 7 line is corrected in Task 0).

---

## Global Constraints

- **Migrations:** forward-only, numbered, checksummed, one transaction each. **0000–0005 are applied and immutable.** Stage 6 adds `0006_energy.sql` only.
- **Ids:** account ids are `bigint` inside and Base58 outside; no request carries an account id (the session's account only); mission attempt ids stay UUIDv7 (the nullable `energy_ledger.attempt_id` stays unused until Stage 7). Development grant idempotency keys are UUIDs supplied by the operator. No new id format.
- **Clock:** every timestamp that regeneration uses comes from PostgreSQL: `transaction_timestamp()` read inside the transaction that materializes or projects. No regeneration math uses the API host's clock.
- **Locking (documented lock order, §15.3):**
  - Operator verbs: `accounts` `FOR UPDATE` → `energy_wallets` `FOR UPDATE`.
  - Account creation: the new `accounts` row → `inventories` (starter) → `energy_wallets` (insert).
  - Later stages: `inventories` → `energy_wallets` → `insurance_coverages`.
  - Every wallet write holds the wallet row `FOR UPDATE` first. Waits are bounded by the connection timeouts; a timeout is busy (503 or exit 75), nothing written, never retried by the server.
- **Ledger:**
  - Append-only; every wallet change writes exactly one entry in the same transaction.
  - Keys:
    - `open`, once per account;
    - `regen:<consumed anchor, microseconds since the Unix epoch, UTC>`;
    - `dev_grant:<uuid>`.
  - Unique `(account_id, idempotency_key)`. Each kind's delta sign is a CHECK. `balance_after >= 0`.
- **Balances:** `energy_wallets.balance >= 0`. A balance may be above the cap (grants now; releases and refunds later). Regeneration never lowers a balance and pauses at or above the cap.
- **Reconciliation:** read-only against wallets and the ledger. It writes only `anomaly_events`, never changes a balance and never corrects anything.
- **App role privileges:** exactly the grants in Task 2. No `UPDATE` or `DELETE` on `energy_ledger`, `energy_policies` or `anomaly_events`; no `DELETE` on wallets; no privilege on any identity sequence.
- **Failures:** `DbFailure.OpenAsync` and the Stage 3 classes; CLI exit codes from `ExitCodes`; logs carry no tokens, keys or credentials.
- **Scope:** no admission, attempts, reservations, holds, charges, releases, void refunds, settlement, insurance, `adjust` entries, purchases or stores, and no change to inventory or picks.
- **Touches:** `Backend/` (src, tests, `db/migrations/0006_energy.sql`, `db/invariants/0006_energy_ledger.sql`, compose passthrough of the new setting, the smoke script, README), `Docs/`, `CLAUDE.md`. **No Unity path.**
- **Infrastructure:**
  - No live deployment, and no Docker networking, SSH, UFW, mission-VM or public-exposure change.
  - VM work uses the throwaway `bg-smoke`/`bg-drill` projects and the existing test cluster only.
  - Tests create or drop only `bg_t_*` test databases.
- **Preserve** the owner's two Unity settings files and `.claude/`.
- **Commit trailer:** `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Commands** (from `Backend/`):
  - ALL: `bash ../.claude/scratchpad/phase13/tools/dt.sh test Backend.sln`.
  - One class: `… test tests/Accounts.Integration.Tests --filter "FullyQualifiedName~<Class>"`.
  - UNIT: `--filter "Requires!=Postgres"`.
  - **Low memory** (this PC): build once with `-m:1`, then run each test project with `--no-build --settings <runsettings with MaxCpuCount 1 and xUnit MaxParallelThreads 2>`. The wrapper appends `-p:` flags, so inline `-- xUnit.*` arguments don't work. Never run Unity at the same time.

## Review Focus

1. **The database clock behind a wallet's anchor** (a clock step back, or an anchor written ahead).
   - Expected: no gain, no entry, no error; the anchor never moves backwards (a wallet at the cap keeps `greatest(anchor, now)`); `GET /v1/me` shows the unchanged balance.
   - Tests: Task 1 `A_clock_behind_the_anchor_gains_nothing_and_keeps_the_anchor`; Task 3 `A_future_anchor_materializes_to_no_change`. The test sets the anchor ahead as superuser with `session_replication_role = replica`.
2. **One idempotency key, sent concurrently or with a different amount.**
   - Expected: 20 concurrent grants with one key give one entry and one balance change, and all 20 report the same `balanceAfter`. The same key with another amount is refused (`IdempotencyKeyReused`, exit 66) with nothing written.
   - Tests: Task 4 `Twenty_concurrent_grants_with_one_key_credit_once` and `A_reused_key_with_another_amount_is_refused_and_writes_nothing`.
3. **A very old anchor and a large regeneration amount** (overflow).
   - Expected: the balance stops at the cap, the anchor becomes now, one entry, no overflow.
   - Tests: Task 1 `A_years_old_anchor_fills_to_the_cap_without_overflow`; Task 3 `A_backdated_anchor_by_a_year_writes_one_entry_to_the_cap`.
4. **A policy published with a lower cap while a wallet is above it.**
   - Expected: the balance is unchanged, regeneration pauses, and `GET /v1/me` shows a balance above `cap` with `nextRegenAt` null. The wallet's `policy_version` moves to the new version at its next materialization.
   - Tests: Task 4 `A_lower_cap_never_reduces_a_balance`; Task 6 `Me_shows_a_balance_above_the_cap_with_no_next_regeneration`.
5. **Reconciliation racing grants, and repeated runs.**
   - Expected: running the reconciliation during 20 concurrent grants reports no mismatch. A corrupted balance gives exactly one unreviewed anomaly, however many runs see it, and the balance is untouched.
   - Tests: Task 5 `Reconciliation_during_concurrent_grants_reports_nothing` and `A_corrupted_balance_raises_one_anomaly_and_is_left_as_it_is`.

---

## Task 0 (controller, docs first): record the rulings

- **Files:**
  - The spec: revision 2.6, §0.6 summarizing E1–E7 as ruled; §6.1 as planned (keys, open entry, policy application); §15.1 (`0006_energy`, with insurance moved to `0007`); §15.3 (energy lock order); §16.1 (`GET /v1/me` energy); §16.4 (CLI verbs; the HTTP grant route deferred if E5 stands); §19 (`BLACKGLASS_ENERGY_RECONCILE_SECONDS`).
  - The stage plan's Stage 6 section (status; this plan's path) and Stage 7 (`0007`).
  - `CLAUDE.md` (Stage 6 authorized and in progress).
  - This plan: revision 2 with the rulings.
- **Commit:** `Phase 13 Stage 6: owner rulings E1..E7 recorded (spec revision 2.6, plan revision 2, CLAUDE.md)`.

## Task 1: `EnergyMath`, the pure regeneration rules (domain)

**Files:**
- Create `src/Accounts.Domain/Energy/EnergyPolicy.cs`, `WalletState.cs` and `EnergyMath.cs`.
- Test `tests/Accounts.Domain.Tests/Energy/EnergyMathTests.cs`.

**Interfaces (produced):**

```csharp
namespace Blackglass.Accounts.Domain.Energy;

/// One immutable policy version (spec §6.1, §19). Interval in whole microseconds, > 0 and <= 7 days.
public sealed record EnergyPolicy(int Version, int Cap, TimeSpan RegenInterval, int RegenAmount, int VoidRefundPct);

/// A wallet as stored. RegenAnchor is UTC with whole microseconds (PostgreSQL timestamptz).
public sealed record WalletState(int Balance, DateTime RegenAnchor, int PolicyVersion);

/// ConsumedAnchor is the anchor the gain was measured from; non-null exactly when Gained > 0.
public sealed record Regeneration(WalletState After, int Gained, DateTime? ConsumedAnchor)
{ public bool Changed(WalletState before) => After != before; }

public sealed class EnergyLimitException(string message) : Exception(message);

public static class EnergyMath
{
    /// Spec §6.1 under ruling E2. now < anchor: nothing changes (the anchor never moves back). balance >= cap:
    /// anchor := max(anchor, now), PolicyVersion := policy.Version, no gain. Else n := floor((now - anchor) / interval);
    /// n = 0: only PolicyVersion moves; n >= 1: gained := min(cap, balance + n*amount) - balance (in long), anchor :=
    /// (balance' == cap) ? now : anchor + n*interval. Throws ArgumentException on non-UTC or sub-microsecond times.
    public static Regeneration Materialize(WalletState wallet, EnergyPolicy policy, DateTime now);
    /// anchor + interval when the materialized balance is below the cap, else null.
    public static DateTime? NextRegenAt(WalletState materialized, EnergyPolicy policy);
    /// "regen:" + microseconds since 1970-01-01T00:00:00Z, invariant digits, no sign or padding.
    public static string RegenKey(DateTime consumedAnchor);
    /// balance + amount, refusing a result above int.MaxValue with EnergyLimitException; amount must be >= 1.
    public static int Credit(int balance, int amount);
}
```

- [ ] **Step 1: Write the failing tests:**
  - **Intervals:**
    - `Less_than_one_interval_gains_nothing_and_keeps_the_anchor`
    - `Exactly_one_interval_gains_one_amount`
    - `A_partial_interval_is_kept_for_the_next_materialization` (25 min at 10 min: +2, anchor + 20 min, next at anchor + 30 min)
    - `Gains_stop_at_the_cap_and_the_anchor_becomes_now`
    - `Reaching_the_cap_exactly_sets_the_anchor_to_now`
  - **Above the cap** (refunds and grants, decision 049, E6):
    - `At_the_cap_nothing_is_gained_and_the_anchor_follows_now`
    - `Above_the_cap_the_balance_is_kept_and_never_reduced`
    - `NextRegenAt_is_null_at_or_above_the_cap`
  - **Clock and limits:**
    - `A_clock_behind_the_anchor_gains_nothing_and_keeps_the_anchor`
    - `A_years_old_anchor_fills_to_the_cap_without_overflow` (amount 1,000,000, interval 1 µs, cap int.MaxValue − 1)
    - `Credit_refuses_a_result_above_int_MaxValue`
    - `Credit_refuses_a_non_positive_amount`
  - **Policy change (E2):**
    - `A_new_policy_applies_from_the_next_materialization_including_the_elapsed_partial_interval`
    - `A_lower_cap_keeps_a_higher_balance_and_pauses_regeneration`
  - **Keys and input:**
    - `RegenKey_is_microseconds_since_the_epoch` (fixed vectors, including a pre-2001 and a far-future anchor)
    - `Times_must_be_UTC_with_whole_microseconds`
  - **Property:** `Materializing_twice_at_the_same_time_changes_nothing_the_second_time` (a seeded random sweep over balances, anchors and policies: `Materialize(Materialize(w).After)` gains 0 and returns `After` unchanged).
- [ ] **Step 2: Run** UNIT (expected FAIL: types missing), **implement**, **run** UNIT (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: EnergyMath, the pure lazy-regeneration rules`.

## Task 2: Migration `0006_energy`, invariants and privileges

**Files:**
- Create `db/migrations/0006_energy.sql` and `db/invariants/0006_energy_ledger.sql`.
- Test `tests/Accounts.Integration.Tests/Database/EnergySchemaTests.cs` and `EnergyPrivilegeTests.cs`.
- Update `UpgradeTests.cs`, which pins the latest version: the 0005 → 0006 upgrade on a Stage 5-populated database.

**Schema** (the spec §15.1 sketch, narrowed to energy):

```sql
create table energy_policies (
  version integer generated always as identity primary key,
  cap integer not null check (cap > 0),
  regen_interval interval not null check (regen_interval > interval '0' and regen_interval <= interval '7 days'),
  regen_amount integer not null check (regen_amount > 0),
  void_refund_pct integer not null check (void_refund_pct between 0 and 100),
  created_at timestamptz not null default statement_timestamp());
insert into energy_policies (cap, regen_interval, regen_amount, void_refund_pct) values (50, interval '10 minutes', 1, 100); -- E7, §19
create table energy_wallets (
  account_id bigint primary key references accounts,
  balance integer not null check (balance >= 0),
  regen_anchor timestamptz not null default transaction_timestamp(),
  policy_version integer not null references energy_policies);
create table energy_ledger (
  entry_id bigint generated always as identity primary key,
  account_id bigint not null references energy_wallets,
  attempt_id uuid,
  kind text not null check (kind in ('open','regen','reserve','charge','release','settle','void_refund','dev_grant','adjust')),
  delta integer not null,
  balance_after integer not null check (balance_after >= 0),
  idempotency_key text not null check (length(idempotency_key) <= 200 and idempotency_key !~ '[[:space:][:cntrl:]]'
                                       and (idempotency_key = kind or idempotency_key like kind || ':_%')),
  created_at timestamptz not null default statement_timestamp(),
  check ((kind = 'open' and delta >= 0) or (kind in ('regen','dev_grant','release','void_refund') and delta > 0)
      or (kind = 'reserve' and delta < 0) or (kind in ('charge','settle') and delta = 0) or (kind = 'adjust' and delta <> 0)),
  unique (account_id, idempotency_key));
create table anomaly_events (
  id bigint generated always as identity primary key, kind text not null check (length(kind) between 1 and 100),
  account_id bigint references accounts, host_id text, build_revision text, attempt_id uuid,
  detail jsonb not null, created_at timestamptz not null default statement_timestamp(),
  reviewed_at timestamptz, reviewed_note text);
```

- **Triggers:**
  - `energy_ledger_is_append_only` and `energy_policy_is_immutable`: `UPDATE`/`DELETE` raise `check_violation` with those constraint names (the spec's 0007 immutability triggers, brought forward for the tables this stage creates).
  - `energy_anchor_forward_only` on `energy_wallets`: the anchor is never lowered (`new.regen_anchor >= old.regen_anchor`), never set after `transaction_timestamp()`, and the policy version never decreases.
- **Operator actions:** the action list adds `energy_open`, `energy_grant` and `energy_policy_publish` (drop and re-add the check, as 0004 did).
- **Privileges** (`accounts_app`):

| Table | Granted |
|---|---|
| `energy_policies` | `SELECT`; `INSERT (cap, regen_interval, regen_amount, void_refund_pct)` |
| `energy_wallets` | `SELECT`; `INSERT (account_id, balance, policy_version)`; `UPDATE (balance, regen_anchor, policy_version)` |
| `energy_ledger` | `SELECT`; `INSERT (account_id, attempt_id, kind, delta, balance_after, idempotency_key)` |
| `anomaly_events` | `SELECT`; `INSERT (kind, account_id, host_id, build_revision, attempt_id, detail)` |

  Nothing on the identity sequences; `PUBLIC` and the outsider role get nothing.
- **Invariants** (`0006_energy_ledger.sql`, one row per violation):
  - a wallet whose balance ≠ the sum of its ledger deltas;
  - a wallet whose balance ≠ the `balance_after` of its last entry (by `entry_id`);
  - an entry whose `balance_after` ≠ the previous entry's `balance_after` + its delta;
  - a wallet with no `open` entry.

- [ ] **Step 1: Write the failing tests:**
  - **Schema** (`EnergySchemaTests`):
    - one test per CHECK, violated directly in SQL (negative balance, negative `balance_after`, each kind's wrong sign, a key not prefixed by its kind, a zero cap, interval or amount, an interval over 7 days, a refund percentage over 100);
    - `A_duplicate_ledger_key_fails_on_the_unique_constraint`;
    - `Ledger_rows_cannot_be_updated_or_deleted_even_by_the_owner`;
    - `Policies_cannot_be_updated_or_deleted`;
    - `The_anchor_never_moves_back_or_past_now`;
    - `The_policy_version_never_decreases`;
    - `Version_1_is_seeded_with_the_provisional_values`.
  - **Privileges** (`EnergyPrivilegeTests`):
    - `The_app_role_has_exactly_the_energy_privileges` (from `information_schema` column and table privileges, like `InventoryPrivilegeTests`);
    - `The_app_role_cannot_delete_a_wallet_or_touch_a_sequence` (42501);
    - `The_outsider_role_reads_nothing`.
  - **Upgrade:** `Upgrading_a_stage5_database_to_0006_keeps_every_row_and_opens_no_wallet` (E3). Migrate to 5 and seed accounts, starters and an edited inventory; migrate to 6; the 0000–0006 invariants pass, and `energy_wallets` is empty.
  - **Invariant fixtures:** `Each_energy_invariant_reports_its_violation` (for each rule, a superuser fixture breaks it with `session_replication_role = replica`).
- [ ] **Step 2: Run** (expected FAIL: version 6 missing), **implement**, **run** the classes plus `AccountsSchemaTests`, `PlayerSchemaTests` and `MigratorTests` (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: migration 0006_energy (policies, wallets, append-only ledger, anomalies, narrow privileges) and the ledger invariants`.

**Checkpoint 1 (independent review, most capable model):** Tasks 1–2, against §6.1, §15.1–§15.2, §18.3 and these rulings. Focus: arithmetic edges, trigger completeness, exact privileges.

## Task 3: Provisioning and materialization under the wallet lock

**Files:**
- Create `src/Accounts.Persistence/Energy/EnergyProvisioner.cs`, `EnergyLedgerWriter.cs` and `EnergyPolicies.cs` (reads the latest version).
- Modify `AccountAdminStore.CreateAccountAsync`: it opens the wallet after the starter, in the same transaction, and the audit result gains `energy: {opened, balance, policyVersion}`. Modify `AdminCommand`'s `account create` output to add `energyOpened=` and `energyBalance=`.
- Test `tests/Accounts.Integration.Tests/Database/EnergyLedgerWriterTests.cs`; update `ProvisioningTests.cs` and `AdminTests.cs`.

**Interfaces (produced):**

```csharp
namespace Blackglass.Accounts.Persistence.Energy;

/// The wallet as opened. Opened is false when it already existed (then Balance and PolicyVersion are the stored ones).
public sealed record WalletOpen(bool Opened, int Balance, int PolicyVersion);

public static class EnergyProvisioner
{
    /// Inside the caller's transaction (the caller holds the accounts row). insert … on conflict do nothing, then, when
    /// inserted, one 'open' ledger entry (delta = balance = the latest policy's cap, E1). Never opens, commits or rolls back.
    public static Task<WalletOpen> OpenAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct);
}

public static class EnergyLedgerWriter
{
    /// Locks the wallet FOR UPDATE (null when there is none), reads transaction_timestamp() and the latest policy (E2),
    /// applies EnergyMath.Materialize and, when it changed, updates the wallet; when Gained > 0 it also inserts the
    /// 'regen' entry (RegenKey of the consumed anchor) with its balance_after. Never commits.
    public static Task<(WalletState Wallet, EnergyPolicy Policy, DateTime Now)?> MaterializeLockedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct);
}
```

- [ ] **Step 1: Write the failing tests:**
  - **Provisioning:**
    - `Account_creation_opens_a_full_wallet_with_one_open_entry`
    - `A_failure_opening_the_wallet_rolls_back_the_account_and_its_starter` (a test-only trigger, created by the superuser in the test database, raises on the wallet insert)
    - `Opening_twice_opens_once`
  - **Materialization:**
    - `A_backdated_anchor_writes_one_regen_entry_keyed_by_the_consumed_anchor`
    - `Materializing_twice_in_a_row_writes_one_entry`
    - `Ten_concurrent_materializations_write_one_entry` (start barrier; distinct connections)
    - `A_wallet_at_the_cap_moves_its_anchor_and_writes_no_entry`
    - `A_future_anchor_materializes_to_no_change`
    - `A_backdated_anchor_by_a_year_writes_one_entry_to_the_cap`
    - `A_failure_inserting_the_regen_entry_leaves_the_wallet_unchanged` (a test-only trigger on `energy_ledger`)
    - `Regeneration_uses_the_database_clock` (the anchor is set relative to the database's `now()`, and the gain matches the database's elapsed time, whatever the test host's clock reads)
    - `The_invariants_hold_after_every_test_in_this_class` (`InvariantFailuresAsync` is empty in each test's teardown)
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** the classes (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: wallets open at account creation; materialization writes one regen entry under the wallet lock`.

## Task 4: Operator verbs: open, grant, publish a policy

**Files:**
- Create `src/Accounts.Persistence/Energy/EnergyAdminStore.cs` and `EnergyException.cs`. Move `AccountAdminStore`'s private audit helper into `src/Accounts.Persistence/Accounts/OperatorAudit.cs` (internal; `account_id` nullable for policy publication), with no behaviour change.
- Modify `AdminArguments` (verbs `energy-open` and `energy-grant`, which take `--account-base58`; `energy-grant` also takes `--amount` and `--idempotency-key`; `energy-policy-publish`, with `--cap`, `--interval-seconds`, `--amount` and `--void-refund-pct` (none needs the catalogue); `energy-reconcile`, which needs no account and no confirmation), `AdminCommand` (outputs and exit codes) and the usage text.
- Test `tests/Accounts.Integration.Tests/Database/EnergyAdminStoreTests.cs` and `EnergyAdminCliTests.cs`; extend the `AdminArguments` unit tests.

**Interfaces (produced):**

```csharp
public enum EnergyError { NoWallet, IdempotencyKeyReused, BalanceLimit, InvalidAmount, InvalidPolicy }
public sealed class EnergyException(EnergyError error, string message) : Exception(message) { public EnergyError Error { get; } = error; }

public sealed record GrantResult(int Amount, int BalanceAfter, int Regenerated, bool Replayed);

public sealed class EnergyAdminStore(NpgsqlDataSource db)
{
    /// accounts FOR UPDATE (NotFound) → EnergyProvisioner.OpenAsync → audit 'energy_open' {opened, balance, policyVersion}.
    public Task<WalletOpen> OpenAsync(long accountId, OperatorContext op, CancellationToken ct = default);
    /// One transaction, in this order:
    ///   1. accounts FOR UPDATE (NotFound), then the wallet FOR UPDATE (NoWallet);
    ///   2. look up 'dev_grant:<key>': the same amount → Replayed with the stored balance_after, a replay audit and no
    ///      other write; another amount → IdempotencyKeyReused, nothing written;
    ///   3. otherwise materialize, then Credit (BalanceLimit), update the wallet, insert the dev_grant entry, and audit
    ///      'energy_grant' {amount, balanceAfter, regenerated, replayed}.
    /// amount is 1..1,000,000 (InvalidAmount). A suspended account can be granted: energy is property, not access.
    public Task<GrantResult> GrantAsync(long accountId, int amount, Guid idempotencyKey, OperatorContext op, CancellationToken ct = default);
    /// Inserts a new version (PostgreSQL mints it); audit 'energy_policy_publish' with the values. InvalidPolicy before
    /// any write when a value is outside its CHECK.
    public Task<EnergyPolicy> PublishPolicyAsync(int cap, TimeSpan interval, int amount, int voidRefundPct, OperatorContext op, CancellationToken ct = default);
}
```

- **CLI outputs:**
  - Every energy verb prints `accountId=` (except publish and reconcile) and `correlationId=`.
  - `energy open` prints `energyOpened=opened|already`, `balance=` and `policyVersion=`.
  - `energy grant` prints `amount=`, `balanceAfter=`, `regenerated=` and `replayed=true|false`.
  - `energy policy publish` prints `policyVersion=`, `cap=`, `intervalSeconds=`, `amount=` and `voidRefundPct=`.
- **CLI exit codes:**
  - `NoWallet` → 65, with a message naming `energy open`;
  - `IdempotencyKeyReused` → 66;
  - `BalanceLimit`, `InvalidAmount` and `InvalidPolicy` → 64.
- **Confirmation:** `energy open` and `energy grant` change an existing account, so they print the `target.*` lines and need confirmation; the target lines gain `target.energyBalance=`, or `(no wallet)`.

- [ ] **Step 1: Write the failing tests:**
  - **Grants:**
    - `A_grant_materializes_first_then_credits_and_writes_two_entries_in_order`
    - `A_replayed_key_returns_the_stored_result_and_writes_no_entry`
    - `A_reused_key_with_another_amount_is_refused_and_writes_nothing`
    - `Twenty_concurrent_grants_with_one_key_credit_once`
    - `Twenty_concurrent_grants_with_distinct_keys_all_credit_and_the_chain_holds`
    - `A_grant_may_exceed_the_cap_and_regeneration_then_pauses` (E6)
    - `A_grant_past_int_MaxValue_is_BalanceLimit_and_writes_nothing`
    - `A_grant_without_a_wallet_is_NoWallet`
    - `A_grant_to_an_unknown_account_is_NotFound`
    - `A_grant_to_a_suspended_account_is_allowed`
  - **Failure injection:**
    - `A_failure_writing_the_audit_rolls_back_the_grant_and_its_regen_entry` (a test-only trigger on `operator_actions`)
    - `A_grant_waiting_on_a_held_wallet_lock_times_out_busy_and_writes_nothing` (bounded `lock_timeout`; CLI exit 75)
    - `A_grant_whose_connection_is_terminated_mid_transaction_writes_nothing` (`pg_terminate_backend` on the waiting backend)
  - **Policies and wallets:**
    - `Publishing_a_policy_mints_the_next_version_and_audits_it`
    - `A_lower_cap_never_reduces_a_balance` (E2)
    - `An_invalid_policy_is_refused_before_any_write`
    - `Opening_an_existing_wallet_reports_already_and_changes_nothing`
  - **CLI:**
    - `Energy_verbs_print_their_outputs_and_exit_codes` (theory)
    - `Energy_grant_and_open_need_confirmation`
    - `Energy_verbs_never_load_the_catalogue` (the refusing-catalogue store, as in Stage 4's boundary tests)
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** the classes plus `AdminTests`, `AdminConfirmationTests` and `AdminCatalogueBoundaryTests` (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: idempotent development grants, wallet opening and policy publication as audited operator verbs`.

## Task 5: Reconciliation

**Files:**
- Create `src/Accounts.Persistence/Energy/EnergyReconciler.cs` and `src/Accounts.Api/Energy/EnergyReconcileService.cs` (a `BackgroundService` with a `PeriodicTimer`; E4).
- Modify `AuthSettings` (`BLACKGLASS_ENERGY_RECONCILE_SECONDS`: 0 = off, else 60..86400, default 3600), `ServeCommand` (registers the service when the setting isn't 0), `deploy/services/compose.yaml` (the env passthrough), and `AdminCommand` (`energy reconcile` prints `walletsChecked=`, `mismatches=`, `anomaliesRecorded=`, and exits 0 even with mismatches; the anomaly is the signal).
- Test `tests/Accounts.Integration.Tests/Database/EnergyReconcilerTests.cs`; extend `AuthSettingsTests` and `LogSecrecyTests`.

**Interfaces (produced):**

```csharp
public sealed record EnergyMismatch(long AccountId, string Rule, int Balance, long LedgerSum, int? LastBalanceAfter);
public sealed record ReconcileReport(int WalletsChecked, IReadOnlyList<EnergyMismatch> Mismatches, int AnomaliesRecorded);

public sealed class EnergyReconciler(NpgsqlDataSource db)
{
    /// Reads every wallet and its ledger in one repeatable-read, read-only snapshot (so a concurrent grant is seen whole
    /// or not at all), applies the four 0006 invariant rules, then, in a separate short transaction, inserts one
    /// anomaly_events row (kind 'energy_ledger_mismatch', detail {rule, balance, ledgerSum, lastBalanceAfter}) per
    /// mismatch that has no unreviewed anomaly with the same account, rule and values. Never updates a wallet or the ledger.
    public Task<ReconcileReport> RunAsync(CancellationToken ct = default);
}
```

- [ ] **Step 1: Write the failing tests:**
  - **Reports:**
    - `A_clean_database_reports_no_mismatch`
    - `A_corrupted_balance_raises_one_anomaly_and_is_left_as_it_is`
    - `A_second_run_does_not_duplicate_an_unreviewed_anomaly`
    - `A_reviewed_anomaly_is_raised_again_if_the_mismatch_persists`
    - `A_broken_chain_and_a_missing_open_entry_are_each_reported`
    - `Reconciliation_during_concurrent_grants_reports_nothing`
  - **The hosted service:**
    - `The_service_runs_on_its_interval_and_logs_counts_without_secrets` (constructed directly with a short interval)
    - `A_database_failure_in_one_run_is_logged_and_the_next_run_still_happens`
    - `Zero_disables_the_service`
  - **CLI:** `Energy_reconcile_prints_counts_and_exits_zero_with_mismatches`
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** the classes (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: read-only wallet and ledger reconciliation with deduplicated anomalies, hourly in the API and on demand`.

## Task 6: `GET /v1/me` energy

**Files:**
- Create `src/Accounts.Persistence/Energy/EnergyReader.cs` (`ProjectAsync(conn, tx, accountId)` → `EnergyView?`: in the caller's snapshot, `transaction_timestamp()`, the latest policy, `EnergyMath.Materialize` in memory only).
- Modify `PlayerReadStore` (`GetMeAsync` → `MeView(long? InventoryVersion, EnergyView? Energy)`, one read-only snapshot), `AuthEndpoints` (`MeResponse` gains `Energy`), and the DTO audit test's expectations.
- Test `tests/Accounts.Integration.Tests/Database/MeEnergyApiTests.cs`; extend `ApiTests` and `PlayerApiTests` where they pin the `/v1/me` shape.

**Interfaces (produced):**

```csharp
public sealed record EnergyView(int Balance, int Cap, DateTime? NextRegenAt);
public sealed record MeView(long? InventoryVersion, EnergyView? Energy);
// JSON: {"accountId":"<Base58>","inventoryVersion":n|null,"energy":{"balance":n,"cap":n,"nextRegenAt":"<UTC ISO-8601 with Z>"|null}|null}
```

- [ ] **Step 1: Write the failing tests:**
  - **Shape and projection:**
    - `Me_returns_exactly_accountId_inventoryVersion_and_energy` (exact member set)
    - `Me_projects_regeneration_without_writing` (a backdated anchor: the projected balance rises; the wallet row, the ledger count and the anchor are unchanged)
    - `Me_shows_a_balance_above_the_cap_with_no_next_regeneration`
    - `Me_shows_null_energy_for_an_account_without_a_wallet`
    - `Me_nextRegenAt_is_the_anchor_plus_the_interval_in_UTC`
  - **Snapshot and headers:**
    - `Me_reads_inventory_and_energy_from_one_snapshot` (a grant committed between the two reads is never half-visible; the held-lock pattern)
    - `Me_is_no_store_and_rate_limited_as_before`
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** the classes (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: GET /v1/me shows the projected energy balance, read-only`.

**Checkpoint 2 (independent review, most capable model):** Tasks 3–6. Focus: lock order, idempotency under concurrency, read-only projection, failure atomicity, the audit and log contents.

## Task 7: Smoke and VM

- **Smoke script** (`deploy/services/tests/smoke-test.sh`), after its existing sections:
  - `account create` prints `energyOpened=opened energyBalance=50`;
  - `GET /v1/me` shows `energy.balance` 50, `cap` 50 and `nextRegenAt` null;
  - `energy grant --amount 5` with a fresh UUID prints `balanceAfter=55 replayed=false`, and the same key again prints `replayed=true` and `balanceAfter=55`;
  - `energy reconcile` prints `mismatches=0`;
  - no key or token appears in the transcript (the Stage 5 printf and `--config -` patterns).
- **VM runs**, from committed HEAD via `vm-sync.sh`:
  - smoke (exit 0);
  - restore drill (exit 0; the `0006` invariant runs with the others);
  - off-host probe (5080, 5432 and 55432 closed; SSH answers).
- **Evidence:** `.claude/scratchpad/phase13/stage-06/`.
- **Commit:** `Phase 13 Stage 6: smoke test covers wallet opening, a replayed grant and reconciliation`.

## Task 8: Docs as built, final review, stop

- Spec (as built for §6.1, §15, §16.1, §16.4, §19), README (an energy section, the env table row, `0006` privileges, the CLI verbs), the stage plan's status, `CLAUDE.md`, and this plan's "As built" (measured counts, rulings, departures, deferred minors).
- The whole-branch review is Checkpoint 3; resolve Critical and Important findings with failing tests first.
- **Completion checks:**
  - backend ALL with counts;
  - the VM smoke, drill and probe;
  - `git diff --stat main...HEAD` showing only the paths listed under Touches;
  - migrations 0000–0005 byte-identical;
  - the owner's settings files and `.claude/` preserved;
  - no `bg_t_` database or SSH tunnel left behind.
- Unity suites are not re-run because no Unity path changes, unless the owner asks; the diff stat is the proof.
- **Stop** before merging, pushing, deploying or beginning Stage 7.

---

## Acceptance criteria

1. `0006_energy` applies to a Stage 5 database; 0000–0005 are unchanged; the 0000–0006 invariants hold after every energy test and after the restore drill.
2. Regeneration uses only `transaction_timestamp()`, is materialized under the wallet lock, and writes exactly one `regen` entry per materialization that gains, keyed by the consumed anchor. Twice in a row, or ten times concurrently, gives one entry.
3. Caps and partial intervals behave as §6.1 states. A balance at or above the cap never regenerates and is never reduced, including after a lower cap is published.
4. Development grants are idempotent per key: replays (sequential and 20 concurrent) credit once and return the stored result. A reused key with another amount is refused with nothing written.
5. `balance >= 0` and `balance_after >= 0` hold, each kind's sign is enforced, and the ledger is append-only, all proven by direct SQL.
6. Reconciliation is read-only, reports no false mismatch under concurrent grants, and records one unreviewed anomaly per persistent mismatch without changing any balance.
7. `GET /v1/me` returns `energy: {balance, cap, nextRegenAt}` (or null) from one read-only snapshot, without writing.
8. Every injected failure (ledger insert, audit insert, wallet insert at account creation, lock timeout, terminated connection) leaves the wallet, the ledger and the account exactly as before.
9. The app role's privileges are exactly Task 2's table; the outsider role reads nothing.
10. No Unity, mission-VM, Docker networking, SSH, UFW or exposure change; no live deployment.

## Out of scope

- Admission and attempts, reservations, holds, charges, releases, void refunds (the `void_refund_pct` column exists, unused), settlement and insurance (Stages 7–9).
- Operator `adjust` entries (the kind exists in the CHECK; the verb comes with the first need), and the HTTP grant route (E5).
- Purchases, stores and any acquisition of energy other than regeneration and development grants.
- Automatic retry of any energy transaction.
- Any Unity change.

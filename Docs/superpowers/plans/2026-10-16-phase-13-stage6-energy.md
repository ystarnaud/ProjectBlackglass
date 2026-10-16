# Phase 13 Stage 6: persistent energy accounting — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Date: 2026-10-16. **Revision 2, 2026-10-16: authorized by the owner with rulings E1–E7 (§A), which supersede revision 1's proposals. Status: implemented on branch `phase13-stage6`, awaiting the owner's review; implementation commits not pushed, not merged, not deployed. Measured results: "As built" at the end.** Base: `main` @ `62cbfcf` (Stage 5 merged and pushed; schema version 5; backend ALL 1250/0, Domain 460 and Integration 790). Branch: `phase13-stage6`. Execution: subagent-driven, integrated by the primary session, with three independent review checkpoints: after Task 2 (the pure rules and `0006`), after Task 6 (persistence, operator verbs, reconciliation, API) and over the whole branch.

**Goal:** every account has a persistent energy wallet that only the server changes.
- **Regeneration.** The balance regenerates lazily against PostgreSQL's clock, up to the cap of a versioned, immutable policy. A new policy version is never retroactive. Each regeneration is recorded once in an append-only ledger.
- **Reconciliation.** It runs periodically and on demand, read-only. It compares every wallet with its ledger and records a deduplicated anomaly on a mismatch, never correcting anything.
- **Operators** can open wallets for earlier accounts, publish a policy version and make idempotent development grants.
- **`GET /v1/me`** shows the projected balance without writing.

Stage 6 builds the accounting only. Admission, reservations, charges, entry costs, refunds and insurance belong to later stages.

**Architecture:**
- **Domain (pure).** `Accounts.Domain/Energy` gains `EnergyPolicy`, `WalletState` and `EnergyMath`.
  - `Materialize(wallet, policies, now)` replays time from the wallet's anchor to `now` across every policy version that became effective in between (§A E2, "the transition algorithm").
  - All arithmetic uses whole microseconds (`long`, with `Int128` for the proportional step), the resolution of PostgreSQL's `timestamptz`.
  - The anchor never moves backwards, a balance is never lowered, and the result does not depend on how often or when the wallet is materialized.
- **Persistence.**
  - Migration `0006_energy` adds `energy_policies` (identity versions with a database-set `effective_at`; version 1 seeded), `energy_wallets`, the append-only `energy_ledger` and `anomaly_events`. It also adds triggers, one deferred constraint trigger that ties every wallet balance to its last ledger entry, and narrow privileges.
  - `EnergyProvisioner` opens a wallet inside a caller's transaction, with its `open` entry. It runs at account creation and through the operator's `energy open`.
  - `EnergyLedgerWriter.MaterializeLockedAsync` regenerates a wallet the caller holds `FOR UPDATE`. It writes the wallet and the `regen` entry, keyed by the consumed anchor, in the same transaction. Later stages call it from their own transactions.
  - `EnergyAdminStore` handles the operator verbs (open, grant, publish), each audited to `operator_actions` in its own transaction.
  - `EnergyReader` projects a wallet for `GET /v1/me`.
  - `EnergyReconciler` checks every wallet against its ledger in one snapshot and records deduplicated anomalies.
- **API.** `GET /v1/me` gains `energy: {balance, cap, nextRegenAt}`, or `null` when the account has no wallet yet (never an invented zero). A hosted service in the API process runs the reconciliation periodically.
- **Admin CLI.** `energy open`, `energy grant`, `energy policy publish` and `energy reconcile`.

**Tech stack:** as Stage 5 (ASP.NET Core minimal APIs on .NET 10, PostgreSQL 17, Npgsql 10, Dapper, xUnit against the real test cluster). No Unity change.

**Spec:** `Docs/superpowers/specs/2026-10-14-phase-13-backend-design.md` §3, §6.1, §6.3, §15.1–§15.4, §16.1 (`GET /v1/me`), §16.4, §18.1, §18.3, §19 and decision 049 (energy); revision 2.6 (Task 0) records the rulings. Stage plan: `Docs/superpowers/plans/2026-10-14-phase-13-backend.md` §Stage 6.

---

## A. Owner rulings (2026-10-16)

| Ruling | Decision |
|---|---|
| **E1 initial energy** | A new account's wallet opens **full**: its balance is the active policy's `opening_balance` (version 1: 50, the cap). It is created in the same transaction as the account and its starter; a failure anywhere rolls everything back. An `open` ledger entry records the opening balance |
| **E2 policy transitions (revised)** | **Non-retroactive.** Each version has a database-set `effective_at`. Earned regeneration is computed under the old policy up to the new one's `effective_at`, and under the new one only afterwards. The balance is preserved, including above a lower cap. Partial progress is carried across proportionally. A new policy never reduces a balance and never awards faster regeneration for earlier time. Materializing immediately or much later gives the same result. Policies are immutable once published |
| **E3 existing accounts** | An explicit, audited, idempotent `energy open` verb. It creates one wallet per account, with the opening balance from the applicable policy and an `open` entry. Concurrent attempts open one wallet; a retry after a commit returns the existing wallet. No wallet is created by a GET; until a wallet is opened the API says so (`energy: null`), never a zero balance |
| **E4 reconciliation** | Periodic, in the API process (the "existing API worker"), every `BLACKGLASS_ENERGY_RECONCILE_SECONDS` (default 3600). It compares each balance with its ledger (opening, regeneration, grants). It never corrects anything and never duplicates an outstanding anomaly. Concurrent sweeps are safe. An operator command runs it on demand. API startup never waits for a sweep |
| **E5 development grants** | Through the operator CLI only; the HTTP route is deferred. Each grant needs confirmation, is audited and takes an idempotency key. It updates the wallet and the ledger in one transaction. Reusing a key with different parameters is refused, and a retry never grants twice. Overflow and a configured bound (`BLACKGLASS_ENERGY_GRANT_MAX`) are respected |
| **E6 above the cap** | The cap limits natural regeneration only. Grants (and later refunds) may raise a balance above it, and regeneration pauses at or above the cap. Nothing is silently discarded; the arithmetic is checked |
| **E7 policy version 1** | `0006_energy` seeds immutable version 1: cap 50, opening balance 50, 1 energy per 10 minutes, void refund 100 %. All provisional. Later versions come only from the audited `energy policy publish`; an ordinary value change needs no code or schema change |

**The owner's technical requirements** (verbatim intent, each mapped to where this plan meets it):

| # | Requirement | Where it is met |
|---|---|---|
| 1 | PostgreSQL is the time source, read after the wallet locks | `clock_timestamp()`, read after the wallet row lock and the policy lock (Global Constraints, Clock) |
| 2 | A clock moving backward causes no negative regeneration, no anchor regression and no duplicate credit | Task 1 rules, the `energy_wallet_forward_only` trigger, keys derived from strictly increasing anchors |
| 3 | Partial intervals are preserved | `regen_progress_us` |
| 4 | Every balance change has a ledger entry in the same transaction | The deferred constraint trigger `energy_wallet_matches_ledger` |
| 5 | Balances are nonnegative and may exceed the cap | CHECKs; E6 |
| 6 | `bigint` account ownership; Base58 outside | — |
| 7 | The documented lock order holds; no deadlock with account creation or admin operations | Global Constraints, Locking; Task 4 deadlock tests |
| 8 | Reconciliation is read-only on balances and writes only deduplicated anomalies | Task 5 |
| 9 | `GET /v1/me` projects without writing | Task 6 |
| 10 | Concurrent grants and regeneration, policy transitions, reconciliation, failed transactions, interrupted connections and lock timeouts are tested | Tasks 3–6 |
| 11 | A failure part-way through rolls the whole operation back | One transaction per operation; the failure-injection tests |
| 12 | No automatic retry | Global Constraints, Failures |

### The transition algorithm (E2)

A wallet stores:
- `balance`;
- `regen_anchor`: the database time up to which regeneration has been accounted. It only moves forward;
- `regen_progress_us`: the microseconds of progress toward the next unit, measured under the wallet's policy and always below that policy's interval;
- `policy_version`: the policy the progress is measured under.

`Materialize(wallet, policies, now)`, where `policies` holds the wallet's own version followed by every later version in order:

```
state := wallet
for each later policy p with p.effective_at <= now, in version order:
    advance(state, under current policy, until p.effective_at)
    state.progress := floor(state.progress * p.interval / current.interval)   -- proportional, Int128, then < p.interval
    current := p; state.policy_version := p.version
advance(state, under current, until now)

advance(state, P, until U):
    if U <= state.anchor: return                                  -- no time elapsed (or the clock is behind): nothing
    elapsed := U - state.anchor; state.anchor := U
    if state.balance >= P.cap: state.progress := 0; return        -- paused at or above the cap; balance never reduced
    total := state.progress + elapsed
    n := total / P.interval; rem := total % P.interval
    reached := state.balance + n * P.amount                       -- in Int128 / checked long
    if reached >= P.cap: gained += P.cap - state.balance; state.balance := P.cap; state.progress := 0
    else:                gained += n * P.amount;           state.balance := reached; state.progress := rem
```

- **The ledger.** When `gained > 0` the materialization writes one `regen` entry, with delta `gained`, keyed `regen:<the anchor before materialization, in µs since the epoch>`. Every gain needs elapsed time, so the anchor strictly increases between entries: keys never repeat, even after the clock steps back.
- **Path independence.** Progress is the remainder of a sum of elapsed times, and pausing at the cap resets it whichever path is taken. Materializing at `t1` and then `t2` therefore gives the same state as materializing once at `t2`, provided both see the same policy set. Task 1 proves this with a seeded property sweep, across transitions too.
- **The policy set is final for every `now` a materialization reads.** Publishing takes the exclusive transaction advisory lock `EnergyPolicies.PublicationLockKey`; every materialization and projection takes it shared, after the wallet lock and before reading the clock.
  - A publication's `effective_at` is set by its BEFORE INSERT trigger to `greatest(clock_timestamp(), latest effective_at + 1 µs)`, after it holds the exclusive lock. Every earlier reader has committed by then, with its `now` read earlier.
  - So no materialization can read a `now` later than a version it did not see.
  - The advisory lock is a leaf: nothing else is locked after it, and publication holds nothing else, so it cannot deadlock.
- **A clock behind the latest `effective_at`** (a step back after a publication): the applicable policy for opening a wallet is the latest version effective at `now`, else version 1. A version whose `effective_at` is at or before the anchor still switches the wallet's version at its place in the replay, with no time to account.

---

## Global Constraints

- **Migrations:** forward-only, numbered, checksummed, one transaction each. **0000–0005 are applied and immutable.** Stage 6 adds `0006_energy.sql` only.
- **Ids:** account ids are `bigint` inside and Base58 outside. No request carries an account id; the account comes from the session. Mission attempt ids stay UUIDv7 (the nullable `energy_ledger.attempt_id` is unused until Stage 7). Development grant idempotency keys are UUIDs supplied by the operator, unique across all accounts. No new id format.
- **Clock:** every time regeneration uses comes from PostgreSQL's `clock_timestamp()`, read inside the transaction **after** it holds the wallet row lock (when it has one) and the shared policy lock. No regeneration math uses the API host's clock. `transaction_timestamp()` is never used for regeneration, because it can predate a lock wait.
- **Locking (documented lock order; spec §15.3):**
  - Operator verbs: `accounts` `FOR UPDATE` → `energy_wallets` `FOR UPDATE` → the policy advisory lock (shared).
  - Account creation: the new `accounts` row → `inventories` (starter) → the policy advisory lock (shared) → `energy_wallets` (insert).
  - `energy open`: `accounts` `FOR UPDATE` → the policy advisory lock (shared) → `energy_wallets` (insert … on conflict do nothing).
  - Policy publication: the policy advisory lock (exclusive) only.
  - `GET /v1/me`: the policy advisory lock (shared), then one statement.
  - Later stages: `inventories` → `energy_wallets` → the policy advisory lock (shared). The advisory lock is a **leaf**: once a transaction holds it, it takes no lock that a queued publisher or another shared holder could hold (insurance activations lock only `insurance_coverages` and never take it; admission reads coverages without locking). A publication begins its transaction only after the operator confirms, and holds it only for the insert and the audit.
  - Waits are bounded by the connection timeouts. A timeout is busy (503 or exit 75), nothing written, never retried by the server.
  - Every ledger entry is inserted while holding its wallet row lock, in a READ COMMITTED transaction (the chain trigger and the advisory-lock protocol both depend on it). A 23514 from an energy constraint is an internal error (500, or exit 1) with the constraint name logged, never busy and never retried.
- **Ledger:**
  - Append-only (triggers and privileges). Every balance change writes exactly one entry in the same transaction, enforced at commit by `energy_wallet_matches_ledger`.
  - Keys:
    - `open`, once per account;
    - `regen:<consumed anchor, µs since the Unix epoch, UTC>`;
    - `dev_grant:<uuid>`.
  - Unique `(account_id, idempotency_key)`, and `dev_grant` keys are also unique globally. Each kind's delta sign is a CHECK, and `balance_after >= 0`.
- **Balances:** `energy_wallets.balance >= 0`. A balance may exceed the cap. Regeneration never lowers a balance and pauses at or above the cap. All sums are checked: a credit past 2,147,483,647 is refused, never wrapped or clamped.
- **Reconciliation:** read-only against wallets and the ledger. It writes only `anomaly_events`, through a partial unique index that admits one outstanding (unreviewed) row per identical finding.
- **App role privileges:** exactly Task 2's table. No `UPDATE` or `DELETE` on `energy_ledger`, `energy_policies` or `anomaly_events`; no `DELETE` on wallets; no column grant on `effective_at`; nothing on any identity sequence.
- **Failures:** `DbFailure.OpenAsync` and the Stage 3 classes; CLI exit codes from `ExitCodes`. Logs carry no tokens, keys or credentials. **No automatic retry** of any energy transaction (deadlock, serialization, lock and statement timeouts are reported, nothing written); a grant's idempotency key makes an operator's retry safe.
- **Settings** (`EnergySettings`, read by `serve` and the admin CLI; provisional, §19):
  - `BLACKGLASS_ENERGY_RECONCILE_SECONDS`: 0 = off, else 60..86400; default 3600.
  - `BLACKGLASS_ENERGY_GRANT_MAX`: 1..1,000,000; default 1000. It is the largest single development grant.
- **Scope:** no admission, attempts, reservations, holds, charges, entry costs, releases, refunds, settlement, insurance, `adjust` entries, purchases, premium currency or trading; no change to inventory or picks.
- **Touches:** `Backend/` (src, tests, `db/migrations/0006_energy.sql`, `db/invariants/0006_energy_ledger.sql`, the compose passthrough of the two settings, the smoke script, README), `Docs/`, `CLAUDE.md`. **No Unity path.**
- **Infrastructure:**
  - No live deployment; no firewall, SSH, Docker networking or mission-server change.
  - VM work uses the throwaway `bg-smoke`/`bg-drill` projects and the existing test cluster only.
  - Tests create or drop only `bg_t_*` databases.
- **Preserve** the owner's two Unity settings files and `.claude/`.
- **Git:** logically separated commits on `phase13-stage6`. Only the documentation baseline (Task 0) is pushed; implementation commits are not pushed, and nothing is merged.
- **Commit trailer:** `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Commands** (from `Backend/`, with `bash Backend/tools/testdb.sh tunnel` open):
  - One class: `bash ../.claude/scratchpad/phase13/tools/dt.sh test tests/Accounts.Integration.Tests --filter "FullyQualifiedName~<Class>"`.
  - UNIT: `… test tests/Accounts.Domain.Tests` (and `--filter "Requires!=Postgres"` on the integration project).
  - **ALL, low-memory recipe (this PC):** `dt.sh build Backend.sln -m:1`, then each test project in turn with `--no-build --settings <runsettings with RunConfiguration.MaxCpuCount 1 and xUnit.MaxParallelThreads 2>`. Inline `-- xUnit.*` arguments break, because the wrapper appends `-p:` flags. Never two test runs at once, never with Unity.

## Review Focus

1. **A policy published between two materializations, or racing one.**
   - Expected: the result equals a single materialization at the later time. Time before the new `effective_at` is credited only under the old policy. A materialization racing a publication waits for the publication or precedes its `effective_at`.
   - Tests:
     - Task 1 `Materializing_in_steps_equals_materializing_once_across_transitions` (property);
     - Task 3 `A_publication_waits_for_an_in_flight_materialization_and_takes_effect_after_its_clock`;
     - Task 3 `A_materialization_waits_for_an_in_flight_publication_and_sees_it`.
2. **The database clock behind a wallet's anchor** (a step back, or an anchor written ahead).
   - Expected: no gain, no entry, no error; the anchor and the policy version never move backwards.
   - Tests: Task 1 `A_clock_behind_the_anchor_changes_nothing`; Task 3 `A_future_anchor_materializes_to_no_change`. The anchor is set ahead as superuser with `session_replication_role = replica`.
3. **One idempotency key sent concurrently, with another amount, or for another account.**
   - Expected: 20 concurrent grants with one key give one entry and one balance change, and all report the same `balanceAfter`.
   - The same key with another amount or account is refused (`IdempotencyKeyReused`, exit 66) with nothing written.
   - Tests: Task 4 `Twenty_concurrent_grants_with_one_key_credit_once`, `A_reused_key_with_another_amount_is_refused_and_writes_nothing` and `A_reused_key_for_another_account_is_refused_and_writes_nothing`.
4. **Overflow at every sum.**
   - Expected:
     - a years-old anchor with a large amount stops at the cap;
     - a grant to `int.MaxValue − n` of more than `n` is `BalanceLimit` with nothing written;
     - the proportional step never overflows.
   - Tests: Task 1 `A_years_old_anchor_fills_to_the_cap_without_overflow` and `The_proportional_step_uses_Int128_at_the_largest_intervals`; Task 4 `A_grant_past_int_MaxValue_is_BalanceLimit_and_writes_nothing`.
5. **Two reconciliation sweeps at once, or a sweep racing grants.**
   - Expected: concurrent sweeps record one anomaly per outstanding finding, and a sweep during 20 concurrent grants reports nothing. API startup completes while a sweep is held.
   - Tests: Task 5 `Two_concurrent_sweeps_record_one_anomaly`, `Reconciliation_during_concurrent_grants_reports_nothing` and `Serve_starts_and_answers_health_while_the_first_sweep_is_held`.

---

## Task 0 (controller, docs first): record the rulings

- **Files:**
  - The spec: revision 2.6, §0.6 (E1–E7, the transition algorithm, the clock and lock rules, schema departures from the §15.1 sketch); pointers in §6.1, §15.1, §15.3, §16.1, §16.4 and §19.
  - The stage plan's Stage 6 section (authorized; this plan's path).
  - `CLAUDE.md` (Stage 6 authorized and in progress).
  - This plan: revision 2.
- **Commit:** `Phase 13 Stage 6: owner rulings E1..E7 recorded (spec revision 2.6, plan revision 2, CLAUDE.md)`.
- **Push** `phase13-stage6` to `origin` (authorized for this documentation baseline only).

## Task 1: `EnergyMath`, the pure rules (domain)

**Files:**
- Create `src/Accounts.Domain/Energy/EnergyPolicy.cs`, `WalletState.cs` and `EnergyMath.cs`.
- Test `tests/Accounts.Domain.Tests/Energy/EnergyMathTests.cs`.

**Interfaces (produced):**

```csharp
namespace Blackglass.Accounts.Domain.Energy;

/// One immutable version (spec §6.1, §19). RegenInterval: whole microseconds, > 0 and <= 7 days. EffectiveAt: UTC,
/// whole microseconds, set by the database. 0 <= OpeningBalance <= Cap.
public sealed record EnergyPolicy(int Version, int Cap, int OpeningBalance, TimeSpan RegenInterval, int RegenAmount, int VoidRefundPct, DateTime EffectiveAt);

/// As stored. RegenAnchor: UTC, whole microseconds; ProgressMicros: 0 <= p < the wallet policy's interval.
public sealed record WalletState(int Balance, DateTime RegenAnchor, long ProgressMicros, int PolicyVersion);

/// ConsumedAnchor is the anchor before materialization; non-null exactly when Gained > 0.
public sealed record Regeneration(WalletState After, int Gained, DateTime? ConsumedAnchor);

public sealed class EnergyLimitException(string message) : Exception(message);

public static class EnergyMath
{
    /// §A "the transition algorithm". `policies` is the wallet's version followed by every later version, ascending by
    /// Version with strictly increasing EffectiveAt (ArgumentException otherwise, or if the first is not the wallet's).
    public static Regeneration Materialize(WalletState wallet, IReadOnlyList<EnergyPolicy> policies, DateTime now);
    /// The latest policy with EffectiveAt <= now, else the first (version 1). `policies` ascending, non-empty.
    public static EnergyPolicy ApplicableAt(IReadOnlyList<EnergyPolicy> policies, DateTime now);
    /// A new wallet: (policy.OpeningBalance, now, 0, policy.Version).
    public static WalletState Open(EnergyPolicy policy, DateTime now);
    /// After materializing at `now` under `current`: now + (interval - progress) when balance < cap, else null.
    public static DateTime? NextRegenAt(WalletState materialized, EnergyPolicy current, DateTime now);
    /// "regen:" + microseconds since 1970-01-01T00:00:00Z, invariant digits (negative for pre-1970, with '-').
    public static string RegenKey(DateTime consumedAnchor);
    /// balance + amount; amount >= 1 (ArgumentOutOfRangeException); a result above int.MaxValue throws EnergyLimitException.
    public static int Credit(int balance, int amount);
}
```

- [ ] **Step 1: Write the failing tests:**
  - **Intervals:**
    - `Less_than_one_interval_accrues_progress_only`
    - `Exactly_one_interval_gains_one_amount`
    - `A_partial_interval_is_kept_as_progress` (25 min at 10 min: +2, progress 5 min)
    - `Progress_carries_into_the_next_materialization`
    - `Gains_stop_at_the_cap_and_progress_resets`
    - `Reaching_the_cap_exactly_resets_progress`
  - **Above the cap (E6):**
    - `At_the_cap_nothing_is_gained_and_the_anchor_follows_now`
    - `Above_the_cap_the_balance_is_kept_and_never_reduced`
    - `NextRegenAt_is_null_at_or_above_the_cap_and_now_plus_the_remaining_progress_below_it`
  - **Transitions (E2):**
    - `Time_before_a_new_policy_is_credited_under_the_old_one`
    - `Time_after_a_new_policy_is_credited_under_the_new_one`
    - `Progress_is_carried_across_a_transition_proportionally` (5 min of 10 → 10 min of 20; 7 min of 10 → 2 min 6 s of 3 min, floored to the µs)
    - `A_lower_cap_keeps_a_higher_balance_and_pauses_regeneration`
    - `A_higher_cap_resumes_regeneration_from_the_effective_time`
    - `Several_transitions_in_one_materialization_apply_in_order`
    - `A_faster_policy_never_pays_for_time_before_its_effective_time`
    - `A_version_effective_at_or_before_the_anchor_switches_with_no_time_to_account`
    - `Materializing_in_steps_equals_materializing_once_across_transitions` (property: a seeded random sweep over balances, progress, policy sequences and step times; `Materialize` at `t1` then `t2` equals `Materialize` at `t2`)
    - `Materializing_twice_at_the_same_time_changes_nothing_the_second_time` (property)
  - **Clock and limits:**
    - `A_clock_behind_the_anchor_changes_nothing`
    - `A_years_old_anchor_fills_to_the_cap_without_overflow` (amount 1,000,000, interval 1 µs, cap `int.MaxValue − 1`)
    - `The_proportional_step_uses_Int128_at_the_largest_intervals` (progress just under 7 days, both intervals 7 days and 1 µs)
    - `Credit_refuses_a_result_above_int_MaxValue`
    - `Credit_refuses_a_non_positive_amount`
  - **Opening and keys:**
    - `ApplicableAt_picks_the_latest_effective_version_else_version_1`
    - `Open_uses_the_opening_balance_and_zero_progress`
    - `RegenKey_is_microseconds_since_the_epoch` (fixed vectors, including pre-1970, pre-2001 and far-future anchors)
    - `Inputs_must_be_UTC_whole_microseconds_and_well_ordered`
- [ ] **Step 2: Run** UNIT (expected FAIL: types missing), **implement**, **run** UNIT (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: EnergyMath, lazy regeneration with non-retroactive policy transitions`.

## Task 2: Migration `0006_energy`, invariants and privileges

**Files:**
- Create `db/migrations/0006_energy.sql` and `db/invariants/0006_energy_ledger.sql`.
- Test `tests/Accounts.Integration.Tests/Database/EnergySchemaTests.cs` and `EnergyPrivilegeTests.cs`.
- Update the tests that pin the latest schema version (`UpgradeTests`, `MigratorTests`, `AccountsSchemaTests`, `PlayerSchemaTests`, as each requires).

**Schema** (the spec §15.1 sketch, narrowed to energy, with the E1/E2 departures):

```sql
create table energy_policies (
  version integer generated always as identity primary key,
  cap integer not null check (cap > 0),
  opening_balance integer not null check (opening_balance >= 0),
  regen_interval interval not null check (regen_interval > interval '0' and regen_interval <= interval '7 days'),
  regen_amount integer not null check (regen_amount > 0),
  void_refund_pct integer not null check (void_refund_pct between 0 and 100),
  effective_at timestamptz not null unique,            -- set by energy_policy_effective_at, never by the caller
  created_at timestamptz not null default statement_timestamp(),
  check (opening_balance <= cap));
-- BEFORE INSERT trigger energy_policy_effective_at: new.effective_at := greatest(clock_timestamp(),
--   (select max(effective_at) from energy_policies) + interval '1 microsecond'), whatever the caller supplied.
insert into energy_policies (cap, opening_balance, regen_interval, regen_amount, void_refund_pct)
  values (50, 50, interval '10 minutes', 1, 100);                      -- E7, §19
create table energy_wallets (
  account_id bigint primary key references accounts,
  balance integer not null check (balance >= 0),
  regen_anchor timestamptz not null,
  regen_progress_us bigint not null check (regen_progress_us >= 0),
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
create index energy_ledger_account_entry on energy_ledger (account_id, entry_id);
create unique index energy_dev_grant_key_global on energy_ledger (idempotency_key) where kind = 'dev_grant';
create table anomaly_events (
  id bigint generated always as identity primary key,
  kind text not null check (length(kind) between 1 and 100 and kind ~ '^[a-z0-9_]+$'),
  account_id bigint references accounts, host_id text, build_revision text, attempt_id uuid,
  dedupe_key text not null check (length(dedupe_key) between 1 and 500),
  detail jsonb not null, created_at timestamptz not null default statement_timestamp(),
  reviewed_at timestamptz, reviewed_note text);
create unique index anomaly_one_outstanding on anomaly_events (kind, dedupe_key) where reviewed_at is null;
```

- **Triggers:**
  - `energy_ledger_is_append_only` and `energy_policy_is_immutable`: `UPDATE`/`DELETE` raise `check_violation` with those constraint names.
  - `energy_policy_effective_at` (BEFORE INSERT, as above).
  - `energy_wallet_forward_only` (BEFORE UPDATE): neither `regen_anchor` nor `policy_version` ever decreases.
  - **`energy_wallet_matches_ledger`** (`CONSTRAINT TRIGGER … AFTER INSERT OR UPDATE … DEFERRABLE INITIALLY DEFERRED`): at commit, the wallet's `balance` equals the `balance_after` of its latest ledger entry (by `entry_id`). A wallet write without its ledger entry therefore fails the commit.
  - Every trigger function stays unexecutable by the app and `PUBLIC` (0001's default revoke).
- **Operator actions:** the action list adds `energy_open`, `energy_grant` and `energy_policy_publish` (drop and re-add the check, as 0004 did).
- **Privileges** (`accounts_app`):

| Table | Granted |
|---|---|
| `energy_policies` | `SELECT`; `INSERT (cap, opening_balance, regen_interval, regen_amount, void_refund_pct)` |
| `energy_wallets` | `SELECT`; `INSERT (account_id, balance, regen_anchor, regen_progress_us, policy_version)`; `UPDATE (balance, regen_anchor, regen_progress_us, policy_version)` |
| `energy_ledger` | `SELECT`; `INSERT (account_id, attempt_id, kind, delta, balance_after, idempotency_key)` |
| `anomaly_events` | `SELECT`; `INSERT (kind, account_id, host_id, build_revision, attempt_id, dedupe_key, detail)` |

  Nothing on the identity sequences; `PUBLIC` and the outsider role get nothing. The app can call `pg_advisory_xact_lock(_shared)` (built-in, granted to `PUBLIC` by PostgreSQL).
- **Invariants** (`0006_energy_ledger.sql`, one row per violation):
  - a wallet whose balance ≠ the sum of its ledger deltas;
  - a wallet whose balance ≠ the `balance_after` of its last entry;
  - an entry whose `balance_after` ≠ the previous entry's `balance_after` + its delta;
  - a wallet with no `open` entry, or an `open` entry that isn't its first;
  - a wallet whose `regen_progress_us` ≥ its policy's interval in µs.

- [ ] **Step 1: Write the failing tests:**
  - **Schema** (`EnergySchemaTests`):
    - one test per CHECK, violated directly in SQL;
    - `A_duplicate_ledger_key_fails_on_the_unique_constraint`;
    - `A_dev_grant_key_is_unique_across_accounts`;
    - `Ledger_rows_cannot_be_updated_or_deleted_even_by_the_owner`;
    - `Policies_cannot_be_updated_or_deleted`;
    - `Effective_at_is_set_by_the_database_and_strictly_increases` (an explicit value from the caller is overridden);
    - `The_anchor_and_policy_version_never_move_back`;
    - `A_wallet_balance_without_its_ledger_entry_fails_at_commit`;
    - `Version_1_is_seeded_with_the_provisional_values`;
    - `Only_one_outstanding_anomaly_per_kind_and_dedupe_key`.
  - **Privileges** (`EnergyPrivilegeTests`):
    - `The_app_role_has_exactly_the_energy_privileges` (`information_schema` column and table privileges, like `InventoryPrivilegeTests`);
    - `The_app_role_cannot_delete_a_wallet_set_effective_at_or_touch_a_sequence` (42501);
    - `The_app_role_can_take_the_policy_advisory_lock`;
    - `The_outsider_role_reads_nothing`.
  - **Upgrade:** `Upgrading_a_stage5_database_to_0006_keeps_every_row_and_opens_no_wallet` (E3). Migrate to 5 and seed accounts, starters and an edited inventory; migrate to 6; the 0000–0006 invariants pass, and `energy_wallets` is empty.
  - **Invariant fixtures:** `Each_energy_invariant_reports_its_violation` (for each rule, a superuser fixture breaks it with `session_replication_role = replica`).
- [ ] **Step 2: Run** (expected FAIL: version 6 missing), **implement**, **run** the classes plus every schema-version-pinning class (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: migration 0006_energy (versioned policies, wallets, append-only ledger, anomalies, narrow privileges) and ledger invariants`.

**Checkpoint 1 (independent review, most capable model):** Tasks 1–2, against §A, §6.1, §15.1–§15.2 and §18.3. Focus: the transition algorithm's determinism and arithmetic edges, trigger completeness, exact privileges.

## Task 3: Opening and materialization under the locks

**Files:**
- Create `src/Accounts.Persistence/Energy/EnergyPolicies.cs` (the lock key; reading the versions), `EnergyProvisioner.cs` and `EnergyLedgerWriter.cs`.
- Modify `AccountAdminStore.CreateAccountAsync`: it opens the wallet after the starter, in the same transaction, and its audit result gains `energy: {opened, balance, policyVersion}`. Modify `AdminCommand`'s `account create` output to add `energyOpened=` and `energyBalance=`. The HTTP response keeps exactly `{accountId, credentialId, accountKey}`.
- Test `tests/Accounts.Integration.Tests/Database/EnergyLedgerWriterTests.cs`; update `ProvisioningTests.cs` and `AdminTests.cs`.

**Interfaces (produced):**

```csharp
namespace Blackglass.Accounts.Persistence.Energy;

public static class EnergyPolicies
{
    public const long PublicationLockKey = 0x426C_6B45_6E50_6F6C; // "BlkEnPol"; transaction advisory lock
    /// pg_advisory_xact_lock_shared(PublicationLockKey), then clock_timestamp(): the caller's `now`.
    internal static Task<DateTime> LockSharedAndReadClockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct);
    /// Every version with Version >= fromVersion, ascending.
    internal static Task<IReadOnlyList<EnergyPolicy>> ReadFromAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int fromVersion, CancellationToken ct);
}

/// Opened is false when the wallet already existed (then Balance and PolicyVersion are the stored ones).
public sealed record WalletOpen(bool Opened, int Balance, int PolicyVersion);

public static class EnergyProvisioner
{
    /// Inside the caller's transaction; the caller holds the accounts row (inserted, or FOR UPDATE). Takes the shared
    /// policy lock, reads the clock, picks EnergyMath.ApplicableAt, inserts the wallet (on conflict do nothing) and, when
    /// inserted, the 'open' entry with delta = balance_after = the opening balance. Never opens, commits or rolls back.
    public static Task<WalletOpen> OpenAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct);
}

public sealed record Materialized(WalletState Wallet, EnergyPolicy Current, DateTime Now, int Gained);

public static class EnergyLedgerWriter
{
    /// The wallet FOR UPDATE (null when there is none); then the shared policy lock and the clock; then the policies from
    /// the wallet's version; EnergyMath.Materialize. When the state changed it updates the wallet; when Gained > 0 it also
    /// inserts the 'regen' entry. Never commits.
    public static Task<Materialized?> MaterializeLockedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long accountId, CancellationToken ct);
}
```

- [ ] **Step 1: Write the failing tests:**
  - **Opening:**
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
    - `The_clock_is_read_after_the_wallet_lock` (a materialization that waited on a held wallet lock credits up to its post-wait clock: the `now` it reports is later than the release)
    - `Regeneration_matches_the_domain_replay_of_the_database_clock` (the stored result equals `EnergyMath.Materialize` replayed with the reported `now` and the stored policies)
  - **Policy transitions** (the test backdates `effective_at` as superuser under `replica`):
    - `A_transition_is_non_retroactive_against_the_database`
    - `A_publication_waits_for_an_in_flight_materialization_and_takes_effect_after_its_clock`
    - `A_materialization_waits_for_an_in_flight_publication_and_sees_it`
  - **Teardown:** the 0000–0006 invariants hold after every test (`InvariantFailuresAsync` is empty in each test's teardown).
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** the classes (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: wallets open at account creation; materialization under the wallet and policy locks writes one regen entry`.

## Task 4: Operator verbs: open, grant, publish a policy

**Files:**
- Create `src/Accounts.Persistence/Energy/EnergyAdminStore.cs` and `EnergyException.cs`, and `src/Accounts.Api/Energy/EnergySettings.cs`.
- Move `AccountAdminStore`'s private audit helper into `src/Accounts.Persistence/Accounts/OperatorAudit.cs` (internal; `account_id` nullable for policy publication), with no behaviour change.
- Modify `AdminArguments`:
  - `energy-open` takes `--account-base58`;
  - `energy-grant` takes `--account-base58`, `--amount` and `--idempotency-key`;
  - `energy-policy-publish` takes `--cap`, `--opening-balance`, `--interval-seconds`, `--amount` and `--void-refund-pct`;
  - `energy-reconcile` takes no account and needs no confirmation;
  - no energy verb needs the catalogue.
- Also modify `AdminCommand` (outputs and exit codes), the usage text, and `deploy/services/compose.yaml` (passthrough of both settings to `api` and `admin`).
- Test `tests/Accounts.Integration.Tests/Database/EnergyAdminStoreTests.cs` and `EnergyAdminCliTests.cs`; extend the `AdminArguments` unit tests; add `EnergySettingsTests`.

**Interfaces (produced):**

```csharp
public enum EnergyError { NoWallet, IdempotencyKeyReused, BalanceLimit, InvalidAmount, InvalidPolicy }
public sealed class EnergyException(EnergyError error, string message) : Exception(message) { public EnergyError Error { get; } = error; }

public sealed record GrantResult(int Amount, int BalanceAfter, int Regenerated, bool Replayed);

public sealed class EnergyAdminStore(NpgsqlDataSource db, int grantMax)
{
    /// accounts FOR UPDATE (NotFound) → EnergyProvisioner.OpenAsync → audit 'energy_open' {opened, balance, policyVersion}.
    public Task<WalletOpen> OpenAsync(long accountId, OperatorContext op, CancellationToken ct = default);
    /// amount is 1..grantMax (InvalidAmount, before any connection). One transaction:
    ///   1. accounts FOR UPDATE (NotFound); the wallet FOR UPDATE (NoWallet);
    ///   2. 'dev_grant:<key>' looked up across all accounts: the same account and amount → Replayed with the stored
    ///      balance_after, a replay audit and no other write; anything else → IdempotencyKeyReused, nothing written;
    ///   3. MaterializeLockedAsync, then EnergyMath.Credit (BalanceLimit), the wallet update, the dev_grant entry, and the
    ///      audit 'energy_grant' {amount, balanceAfter, regenerated, replayed:false}.
    /// A concurrent first use of one key for two accounts: the global unique index fails the second; that is
    /// IdempotencyKeyReused with its transaction rolled back. A suspended account can be granted (property, not access).
    public Task<GrantResult> GrantAsync(long accountId, int amount, Guid idempotencyKey, OperatorContext op, CancellationToken ct = default);
    /// The exclusive policy lock, then insert (PostgreSQL mints version and effective_at); audit 'energy_policy_publish'
    /// with the values, version and effective_at. InvalidPolicy before any write when a value is outside its CHECK.
    public Task<EnergyPolicy> PublishPolicyAsync(int cap, int openingBalance, TimeSpan interval, int amount, int voidRefundPct, OperatorContext op, CancellationToken ct = default);
}
```

- **CLI outputs:**
  - Every energy verb prints `correlationId=`; `open` and `grant` also print `accountId=`.
  - `energy open` prints `energyOpened=opened|already`, `balance=` and `policyVersion=`.
  - `energy grant` prints `amount=`, `balanceAfter=`, `regenerated=` and `replayed=true|false`.
  - `energy policy publish` prints `policyVersion=`, `effectiveAt=`, `cap=`, `openingBalance=`, `intervalSeconds=`, `amount=` and `voidRefundPct=`.
- **CLI exit codes:**
  - `NoWallet` → 65, with a message naming `energy open`;
  - `IdempotencyKeyReused` → 66;
  - `BalanceLimit`, `InvalidAmount` and `InvalidPolicy` → 64;
  - busy → 75.
- **Confirmation:**
  - `energy open` and `energy grant` change an existing account, so they print the `target.*` lines and need confirmation; the target lines gain `target.energyBalance=`, or `(no wallet)`.
  - `energy policy publish` changes every wallet's future, so it prints `target.policy*` lines (current and proposed values) and also needs `--yes` or typing `publish`.

- [ ] **Step 1: Write the failing tests:**
  - **Grants:**
    - `A_grant_materializes_first_then_credits_and_writes_entries_in_order`
    - `A_replayed_key_returns_the_stored_result_and_writes_no_entry`
    - `A_reused_key_with_another_amount_is_refused_and_writes_nothing`
    - `A_reused_key_for_another_account_is_refused_and_writes_nothing` (sequential, and concurrent first use)
    - `Twenty_concurrent_grants_with_one_key_credit_once`
    - `Twenty_concurrent_grants_with_distinct_keys_all_credit_and_the_chain_holds`
    - `A_grant_may_exceed_the_cap_and_regeneration_then_pauses` (E6)
    - `A_grant_past_int_MaxValue_is_BalanceLimit_and_writes_nothing`
    - `A_grant_above_the_configured_maximum_is_InvalidAmount`
    - `A_grant_without_a_wallet_is_NoWallet`
    - `A_grant_to_an_unknown_account_is_NotFound`
    - `A_grant_to_a_suspended_account_is_allowed`
  - **Failure injection and locks:**
    - `A_failure_writing_the_audit_rolls_back_the_grant_and_its_regen_entry` (a test-only trigger on `operator_actions`)
    - `A_grant_waiting_on_a_held_wallet_lock_times_out_busy_and_writes_nothing` (bounded `lock_timeout`; CLI exit 75)
    - `A_grant_whose_connection_is_terminated_mid_transaction_writes_nothing` (`pg_terminate_backend` on the waiting backend)
    - `Grants_opens_and_account_lifecycle_commands_on_one_account_never_deadlock` (concurrent grant, open, suspend and backfill on one account; all finish)
  - **Opening and policies:**
    - `Concurrent_opens_of_one_account_open_one_wallet`
    - `Opening_an_existing_wallet_reports_already_and_changes_nothing`
    - `Publishing_a_policy_mints_the_next_version_with_a_database_effective_time_and_audits_it`
    - `A_lower_cap_never_reduces_a_balance` (E2)
    - `An_invalid_policy_is_refused_before_any_write`
  - **CLI:**
    - `Energy_verbs_print_their_outputs_and_exit_codes` (theory)
    - `Energy_grant_open_and_publish_need_confirmation`
    - `Energy_verbs_never_load_the_catalogue` (the refusing-catalogue store, as in Stage 4's boundary tests)
    - `Grant_output_and_audit_carry_no_secret` (`LogSecrecyTests` style)
  - **Settings:** `EnergySettings_defaults_bounds_and_refusals`
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** the classes plus `AdminTests`, `AdminConfirmationTests` and `AdminCatalogueBoundaryTests` (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: idempotent development grants, wallet opening and policy publication as audited, confirmed operator verbs`.

## Task 5: Reconciliation

**Files:**
- Create `src/Accounts.Persistence/Energy/EnergyReconciler.cs` and `src/Accounts.Api/Energy/EnergyReconcileService.cs`.
  - The service is a `BackgroundService` whose `ExecuteAsync` first waits one interval on a `PeriodicTimer`, never sweeping during startup (E4).
  - Each sweep is wrapped in try/catch: a failure is logged on `Blackglass.Energy` (SQLSTATE only) and the next tick still runs.
- Modify `ServeCommand` (registers the service when the setting isn't 0) and `AdminCommand`.
  - `energy reconcile` prints `walletsChecked=`, `mismatches=` and `anomaliesRecorded=`.
  - It exits 0 even when it finds mismatches: the anomaly is the signal.
- Test `tests/Accounts.Integration.Tests/Database/EnergyReconcilerTests.cs`; extend `LogSecrecyTests`.

**Interfaces (produced):**

```csharp
public sealed record EnergyMismatch(long AccountId, string Rule, int Balance, long LedgerSum, int? LastBalanceAfter);
public sealed record ReconcileReport(int WalletsChecked, IReadOnlyList<EnergyMismatch> Mismatches, int AnomaliesRecorded);

public sealed class EnergyReconciler(NpgsqlDataSource db)
{
    /// One repeatable-read, read-only snapshot over every wallet and its ledger (a concurrent grant is seen whole or not
    /// at all). Applies the 0006 invariant rules. Then, in a separate short transaction, inserts per mismatch one
    /// anomaly_events row (kind 'energy_ledger_mismatch', dedupe_key '<account>:<rule>' (Checkpoint 1 F4: the numbers go in detail, so a moving balance doesn't open a new anomaly each sweep),
    /// detail {rule, balance, ledgerSum, lastBalanceAfter}) with ON CONFLICT DO NOTHING on anomaly_one_outstanding, so
    /// concurrent sweeps and repeated runs record one outstanding row per finding. Never updates a wallet or the ledger.
    public Task<ReconcileReport> RunAsync(CancellationToken ct = default);
}
```

- [ ] **Step 1: Write the failing tests:**
  - **Reports:**
    - `A_clean_database_reports_no_mismatch`
    - `A_corrupted_balance_raises_one_anomaly_and_is_left_as_it_is` (the balance and ledger are compared before and after)
    - `A_second_run_does_not_duplicate_an_outstanding_anomaly`
    - `Two_concurrent_sweeps_record_one_anomaly`
    - `A_reviewed_anomaly_is_raised_again_if_the_mismatch_persists`
    - `Each_rule_is_reported` (sum, last entry, broken chain, missing or misplaced `open`, progress ≥ interval)
    - `Reconciliation_during_concurrent_grants_reports_nothing`
    - `A_failed_anomaly_write_changes_no_balance_and_reports_the_failure` (a test-only trigger)
  - **The hosted service:**
    - `The_service_sweeps_on_its_interval_and_logs_counts_without_secrets` (constructed directly with a short interval)
    - `A_database_failure_in_one_sweep_is_logged_and_the_next_sweep_still_happens`
    - `Serve_starts_and_answers_health_while_the_first_sweep_is_held` (a gated reconciler)
    - `Zero_disables_the_service`
  - **CLI:** `Energy_reconcile_prints_counts_and_exits_zero_with_mismatches`
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** the classes (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: read-only wallet and ledger reconciliation with deduplicated anomalies, hourly in the API and on demand`.

## Task 6: `GET /v1/me` energy

**Files:**
- Create `src/Accounts.Persistence/Energy/EnergyReader.cs`.
- Modify `PlayerReadStore`: add `GetMeAsync` → `MeView(long? InventoryVersion, EnergyView? Energy)`.
  - It runs one `read committed, read only` transaction: the shared policy lock, then **one statement** that returns the inventory version, the wallet, `clock_timestamp()` and the policies from the wallet's version (a single snapshot taken after the lock).
  - Then `EnergyMath.Materialize` in memory only.
- Also modify `AuthEndpoints` (`MeResponse` gains `Energy`) and the DTO audit test's expectations.
- Test `tests/Accounts.Integration.Tests/Database/MeEnergyApiTests.cs`; extend `ApiTests` and `PlayerApiTests` where they pin the `/v1/me` shape.

**Interfaces (produced):**

```csharp
public sealed record EnergyView(int Balance, int Cap, DateTime? NextRegenAt);
public sealed record MeView(long? InventoryVersion, EnergyView? Energy);
// JSON: {"accountId":"<Base58>","inventoryVersion":n|null,"energy":{"balance":n,"cap":n,"nextRegenAt":"<UTC ISO-8601, Z>"|null}|null}
// cap is the applicable policy's cap; energy is null when the account has no wallet (E3), never a zero balance.
```

- [ ] **Step 1: Write the failing tests:**
  - **Shape and projection:**
    - `Me_returns_exactly_accountId_inventoryVersion_and_energy` (exact member set)
    - `Me_projects_regeneration_without_writing` (a backdated anchor: the projected balance rises; the wallet row, the ledger count and the anchor are unchanged)
    - `Me_projects_across_a_policy_transition_as_a_materialization_would`
    - `Me_shows_a_balance_above_the_cap_with_no_next_regeneration`
    - `Me_shows_null_energy_for_an_account_without_a_wallet_and_creates_none`
    - `Me_nextRegenAt_is_now_plus_the_remaining_progress_in_UTC`
  - **Consistency and headers:**
    - `Me_reads_inventory_and_energy_from_one_snapshot` (a grant committing during the read is seen whole or not at all; the held-lock pattern)
    - `Me_is_no_store_and_rate_limited_as_before`
- [ ] **Step 2: Run** (expected FAIL), **implement**, **run** the classes (expected PASS).
- [ ] **Step 3: Commit.** `Phase 13 Stage 6: GET /v1/me shows the projected energy balance, read-only`.

**Checkpoint 2 (independent review, most capable model):** Tasks 3–6. Focus: the lock order and the advisory leaf lock, idempotency under concurrency, read-only projection, failure atomicity, the audit and log contents.

## Task 7: Smoke and VM

- **Smoke script** (`deploy/services/tests/smoke-test.sh`), after its existing sections:
  - `account create` prints `energyOpened=opened energyBalance=50`;
  - `GET /v1/me` shows `energy.balance` 50, `cap` 50 and `nextRegenAt` null;
  - `energy grant --amount 5 --yes` with a fresh UUID prints `balanceAfter=55 replayed=false`, and the same key again prints `replayed=true balanceAfter=55`;
  - `energy reconcile` prints `mismatches=0`;
  - no key or token appears in the transcript (the Stage 5 printf and `--config -` patterns).
- **VM runs**, from committed HEAD via `vm-sync.sh`:
  - smoke (exit 0);
  - restore drill (exit 0; the `0006` invariant runs with the others);
  - off-host probe (5080, 5432 and 55432 closed; SSH answers).
- **Evidence:** `.claude/scratchpad/phase13/stage-06/`.
- **Commit:** `Phase 13 Stage 6: smoke test covers wallet opening, a replayed grant and reconciliation`.

## Task 8: Docs as built, final review, stop

- Spec (as built for §0.6, §6.1, §15, §16.1, §16.4, §19), README (an energy section, the env table rows, `0006` privileges, the CLI verbs), the stage plan's status, `CLAUDE.md`, and this plan's "As built" (measured counts, rulings, departures, deferred minors).
- The whole-branch review is Checkpoint 3; resolve Critical and Important findings with failing tests first.
- **Completion checks:**
  - backend ALL with counts (low-memory recipe);
  - the VM smoke, drill and probe;
  - `git diff --stat main...HEAD` showing only the paths listed under Touches, and **no Unity runtime, editor, scene, asset or test path**: the recorded justification for not running the Unity suites;
  - migrations 0000–0005 byte-identical;
  - the owner's settings files and `.claude/` preserved;
  - no `bg_t_` database or SSH tunnel left behind.
- **Stop** before Stage 7, merging, or pushing implementation commits.

---

## Acceptance criteria

1. `0006_energy` applies to a Stage 5 database; 0000–0005 are unchanged; the 0000–0006 invariants hold after every energy test and after the restore drill.
2. Regeneration reads only `clock_timestamp()` after the wallet and policy locks. It is materialized under the wallet lock and writes exactly one `regen` entry per gaining materialization, keyed by the consumed anchor. Twice in a row, or ten times concurrently, gives one entry. A clock behind the anchor changes nothing.
3. Caps and partial progress behave as §A states. A balance at or above the cap never regenerates and is never reduced.
4. Policy transitions are non-retroactive and path-independent (the property tests), and racing publications are serialized by the advisory lock (the integration tests). Published versions are immutable, with database-set effective times.
5. New accounts open full in the account's transaction; earlier accounts open only through the audited, idempotent `energy open`; no GET creates a wallet; a missing wallet reads as `energy: null`.
6. Development grants are confirmed, audited and idempotent per key. Replays (sequential and 20 concurrent) credit once and return the stored result. A reused key with other parameters is refused with nothing written. Overflow and `BLACKGLASS_ENERGY_GRANT_MAX` are enforced.
7. `balance >= 0` and `balance_after >= 0` hold, each kind's sign is enforced, the ledger is append-only, and a wallet balance without its ledger entry cannot commit, all proven by direct SQL.
8. Reconciliation is read-only on balances. It reports no false mismatch under concurrent grants and records one outstanding anomaly per finding, even with concurrent sweeps. It never delays API startup.
9. `GET /v1/me` returns `energy: {balance, cap, nextRegenAt}` (or null) from one snapshot, without writing.
10. Every injected failure (ledger insert, audit insert, wallet insert at account creation, anomaly insert, lock timeout, terminated connection) leaves wallets, the ledger and accounts exactly as before.
11. The app role's privileges are exactly Task 2's table; the outsider role reads nothing.
12. No Unity path, mission-VM, Docker networking, SSH, firewall or exposure change; no live deployment.

## Out of scope

- Admission and attempts, reservations, holds, charges, entry costs, releases, refunds (the `void_refund_pct` column exists, unused), settlement and insurance (Stages 7–9).
- Operator `adjust` entries (the kind exists in the CHECK; the verb comes with the first need), and the HTTP grant route (E5).
- Purchases, premium currency, trading, and any acquisition of energy other than regeneration and development grants.
- Automatic retry of any energy transaction.
- Any Unity change.

---

## As built (2026-10-16)

**Branch `phase13-stage6`.**
- **Pushed:** the documentation baseline, up to `b4bbd5b` (owner-authorized).
- **Not pushed:** the implementation commits `6af495a..e89842e` (21 commits on top of the baseline).
- **Schema version 6** (`0006_energy`). Migrations 0000–0005 are byte-identical to `main` (`62cbfcf`).

**Tasks and commits**

| Task | Commits |
|---|---|
| 0 rulings | `7dd420b`, `6b0a7f6` (pre-authorization docs), `b4bbd5b` |
| 1 `EnergyMath` | `6af495a`, `fe54f59`, `1ef778e` |
| 2 `0006_energy` | `98472c9`, `c1ed25f`, `d3b7624`, `c653774` (comment) |
| 3 opening and materialization | `04d72ac`, `ca3436b` |
| 4 operator verbs | `6f20d94`, `dde35a1`, `e4b03ba` |
| 5 reconciliation | `d8e4d77`, plus `842cd97` (Checkpoint 2) |
| 6 `GET /v1/me` | `a742bff`, `f626a26` |
| 7 smoke | `3761f09`, plus the pattern fix in `e89842e` |
| 8 docs | `305d4e7`, `59aac9c`, `e45b72c`, `e89842e` |

**Measured** (low-memory recipe: `-m:1` build, then each test project with MaxCpuCount 1 and xUnit MaxParallelThreads 2; no Unity running):

| Where | Domain | Integration | Total |
|---|---|---|---|
| Final, at `e45b72c` (`e89842e` changes only docs and the smoke script) | 500/0 | 1055/0 (34 m 43 s) | **1555/0**, exit 0 |
| Stage 5 base | 460 | 790 | 1250 |
| Added by Stage 6 | +40 | +265 | +305 |

Checkpoint suites:
- At `c1ed25f`: Domain 500/0, Integration 883/1. The one failure was a trigger-list pin missing 0006, fixed in `d3b7624`.
- At `d8e4d77`: 500/0 and 1051/0.

The owner rulings' tests are named in the Checkpoint 3 acceptance table. Among them:
- a microsecond-by-microsecond oracle (1500 cases) and two path-independence property sweeps (4000 cases each);
- ten concurrent materializations giving one entry;
- 20 concurrent same-key grants crediting once;
- a concurrent first use of one key on two accounts;
- a queued publication draining with in-flight holders and new requesters, with no deadlock;
- injected failures at the ledger insert, the audit insert, the wallet insert at account creation and the anomaly insert;
- lock timeouts on a grant, on account creation and on a publication;
- a terminated connection;
- two concurrent sweeps;
- reconciliation during 20 grants;
- `/v1/me` proven read-only on a read-write source.

Mutation checks proved that each concurrency and ordering test fails when its protection is removed. A leaf violation surfaced as 40P01.

**VM** (`bg-dev-services01`, throwaway projects only):
- **At `59aac9c`:**
  - smoke 46 ok / 0 FAIL, exit 0, including the five energy checks;
  - off-host probe: 5080, 5432 and 55432 closed, 22 answers;
  - restore drill 16 ok, `DRILL PASS`, 4 invariant files held at schema 6, exit 0.
- **At `e89842e`:** smoke 46 ok / 0 FAIL, exit 0, and the probe the same.
- **Hygiene:** no key or token in any transcript. After the runs only `blackglass-testdb` remains, with listeners on 22 and loopback.
- **Evidence:** `.claude/scratchpad/phase13/stage-06/` (local, not committed).

**Unity suites not run.** `git diff --name-only 62cbfcf..e89842e` lists no `Assets/`, `ProjectSettings/`, `Packages/` or `UserSettings/` path: no Unity runtime, editor, scene, asset or test path changed (owner condition). The owner's two settings files are unchanged and unstaged (diff hash identical before and after); `.claude/` was never staged.

**Reviews**

| Review | Model | Scope | Verdict |
|---|---|---|---|
| Task 1 | Sonnet | `EnergyMath` | Approved: 0 Critical / 0 Important / 4 Minor. Two fixed: an independent oracle, `NextRegenAt` saturation |
| Checkpoint 1 | Opus, independent | Tasks 1–2 | Approved with fixes: 0 / 1 / 10. I1 (ledger chain at insert) and S1–S5 fixed in `d3b7624`. Re-review approved; N1 fixed as a comment |
| Task 3 | Sonnet | | Approved: 0 / 1 / 4. I1 (clock after the policy lock) fixed and mutation-checked |
| Task 4 | Sonnet | | Approved: 0 / 0 / 5. Four fixed |
| Task 6 | Sonnet | | Pass: 0 / 0 / 5. Four fixed |
| Checkpoint 2 | Opus, independent | Tasks 3–6, and Task 5's review | Approved with fixes: 0 / 0 / 7. All fixed (code, tests, docs) |
| Checkpoint 3 | Opus, independent | Whole branch | **Ready to merge**: 0 / 0 / 5. M1–M4 fixed in `e89842e` |

**Rulings** (each with what it costs if wrong):
1. `NextRegenAt` counts from `max(now, anchor)`. Cost: one line.
2. The ledger key CHECK is strict, using `starts_with` and then per-kind patterns. Cost: none, since 0006 was applied only to test databases.
3. Deferred consistency checks are enforced on both sides, wallet and ledger. Cost: one trigger per ledger insert.
4. `anomaly_events.reviewed_at` stays unwritable by the app until anomaly review exists. Cost: one grant later.
5. Checkpoints 1 and 2 serve as the task reviews for Tasks 2 and 5. Cost: none; the checkpoints are stricter.
6. The chain trigger's comment states what a writer outside the wallet lock voids; there is no in-trigger lock, which would add a deadlock path. Cost: a later migration adds the lock.
7. The grant audit records the idempotency key. Cost: one field.
8. `serve` validates both energy settings. Cost: `serve` refuses a setting it doesn't use.
9. `energy reconcile` names each finding (`mismatch=<Base58>:<rule>`). Cost: extra output lines.
10. On-demand reconcile writes no audit row. Cost: a later migration adds an action value.
11. A sweep's anomalies stay all-or-nothing, and the foreign-key key-share is documented in §15.3. Cost: a sweep may wait behind an operator command and retry an hour later.
12. A lowered `BLACKGLASS_ENERGY_GRANT_MAX` refuses a same-key retry with exit 64 before the replay lookup. This is harmless (no double grant) and left as is. Cost: an operator raises the limit to replay.

**Departures from revision 2's text** (all strengthenings, recorded in spec §0.6 "As built"):
- Each change below is enforced by the database:
  - `energy_ledger_chain`;
  - the ledger-side commit check;
  - per-kind key patterns;
  - the publication lock taken inside the `effective_at` trigger;
  - no year or month part in intervals;
  - a progress bound on write;
  - no TRUNCATE.
- The anomaly dedupe key is `<Base58>:<rule>`.
- `energy open` prints `advisory=true` for an existing wallet.
- Grants give unknown-outcome guidance.
- One internal reader serves the clock and the policies; `EnergyReader` serves `/v1/me`.

**Deferred minors:**
- Task 1:
  - a trivial micro-to-week assertion;
  - `VoidRefundPct` is not validated in the domain (the database CHECK covers it).
- Checkpoint 1 S3 nit: `regen:` admits non-canonical digits. Harmless, given the forward-only anchor and the chain.
- Task 4 M5: two publishers can confirm against the same preview. Both are audited.
- Checkpoint 3 M5: the HTTP route's 503 behind a held publication lock is pinned at the store and the CLI only.

**Residual risks** (Checkpoint 3):
- A stalled publication queues `/v1/me`, account creation, grants and opens for up to `lock_timeout`; publish at quiet times.
- Grants hold the `accounts` row, so logins and edits on that account wait behind them.
- Reconciliation does full-table sweeps under the 15 s `statement_timeout` (Phase 14: incremental reconciliation).
- One database role serves both the API and the admin CLI.
- Earlier accounts need `energy open` one by hand after a deployment.

**Stage 7 obligations** (recorded in spec §0.6 and §15.3, `CLAUDE.md` and the stage plan):
- one global unique key per transaction after the energy policy lock;
- steps 5–6 of the §15.3 order may only read without locking after it;
- every energy writer locks the wallet before inserting entries;
- no automatic retry of energy transactions;
- `InventoryLocked` must become a mandatory, fail-closed, database-backed check before admission is enabled.

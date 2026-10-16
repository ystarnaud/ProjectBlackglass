-- 0006 energy (Stage 6; spec §0.6 revision 2.6, §6.1, §15.1-§15.2; owner rulings E1-E7). Versioned, immutable policies
-- with a database-set effective time; one wallet per account; an append-only ledger that every balance change writes in
-- the same transaction; deduplicated anomaly records for the reconciliation. No wallet is opened here (E3): existing
-- accounts get one only through the audited `energy open` verb.

-- Policies. A version is never changed or removed once published (E2, E7). Its effective_at is set by the database, never
-- by the caller: the clock when the row is inserted, and always strictly after the previous version's (E2's transition
-- order). Publication holds the exclusive policy advisory lock while it inserts, so no materialization can have read a
-- `now` later than a version it did not see.
create table energy_policies (
  version integer generated always as identity primary key,
  cap integer not null check (cap > 0),
  opening_balance integer not null check (opening_balance >= 0),
  regen_interval interval not null check (regen_interval > interval '0' and regen_interval <= interval '7 days'),
  regen_amount integer not null check (regen_amount > 0),
  void_refund_pct integer not null check (void_refund_pct between 0 and 100),
  effective_at timestamptz not null unique,
  created_at timestamptz not null default statement_timestamp(),
  check (opening_balance <= cap));

-- Whatever the caller supplied, effective_at becomes the later of the clock and the latest effective_at plus 1 µs (a
-- clock that stepped back still yields a strictly increasing order). It runs as the invoker: the app reads the table.
-- Like every trigger function here it stays unexecutable by the app and PUBLIC (0001's global default revoke); triggers
-- fire whatever the invoker's EXECUTE privilege.
create function energy_policy_effective_at() returns trigger language plpgsql as $$
begin
  new.effective_at := greatest(clock_timestamp(),
    coalesce((select max(effective_at) from energy_policies) + interval '1 microsecond', '-infinity'::timestamptz));
  return new;
end $$;

create trigger energy_policy_effective_at before insert on energy_policies
  for each row execute function energy_policy_effective_at();

-- Immutable, for the owner too: only session_replication_role = replica (superuser only) skips it.
create function energy_policy_is_immutable() returns trigger language plpgsql as $$
begin
  raise exception 'energy policies are immutable once published'
    using errcode = 'check_violation', constraint = 'energy_policy_is_immutable';
end $$;

create trigger energy_policy_is_immutable before update or delete on energy_policies
  for each row execute function energy_policy_is_immutable();

-- Version 1 (E7, §19; provisional): cap 50, opening balance 50 (E1: a wallet opens full), 1 energy per 10 minutes,
-- void refund 100 %. Inserted after its trigger so the database sets its effective_at.
insert into energy_policies (cap, opening_balance, regen_interval, regen_amount, void_refund_pct)
  values (50, 50, interval '10 minutes', 1, 100);

-- One wallet per account. regen_anchor is the database time up to which regeneration is accounted, and only moves forward;
-- regen_progress_us is the progress toward the next unit under policy_version (kept below that policy's interval, checked
-- by the invariant). A balance may exceed the cap (E6).
create table energy_wallets (
  account_id bigint primary key references accounts,
  balance integer not null check (balance >= 0),
  regen_anchor timestamptz not null,
  regen_progress_us bigint not null check (regen_progress_us >= 0),
  policy_version integer not null references energy_policies);

-- Every balance change, append-only. Keys: 'open' (once per account), 'regen:<consumed anchor, µs since the Unix epoch>',
-- 'dev_grant:<operator uuid>' (also unique across accounts). Each kind's delta sign is a CHECK. attempt_id is unused until
-- Stage 7. The two CHECKs that read two columns are named (PostgreSQL would call them energy_ledger_check and _check1).
-- A key is the bare kind or the kind, ':' and a non-empty suffix; starts_with, not LIKE, whose '_' would match any
-- character in 'dev_grant' and 'void_refund'.
create table energy_ledger (
  entry_id bigint generated always as identity primary key,
  account_id bigint not null references energy_wallets,
  attempt_id uuid,
  kind text not null check (kind in ('open', 'regen', 'reserve', 'charge', 'release', 'settle', 'void_refund', 'dev_grant', 'adjust')),
  delta integer not null,
  balance_after integer not null check (balance_after >= 0),
  idempotency_key text not null
    constraint energy_ledger_idempotency_key_check check (length(idempotency_key) <= 200 and idempotency_key !~ '[[:space:][:cntrl:]]'
                                                          and (idempotency_key = kind or (starts_with(idempotency_key, kind || ':')
                                                                                          and length(idempotency_key) > length(kind) + 1))),
  created_at timestamptz not null default statement_timestamp(),
  constraint energy_ledger_delta_sign_check check ((kind = 'open' and delta >= 0) or (kind in ('regen', 'dev_grant', 'release', 'void_refund') and delta > 0)
      or (kind = 'reserve' and delta < 0) or (kind in ('charge', 'settle') and delta = 0) or (kind = 'adjust' and delta <> 0)),
  unique (account_id, idempotency_key));
create index energy_ledger_account_entry on energy_ledger (account_id, entry_id);
create unique index energy_dev_grant_key_global on energy_ledger (idempotency_key) where kind = 'dev_grant';

-- Findings of the reconciliation (E4). The partial unique index admits one outstanding (unreviewed) row per identical
-- finding, so concurrent sweeps record it once (insert ... on conflict do nothing).
create table anomaly_events (
  id bigint generated always as identity primary key,
  kind text not null check (length(kind) between 1 and 100 and kind ~ '^[a-z0-9_]+$'),
  account_id bigint references accounts,
  host_id text,
  build_revision text,
  attempt_id uuid,
  dedupe_key text not null check (length(dedupe_key) between 1 and 500),
  detail jsonb not null,
  created_at timestamptz not null default statement_timestamp(),
  reviewed_at timestamptz,
  reviewed_note text);
create unique index anomaly_one_outstanding on anomaly_events (kind, dedupe_key) where reviewed_at is null;

-- The ledger is append-only, for the owner too (the app also holds no UPDATE or DELETE on it).
create function energy_ledger_is_append_only() returns trigger language plpgsql as $$
begin
  raise exception 'the energy ledger is append-only'
    using errcode = 'check_violation', constraint = 'energy_ledger_is_append_only';
end $$;

create trigger energy_ledger_is_append_only before update or delete on energy_ledger
  for each row execute function energy_ledger_is_append_only();

-- Neither the anchor nor the policy version ever moves back (owner requirement 2: a clock stepping back causes no anchor
-- regression, so regen keys, derived from consumed anchors, never repeat).
create function energy_wallet_forward_only() returns trigger language plpgsql as $$
begin
  if new.regen_anchor < old.regen_anchor then
    raise exception 'the energy regeneration anchor never moves back'
      using errcode = 'check_violation', constraint = 'energy_wallet_forward_only';
  end if;
  if new.policy_version < old.policy_version then
    raise exception 'the energy policy version of a wallet never moves back'
      using errcode = 'check_violation', constraint = 'energy_wallet_forward_only';
  end if;
  return new;
end $$;

create trigger energy_wallet_forward_only before update on energy_wallets
  for each row execute function energy_wallet_forward_only();

-- Every balance change has its ledger entry in the same transaction (owner requirement 4): at commit, a wallet that was
-- inserted or updated, or that received a ledger entry, must hold the balance_after of its latest entry (by entry_id); a
-- wallet with no entry fails. So neither a wallet write without its entry nor an entry without its wallet write commits.
-- One function serves both constraint triggers and names the failing one (TG_NAME). It re-reads the wallet rather than
-- trusting NEW, since one transaction may write the row several times (each queues an event, all judged on the final
-- rows).
create function energy_wallet_matches_ledger() returns trigger language plpgsql as $$
declare
  wallet_balance integer;
  ledger_balance integer;
begin
  select balance into wallet_balance from energy_wallets where account_id = new.account_id;
  if not found then
    return null;
  end if;
  select balance_after into ledger_balance from energy_ledger where account_id = new.account_id order by entry_id desc limit 1;
  if ledger_balance is distinct from wallet_balance then
    raise exception 'an energy wallet balance must equal the balance_after of its latest ledger entry'
      using errcode = 'check_violation', constraint = tg_name;
  end if;
  return null;
end $$;

create constraint trigger energy_wallet_matches_ledger after insert or update on energy_wallets
  deferrable initially deferred
  for each row execute function energy_wallet_matches_ledger();

create constraint trigger energy_ledger_matches_wallet after insert on energy_ledger
  deferrable initially deferred
  for each row execute function energy_wallet_matches_ledger();

-- The energy operator verbs are audited (0004's list is extended here, forward-only).
alter table operator_actions drop constraint operator_actions_action_check;
alter table operator_actions add constraint operator_actions_action_check check (action in (
  'account_create', 'credential_rotate', 'credential_revoke', 'sessions_revoke', 'account_suspend', 'account_reactivate',
  'starter_backfill',
  'energy_open', 'energy_grant', 'energy_policy_publish',
  'revalidate', 'participant_void', 'host_declared_lost', 'host_drain', 'host_enable', 'build_disable', 'energy_adjust'));

-- accounts_app privileges, narrowed from 0000's table-level defaults. It reads all four tables; it inserts through the
-- listed columns only (never an identity, an effective or creation time, or a review); it updates only a wallet's
-- balance, anchor, progress and policy version (which also lets it lock a wallet FOR UPDATE); it never deletes or
-- truncates, and holds nothing on the identity sequences (inserts need none). The policy advisory lock
-- (pg_advisory_xact_lock[_shared]) is a built-in granted to PUBLIC.
revoke insert, update, delete on energy_policies from accounts_app;
grant insert (cap, opening_balance, regen_interval, regen_amount, void_refund_pct) on energy_policies to accounts_app;
revoke insert, update, delete on energy_wallets from accounts_app;
grant insert (account_id, balance, regen_anchor, regen_progress_us, policy_version),
  update (balance, regen_anchor, regen_progress_us, policy_version) on energy_wallets to accounts_app;
revoke insert, update, delete on energy_ledger from accounts_app;
grant insert (account_id, attempt_id, kind, delta, balance_after, idempotency_key) on energy_ledger to accounts_app;
revoke insert, update, delete on anomaly_events from accounts_app;
grant insert (kind, account_id, host_id, build_revision, attempt_id, dedupe_key, detail) on anomaly_events to accounts_app;
revoke all on sequence energy_policies_version_seq, energy_ledger_entry_id_seq, anomaly_events_id_seq from accounts_app;

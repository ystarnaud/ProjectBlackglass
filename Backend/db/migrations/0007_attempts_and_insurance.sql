-- 0007 attempts and insurance (Stage 7; spec §0.7 revision 2.7, §6.2, §7.1-§7.3, §15.1-§15.3; owner rulings S7-D1-S7-D9).
-- Insurance policy versions, timed account coverages and their append-only activations; versioned mission templates; the
-- host rows the service-health check reads; mission attempts, their participants, energy holds, insurance snapshots and
-- deploy requests. Stage 7 admits and releases; it never starts, charges or settles (Stages 8 and 9).
-- Order: tables and their triggers, then the seeds (after their tables' triggers exist), then the forward changes to
-- 0006's energy_ledger, then the operator action list, then the privileges.
-- Identifiers (policy, template and host ids) use the catalogue definition id form: lower-case letters and digits in
-- groups joined by '.' or '-', at most 64 characters. None of these tables has an identity column, so 0007 adds no
-- sequence. Every row stamp defaults to statement_timestamp() (0001's rule: a statement that waited stamps after the wait).

-- ---- trigger functions ----

-- One refusal for every row or table that must not change: UPDATE or DELETE (row level) and TRUNCATE (statement level,
-- which skips row triggers, directly or through a cascade). TG_ARGV[0] names the constraint the error reports. Like every
-- trigger function here it runs as the invoker and stays unexecutable by the app and PUBLIC (0001's global default revoke);
-- triggers fire whatever the invoker's EXECUTE privilege. Only session_replication_role = replica (superuser only) skips
-- them, for the owner too.
create function refuse_row_change() returns trigger language plpgsql as $$
begin
  raise exception '% on % is refused', tg_op, tg_table_name
    using errcode = 'check_violation', constraint = tg_argv[0];
end $$;

-- ---- insurance ----

-- Policies (S7-D3): a version is immutable once published; rebalancing publishes a new version (Task 3's audited verb,
-- which computes the next version under a per-policy advisory lock; the primary key is the backstop). The terms are the
-- §6.2 JSON object, validated by InsuranceTerms before insert.
create table insurance_policies (
  policy_id text not null check (length(policy_id) <= 64 and policy_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  version integer not null check (version >= 1),
  terms jsonb not null check (jsonb_typeof(terms) = 'object'),
  created_at timestamptz not null default statement_timestamp(),
  primary key (policy_id, version));

create trigger insurance_policy_is_immutable before update or delete on insurance_policies
  for each row execute function refuse_row_change('insurance_policy_is_immutable');
create trigger insurance_policies_no_truncate before truncate on insurance_policies
  for each statement execute function refuse_row_change('insurance_policy_is_immutable');

-- Activations: append-only, one per (account, idempotency key). Each records the coverage's expiry before it (null when
-- there was none) and after it: greatest(previous expiry, now) + duration, always later than the previous expiry
-- (insurance_activation_extends). The duration carries no year or month part (0006's regen_interval rule: Npgsql can't
-- read one as a TimeSpan). Phase 13 has development activations only (source 'dev_grant').
-- insurance_activation_identity lets a coverage and a snapshot name an activation together with its account and policy
-- version, so neither can borrow another account's activation or another policy's.
create table insurance_activations (
  activation_id uuid primary key,
  account_id bigint not null references accounts,
  policy_id text not null,
  policy_version integer not null,
  duration interval not null check (duration > interval '0' and extract(year from duration) = 0 and extract(month from duration) = 0),
  previous_expires_at timestamptz,
  resulting_expires_at timestamptz not null,
  source text not null check (source in ('dev_grant')),
  idempotency_key uuid not null,
  created_at timestamptz not null default statement_timestamp(),
  constraint insurance_activations_policy_fkey foreign key (policy_id, policy_version) references insurance_policies,
  constraint insurance_activation_extends check (previous_expires_at is null or resulting_expires_at > previous_expires_at),
  constraint insurance_activation_identity unique (activation_id, account_id, policy_id, policy_version),
  unique (account_id, idempotency_key));

create trigger insurance_activation_is_append_only before update or delete on insurance_activations
  for each row execute function refuse_row_change('insurance_activation_is_append_only');
create trigger insurance_activations_no_truncate before truncate on insurance_activations
  for each statement execute function refuse_row_change('insurance_activation_is_append_only');

-- At most one coverage per account (benefits never multiply), set by its latest activation, which must be the account's
-- own and of the coverage's policy version. Active means starts_at <= now < expires_at. The activation row is inserted
-- before the coverage row it sets (the foreign key is immediate).
create table insurance_coverages (
  account_id bigint primary key references accounts,
  policy_id text not null,
  policy_version integer not null,
  activation_id uuid not null,
  starts_at timestamptz not null,
  expires_at timestamptz not null,
  constraint insurance_coverage_expires_after_start check (expires_at > starts_at),
  constraint insurance_coverages_policy_fkey foreign key (policy_id, policy_version) references insurance_policies,
  constraint insurance_coverages_activation_fkey foreign key (activation_id, account_id, policy_id, policy_version)
    references insurance_activations (activation_id, account_id, policy_id, policy_version));

-- Forward-only (column grants can't say "one way"). On every update of a coverage row:
--   * with policy_id unchanged, expires_at never decreases (an extension or a version move keeps or lengthens it);
--   * policy_id may change only once the old coverage has expired: old.expires_at <= clock_timestamp() at the update
--     (cross-tier while active is deferred, S7-D3; the writer reads its now after the coverage lock, and this clock is
--     later, so a coverage the writer saw as expired is expired here too).
create function insurance_coverage_forward_only() returns trigger language plpgsql as $$
begin
  if new.policy_id = old.policy_id and new.expires_at < old.expires_at then
    raise exception 'an insurance coverage''s expiry never moves back under the same policy'
      using errcode = 'check_violation', constraint = 'insurance_coverage_forward_only';
  end if;
  if new.policy_id <> old.policy_id and old.expires_at > clock_timestamp() then
    raise exception 'an insurance coverage changes policy only once it has expired'
      using errcode = 'check_violation', constraint = 'insurance_coverage_forward_only';
  end if;
  return new;
end $$;

create trigger insurance_coverage_forward_only before update on insurance_coverages
  for each row execute function insurance_coverage_forward_only();

-- ---- templates and hosts ----

-- Mission templates (S7-D1): immutable versions; the backend owns admission rules and costs, Unity owns generation and
-- gameplay. A cost of 0 is allowed (a zero-cost admission takes no energy path). Rebalancing publishes a new version.
create table mission_templates (
  template_id text not null check (length(template_id) <= 64 and template_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  version integer not null check (version >= 1),
  energy_cost integer not null check (energy_cost >= 0),
  min_operatives integer not null check (min_operatives >= 1),
  max_operatives integer not null,
  created_at timestamptz not null default statement_timestamp(),
  constraint mission_template_operative_range check (max_operatives >= min_operatives),
  primary key (template_id, version));

create trigger mission_template_is_immutable before update or delete on mission_templates
  for each row execute function refuse_row_change('mission_template_is_immutable');
create trigger mission_templates_no_truncate before truncate on mission_templates
  for each statement execute function refuse_row_change('mission_template_is_immutable');

-- Mission hosts, only what the fail-closed health check reads (S7-D2). kind 'agent' rows come from Stage 8's
-- registration; 'dev_fixture' rows only from the audited `host register-dev`, and the API counts them only with
-- BLACKGLASS_DEV_HOSTS=enabled (never in production). A host is disabled unless its insert says otherwise. Stage 8 adds
-- what claims need.
create table mission_hosts (
  host_id text primary key check (length(host_id) <= 64 and host_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  kind text not null check (kind in ('dev_fixture', 'agent')),
  secret_hash bytea not null unique check (length(secret_hash) = 32),
  enabled boolean not null default false,
  drained boolean not null default false,
  capacity integer not null check (capacity >= 0),
  last_heartbeat_at timestamptz,
  created_at timestamptz not null default statement_timestamp());

-- ---- attempts ----

-- One mission instance. attempt_id is a UUIDv7 the app supplies. seed (CSPRNG, 31-bit non-negative) and retention_salt
-- (32 random bytes) never leave the backend's trusted paths (never a client response, a log, an audit row or an error).
-- cancel_reason is set exactly when cancelled, ended_at exactly when cancelled or settled. Stage 8 adds the host, port,
-- process and build columns (and the cancel reasons of its own transitions).
create table mission_attempts (
  attempt_id uuid primary key,
  created_by bigint not null references accounts,
  template_id text not null,
  template_version integer not null,
  seed integer not null check (seed >= 0),
  retention_salt bytea not null check (length(retention_salt) = 32),
  state text not null default 'admitted' check (state in ('admitted', 'assigned', 'launching', 'ready', 'running', 'ending',
                                                          'unresponsive', 'cancelled', 'settled')),
  cancel_reason text check (cancel_reason in ('no_capacity', 'player_cancel')),
  created_at timestamptz not null default statement_timestamp(),
  assign_deadline timestamptz not null,
  ended_at timestamptz,
  constraint mission_attempts_template_fkey foreign key (template_id, template_version) references mission_templates,
  constraint mission_attempt_cancel_reason_iff_cancelled check ((state = 'cancelled') = (cancel_reason is not null)),
  constraint mission_attempt_ended_at_iff_ended check ((state in ('cancelled', 'settled')) = (ended_at is not null)),
  constraint mission_attempt_template_identity unique (attempt_id, template_id, template_version));
create index mission_attempts_admitted_deadline on mission_attempts (assign_deadline) where state = 'admitted';

-- Guarded transitions (spec §7.2; every transition is also guarded by its expected state in the writer's UPDATE). An
-- attempt is inserted 'admitted'. An update may change state only along these edges, exactly:
--   admitted -> assigned -> launching -> ready -> running -> ending -> settled;
--   admitted | assigned | launching | ready -> cancelled;
--   running -> unresponsive; unresponsive -> running | ending | settled.
-- Rewriting the same state is a no-op that passes (running -> running in §7.2). attempt_id, created_by, template_id,
-- template_version, seed, retention_salt, created_at and assign_deadline never change; cancel_reason and ended_at change
-- only together with a state change (the CHECKs then say when they are set). A later stage that needs other edges or
-- columns replaces this function in its own migration.
create function mission_attempt_transition() returns trigger language plpgsql as $$
begin
  if tg_op = 'INSERT' then
    if new.state <> 'admitted' then
      raise exception 'a mission attempt starts admitted'
        using errcode = 'check_violation', constraint = 'mission_attempt_transition';
    end if;
    return new;
  end if;
  if new.attempt_id <> old.attempt_id or new.created_by <> old.created_by or new.template_id <> old.template_id
     or new.template_version <> old.template_version or new.seed <> old.seed or new.retention_salt <> old.retention_salt
     or new.created_at <> old.created_at or new.assign_deadline <> old.assign_deadline then
    raise exception 'a mission attempt''s identity, template, secrets, creation and assignment deadline never change'
      using errcode = 'check_violation', constraint = 'mission_attempt_transition';
  end if;
  if new.state = old.state then
    if new.cancel_reason is distinct from old.cancel_reason or new.ended_at is distinct from old.ended_at then
      raise exception 'a mission attempt''s cancel reason and end change only with its state'
        using errcode = 'check_violation', constraint = 'mission_attempt_transition';
    end if;
  elsif (old.state, new.state) not in (
      ('admitted', 'assigned'), ('assigned', 'launching'), ('launching', 'ready'), ('ready', 'running'), ('running', 'ending'),
      ('ending', 'settled'), ('admitted', 'cancelled'), ('assigned', 'cancelled'), ('launching', 'cancelled'),
      ('ready', 'cancelled'), ('running', 'unresponsive'), ('unresponsive', 'running'), ('unresponsive', 'ending'),
      ('unresponsive', 'settled')) then
    raise exception 'a mission attempt cannot move from % to %', old.state, new.state
      using errcode = 'check_violation', constraint = 'mission_attempt_transition';
  end if;
  return new;
end $$;

create trigger mission_attempt_transition before insert or update on mission_attempts
  for each row execute function mission_attempt_transition();
create trigger mission_attempt_is_permanent before delete on mission_attempts
  for each row execute function refuse_row_change('mission_attempt_is_permanent');
create trigger mission_attempts_no_truncate before truncate on mission_attempts
  for each statement execute function refuse_row_change('mission_attempt_is_permanent');

-- One account in an attempt (co-op compatible; admission refuses a second participant until co-op is authorized). Its
-- template is its attempt's (the composite foreign key). loadout_snapshot is the S7-D8 snapshot (the selected operatives
-- in their requested order, their advancement, bags and equipment, the account identity, the inventory version and the
-- catalogue version), immutable after admission like inventory_version and energy_cost. Later stages add the timestamps
-- of their states.
create table attempt_participants (
  attempt_id uuid not null,
  account_id bigint not null references accounts,
  template_id text not null,
  template_version integer not null,
  state text not null default 'admitted' check (state in ('admitted', 'joined', 'left', 'result_pending', 'unresponsive',
                                                          'quarantined', 'released', 'settled')),
  loadout_snapshot jsonb not null check (jsonb_typeof(loadout_snapshot) = 'object'),
  inventory_version bigint not null check (inventory_version >= 1),
  energy_cost integer not null check (energy_cost >= 0),
  primary key (attempt_id, account_id),
  constraint attempt_participants_attempt_template_fkey foreign key (attempt_id, template_id, template_version)
    references mission_attempts (attempt_id, template_id, template_version));

-- One active participation per account (spec §7.1), in PostgreSQL whatever the application does; admission also takes
-- the account's inventories row lock first, so the normal path answers 409 AttemptActive instead. The list of active
-- states is shared with the inventory lock's participation query (Task 5 pins that they agree).
create unique index one_active_participation on attempt_participants (account_id)
  where state in ('admitted', 'joined', 'left', 'result_pending', 'unresponsive', 'quarantined');

-- Guarded transitions (spec §7.2, participant states). A participant is inserted 'admitted'. An update may change state
-- only along these edges, exactly:
--   admitted -> joined | released;
--   joined -> left; left -> joined;
--   joined | left -> result_pending; result_pending -> settled;
--   joined | left | result_pending -> quarantined (an invalid manifest, §10.4); quarantined -> settled.
-- §7.2 gives the participant state 'unresponsive' no edge, so none is allowed yet; §10.3's settlement also accepts joined,
-- left and unresponsive participants directly. Stage 8 or 9 replaces this function with the edges it needs. Rewriting
-- the same state passes. Only state changes: attempt_id, account_id, template_id, template_version, loadout_snapshot,
-- inventory_version and energy_cost never do.
create function attempt_participant_transition() returns trigger language plpgsql as $$
begin
  if tg_op = 'INSERT' then
    if new.state <> 'admitted' then
      raise exception 'an attempt participant starts admitted'
        using errcode = 'check_violation', constraint = 'attempt_participant_transition';
    end if;
    return new;
  end if;
  if new.attempt_id <> old.attempt_id or new.account_id <> old.account_id or new.template_id <> old.template_id
     or new.template_version <> old.template_version or new.loadout_snapshot is distinct from old.loadout_snapshot
     or new.inventory_version <> old.inventory_version or new.energy_cost <> old.energy_cost then
    raise exception 'an attempt participant''s keys, template, snapshot, inventory version and energy cost never change'
      using errcode = 'check_violation', constraint = 'attempt_participant_transition';
  end if;
  if new.state <> old.state and (old.state, new.state) not in (
      ('admitted', 'joined'), ('admitted', 'released'), ('joined', 'left'), ('left', 'joined'), ('joined', 'result_pending'),
      ('left', 'result_pending'), ('result_pending', 'settled'), ('joined', 'quarantined'), ('left', 'quarantined'),
      ('result_pending', 'quarantined'), ('quarantined', 'settled')) then
    raise exception 'an attempt participant cannot move from % to %', old.state, new.state
      using errcode = 'check_violation', constraint = 'attempt_participant_transition';
  end if;
  return new;
end $$;

create trigger attempt_participant_transition before insert or update on attempt_participants
  for each row execute function attempt_participant_transition();
create trigger attempt_participant_is_permanent before delete on attempt_participants
  for each row execute function refuse_row_change('attempt_participant_is_permanent');
create trigger attempt_participants_no_truncate before truncate on attempt_participants
  for each statement execute function refuse_row_change('attempt_participant_is_permanent');

-- Each participant's energy hold. A positive amount was reserved with exactly one 'reserve:<attempt>' ledger entry (and is
-- given back with exactly one 'release:<attempt>' entry); a zero amount (a zero-cost template) has no entry at all. The
-- invariants (0007_attempts.sql) check both. updated_at is set by the database at the insert and at each state change.
create table energy_holds (
  attempt_id uuid not null,
  account_id bigint not null,
  amount integer not null check (amount >= 0),
  state text not null default 'reserved' check (state in ('reserved', 'charged', 'released', 'settled', 'refunded')),
  updated_at timestamptz not null default statement_timestamp(),
  primary key (attempt_id, account_id),
  constraint energy_holds_participant_fkey foreign key (attempt_id, account_id) references attempt_participants);

-- Guarded transitions. A hold is inserted 'reserved'. An update may change state only along these edges, exactly:
--   reserved -> released (Stage 7: cancel or assignment deadline);
--   reserved -> charged (Stage 8's start commit).
-- Stage 9 replaces this function to add its settle and refund edges. Rewriting the same state passes (and keeps
-- updated_at). amount, attempt_id and account_id never change; updated_at only by this trigger.
create function energy_hold_transition() returns trigger language plpgsql as $$
begin
  if tg_op = 'INSERT' then
    if new.state <> 'reserved' then
      raise exception 'an energy hold starts reserved'
        using errcode = 'check_violation', constraint = 'energy_hold_transition';
    end if;
    new.updated_at := statement_timestamp();
    return new;
  end if;
  if new.attempt_id <> old.attempt_id or new.account_id <> old.account_id or new.amount <> old.amount then
    raise exception 'an energy hold''s keys and amount never change'
      using errcode = 'check_violation', constraint = 'energy_hold_transition';
  end if;
  if new.state = old.state then
    if new.updated_at <> old.updated_at then
      raise exception 'an energy hold''s updated_at is set by the database'
        using errcode = 'check_violation', constraint = 'energy_hold_transition';
    end if;
  elsif (old.state, new.state) in (('reserved', 'released'), ('reserved', 'charged')) then
    new.updated_at := statement_timestamp();
  else
    raise exception 'an energy hold cannot move from % to %', old.state, new.state
      using errcode = 'check_violation', constraint = 'energy_hold_transition';
  end if;
  return new;
end $$;

create trigger energy_hold_transition before insert or update on energy_holds
  for each row execute function energy_hold_transition();
create trigger energy_hold_is_permanent before delete on energy_holds
  for each row execute function refuse_row_change('energy_hold_is_permanent');
create trigger energy_holds_no_truncate before truncate on energy_holds
  for each statement execute function refuse_row_change('energy_hold_is_permanent');

-- A participant's insurance snapshot (S7-D3): copied at admission when the account's coverage is active at admission's
-- now, with the activation that set it in force, its policy version and the complete terms; immutable afterwards, whatever
-- the coverage does later. A participant without one was uninsured. Cancelled attempts get no payout (Stage 9).
create table attempt_insurance (
  attempt_id uuid not null,
  account_id bigint not null,
  activation_id uuid not null,
  policy_id text not null,
  policy_version integer not null,
  terms jsonb not null check (jsonb_typeof(terms) = 'object'),
  snapshotted_at timestamptz not null default statement_timestamp(),
  primary key (attempt_id, account_id),
  constraint attempt_insurance_participant_fkey foreign key (attempt_id, account_id) references attempt_participants,
  constraint attempt_insurance_activation_fkey foreign key (activation_id, account_id, policy_id, policy_version)
    references insurance_activations (activation_id, account_id, policy_id, policy_version),
  constraint attempt_insurance_policy_fkey foreign key (policy_id, policy_version) references insurance_policies);

create trigger attempt_insurance_is_immutable before update or delete on attempt_insurance
  for each row execute function refuse_row_change('attempt_insurance_is_immutable');
create trigger attempt_insurance_no_truncate before truncate on attempt_insurance
  for each statement execute function refuse_row_change('attempt_insurance_is_immutable');

-- Deploy idempotency (S7-D5): one row per (account, Idempotency-Key), written only by a successful admission with the
-- SHA-256 of the normalized request and the stored response; the same key and hash replays it, another hash is 409 with
-- nothing written. It names the attempt its account takes part in. Immutable after insert.
create table deploy_requests (
  account_id bigint not null references accounts,
  idempotency_key uuid not null,
  request_hash bytea not null check (length(request_hash) = 32),
  attempt_id uuid not null,
  response jsonb not null check (jsonb_typeof(response) = 'object'),
  created_at timestamptz not null default statement_timestamp(),
  primary key (account_id, idempotency_key),
  constraint deploy_requests_participant_fkey foreign key (attempt_id, account_id) references attempt_participants);

create trigger deploy_request_is_immutable before update or delete on deploy_requests
  for each row execute function refuse_row_change('deploy_request_is_immutable');
create trigger deploy_requests_no_truncate before truncate on deploy_requests
  for each statement execute function refuse_row_change('deploy_request_is_immutable');

-- ---- seeds (provisional values, §19), inserted after their tables' triggers exist ----

-- Insurance policy 'standard' version 1: the §6.2 terms, verbatim.
insert into insurance_policies (policy_id, version, terms) values ('standard', 1, '{
  "covers": ["defeat", "abandoned", "disconnected", "expired"],
  "consumableRefund": { "percent": 50, "rounding": "floor", "destination": "stash" },
  "lootRetention": {
    "maxEntries": 2,
    "maxUnitsPerEntry": 3,
    "eligibleCategories": ["Consumable", "Armor", "Utility", "Weapon"],
    "rank": ["categoryPriority", "saltedHash"],
    "categoryPriority": ["Weapon", "Armor", "Utility", "Consumable"]
  },
  "progressXp": { "percent": 40, "basis": "requiredObjectivesCompleted" }
}');

-- Mission template 'procedural-standard' version 1 (S7-D1): energy cost 10, 1 to 3 operatives.
insert into mission_templates (template_id, version, energy_cost, min_operatives, max_operatives)
  values ('procedural-standard', 1, 10, 1, 3);

-- ---- forward changes to 0006's energy_ledger ----

-- No ledger row has a reserve, release, charge, settle or void_refund kind before this migration (no Stage 6 path writes
-- one, and 0006 left attempt_id unused), and every existing row has a null attempt_id, so the new constraints hold for
-- every existing row and validate at once.
-- An entry's attempt exists.
alter table energy_ledger add constraint energy_ledger_attempt_id_fkey foreign key (attempt_id) references mission_attempts;
-- The attempt-bound kinds carry their attempt; the account kinds carry none; adjust may or may not.
alter table energy_ledger add constraint energy_ledger_kind_attempt_check check (case
  when kind in ('reserve', 'release', 'charge', 'settle', 'void_refund') then attempt_id is not null
  when kind in ('open', 'regen', 'dev_grant') then attempt_id is null
  else true end);
-- 0006's per-kind key CHECK, replaced by name with every other kind's rule identical, and reserve and release tightened:
-- exactly 'reserve:' or 'release:' and the entry's own attempt_id as text (canonical lower case), so one hold reserves
-- and releases at most once per account (the unique key).
alter table energy_ledger drop constraint energy_ledger_idempotency_key_check;
alter table energy_ledger add constraint energy_ledger_idempotency_key_check check (length(idempotency_key) <= 200
  and idempotency_key !~ '[[:space:][:cntrl:]]'
  and case kind
        when 'open' then idempotency_key = 'open'
        when 'regen' then idempotency_key ~ '^regen:-?[0-9]+$'
        when 'dev_grant' then idempotency_key ~ '^dev_grant:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
        when 'reserve' then attempt_id is not null and idempotency_key = 'reserve:' || attempt_id::text
        when 'release' then attempt_id is not null and idempotency_key = 'release:' || attempt_id::text
        else idempotency_key = kind or (starts_with(idempotency_key, kind || ':') and length(idempotency_key) > length(kind) + 1)
      end);

-- ---- audit ----

-- The Stage 7 operator verbs are audited (0006's list is extended here, forward-only).
alter table operator_actions drop constraint operator_actions_action_check;
alter table operator_actions add constraint operator_actions_action_check check (action in (
  'account_create', 'credential_rotate', 'credential_revoke', 'sessions_revoke', 'account_suspend', 'account_reactivate',
  'starter_backfill',
  'energy_open', 'energy_grant', 'energy_policy_publish',
  'insurance_policy_publish', 'insurance_activate', 'mission_template_publish', 'host_register_dev',
  'revalidate', 'participant_void', 'host_declared_lost', 'host_drain', 'host_enable', 'build_disable', 'energy_adjust'));

-- ---- privileges ----

-- accounts_app, narrowed from 0000's table-level defaults. It reads every table. It inserts through the columns each
-- path writes, never a creation time, a stamp or an initial state (the defaults and triggers set them). It updates only:
-- an attempt's state, cancel_reason and ended_at (release; Stage 8 adds its columns); a participant's and a hold's state;
-- a coverage's policy, version, activation and dates (activation); a host's last_heartbeat_at, enabled, drained and
-- capacity (Stage 8 refines this). Each UPDATE grant also lets it lock those rows FOR UPDATE. It never deletes or
-- truncates; templates, policies, activations, snapshots and deploy requests are insert-only. No sequence exists here.
revoke insert, update, delete on insurance_policies from accounts_app;
grant insert (policy_id, version, terms) on insurance_policies to accounts_app;
revoke insert, update, delete on insurance_activations from accounts_app;
grant insert (activation_id, account_id, policy_id, policy_version, duration, previous_expires_at, resulting_expires_at, source, idempotency_key)
  on insurance_activations to accounts_app;
revoke insert, update, delete on insurance_coverages from accounts_app;
grant insert (account_id, policy_id, policy_version, activation_id, starts_at, expires_at),
  update (policy_id, policy_version, activation_id, starts_at, expires_at) on insurance_coverages to accounts_app;
revoke insert, update, delete on mission_templates from accounts_app;
grant insert (template_id, version, energy_cost, min_operatives, max_operatives) on mission_templates to accounts_app;
revoke insert, update, delete on mission_hosts from accounts_app;
grant insert (host_id, kind, secret_hash, capacity, enabled),
  update (last_heartbeat_at, enabled, drained, capacity) on mission_hosts to accounts_app;
revoke insert, update, delete on mission_attempts from accounts_app;
grant insert (attempt_id, created_by, template_id, template_version, seed, retention_salt, assign_deadline),
  update (state, cancel_reason, ended_at) on mission_attempts to accounts_app;
revoke insert, update, delete on attempt_participants from accounts_app;
grant insert (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost),
  update (state) on attempt_participants to accounts_app;
revoke insert, update, delete on energy_holds from accounts_app;
grant insert (attempt_id, account_id, amount), update (state) on energy_holds to accounts_app;
revoke insert, update, delete on attempt_insurance from accounts_app;
grant insert (attempt_id, account_id, activation_id, policy_id, policy_version, terms) on attempt_insurance to accounts_app;
revoke insert, update, delete on deploy_requests from accounts_app;
grant insert (account_id, idempotency_key, request_hash, attempt_id, response) on deploy_requests to accounts_app;

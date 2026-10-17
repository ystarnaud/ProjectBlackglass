-- 0008 hosts, sessions and tickets (Stage 8; spec §0.8 revision 2.8, §4.3-§4.4, §7.4-§7.6, §8.4, §15.1-§15.3; owner rulings
-- B1-B13 and the plan's revision-2 corrections R1-R7).
-- The build allowlist, insert-only attempt secrets (the seed and retention salt leave mission_attempts: B1, an approved
-- exception to expand-and-contract because the accounts stack was never deployed), the host agent columns, the attempt
-- assignment, session, ready and start columns, participant start and release columns, join tickets, registered objectives
-- and loot pools, process exit evidence, the tightened 'charge:' key and the new operator actions.
-- Order (R5): tables that others reference first (allowed_builds, allowed_build_templates, attempt_secrets), then columns,
-- then backfills (while the Stage 7 trigger functions are still in place), then the replaced trigger functions, then the
-- constraints that depend on the backfilled values, then the new attempt-bound tables, then the ledger, audit and
-- privilege changes. Every row stamp defaults to statement_timestamp(); deadlines and lifecycle stamps are written by the
-- app from clock_timestamp() read after the transaction's locks.

-- ---- builds ----

-- A mission server build may run attempts only when its (revision, catalogue version) is allowlisted and enabled, and only
-- the template versions listed for it (B4). Disabling a build stops new claims and registrations; it never touches an
-- attempt already running. Rows are never deleted; only enabled changes.
create table allowed_builds (
  build_revision text not null check (build_revision ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
  catalogue_version text not null check (catalogue_version ~ '^sha256:[0-9a-f]{64}$'),
  enabled boolean not null default true,
  created_at timestamptz not null default statement_timestamp(),
  primary key (build_revision, catalogue_version));

create function allowed_build_change() returns trigger language plpgsql as $$
begin
  if new.build_revision <> old.build_revision or new.catalogue_version <> old.catalogue_version or new.created_at <> old.created_at then
    raise exception 'an allowed build''s identity and creation never change'
      using errcode = 'check_violation', constraint = 'allowed_build_change';
  end if;
  return new;
end $$;

create trigger allowed_build_change before update on allowed_builds
  for each row execute function allowed_build_change();
create trigger allowed_build_is_permanent before delete on allowed_builds
  for each row execute function refuse_row_change('allowed_build_is_permanent');
create trigger allowed_builds_no_truncate before truncate on allowed_builds
  for each statement execute function refuse_row_change('allowed_build_is_permanent');

-- The template versions a build may run. Insert-only.
create table allowed_build_templates (
  build_revision text not null,
  catalogue_version text not null,
  template_id text not null,
  template_version integer not null,
  created_at timestamptz not null default statement_timestamp(),
  primary key (build_revision, catalogue_version, template_id, template_version),
  constraint allowed_build_templates_build_fkey foreign key (build_revision, catalogue_version) references allowed_builds,
  constraint allowed_build_templates_template_fkey foreign key (template_id, template_version) references mission_templates);

create trigger allowed_build_template_is_immutable before update or delete on allowed_build_templates
  for each row execute function refuse_row_change('allowed_build_template_is_immutable');
create trigger allowed_build_templates_no_truncate before truncate on allowed_build_templates
  for each statement execute function refuse_row_change('allowed_build_template_is_immutable');

-- ---- attempt secrets (B1) ----

-- The seed (CSPRNG, 31-bit non-negative) and retention salt (32 bytes) drawn at admission, one row per attempt, inserted in
-- admission's transaction right after the attempt. Insert-only. They live apart from mission_attempts so a constraint
-- violation's DETAIL on that row (which the server log prints at the default verbosity) can never carry them. The seed
-- reaches only the registered process; the salt never leaves the backend.
create table attempt_secrets (
  attempt_id uuid primary key references mission_attempts,
  seed integer not null check (seed >= 0),
  retention_salt bytea not null check (length(retention_salt) = 32),
  created_at timestamptz not null default statement_timestamp());

create trigger attempt_secret_is_immutable before update or delete on attempt_secrets
  for each row execute function refuse_row_change('attempt_secret_is_immutable');
create trigger attempt_secrets_no_truncate before truncate on attempt_secrets
  for each statement execute function refuse_row_change('attempt_secret_is_immutable');

insert into attempt_secrets (attempt_id, seed, retention_salt, created_at)
  select attempt_id, seed, retention_salt, created_at from mission_attempts;

-- ---- hosts ----

-- port_base: an agent host's mission ports are [port_base, port_base + capacity). build_revision and catalogue_version are
-- what its latest heartbeat reported (both or neither). secret_rotated_at is stamped by `host rotate-secret`.
alter table mission_hosts
  add column port_base integer,
  add column build_revision text,
  add column catalogue_version text,
  add column secret_rotated_at timestamptz,
  add constraint mission_host_port_range check (port_base is null or (port_base >= 1024 and port_base + capacity <= 65536)),
  add constraint mission_host_agent_ports check (kind <> 'agent' or port_base is not null),
  add constraint mission_host_reported_build check ((build_revision is null) = (catalogue_version is null)
    and (build_revision is null or build_revision ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')
    and (catalogue_version is null or catalogue_version ~ '^sha256:[0-9a-f]{64}$'));

-- ---- attempts: columns, backfill, transitions, constraints ----

-- catalogue_version: the API catalogue the attempt was admitted with (its participants' snapshots name it). The claim
-- binds host_id, mission_port, build_revision, process_credential_hash (SHA-256 of the pc1_ credential), its
-- credential_generation (1 at the claim, +1 per recovery rotation while still assigned: R1), claim_key (the agent's
-- Idempotency-Key) and launch_deadline. Registration binds process_session_id; ready binds ready_at, join_deadline,
-- layout_hash, loot_table_version and loot_hash (R3); the first start binds started_at. slot_released_at is stamped once
-- when the host's slot and port are confirmed free (an exit report, or a complete unit inventory without the unit: B6, R2).
alter table mission_attempts
  add column catalogue_version text,
  add column host_id text,
  add column mission_port integer,
  add column build_revision text,
  add column process_credential_hash bytea,
  add column credential_generation integer,
  add column claim_key uuid,
  add column launch_deadline timestamptz,
  add column process_session_id uuid,
  add column ready_at timestamptz,
  add column join_deadline timestamptz,
  add column layout_hash text,
  add column loot_table_version integer,
  add column loot_hash text,
  add column started_at timestamptz,
  add column last_process_heartbeat_at timestamptz,
  add column slot_released_at timestamptz;

-- Every Stage 7 attempt has at least one participant, inserted in admission's transaction, whose snapshot names the
-- catalogue version (S7-D8). Runs under the Stage 7 trigger function, which allows it (state unchanged).
update mission_attempts a set catalogue_version = (
  select min(p.loadout_snapshot ->> 'catalogueVersion') from attempt_participants p where p.attempt_id = a.attempt_id);

-- Guarded transitions (spec §7.2), replacing 0007's function (which named the secrets). Edges, exactly:
--   admitted -> assigned -> launching -> ready -> running -> ending -> settled;
--   admitted | assigned | launching | ready -> cancelled;
--   running -> unresponsive; unresponsive -> running | ending | settled.
-- running, ending and unresponsive never become cancelled: nothing after a start cancels (D-11). Rewriting the same state
-- passes. attempt_id, created_by, template_id, template_version, catalogue_version, created_at and assign_deadline never
-- change. host_id, mission_port, build_revision, claim_key, launch_deadline, process_session_id, ready_at, join_deadline,
-- layout_hash, loot_table_version, loot_hash, started_at and slot_released_at are written once (null to a value) and never
-- change afterwards. process_credential_hash and credential_generation are written at the claim and change afterwards only
-- by a recovery rotation while the attempt stays assigned, the generation by exactly one (R1). cancel_reason and ended_at
-- change only together with a state change. The CHECKs below say which columns each state requires.
create or replace function mission_attempt_transition() returns trigger language plpgsql as $$
begin
  if tg_op = 'INSERT' then
    if new.state <> 'admitted' then
      raise exception 'a mission attempt starts admitted'
        using errcode = 'check_violation', constraint = 'mission_attempt_transition';
    end if;
    return new;
  end if;
  if new.attempt_id <> old.attempt_id or new.created_by <> old.created_by or new.template_id <> old.template_id
     or new.template_version <> old.template_version or new.catalogue_version is distinct from old.catalogue_version
     or new.created_at <> old.created_at or new.assign_deadline <> old.assign_deadline then
    raise exception 'a mission attempt''s identity, template, catalogue, creation and assignment deadline never change'
      using errcode = 'check_violation', constraint = 'mission_attempt_transition';
  end if;
  if (old.host_id is not null and new.host_id is distinct from old.host_id)
     or (old.mission_port is not null and new.mission_port is distinct from old.mission_port)
     or (old.build_revision is not null and new.build_revision is distinct from old.build_revision)
     or (old.claim_key is not null and new.claim_key is distinct from old.claim_key)
     or (old.launch_deadline is not null and new.launch_deadline is distinct from old.launch_deadline)
     or (old.process_session_id is not null and new.process_session_id is distinct from old.process_session_id)
     or (old.ready_at is not null and new.ready_at is distinct from old.ready_at)
     or (old.join_deadline is not null and new.join_deadline is distinct from old.join_deadline)
     or (old.layout_hash is not null and new.layout_hash is distinct from old.layout_hash)
     or (old.loot_table_version is not null and new.loot_table_version is distinct from old.loot_table_version)
     or (old.loot_hash is not null and new.loot_hash is distinct from old.loot_hash)
     or (old.started_at is not null and new.started_at is distinct from old.started_at)
     or (old.slot_released_at is not null and new.slot_released_at is distinct from old.slot_released_at) then
    raise exception 'a mission attempt''s assignment, session, ready, start and slot release are written once'
      using errcode = 'check_violation', constraint = 'mission_attempt_transition';
  end if;
  if old.process_credential_hash is not null
     and (new.process_credential_hash is distinct from old.process_credential_hash
          or new.credential_generation is distinct from old.credential_generation)
     and not (old.state = 'assigned' and new.state = 'assigned'
              and new.process_credential_hash is not null and new.credential_generation = old.credential_generation + 1) then
    raise exception 'a process credential is replaced only by a recovery while the attempt is assigned'
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

alter table mission_attempts drop column seed, drop column retention_salt;

alter table mission_attempts alter column catalogue_version set not null;
alter table mission_attempts drop constraint mission_attempts_cancel_reason_check;
alter table mission_attempts
  add constraint mission_attempts_cancel_reason_check check (cancel_reason in ('no_capacity', 'player_cancel', 'launch_timeout',
    'join_timeout', 'process_exited', 'process_failed', 'invalid_pool')),
  add constraint mission_attempt_catalogue_version check (catalogue_version ~ '^sha256:[0-9a-f]{64}$'),
  add constraint mission_attempt_host_fkey foreign key (host_id) references mission_hosts,
  add constraint mission_attempt_build_fkey foreign key (build_revision, catalogue_version) references allowed_builds,
  add constraint mission_attempt_credential_hash_unique unique (process_credential_hash),
  add constraint mission_attempt_claim_key_unique unique (host_id, claim_key),
  add constraint mission_attempt_process_session_unique unique (process_session_id),
  add constraint mission_attempt_session_identity unique (attempt_id, process_session_id),
  add constraint mission_attempt_port_range check (mission_port is null or mission_port between 1024 and 65535),
  add constraint mission_attempt_credential_shape check ((process_credential_hash is null or length(process_credential_hash) = 32)
    and (credential_generation is null or credential_generation >= 1)),
  add constraint mission_attempt_ready_shape check ((layout_hash is null or layout_hash ~ '^[0-9a-f]{16}$')
    and (loot_hash is null or loot_hash ~ '^sha256:[0-9a-f]{64}$') and (loot_table_version is null or loot_table_version >= 1)),
  -- The claim's columns are set together, exactly from assigned on (an attempt cancelled before its claim has none).
  add constraint mission_attempt_assignment check (
    (host_id is null) = (mission_port is null) and (host_id is null) = (build_revision is null)
    and (host_id is null) = (process_credential_hash is null) and (host_id is null) = (credential_generation is null)
    and (host_id is null) = (claim_key is null) and (host_id is null) = (launch_deadline is null)
    and (state <> 'admitted' or host_id is null)
    and (state not in ('assigned', 'launching', 'ready', 'running', 'ending', 'unresponsive', 'settled') or host_id is not null)),
  -- A session exists only from launching on (registration from assigned moves the attempt to launching first) and is
  -- required from ready on; ready's columns are set together, exactly from ready on (or on a cancelled attempt that was ready).
  add constraint mission_attempt_session check (
    (process_session_id is null or state not in ('admitted', 'assigned'))
    and (ready_at is null) = (join_deadline is null) and (ready_at is null) = (layout_hash is null)
    and (ready_at is null) = (loot_table_version is null) and (ready_at is null) = (loot_hash is null)
    and (ready_at is null or process_session_id is not null)
    and (state not in ('ready', 'running', 'ending', 'unresponsive', 'settled') or ready_at is not null)
    and (state not in ('admitted', 'assigned', 'launching') or ready_at is null)),
  add constraint mission_attempt_started check ((started_at is not null) = (state in ('running', 'ending', 'unresponsive', 'settled'))),
  add constraint mission_attempt_process_heartbeat check (last_process_heartbeat_at is null or process_session_id is not null),
  add constraint mission_attempt_slot_release check (slot_released_at is null or host_id is not null);

-- One port per live slot on a host (B6): a slot stays live until its release is confirmed, whatever the attempt's state.
create unique index one_port_per_live_slot on mission_attempts (host_id, mission_port)
  where host_id is not null and slot_released_at is null;
create index mission_attempts_waiting on mission_attempts (created_at) where state = 'admitted';
create index mission_attempts_launch_deadline on mission_attempts (launch_deadline) where state in ('assigned', 'launching');
create index mission_attempts_join_deadline on mission_attempts (join_deadline) where state in ('ready', 'running');
create index mission_attempts_process_lease on mission_attempts (last_process_heartbeat_at) where state = 'running';

-- ---- participants ----

-- started_at: set when the participant's start commits (admitted -> joined), never changed. release_reason: set when it
-- is released (one of the attempt cancel reasons), never changed. Stage 7's released participants were released with their
-- attempt's cancellation, whose reason they take.
alter table attempt_participants add column started_at timestamptz, add column release_reason text;
update attempt_participants p set release_reason = a.cancel_reason
  from mission_attempts a where a.attempt_id = p.attempt_id and p.state = 'released';

-- Guarded transitions, replacing 0007's function: the same edges, plus started_at and release_reason written once.
--   admitted -> joined | released; joined -> left; left -> joined;
--   joined | left -> result_pending; result_pending -> settled;
--   joined | left | result_pending -> quarantined; quarantined -> settled.
create or replace function attempt_participant_transition() returns trigger language plpgsql as $$
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
  if (old.started_at is not null and new.started_at is distinct from old.started_at)
     or (old.release_reason is not null and new.release_reason is distinct from old.release_reason) then
    raise exception 'an attempt participant''s start and release reason are written once'
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

alter table attempt_participants
  add constraint attempt_participant_started check ((started_at is not null) = (state in ('joined', 'left', 'result_pending',
    'unresponsive', 'quarantined', 'settled'))),
  add constraint attempt_participant_release_reason check ((release_reason is not null) = (state = 'released')
    and (release_reason is null or release_reason in ('no_capacity', 'player_cancel', 'launch_timeout', 'join_timeout',
      'process_exited', 'process_failed', 'invalid_pool')));

-- ---- join tickets (B7) ----

-- A participant's ticket, stored as SHA-256 of the jt1_ secret, single use. Minting revokes the participant's earlier live
-- tickets (revoked_at). Redemption sets redeemed_at and the redeeming process session, which must be its own attempt's
-- (the composite foreign key). It references the participant only, never accounts directly, so no lock on accounts
-- follows the attempt lock. Rows are never deleted.
create table join_tickets (
  ticket_hash bytea primary key check (length(ticket_hash) = 32),
  attempt_id uuid not null,
  account_id bigint not null,
  issued_at timestamptz not null default statement_timestamp(),
  expires_at timestamptz not null,
  redeemed_at timestamptz,
  redeemed_by_session uuid,
  revoked_at timestamptz,
  constraint join_tickets_participant_fkey foreign key (attempt_id, account_id) references attempt_participants,
  constraint join_tickets_session_fkey foreign key (attempt_id, redeemed_by_session)
    references mission_attempts (attempt_id, process_session_id),
  constraint join_ticket_expiry check (expires_at > issued_at),
  constraint join_ticket_redemption check ((redeemed_at is null) = (redeemed_by_session is null)),
  constraint join_ticket_single_outcome check (redeemed_at is null or revoked_at is null));
create index join_tickets_live on join_tickets (attempt_id, account_id) where redeemed_at is null and revoked_at is null;

create function join_ticket_change() returns trigger language plpgsql as $$
begin
  if new.ticket_hash <> old.ticket_hash or new.attempt_id <> old.attempt_id or new.account_id <> old.account_id
     or new.issued_at <> old.issued_at or new.expires_at <> old.expires_at
     or (old.redeemed_at is not null and (new.redeemed_at is distinct from old.redeemed_at
                                          or new.redeemed_by_session is distinct from old.redeemed_by_session))
     or (old.revoked_at is not null and new.revoked_at is distinct from old.revoked_at) then
    raise exception 'a join ticket is redeemed or revoked once and otherwise never changes'
      using errcode = 'check_violation', constraint = 'join_ticket_change';
  end if;
  return new;
end $$;

create trigger join_ticket_change before update on join_tickets
  for each row execute function join_ticket_change();
create trigger join_ticket_is_permanent before delete on join_tickets
  for each row execute function refuse_row_change('join_ticket_is_permanent');
create trigger join_tickets_no_truncate before truncate on join_tickets
  for each statement execute function refuse_row_change('join_ticket_is_permanent');

-- ---- registered objectives and loot pool (spec §8.4, §15.1; R3) ----

-- Written once at ready with the attempt's loot_hash and loot_table_version; insert-only. ordinal keeps the reported order.
create table attempt_objectives (
  attempt_id uuid not null references mission_attempts,
  objective_id text not null check (length(objective_id) <= 64 and objective_id ~ '^[a-z0-9]+([._-][a-z0-9]+)*$'),
  required boolean not null,
  ordinal integer not null check (ordinal >= 0),
  primary key (attempt_id, objective_id),
  unique (attempt_id, ordinal));

create table attempt_loot_pool (
  attempt_id uuid not null references mission_attempts,
  container_index integer not null check (container_index >= 0),
  line integer not null check (line >= 0),
  definition_id text not null check (length(definition_id) <= 64 and definition_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  quantity integer not null check (quantity > 0),
  primary key (attempt_id, container_index, line));

create trigger attempt_objective_is_immutable before update or delete on attempt_objectives
  for each row execute function refuse_row_change('attempt_objective_is_immutable');
create trigger attempt_objectives_no_truncate before truncate on attempt_objectives
  for each statement execute function refuse_row_change('attempt_objective_is_immutable');
create trigger attempt_loot_pool_is_immutable before update or delete on attempt_loot_pool
  for each row execute function refuse_row_change('attempt_loot_pool_is_immutable');
create trigger attempt_loot_pool_no_truncate before truncate on attempt_loot_pool
  for each statement execute function refuse_row_change('attempt_loot_pool_is_immutable');

-- ---- process exit evidence (B11 revised, R6) ----

-- One report per attempt and source, insert-only: 'agent' from the host agent's exited report (exit_code, signal, and the
-- per-participant result presence in attempt_exit_results), 'process' from the process's failed report (its session and
-- reason). attempt_state and any_started record what the backend saw when the report arrived; outcome is what it applied
-- ('cancelled' before any start, 'none' after one). An identical repeat returns the stored outcome; a conflicting one is
-- refused and never overwrites this row. Stage 9 evaluates these as evidence.
create table attempt_exit_reports (
  attempt_id uuid not null references mission_attempts,
  source text not null check (source in ('agent', 'process')),
  host_id text,
  process_session_id uuid,
  attempt_state text not null,
  any_started boolean not null,
  exit_code integer check (exit_code between 0 and 255),
  signal text check (signal ~ '^SIG[A-Z0-9]{1,13}$'),
  reason text check (length(reason) <= 64 and reason ~ '^[a-z0-9]+([._-][a-z0-9]+)*$'),
  outcome text not null check (outcome in ('cancelled', 'none')),
  received_at timestamptz not null default statement_timestamp(),
  primary key (attempt_id, source),
  constraint attempt_exit_report_source_shape check (case source
    when 'agent' then host_id is not null and process_session_id is null and reason is null
    else host_id is null and process_session_id is not null and exit_code is null and signal is null and reason is not null end),
  constraint attempt_exit_reports_session_fkey foreign key (attempt_id, process_session_id)
    references mission_attempts (attempt_id, process_session_id));

-- The agent's per-participant result presence: whether it found that participant's result file when the unit ended.
create table attempt_exit_results (
  attempt_id uuid not null,
  source text not null default 'agent' check (source = 'agent'),
  account_id bigint not null,
  present boolean not null,
  primary key (attempt_id, account_id),
  constraint attempt_exit_results_report_fkey foreign key (attempt_id, source) references attempt_exit_reports,
  constraint attempt_exit_results_participant_fkey foreign key (attempt_id, account_id) references attempt_participants);

create trigger attempt_exit_report_is_immutable before update or delete on attempt_exit_reports
  for each row execute function refuse_row_change('attempt_exit_report_is_immutable');
create trigger attempt_exit_reports_no_truncate before truncate on attempt_exit_reports
  for each statement execute function refuse_row_change('attempt_exit_report_is_immutable');
create trigger attempt_exit_result_is_immutable before update or delete on attempt_exit_results
  for each row execute function refuse_row_change('attempt_exit_report_is_immutable');
create trigger attempt_exit_results_no_truncate before truncate on attempt_exit_results
  for each statement execute function refuse_row_change('attempt_exit_report_is_immutable');

-- ---- forward changes to the energy ledger ----

-- No 'charge' entry exists before this migration (no Stage 6 or 7 path writes one), so the tightened rule holds for every
-- existing row. 0007's per-kind key CHECK is replaced by name with every rule identical, except charge: exactly
-- 'charge:' and the entry's own attempt_id as text, so a hold charges at most once per account (the unique key).
alter table energy_ledger drop constraint energy_ledger_idempotency_key_check;
alter table energy_ledger add constraint energy_ledger_idempotency_key_check check (length(idempotency_key) <= 200
  and idempotency_key !~ '[[:space:][:cntrl:]]'
  and case kind
        when 'open' then idempotency_key = 'open'
        when 'regen' then idempotency_key ~ '^regen:-?[0-9]+$'
        when 'dev_grant' then idempotency_key ~ '^dev_grant:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
        when 'reserve' then attempt_id is not null and idempotency_key = 'reserve:' || attempt_id::text
        when 'release' then attempt_id is not null and idempotency_key = 'release:' || attempt_id::text
        when 'charge' then attempt_id is not null and idempotency_key = 'charge:' || attempt_id::text
        else idempotency_key = kind or (starts_with(idempotency_key, kind || ':') and length(idempotency_key) > length(kind) + 1)
      end);

-- ---- audit ----

alter table operator_actions drop constraint operator_actions_action_check;
alter table operator_actions add constraint operator_actions_action_check check (action in (
  'account_create', 'credential_rotate', 'credential_revoke', 'sessions_revoke', 'account_suspend', 'account_reactivate',
  'starter_backfill',
  'energy_open', 'energy_grant', 'energy_policy_publish',
  'insurance_policy_publish', 'insurance_activate', 'mission_template_publish', 'host_register_dev', 'host_heartbeat_dev',
  'revalidate', 'participant_void', 'host_declared_lost', 'host_drain', 'host_enable', 'build_disable', 'energy_adjust',
  'host_register', 'host_rotate_secret', 'host_disable', 'host_capacity', 'build_allow'));

-- ---- privileges ----

-- accounts_app, narrowed from 0000's table-level defaults to the columns each path writes (dropping seed and
-- retention_salt dropped their column grants). It never sets a creation time or stamp the defaults own, never updates an
-- attempt's identity, secrets, objectives, pool, exit evidence or allowed template, and never deletes or truncates.
-- Hosts: registration inserts; the audited verbs switch enabled, drained, capacity and the secret; heartbeats stamp the
-- time and reported build. Builds: `build allow` inserts and re-enables, `build disable` disables. Attempts: admission
-- inserts; claim, recovery, launching, registration, ready, start, heartbeats, release and slot release update.
revoke insert, update, delete on mission_hosts from accounts_app;
grant insert (host_id, kind, secret_hash, capacity, enabled, port_base),
  update (enabled, drained, capacity, secret_hash, secret_rotated_at, last_heartbeat_at, build_revision, catalogue_version)
  on mission_hosts to accounts_app;
revoke insert, update, delete on allowed_builds from accounts_app;
grant insert (build_revision, catalogue_version), update (enabled) on allowed_builds to accounts_app;
revoke insert, update, delete on allowed_build_templates from accounts_app;
grant insert (build_revision, catalogue_version, template_id, template_version) on allowed_build_templates to accounts_app;
revoke insert, update, delete on attempt_secrets from accounts_app;
grant insert (attempt_id, seed, retention_salt) on attempt_secrets to accounts_app;
revoke insert, update, delete on mission_attempts from accounts_app;
grant insert (attempt_id, created_by, template_id, template_version, catalogue_version, assign_deadline),
  update (state, cancel_reason, ended_at, host_id, mission_port, build_revision, process_credential_hash, credential_generation,
          claim_key, launch_deadline, process_session_id, ready_at, join_deadline, layout_hash, loot_table_version, loot_hash,
          started_at, last_process_heartbeat_at, slot_released_at)
  on mission_attempts to accounts_app;
revoke insert, update, delete on attempt_participants from accounts_app;
grant insert (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost),
  update (state, started_at, release_reason) on attempt_participants to accounts_app;
revoke insert, update, delete on join_tickets from accounts_app;
grant insert (ticket_hash, attempt_id, account_id, expires_at), update (redeemed_at, redeemed_by_session, revoked_at)
  on join_tickets to accounts_app;
revoke insert, update, delete on attempt_objectives from accounts_app;
grant insert (attempt_id, objective_id, required, ordinal) on attempt_objectives to accounts_app;
revoke insert, update, delete on attempt_loot_pool from accounts_app;
grant insert (attempt_id, container_index, line, definition_id, quantity) on attempt_loot_pool to accounts_app;
revoke insert, update, delete on attempt_exit_reports from accounts_app;
grant insert (attempt_id, source, host_id, process_session_id, attempt_state, any_started, exit_code, signal, reason, outcome)
  on attempt_exit_reports to accounts_app;
revoke insert, update, delete on attempt_exit_results from accounts_app;
grant insert (attempt_id, account_id, present) on attempt_exit_results to accounts_app;

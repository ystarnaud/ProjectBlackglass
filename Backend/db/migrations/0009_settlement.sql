-- 0009 settlement (Stage 9; spec §0.9 revision 2.9; plan 2026-10-17-phase-13-stage9-settlement.md revision 3, §D).
-- The catalogue history, preserved result submissions, participant settlements, per-line pool collections, quarantine and
-- recorded-hash columns, the settle and refund edges, host-loss declarations, exit evidence marks and outbox observations,
-- the exact settle and void_refund ledger keys, the Stage 9 operator actions and anomaly reviews, and the privileges.
-- Order: tables others reference first (catalogue_versions, result_submissions), then the settlement tables, then the
-- columns, triggers and constraints on existing tables, then the ledger, audit and privilege changes. 0000-0008 are unchanged.

-- ---- catalogue history (S9-B20.1) ----

-- Every catalogue an attempt was admitted under, verbatim, keyed by its catalogueVersion (the SHA-256 of these bytes,
-- which the CHECK proves). Insert-only: settlement and ready judge an attempt by its admission catalogue, never another.
create table catalogue_versions (
  version text primary key check (version ~ '^sha256:[0-9a-f]{64}$'),
  bytes bytea not null check (length(bytes) between 1 and 4194304),
  registered_at timestamptz not null default statement_timestamp(),
  constraint catalogue_version_is_hash check (version = 'sha256:' || encode(sha256(bytes), 'hex')));

create trigger catalogue_version_is_immutable before update or delete on catalogue_versions
  for each row execute function refuse_row_change('catalogue_version_is_immutable');
create trigger catalogue_versions_no_truncate before truncate on catalogue_versions
  for each statement execute function refuse_row_change('catalogue_version_is_immutable');

-- Every new attempt references a registered catalogue (the owner's B20 ruling). NOT VALID: schema-8 attempts admitted before
-- the history existed are not checked (their settlement waits for `catalogue import`, S9-B20.5); every later insert is.
alter table mission_attempts add constraint mission_attempts_catalogue_version_fkey
  foreign key (catalogue_version) references catalogue_versions not valid;

-- ---- result submissions (S9-B3, S9-B21, S9-B22) ----

-- Every body the results route (source 'agent') or `attempt import-result` (source 'operator_import') stored, insert-only.
-- raw_hash is the SHA-256 of the request bytes as received (null only for a body over the 64 KiB cap, of which only the
-- length is kept); manifest_hash exists exactly when the body parsed as a format-v1 manifest. The bytes themselves live in
-- result_submission_payloads, so no constraint violation's DETAIL on this row ever prints a manifest. attempt_id and account_id
-- link the row to its participant only when the body names a participant of an attempt of the submitting host (or the
-- operator imported it); claimed_* keep what an unlinked body claimed. A byte-identical repeat only raises receive_count
-- (the trigger stamps last_received_at). disposition is the answer the row received.
create table result_submissions (
  submission_id bigint generated always as identity primary key,
  source text not null check (source in ('agent', 'operator_import')),
  host_id text references mission_hosts,
  process_session_id uuid,
  attempt_id uuid,
  account_id bigint,
  claimed_attempt_id uuid,
  claimed_account_id bigint,
  attempt_revoked boolean not null,
  raw_too_large boolean not null default false,
  raw_length integer not null check (raw_length >= 0),
  raw_hash bytea check (raw_hash is null or length(raw_hash) = 32),
  manifest_hash text check (manifest_hash ~ '^sha256:[0-9a-f]{64}$'),
  disposition text not null check (disposition in ('settled', 'same_as', 'quarantined', 'held', 'conflict', 'late_after_void', 'refused', 'malformed')),
  same_as_submission_id bigint references result_submissions,
  codes text[] not null default '{}' check (cardinality(codes) <= 64),
  first_received_at timestamptz not null default statement_timestamp(),
  last_received_at timestamptz not null default statement_timestamp(),
  receive_count integer not null default 1 check (receive_count >= 1),
  constraint result_submission_participant_fkey foreign key (attempt_id, account_id) references attempt_participants,
  constraint result_submission_identity unique (submission_id, attempt_id, account_id),
  constraint result_submission_raw check (case when raw_too_large then raw_hash is null and raw_length > 65536
                                               else raw_hash is not null and raw_length between 0 and 65536 end),
  constraint result_submission_participant check ((attempt_id is null) = (account_id is null)),
  constraint result_submission_source check (case source when 'agent' then host_id is not null else attempt_id is not null end),
  constraint result_submission_same_as check ((disposition = 'same_as') = (same_as_submission_id is not null)),
  constraint result_submission_disposition check (case
    when disposition in ('settled', 'same_as', 'quarantined', 'held', 'conflict', 'late_after_void') then attempt_id is not null and manifest_hash is not null
    when disposition = 'malformed' then manifest_hash is null
    else true end));
create unique index result_submissions_participant_raw on result_submissions (attempt_id, account_id, raw_hash) where attempt_id is not null;
create unique index result_submissions_host_raw on result_submissions (host_id, raw_hash) where attempt_id is null;
create index result_submissions_host_recent on result_submissions (host_id, first_received_at) where attempt_id is null;

-- Insert-only, except a repeat's receive count (upward) and the stamp this trigger sets with it.
create function result_submission_change() returns trigger language plpgsql as $$
begin
  if row(new.submission_id, new.source, new.host_id, new.process_session_id, new.attempt_id, new.account_id, new.claimed_attempt_id,
         new.claimed_account_id, new.attempt_revoked, new.raw_too_large, new.raw_length, new.raw_hash,
         new.manifest_hash, new.disposition, new.same_as_submission_id, new.codes, new.first_received_at)
     is distinct from
     row(old.submission_id, old.source, old.host_id, old.process_session_id, old.attempt_id, old.account_id, old.claimed_attempt_id,
         old.claimed_account_id, old.attempt_revoked, old.raw_too_large, old.raw_length, old.raw_hash,
         old.manifest_hash, old.disposition, old.same_as_submission_id, old.codes, old.first_received_at)
     or new.receive_count <= old.receive_count then
    raise exception 'a result submission only counts its repeats'
      using errcode = 'check_violation', constraint = 'result_submission_change';
  end if;
  new.last_received_at := greatest(old.last_received_at, statement_timestamp());
  return new;
end $$;

create trigger result_submission_change before update on result_submissions
  for each row execute function result_submission_change();
create trigger result_submission_is_permanent before delete on result_submissions
  for each row execute function refuse_row_change('result_submission_is_permanent');
create trigger result_submissions_no_truncate before truncate on result_submissions
  for each statement execute function refuse_row_change('result_submission_is_permanent');

-- A submission's bytes: the raw request bytes (absent for an over-cap body) and the canonical form (when it parsed). Insert-
-- only. No CHECK or NOT NULL here, whose violation DETAIL would print the bytes: the trigger checks them against the parent
-- row (length, SHA-256) and refuses with a message that names no value.
create table result_submission_payloads (
  submission_id bigint primary key references result_submissions,
  raw_bytes bytea,
  canonical_bytes bytea);

create function result_submission_payload_matches() returns trigger language plpgsql as $$
declare
  parent result_submissions%rowtype;
begin
  select * into parent from result_submissions where submission_id = new.submission_id;
  if not found
     or (parent.raw_too_large and new.raw_bytes is not null)
     or (not parent.raw_too_large and (new.raw_bytes is null or length(new.raw_bytes) <> parent.raw_length or sha256(new.raw_bytes) <> parent.raw_hash))
     or ((new.canonical_bytes is null) <> (parent.manifest_hash is null))
     or (new.canonical_bytes is not null and (length(new.canonical_bytes) > 65536
         or 'sha256:' || encode(sha256(new.canonical_bytes), 'hex') <> parent.manifest_hash)) then
    raise exception 'a submission payload must match its submission''s length and hashes'
      using errcode = 'check_violation', constraint = 'result_submission_payload_matches';
  end if;
  return new;
end $$;

create trigger result_submission_payload_matches before insert on result_submission_payloads
  for each row execute function result_submission_payload_matches();
create trigger result_submission_payload_is_immutable before update or delete on result_submission_payloads
  for each row execute function refuse_row_change('result_submission_is_permanent');
create trigger result_submission_payloads_no_truncate before truncate on result_submission_payloads
  for each statement execute function refuse_row_change('result_submission_is_permanent');

-- ---- participant settlements (S9-B2, S9-B3, S9-B16) ----

-- Exactly one per settled participant (the primary key makes a second impossible). A settlement references the submission
-- it settled (its own participant's: the composite key) and whether a recorded hash matched it; a protective void
-- (outcome infra_void) references none and records its basis. The receipt is stored as the exact bytes served (text, not
-- jsonb, which would reformat them). Immutable.
create table participant_settlements (
  attempt_id uuid not null,
  account_id bigint not null,
  outcome text not null check (outcome in ('success', 'defeat', 'abandoned', 'disconnected', 'expired', 'infra_void')),
  submission_id bigint unique,
  void_basis text check (void_basis in ('exit_evidence', 'operator_quarantine', 'operator_revoked_unrecoverable', 'host_declared_lost')),
  recorded_hash text check (recorded_hash in ('matched', 'absent')),
  manifest_hash text check (manifest_hash ~ '^sha256:[0-9a-f]{64}$'),
  catalogue_version text not null references catalogue_versions,
  retention_selection jsonb not null default '[]' check (jsonb_typeof(retention_selection) = 'array'),
  receipt text not null check (length(receipt) between 2 and 65536),
  settled_at timestamptz not null,
  primary key (attempt_id, account_id),
  constraint participant_settlement_participant_fkey foreign key (attempt_id, account_id) references attempt_participants,
  constraint participant_settlement_submission_fkey foreign key (submission_id, attempt_id, account_id)
    references result_submissions (submission_id, attempt_id, account_id),
  constraint participant_settlement_shape check (case when outcome = 'infra_void'
    then submission_id is null and void_basis is not null and recorded_hash is null and manifest_hash is null
    else submission_id is not null and void_basis is null and recorded_hash is not null and manifest_hash is not null end));

create trigger participant_settlement_is_immutable before update or delete on participant_settlements
  for each row execute function refuse_row_change('participant_settlement_is_immutable');
create trigger participant_settlements_no_truncate before truncate on participant_settlements
  for each statement execute function refuse_row_change('participant_settlement_is_immutable');

-- ---- pool collections (V9) ----

-- The running per-line total collected by the attempt's settled participants: only grows, never past the pool line.
create table pool_collections (
  attempt_id uuid not null,
  container_index integer not null,
  line integer not null,
  collected integer not null check (collected > 0),
  primary key (attempt_id, container_index, line),
  constraint pool_collections_line_fkey foreign key (attempt_id, container_index, line) references attempt_loot_pool);

create function pool_collection_change() returns trigger language plpgsql as $$
begin
  if tg_op = 'UPDATE' and (new.attempt_id <> old.attempt_id or new.container_index <> old.container_index or new.line <> old.line
                           or new.collected <= old.collected) then
    raise exception 'a pool collection only grows'
      using errcode = 'check_violation', constraint = 'pool_collection_change';
  end if;
  if new.collected > (select quantity from attempt_loot_pool l
                      where l.attempt_id = new.attempt_id and l.container_index = new.container_index and l.line = new.line) then
    raise exception 'a pool collection never exceeds its pool line'
      using errcode = 'check_violation', constraint = 'pool_collection_change';
  end if;
  return new;
end $$;

create trigger pool_collection_change before insert or update on pool_collections
  for each row execute function pool_collection_change();
create trigger pool_collection_is_permanent before delete on pool_collections
  for each row execute function refuse_row_change('pool_collection_is_permanent');
create trigger pool_collections_no_truncate before truncate on pool_collections
  for each statement execute function refuse_row_change('pool_collection_is_permanent');

-- ---- participants: quarantine, recorded hash, the Stage 9 edges (S9-B5, S9-B16, S9-B21) ----

-- quarantined_at and quarantine_reason: set when the participant is first quarantined, kept afterwards (also once settled).
-- escalate_at: set exactly while quarantined (the next escalation alert; never a void). recorded_manifest_hash: the
-- process's recorded hash, written once.
alter table attempt_participants
  add column quarantined_at timestamptz,
  add column escalate_at timestamptz,
  add column quarantine_reason text,
  add column recorded_manifest_hash text,
  add constraint attempt_participant_quarantine_reason check (quarantine_reason in ('invalid_manifest', 'malformed_manifest', 'recorded_hash_mismatch',
    'inventory_base_changed', 'catalogue_unavailable', 'settlement_check_failed', 'host_revoked', 'operator_import')),
  add constraint attempt_participant_recorded_hash check (recorded_manifest_hash ~ '^sha256:[0-9a-f]{64}$'),
  add constraint attempt_participant_quarantine check ((state = 'quarantined') = (escalate_at is not null)
    and (quarantined_at is null) = (quarantine_reason is null)
    and (state <> 'quarantined' or quarantined_at is not null)
    and (quarantined_at is null or state in ('quarantined', 'settled')));

-- Guarded transitions, replacing 0008's function: its edges plus joined | left -> settled (S9-B16 option (a): a settlement
-- without a recorded hash, or a protective void). Edges, exactly:
--   admitted -> joined | released; joined -> left; left -> joined;
--   joined | left -> result_pending; joined | left | result_pending -> settled;
--   joined | left | result_pending -> quarantined; quarantined -> settled.
-- started_at, release_reason, quarantined_at, quarantine_reason and recorded_manifest_hash are written once. escalate_at
-- changes only on a quarantined row (moved forward by the escalation sweep) or when the row leaves quarantine.
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
     or (old.release_reason is not null and new.release_reason is distinct from old.release_reason)
     or (old.quarantined_at is not null and new.quarantined_at is distinct from old.quarantined_at)
     or (old.quarantine_reason is not null and new.quarantine_reason is distinct from old.quarantine_reason)
     or (old.recorded_manifest_hash is not null and new.recorded_manifest_hash is distinct from old.recorded_manifest_hash) then
    raise exception 'an attempt participant''s start, release reason, quarantine and recorded hash are written once'
      using errcode = 'check_violation', constraint = 'attempt_participant_transition';
  end if;
  if new.escalate_at is distinct from old.escalate_at and old.state <> 'quarantined' and new.state <> 'quarantined' then
    raise exception 'an attempt participant''s escalation changes only while it is quarantined'
      using errcode = 'check_violation', constraint = 'attempt_participant_transition';
  end if;
  if new.state <> old.state and (old.state, new.state) not in (
      ('admitted', 'joined'), ('admitted', 'released'), ('joined', 'left'), ('left', 'joined'), ('joined', 'result_pending'),
      ('left', 'result_pending'), ('result_pending', 'settled'), ('joined', 'settled'), ('left', 'settled'), ('joined', 'quarantined'),
      ('left', 'quarantined'), ('result_pending', 'quarantined'), ('quarantined', 'settled')) then
    raise exception 'an attempt participant cannot move from % to %', old.state, new.state
      using errcode = 'check_violation', constraint = 'attempt_participant_transition';
  end if;
  return new;
end $$;

-- ---- holds: the settle and refund edges (S9-B15.1) ----

-- Replacing 0007's function: reserved -> released | charged (Stages 7 and 8), and charged -> settled | refunded (Stage 9).
create or replace function energy_hold_transition() returns trigger language plpgsql as $$
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
  elsif (old.state, new.state) in (('reserved', 'released'), ('reserved', 'charged'), ('charged', 'settled'), ('charged', 'refunded')) then
    new.updated_at := statement_timestamp();
  else
    raise exception 'an energy hold cannot move from % to %', old.state, new.state
      using errcode = 'check_violation', constraint = 'energy_hold_transition';
  end if;
  return new;
end $$;

-- ---- hosts: permanent loss (S9-B8) ----

-- declared_lost_at: stamped once by `host declare-lost`. A lost host is never enabled again and its secret never rotates
-- (a recovered machine registers under a new host id).
alter table mission_hosts add column declared_lost_at timestamptz;

create function mission_host_declared_lost() returns trigger language plpgsql as $$
begin
  if old.declared_lost_at is not null and (new.declared_lost_at is distinct from old.declared_lost_at or new.enabled
                                           or new.secret_hash <> old.secret_hash or not new.secret_revoked) then
    raise exception 'a host declared lost is never re-enabled, rotated or undeclared'
      using errcode = 'check_violation', constraint = 'mission_host_declared_lost';
  end if;
  if old.declared_lost_at is null and new.declared_lost_at is not null and (new.enabled or not new.secret_revoked) then
    raise exception 'only a disabled host with a revoked secret is declared lost'
      using errcode = 'check_violation', constraint = 'mission_host_declared_lost';
  end if;
  return new;
end $$;

create trigger mission_host_declared_lost before update on mission_hosts
  for each row execute function mission_host_declared_lost();

-- ---- exit evidence (S9-B1, S9-B7) ----

-- outbox_complete: the agent declared a complete, successful outbox scan. attempt_revoked: the attempt's credentials were
-- already revoked when the report was stored (such a report is never void evidence). Agent rows from now on set both; the
-- schema-8 rows are not checked (NOT VALID). outcome gains 'voided'.
alter table attempt_exit_reports add column outbox_complete boolean, add column attempt_revoked boolean;
alter table attempt_exit_reports add constraint attempt_exit_report_stage9_marks
  check (source <> 'agent' or (outbox_complete is not null and attempt_revoked is not null)) not valid;
alter table attempt_exit_reports drop constraint attempt_exit_reports_outcome_check;
alter table attempt_exit_reports add constraint attempt_exit_reports_outcome_check check (outcome in ('cancelled', 'none', 'voided'));

-- The one supplementary outbox observation an attempt may get (rev 3): a complete scan reported after an agent report that
-- declared an incomplete one. It never replaces the report; the void rule reads it with the report's exit status.
create table attempt_outbox_observations (
  attempt_id uuid primary key,
  source text not null default 'agent' check (source = 'agent'),
  host_id text not null,
  attempt_revoked boolean not null,
  outcome text not null check (outcome in ('voided', 'none')),
  received_at timestamptz not null default statement_timestamp(),
  constraint attempt_outbox_observation_report_fkey foreign key (attempt_id, source) references attempt_exit_reports,
  constraint attempt_outbox_observation_host_fkey foreign key (attempt_id, host_id) references mission_attempts (attempt_id, host_id));

create function attempt_outbox_observation_follows() returns trigger language plpgsql as $$
begin
  if not exists (select 1 from attempt_exit_reports r
                 where r.attempt_id = new.attempt_id and r.source = 'agent' and r.outbox_complete = false) then
    raise exception 'an outbox observation follows an agent report of an incomplete scan'
      using errcode = 'check_violation', constraint = 'attempt_outbox_observation_follows';
  end if;
  return new;
end $$;

create trigger attempt_outbox_observation_follows before insert on attempt_outbox_observations
  for each row execute function attempt_outbox_observation_follows();

create table attempt_outbox_observation_results (
  attempt_id uuid not null references attempt_outbox_observations,
  account_id bigint not null,
  present boolean not null,
  primary key (attempt_id, account_id),
  constraint attempt_outbox_observation_results_participant_fkey foreign key (attempt_id, account_id) references attempt_participants);

create trigger attempt_outbox_observation_is_immutable before update or delete on attempt_outbox_observations
  for each row execute function refuse_row_change('attempt_outbox_observation_is_immutable');
create trigger attempt_outbox_observations_no_truncate before truncate on attempt_outbox_observations
  for each statement execute function refuse_row_change('attempt_outbox_observation_is_immutable');
create trigger attempt_outbox_observation_result_is_immutable before update or delete on attempt_outbox_observation_results
  for each row execute function refuse_row_change('attempt_outbox_observation_is_immutable');
create trigger attempt_outbox_observation_results_no_truncate before truncate on attempt_outbox_observation_results
  for each statement execute function refuse_row_change('attempt_outbox_observation_is_immutable');

-- ---- operatives: settlement XP (S9-B13) ----

-- Settlement is the first writer of experience after creation; it only adds (capped at int.MaxValue), so experience never
-- decreases (the picks already made stay earned).
create function operative_experience_never_decreases() returns trigger language plpgsql as $$
begin
  if new.experience < old.experience then
    raise exception 'an operative''s experience never decreases'
      using errcode = 'check_violation', constraint = 'operative_experience_never_decreases';
  end if;
  return new;
end $$;

create trigger operative_experience_never_decreases before update on operatives
  for each row execute function operative_experience_never_decreases();

-- ---- anomaly reviews (S9-B9) ----

-- `anomaly review` stamps reviewed_at and its note once; nothing else about an anomaly ever changes.
create function anomaly_event_review() returns trigger language plpgsql as $$
begin
  if row(new.id, new.kind, new.account_id, new.host_id, new.build_revision, new.attempt_id, new.dedupe_key, new.detail, new.created_at)
       is distinct from row(old.id, old.kind, old.account_id, old.host_id, old.build_revision, old.attempt_id, old.dedupe_key, old.detail, old.created_at)
     or old.reviewed_at is not null or new.reviewed_at is null or new.reviewed_note is null then
    raise exception 'an anomaly is reviewed once, with a note, and otherwise never changes'
      using errcode = 'check_violation', constraint = 'anomaly_event_review';
  end if;
  return new;
end $$;

create trigger anomaly_event_review before update on anomaly_events
  for each row execute function anomaly_event_review();
create trigger anomaly_event_is_permanent before delete on anomaly_events
  for each row execute function refuse_row_change('anomaly_event_is_permanent');
alter table anomaly_events add constraint anomaly_event_review_note check (reviewed_note is null or (length(reviewed_note) between 1 and 1000
  and reviewed_note !~ '[[:cntrl:]]'));

-- ---- forward changes to the energy ledger (S9-B15.2) ----

-- No settle or void_refund entry exists before this migration (no earlier path writes one), so the tightened rule holds for
-- every existing row. 0008's per-kind key CHECK is replaced by name with every rule identical, plus settle and void_refund:
-- exactly the kind, ':' and the entry's own attempt_id as text, so a hold settles or is refunded at most once per account.
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
        when 'settle' then attempt_id is not null and idempotency_key = 'settle:' || attempt_id::text
        when 'void_refund' then attempt_id is not null and idempotency_key = 'void_refund:' || attempt_id::text
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
  'host_register', 'host_rotate_secret', 'host_disable', 'host_capacity', 'build_allow',
  'result_import', 'catalogue_import', 'anomaly_review'));

-- ---- privileges ----

-- accounts_app, narrowed from 0000's table-level defaults to the columns each Stage 9 path writes. It never deletes or
-- truncates, never chooses an identity (no sequence privilege), never stamps a review or receive time the database owns,
-- and never changes a settlement, submission body, catalogue or exit evidence.
revoke insert, update, delete on catalogue_versions from accounts_app;
grant insert (version, bytes) on catalogue_versions to accounts_app;
revoke insert, update, delete on result_submissions from accounts_app;
grant insert (source, host_id, process_session_id, attempt_id, account_id, claimed_attempt_id, claimed_account_id, attempt_revoked,
              raw_too_large, raw_length, raw_hash, manifest_hash, disposition, same_as_submission_id, codes),
  update (receive_count) on result_submissions to accounts_app;
revoke insert, update, delete on result_submission_payloads from accounts_app;
grant insert (submission_id, raw_bytes, canonical_bytes) on result_submission_payloads to accounts_app;
revoke insert, update, delete on participant_settlements from accounts_app;
grant insert (attempt_id, account_id, outcome, submission_id, void_basis, recorded_hash, manifest_hash, catalogue_version, retention_selection,
              receipt, settled_at) on participant_settlements to accounts_app;
revoke insert, update, delete on pool_collections from accounts_app;
grant insert (attempt_id, container_index, line, collected), update (collected) on pool_collections to accounts_app;
grant update (quarantined_at, escalate_at, quarantine_reason, recorded_manifest_hash) on attempt_participants to accounts_app;
grant update (declared_lost_at) on mission_hosts to accounts_app;
grant insert (outbox_complete, attempt_revoked) on attempt_exit_reports to accounts_app;
revoke insert, update, delete on attempt_outbox_observations from accounts_app;
grant insert (attempt_id, host_id, attempt_revoked, outcome) on attempt_outbox_observations to accounts_app;
revoke insert, update, delete on attempt_outbox_observation_results from accounts_app;
grant insert (attempt_id, account_id, present) on attempt_outbox_observation_results to accounts_app;
grant update (experience) on operatives to accounts_app;
grant update (reviewed_at, reviewed_note) on anomaly_events to accounts_app;
revoke all on sequence result_submissions_submission_id_seq from accounts_app;

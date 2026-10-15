-- 0004 inventory (Stage 4; spec §5). One inventories row per account: the lock anchor and the version shared by picks
-- and Stage 5 edits; it records which catalogue's starter the account received (never re-granted). Non-stackables are
-- bigint-identity instances; stackables are fungible stacks per location (operative_id null = the stash).
create table inventories (
  account_id bigint primary key references accounts,
  version bigint not null default 1 check (version >= 1),
  starter_granted boolean not null default false,
  starter_catalogue_version text check (starter_catalogue_version ~ '^sha256:[0-9a-f]{64}$'),
  starter_granted_at timestamptz,
  check ((starter_granted and starter_catalogue_version is not null and starter_granted_at is not null)
      or (not starter_granted and starter_catalogue_version is null and starter_granted_at is null)));
create table item_instances (
  item_id bigint generated always as identity primary key check (item_id > 0),
  account_id bigint not null references accounts,
  definition_id text not null check (length(definition_id) <= 64 and definition_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  operative_id bigint,
  equipped_slot text check (equipped_slot in ('weapon', 'armor', 'utility')),
  origin text not null check (origin in ('starter', 'settlement', 'insurance', 'dev_grant')),
  origin_ref text not null check (length(origin_ref) between 1 and 300 and origin_ref !~ '[[:space:][:cntrl:]]'),
  created_at timestamptz not null default statement_timestamp(),
  foreign key (account_id, operative_id) references operatives (account_id, operative_id),
  check (equipped_slot is null or operative_id is not null),
  unique (origin, origin_ref));
create unique index item_one_per_slot on item_instances (operative_id, equipped_slot) where equipped_slot is not null;
create index item_instances_account on item_instances (account_id);
create table item_stacks (
  account_id bigint not null references accounts,
  operative_id bigint,
  definition_id text not null check (length(definition_id) <= 64 and definition_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  quantity integer not null check (quantity > 0),
  foreign key (account_id, operative_id) references operatives (account_id, operative_id),
  unique nulls not distinct (account_id, operative_id, definition_id));
-- The starter backfill is an operator action (0001's list is extended here, forward-only).
alter table operator_actions drop constraint operator_actions_action_check;
alter table operator_actions add constraint operator_actions_action_check check (action in (
  'account_create', 'credential_rotate', 'credential_revoke', 'sessions_revoke', 'account_suspend', 'account_reactivate',
  'starter_backfill',
  'revalidate', 'participant_void', 'host_declared_lost', 'host_drain', 'host_enable', 'build_disable', 'energy_adjust'));
revoke insert, update, delete on inventories from accounts_app;
grant insert (account_id), update (version, starter_granted, starter_catalogue_version, starter_granted_at) on inventories to accounts_app;
revoke insert, update, delete on item_instances from accounts_app;
grant insert (account_id, definition_id, operative_id, equipped_slot, origin, origin_ref) on item_instances to accounts_app;
revoke insert, update, delete on item_stacks from accounts_app;
grant insert (account_id, operative_id, definition_id, quantity) on item_stacks to accounts_app;
revoke all on sequence item_instances_item_id_seq from accounts_app;

-- The starter grant record is final and the version never decreases (owner ruling D4: a starter is never re-granted or
-- replaced). Column grants can't say "one way", so this trigger does, following 0002: once starter_granted is true no
-- statement may change starter_granted, starter_catalogue_version or starter_granted_at, and no statement may lower
-- version (which would let a stale edit win again, an ABA on the shared version). The false -> true grant, with all three
-- columns set together (the all-or-nothing CHECK), and version = version + 1 still pass. The app role can't disable,
-- drop or replace the trigger (only the table owner can), nor skip it with session_replication_role (superuser only).
-- Triggers fire whatever the invoker's EXECUTE privilege, so the function stays unexecutable by the app and PUBLIC
-- (0001's global default revoke).
create function inventory_is_forward_only() returns trigger language plpgsql as $$
begin
  if old.starter_granted and (new.starter_granted is distinct from old.starter_granted
      or new.starter_catalogue_version is distinct from old.starter_catalogue_version
      or new.starter_granted_at is distinct from old.starter_granted_at) then
    raise exception 'the starter grant is final: inventories.starter_* cannot change once granted'
      using errcode = 'check_violation', constraint = 'starter_grant_is_final';
  end if;
  if new.version < old.version then
    raise exception 'the inventory version never decreases'
      using errcode = 'check_violation', constraint = 'inventory_version_never_decreases';
  end if;
  return new;
end $$;

create trigger inventories_forward_only before update on inventories
  for each row execute function inventory_is_forward_only();

-- A populated schema-7 database for the 0008 upgrade (owner ruling B1: tested from a populated schema 7). Three accounts:
-- one admitted attempt, one cancelled by the player and one cancelled for no capacity, each with its participant (the
-- snapshot names its catalogue version), a zero-amount hold (no ledger needed) and known secrets that 0008 must copy
-- into attempt_secrets before it drops the columns. Runs as the migrator through every trigger.
insert into accounts (display_name) values ('upgrade-a'), ('upgrade-b'), ('upgrade-c');

insert into mission_attempts (attempt_id, created_by, template_id, template_version, seed, retention_salt, assign_deadline)
select ('0192f0c1-0000-7000-8000-00000000000' || a.n)::uuid, acc.account_id, 'procedural-standard', 1, 1000 + a.n,
       decode(repeat(lpad(to_hex(a.n), 2, '0'), 32), 'hex'), statement_timestamp() + interval '60 seconds'
from (values (1, 'upgrade-a'), (2, 'upgrade-b'), (3, 'upgrade-c')) as a(n, name)
join accounts acc on acc.display_name = a.name;

insert into attempt_participants (attempt_id, account_id, template_id, template_version, loadout_snapshot, inventory_version, energy_cost)
select attempt_id, created_by, template_id, template_version,
       jsonb_build_object('accountId', created_by::text, 'inventoryVersion', 1,
                          'catalogueVersion', 'sha256:' || repeat('ab', 32), 'operatives', jsonb_build_array()),
       1, 0
from mission_attempts;

insert into energy_holds (attempt_id, account_id, amount) select attempt_id, account_id, 0 from attempt_participants;

update mission_attempts set state = 'cancelled', cancel_reason = 'player_cancel', ended_at = statement_timestamp()
  where attempt_id = '0192f0c1-0000-7000-8000-000000000002';
update mission_attempts set state = 'cancelled', cancel_reason = 'no_capacity', ended_at = statement_timestamp()
  where attempt_id = '0192f0c1-0000-7000-8000-000000000003';
update attempt_participants set state = 'released'
  where attempt_id in ('0192f0c1-0000-7000-8000-000000000002', '0192f0c1-0000-7000-8000-000000000003');
update energy_holds set state = 'released'
  where attempt_id in ('0192f0c1-0000-7000-8000-000000000002', '0192f0c1-0000-7000-8000-000000000003');

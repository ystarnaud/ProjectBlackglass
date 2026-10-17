-- Violations (one row each) of the Stage 8 lifecycle (spec §0.8, §7.4-§7.5, §15.2, §18.3):
--   attempt_secrets_missing   an attempt without exactly one attempt_secrets row (B1);
--   hold_charge               a hold whose charge entries (kind 'charge', its attempt and account) are not exactly one of
--                             delta 0 when it was charged (charged, settled or refunded) with a positive amount, or none
--                             otherwise (B9);
--   started_participant       a participant with a start whose hold was never charged or whose attempt never ran (its
--                             attempt is not running, ending, unresponsive or settled);
--   ticket_session            a redeemed ticket whose session is not its attempt's process session;
--   build_not_allowed         an assigned-or-later attempt whose build is not allowlisted for its template version (B4);
--   slot_port_below_host      a slot whose port is below its host's port base (the base never changes);
--   loot_hash                 a ready-or-later attempt whose stored loot hash is not the canonical hash of its stored pool
--                             and table version (R3), or that registered no objective.
with charges as (
  select attempt_id, account_id, count(*) as entries, coalesce(sum(abs(delta::bigint)), 0) as moved
  from energy_ledger where kind = 'charge' group by attempt_id, account_id)
select 'attempt_secrets_missing' as kind, a.attempt_id::text as id
from mission_attempts a
where (select count(*) from attempt_secrets s where s.attempt_id = a.attempt_id) <> 1
union all
select 'hold_charge', h.attempt_id::text || '/' || h.account_id::text
from energy_holds h
left join charges c on c.attempt_id = h.attempt_id and c.account_id = h.account_id
where coalesce(c.entries, 0) <> case when h.amount > 0 and h.state in ('charged', 'settled', 'refunded') then 1 else 0 end
   or coalesce(c.moved, 0) <> 0
union all
select 'started_participant', p.attempt_id::text || '/' || p.account_id::text
from attempt_participants p
join mission_attempts a on a.attempt_id = p.attempt_id
join energy_holds h on h.attempt_id = p.attempt_id and h.account_id = p.account_id
where p.started_at is not null
  and (h.state not in ('charged', 'settled', 'refunded') or a.state not in ('running', 'ending', 'unresponsive', 'settled'))
union all
select 'ticket_session', t.ticket_hash::text
from join_tickets t
join mission_attempts a on a.attempt_id = t.attempt_id
where t.redeemed_by_session is not null and t.redeemed_by_session is distinct from a.process_session_id
union all
select 'build_not_allowed', a.attempt_id::text
from mission_attempts a
where a.build_revision is not null
  and not exists (select 1 from allowed_build_templates t
                  where t.build_revision = a.build_revision and t.catalogue_version = a.catalogue_version
                    and t.template_id = a.template_id and t.template_version = a.template_version)
union all
select 'slot_port_below_host', a.attempt_id::text
from mission_attempts a
join mission_hosts h on h.host_id = a.host_id
where h.port_base is null or a.mission_port < h.port_base
union all
select 'loot_hash', a.attempt_id::text
from mission_attempts a
where a.loot_hash is not null
  and (a.loot_hash <> 'sha256:' || encode(sha256(convert_to(
         'blackglass-loot-pool/1' || E'\n' || 'table=' || a.loot_table_version::text || E'\n'
         || coalesce((select string_agg(l.container_index::text || ',' || l.line::text || ',' || l.definition_id || ',' || l.quantity::text || E'\n',
                                        '' order by l.container_index, l.line)
                      from attempt_loot_pool l where l.attempt_id = a.attempt_id), ''),
         'UTF8')), 'hex')
       or not exists (select 1 from attempt_objectives o where o.attempt_id = a.attempt_id));

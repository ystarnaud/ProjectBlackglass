-- Violations (one row each) of Stage 9 settlement (spec §0.9; plan §D item 12; §18.3 additions):
--   settled_without_row       a settled participant without exactly one settlement row;
--   row_without_settled       a settlement row whose participant is not settled;
--   hold_settle               a hold whose 'settle' entries are not exactly one of delta 0 when it is settled with a positive
--                             amount, or none otherwise;
--   hold_refund               a hold whose 'void_refund' entries are not at most one, positive and at most its amount, and
--                             only when it is refunded with a positive amount;
--   settled_hold_state        a settlement whose hold is not settled (a result) or refunded (a void);
--   submission_not_settled    a result settlement whose submission is not its participant's with disposition 'settled', or
--                             one an operator's revalidate action settled;
--   revoked_settlement        a result settlement of an attempt revoked before it, without a revalidate action covering it;
--   evidence_void             an exit_evidence void without a qualifying agent report: exit status, not revoked, and the
--                             participant listed absent by a complete scan (the report itself, or its outbox observation);
--   pool_collection           a pool_collections row that differs from the sum of its attempt's settled manifests' collected
--                             quantities (read from the stored canonical manifests), or a settled line missing from it;
--   minted_not_in_receipt     a settlement: or insurance: item whose id its participant's receipt does not list;
--   catalogue_version         a settlement whose catalogue is not its attempt's, or a result settlement's catalogue missing
--                             from the history;
--   quarantine_unalerted      a quarantined participant without a result_quarantined, result_malformed, catalogue_unavailable,
--                             recorded_hash_mismatch or quarantine_escalation anomaly naming it (reviewed or not);
--   settled_attempt_open      a settled attempt with a participant neither settled nor released;
--   ending_without_time       an attempt in ending without the ending_at its trigger stamps (the stalled alert counts from it);
--   submission_payload        a submission without exactly one payload row.
with settles as (
  select attempt_id, account_id, count(*) as entries, coalesce(sum(abs(delta::bigint)), 0) as moved
  from energy_ledger where kind = 'settle' group by attempt_id, account_id),
refunds as (
  select attempt_id, account_id, count(*) as entries, coalesce(sum(delta::bigint), 0) as moved, min(delta) as smallest
  from energy_ledger where kind = 'void_refund' group by attempt_id, account_id),
collected as (
  select s.attempt_id, (c ->> 'containerIndex')::int as container_index, (c ->> 'line')::int as line, sum((c ->> 'quantity')::bigint) as total
  from participant_settlements s
  join result_submission_payloads r on r.submission_id = s.submission_id
  cross join lateral jsonb_array_elements(convert_from(r.canonical_bytes, 'UTF8')::jsonb -> 'operatives') o
  cross join lateral jsonb_array_elements(o -> 'collected') c
  group by s.attempt_id, (c ->> 'containerIndex')::int, (c ->> 'line')::int)
select 'settled_without_row' as kind, p.attempt_id::text || '/' || p.account_id::text as id
from attempt_participants p
where p.state = 'settled' and (select count(*) from participant_settlements s where s.attempt_id = p.attempt_id and s.account_id = p.account_id) <> 1
union all
select 'row_without_settled', s.attempt_id::text || '/' || s.account_id::text
from participant_settlements s join attempt_participants p using (attempt_id, account_id)
where p.state <> 'settled'
union all
select 'hold_settle', h.attempt_id::text || '/' || h.account_id::text
from energy_holds h left join settles e using (attempt_id, account_id)
where coalesce(e.entries, 0) <> case when h.state = 'settled' and h.amount > 0 then 1 else 0 end or coalesce(e.moved, 0) <> 0
union all
select 'hold_refund', h.attempt_id::text || '/' || h.account_id::text
from energy_holds h left join refunds e using (attempt_id, account_id)
where coalesce(e.entries, 0) > 1
   or (coalesce(e.entries, 0) = 1 and (h.state <> 'refunded' or h.amount = 0 or e.smallest <= 0 or e.moved > h.amount))
union all
select 'settled_hold_state', s.attempt_id::text || '/' || s.account_id::text
from participant_settlements s join energy_holds h using (attempt_id, account_id)
where h.state <> case when s.outcome = 'infra_void' then 'refunded' else 'settled' end
union all
select 'submission_not_settled', s.attempt_id::text || '/' || s.account_id::text
from participant_settlements s
where s.outcome <> 'infra_void'
  and not exists (select 1 from result_submissions r where r.submission_id = s.submission_id and r.attempt_id = s.attempt_id
                  and r.account_id = s.account_id and r.disposition = 'settled')
  and not exists (select 1 from operator_actions o where o.action = 'revalidate' and o.attempt_id = s.attempt_id and o.account_id = s.account_id
                  and o.result ->> 'result' = 'settled' and (o.result ->> 'submissionId')::bigint = s.submission_id)
union all
select 'revoked_settlement', s.attempt_id::text || '/' || s.account_id::text
from participant_settlements s join mission_attempts a using (attempt_id)
where s.outcome <> 'infra_void' and a.credential_revoked_at is not null and a.credential_revoked_at < s.settled_at
  and not exists (select 1 from operator_actions o where o.action = 'revalidate' and o.attempt_id = s.attempt_id
                  and o.account_id = s.account_id and o.result ->> 'result' = 'settled')
union all
select 'evidence_void', s.attempt_id::text || '/' || s.account_id::text
from participant_settlements s
where s.void_basis = 'exit_evidence'
  and not exists (select 1 from attempt_exit_reports r
                  where r.attempt_id = s.attempt_id and r.source = 'agent' and r.attempt_revoked = false
                    and (r.exit_code is not null or r.signal is not null)
                    and ((r.outbox_complete and exists (select 1 from attempt_exit_results x where x.attempt_id = s.attempt_id
                                                        and x.account_id = s.account_id and not x.present))
                         or exists (select 1 from attempt_outbox_observations ob join attempt_outbox_observation_results x using (attempt_id)
                                    where ob.attempt_id = s.attempt_id and not ob.attempt_revoked and x.account_id = s.account_id and not x.present)))
union all
select 'pool_collection', coalesce(pc.attempt_id, c.attempt_id)::text || '/' || coalesce(pc.container_index, c.container_index)::text
       || '/' || coalesce(pc.line, c.line)::text
from pool_collections pc full join collected c using (attempt_id, container_index, line)
where pc.collected is distinct from c.total
union all
select 'minted_not_in_receipt', m.item_id::text
from (select i.item_id, i.account_id, i.origin, i.origin_ref,
             (with recursive d(n, s) as (select i.item_id, ''::text
                                         union all
                                         select n / 58, substr('123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz', (n % 58)::int + 1, 1) || s
                                         from d where n > 0)
              select s from d where n = 0) as b58
      from item_instances i where i.origin in ('settlement', 'insurance')) m
where not exists (
  select 1 from participant_settlements s
  where s.account_id = m.account_id and starts_with(m.origin_ref, m.origin || ':' || s.attempt_id::text || ':')
    and (exists (select 1 from jsonb_array_elements(s.receipt::jsonb -> 'granted') g cross join lateral jsonb_array_elements_text(g -> 'itemIds') x
                 where x = m.b58)
         or exists (select 1 from jsonb_array_elements(coalesce(s.receipt::jsonb -> 'insurance' -> 'retained', '[]'::jsonb)) r
                    cross join lateral jsonb_array_elements_text(r -> 'itemIds') x where x = m.b58)))
union all
select 'catalogue_version', s.attempt_id::text || '/' || s.account_id::text
from participant_settlements s join mission_attempts a using (attempt_id)
where s.catalogue_version <> a.catalogue_version
   or (s.outcome <> 'infra_void' and not exists (select 1 from catalogue_versions c where c.version = s.catalogue_version))
union all
select 'quarantine_unalerted', p.attempt_id::text || '/' || p.account_id::text
from attempt_participants p
where p.state = 'quarantined'
  and not exists (select 1 from anomaly_events e where e.kind in ('result_quarantined', 'result_malformed', 'catalogue_unavailable', 'recorded_hash_mismatch', 'quarantine_escalation')
                  and e.attempt_id = p.attempt_id and e.account_id = p.account_id)
union all
select 'settled_attempt_open', a.attempt_id::text
from mission_attempts a
where a.state = 'settled'
  and exists (select 1 from attempt_participants p where p.attempt_id = a.attempt_id and p.state not in ('settled', 'released'))
union all
select 'ending_without_time', a.attempt_id::text
from mission_attempts a
where a.state = 'ending' and a.ending_at is null
union all
select 'submission_payload', r.submission_id::text
from result_submissions r
where not exists (select 1 from result_submission_payloads x where x.submission_id = r.submission_id);

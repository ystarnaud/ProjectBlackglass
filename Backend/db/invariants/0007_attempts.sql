-- Violations (one row each) of the attempts, holds and insurance snapshots (Stage 7; spec §7.1, §15.2, §18.3):
--   hold_state                 a participant without a hold, or whose hold's state is not its own state's: admitted ->
--                              reserved, released -> released, settled -> settled or refunded, every other (started)
--                              state -> charged;
--   hold_reserve               a hold whose reserve entries (kind 'reserve', its attempt and account) are not exactly one
--                              of delta -amount for a positive amount, or none at all for a zero amount (a zero-cost
--                              template takes no energy path);
--   hold_release               a hold whose release entries are not exactly one of delta +amount when it is released with a
--                              positive amount, or none otherwise;
--   entry_without_hold         a reserve or release entry whose attempt and account have no hold;
--   two_active_participations  an account with more than one participation in an active state (the states of
--                              one_active_participation);
--   participant_template       a participant whose template or template version differs from its attempt's (or whose
--                              attempt is missing);
--   insurance_terms            an insurance snapshot whose terms differ from its policy version's terms (or whose policy
--                              version is missing);
--   ended_attempt_active_participant
--                              an ended attempt (cancelled or settled) that still has a participant in an active state: a
--                              partial release that would leave the inventory locked and the energy reserved forever. It is
--                              one-directional on purpose: a participant may end while its attempt goes on (Stage 8's co-op
--                              join deadline releases one participant of a continuing attempt);
--   hold_amount                a hold whose amount differs from its participant's recorded energy_cost (the two duplicate
--                              one value; hold_reserve ties the ledger to the hold, this ties the hold to the participant).
-- Sums are bigint, so a corrupt ledger can't overflow the check itself.
with entries as (
  select attempt_id, account_id,
         count(*) filter (where kind = 'reserve') as reserves,
         coalesce(sum(delta::bigint) filter (where kind = 'reserve'), 0) as reserved,
         count(*) filter (where kind = 'release') as releases,
         coalesce(sum(delta::bigint) filter (where kind = 'release'), 0) as released
  from energy_ledger
  where kind in ('reserve', 'release')
  group by attempt_id, account_id)
select 'hold_state' as kind, p.attempt_id::text || '/' || p.account_id::text as id
from attempt_participants p
left join energy_holds h on h.attempt_id = p.attempt_id and h.account_id = p.account_id
where h.attempt_id is null or not case p.state
        when 'admitted' then h.state = 'reserved'
        when 'released' then h.state = 'released'
        when 'settled' then h.state in ('settled', 'refunded')
        else h.state = 'charged'
      end
union all
select 'hold_reserve', h.attempt_id::text || '/' || h.account_id::text
from energy_holds h
left join entries e on e.attempt_id = h.attempt_id and e.account_id = h.account_id
where coalesce(e.reserves, 0) <> case when h.amount > 0 then 1 else 0 end
   or coalesce(e.reserved, 0) <> -h.amount::bigint
union all
select 'hold_release', h.attempt_id::text || '/' || h.account_id::text
from energy_holds h
left join entries e on e.attempt_id = h.attempt_id and e.account_id = h.account_id
where coalesce(e.releases, 0) <> case when h.amount > 0 and h.state = 'released' then 1 else 0 end
   or coalesce(e.released, 0) <> case when h.amount > 0 and h.state = 'released' then h.amount::bigint else 0 end
union all
select 'entry_without_hold', l.entry_id::text
from energy_ledger l
where l.kind in ('reserve', 'release')
  and not exists (select 1 from energy_holds h where h.attempt_id = l.attempt_id and h.account_id = l.account_id)
union all
select 'two_active_participations', p.account_id::text
from attempt_participants p
where p.state in ('admitted', 'joined', 'left', 'result_pending', 'unresponsive', 'quarantined')
group by p.account_id
having count(*) > 1
union all
select 'participant_template', p.attempt_id::text || '/' || p.account_id::text
from attempt_participants p
left join mission_attempts a on a.attempt_id = p.attempt_id
where a.attempt_id is null or p.template_id <> a.template_id or p.template_version <> a.template_version
union all
select 'insurance_terms', i.attempt_id::text || '/' || i.account_id::text
from attempt_insurance i
left join insurance_policies p on p.policy_id = i.policy_id and p.version = i.policy_version
where p.terms is distinct from i.terms
union all
select 'ended_attempt_active_participant', p.attempt_id::text || '/' || p.account_id::text
from attempt_participants p
join mission_attempts a on a.attempt_id = p.attempt_id
where a.state in ('cancelled', 'settled')
  and p.state in ('admitted', 'joined', 'left', 'result_pending', 'unresponsive', 'quarantined')
union all
select 'hold_amount', h.attempt_id::text || '/' || h.account_id::text
from energy_holds h
join attempt_participants p on p.attempt_id = h.attempt_id and p.account_id = h.account_id
where h.amount <> p.energy_cost;

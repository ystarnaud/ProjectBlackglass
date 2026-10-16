-- Violations (one row each) of the energy ledger (Stage 6; spec §15.2, §18.3):
--   balance_vs_sum               a wallet whose balance is not the sum of its ledger deltas;
--   balance_vs_last_entry        a wallet whose balance is not the balance_after of its latest entry (or that has none);
--   entry_chain                  an entry whose balance_after is not the previous entry's balance_after (0 for the first)
--                                plus its delta;
--   open_missing                 a wallet with no 'open' entry;
--   open_not_first               an 'open' entry that is not its wallet's first;
--   progress_not_below_interval  a wallet whose regen_progress_us is not below its policy's interval in microseconds.
-- Sums are bigint, so a corrupt ledger can't overflow the check itself.
with sums as (
  select account_id, sum(delta::bigint) as total from energy_ledger group by account_id),
latest as (
  select distinct on (account_id) account_id, balance_after from energy_ledger order by account_id, entry_id desc),
chain as (
  select entry_id, delta, balance_after, lag(balance_after, 1, 0) over (partition by account_id order by entry_id) as previous
  from energy_ledger),
firsts as (
  select account_id, min(entry_id) as first_entry from energy_ledger group by account_id)
select 'balance_vs_sum' as kind, w.account_id as id
from energy_wallets w left join sums s on s.account_id = w.account_id
where w.balance <> coalesce(s.total, 0)
union all
select 'balance_vs_last_entry', w.account_id
from energy_wallets w left join latest l on l.account_id = w.account_id
where l.balance_after is distinct from w.balance
union all
select 'entry_chain', c.entry_id
from chain c
where c.balance_after::bigint <> c.previous::bigint + c.delta
union all
select 'open_missing', w.account_id
from energy_wallets w
where not exists (select 1 from energy_ledger e where e.account_id = w.account_id and e.kind = 'open')
union all
select 'open_not_first', e.entry_id
from energy_ledger e join firsts f on f.account_id = e.account_id
where e.kind = 'open' and e.entry_id <> f.first_entry
union all
select 'progress_not_below_interval', w.account_id
from energy_wallets w join energy_policies p on p.version = w.policy_version
where w.regen_progress_us >= (extract(epoch from p.regen_interval) * 1000000)::bigint;

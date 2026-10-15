-- Violations (one row each): an operative, item instance or item stack whose account has no inventories row with
-- starter_granted = true; or an operative whose choice ordinals are not exactly 0..n-1 (ordinals are unique per
-- operative, so that is the same as: the largest ordinal is the count minus one).
select 'operative' as kind, o.operative_id as id
from operatives o
where not exists (select 1 from inventories i where i.account_id = o.account_id and i.starter_granted)
union all
select 'item', t.item_id
from item_instances t
where not exists (select 1 from inventories i where i.account_id = t.account_id and i.starter_granted)
union all
select 'stack', s.account_id
from item_stacks s
where not exists (select 1 from inventories i where i.account_id = s.account_id and i.starter_granted)
union all
select 'choice_ordinals', c.operative_id
from operative_choices c
group by c.operative_id
having min(c.ordinal) <> 0 or max(c.ordinal) <> count(*) - 1;

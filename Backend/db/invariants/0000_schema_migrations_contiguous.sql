-- Violations: the migration history must be 0000, 0001, ... with no gaps (spec §15.4).
select s.version
from schema_migrations s
where s.version > 0
  and not exists (select 1 from schema_migrations p where p.version = s.version - 1)
union all
select 0
where exists (select 1 from schema_migrations)
  and not exists (select 1 from schema_migrations where version = 0);

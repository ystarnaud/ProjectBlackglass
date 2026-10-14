-- Exact row count of every table in the public schema, then a digest of schema_migrations. backup.sh runs it inside
-- the dump's own snapshot and restore-drill.sh runs it on the restored copy; the two outputs must be identical.
select table_name || ' ' || (xpath('/row/c/text()',
         query_to_xml(format('select count(*) as c from public.%I', table_name), false, true, '')))[1]::text
from information_schema.tables
where table_schema = 'public' and table_type = 'BASE TABLE'
order by table_name;

select 'schema_migrations ' || case
  when to_regclass('public.schema_migrations') is null then 'absent'
  else (xpath('/row/d/text()', query_to_xml(
          'select coalesce(string_agg(version || '':'' || encode(checksum, ''hex''), '','' order by version), ''empty'') as d from schema_migrations',
          false, true, '')))[1]::text
end;

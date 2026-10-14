-- 0000 foundation (decision 048, spec §15.4): database and schema privileges for the two application roles.
-- The database is owned by accounts_migrator, which runs this without superuser rights.
-- Later migrations create tables as accounts_migrator; the default privileges below give accounts_app data access
-- (no TRUNCATE, no DDL). Append-only tables revoke UPDATE and DELETE in their own migration.

do $$
begin
  execute format('revoke all on database %I from public', current_database());
  execute format('grant connect on database %I to accounts_app', current_database());
end
$$;

revoke all on schema public from public;
grant usage on schema public to accounts_app;

alter default privileges for role accounts_migrator in schema public
  grant select, insert, update, delete on tables to accounts_app;
alter default privileges for role accounts_migrator in schema public
  grant usage, select on sequences to accounts_app;

-- The migrator creates schema_migrations before running 0000, so it needs an explicit grant: read-only.
grant select on schema_migrations to accounts_app;

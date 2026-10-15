-- 0002 a revocation is permanent (Stage 3 security correction; spec §15.1).
-- 0001 lets accounts_app UPDATE revoked_at on dev_credentials and auth_sessions, which revocation needs; a column
-- grant can't say "only from null". This trigger does: once revoked_at is set, no statement may clear it or change
-- it. Setting it again to the same value is a no-op and passes. The app role can't disable, drop or replace the
-- trigger (only the table owner can), nor skip it with session_replication_role (superuser only).
-- Triggers fire whatever the invoker's EXECUTE privilege, so the function stays unexecutable by the app and PUBLIC
-- (0001's global default revoke).

create function revocation_is_final() returns trigger language plpgsql as $$
begin
  if old.revoked_at is not null and new.revoked_at is distinct from old.revoked_at then
    raise exception 'a revocation is permanent: %.revoked_at cannot be cleared or changed', tg_table_name
      using errcode = 'check_violation', constraint = 'revocation_is_final';
  end if;
  return new;
end $$;

create trigger dev_credentials_revocation_is_final before update on dev_credentials
  for each row execute function revocation_is_final();
create trigger auth_sessions_revocation_is_final before update on auth_sessions
  for each row execute function revocation_is_final();

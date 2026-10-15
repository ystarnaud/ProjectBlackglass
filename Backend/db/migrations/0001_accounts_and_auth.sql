-- 0001 accounts and development authentication (decision 048 and its amendment; spec §4.1, §4.2, §15.1; Stage 3).
-- Account ids are bigint, generated only by PostgreSQL. Credential, session and correlation ids are UUIDs minted by
-- the API. Secrets are stored as SHA-256 only.
-- Rows are stamped with statement_timestamp(), not now() (the transaction's start): a call that waited for an account
-- lock then stamps what it creates or revokes after the change it waited for, so a successor credential is never
-- created before its predecessor was revoked, and audit times follow the order changes committed in.

-- Stage 1 carry-over: PostgreSQL grants EXECUTE on every new function to PUBLIC. Revoke that default for the migrator's
-- future functions, globally (a per-schema revoke can't remove the built-in global default; verified on 17.11). A later
-- migration that creates a function grants EXECUTE to accounts_app explicitly when the app needs it.
alter default privileges for role accounts_migrator revoke execute on functions from public;

create table accounts (
  account_id bigint generated always as identity primary key check (account_id > 0),
  status text not null default 'active' check (status in ('active', 'suspended')),
  display_name text check (display_name is null
    or (length(display_name) between 1 and 64 and display_name !~ '[[:cntrl:]]' and display_name !~ '^\s|\s$')),
  created_at timestamptz not null default statement_timestamp());

-- The optional development handle: at most one per account, and a handle names one account.
create table account_identities (
  provider text not null check (provider in ('dev')),
  subject text not null check (subject ~ '^[a-z0-9]+([.-][a-z0-9]+)*$' and length(subject) <= 64),
  account_id bigint not null references accounts,
  created_at timestamptz not null default statement_timestamp(),
  primary key (provider, subject),
  unique (provider, account_id));

create table dev_credentials (
  credential_id uuid primary key,
  account_id bigint not null references accounts,
  secret_hash bytea not null unique check (length(secret_hash) = 32),
  created_at timestamptz not null default statement_timestamp(),
  revoked_at timestamptz,
  check (revoked_at is null or revoked_at >= created_at),
  unique (credential_id, account_id));
-- Rotation leaves exactly one live credential; the database refuses a second.
create unique index dev_credentials_one_live on dev_credentials (account_id) where revoked_at is null;

-- A session is bound to the credential that created it, and to that credential's account.
create table auth_sessions (
  session_id uuid primary key,
  account_id bigint not null,
  credential_id uuid not null,
  token_hash bytea not null unique check (length(token_hash) = 32),
  created_at timestamptz not null default statement_timestamp(),
  expires_at timestamptz not null,
  revoked_at timestamptz,
  check (expires_at > created_at),
  check (revoked_at is null or revoked_at >= created_at),
  foreign key (credential_id, account_id) references dev_credentials (credential_id, account_id));
create index auth_sessions_live_account on auth_sessions (account_id) where revoked_at is null;
create index auth_sessions_live_credential on auth_sessions (credential_id) where revoked_at is null;

-- Every operator decision, append-only (spec §15.1; moved here from 0006 for the account lifecycle, owner Q3).
create table operator_actions (
  id bigint generated always as identity primary key,
  correlation_id uuid not null,
  operator text not null check (length(operator) between 1 and 200 and operator !~ '[[:cntrl:]]'),
  action text not null check (action in (
    'account_create', 'credential_rotate', 'credential_revoke', 'sessions_revoke', 'account_suspend', 'account_reactivate',
    'revalidate', 'participant_void', 'host_declared_lost', 'host_drain', 'host_enable', 'build_disable', 'energy_adjust')),
  attempt_id uuid,
  account_id bigint references accounts,
  credential_id uuid references dev_credentials,
  host_id text,
  reason text not null check (length(reason) between 1 and 1000 and reason !~ '[[:cntrl:]]'),
  checks jsonb not null,
  result jsonb not null,
  created_at timestamptz not null default statement_timestamp());
create index operator_actions_account on operator_actions (account_id) where account_id is not null;

-- accounts_app privileges, narrowed from 0000's table-level defaults to the columns the app writes. It can never choose
-- an account or audit id (not even with OVERRIDING SYSTEM VALUE), set a creation time, change a primary key or a
-- stored hash, re-point a row to another account, delete, or touch an identity sequence (inserts need no privilege on
-- it). FOR SHARE/FOR UPDATE need UPDATE on one column: login and the lifecycle lock accounts and dev_credentials rows.
revoke insert, update, delete on accounts from accounts_app;
grant insert (status, display_name), update (status, display_name) on accounts to accounts_app;
revoke insert, update, delete on account_identities from accounts_app;
grant insert (provider, subject, account_id) on account_identities to accounts_app;
revoke insert, update, delete on dev_credentials from accounts_app;
grant insert (credential_id, account_id, secret_hash), update (revoked_at) on dev_credentials to accounts_app;
revoke insert, update, delete on auth_sessions from accounts_app;
grant insert (session_id, account_id, credential_id, token_hash, expires_at), update (revoked_at) on auth_sessions to accounts_app;
revoke insert, update, delete on operator_actions from accounts_app;
grant insert (correlation_id, operator, action, attempt_id, account_id, credential_id, host_id, reason, checks, result)
  on operator_actions to accounts_app;
revoke all on sequence accounts_account_id_seq, operator_actions_id_seq from accounts_app;

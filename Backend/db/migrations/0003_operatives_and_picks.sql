-- 0003 operatives and advancement picks (Stage 4; decision 048 amendment 2; spec §5.3, §15.1).
-- Operative ids are bigint identities minted only by PostgreSQL (Base58 outside). definition_id is the catalogue's
-- permanent operative GUID (validated in code). (origin, origin_ref) makes every grant mint at most one operative.
create table operatives (
  operative_id bigint generated always as identity primary key check (operative_id > 0),
  account_id bigint not null references accounts,
  definition_id text not null check (length(definition_id) <= 64 and definition_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  experience integer not null default 0 check (experience >= 0),
  origin text not null check (origin in ('starter')),
  origin_ref text not null check (length(origin_ref) <= 300 and origin_ref !~ '[[:space:][:cntrl:]]'),
  created_at timestamptz not null default statement_timestamp(),
  unique (account_id, operative_id),
  unique (origin, origin_ref));
create table operative_choices (                        -- append-only: no respec
  operative_id bigint not null references operatives,
  ordinal smallint not null check (ordinal >= 0),
  choice_id text not null check (length(choice_id) <= 64 and choice_id ~ '^[a-z0-9]+([.-][a-z0-9]+)*$'),
  picked_at timestamptz not null default statement_timestamp(),
  primary key (operative_id, ordinal));
revoke insert, update, delete on operatives from accounts_app;
grant insert (account_id, definition_id, origin, origin_ref) on operatives to accounts_app;
revoke insert, update, delete on operative_choices from accounts_app;
grant insert (operative_id, ordinal, choice_id) on operative_choices to accounts_app;
revoke all on sequence operatives_operative_id_seq from accounts_app;

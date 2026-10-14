#!/bin/sh
# Runs once, on the first start of an empty data directory (official postgres image, as the postgres superuser over
# the local socket). Creates the two application roles and the accounts database; passwords come from Compose secrets.
set -eu
psql -v ON_ERROR_STOP=1 --username "${POSTGRES_USER:-postgres}" --dbname postgres <<'SQL'
\set migrator_pw `cat /run/secrets/pg_init_migrator_password`
\set app_pw `cat /run/secrets/pg_init_app_password`
create role accounts_migrator login nosuperuser nocreatedb nocreaterole noinherit noreplication password :'migrator_pw';
create role accounts_app      login nosuperuser nocreatedb nocreaterole noinherit noreplication password :'app_pw';
create database accounts owner accounts_migrator;
revoke all on database accounts from public;
grant connect on database accounts to accounts_app;
SQL

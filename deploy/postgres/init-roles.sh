#!/bin/sh
# Runs once, as the PostgreSQL superuser, when the server initialises an empty data directory
# (the postgres image runs every script in /docker-entrypoint-initdb.d).
#
# Creates the four login roles and an empty `auction` schema owned by `migrator`. Everything else,
# tables and grants included, is created by the migrator, so no service ever needs the superuser.
: "${POSTGRES_DB:?}"
: "${MIGRATOR_DB_PASSWORD:?set MIGRATOR_DB_PASSWORD}"
: "${ENGINE_DB_PASSWORD:?set ENGINE_DB_PASSWORD}"
: "${API_DB_PASSWORD:?set API_DB_PASSWORD}"
: "${VERIFIER_DB_PASSWORD:?set VERIFIER_DB_PASSWORD}"

psql --no-psqlrc -v ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v db="$POSTGRES_DB" \
  -v migrator_password="$MIGRATOR_DB_PASSWORD" \
  -v engine_password="$ENGINE_DB_PASSWORD" \
  -v api_password="$API_DB_PASSWORD" \
  -v verifier_password="$VERIFIER_DB_PASSWORD" <<'SQL'
-- Passwords are stored as SCRAM-SHA-256 (the PostgreSQL 18 default).
CREATE ROLE migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE engine   LOGIN PASSWORD :'engine_password';
CREATE ROLE api      LOGIN PASSWORD :'api_password';
CREATE ROLE verifier LOGIN PASSWORD :'verifier_password';

-- Only these roles may connect, and nobody else gets temporary tables or objects in `public`.
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO migrator, engine, api, verifier;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- Trigram search on titles. Installed here because creating an extension needs more than the
-- migrator is given.
CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;

CREATE SCHEMA auction AUTHORIZATION migrator;
ALTER ROLE migrator SET search_path = auction, public;
ALTER ROLE engine   SET search_path = auction, public;
ALTER ROLE api      SET search_path = auction, public;
ALTER ROLE verifier SET search_path = auction, public;
SQL

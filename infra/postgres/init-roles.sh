#!/usr/bin/env bash
set -euo pipefail
: "${DB_APP_PASSWORD:?Missing application password}"
: "${DB_MIGRATION_PASSWORD:?Missing migration password}"
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\getenv app_password DB_APP_PASSWORD
\getenv migration_password DB_MIGRATION_PASSWORD
CREATE ROLE acropolis_migrator LOGIN PASSWORD :'migration_password' NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE ROLE acropolis_app LOGIN PASSWORD :'app_password' NOSUPERUSER NOCREATEDB NOCREATEROLE;
SELECT format('ALTER DATABASE %I OWNER TO acropolis_migrator', current_database()) \gexec
SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database()) \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO acropolis_app, acropolis_migrator', current_database()) \gexec
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
CREATE SCHEMA platform AUTHORIZATION acropolis_migrator;
GRANT USAGE ON SCHEMA platform TO acropolis_app;
ALTER DEFAULT PRIVILEGES FOR ROLE acropolis_migrator IN SCHEMA platform GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO acropolis_app;
ALTER DEFAULT PRIVILEGES FOR ROLE acropolis_migrator IN SCHEMA platform GRANT USAGE, SELECT ON SEQUENCES TO acropolis_app;
SQL

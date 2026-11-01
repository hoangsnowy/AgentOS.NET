#!/usr/bin/env bash
# postgres-init.sh — first-boot init for the bundled Postgres (docker compose + the Helm chart's StatefulSet).
# Runs once, from /docker-entrypoint-initdb.d, only when the data directory is empty.
#
# The app database (POSTGRES_DB, default `agentos`) is created by the image itself. This adds Keycloak's own
# database (KEYCLOAK_DB, default `keycloak`) on the same server — the same layout the AppHost provisions
# (`postgres.AddDatabase("keycloak-db", databaseName: "keycloak")`).
#
# The row-level-security role `agentos_rls` is NOT created here: the hosts create it, its grants and the
# policies at every boot (RowLevelSecurity.EnsureAsync, ADR-0012), which needs a user with CREATEROLE —
# the bundled POSTGRES_USER is the superuser, so that just works.
set -euo pipefail

db="${KEYCLOAK_DB:-keycloak}"
exists="$(psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres -tAc \
  "SELECT 1 FROM pg_database WHERE datname = '${db//\'/\'\'}'")"
if [[ "$exists" != "1" ]]; then
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
    -c "CREATE DATABASE \"${db//\"/\"\"}\" OWNER \"${POSTGRES_USER//\"/\"\"}\""
  echo "Created database '$db'."
fi

#!/usr/bin/env bash
# Restore the parking_db PostgreSQL database from the dump (Linux / macOS).
# Assumes psql/createdb/pg_restore are on PATH. Edit the password if yours differs.
set -e
export PGPASSWORD="parking123"      # the postgres superuser password
DUMP="$(dirname "$0")/parking_db.dump"

echo "Creating database parking_db..."
createdb -U postgres parking_db 2>/dev/null || true   # ignore if it exists

echo "Restoring data from $DUMP ..."
pg_restore -U postgres -d parking_db --no-owner --clean --if-exists "$DUMP"

echo "Done. Database parking_db is ready."

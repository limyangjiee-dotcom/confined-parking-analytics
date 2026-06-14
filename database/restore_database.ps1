# Restore the parking_db PostgreSQL database from the dump (Windows / PowerShell).
# Edit PG_BIN and PGPASSWORD if your PostgreSQL install or password differ.

$PG_BIN  = "C:\Program Files\PostgreSQL\16\bin"
$env:PGPASSWORD = "parking123"      # <-- the postgres superuser password
$DUMP = Join-Path $PSScriptRoot "parking_db.dump"

Write-Host "Creating database parking_db..."
& "$PG_BIN\createdb.exe" -U postgres parking_db 2>$null   # ignore error if it already exists

Write-Host "Restoring data from $DUMP ..."
& "$PG_BIN\pg_restore.exe" -U postgres -d parking_db --no-owner --clean --if-exists $DUMP

if ($?) { Write-Host "Done. Database parking_db is ready." -ForegroundColor Green }
else    { Write-Host "Restore reported errors — check the output above." -ForegroundColor Yellow }

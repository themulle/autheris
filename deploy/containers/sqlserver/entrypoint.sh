#!/bin/bash
set -e

# Start SQL Server in background
/opt/mssql/bin/sqlservr &
PID=$!

echo "[sqlserver] Waiting for SQL Server engine to be ready..."
for i in $(seq 1 60); do
    if /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT 1" > /dev/null 2>&1; then
        echo "[sqlserver] SQL Server engine is ready!"
        break
    fi
    sleep 1
done

echo "[sqlserver] Executing database initialization script..."
/opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -i /usr/local/bin/init-db.sql
echo "[sqlserver] Initialization complete. Marking ready..."
touch /tmp/.sqlserver_ready

# Keep foreground container process running
wait $PID

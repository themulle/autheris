#!/usr/bin/env bash
# Generates deploy/.env with random per-checkout secrets and deploy/certs/ with a SQL Server TLS certificate (benchmark-only stack).
# Refuses to overwrite an existing .env.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
command -v openssl >/dev/null 2>&1 || { echo "ERROR: openssl is required" >&2; exit 1; }

# SR-D-08: self-signed CA and SQL Server certificate (SAN DNS:sqlserver) for the benchmark stack. deploy/certs/ is gitignored.
if [ ! -f certs/sqlserver.crt ] || [ ! -f certs/sqlserver.key ]; then
    mkdir -p certs
    (
        umask 022
        openssl req -x509 -newkey rsa:4096 -sha256 -nodes -days 365 -subj "/CN=Autheris Benchmark CA" \
            -keyout certs/ca.key -out certs/ca.crt >/dev/null 2>&1
        openssl req -newkey rsa:3072 -sha256 -nodes -subj "/CN=sqlserver" \
            -keyout certs/sqlserver.key -out certs/sqlserver.csr >/dev/null 2>&1
        printf 'subjectAltName=DNS:sqlserver\nextendedKeyUsage=serverAuth\n' > certs/sqlserver.ext
        openssl x509 -req -in certs/sqlserver.csr -CA certs/ca.crt -CAkey certs/ca.key -CAcreateserial -sha256 -days 365 \
            -extfile certs/sqlserver.ext -out certs/sqlserver.crt >/dev/null 2>&1
        rm -f certs/sqlserver.csr certs/sqlserver.ext
        # The SQL Server process runs as a different uid inside the container, so the key must be world-readable on disk.
        # This is benchmark-only material in a gitignored directory; never reuse it outside the benchmark stack.
        chmod 644 certs/sqlserver.key
        chmod 600 certs/ca.key
    )
    echo "Created deploy/certs/ with a benchmark CA and SQL Server certificate (SAN DNS:sqlserver)."
fi

if [ -f .env ]; then
    echo ".env already exists - not overwriting."
    exit 0
fi
rnd() { openssl rand -hex "${1:-24}"; }
umask 077
cat > .env <<ENVEOF
GQL_PG_PASSWORD=$(rnd)
MSSQL_SA_PASSWORD=Aa1!$(rnd 16)
MSSQL_CRM_PASSWORD=Aa1!$(rnd 16)
MINIO_ROOT_USER=bench$(rnd 4)
MINIO_ROOT_PASSWORD=$(rnd)
GRAFANA_ADMIN_PASSWORD=$(rnd 16)
REDIS_PASSWORD=$(rnd)
GATEWAY_HMAC_SECRET=$(rnd 32)
FORWARD_AUTH_SHARED_SECRET=$(rnd 32)
BENCHMARK_SECRET_KEY=$(rnd 32)
OPENMETADATA_WEBHOOK_SECRET=$(rnd 32)
CATALOG_WEBHOOK_SECRET=$(rnd 32)
ITSM_WEBHOOK_SECRET=$(rnd 32)
ENVEOF
echo "Created deploy/.env with random secrets."

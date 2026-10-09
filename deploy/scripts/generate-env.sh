#!/usr/bin/env bash
# Generates deploy/.env with random per-checkout secrets (benchmark-only stack).
# Refuses to overwrite an existing .env.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
if [ -f .env ]; then
    echo ".env already exists - not overwriting."
    exit 0
fi
command -v openssl >/dev/null 2>&1 || { echo "ERROR: openssl is required" >&2; exit 1; }
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
ENVEOF
echo "Created deploy/.env with random secrets."

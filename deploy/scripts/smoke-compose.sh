#!/usr/bin/env bash
# Smoke test for the benchmark compose stack: random secrets, start postgres + redis + gateway, poll the gateway readiness
# probe for up to 120 s, tear down. Uses its own compose project name, so "down -v" only removes smoke resources, and a
# temporary env file, so an existing deploy/.env is never read or modified.
# Requires podman-compose (or "podman compose") and openssl.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

ENV_FILE=""
PROJECT="autheris-smoke-$$"
COMPOSE=()
GENERATED_ENV=0
GENERATED_CERTS=0

cleanup() {
    if [ "${#COMPOSE[@]}" -gt 0 ]; then
        "${COMPOSE[@]}" -p "$PROJECT" --env-file "$ENV_FILE" -f podman-compose.yaml down -v >/dev/null 2>&1 || true
    fi
    [ -n "$ENV_FILE" ] && rm -f "$ENV_FILE"
    if [ "$GENERATED_ENV" = "1" ]; then rm -f .env; fi
    if [ "$GENERATED_CERTS" = "1" ]; then rm -rf certs; fi
}
trap cleanup EXIT

command -v openssl >/dev/null 2>&1 || { echo "ERROR: openssl is required" >&2; exit 1; }
if command -v podman-compose >/dev/null 2>&1; then
    COMPOSE=(podman-compose)
elif command -v podman >/dev/null 2>&1; then
    COMPOSE=(podman compose)
else
    echo "ERROR: podman-compose or podman is required" >&2
    exit 1
fi

# The gateway pins deploy/certs/sqlserver.crt; generate-env.sh creates it (and a .env only when none exists).
if [ ! -f certs/sqlserver.crt ]; then GENERATED_CERTS=1; fi
if [ ! -f .env ]; then GENERATED_ENV=1; fi
bash scripts/generate-env.sh >/dev/null

ENV_FILE="$(mktemp)"
chmod 600 "$ENV_FILE"
# Random values for every key of .env.example; SQL Server needs a complex password.
while IFS='=' read -r key value; do
    case "$key" in ''|\#*) continue ;; esac
    case "$key" in
        MSSQL_*_PASSWORD) printf '%s=Aa1!%s\n' "$key" "$(openssl rand -hex 16)" ;;
        *) printf '%s=%s\n' "$key" "$(openssl rand -hex 24)" ;;
    esac
done < .env.example > "$ENV_FILE"
printf 'SEED_ROW_COUNT=1000\nSEED_ITEMS_COUNT=500\n' >> "$ENV_FILE"

BIND_ADDR="127.0.0.1"
GATEWAY_PORT="${GATEWAY_PORT:-5050}"
export BIND_ADDR GATEWAY_PORT

"${COMPOSE[@]}" -p "$PROJECT" --env-file "$ENV_FILE" -f podman-compose.yaml up -d --build postgres redis gqlgateway-api

URL="http://${BIND_ADDR}:${GATEWAY_PORT}/health/ready"
deadline=$((SECONDS + 120))
until curl -fsS -o /dev/null "$URL"; do
    if [ "$SECONDS" -ge "$deadline" ]; then
        echo "FAIL: $URL not ready after 120 s" >&2
        "${COMPOSE[@]}" -p "$PROJECT" --env-file "$ENV_FILE" -f podman-compose.yaml logs --tail 50 gqlgateway-api >&2 || true
        exit 1
    fi
    sleep 3
done
echo "OK: $URL is ready"

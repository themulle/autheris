#!/usr/bin/env bash
# Smoke test for the benchmark compose stack: random database password, start postgres + redis + gateway + reverse proxy,
# poll the gateway readiness probe for up to 120 s, tear everything down (including volumes).
# Requires podman-compose (or "podman compose") and openssl. No credentials are stored: all secrets are random per run.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
command -v openssl >/dev/null 2>&1 || { echo "ERROR: openssl is required" >&2; exit 1; }

if command -v podman-compose >/dev/null 2>&1; then
    COMPOSE=(podman-compose)
elif command -v podman >/dev/null 2>&1; then
    COMPOSE=(podman compose)
else
    echo "ERROR: podman-compose or podman is required" >&2
    exit 1
fi

CREATED_ENV=0
if [ ! -f .env ]; then
    bash scripts/generate-env.sh >/dev/null
    CREATED_ENV=1
fi

cleanup() {
    "${COMPOSE[@]}" -f podman-compose.yaml down -v >/dev/null 2>&1 || true
    if [ "$CREATED_ENV" = "1" ]; then
        rm -f .env
    fi
}
trap cleanup EXIT

# The shell environment overrides the .env file: the database password is random for every run.
GQL_PG_PASSWORD="$(openssl rand -hex 24)"
export GQL_PG_PASSWORD
export BIND_ADDR="${BIND_ADDR:-127.0.0.1}"
GATEWAY_PORT="${GATEWAY_PORT:-5050}"
export GATEWAY_PORT
export SEED_ROW_COUNT="${SEED_ROW_COUNT:-1000}"
export SEED_ITEMS_COUNT="${SEED_ITEMS_COUNT:-500}"

"${COMPOSE[@]}" -f podman-compose.yaml up -d --build postgres redis gqlgateway-api

URL="http://${BIND_ADDR}:${GATEWAY_PORT}/health/ready"
deadline=$((SECONDS + 120))
until curl -fsS -o /dev/null "$URL"; do
    if [ "$SECONDS" -ge "$deadline" ]; then
        echo "FAIL: $URL not ready after 120 s" >&2
        "${COMPOSE[@]}" -f podman-compose.yaml logs --tail 50 gqlgateway-api >&2 || true
        exit 1
    fi
    sleep 3
done
echo "OK: $URL is ready"

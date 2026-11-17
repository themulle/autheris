#!/usr/bin/env bash
# Starts a container image with the signed Lakehouse module against a throwaway PostgreSQL sidecar (verified TLS) and asserts the module routes.
# Usage: IMAGE_REF=<image reference> bash .github/scripts/module-e2e.sh
# The gateway requires a governance database (PostgreSQL or SQL Server) and, outside Development, a verified-TLS connection to it; the
# sidecar setup mirrors benchmarks/scripts/footprint.sh (throwaway CA and server certificate valid one day, random password per run).
set -euo pipefail

: "${IMAGE_REF:?IMAGE_REF is required}"
IMAGE_REF_LOWER="${IMAGE_REF,,}"

RUN_ID="$$"
NET_NAME="module-e2e-net-$RUN_ID"
PG_NAME="module-e2e-pg-$RUN_ID"
APP_NAME="module-e2e-app-$RUN_ID"
MIGRATE_NAME="module-e2e-migrate-$RUN_ID"
DATA_VOL="module-e2e-data-$RUN_ID"
# Digest-pinned like every other CI image (same major as deploy/podman-compose.yaml).
PG_IMAGE='docker.io/library/postgres:18-alpine@sha256:b6972e1115a4ff8eba5d7f7d19184926d1e16d7d1136ae1e1a7ebf286da0f015'
TLS_DIR="$(mktemp -d)"

cleanup() {
  docker rm -f "$APP_NAME" "$PG_NAME" "$MIGRATE_NAME" >/dev/null 2>&1 || true
  docker volume rm -f "$DATA_VOL" >/dev/null 2>&1 || true
  docker network rm "$NET_NAME" >/dev/null 2>&1 || true
  rm -rf "$TLS_DIR"
}
trap cleanup EXIT

dump_logs() {
  echo "--- app container logs ($1) ---" >&2
  docker logs "$1" >&2 2>&1 || true
  echo "--- postgres sidecar logs ---" >&2
  docker logs "$PG_NAME" >&2 2>&1 || true
}

(
  cd "$TLS_DIR"
  openssl genrsa -out ca.key 2048 2>/dev/null
  openssl req -x509 -new -key ca.key -sha256 -days 1 -subj "/CN=autheris-module-e2e-ca" -out ca.crt
  openssl genrsa -out server.key 2048 2>/dev/null
  openssl req -new -key server.key -subj "/CN=$PG_NAME" -out server.csr
  printf 'subjectAltName=DNS:%s\nextendedKeyUsage=serverAuth\nbasicConstraints=CA:FALSE\n' "$PG_NAME" > server.ext
  openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial -sha256 -days 1 -extfile server.ext -out server.crt 2>/dev/null
  rm -f ca.key ca.srl server.csr server.ext
  chmod 0644 ca.crt server.crt server.key
)
chmod 0755 "$TLS_DIR"

PG_PASSWORD="$(openssl rand -hex 24)"
docker network create "$NET_NAME" >/dev/null
docker volume create "$DATA_VOL" >/dev/null
# The key must be owned by the postgres user with mode 0600; copy it inside the container (root at start) instead of chown-ing on the host.
docker run -d --name "$PG_NAME" --network "$NET_NAME" \
  -e POSTGRES_USER=modulee2e -e POSTGRES_DB=governancedb -e POSTGRES_PASSWORD="$PG_PASSWORD" \
  -v "$TLS_DIR:/tls-src:ro,z" \
  --entrypoint sh "$PG_IMAGE" -c '
    set -e
    mkdir -p /tls
    cp /tls-src/server.crt /tls-src/server.key /tls/
    chown postgres:postgres /tls/server.crt /tls/server.key
    chmod 0600 /tls/server.key
    exec docker-entrypoint.sh postgres -c ssl=on -c ssl_cert_file=/tls/server.crt -c ssl_key_file=/tls/server.key' >/dev/null

PG_READY=false
for _ in $(seq 1 60); do
  if docker exec "$PG_NAME" pg_isready -h 127.0.0.1 -U modulee2e -d governancedb -q >/dev/null 2>&1; then
    PG_READY=true
    break
  fi
  sleep 1
done
if [ "$PG_READY" != "true" ]; then
  echo "Error: PostgreSQL sidecar did not become ready within 60 seconds" >&2
  docker logs "$PG_NAME" >&2 2>&1 || true
  exit 1
fi
# The entrypoint restarts the server once after initdb; wait for it to settle on the TCP socket with TLS.
sleep 2

PG_CONN="Host=$PG_NAME;Port=5432;Database=governancedb;Username=modulee2e;Password=$PG_PASSWORD;SSL Mode=VerifyFull;Root Certificate=/tls/ca.crt"

RUN_ENV=(
  --network "$NET_NAME"
  -v "$TLS_DIR/ca.crt:/tls/ca.crt:ro,z"
  -v "$DATA_VOL:/app/data"
  -e ASPNETCORE_ENVIRONMENT=Production
  -e Gateway__Hosting__AllowedHosts__0=127.0.0.1
  -e Gateway__Hosting__AllowedHosts__1=localhost
  -e Gateway__DataMasking__HmacSecretKeyVaultRef=MODULE_E2E_HMAC_KEY
  -e MODULE_E2E_HMAC_KEY="$(openssl rand -hex 32)"
  -e Gateway__RestApi__BulkStream__SigningKey="$(openssl rand -hex 32)"
  -e Gateway__Authentication__Mode=Upstream
  -e Gateway__Authentication__ForwardAuth__Enabled=true
  -e Gateway__Authentication__ForwardAuth__TrustedProxies__0=127.0.0.1
  -e Gateway__Authentication__ForwardAuth__SharedSecret="$(openssl rand -hex 32)"
  -e Gateway__Audit__ChainAnchorPath=/app/data/audit-chain.anchor
  -e Authentication__Realms__Realms__proxy__Kind=ForwardAuth
  -e Authentication__Realms__Realms__proxy__FixedTenantId=module-e2e
  -e Authentication__Realms__Realms__proxy__Provisioning=None
  -e Gateway__Tenancy__DefaultTenantId=module-e2e
  -e Gateway__Governance__GrantRecords__SigningKey="$(openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt)"
  -e Gateway__SqlEndpoints__Directory=/app/data/queries
  -e Gateway__GovernanceDb__Provider=PostgreSql
  -e Gateway__GovernanceDb__ConnectionString="$PG_CONN"
  -e Gateway__Modules__Path=/app/modules
  -e Gateway__Modules__Enabled__0=lakehouse
)

echo "Applying governance schema migrations (one-off job)..."
if ! docker run --name "$MIGRATE_NAME" "${RUN_ENV[@]}" "$IMAGE_REF_LOWER" --migrate-governance; then
  echo "Error: governance migration job failed" >&2
  dump_logs "$MIGRATE_NAME"
  exit 1
fi
RUN_ENV+=(-e Gateway__GovernanceDb__Migrations__Mode=Verify)

docker run -d --name "$APP_NAME" -p 127.0.0.1:8080:8080 "${RUN_ENV[@]}" "$IMAGE_REF_LOWER" >/dev/null

ready=0
for _ in $(seq 1 60); do
  code="$(curl -sS -o /dev/null -w "%{http_code}" http://127.0.0.1:8080/health/live || true)"
  if [ "$code" = "200" ]; then
    ready=1
    break
  fi
  sleep 1
done
if [ "$ready" != "1" ]; then
  dump_logs "$APP_NAME"
  echo "Error: Container failed to become healthy on /health/live" >&2
  exit 1
fi

module_code="$(curl -sS -o /dev/null -w "%{http_code}" http://127.0.0.1:8080/modules/lakehouse/iceberg/v1/tenant-a/namespaces)"
if [ "$module_code" != "401" ]; then
  dump_logs "$APP_NAME"
  echo "Error: Expected HTTP 401 on /modules/lakehouse/iceberg/v1/tenant-a/namespaces, got $module_code" >&2
  exit 1
fi

legacy_ns_code="$(curl -sS -o /dev/null -w "%{http_code}" http://127.0.0.1:8080/v1/tenant-a/namespaces)"
if [ "$legacy_ns_code" != "404" ]; then
  echo "Error: Expected HTTP 404 on /v1/tenant-a/namespaces, got $legacy_ns_code" >&2
  exit 1
fi

legacy_cfg_code="$(curl -sS -o /dev/null -w "%{http_code}" http://127.0.0.1:8080/v1/config)"
if [ "$legacy_cfg_code" != "404" ]; then
  echo "Error: Expected HTTP 404 on /v1/config, got $legacy_cfg_code" >&2
  exit 1
fi
echo "Signed module container e2e passed."

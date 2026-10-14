#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Autheris Container Hardening & Posture Verification Script
# Ref: SC-08, SC-09, SC-11
# ==============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

echo "==> [Hardening Check] Verifying Compose files and Dockerfiles..."
FAILURES=0

# 1. Check Root Dockerfile
echo "--> Checking Dockerfile (Root)..."
if ! grep -q "USER 10001:10001" "${REPO_ROOT}/Dockerfile"; then
    echo "ERROR: Dockerfile does not specify 'USER 10001:10001'" >&2
    FAILURES=$((FAILURES + 1))
fi

if ! grep -q "HEALTHCHECK" "${REPO_ROOT}/Dockerfile"; then
    echo "ERROR: Dockerfile does not define a HEALTHCHECK" >&2
    FAILURES=$((FAILURES + 1))
fi

# 2. Check docker-compose.yml
echo "--> Checking docker-compose.yml..."
for setting in "read_only: true" "no-new-privileges:true" "cap_drop:" "ALL"; do
    if ! grep -q "${setting}" "${REPO_ROOT}/docker-compose.yml"; then
        echo "ERROR: docker-compose.yml is missing required security option: ${setting}" >&2
        FAILURES=$((FAILURES + 1))
    fi
done

# 3. Check Benchmark Dockerfiles
echo "--> Checking Benchmark Dockerfiles..."
if ! grep -q "USER 10001:10001" "${REPO_ROOT}/benchmarks/load/docker/Dockerfile.gql"; then
    echo "ERROR: Dockerfile.gql does not specify 'USER 10001:10001'" >&2
    FAILURES=$((FAILURES + 1))
fi

if grep -q "ASPNETCORE_ENVIRONMENT=Development" "${REPO_ROOT}/benchmarks/load/docker/Dockerfile.gql"; then
    echo "ERROR: Dockerfile.gql must not run in Development environment" >&2
    FAILURES=$((FAILURES + 1))
fi

# 4. Check sqlserver entrypoint for cleartext password on command line
echo "--> Checking SQL Server entrypoint..."
if grep -q "sqlcmd .* -P" "${REPO_ROOT}/deploy/containers/sqlserver/entrypoint.sh"; then
    echo "ERROR: SQL Server entrypoint exposes cleartext password on CLI arguments via -P" >&2
    FAILURES=$((FAILURES + 1))
fi

# 5. Check Reverse Proxy /metrics access
echo "--> Checking reverse proxy /metrics..."
if ! grep -A 2 "location /metrics" "${REPO_ROOT}/deploy/containers/reverse-proxy/nginx.conf" | grep -q "deny all"; then
    echo "ERROR: nginx.conf does not deny /metrics access" >&2
    FAILURES=$((FAILURES + 1))
fi

# 6. Reverse proxy must run non-root and the compose stack must drop capabilities (SR-D-08)
echo "--> Checking benchmark reverse proxy hardening..."
if ! grep -q "^USER nginx" "${REPO_ROOT}/deploy/containers/reverse-proxy/Containerfile"; then
    echo "ERROR: reverse-proxy Containerfile does not run as non-root (USER nginx)" >&2
    FAILURES=$((FAILURES + 1))
fi
if ! sed -n '/^  reverse-proxy:/,/^  load-generator:/p' "${REPO_ROOT}/deploy/podman-compose.yaml" | grep -q "cap_drop:"; then
    echo "ERROR: podman-compose.yaml reverse-proxy does not drop capabilities" >&2
    FAILURES=$((FAILURES + 1))
fi
if grep -Eq '\$\{[A-Z_]+:-\$\{GATEWAY_HMAC_SECRET' "${REPO_ROOT}/deploy/podman-compose.yaml"; then
    echo "ERROR: podman-compose.yaml falls back to the shared GATEWAY_HMAC_SECRET for other consumers" >&2
    FAILURES=$((FAILURES + 1))
fi

# 7. Helm: production render must fail without authenticated metrics scraping (SR-D-06)
if command -v helm >/dev/null 2>&1; then
    echo "--> Checking Helm ServiceMonitor authentication..."
    CHART="${REPO_ROOT}/deploy/helm/autheris"
    if helm template t "${CHART}" -f "${CHART}/values.production.yaml" --set image.digest=sha256:0 \
        --set monitoring.serviceMonitor.auth.type=none >/dev/null 2>&1; then
        echo "ERROR: production Helm render accepts an unauthenticated ServiceMonitor" >&2
        FAILURES=$((FAILURES + 1))
    fi
    if ! helm template t "${CHART}" -f "${CHART}/values.production.yaml" --set image.digest=sha256:0 2>/dev/null \
        | grep -q "authorization:"; then
        echo "ERROR: production Helm render has no authenticated ServiceMonitor" >&2
        FAILURES=$((FAILURES + 1))
    fi
else
    echo "--> helm not installed; skipping Helm render checks."
fi

if [ "$FAILURES" -gt 0 ]; then
    echo "❌ Container hardening verification FAILED with ${FAILURES} error(s)." >&2
    exit 1
fi

echo "✅ All container hardening security checks PASSED."

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

# 7. Helm: production render must fail without authenticated metrics scraping (SR-D-06) and without the required
#    authentication / trusted-proxy / tenant-column settings (SEC2-09).
if command -v helm >/dev/null 2>&1; then
    echo "--> Checking Helm production render guards..."
    CHART="${REPO_ROOT}/deploy/helm/autheris"
    BASE=(-f "${CHART}/values.production.yaml" --set image.digest=sha256:0)
    ENTRA=(--set gateway.authentication.entraId.enabled=true
           --set gateway.authentication.entraId.tenantId=tenant
           --set gateway.authentication.entraId.clientId=client
           --set gateway.authentication.entraId.audience=api://autheris)

    render() { helm template t "${CHART}" "$@" 2>&1; }
    expect_failure() {
        local label="$1"; shift
        if helm template t "${CHART}" "$@" >/dev/null 2>&1; then
            echo "ERROR: production Helm render must fail: ${label}" >&2
            FAILURES=$((FAILURES + 1))
        fi
    }

    # Positive render: a fully configured production release renders and carries the authenticated ServiceMonitor.
    if ! POSITIVE="$(render "${BASE[@]}" "${ENTRA[@]}")"; then
        echo "ERROR: fully configured production Helm render failed: ${POSITIVE}" >&2
        FAILURES=$((FAILURES + 1))
    else
        grep -q "authorization:" <<<"${POSITIVE}" || { echo "ERROR: production Helm render has no authenticated ServiceMonitor" >&2; FAILURES=$((FAILURES + 1)); }
        grep -q 'Gateway__DataSources__RequireTenantColumn: "true"' <<<"${POSITIVE}" || { echo "ERROR: production Helm render does not enable RequireTenantColumn" >&2; FAILURES=$((FAILURES + 1)); }
        grep -q 'Gateway__Authentication__EntraId__Audience: "api://autheris"' <<<"${POSITIVE}" || { echo "ERROR: production Helm render does not carry the EntraId audience" >&2; FAILURES=$((FAILURES + 1)); }
    fi

    expect_failure "unauthenticated ServiceMonitor" "${BASE[@]}" "${ENTRA[@]}" --set monitoring.serviceMonitor.auth.type=none
    expect_failure "no authentication provider" "${BASE[@]}"
    expect_failure "two authentication providers" "${BASE[@]}" "${ENTRA[@]}" \
        --set gateway.authentication.adfs.enabled=true --set gateway.authentication.adfs.authority=https://adfs.example/adfs \
        --set gateway.authentication.adfs.audience=api://autheris
    for field in tenantId clientId audience; do
        expect_failure "EntraId ${field} missing" "${BASE[@]}" "${ENTRA[@]}" --set "gateway.authentication.entraId.${field}="
    done
    expect_failure "ADFS authority missing" "${BASE[@]}" --set gateway.authentication.adfs.enabled=true \
        --set gateway.authentication.adfs.audience=api://autheris
    expect_failure "ADFS audience missing" "${BASE[@]}" --set gateway.authentication.adfs.enabled=true \
        --set gateway.authentication.adfs.authority=https://adfs.example/adfs
    expect_failure "forwardAuth without trustedProxies" "${BASE[@]}" --set gateway.authentication.forwardAuth.enabled=true
    expect_failure "RequireTenantColumn disabled without exemption" "${BASE[@]}" "${ENTRA[@]}" --set gateway.dataSources.requireTenantColumn=false

    if ! render "${BASE[@]}" "${ENTRA[@]}" --set gateway.dataSources.requireTenantColumn=false \
        --set gateway.dataSources.requireTenantColumnExemption=true >/dev/null; then
        echo "ERROR: explicit RequireTenantColumn exemption must render" >&2
        FAILURES=$((FAILURES + 1))
    fi
    if ! FWD="$(render "${BASE[@]}" --set gateway.authentication.forwardAuth.enabled=true \
        --set 'gateway.authentication.forwardAuth.trustedProxies={10.1.2.3}')"; then
        echo "ERROR: forwardAuth with trustedProxies must render: ${FWD}" >&2
        FAILURES=$((FAILURES + 1))
    elif ! grep -q 'ForwardAuth__TrustedProxies__0: "10.1.2.3"' <<<"${FWD}" || ! grep -q 'ForwardAuth__TrustedNetworks__0:' <<<"${FWD}"; then
        echo "ERROR: forwardAuth render does not carry trusted proxies and networks" >&2
        FAILURES=$((FAILURES + 1))
    fi
else
    echo "--> helm not installed; skipping Helm render checks (CI job helm-render runs them)."
fi

# 7b. No benchmark connection string may trust the SQL Server certificate blindly (SR-D-08)
echo "--> Checking TrustServerCertificate..."
if grep -rniE "TrustServerCertificate=True" "${REPO_ROOT}/deploy" --exclude="verify-hardening.sh" --exclude-dir=certs >/dev/null; then
    echo "ERROR: deploy/ contains TrustServerCertificate=True" >&2
    FAILURES=$((FAILURES + 1))
fi
if ! sed -n '/^  reverse-proxy:/,/^  load-generator:/p' "${REPO_ROOT}/deploy/podman-compose.yaml" | grep -q "read_only: true"; then
    echo "ERROR: podman-compose.yaml reverse-proxy does not use a read-only root filesystem" >&2
    FAILURES=$((FAILURES + 1))
fi

# 8. Every container base image is pinned to a full patch tag plus digest (SEC2-08)
echo "--> Checking base image pins..."
while IFS= read -r line; do
    file="${line%%:*}"
    from="${line#*:}"
    if ! grep -Eq '^FROM[[:space:]]+(--platform=[^[:space:]]+[[:space:]]+)?[^[:space:]@]+:[0-9]+\.[0-9]+\.[0-9]+[^@[:space:]]*@sha256:[0-9a-f]{64}' <<<"${from}"; then
        echo "ERROR: ${file#"${REPO_ROOT}"/}: base image is not pinned to <major>.<minor>.<patch>@sha256: (${from})" >&2
        FAILURES=$((FAILURES + 1))
    fi
done < <(grep -rEn --include='Dockerfile*' --include='Containerfile*' '^FROM[[:space:]]' "${REPO_ROOT}" \
    --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj --exclude-dir=node_modules | sed -E 's/^([^:]+):[0-9]+:/\1:/')

if [ "$FAILURES" -gt 0 ]; then
    echo "❌ Container hardening verification FAILED with ${FAILURES} error(s)." >&2
    exit 1
fi

echo "✅ All container hardening security checks PASSED."

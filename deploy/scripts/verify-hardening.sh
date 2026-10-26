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
    # A negative render must fail for the expected reason (error message pattern), not for an unrelated template error.
    expect_failure() {
        local label="$1" pattern="$2"; shift 2
        local out
        if out="$(helm template t "${CHART}" "$@" 2>&1)"; then
            echo "ERROR: production Helm render must fail: ${label}" >&2
            FAILURES=$((FAILURES + 1))
        elif ! grep -qE "${pattern}" <<<"${out}"; then
            echo "ERROR: production Helm render failed for the wrong reason (${label}): ${out}" >&2
            FAILURES=$((FAILURES + 1))
        fi
    }
    expect_success() {
        local label="$1"; shift
        local out
        if ! out="$(helm template t "${CHART}" "$@" 2>&1)"; then
            echo "ERROR: production Helm render must succeed (${label}): ${out}" >&2
            FAILURES=$((FAILURES + 1))
        fi
    }
    FWD=(--set gateway.authentication.forwardAuth.enabled=true
         --set 'gateway.authentication.forwardAuth.trustedProxies={10.1.2.3}'
         --set 'gateway.authentication.forwardAuth.trustedNetworks={10.1.2.0/24}'
         --set gateway.authentication.forwardAuth.sharedSecret.existingSecret=fwd-secret)

    # Positive render: a fully configured production release renders and carries the authenticated ServiceMonitor.
    if ! POSITIVE="$(render "${BASE[@]}" "${ENTRA[@]}")"; then
        echo "ERROR: fully configured production Helm render failed: ${POSITIVE}" >&2
        FAILURES=$((FAILURES + 1))
    else
        grep -q "authorization:" <<<"${POSITIVE}" || { echo "ERROR: production Helm render has no authenticated ServiceMonitor" >&2; FAILURES=$((FAILURES + 1)); }
        grep -q 'Gateway__DataSources__RequireTenantColumn: "true"' <<<"${POSITIVE}" || { echo "ERROR: production Helm render does not enable RequireTenantColumn" >&2; FAILURES=$((FAILURES + 1)); }
        grep -q 'Gateway__Authentication__EntraId__Audience: "api://autheris"' <<<"${POSITIVE}" || { echo "ERROR: production Helm render does not carry the EntraId audience" >&2; FAILURES=$((FAILURES + 1)); }
    fi

    expect_failure "unauthenticated ServiceMonitor" "must be bearer or mtls" "${BASE[@]}" "${ENTRA[@]}" --set monitoring.serviceMonitor.auth.type=none
    expect_failure "no authentication provider" "exactly one of" "${BASE[@]}"
    expect_failure "two authentication providers" "exactly one of" "${BASE[@]}" "${ENTRA[@]}" \
        --set gateway.authentication.adfs.enabled=true --set gateway.authentication.adfs.authority=https://adfs.example/adfs \
        --set gateway.authentication.adfs.audience=api://autheris
    for field in tenantId clientId audience; do
        expect_failure "EntraId ${field} missing" "entraId.${field} is required" "${BASE[@]}" "${ENTRA[@]}" --set "gateway.authentication.entraId.${field}="
    done
    expect_failure "ADFS authority missing" "adfs.authority is required" "${BASE[@]}" --set gateway.authentication.adfs.enabled=true \
        --set gateway.authentication.adfs.audience=api://autheris
    expect_failure "ADFS audience missing" "adfs.audience is required" "${BASE[@]}" --set gateway.authentication.adfs.enabled=true \
        --set gateway.authentication.adfs.authority=https://adfs.example/adfs
    expect_failure "forwardAuth without trustedProxies" "trustedProxies must list" "${BASE[@]}" --set gateway.authentication.forwardAuth.enabled=true
    expect_failure "forwardAuth with empty trustedNetworks" "trustedNetworks must be set explicitly" "${BASE[@]}" "${FWD[@]}" \
        --set 'gateway.authentication.forwardAuth.trustedNetworks=null'
    expect_failure "forwardAuth without shared secret" "sharedSecret.existingSecret" "${BASE[@]}" "${FWD[@]}" \
        --set gateway.authentication.forwardAuth.sharedSecret.existingSecret=
    expect_failure "RequireTenantColumn disabled without exemption" "requireTenantColumn must be true" "${BASE[@]}" "${ENTRA[@]}" \
        --set gateway.dataSources.requireTenantColumn=false

    expect_success "explicit RequireTenantColumn exemption" "${BASE[@]}" "${ENTRA[@]}" --set gateway.dataSources.requireTenantColumn=false \
        --set gateway.dataSources.requireTenantColumnExemption=true
    expect_success "kerberos only" "${BASE[@]}" --set gateway.authentication.kerberos.enabled=true
    expect_success "adfs only" "${BASE[@]}" --set gateway.authentication.adfs.enabled=true \
        --set gateway.authentication.adfs.authority=https://adfs.example/adfs --set gateway.authentication.adfs.audience=api://autheris
    if ! FWDOUT="$(render "${BASE[@]}" "${FWD[@]}")"; then
        echo "ERROR: forwardAuth with trusted proxies, networks and shared secret must render: ${FWDOUT}" >&2
        FAILURES=$((FAILURES + 1))
    elif ! grep -q 'ForwardAuth__TrustedProxies__0: "10.1.2.3"' <<<"${FWDOUT}" \
        || ! grep -q 'ForwardAuth__TrustedNetworks__0: "10.1.2.0/24"' <<<"${FWDOUT}" \
        || ! grep -q 'Gateway__Authentication__ForwardAuth__SharedSecret' <<<"${FWDOUT}"; then
        echo "ERROR: forwardAuth render does not carry trusted proxies, networks and the shared secret reference" >&2
        FAILURES=$((FAILURES + 1))
    fi

    # Admin API exposure (issue #281, SEC-281-6): the default ingress must deny /api/admin; exposeAdmin=true must remove the rule.
    if ! DEFOUT="$(render)"; then
        echo "ERROR: default Helm render failed: ${DEFOUT}" >&2
        FAILURES=$((FAILURES + 1))
    elif ! grep -q 'path: /api/admin' <<<"${DEFOUT}" || ! grep -q -- '-admin-blocked' <<<"${DEFOUT}"; then
        echo "ERROR: default Helm ingress exposes /api/admin (no deny rule rendered)" >&2
        FAILURES=$((FAILURES + 1))
    elif grep -qi 'snippet' <<<"${DEFOUT}"; then
        echo "ERROR: default Helm render contains a snippet annotation" >&2
        FAILURES=$((FAILURES + 1))
    fi
    if ! OPTOUT="$(render --set ingress.exposeAdmin=true)"; then
        echo "ERROR: Helm render with ingress.exposeAdmin=true failed: ${OPTOUT}" >&2
        FAILURES=$((FAILURES + 1))
    elif grep -q 'path: /api/admin' <<<"${OPTOUT}"; then
        echo "ERROR: ingress.exposeAdmin=true still renders the /api/admin rule" >&2
        FAILURES=$((FAILURES + 1))
    fi
    # A non-boolean exposeAdmin must keep the deny rule (fail closed).
    if ! STROUT="$(render --set-string ingress.exposeAdmin=true)" || ! grep -q 'path: /api/admin' <<<"${STROUT}"; then
        echo "ERROR: string ingress.exposeAdmin=true must keep the /api/admin deny rule" >&2
        FAILURES=$((FAILURES + 1))
    fi
    # nginx: use-regex makes the deny case-insensitive; no snippet annotation may ever be rendered.
    if ! NGXOUT="$(render --set ingress.className=nginx)"; then
        echo "ERROR: nginx Helm render failed: ${NGXOUT}" >&2
        FAILURES=$((FAILURES + 1))
    elif ! grep -q 'nginx.ingress.kubernetes.io/use-regex: "true"' <<<"${NGXOUT}" || ! grep -q 'path: /api/admin' <<<"${NGXOUT}"; then
        echo "ERROR: nginx Helm render lacks use-regex or the /api/admin deny rule" >&2
        FAILURES=$((FAILURES + 1))
    elif grep -qi 'snippet' <<<"${NGXOUT}"; then
        echo "ERROR: nginx Helm render contains a snippet annotation" >&2
        FAILURES=$((FAILURES + 1))
    fi
    if ! NGXOPT="$(render --set ingress.className=nginx --set ingress.nginxCaseInsensitiveAdminDeny=false)" \
        || ! grep -q 'path: /api/admin' <<<"${NGXOPT}" || grep -q 'use-regex' <<<"${NGXOPT}"; then
        echo "ERROR: nginx opt-out render must keep the deny rule and omit use-regex" >&2
        FAILURES=$((FAILURES + 1))
    fi
    if ! NGXUSR="$(render --set ingress.className=nginx --set-string 'ingress.annotations.nginx\.ingress\.kubernetes\.io/use-regex=false')" \
        || ! grep -q 'use-regex: "true"' <<<"${NGXUSR}" || grep -q 'use-regex: "false"' <<<"${NGXUSR}"; then
        echo "ERROR: a user annotation must not switch off use-regex while the deny rule relies on it" >&2
        FAILURES=$((FAILURES + 1))
    fi
    if ! EXPNGX="$(render --set ingress.className=nginx --set ingress.exposeAdmin=true)" || grep -qi 'snippet' <<<"${EXPNGX}"; then
        echo "ERROR: nginx render with exposeAdmin=true failed or contains a snippet annotation" >&2
        FAILURES=$((FAILURES + 1))
    fi

    # NF-HA-01 wiring (WAVE3-STARTUP-SC-03/04): the four Gateway__HighAvailability__* env vars come from values, and the render fails
    # when an inequality is violated or a value is unset, non-numeric or not positive.
    for ha_out in "${DEFOUT}" "${POSITIVE}"; do
        for ha_key in DrainDelaySeconds ShutdownTimeoutSeconds QueryTimeoutSeconds TerminationGracePeriodSeconds; do
            grep -q "name: Gateway__HighAvailability__${ha_key}\$" <<<"${ha_out}" || { echo "ERROR: Helm render is missing Gateway__HighAvailability__${ha_key}" >&2; FAILURES=$((FAILURES + 1)); }
        done
    done
    expect_failure "grace period too short" "NF-HA-01b" "${BASE[@]}" "${ENTRA[@]}" --set terminationGracePeriodSeconds=30
    expect_failure "shutdown below query + 10" "NF-HA-01a" "${BASE[@]}" "${ENTRA[@]}" --set gateway.queryTimeoutSeconds=40
    expect_failure "non-positive drain" "gateway.drainDelaySeconds must be a positive" "${BASE[@]}" "${ENTRA[@]}" --set gateway.drainDelaySeconds=0
    expect_failure "drain above application range" "gateway.drainDelaySeconds must be between 1 and 30" "${BASE[@]}" "${ENTRA[@]}" --set gateway.drainDelaySeconds=31
    expect_failure "query timeout above application range" "gateway.queryTimeoutSeconds must be between 5 and 120" "${BASE[@]}" "${ENTRA[@]}" --set gateway.queryTimeoutSeconds=121
    expect_failure "shutdown above application range" "gateway.shutdownTimeoutSeconds must be between 10 and 180" "${BASE[@]}" "${ENTRA[@]}" --set gateway.shutdownTimeoutSeconds=181
    expect_failure "grace above application range" "terminationGracePeriodSeconds must be between 20 and 300" "${BASE[@]}" "${ENTRA[@]}" --set terminationGracePeriodSeconds=301
    expect_failure "non-numeric shutdown" "gateway.shutdownTimeoutSeconds must be a positive" "${BASE[@]}" "${ENTRA[@]}" --set-string gateway.shutdownTimeoutSeconds=abc
    expect_failure "unset query timeout" "gateway.queryTimeoutSeconds must be a positive" "${BASE[@]}" "${ENTRA[@]}" --set gateway.queryTimeoutSeconds=null
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

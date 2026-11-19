# ==============================================================================
# Autheris - Getting Started Container
# Multi-protocol Enterprise GraphQL Gateway
# ==============================================================================

# Image flavor: `core` ships no modules (no /app/modules); `full` additionally ships the signed modules from dist/modules after
# an offline signature verification against the release public key embedded in the host. Any other value fails the build.
ARG FLAVOR=core

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242 AS dotnet-base

# core: nothing but a flavor marker is added to /app.
FROM dotnet-base AS modules-core
RUN mkdir -p /payload && echo core > /payload/.image-flavor

# full: every module must verify against the pinned release public key or the build fails.
# The trust root is the same file that is embedded into Autheris.Api as the Keys/module-release-*.pem resource; it is never taken
# from a build argument, environment variable or secret.
FROM dotnet-base AS modules-full
COPY dist/tools/module-signer/ /tools/module-signer/
COPY src/Autheris.Api/Hosting/Keys/module-release-pub.pem /tools/module-release-pub.pem
COPY dist/modules/ /payload/modules/
RUN set -eu; \
    count=0; \
    for module_dir in /payload/modules/*/; do \
        [ -d "$module_dir" ] || continue; \
        dotnet /tools/module-signer/Autheris.ModuleSigner.dll verify --pub /tools/module-release-pub.pem --dir "$module_dir" || exit 1; \
        count=$((count + 1)); \
    done; \
    [ "$count" -gt 0 ] || { echo "Error: FLAVOR=full requires at least one signed module in dist/modules" >&2; exit 1; }; \
    echo full > /payload/.image-flavor; \
    find /payload/modules -type d -exec chmod 0555 {} + && find /payload/modules -type f -exec chmod 0444 {} +

# An unknown flavor has no matching modules-<flavor> stage and fails the build here.
FROM modules-${FLAVOR} AS modules-selected
ARG FLAVOR
RUN case "$FLAVOR" in core|full) ;; *) echo "Error: FLAVOR must be core or full" >&2; exit 1;; esac

FROM dotnet-base AS runtime
WORKDIR /app

# Default configuration (8080 HTTP)
# SEC C-04: The published image runs in PRODUCTION by default (all security controls active).
# Development mode inside the container requires an explicit opt-in (AUTHERIS_ALLOW_DEV_IN_CONTAINER=true);
# see docker-compose.dev.yml for local getting-started usage.
# DEP-4: No unconfigured HTTPS (8081) in the container default, because Kestrel fails on startup
# without a mounted certificate. TLS termination happens at the ingress / reverse proxy.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 \
    DOTNET_System_Net_Security_UseManagedNtlm=true \
    Gateway__State__StateDirectory=/app/data \
    Gateway__GovernanceDb__ConnectionString="Data Source=/app/data/governance.db"

# Official .NET container ports (HTTP)
EXPOSE 8080

# Create data directory for persistent SQLite governance DB & cache and transfer ownership to non-root user
# DEP-4: Prevents SQLite Error 14 ('unable to open database file'), since /app is owned by root
# Issue #102: Install GSSAPI, gss-ntlmssp, and Kerberos runtime packages for SQL Server Windows Authentication (NTLM/Kerberos)
ENV APP_UID=10001
RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app/data
RUN mkdir -p /app/scratch && chown -R $APP_UID:$APP_UID /app/scratch
RUN apt-get update \
    && apt-get --only-upgrade install -y libpcre2-8-0 apt libapt-pkg7.0 libc6 libc-bin perl-base \
    && apt-get install -y --no-install-recommends \
        libgssapi-krb5-2 \
        gss-ntlmssp \
        krb5-user \
        libkrb5-3 \
        zlib1g \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /etc/gss/mech.d \
    && if [ -f /etc/gss/mech.d/mech.ntlmssp.conf ]; then \
        chmod 0644 /etc/gss/mech.d/mech.ntlmssp.conf \
        && cp -f /etc/gss/mech.d/mech.ntlmssp.conf /etc/gss/mech \
        && chmod 0644 /etc/gss/mech; \
    fi \
    && mkdir -p /app/data/gss \
    && chown -R $APP_UID:$APP_UID /app/data/gss \
    && chmod 0700 /app/data/gss
VOLUME /app/data
VOLUME /app/scratch

# Copy the artifacts published by the CI/CD runner
COPY dist/publish/ ./
# Root-owned payload of the selected flavor (marker, plus /app/modules with 0555/0444 permissions for full).
COPY --from=modules-selected --chown=0:0 /payload/ /app/

# Non-root user for container hardening (UID:GID 10001:10001)
USER 10001:10001

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD dotnet Autheris.Api.dll --healthcheck || exit 1

ENTRYPOINT ["dotnet", "Autheris.Api.dll"]

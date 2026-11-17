# ==============================================================================
# Autheris - Getting Started Container
# Multi-protocol Enterprise GraphQL Gateway
# ==============================================================================

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS runtime
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
RUN mkdir -p /app/shared-docs /app/scratch && chown -R $APP_UID:$APP_UID /app/shared-docs /app/scratch
RUN apt-get update \
    && apt-get --only-upgrade install -y libpcre2-8-0 apt libapt-pkg6.0t64 \
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
VOLUME /app/shared-docs
VOLUME /app/scratch

# Copy the artifacts published by the CI/CD runner
COPY dist/publish/ ./
COPY dist/modules/ /app/modules/
# Production refuses module directories with writable permission bits (0555/0444 expected); the build context carries 0755/0644.
RUN find /app/modules -type d -exec chmod 0555 {} + && find /app/modules -type f -exec chmod 0444 {} +

# Non-root user for container hardening (UID:GID 10001:10001)
USER 10001:10001

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD dotnet Autheris.Api.dll --healthcheck || exit 1

ENTRYPOINT ["dotnet", "Autheris.Api.dll"]

# ==============================================================================
# Autheris - Getting Started Container
# Multi-protocol Enterprise GraphQL Gateway with Embedded Microsoft Garnet Cache
# ==============================================================================

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS runtime
WORKDIR /app

# Standard-Konfiguration (8080 HTTP)
# SEC C-04: Das veröffentlichte Image läuft standardmäßig in PRODUCTION (alle Schutzmechanismen aktiv).
# Development im Container ist nur mit explizitem Opt-in möglich (AUTHERIS_ALLOW_DEV_IN_CONTAINER=true),
# siehe docker-compose.dev.yml für den lokalen Getting-Started-Betrieb.
# DEP-4: Kein unkonfiguriertes HTTPS (8081) im Container-Default, da ohne gemountetes Zertifikat Kestrel
# beim Start abbricht. TLS-Terminierung erfolgt am Ingress / Reverse Proxy.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 \
    DOTNET_System_Net_Security_UseManagedNtlm=true \
    Gateway__Caching__Garnet__EnableEmbeddedServer=true \
    Gateway__Caching__Garnet__Host=127.0.0.1 \
    Gateway__Caching__Garnet__Port=3278 \
    Gateway__State__StateDirectory=/app/data \
    Gateway__GovernanceDb__ConnectionString="Data Source=/app/data/governance.db"

# Offizielle .NET Container Ports (HTTP)
EXPOSE 8080

# Datenverzeichnis für persistente SQLite-Governance-DB & Cache anlegen und an Non-Root User übergeben
# DEP-4: Verhindert SQLite Error 14 ('unable to open database file'), da /app root gehört
# Issue #102: Install GSSAPI, gss-ntlmssp, and Kerberos runtime packages for SQL Server Windows Authentication (NTLM/Kerberos)
ENV APP_UID=10001
RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app/data
RUN mkdir -p /app/shared-docs /app/scratch && chown -R $APP_UID:$APP_UID /app/shared-docs /app/scratch
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libgssapi-krb5-2 \
        gss-ntlmssp \
        krb5-user \
        libkrb5-3 \
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

# Kopiere die vom CI/CD-Runner publizierten Artefakte
COPY dist/publish/ ./

# Non-Root User für Container-Härtung (UID:GID 10001:10001)
USER 10001:10001

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD dotnet Autheris.Api.dll --healthcheck || exit 1

ENTRYPOINT ["dotnet", "Autheris.Api.dll"]

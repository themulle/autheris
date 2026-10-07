# ==============================================================================
# Autheris - Getting Started Container
# Multi-protocol Enterprise GraphQL Gateway with Embedded Microsoft Garnet Cache
# ==============================================================================

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
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
    Gateway__Caching__Garnet__EnableEmbeddedServer=true \
    Gateway__Caching__Garnet__Host=127.0.0.1 \
    Gateway__Caching__Garnet__Port=3278 \
    Gateway__GovernanceDb__ConnectionString="Data Source=/app/data/governance.db;Cache=Shared"

# Offizielle .NET Container Ports (HTTP)
EXPOSE 8080

# Datenverzeichnis für persistente SQLite-Governance-DB & Cache anlegen und an Non-Root User übergeben
# DEP-4: Verhindert SQLite Error 14 ('unable to open database file'), da /app root gehört
RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app/data
VOLUME /app/data

# Kopiere die vom CI/CD-Runner publizierten Artefakte
COPY dist/publish/ ./

# Non-Root User für Container-Härtung
USER $APP_UID

ENTRYPOINT ["dotnet", "Autheris.Api.dll"]

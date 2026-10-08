# Architektonischer Umsetzungsplan: Bereich 1 – API- & Protokoll-Härtung (MCP OAuth Discovery & WebSQL Content Negotiation)

**Dokument-ID:** `PLAN-BEREICH-1-API-PROTOKOLL-HAERTUNG-2026-10-08`  
**Datum:** 2026-10-08  
**Rolle:** Solution Architect (`csharp-architect`)  
**Status:** Genehmigungsreif / Bereit für TDD-Umsetzung  
**Bezug:** [`docs/plans/2026-10-08-websql-befunde-v1-1-0.md`](2026-10-08-websql-befunde-v1-1-0.md) (Befund 1.2 und 3.2).

---

## 1. Architektonische Leitplanken & Anti-Overengineering (nach `csharp-architect`)

1. **YAGNI & KISS (Keine unnötigen Abstraktionen):**
   - Keine Einführung neuer Middleware-Klassen oder Framework-Pipelines für zwei triviale RFC-Endpunkte.
   - MCP OAuth Discovery baut direkt auf den bereits vorhandenen Daten aus `GatewayMcpOAuth.AuthorizationServers(options)` auf.
   - WebSQL Content Negotiation nutzt die bewährte `ParquetContentNegotiation`-Komponente, die bereits für OData erfolgreich im Einsatz ist.
2. **Fail-Closed & Standard-Konformität (RFC 9728 / RFC 8414 / RFC 9110):**
   - Discovery-Endpunkte müssen anonym aufrufbar sein (`.AllowAnonymous()`), da standardkonforme OAuth-Clients (wie AI-Agenten oder API-Gateways) Discovery-Metadaten zwingend vor Erhalt eines Access-Tokens abrufen.
   - HTTP 406 (Not Acceptable) bei inkompatiblen `Accept`-Headern schützt Clients vor stillschweigendem Typ- und Formatverlust (JSON statt CSV/NDJSON).
3. **I/O- und DI-Konsistenz:**
   - Registrierung schlank als Minimal-APIs in `McpEndpoints` bzw. `WebSqlEndpoints`.
   - Reine Leseoperationen ohne Nebeneffekte und ohne Allokationen im Request-Hotpath.

---

## 2. Problemstellung & Soll-Zustand

### 2.1 MCP OAuth Discovery (Befund 3.2)
- **Ist-Zustand:**
  - Das MCP C# SDK registriert geschützte Ressourcen-Metadaten nur unter dem systemspezifischen Pfad `/.well-known/oauth-protected-resource/mcp`.
  - Standard-OAuth-Clients fragen jedoch häufig die Root-Pfade nach RFC 9728 bzw. RFC 8414 ab:
    - `GET /.well-known/oauth-protected-resource`
    - `GET /.well-known/oauth-authorization-server`
  - Wegen der globalen `FallbackPolicy = RequireAuthenticatedUser` antworten diese Root-Pfade mit HTTP 401 Unauthorized.
- **Soll-Zustand:**
  - `GET /.well-known/oauth-protected-resource` antwortet anonym (`200 OK`) mit den Metadaten der Standard-MCP-Ressource (`/mcp`) oder leitet mit `307 Temporary Redirect` dorthin weiter.
  - `GET /.well-known/oauth-authorization-server` antwortet anonym (`200 OK`) mit der Liste der konfigurierten Autorisierungsserver (`{"authorization_servers": [...]}`). Wenn keine OAuth-Server konfiguriert sind (z. B. nur Basic/Kerberos), antwortet der Endpunkt mit HTTP 404 Not Found.

### 2.2 WebSQL Strikte Content Negotiation (Befund 1.2)
- **Ist-Zustand:**
  - `POST /api/v1/sql` wertet `Accept: application/vnd.apache.parquet` und `?format=parquet` aus.
  - Sendet ein Client jedoch einen inkompatiblen `Accept`-Header wie `Accept: text/csv` oder `application/x-ndjson`, ignoriert WebSQL diesen und liefert stillschweigend JSON (HTTP 200).
- **Soll-Zustand:**
  - Wie in `ODataEndpoints`: Wenn ein expliziter `Accept`-Header übergeben wird, der weder Parquet noch JSON oder Wildcard (`*/*`) akzeptiert, antwortet der Endpunkt sofort mit HTTP 406 Not Acceptable.

---

## 3. Entwurf der Änderungen

### 3.1 `McpEndpoints.cs`
Erweiterung in `MapMcpEndpoints`:
```csharp
if (GatewayMcpOAuth.IsEnabled(gatewayOptions))
{
    // RFC 9728: Root Protected Resource Metadata Fallback -> Verweist auf /mcp
    app.MapGet("/.well-known/oauth-protected-resource", (HttpContext context) =>
    {
        var target = $"{context.Request.PathBase}/.well-known/oauth-protected-resource{mcpBasePath}";
        return Results.Redirect(target, permanent: false);
    }).AllowAnonymous();

    // RFC 8414: Authorization Server Discovery
    app.MapGet("/.well-known/oauth-authorization-server", () =>
    {
        var servers = GatewayMcpOAuth.AuthorizationServers(gatewayOptions);
        return Results.Ok(new
        {
            authorization_servers = servers,
            issuer = servers.FirstOrDefault()
        });
    }).AllowAnonymous();
}
```

### 3.2 `WebSqlEndpoints.cs`
Erweiterung vor der Streaming-Ausführung in `HandleWebSqlRequest`:
```csharp
// Befund 1.2: Strict Content Negotiation.
if (httpContext.Request.Headers.Accept.Count > 0)
{
    var acceptEval = ParquetContentNegotiation.Evaluate(httpContext.Request);
    if (!acceptEval.ParquetPreferred && !acceptEval.HasJsonAlternative && !isParquet)
    {
        httpContext.Response.StatusCode = StatusCodes.Status406NotAcceptable;
        return;
    }
}
```

---

## 4. TDD-Testplan

1. `McpSdkTransportTests.cs`:
   - `WellKnown_ProtectedResource_Root_RedirectsOrServesMetadataAnonymously()`:
     Aufruf `GET /.well-known/oauth-protected-resource` ohne Authorization-Header liefert Redirect (307) auf `/mcp` oder 200 OK mit `authorization_servers`.
   - `WellKnown_AuthorizationServer_ReturnsConfiguredServersAnonymously()`:
     Aufruf `GET /.well-known/oauth-authorization-server` ohne Auth liefert HTTP 200 OK und JSON mit `authorization_servers`.
2. `GovernedWebSqlIntegrationTests.cs` / `ParquetExportDateAndNegotiationTests.cs`:
   - `WebSql_UnsupportedAcceptHeader_Returns406NotAcceptable()`:
     Aufruf `POST /api/v1/sql` mit `Accept: text/csv` liefert HTTP 406.
   - `WebSql_FormatParquetQueryParam_DeliversParquet()`:
     Aufruf `POST /api/v1/sql?format=parquet` liefert Parquet-Binärdaten.

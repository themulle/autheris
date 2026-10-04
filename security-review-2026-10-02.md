# Security Review – Autheris (Whitebox, Stand 2026-10-02)

**Scope:** `gql/src` (5 Produktionsprojekte: Api, GraphQL, Application, Infrastructure, Domain; ca. 42k LOC), `gql_extensions/src` (ca. 5k LOC), `gql_sqlparser` (ca. 1,6k LOC, ohne Tests/Fixtures). Nicht im Scope: Tests, Benchmarks, Docs, `gql_integrationtest`, `sling-cli`.
**Methode:** Statische Code-Analyse, Schicht für Schicht von außen (Deployment, Hosting) nach innen (Domain, SQL-Parser). Datenflüsse wurden bis zur Senke verfolgt. Es gab **keine Laufzeit-PoCs**. Wo ein Befund von Konfiguration, Fremdbibliotheken (HotChocolate, Casbin.NET, Trino) oder Betriebsumgebung abhängt, ist das beim Befund vermerkt.
**Verhältnis zur bestehenden `security-review.md` (2026-09-28, "alle Befunde behoben"):** Mehrere dort als behoben gemeldete Punkte sind nur teilweise wirksam (siehe Abschnitt 12).

---

## 1. Management Summary

| Schweregrad | Anzahl |
|---|---|
| 🔴 Kritisch | 6 |
| 🟠 Hoch | 17 |
| 🟡 Mittel | 27 |
| 🟢 Niedrig / Info | 20+ |

**Kernaussage:** Die Grundhygiene ist gut. Repository-SQL ist durchgehend parametrisiert, Identifier werden per Allowlist gequotet, es gibt keine unsichere Deserialisierung, HMAC-Vergleiche sind timing-safe, die SSRF-Abwehr nutzt IP-Pinning, und die Prod-Guards für `danger_*`-Flags greifen. Die **eigentliche Sicherheitsgrenze des Produkts, die Durchsetzung von RLS, Masking und Consent, lässt sich aber auf mehreren unabhängigen Wegen umgehen.** Am schwersten wiegt der WebSQL-Pfad (`/api/sql`, standardmäßig aktiv), danach Streaming, MCP und die Vier-Augen-Freigabe.

**Top 6 (sofort beheben):**

1. **WebSQL-Funktionsbypass:** `SELECT query_to_xml('SELECT * FROM hr.salaries',…)` liefert ganze Tabellen ohne RLS, Masking oder ABAC (C-01).
2. **CTE-Name mit Punkt:** `WITH "sales.orders" AS (…) SELECT * FROM sales.orders` umgeht RLS, ABAC und Masking (C-02).
3. **WebSQL ignoriert Consents.** Bei aktivem Casbin werden außerdem sensible Spalten im Klartext geliefert (C-03).
4. **Container-Image läuft standardmäßig als `Development`.** Damit sind alle Prod-Guards aus, und Secrets sind gleich ihrem Referenznamen, also öffentlich bekannt (C-04).
5. **Vier-Augen-Prinzip (HitL) umgehbar:** Jeder angemeldete Benutzer sieht und genehmigt Tickets aller Tenants, Selbstfreigabe eingeschlossen (C-05).
6. **Stack-Overflow im SQL-Parser:** Tiefe Klammerung bringt den gesamten Prozess zum Absturz, eine Anfrage genügt (C-06).

---

## 2. Schicht 1 – Deployment, Container, Hosting

### C-04 🔴 Container und Compose setzen `ASPNETCORE_ENVIRONMENT=Development`
`Dockerfile:12`, `docker-compose.yml:9`
```dockerfile
ENV ASPNETCORE_ENVIRONMENT=Development
```
Das veröffentlichte Image (`ghcr.io/themulle/gql`, über `docker-publish.yml`) läuft ohne Override in Development. Damit fallen praktisch alle Schutzmechanismen weg:

- `ValidateGatewayOptions` lässt Quickstart-Profil, AnonymousAccess, TestAuth, `danger_*`-Flags, `TrustedOrigins:*` und ungültige Zertifikate zu.
- `DefaultEnvironmentSecretProvider.cs:102` liefert **den Referenznamen als Secret**. Audit-HMAC-Key, ITSM-Webhook-Secret (`"itsm:webhook-secret"`) und Masking-Key sind damit öffentlich bekannt. Webhooks lassen sich fälschen, die Audit-Kette neu signieren.
- Basic-Auth akzeptiert Klartext-Passwörter. ForwardAuth funktioniert ohne Trusted Proxy und ohne Shared Secret, `X-Forwarded-User`/`-Roles`/`-Tenant` sind dann frei setzbar.
- Fehlt eine Datenquelle, wird synthetisch gefallbackt. Ausführliche Fehler, DevPortal und Health-Details sind sichtbar, HSTS und HTTPS-Redirect aus.

**Fix:** Image-Default `Production`, Dev nur über Compose-Override. Beim Start zusätzlich abbrechen, wenn `Development` und gleichzeitig `DOTNET_RUNNING_IN_CONTAINER=true` gesetzt sind und kein explizites Opt-in vorliegt.

### H-01 🟠 Eingebetteter Garnet bzw. Redis ohne Authentifizierung, L2-Consent-Cache ohne Integritätsschutz
`Infrastructure/Garnet/GarnetServerManager.cs:56-60`, `Cache/ConsentCacheService.cs:107-118`, `Cache/EpochValidationService.cs:53-59`
Zugriffsentscheidungen (`IsAllowed`, `RowFilterSql`, `ColumnAccess`) werden ungeprüft aus Redis bzw. Garnet übernommen. Die Epoche, gegen die validiert wird, liegt ebenfalls dort. Garnet startet ohne `--auth` und ohne TLS, der Redis-Default ist `localhost:6379` ohne Passwort.
**Angriff:** Ein Pod oder Prozess mit Netzzugang schreibt einen Envelope `IsAllowed=true, RowFilterSql=null`. Damit ist der Consent für beliebige SIDs ausgehebelt.
**Fix:** Cache-Einträge per HMAC über Key und Payload signieren (eigener HKDF-Key). Garnet und Redis mit ACL/Passwort und TLS betreiben, außerhalb von Dev erzwingen.

### M-01 🟡 Kestrel: globales Body-Limit 100 MB, keine Verbindungslimits
`Api/Program.cs:13-16`. Der Wert ist `MaxRequestBodySize = 100 MB`, `MaxConcurrentConnections`/`MaxConcurrentUpgradedConnections = null`. Das verstärkt M-07 (chunked bypass) und H-07 (Dauerverbindungen).
**Fix:** Global 1–2 MB, Ausnahmen pro Endpoint (dbt-sync). Upgrade-Verbindungen begrenzen.

---

## 3. Schicht 2 – Authentifizierung

### H-02 🟠 `OpenSchema` schaltet Auth für GraphQL und MCP sowie Introspection ab und ist in Prod erlaubt
`Domain/Options/GatewayOptions.cs:58`, `Api/Extensions/GatewayApplicationBuilderExtensions.cs:256`, `GatewayServiceCollectionExtensions.cs:847`, `Api/Endpoints/McpEndpoints.cs:31,112-149`
```csharp
if (!gatewayOptions.IsAnonymousAccessAllowed && !gatewayOptions.IsOpenSchemaAllowed)
    gqlEndpoint.RequireAuthorization();
var allowOpenMcp = gatewayOptions.IsMcpAuthBypassed || gatewayOptions.IsOpenSchemaAllowed;
```
Dokumentiert ist der Schalter als "Katalog und OpenAPI öffentlich". Tatsächlich hat er folgende Wirkung:

- `/graphql` und alle MCP-Routen inklusive `tools/call` sind ohne Login erreichbar.
- Introspection ist an.
- Die Prüfung, ob eine MCP-Session dem Aufrufer gehört, entfällt.

Der Schalter fehlt in `GetAllActiveBypasses()`, wird in Prod also **nicht** blockiert. Anonyme Aufrufer wählen ihren Tenant per `X-Tenant-ID` (`TenantResolutionMiddleware.cs:84`).
**Fix:** OpenSchema nur auf Doku- und OpenAPI-Routen wirken lassen. Zusätzlich als `DANGER` in die Bypass-Liste aufnehmen. Die Besitzprüfung der MCP-Session immer ausführen.

### M-02 🟡 JWT: Audience-Prüfung entfällt, wenn weder `Audience` noch `ClientId` konfiguriert sind
`GatewayServiceCollectionExtensions.cs:691-700`. Der Wert `ValidateAudience = validAudiences.Count > 0` macht eine fehlende Konfiguration stillschweigend zu "jede Audience des Tenants". Tokens für andere Apps desselben Entra-Tenants werden dann akzeptiert. Bei Entra ohne `TenantId` entfällt auch die Issuer-Prüfung.
**Fix:** Fehlt Audience oder Issuer bei aktivem JWT, den Start verweigern (fail-closed).

### M-03 🟡 Keine FallbackPolicy, keine benannten Policies
`AddAuthorization()` ist ohne Policies konfiguriert, `RequireAuthorization()` bedeutet überall nur "angemeldet". Jede Rollen- und Tenant-Prüfung steht von Hand im Handler. Das ist die Ursache von C-05, H-04, H-05 und M-12.
**Fix:** `FallbackPolicy = RequireAuthenticatedUser` setzen, benannte Policies definieren (`GovernanceAdmin`, `Approver`, `ClusterAdmin` …) und deklarativ an die Endpoints hängen.

**Info:** ForwardAuth übernimmt Rollen, Gruppen und Tenant aus Headern (`ForwardAuthAuthenticationHandler.cs:186-240`). Das ist nur sicher, wenn der Proxy diese Header von Clients zuverlässig entfernt. Das sollte im Betriebshandbuch als harte Anforderung stehen. Trusted-Proxy-Check und Shared Secret (SHA-256 + FixedTimeEquals) sind korrekt umgesetzt.

---

## 4. Schicht 3 – Middleware-Pipeline

### H-07 🟠 Dauerverbindungen (WebSocket, SSE) halten Ressourcengruppen-Slots unbegrenzt und blockieren den Gateway
`Api/Middleware/ResourceGroupMiddleware.cs:60,112-114`, `Application/ResourceGroups/ResourceGroupManager.cs:118-138`
Die Slots gelten global pro Tier (Interactive 50, AutonomousAgents 10) und bleiben bis zum Ende der Anfrage belegt. `/mcp/sse` und GraphQL-WebSockets enden nie von selbst.
**Angriff:** 10 SSE-Verbindungen eines Benutzers legen MCP für alle Tenants lahm.
**Fix:** Upgrade- und SSE-Anfragen von den Slots ausnehmen, eigene Verbindungslimits pro SID und Tenant einführen, Slots pro Tenant aufteilen.

### M-04 🟡 `GatewayExtensibilityMiddleware` puffert komplette Antworten
`Middleware/GatewayExtensibilityMiddleware.cs:100-122`. Die 16-MB-Grenze wird erst nach dem vollständigen Puffern geprüft. Streaming-Antworten (SSE-Subscriptions, @defer, WebSQL-Streaming) wachsen dadurch unbegrenzt im Heap.
**Fix:** Begrenzter Puffer, `text/event-stream`, multipart und Upgrades ausnehmen.

### M-05 🟡 CSRF-Schutz deckt `/mcp` nicht ab
`GatewayApplicationBuilderExtensions.cs:90-94`. Die MCP-Handler akzeptieren `text/plain` (Simple Request ohne Preflight). Bei Negotiate/Kerberos sind Cross-Site-Requests mit automatischer Anmeldung möglich (abhängig von der Browser-Zonenkonfiguration).
**Fix:** `/mcp` in die CSRF-Prüfung aufnehmen und `Content-Type: application/json` erzwingen.

### M-06 🟡 Break-Glass anonym auslösbar, Client-IP aus `X-Forwarded-For`
`Application/Extensibility/Interceptors/JustificationAndBreakGlassInterceptor.cs:41-42,79,108-119`, `GatewayOptions.cs:709-711`. Defaults: `EnableBreakGlass=true`, `RequireRoleForBreakGlass=false`. Die Middleware läuft auch für anonyme `/api`-Pfade. Die Folge sind gefälschte `BREAK_GLASS_ACTIVATED`-Audit-Einträge mit gespoofter IP. Heute folgt daraus kein Rechtegewinn, kritisch wird es, sobald ein Pfad `IsBreakGlass` auswertet.
**Fix:** `RequireRoleForBreakGlass=true` als Default, Ticket per ITSM verifizieren, IP über `RemoteIpAddress` bzw. ForwardedHeaders bestimmen.

### M-07 🟡 Größenprüfung per `ContentLength` lässt sich mit chunked Transfer umgehen
`Endpoints/WebhookEndpoints.cs:26,57,141,233`, ebenso in `StreamingCdcEndpoints`, `DbtEndpoints`, `GovernanceEndpoints` (ingest-openapi), `SchemaRegistryEndpoints`, `WebSqlEndpoints`. Bei `null > X` ergibt die Prüfung `false`. Gelesen wird dann bis 100 MB (ca. 200 MB UTF-16-Heap), auch auf anonymen Webhooks.
**Fix:** `IHttpMaxRequestBodySizeFeature`/`RequestSizeLimit` pro Endpoint setzen.

### M-08 🟡 In-Memory-Rate-Limiter: globale Obergrenze, IPv6 nicht aggregiert
`Infrastructure/RateLimiting/InMemoryRateLimiterService.cs:84-89`. Ab 25.000 Einträgen bekommen **alle neuen** Clients 429. Mit IPv6-Rotation innerhalb eines /64 ist das trivial erreichbar. Derselbe Limiter dient als Fallback bei Redis-Ausfall.
**Fix:** IPv6 auf /64 aggregieren, LRU-Eviction statt globaler Sperre.

**Niedrig:**
- Der Tenant wird je nach Endpoint unterschiedlich ermittelt. `ResourceGroupMiddleware.cs:54` prüft auf `is string`, in `context.Items` liegt aber ein `TenantId`-Objekt. Dadurch landen alle Benutzer im Topf `default`, und bei Entra greift der Fallback auf den Claim nicht. **Fix:** Überall `context.Items[TenantIdItemKey]` verwenden.
- Positiv: `X-Forwarded-For` wird nur von KnownProxies übernommen, der Tenant-Wechsel per Header ist für Nicht-Admins blockiert.

---

## 5. Schicht 4 – REST-Endpoints

### C-05 🔴 HitL-/Vier-Augen-Freigabe umgehbar (mandantenübergreifend, Selbstfreigabe möglich)
`Api/Endpoints/HitLEndpoints.cs:20-66`, `Application/Mcp/Services/HitLStepUpApprovalService.cs:192-193`, `AiDataGuardrailService.cs:272`
```csharp
var tickets = hitlService.GetPendingTickets(tenantId);          // tenantId aus Query, null = alle
var approverSid = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? ...
```
- Es gibt keine Prüfung auf eine Approver-Rolle und keine Bindung an den Tenant.
- `GET /pending` ohne `tenantId` liefert die Tickets aller Tenants inklusive `ArgumentsJson`.
- Der Antragsteller wird über `GetUserSid()` bestimmt (PrimarySid/`oid`), der Genehmiger über `NameIdentifier` (`sub` bzw. `DOMAIN\user`). Die beiden Werte sind nie gleich, deshalb **greift die Sperre gegen Selbstfreigabe faktisch nie.**

**Angriff:** Ein Benutzer stößt ein MCP-Tool auf eine Four-Eyes-Tabelle an, liest die `approvalId` und genehmigt sein eigenes Ticket.
**Fix:** Approver-Policy plus `IsAuthorizedApproverForTableAsync`. Tenant aus `context.Items`. Auf beiden Seiten `GetUserSid()` verwenden.

### H-04 🟠 CDC-Ingest: Teilstring "ADMIN" im Namen macht zum Cluster-Admin
`Endpoints/StreamingCdcEndpoints.cs:40-43`
```csharp
(sid != null && sid.Contains("ADMIN", OrdinalIgnoreCase)) || (user.Identity?.Name?.Contains("ADMIN", ...))
```
Konten wie `CORP\badminton` oder ein ForwardAuth- bzw. Basic-Benutzer `readmin` gelten als Admin. Sie können CDC-Events mit beliebiger `TenantId` einspeisen und so fremde Subscriptions mit gefälschten Datenänderungen versorgen.
**Fix:** Den Teilstring-Check ersatzlos streichen und nur über Rollen bzw. Policies entscheiden.

### H-05 🟠 Policy-Simulation liest Audit-Logs fremder Tenants
`Endpoints/GovernanceEndpoints.cs:62-75`, `Application/Governance/Services/PolicySimulationService.cs:58-70`, `SqliteGovernanceRepository.Audit.cs:155-160`. Der Tenant kommt aus dem Request-Body, `null` bedeutet alle Tenants, die Rolle `DataOwner` reicht.
**Angriff:** Draft `p,*,*,*,*,deny` mit `limit=10000` liefert ActorSid, Tabelle, Spalte und Zeitpunkt aller erlaubten Zugriffe aller Tenants.
**Fix:** Tenant serverseitig aus dem Principal erzwingen, fremde Tenants nur für ClusterAdmin.

### H-06 🟠 ITSM-Webhook: Instanz-ID unsigniert, ein Secret für alle Instanzen, Ticket-Lookup ohne Tenant
`Endpoints/WebhookEndpoints.cs:102-105`, `Infrastructure/Itsm/ItsmWebhookHandler.cs:84,113,150`, `SqliteGovernanceRepository.Consent.cs:503-518`
Die HMAC-Signatur deckt nur `t=…` und Body ab. Die Instanz kommt aus `X-Instance-ID` oder `?instance=` und hat Vorrang. Es gibt nur ein globales Secret, und Tickets werden ohne Tenant-Filter gesucht.
**Angriff:** Wer Zugriff auf die ITSM-Instanz von Tenant A hat, genehmigt Consent-Requests von Tenant B.
**Fix:** Ein Secret pro Instanz, Instanz nur aus dem signierten Payload, Lookup `WHERE ticket=@t AND tenant_id=@expected`, Event-ID gegen Replay speichern.

### M-09 🟡 MCP: globales Session-Limit erschöpfbar, Body ohne Größenlimit
`Endpoints/McpEndpoints.cs:151-152,198-226`, `Application/Mcp/Services/McpSessionStore.cs:23,45-58`. Das Limit liegt bei 10.000 Sessions global, jeder `POST /mcp` ohne Session erzeugt eine neue (TTL 1 h). Ein einzelner Benutzer kann MCP damit eine Stunde lang für alle sperren. HitL-Tickets werden nie entfernt (Speicherleck).
**Fix:** Limit pro Principal, 429 statt 500, Body-Limit setzen, Tickets nach Ablauf aufräumen.

### M-10 🟡 WebSQL gibt `ex.Message` (inkl. DB-Fehlern) an den Client
`Endpoints/WebSqlEndpoints.cs:85,197,225`. Das legt Tabellen-, Schema- und Servernamen offen und ermöglicht das Aufzählen von Tabellen ohne Freigabe.
**Fix:** Generische Meldung plus Trace-ID.

### M-11 🟡 Admin-Endpoints ohne Tenant-Bindung
`DbtEndpoints.cs:210-231` (`ResetAllAsync`), dbt-Proposals approve/reject für beliebige GUIDs, `GovernanceEndpoints.cs:97-112` (Sunsetting global). Der Befund ist relevant, falls `DataOwner` eine mandantenbezogene Rolle ist.

### M-12 🟡 Schema-Registry: jeder `Developer` publiziert für jeden Service, `RegisteredBy` aus dem Body
`SchemaRegistryEndpoints.cs:27-35,104-128`, `SchemaRegistryService.cs:116`. Das verfälscht die Audit-Zuordnung, die Auswirkung ist begrenzt (die Registry wird nicht für das Routing genutzt).

**Info:** Für `IDataCatalogWebhookHandler` (Kern), `IOpenMetadataSyncService` und `IDbtWebhookReceiver` fehlt im Kern eine DI-Registrierung. Die Implementierungen liegen in `gql_extensions` (siehe Schicht 9). Der EventGrid-Handshake wird vor der Signaturprüfung beantwortet.

---

## 6. Schicht 5 – GraphQL (HotChocolate)

### H-08 🟠 `PersistedQueriesOnly` sehr wahrscheinlich wirkungslos
`GatewayServiceCollectionExtensions.cs:803-845`. Die Pipeline wird explizit aufgebaut, `UseOnlyPersistedOperationAllowed()` hängt **nach** `UseOperationExecution`. Es gibt weder `UseReadPersistedOperation` noch einen Document-Storage, und `OnlyAllowPersistedDocuments` wird nicht gesetzt. *(Begründung auf Basis HC 14/15, zur Laufzeit verifizieren.)*
**Fix:** Persisted-Middlewares direkt nach `UseDocumentCache` einhängen, Option setzen, Integrationstest ergänzen.

### H-09 🟠 CDC-Subscriptions umgehen Consent, Spalten-Deny und Masking; ABAC-Attribute fehlen
`GraphQL/Subscriptions/Subscription.cs:57-60`, `Application/Streaming/Services/StreamRlsPolicyEnforcer.cs:88-102,136-161`, `CasbinEnforcementService.cs:336-340`
Der Streaming-Pfad entscheidet nur über Casbin. Casbin liefert immer `hasUnconstrainedColumnAllow: true` ohne Spaltenregeln, alle Spalten sind dann `Clear`. Consent-Deny, Consent-Masks, Katalog-`ColumnMaskingRules` und "sensibel → Mask" greifen nie. Zusätzlich werden `Attributes = null` und `ClientIp = Loopback` übergeben. Attributbasierte **Deny**-Regeln (`ctx.Department == "Contractor"`) greifen deshalb nie.
**Angriff:** `subscription { onTableChanged(table:"hr_table_1") { payloadJson } }` liefert Gehalt und IBAN im Klartext.
**Fix:** Dieselbe Entscheidungslogik wie in `GatewayExecutionService` verwenden (Consent → Casbin → Katalog-Masking), mit vollständigem `SecurityEvaluationContext`.

### M-13 🟡 Cost-Analyse rechnet Tabellen-Resolver zu billig, Alias-Amplifikation möglich
`GraphQL/Interceptors/QueryCostAnalyzerRule.cs:126-153,193-196`, `Types/QueryTypes.cs:24-28`. Felder mit `first`, die einen Objekttyp (`TableRecordPayload`) zurückgeben, kosten etwa 8 Punkte. Rund 60 Aliase mit je 5.000 Zeilen ergeben etwa 300k Zeilen pro Request unterhalb des Budgets.
**Fix:** Kosten aus `first`/`limit` unabhängig vom Rückgabetyp berechnen, Limit für Root-Felder bzw. Aliase.

### M-14 🟡 WebSocket-Subscriptions ignorieren Token-Ablauf und -Entzug
`GraphQL/Subscriptions/WebSocketAuthInterceptor.cs:39,66-70`. Authentifiziert wird nur beim Connect. Der Token-Pfad über `connection_init` ist vermutlich toter Code (falscher Typcheck).
**Fix:** `exp` speichern und die Session bei Ablauf schließen, in `OnRequestAsync` erneut prüfen.

### M-15 🟡 `tableConsumers` legt jedem angemeldeten Benutzer das Zugriffsprotokoll beliebiger Tabellen offen
`Types/QueryTypes.cs:286-306`, `Application/Lineage/LineageImpactAnalyzerService.cs:236-282`. Ausgegeben werden alle Actor-SIDs mit Zugriffszahl und Zeitraum für bis zu 3650 Tage. `calculateConsentRevocationImpact` hat ebenfalls keine Owner-Prüfung, und `LegacySingleTenant`-Consents sind mandantenübergreifend sichtbar.
**Fix:** Auf Owner, GovernanceAdmin und PrivacyAdmin beschränken.

### M-16 🟡 Quota und Rate-Limit: zufällige `X-API-Key` ergeben jeweils einen frischen Bucket, angemeldete Benutzer teilen sich den Bucket pro `client_id`
`GraphQL/Interceptors/CostAndQuotaMiddleware.cs:67-94`, `Application/Caching/Services/ClientTierResolver.cs:62-90`. `RegisterApiKey` wird nie aufgerufen, der Resolver ist `Scoped`.
**Fix:** Subject = Tenant + SID, unbekannte Keys ignorieren bzw. auf IP-Basis zurückfallen.

### M-17 🟡 MCP-Executor liefert bei Deny oder Fehlern erfundene PII-Daten statt eines Fehlers
`GraphQL/Mcp/GatewayMcpQueryExecutor.cs:192-206,253-300`. Die `GatewayForbiddenException` wird verschluckt, danach kommt `GenerateDefaultToolResponse` ("Erika Mustermann", IBAN, Diagnose). Das ist kein Datenabfluss, aber eine Integritätsverletzung: Verweigerungen sind für den Agenten unsichtbar. Zusätzlich lesen die Resolver den Principal aus `IHttpContextAccessor` statt aus dem MCP-GlobalState, die MCP-Identität wird also ignoriert.
**Fix:** Strukturiertes MCP-Error-Result zurückgeben, Mock-Generator aus dem Produktivcode entfernen.

**Niedrig:**
- Federation-Result-Masking lässt sich per Alias-Kollision und benannte Fragmente umgehen (`SubgraphResultMaskingMiddleware.cs:94-138`, `SubgraphResultMasker.cs:135-146`). Federation ist derzeit faktisch inaktiv.
- `requestTableAccess` liefert `NOT_FOUND` mit dem Tabellennamen und erlaubt damit Enumeration (`MutationTypes.cs:181-188`).
- dbt-Quarantäne und Sunsetting lassen sich per Variablen bzw. Fragment-Spreads umgehen (`DbtHealthExecutionMiddleware.cs:150-182`, `SchemaSunsettingExecutionMiddleware.cs:150-179`).
- Beim CDN-Cache fehlt `Vary: X-Domain-Scope`, außerdem werden Fehlerantworten `public` gecacht (`CdnCacheTagMiddleware.cs:68-71`).

**Positiv:** Mutationen (approve/reject/revoke) prüfen Tenant, Funktionstrennung und Owner korrekt. Introspection ist in Prod standardmäßig aus, das Depth-Limit ist gesetzt, und Fehler werden in Prod per Code-Whitelist maskiert. `SqlFilterProvider` ist injection-sicher, aber toter Code.

---

## 7. Schicht 6 – Application-Kern: Policy, Consent, SQL-Generierung

### C-01 🔴 WebSQL: SQL-Funktionen umgehen RLS, Masking und ABAC vollständig
`Application/Sql/Services/GovernedSqlExecutionService.cs:86-123,261`; `gql_sqlparser/Analysis/SqlQueryAnalyzer.cs`, `RlsListener.cs` (keine Funktionsprüfung)
RLS, Masking und Policy hängen ausschließlich an Tabellenknoten im AST. Eine Abfrage ohne Tabellenknoten wird nur um `LIMIT` ergänzt.
```sql
SELECT query_to_xml('SELECT * FROM hr.salaries', true, false, '')   -- PostgreSQL
SELECT table_to_xml('hr.salaries', true, false, '')
```
Das Ergebnis ist die ganze Tabelle eines beliebigen Tenants im Klartext. Je nach DB-Rechten sind auch `pg_read_file`, `dblink`, `lo_import` oder `set_config` erreichbar. `WebSql.Enabled` ist standardmäßig `true`.
**Fix:** Funktions-Allowlist im AST, keine Funktionen mit SQL- oder Tabellenname-Argument. "Keine referenzierte Tabelle → ablehnen". DB-Rolle des Gateways ohne `EXECUTE` auf solche Funktionen.

### C-03 🔴 WebSQL ignoriert das Consent-Modell; mit Casbin werden auch sensible Spalten im Klartext geliefert
`GovernedSqlExecutionService.cs:149-192,222-226`
```csharp
if (policyDecision != null) lvl = policyDecision.GetColumnAccess(col.ColumnName);   // Casbin → immer Clear
else if (tableMeta.ColumnMaskingRules.ContainsKey(col.ColumnName) || col.IsSensitive) lvl = Mask;
```
- `_consentResolution` wird injiziert, aber nie verwendet. Ohne Casbin-Policies gilt nur der Tenant-Filter: kein Consent-Deny, keine Consent-RLS.
- Mit Casbin greift der `else if`-Zweig nie, auch `IsSensitive`-Spalten werden nicht maskiert.
- Tabellen ohne Katalogeintrag werden nicht abgelehnt.
- `dataSource` ist frei wählbar, damit ist jede konfigurierte Verbindung erreichbar.

**Fix:** Consent-Auflösung pro Tabelle wie im GraphQL-Pfad. Katalogregel "sensibel → Mask" immer anwenden. Ohne Metadaten ablehnen. Datenquellen-Allowlist pro Tenant.

### H-10 🟠 Filter-Orakel auf maskierten bzw. sensiblen Spalten
`Application/Services/SqlDataSourceExecutor.cs:72-76,142-148,222-226`, `Connectors/ConnectorSecurityPolicyEvaluator.cs:56-63`. Die Filterprüfung nutzt `GetColumnAccess` (gibt bei unconstrained bzw. Casbin `Clear` zurück). Die Regel "sensibel → Mask" gilt nur für die Projektion. Erreichbar über MCP (`variables` → `queryArguments`).
**Angriff:** `query_customers {"ssn":"123-45-6789"}`, die Treffermenge verrät die Existenz des Werts.
**Fix:** Eine gemeinsame Funktion `EffectiveAccess(col)` für Filter und Projektion. Filtern nur bei explizitem `Clear`.

### H-11 🟠 Consent-Union: Spalten ohne Regel werden für alle Zeilen der Vereinigung freigegeben
`Application/Services/ConsentResolutionService.cs:84,179`, `Domain/Interfaces/TableAccessDecision.cs:19`. Beispiel: Consent A (EU, nur `name` Clear) und Consent B (US, keine Spaltenregeln). Dann ist `ssn` auch für die EU-Zeilen `Clear`.
**Fix:** Über alle Metadaten-Spalten iterieren. `hasUnconstrainedColumnAllow` nur setzen, wenn ein Consent **weder Zeilenfilter noch Spaltenregeln** hat.

### H-12 🟠 Casbin: RLS-Filter-Sammlung weicht von der Allow-Entscheidung ab (fail-open)
`Application/Governance/CasbinEnforcementService.cs:76,287,385-449,545-573`. Die Entscheidung trifft Casbin per `keyMatch2` und `eval(p.sub_rule)`, die Filter sammelt eigener Code per `MatchObjectPattern` und eigenem Interpreter (ohne `r.obj`/`r.act`, Gruppe ≠ User). Matcht eine Regel nur in Casbin, ist der Zugriff erlaubt, aber der Filter fehlt. Beispiel: `finance.dbo.:table` liefert alle Regionen.
**Fix:** Ein einziger Matcher für Entscheidung und Filter (z. B. `EnforceEx`, liefert die matchenden Regeln). Fail-closed, wenn eine Allow-Regel einen Filter hat, der nicht gesammelt wurde.

### H-13 🟠 In-DB-"HMAC" ist ungekeyter SHA-256 mit öffentlichem Salt
`SqlDataSourceExecutor.cs:133,284,388-401`, `GovernedSqlExecutionService.cs:181,440-453`
```csharp
string hmacSalt = _options.Value.DataMasking.HmacSecretKeyVaultRef ?? ... ?? "gateway_salt";
```
Als "Salt" dient der **Name** des Key-Vault-Secrets (`GQL-HMAC-SECRET-KEY`), nicht das Secret selbst. Er steht zusätzlich im Debug-Log und im WebSQL-Audit (`securedSql`). Werte mit wenig Entropie (SSN, Geburtsdatum, PLZ, Gehalt) lassen sich per Wörterbuch umkehren.
**Fix:** Echtes HMAC mit dem aufgelösten Secret, am besten im Gateway. Im SQL nur als Parameter, nie loggen. Key pro Tenant ableiten.

### M-18 🟡 Wildcard `x.*` matcht über Segmentgrenzen
`CasbinEnforcementService.cs:553-557`. `finance.dbo.*` gibt auch `finance.dbo_hr.salaries` frei. In keyMatch2 wirkt `.` als Regex-Joker.
**Fix:** `prefix + "."` vergleichen bzw. pro Segment matchen.

### M-19 🟡 RLS-Template-Interpolation erlaubt Leerzeichen und SQL-Keywords in Claims
`CasbinEnforcementService.cs:463-473,523-535`. Die Regex `^[a-zA-Z0-9\-_.@: ]{1,256}$` lässt den Wert `0 OR tenant_id IS NOT NULL` durch, der bei ungequoteten Templates (`cost_center = ${attr.cost_center}`) zu SQL wird. Voraussetzung: Der Claim ist vom Benutzer beeinflussbar. Der Fix für CRIT-02 aus dem Vor-Review ist damit unvollständig.
**Fix:** Werte als DB-Parameter übergeben (`RowFilterParameters`) oder immer als quotiertes Literal einsetzen.

### M-20 🟡 WebSQL-DML (bei `AllowDml`): Casbin prüft nur `read`, WITH-CHECK erwartet Tenant `"42"`
`CasbinEnforcementService.cs:287`, `GovernedSqlExecutionService.cs:245-258`, `gql_sqlparser/IRlsPolicyProvider.cs:147`. Leserecht genügt für UPDATE und DELETE. `ExpectedTenantValue` bleibt beim Default `"42"`, ein INSERT in Tenant `42` ist erlaubt, in den eigenen Tenant nicht.
**Fix:** Aktion an Casbin übergeben, `ExpectedTenantValue = tenantId`, `RequireTenantColumnInInsert = true`.

### M-21 🟡 Streaming-RLS-Evaluator: zweiwertige Logik statt SQL-NULL-Semantik (fail-open)
`Application/Streaming/Services/StreamingRowFilterAstEvaluator.cs:142,260-262,420-423`. Bei `col <> 'CN'` mit NULL ergibt der Ausdruck hier `true`, bei `NOT (col = 'x')` mit fehlender Spalte ebenfalls `true`. Funktionen und Parameter werden als Spaltennamen behandelt.
**Fix:** Dreiwertige Logik, unbekannte Ausdrücke beim Kompilieren ablehnen.

**Niedrig:**
- Die Consent-Cache-TTL ist nicht auf `ValidTo` begrenzt, abgelaufene Consents gelten bis zu 10 Minuten weiter (`GatewayExecutionService.cs:189-192`; korrekt umgesetzt ist es in Z. 738-755).
- PostgreSQL-Backslash-Verdopplung verfälscht Literale bei `standard_conforming_strings=on`. Bei negativen Filtern (`NOT (dept = 'R\\D')`) ist das potenziell fail-open (`Domain/Common/DatabaseDialect.cs:101-104`).
- Der Casbin-Decision-Cache hat keinen Zeitbezug, zeitbasierte `sub_rule`s gelten bis zu 60 s über die Grenze hinaus (`CasbinEnforcementService.cs:237`).
- `DifferentialPrivacyEngine`: Value, Epsilon, Sensitivity und CohortCount kommen vom Client, die Engine bietet damit keinen echten Schutz.
- Toter Code mit Risiko beim späteren Aktivieren:
  - `CompiledSqlQueryPlanCache`: Der Cache-Key enthält keinen Principal und keine Spaltenrechte.
  - `CrossDomainJoinEngine`: Joins über gesperrte Fremdschlüssel möglich.
  - `PushdownPlanner`, `SingleQueryAstCompiler`.

**Positiv:**
- Identifier-Quoting per Allowlist-Regex (`\A…\z`).
- Konsequente Klammerung in `RowFilterSqlBuilder` und bei der OR-Union.
- Fail-closed bei Exceptions in `EvaluatePolicyAsync`.
- CTE-Shadowing einer gleichnamigen einteiligen Tabelle ist korrekt gelöst.

---

## 8. Schicht 7 – SQL-Parser / RLS-Rewriter (`gql_sqlparser`)

### C-02 🔴 CTE-Name mit Punkt umgeht RLS, ABAC und Masking für schema-qualifizierte Tabellen
`RlsListener.cs:27-37,97-101,168-172`, `Analysis/SqlQueryAnalyzer.cs:117-121,303-307`
```csharp
string cteName = NormalizeIdentifier(context.name.GetText());     // "\"sales.orders\"" → sales.orders
...
string normalizedName = NormalizeQualifiedName(context.qualifiedName()); // sales + orders → "sales.orders"
if (IsCte(normalizedName)) return;
```
```sql
WITH "sales.orders" AS (SELECT 1 x) SELECT * FROM sales.orders
```
Analyzer und Listener überspringen die echte Tabelle `sales.orders`. Sie fehlt in `ReferencedTables`, es gibt keine ABAC-Prüfung, keinen Filter und kein Masking. Die Ziel-DB löst `sales.orders` als Schema-Tabelle auf. Das Ergebnis ist ein ungefilterter Vollzugriff über alle Tenants.
**Fix:** CTE-Namen als einteilige Identifier speichern und `IsCte` nur bei `identifier().Length == 1` auslösen. Nie mit gejointen Strings vergleichen.

### C-06 🔴 Stack-Overflow-DoS durch tiefe Verschachtelung
`FastSqlEngine.cs:36,60,141`. Es gibt nur ein Längenlimit (512k Zeichen). Rekursiver Parser und `ParseTreeWalker` haben kein Tiefenlimit. Eine `StackOverflowException` lässt sich nicht abfangen, **eine** Anfrage eines angemeldeten Benutzers beendet den Gateway-Prozess. Das ist dieselbe Schwachstellenklasse wie CVE-2026-40324 aus dem Vor-Review, hier im eigenen Parser.
**Fix:** Tiefenprüfung auf Token-Ebene vor dem Parsen (z. B. max. 200), Parsen auf einem Thread mit definiertem Stack, Längenlimit ca. 64k.

### H-14 🟠 Table Functions werden ungeprüft durchgereicht
Grammatik `SqlBase.g4:516` (`TABLE(tableFunctionCall)`), kein Handler in Listener und Analyzer. Beispiel: `SELECT * FROM TABLE(pg.system.query(query => 'SELECT * FROM orders'))` führt Roh-SQL auf der Ziel-DB aus. Das hängt vom Trino-Connector ab (`system.query`/`raw_query`).
**Fix:** `tableFunctionInvocation`, `WITH SESSION` und `WITH FUNCTION` standardmäßig ablehnen, nur per Allowlist zulassen.

### H-15 🟠 DML-Pfad ohne Masking: Spalten kopieren und Werte per Orakel erraten
`RlsListener.cs:223-289` (bei `IsWebSqlDmlAllowed`). Zwei Varianten: `UPDATE customers SET notes = ssn`, danach liefert `SELECT notes` den Klartext. Oder `… WHERE ssn LIKE '1%'`, dann verrät der Rowcount den Wert schrittweise.
**Fix:** Spalten in SET und WHERE gegen die Masking-Policy prüfen und bei maskierten Spalten ablehnen.

### M-22 🟡 `EnforcedMaxRows` lässt sich mit CTE oder Subquery aushebeln
`RlsListener.cs:126-163`. `_subqueryDepth` erfasst `namedQuery`, `IN (query)` und `EXISTS` nicht. Das LIMIT landet in der inneren Query, die äußere bleibt unbegrenzt.
**Fix:** LIMIT explizit am Root-`queryNoWith` setzen.

### M-23 🟡 INSERT-WITH-CHECK umgehbar
`RlsListener.cs:314-380`. Umgehungen: ohne Spaltenliste, `UNION ALL` (nur der erste Zweig wird geprüft), Ausdrücke (`40+3`), mehrere `VALUES`, `'''42'''`.
**Fix:** Tenant-Spalte im Rewrite serverseitig ersetzen statt Literale zu vergleichen.

### M-24 🟡 Spaltennamen aus dem Katalog werden ungequotet ins Sicherheits-SQL übernommen
`RlsListener.cs:484-504`. Die Namen stammen aus OM-, Katalog- oder dbt-Sync. Ein Name wie `id, ssn AS id2` manipuliert die Subquery.
**Fix:** Identifier immer quoten, beim Sync validieren.

**Niedrig:**
- Eine statische, geteilte `DefaultErrorStrategy` (Instanzzustand) führt zu einer Race Condition im Fehlerpfad (`FastSqlEngine.cs:28,69`).
- Normalisierung ohne Trino-Lowercasing ist riskant für eigene `IRlsPolicyProvider`.

**Positiv:**
- Parse-Fehler führen zur Ablehnung (fail-closed, `EOF` in `singleStatement`).
- Der RLS-Filter wird korrekt geklammert.
- Keine ausnutzbaren Unterschiede bei Backquote- und Digit-Identifiern.

---

## 9. Schicht 8 – Application-Integrationen (MCP, Extensibility, HTTP-Datasources, Katalog)

### H-16 🟠 MCP-Sessions nur an `client_id` gebunden, nicht an den Benutzer
`Api/Endpoints/McpEndpoints.cs:87,136-148,182-206`, `McpSessionStore.cs:62`. Die Session speichert UserSid, Rollen und Tenant des Erzeugers, geprüft wird aber nur `client_id`. Alle Benutzer einer SPA bestehen diese Prüfung. Die Session-ID steht im Query-String (Logs, Proxys).
**Angriff:** Mit einer fremden Session-ID ruft ein anderer Benutzer `tools/call` mit der Identität und dem Tenant des Erzeugers auf.
**Fix:** Session an `sub`/`oid` + `tid` binden, Rollen pro Request aus dem Token lesen, Session-ID nur im Header übertragen.

### M-25 🟡 HTTP-Datasource sendet Credentials an Redirect-Ziele auf fremden Hosts
`Application/Services/DeclarativeHttpDataSourceExecutor.cs:209-223,546-578`. Nach einem Redirect werden Bearer-Token des Benutzers (`ForwardBearerToken`), API-Key und Client-Credential erneut gesetzt, die SSRF-Prüfung blockiert nur interne Ziele. Bei `ForwardBearerToken` ist der Befund Hoch.
**Fix:** Redirects nur auf denselben Origin, sonst ohne Auth-Header weiterleiten oder abbrechen.

### M-26 🟡 Tenant-Pushdown im HTTP-Datasource fail-open und per Parameter überschreibbar
`DeclarativeHttpDataSourceExecutor.cs:309-333,522-531`. Der konfigurierte `TenantIdQueryParam` fehlt in der Sperrliste, ein Benutzerargument mit diesem Namen steht in der URL vor dem echten Tenant-Parameter. Ohne Tenant-Claim wird gar kein Filter gesetzt.
**Fix:** Gateway-Parameter für Benutzerargumente sperren, Argumente per Allowlist zulassen, ohne Tenant ablehnen.

### M-27 🟡 Plugin-Integritätsprüfung wirkungslos (Fix für CRIT-01 aus dem Vor-Review unvollständig)
`Infrastructure/Plugins/PluginManager.cs:72-77,101-109,219-223`, `Application/Extensibility/DynamicPluginAssemblyLoadContext.cs:33-43,76`
- `manifest.json` liegt unsigniert neben den DLLs. Wer DLLs schreiben kann, schreibt auch das Manifest.
- Zwischen Hash (`ReadAllBytes`) und `LoadFromAssemblyPath` liegt ein TOCTOU-Fenster.
- Native Bibliotheken und transitive Abhängigkeiten werden nicht geprüft.
- `TrustedPluginHashes` in den Optionen wird nirgends gelesen.

**Fix:** Erwartete Hashes aus Konfiguration bzw. Key Vault oder signiertes Manifest. Laden per `LoadFromStream(geprüfte Bytes)`. Plugin-Verzeichnis für den Prozess read-only.

### M-28 🟡 MCP-Ressourcen behandeln anonyme Principals als Global-Admin
`Application/Mcp/Services/SemanticMcpCompiler.cs:114-115`. `isGlobalAdmin = … || isAnonymous` hebt den Consent- und Tenant-Filter auf, Glossar, Spaltendokumentation und Golden Queries aller Tabellen werden sichtbar (stdio-Sessions, Tokens ohne SID-Claim).
**Fix:** Anonym = keine Tabellen.

### M-29 🟡 Prompt-Injection-Guard per JSON-Escapes umgehbar
`McpProtocolHandler.cs:187-189`, `SemanticPromptGuardrail.cs:63`. Die Regex läuft auf `GetRawText()`, also auf noch escaptem JSON. `ignore all previous…` wird erst danach dekodiert und rutscht durch.
**Fix:** Erst dekodieren und NFKC-normalisieren, dann prüfen. Den Guard als Heuristik einstufen, nicht als Sicherheitsgrenze.

### M-30 🟡 OpenAPI-Ingestion überschreibt Governance-Flags ohne Ratchet
`Application/DataCatalog/Services/OpenApiIngestionService.cs:116,183-208`. `RequiresFourEyes`, `Sensitivity` und `ColumnMaskingRules` bestehender Tabellen werden zurückgesetzt.
**Fix:** Merge mit Ratchet wie im Katalog-Sync.

**Niedrig:**
- JSON-Injection in MCP-Antworten durch String-Templates (`McpProtocolHandler.cs:150-152,319-322,369-370`).
- Unbegrenzte Batch-Fan-outs und Antwortgrößen im HTTP-Datasource.
- Der PII-Scrubber ist eine lückenhafte Denylist.
- Das "SHA-256 Audit Seal" im PDF ist ungekeyt (`GdprAuditReportPdfExporter.cs:181-186`).
- MCP-ABAC verwendet eine feste Loopback-IP.

**Positiv:**
- SSRF-Abwehr mit IP-Pinning im `ConnectCallback` und erneuter Prüfung nach Redirects. Kleine Lücken in `IsRestrictedIp`: `64:ff9b::/96`, `2002::/16`, `198.18/15`, `240/4`.
- Pfad-Platzhalter sind gesperrt.
- Beim HitL kein Replay, und die Freigabe ist an den konkreten Tool-Call gebunden.

---

## 10. Schicht 9 – Infrastructure

### H-17 🟠 Audit-Hash-Kette: Abschneiden des Endes oder Totallöschung bleibt unentdeckt
`Infrastructure/Persistence/SqliteGovernanceRepository.Audit.cs:37-42,240-245`, `…Schema.cs:253-260`. Der Referenzwert `_lastAuditHash` wird beim Start und vor jedem Insert **aus derselben DB** gelesen. Nach dem Löschen der letzten N Einträge verkettet sich der nächste Event sauber an das gekürzte Ende. Nach einer Totallöschung und einem Neustart gilt GENESIS wieder als gültig. Der WORM-Export prüft nur diese Kette.
**Fix:** Endanker (Sequenznummer und letzter Hash) extern und signiert festhalten (WORM/S3, Notar). Lückenlose Sequenznummer in den Hash aufnehmen. Nie aus der geprüften DB resynchronisieren.

### M-31 🟡 WORM-Export still unvollständig
`AuditWormExportService.cs:71-75` + `SqliteGovernanceRepository.Audit.cs:162-163`. Angefordert werden 100.000 Einträge, das Repository begrenzt auf 5.000, sortiert nach `occurred_at DESC`. Bei mehr als 5.000 Einträgen im Fenster fehlen die älteren, gemeldet wird trotzdem `Success`. Die Lückenlosigkeit im Fenster wird nicht geprüft.
**Fix:** Eigene Methode mit Paging nach `rowid`, Kontinuitäts- und Anzahlprüfung, Sequenzbereich im Manifest.

**Niedrig:**
- Consent-Aktivierung ohne Statusbedingung im UPDATE. Approve nach Reject ist möglich (Race), Replays erzeugen doppelte Consents, und das Audit-Feld `ActorSid` enthält den Antragsteller (`…Consent.cs:589-594`).
- Der CDC-Poller verliert Events an Batch-Grenzen (`TOP` + Watermark auf `maxVersion`), und Watermark-Resets erfolgen still (`MssqlChangeTrackingPoller.cs:139-247`).
- Die Policy-Epoche kann auf 1 zurückfallen (Redis ohne Persistenz, `SET` statt `NX`) (`EpochValidationService.cs:61-66`).
- Fail-closed im Degraded-Modus entscheidet per Tabellennamen-Heuristik statt per Klassifizierung (`EpochValidationService.cs:104-108`).
- Klartext-Secret-Felder in den Optionen (S3, Jira, ServiceNow, BasicAuth, ForwardAuth). Fehler bei der Key-Vault-Auflösung des Audit-Keys werden geschluckt, dann greift ein HKDF-Fallback (`SqliteGovernanceRepository.cs:47-71`).
- Lokaler "WORM"-Export nur mit `FileAttributes.ReadOnly` (`AuditWormExportService.cs:168-176`).

**Positiv:**
- Repository-SQL vollständig parametrisiert.
- Kein BinaryFormatter, kein TypeNameHandling, kein MessagePack-Typeless.
- `RedisChannel.Literal`.
- Lua-Rate-Limiter atomar.
- Webhook-HMAC mit `FixedTimeEquals` und ±5-Minuten-Fenster.
- Idempotency-Key pro SID.
- TLS-Validierung nur hinter einem Flag deaktivierbar, das in Prod gesperrt ist.

---

## 11. Schicht 10 – Domain

- **`Sid`** (`Domain/Common/Sid.cs:5-36`): keine Validierung von Format, Länge oder Zeichensatz, dazu eine implizite Konvertierung aus `string`. Die Fallback-Kette `GetUserSid` reicht bis `sub`/`appid`/`client_id`/`azp`, verschiedene Identitätsquellen teilen sich damit einen Namensraum. Das ist die Ursache für die SID-Inkonsistenz in C-05. 🟢
- **`TableIdentifier`** (`TableIdentifier.cs:177-183`): blockiert nur `\0 \r \n .`. `:`, Steuerzeichen und Unicode sind ohne Normalisierung erlaubt. Folge: Kollisionen im Consent-Cache-Key (`ConsentCacheService.cs:274`, `:`-Join ohne Escaping) und JSON-Injection in MCP (siehe Schicht 8). 🟢
- **Unsichere Defaults in `GatewayOptions`:**
  - `WebSql.Enabled = true`
  - `EnableBreakGlass = true` / `RequireRoleForBreakGlass = false`
  - `OpenSchema` nicht in der Bypass-Liste
  - `HasAnySecurityBypassActive` ohne WebSQL-Flags (Health meldet "STRICT_ZERO_TRUST")
  - Folgende Optionen werden nirgends gelesen: `OpenMetadata.WebhookSecret`, `Catalog.WebhookSecret`, `Dbt.WebhookSecret`, `Plugins.TrustedPluginHashes`. 🟡
- **`TenantId`:** Regex `^[a-zA-Z0-9_-]{1,64}\z` ist in Ordnung.

---

## 12. Extensions (`gql_extensions`)

### H-18 🟠 Azure SharedKey-Signatur wird an fremde Storage-Accounts gesendet (wiederverwendbar)
`Lakehouse/Services/AzureBlobStorageProvider.cs:400-452`, `CompositeLakehouseStorageProvider.cs:147`, `IcebergMetadataReader.cs:275-287`. Akzeptiert wird jeder Host unter `*.blob.core.windows.net`, signiert wird aber über `/{eigenerAccount}/{eigenerContainer}/{pfad}`. Eine manipulierte Iceberg-Metadatei leitet den signierten Request an `evil.blob.core.windows.net`. Die Signatur ist 15 Minuten gegen den produktiven Account wiederverwendbar.
**Fix:** Nur den exakten Host `{AccountName}.blob|dfs.core.windows.net` zulassen.

### H-19 🟠 OpenMetadata-Sync leitet Datenfreigaben aus Metadaten-Policies ab
`OpenMetadata/OpenMetadataSyncService.cs:151-171,628-655`. Jede OM-Regel `allow` auf `all`/`table` wird zu einem Allow-Consent für ein Jahr. `Operations` (z. B. nur `EditDescription`) und `Condition` werden ignoriert, Rücknahmen gibt es nicht. Wer in OM Policies bearbeiten darf, steuert damit den Datenzugriff im Gateway.
**Fix:** Allowlist für Daten-Operationen, Conditions auswerten oder verwerfen, Reconcile mit Löschen, Vorschläge mit Vier-Augen-Freigabe statt Auto-Consent.

### H-20 🟠 dbt-Manifest: Path Traversal und Injection von SQL-Endpoints
`Dbt/DbtMetadataIngestionService.cs:385-395` → `gql/…/SqlEndpointLoader.cs:279-301`. `name`, `schema`, Spalten, `description` und `database` aus dem Manifest werden roh übernommen. `name: "../../x"` schreibt eine Datei außerhalb des Verzeichnisses, gleichnamige Endpoints werden überschrieben, Zeilenumbrüche schleusen Direktiven ein, der Hot-Reload greift sofort. Erreichbar per `POST /api/extensions/dbt/sync` (Rolle DataOwner), Voraussetzung ist `AutoSyncFromDbt=true`.
**Fix:** Namens-Regex `^[a-z0-9_]+$`, `GetFullPath`-Containment, Identifier quoten, Newlines entfernen, nicht überschreiben.

### M-32 🟡 Katalog- und OM-Mirror-Sync schwächt Governance-Flags ab
`DataCatalog/DataCatalogSyncService.cs:101-105,179-190,251-258`, `OpenMetadataSyncService.cs:97,431,513-571`. Bei bestehenden Tabellen werden Vier-Augen-Pflicht, `IsActive`, Sensitivität, `is_sensitive` und die Datenquellen-Felder überschrieben. Ein entferntes Tag oder eine leere Spaltenliste (Collibra, Purview, Alation) senkt den Schutz, auch per Webhook.
**Fix:** Merge-Semantik, der Sync darf Flags nur verschärfen.

### M-33 🟡 S3: Host-Allowlist ohne Punkt, keine Bucket-Allowlist
`S3LakehouseStorageProvider.cs:116-169`, `IcebergMetadataReader.cs:310-319`. `attacker-amazonaws.com` wird akzeptiert und bekommt den SigV4-signierten Request. Bucket und Key aus Manifests sind frei wählbar (Confused Deputy).
**Fix:** Host-Prüfung auf `.amazonaws.com`, Pfade unter der konfigurierten Tabellen-Location erzwingen.

### M-34 🟡 Katalog-Webhook: Timestamp nicht von der Signatur abgedeckt → Replay
`DataCatalog/CatalogWebhookHandler.cs:54-90,122-129`. Das HMAC deckt nur den Payload ab, es gibt keine Nonce. Zusammen mit M-32 lassen sich Herabstufungen wiederholt abspielen.
**Fix:** `timestamp + "." + payload` signieren, Event-IDs verteilt deduplizieren.

### M-35 🟡 Lakehouse-Executor: Masking nur per Namens-Heuristik, Tenant-Filter fail-open, Cache-Key aus nicht vertrauenswürdiger `table-uuid`
`LakehouseDataSourceExecutor.cs:429-431,533-621`, `IcebergPartitionPruner.cs:58-83`, `IcebergMetadataReader.cs:57,161-166`. Aktuell liefert der Executor Mock-Daten. Sobald echte Daten gelesen werden, ist der Befund Hoch.

**Niedrig:**
- OData-`$metadata` zeigt alle Tabellen und Spalten ohne Policy-Filter.
- Token im Log, wenn die OM-Secret-Auflösung fehlschlägt.
- Unbegrenzte Downloads (S3, Azure, Local, Katalog-Clients bei chunked).
- `LocalStorageProvider` löst Symlinks nicht auf, Default-Base ist `AppContext.BaseDirectory`.
- dbt-Webhook-Replay-Schutz ist optional.
- Jira- und ServiceNow-Clients melden bei einer Nicht-JSON-2xx-Antwort Erfolg mit erfundener Ticket-ID.

**Positiv:**
- Keine TLS-Deaktivierung, keine unsichere Deserialisierung, kein DTD-Parsing.
- CSDL-Escaping korrekt.
- `../` im LocalStorage korrekt abgewehrt.
- OData ohne `$filter`/`$orderby`, also keine Injection-Fläche.

---

## 13. Abgleich mit `security-review.md` vom 2026-09-28

| Vor-Review | Status laut Vor-Review | Befund heute |
|---|---|---|
| CRIT-01 Plugin-DLL-Integrität | behoben | **Unvollständig:** Manifest unsigniert neben den DLLs, TOCTOU, `TrustedPluginHashes` ungenutzt (M-27) |
| CRIT-02 SQL-Injection RLS-Template | behoben | **Unvollständig:** Regex erlaubt Leerzeichen und Keywords, ungequotete Templates sind weiter injizierbar (M-19) |
| CRIT-03 Parser-Stack-Overflow (HotChocolate) | behoben | Für HC behoben, **gleiche Klasse im eigenen Trino-Parser offen** (C-06) |
| Governance-RBAC / GDPR-BOLA | behoben | GDPR-Export und Mutationen korrekt. Policy-Simulation, HitL und `tableConsumers` weiterhin BOLA (H-05, C-05, M-15) |

---

## 14. Empfohlene Reihenfolge der Behebung

**Sofort (Hotfix / Konfiguration):**

1. `WebSql.Enabled=false` als Default, bis C-01, C-02, C-03, H-14 und C-06 behoben sind.
2. Docker-Image auf `ASPNETCORE_ENVIRONMENT=Production` umstellen (C-04).
3. HitL-Endpoints hinter eine Approver-Policy mit Tenant-Bindung stellen und die SID vereinheitlichen (C-05).
4. Den "ADMIN"-Teilstring-Check entfernen (H-04). `OpenSchema` in die Bypass-Liste aufnehmen (H-02).

**Kurzfristig (Sprint):**

5. Parser: CTE-Fix, Tiefenlimit, Funktions- und Table-Function-Allowlist, LIMIT am Root (C-02, C-06, C-01, H-14, M-22).
6. Eine zentrale Funktion für die effektive Spaltenentscheidung (Consent → Casbin → Katalog) für **alle** Pfade: GraphQL, WebSQL, Streaming, MCP-Filter (C-03, H-09, H-10, H-11).
7. Casbin: ein einziger Matcher für Entscheidung und Filter, Segment-Wildcards, RLS-Werte als Parameter (H-12, M-18, M-19).
8. Echtes HMAC mit Secret für das Masking (H-13).
9. MCP-Sessions an den Benutzer binden, Limits pro Principal (H-16, M-09).

**Mittelfristig:**

10. Cache-Integrität (HMAC) und Redis/Garnet-Auth (H-01). Audit-Endanker extern (H-17), WORM-Paging (M-31).
11. ITSM- und Katalog-Webhooks: Secret pro Instanz, signierter Timestamp, Deduplizierung (H-06, M-34).
12. Katalog-, OM-, dbt- und OpenAPI-Sync nur verschärfend, OM-Policies nicht automatisch als Consent übernehmen, dbt-Pfade validieren (H-19, H-20, M-30, M-32).
13. Storage-Provider: exakte Host- und Bucket-Allowlist (H-18, M-33).
14. Autorisierung deklarativ: FallbackPolicy und benannte Policies (M-03). Request-Body-Limits pro Endpoint (M-01, M-07).
15. Persisted Queries und Cost-Modell per Integrationstest absichern (H-08, M-13).

**Querschnitt:** Für jeden Kritisch- und Hoch-Befund einen negativen Integrationstest ergänzen, der den Bypass-Payload aus diesem Dokument enthält. So bleibt die Behebung regressionsfest.

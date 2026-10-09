# Security-Review Gesamtprojekt (2026-10-07)

Stand: Branch `feat/ast-target-dialect-generator`, Commit `9801fa1`. Zusätzlich die PoC-Konfiguration in
`talos/POC_Backstage_citizen_dev` (Repo auf lis-github, Betrieb gegen LWETEM_PROD).

Vorgehen: sieben getrennte, nur lesende Prüfungen:
- API und Identität
- SQL/WebSQL
- übrige SQL-Pfade
- Richtlinien und Mandanten
- GraphQL/MCP/Streaming
- Infrastruktur/Erweiterungen
- Deployment/Lieferkette

Jeder Befund hat Fundstellen im Code. Mit ✔ markierte Befunde sind zusätzlich von Hand am Code nachgeprüft; die übrigen sind belegt, aber nicht zweitgeprüft. Es wurde nichts gebaut oder ausgeführt.

Schweregrad: **hoch** = Schutzversprechen greift nicht oder Daten sind direkt gefährdet; **mittel** = Lücke mit Voraussetzung (Rolle, Konfiguration, Feature); **niedrig** = Härtung, Seitenkanal, Fehlkonfigurationsschutz.

## Kurzfassung

Die Kernpfade (OData, GraphQL-Tabellen, WebSQL, Prozeduren) sind solide gehärtet:
- **Abfrage-Pfade:** Bezeichner geprüft und quotiert, Werte gebunden, Tenant- und Row-Filter in jeder Ebene, Masking auch in WHERE/ORDER BY, fail-closed bei Unbekanntem.
- **Betrieb:** strenge Startvalidierung, Dev-Modi nur in Development, Audit-Kette mit HMAC und Anker.

Kritische Befunde gibt es keine. Die wichtigsten Schwächen sind **Schutzschichten, die dokumentiert, aber nicht aktiv sind** (Casbin, ReBAC, Vier-Augen bei HIGH, Schema-Contracts), sowie Lücken an Nebenpfaden.

Die fünf dringendsten Punkte:

1. **POL-1 ✔** Casbin-Richtlinien werden in Produktion nie geladen. Damit wirken weder Casbin-DENY noch Casbin-Row-Filter auf irgendeinem Pfad.
2. **PoC (DEP-1/2/3) ✔:**
   - Admin- und Proxy-Konto haben dasselbe Passwort, aus dem Repo ableitbar.
   - Der HMAC-Schlüssel steht im Repo.
   - Der PoC läuft im Development-Profil gegen die Produktions-DB.
3. **SQL-1 ✔** Bei WebSQL-UPDATE wird die Änderung der Mandantenspalte nur erkannt, wenn sie `tenant_id` heißt. Datensätze können dann in einen fremden Mandanten verschoben werden (nur mit Writer-Rolle).
4. **SQL2-1 ✔** `@result-column x clear` in Prozedurdefinitionen hebt Maskierungen des Data Owners auf.
5. **GQL-1 ✔** Ein WebSocket-`connection_init` mit fremdem Token mischt die Identitäten: Rollen kommen vom Token, der Mandant vom Upgrade-Request.

## Befunde Security

### hoch

| ID | Befund | Fundstellen | Behebung |
|---|---|---|---|
| POL-1 ✔ | Casbin wird ohne Modell- und Policy-Pfad registriert. `CasbinOptions` (Enabled, ModelPath, PolicyPath) liest kein Code. Kein Produktionscode ruft `LoadPolicyFromFile/Text` oder `AddPolicy` auf. `HasPolicies()` ist immer false, alle Casbin-Gates werden übersprungen. Die Doku verspricht Enforcement auf allen Pfaden. | `GatewayServiceCollectionExtensions.cs:373`, `GatewayOptions.cs:1110-1116`, `CasbinEnforcementService.cs:84-97, 962, 1043` | Policies beim Start laden (mit Watcher). `Enabled=true` ohne geladene Policy → Start abbrechen bzw. unhealthy. |
| SQL-1 ✔ | `RlsOptions.TenantColumnName` wird nicht gesetzt; es gilt der Bibliotheks-Default `tenant_id`. Der Lesefilter nutzt dagegen den pro Tabelle aufgelösten Namen (`TenantId`, `tenantId` …). Ein `UPDATE … SET TenantId = 'fremd'` wird nicht erkannt. | `GovernedSqlExecutionService.cs:446-457, 578-615`; `IRlsPolicyProvider.cs:156`; `AstSecurityVisitor.cs:296-313`; `RlsListener.cs:332-341` | Tenant-Spalte pro Tabelle an `RlsOptions` übergeben (Map). Test mit abweichendem Spaltennamen. |
| SQL2-1 ✔ | In Prozeduren setzt `clear` jede Ergebnisspalte auf Clear, auch wenn Consent oder Katalog Mask liefern und auch für Spalten aus Fremdtabellen (die Prüfung `foreign` kommt erst danach). ADR-018 erlaubt `clear` nur für Spalten ohne Quelle. Voraussetzung: Schreibrecht auf die Definitionsdatei. | `GovernedProcedureExecutionService.cs:561-569` | `cleared` nur im Zweig „keine Quelle“ auswerten. Test mit maskierter Spalte, die als clear deklariert ist. |
| DEP-1 ✔ (PoC) | `admin` (GovernanceAdmin) und `citizen-service` haben denselben PBKDF2-Hash. Der Default von `AUTHERIS_PROXY_BASIC_AUTH` in `compose.yaml` enthält das Passwort base64-kodiert. | PoC `autheris/appsettings.json:45,52`, `docker-compose.autheris.yaml:36,42`, `compose.yaml:211` | Passwörter rotieren, je Konto zufällig, nur in `.env`. Defaults durch `${VAR:?}` ersetzen. |
| DEP-2 ✔ (PoC) | Der HMAC-Masking-Schlüssel steht als Default im Repo. Aus demselben Wert wird der Audit-Ketten-Schlüssel abgeleitet. | PoC `docker-compose.autheris.yaml:111`, `.env.autheris.example:31`, `.env.example:76` | Wert als kompromittiert behandeln und rotieren. Kein Default (`:?`), eigener `GovernanceDb:AuditHmacKeyVaultRef`. |
| DEP-3 (PoC) | Development mit Quickstart-Preset, OpenSchema, Introspection, Allow-all-CORS, WebSQL und MCP läuft gegen LWETEM_PROD. Dazu `TrustServerCertificate=True`. Backstage ist auf allen Interfaces erreichbar (`3000:7007`) und proxyt mit dem Servicekonto. | PoC `docker-compose.autheris.yaml:19-32, 60-108`, `compose.yaml:214` | Ports an 127.0.0.1 binden, Test-DB oder Read-only-Replikat, gültige CA. |

### mittel

| ID | Befund | Fundstellen | Behebung |
|---|---|---|---|
| GQL-1 ✔ | Ein Token in `connection_init` ersetzt `HttpContext.User`. `Items["TenantId"]` und `SecurityPrincipalContext` bleiben vom Upgrade-Request. Mutationen (approve, reject, revoke) nehmen den Mandanten aus `Items` und die Rollen vom Token. Voraussetzung: gültige Identitäten in beiden Mandanten. | `WebSocketAuthInterceptor.cs:78-117`, `MutationTypes.cs:367-377` | Nach dem Wechsel den Sicherheitskontext neu bilden und `Items` überschreiben, oder abweisen, wenn das Token-Subjekt ≠ Upgrade-Subjekt. |
| POL-2 | Vier-Augen bei `Sensitivity=HIGH` (ADR-008) ist nicht umgesetzt; geprüft wird nur `requires_four_eyes`. | `PostgreSqlGovernanceRepository.Consent.cs:1130-1139`, `SqliteGovernanceRepository.Consent.cs:1019` | HIGH/RESTRICTED/SECRET in `StatusAfterApproval` einbeziehen. |
| POL-3 | Die ITSM-Aktivierung legt Consents ohne Spaltenregeln an. Später hinzugekommene, nicht getaggte Spalten sind dann Clear. Der GraphQL-Pfad friert die Spalten dagegen ein. | `PostgreSqlGovernanceRepository.Consent.cs:1389-1408`, `SqliteGovernanceRepository.Consent.cs:796-814` | Gemeinsamen Spalten-Snapshot verwenden. |
| POL-4 | Der Lakehouse-Executor maskiert HMAC ohne Mandantenbezug und setzt `InDbColumnMaskingExecuted`. Pseudonyme sind dadurch über Mandanten hinweg gleich. | `LakehouseDataSourceExecutor.cs:99-114`, `DeltaLakeDataSourceExecutor.cs:196-201` | `MaskingRule.CreateTenantScopedHmacRule` vor `MaskValue`. |
| POL-5 | Purview/Collibra/Alation werten Art.-9-Tags nur auf Tabellenebene aus. Eine Spalte mit `HealthData` bleibt ohne Maskierung. | `DataCatalogSyncService.cs:68-109` | Spalten-Tags gegen `GdprArticle9Tags` prüfen → sensibel, REDACT, Tabelle HIGH. |
| POL-6 | ReBAC (`can_query`) prüfen nur MCP-RAG, DuckDB-OLAP und Streaming, nicht OData, GraphQL, WebSQL oder Prozeduren. Laut Doku prüft jede Abfrage ReBAC. | `UnifiedPolicyDecisionPoint.cs:69-86`, fehlt in `GatewayExecutionService.ResolveTableAccessAsync` | Zentral in `ResolveTableAccessAsync` prüfen oder den Geltungsbereich dokumentieren. |
| POL-7 | ReBAC: Leere Tupelfelder wirken als Wildcard. Ein Tupel mit leerem `User` und Relation `parent` öffnet die Tabelle für alle. | `ZanzibarRebacEvaluator.cs:237, 263-293`, `InMemoryRebacStore.cs:84-97`, `RedisRebacStore.cs:86-88`, `RebacEndpoints.cs:56-93` | Leere Felder an API und Store ablehnen, im Evaluator als Deny behandeln. |
| EXT-1 ✔ | dbt-Vorschläge `RLS_FILTER:` und `CASBIN_ROLES:` werden beim Approve als Maskierungsregel gespeichert. Es entsteht kein Row-Filter (die Spalte wird nur geschwärzt). Approve überschreibt zudem stärkere Masken ohne Ratchet. | `DbtMetadataIngestionService.cs:233-291, 422-460` | RLS- und Casbin-Vorschläge über ihre echten Pfade anwenden oder ablehnen. Masking nur verschärfend. |
| EXT-2 ✔ | Der Katalog-Ratchet übernimmt `SourceName` aus dem Katalog (= Domain). Ein Sync kann eine Tabelle auf eine andere DB-Verbindung mit anderen Rechten umleiten. Behoben: bestehender `SourceName` bleibt (DataCatalog, OpenMetadata-Batch und -Webhook, OpenAPI); eine ungebundene Tabelle darf nur an ihre eigene Domäne gebunden werden (`CatalogGovernanceRatchet.RatchetSourceName`). | `CatalogGovernanceRatchet.cs:31`, `DataCatalogSyncService.cs:150`, `OpenMetadataSyncService.cs:754` | `SourceName` aus dem Bestand übernehmen. |
| EXT-3 / API-12 | Traffic-Shadowing kopiert Identitäts-Header (`X-Forwarded-User/Tenant`, `X-Tenant-ID`). Query-String und Body werden kaum redigiert. Opt-in. | `TrafficShadowingMiddleware.cs:68-79`, `PiiShadowingRedactor.cs:276-321` | Header-Allowlist, Literale strukturiert ersetzen. |
| EXT-5 | Lakehouse: Azure ohne Container-Allowlist, Requests per SharedKey für den ganzen Account. Bei S3 erlaubt eine leere Bucket-Allowlist jeden `*.s3.amazonaws.com`-Host. Folge: Confused Deputy über Mandanten-Locations. | `AzureBlobStorageProvider.cs:121-205`, `LakehouseLocationGuard.cs:354-388`, `S3LakehouseStorageProvider.cs:163-175` | Allowlists, leer = fail-closed außerhalb von Development. |
| INF-1 | Epoch-Validierung: Redis-Fehler bei bestehender Verbindung werden verschluckt, danach gilt die alte lokale Epoch. Widerrufe greifen dann bis zum Ablauf der L1-TTL nicht. | `EpochValidationService.cs:58-98, 113-148` | Fehlerpfad wie „degraded“ behandeln (fail-closed für sensible Daten). |
| SQL-2 | WebSQL-Policy-Maps sind case-insensitive. In PostgreSQL sind `"Foo"` und `foo` zwei Tabellen; ihre Policies überschreiben sich, ein RLS-Bypass ist möglich. | `GovernedSqlExecutionService.cs:276-282, 381, 388-392, 470-526` | Schlüssel pro Dialekt falten oder Kollision ablehnen. |
| SQL-3 | Es gibt keinen Databricks-Generator; unbekannte Dialekte fallen still auf ANSI zurück. `CastExpression.TargetType`, `Extract.Field` und Binary-Literale werden roh ausgegeben. WebSQL lässt heute nur PG, MSSQL und SQLite zu. | `SqlDialectGeneratorFactory.cs:571-579`, `SqlDialectGeneratorBase.cs:530-762` | Factory wirft bei unbekanntem Dialekt; Allowlist für Typ- und Feldnamen. |
| SQL-5 | Dreiteilige Namen: Der Katalogteil wird nur gegen den logischen Namen geprüft, aber roh ausgegeben. Auf SQL Server ist das der Datenbankname. | `GovernedSqlExecutionService.cs:370-377`, `RlsListener.cs:938` | Katalogteil entfernen oder auf den physischen Namen umschreiben. |
| MCP-1 | `resources/list` und `resources/read` liefern alle Spalten inklusive gesperrter, mit Beschreibungen und dbt/OM-Metadaten. Unbedingte Deny-Consents werden ignoriert. | `SemanticMcpCompiler.cs:128-139, 165-213, 266-301` | Spaltenfilter aus `Query.GetCatalogAsync` wiederverwenden. |
| GQL-3 | Subscriptions schreiben kein Audit (Subscribe und Delivery). Die Zugriffe fehlen auch im DSGVO-Disclosure-Report. | `Subscription.cs:100-136`, `StreamRlsPolicyEnforcer.cs` | `STREAM_SUBSCRIBE` plus aggregiertes Delivery-Audit. |
| GQL-4 | Subscriptions: Topic-Namen sind frei wählbar, es gibt nur globale Limits (1000 Topics, 500 pro Topic). `cdc_all` steht jedem offen. Ein Nutzer kann alle Slots belegen. | `InMemoryCdcEventChannel.cs:22-23, 85-95`, `Subscription.cs:56-59` | Limits pro Subjekt, Topic gegen Katalog und Consent prüfen, `cdc_all` nur für Admins. |
| API-1 | `warn_fallback_default_tenant_for_webhooks` und `Itsm.LegacyGlobalWebhookSecret` sind nur WARN und damit in Production erlaubt. Ein ITSM-Callback kann dann Requests fremder Mandanten genehmigen. | `GatewayOptions.cs:104, 222`, `ItsmWebhookHandler.cs:283-310` | Als DANGER einstufen. |
| API-2 | Envoy ext_authz im JSON-Modus entscheidet mit der Identität des aufrufenden Mesh-Dienstes, nicht mit der des Endnutzers (Confused Deputy). | `EnvoyExtAuthzEndpoints.cs:20-27`, `EnvoyExtAuthzService.cs:131-166` | Endnutzer aus `source.principal` oder weitergereichtem Token, sonst JSON-Modus entfernen. |
| API-3 | Schema-Contracts (Partner-Slices) werden nicht durchgesetzt. Header und `?contract=` überschreiben zudem den Claim. | `SchemaContractMiddleware.cs:40-57, 86` | Durchsetzen (Claim hat Vorrang, nur Einschränkung) oder als nicht sicherheitswirksam dokumentieren. |
| API-4 | Das FinOps-Budget nutzt rohe Claims mit Fallback `default`. Anonyme Webhook-Aufrufe belasten das Budget der Kerberos-Nutzer (DoS). | `FinOpsBudgetMiddleware.cs:45-52, 95` | Tenant aus `SecurityPrincipalContext`, Anonyme nicht verbuchen. |
| DEP-6 / POL-14 | HMAC-Masterkey, Audit-Key und ForwardAuth-Secret haben keine Mindestlänge. | `DefaultEnvironmentSecretProvider.cs:86-121`, `ColumnMaskingProvider.cs:26-45` | ≥32 Byte außerhalb von Development erzwingen. |
| DEP-7 / INF-6 | Die Governance-DB akzeptiert `SSL Mode=Require` (ohne Zertifikatsprüfung). Datenquellen werden nicht auf `TrustServerCertificate=True` geprüft, die Doku empfiehlt das sogar. | `PostgreSqlGovernanceRepository.cs:80`, `SqlConnectionFactory.cs:17-75`, `configuration-guide.md:208, 765` | `VerifyFull` bzw. `TrustServerCertificate=false` in Production. |
| DEP-8 | Serilog ersetzt die LoggerFactory, die `Logging:LogLevel`-Filter wirken nicht. ASP.NET loggt Query-Strings (`$filter=email eq …`) auf Information. | `Program.cs:36-39`, `appsettings*.json` | `Serilog:MinimumLevel:Override`, Query-Strings redigieren. |
| DEP-5 | Compose und README ziehen `ghcr.io/themulle/gql:getting-started`, die Pipeline baut aber `ghcr.io/themulle/autheris`. | `docker-compose.yml:13`, `README.md:369` | Image-Namen vereinheitlichen, altes Package entfernen. |
| SQL2-3 | Der Fallback `new SqlDataSourceExecutor()` hat kein Environment und erzeugt deshalb synthetische Daten. In Produktion bekommen Vektor- und Lakehouse-Tabellen ohne Extension erfundene Zeilen als Antwort. | `GatewayExecutionService.cs:40, 156-157`, `SqlDataSourceExecutor.cs:94-106` | Fallback entfernen und 501 liefern; `_environment == null` gilt als Produktion. |
| SQL2-8 | Prozeduren haben kein Byte-Limit; LOB-Spalten werden vollständig materialisiert. | `MssqlProcedureInvoker.cs:136-154` | Byte-Budget wie `ReadRowsAsync`. |
| SQL2-9 | DuckDB-Timeout nur über das CancellationToken, Generatorfunktionen sind erlaubt (Confidence niedrig). | `DuckDbOlapEngine.cs:65-69, 309-317` | `Interrupt` bei Abbruch, Generatoren sperren. |

### niedrig (Auswahl, Details in den Fundstellen)

- **POL-8:** Vorab terminierte DENY-Consents greifen bis zu 10 min verspätet.
- **POL-9:** Der Degraded-Mode bestimmt Sensitivität per Substring im Tabellennamen; L1 wird bei Reconnect nicht geleert.
- **POL-10:** Der Casbin-Cache-Key enthält die `RequestedColumns` nicht.
- **POL-11:** Die ReBAC-Objekt-ID enthält kein Schema.
- **POL-12:** Audit der Freigabeschritte nach dem Commit, Fehler werden verschluckt.
- **POL-13:** Federation-Masking-Bypass über rohe Admin-Rollen-Claims.
- **POL-19:** `ChunkPiiRedactor` ist nicht fail-closed ohne Katalogspalten.
- **GQL-5:** `FORBIDDEN`-Meldung verrät die Existenz von Tabellen und den Grund der Ablehnung.
- **GQL-6:** `tableConsumers` ohne Consent- und Tenant-Filter.
- **GQL-12:** dbt-Quarantäne-Status vor der Autorisierung.
- **MCP-4:** Synthetische Rollen `AiAgent`/`Reader` im Fast-Path.
- **MCP-5:** Golden Queries ohne Consent-Filter.
- **MCP-6:** `@mcpTool` mit geratener `TargetTable` (latent).
- **MCP-7:** `catch (Exception)` beim Argument-Parsing ist fail-open.
- **API-8:** `TrustedNetworks` wird nicht validiert (`0.0.0.0/0` möglich).
- **API-9:** `DataProtectionOfficer` fehlt in der Header-Rollen-Denylist.
- **API-11:** Rohe Exception-Texte in WebSQL, Arrow-Flight und Iceberg.
- **API-13:** BasicAuth-Lockout nicht atomar und synchron.
- **API-16:** `warn_allow_all_cors_origins` ist in Production erlaubt.
- **API-17:** WASM-Plugin `:latest` ungepinnt.
- **EXT-6:** OpenMetadata-User-Pfad ignoriert das Rollen-Mapping.
- **EXT-7:** DataCatalog legt neue Tabellen sofort aktiv an.
- **INF-2:** CDN-Purge und Shadowing nutzen einen ungehärteten HttpClient.
- **INF-3:** Secret-Referenzen landen im Klartext im Log.
- **SQL-6:** Die Join-Guardrail arbeitet ohne Tabellenbezug.
- **SQL2-10:** CrossDomainJoin baut den IN-Filter per String (toter Code).
- **SQL2-13:** Tree-Memo nur pro Tabelle.
- **SQL2-14:** Der DuckDB-Validator kennt keine Kommentare.
- **SQL2-15:** Maskentext ohne `EscapeSqlLiteral` und ohne `N`-Präfix.
- **SQL2-16:** Prozeduren ohne Audit-Repository liefern trotzdem aus.
- **SQL2-19:** Unqualifizierte Spalten in RLS-Subqueries.
- **WF-1:** `ExtendConsentExpiryAsync` ohne Prüfungen (ohne Aufrufer).
- **CI und Lieferkette:**
  - **DEP-9:** Der `--vulnerable`-Schritt in CI ist wirkungslos.
  - **DEP-10:** Kein Locked-Mode, keine `NuGet.config`.
  - **DEP-11:** `workflow_dispatch` überschreibt `latest`; Release ohne Tests und Signatur.
  - **DEP-12:** Alte Benchmark-Secrets stehen in der Git-History (HEAD ist sauber).
  - **DEP-14:** Klartext-BasicAuth-Passwörter werden erst beim Login abgelehnt.
  - **DEP-15:** Benchmark-Images mit `:latest` und `sa`.
  - **DEP-16:** Kein `AllowedHosts`, CORS-localhost-Fallback in Production.

## Laufzeitfehler

| ID | Befund | Fundstellen |
|---|---|---|
| DEP-4 | Das veröffentlichte Image startet mit Defaults nicht: `ASPNETCORE_HTTPS_PORTS=8081` ohne Zertifikat; `governance.db` in `/app` gehört root, der Prozess läuft als `$APP_UID`. | `Dockerfile:6-29`, `GatewayOptions.cs:547` |
| SQL2-2 | OLAP liest höchstens 5.000 Zeilen (Clamp in `SqlConnector`). Aggregate werden still über einen Ausschnitt berechnet, die Kapazitätsprüfung greift nie. | `SqlConnector.cs:155`, `DuckDbOlapEndpoints.cs:225-238` |
| SQL-4 | Im Modus `SqlRewriterEngine=AstCompiler` scheitert jede Abfrage auf HMAC-Spalten, weil die Maskenausdrücke neu geparst werden. ShadowDualRun verschluckt die Fehler. | `AstSecurityVisitor.cs:150-153`, `FastSqlEngine.cs:764-771` |
| GQL-2 | Subscriptions über graphql-ws: Middlewares setzen Header bzw. Status, nachdem die Response gestartet ist, und werfen dabei. | `CdnCacheTagMiddleware.cs:42-88`, `CostAndQuotaMiddleware.cs:93-122` |
| SQL2-4 | OLAP und CrossDomain maskieren ein zweites Mal (HMAC(HMAC(x))), Pseudonyme passen nicht zu den anderen APIs. | `ConnectorRowMasker.cs:30-53`, `DuckDbOlapEndpoints.cs:248` |
| MCP-2 | Sync-over-async auf Redis in jeder MCP-Anfrage, teils innerhalb von `lock` (Thread-Pool-Starvation, Deadlock möglich). | `McpSessionStore.cs:93-227`, `HitLStepUpApprovalService.cs:271-449` |
| MCP-3 / API-5 / API-6 | SSE: parallele Writes auf dieselbe Response; Cluster-Subscription je Session wird nie freigegeben (Leak). | `McpEndpoints.cs:73-108`, `McpSessionStore.cs:239-268` |
| EXT-4 | Delta- und Iceberg-Executor liefern Beispieldaten statt echter Dateien, auch in Produktion. Der PG-CDC-Worker ist ein reiner Delay-Loop. | `DeltaLakeDataSourceExecutor.cs:104-146`, `LakehouseDataSourceExecutor.cs:289-355`, `PostgreSqlLogicalReplicationService.cs:150-184` |
| API-7 | Ein ungültiger Tenant-Claim ergibt 500 und Error-Log-Flut statt 403. | `ClaimsNormalizer.cs:110-117`, `GatewayExceptionHandler.cs:74-88` |
| API-14 | EntraId und ADFS gleichzeitig: ADFS-Signaturschlüssel werden nie geladen. | `GatewayServiceCollectionExtensions.cs:697-711` |
| API-15 | Swagger außerhalb von Development antwortet mit 401 ohne Challenge; mit Kerberos nicht nutzbar. | `ODataEndpoints.cs:210-215` |
| SQL2-18 | `RollbackAsync(ct)` im catch-Block verdeckt die ursprüngliche Exception. | `SqlDataSourceExecutor.cs:355` |
| SQL-7 / SQL-8 | Root-LIMIT landet in der Subquery von DML. Aliase sind bei PG/SQLite unquotiert. | `AstSecurityVisitor.cs:47-82`, `RlsListener.cs:958` |
| INF-4 | Der ITSM-Outbox-Lock läuft nach 5 min ab, dadurch entstehen doppelte Tickets. | `ItsmOutboxDispatcherHostedService.cs:22-101` |
| INF-5 | Webhook-Dedup nur pro Prozess; die OM-Event-ID wird vor der Verarbeitung registriert. | `CatalogWebhookHandler.cs:34, 228-250` |
| EXT-8 | S3 fest auf Region `us-east-1`, Response-Leak bei Fehlern, stille unsignierte Requests. | `S3LakehouseStorageProvider.cs:92-263` |
| GQL-8 | Race beim Entfernen leerer Topics, Subscriber gehen verloren. | `InMemoryCdcEventChannel.cs:90-147` |
| GQL-11 | Idempotency-Key ohne Argument-Hash; Tenant-Fallback uneinheitlich. | `MutationTypes.cs:84-196` |
| POL-15 | PostgreSQL-Aktivierung verliert die SID bei Gruppen und Service-Principals. | `PostgreSqlGovernanceRepository.Consent.cs:1390` |
| SQL2-17 | `finance_items` im GraphQL liefert hart kodierte Demo-Daten. | `GatewayExecutionService.cs:967-1074` |

## Architektur

1. **Eine Zugriffsentscheidung statt vier (SQL2-6, POL-6, API-10).**
   - Heute bilden GatewayExecutionService, CrossDomainResolver, Prozeduren und WebSQL Consent, Cache und Casbin-Merge je unterschiedlich ab. ReBAC wird nur teilweise geprüft, Rollen werden doppelt ausgewertet.
   - Vorschlag: `ITableAccessResolver` als einzige Stelle, die vollständig merged (Spalten, Filter, Parameter, ReBAC, Casbin).
2. **Ein gemeinsamer Lesepfad für Konnektoren (SQL2-5, SQL2-4).**
   - Nur der GatewayExecutionService filtert nach, wenn der Konnektor nicht gefiltert hat. OLAP, CrossDomain und Streaming verlassen sich auf den Konnektor.
   - Vorschlag: ein `GovernedConnectorReader` mit Policy, Nachfilter, genau einmal Masking und Byte-Limit.
3. **Toten und Demo-Code entfernen (SQL2-21, GQL-10, POL-17, SER-1, SQL2-17, INF-6).**
   - Betroffen: `SingleQueryAstCompiler`, `SqlFilterProvider`, `CompositeKeySqlGenerator`, `PushdownPlanner`, `CrossDomainJoinEngine`, `StreamingResultPipeline`, parametrisierter RowFilter-Pfad, Federation-Clients, `GovernedExecutionKernel.ExecuteAsync`, Arrow-Flight-Stub, `TenantBackfillMigration`.
   - Mehrere davon enthalten Fail-open-Muster, die bei späterer Verdrahtung scharf würden.
4. **Feature-Status ehrlich machen (EXT-4, SQL2-3).** Stubs mit Beispieldaten (Lakehouse, Vektor-Fallback, PG-CDC) außerhalb von Development hart ablehnen.
5. **Provider-Auflösung vereinheitlichen (SQL2-22).** Provider-Namen werden heute an fünf Stellen unterschiedlich aufgelöst. Vorschlag: eine `ProviderInfo` (Dialekt, Factory, Session-Init).
6. **Doku an Optionsklassen angleichen (DEP-13).** Beispiele: `Audit:HmacSecretKeyVaultRef` existiert nicht, `GovernanceDb__Provider=SqlServer` bricht den Start ab, Ports stimmen nicht.
7. **Tree-Pfad vor dem Verdrahten (SQL2-7, SQL2-11, SQL2-12).**
   - Gesamt-Zeilenbudget über alle Ebenen und MaxOffset.
   - Parameter-Präfix pro Tabelle.
   - Gemeinsame Normalisierung der HMAC-Eingabe mit REST.

## Empfohlene Reihenfolge

1. **Sofort (PoC):** Passwörter und HMAC-Schlüssel rotieren, Ports an 127.0.0.1 binden, Defaults aus `compose.yaml` entfernen (DEP-1/2/3).
2. **Vor dem nächsten Release:**
   - Casbin laden oder `Enabled` ehrlich auf false setzen (POL-1).
   - Tenant-Spalte an `RlsOptions` übergeben (SQL-1).
   - `clear` in Prozeduren korrigieren (SQL2-1).
   - WebSocket-Identität vereinheitlichen (GQL-1).
   - Image-Start reparieren (DEP-4).
3. **Danach:**
   - Governance-Lücken: POL-2/3/4/5/7, EXT-1/2, MCP-1.
   - Audit für Subscriptions: GQL-3.
   - Härtung der Startvalidierung: DEP-6/7/8, API-1.
4. **Mittelfristig:** Architektur 1 bis 3 umsetzen; damit verschwinden mehrere Einzelbefunde strukturell.

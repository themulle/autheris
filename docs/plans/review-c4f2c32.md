# Review Commit c4f2c32 (Sicherheitskorrekturen und GraphQL G1–G5)

Stand: Branch `feat/ast-target-dialect-generator`, geprüft wurde `c4f2c32` gegen `1dd4e09`. Grundlage sind die Befunde aus
[security-review-2026-10-07.md](security-review-2026-10-07.md) und die Spezifikation
GraphQL-Übergabe G1–G5 (Datei entfernt, Stand in `status-und-umsetzungsplan-2026-10-07.md`).

Vorgehen:
- Nur gelesen, nichts gebaut oder ausgeführt (kein .NET-SDK). Erster Schritt für die Umsetzung: `dotnet build` und `dotnet test`.
- Mit ✔ markierte Punkte sind von Hand am Code nachgeprüft, die übrigen haben Fundstellen, sind aber nicht zweitgeprüft.
- Zeilenangaben beziehen sich auf `c4f2c32`.

## Ergebnis

Der Commit sollte so noch nicht nach `main`:
- Bestehende Tests brechen.
- Die Casbin-Korrektur hat neue Lücken.
- Für fast keinen Sicherheitsfix gibt es einen Test, obwohl TDD verlangt war.

Viel ist richtig gelöst. Das neue GraphQL-Schema leitet jeden Datenzugriff über `IGovernedTreeQueryService`; einen Weg an den Prüfungen vorbei gibt es nicht. Die Lebensdauer der `JsonElement`-Werte ist korrekt (`Clone()` vor dem Dispose).

## Muss vor dem Merge behoben werden

| ID | Befund | Fundstellen | Behebung |
|---|---|---|---|
| R-SQL-1 ✔ | Seit SQL2-3 wirft der Service ohne Datenquellen-Executor `NotSupportedException`, und `_environment == null` gilt als Produktion. Mehrere Tests bauen den Service genau so und erwarten erfundene Zeilen. Sicher betroffen: `PipelineAndInfrastructureSecurityTests.cs:81`, `DataPathSecurityTests.cs:389` und `:523`, `SecurityReview20261002WebSqlTests.cs:458`, `SqlDataSourceExecutorTests.cs:331`. Wahrscheinlich auch `DataPathSecurityTests.cs:434`. | `GatewayExecutionService.cs:155-159`, `SqlDataSourceExecutor.cs:99-106` | Tests auf einen injizierten Test-Executor oder ein Development-Environment umstellen. Neuer Test: Environment `null` ergibt `NotSupported`. |
| R-POL-2 ✔ | Hat ein Mandant eigene Casbin-Regeln, werden die globalen `*`-Regeln für ihn übersprungen (`else if`), auch globale Verbote. Das gibt Zugriff frei, der verboten sein sollte. | `CasbinEnforcementService.cs:283-296` | Für die Auswertung Mandanten- und `*`-Regeln vereinigen, mindestens für Deny. |
| R-POL-3 ✔ | Globales Neuladen ersetzt nur Mandanten, die in der neuen Datei stehen. Gestrichene Mandanten behalten ihre alten Allow- und RLS-Regeln bis zum Neustart. Außerdem: kein Lock gegen parallele Reloads, Enforcer und Regeln werden nicht atomar getauscht, laufende Auswertungen können nach `_decisionCache.Clear()` alte Entscheidungen bis zu 60 s cachen. | `CasbinEnforcementService.cs:1117-1132` | Den Zustand als einen Snapshot (`Dictionary<tenant,(Enforcer,Rules)>`) per `Interlocked.Exchange` tauschen und gestrichene Mandanten entfernen. Last-known-good nur bei Parse-Fehler oder leerer Datei. Policy-Epoch in den Cache-Key aufnehmen. |
| R-POL-1 | Ein leeres Mandantenfeld in der globalen Datei wird zu `*`. `IsAllowRuleMatch` und der Casbin-Matcher verlangen aber exakt denselben Mandanten, Wildcard-Allows greifen daher nie. Gleichzeitig liefert `HasPolicies()` für jeden Mandanten true, sobald `*`-Regeln existieren. Ein globales Deny sperrt so jeden Mandanten ohne eigene Allows komplett aus. | `CasbinEnforcementService.cs:73, 99-107, 474-478, 1067, 1117-1125` | Wildcard-Semantik festlegen: leeres Feld ablehnen (`FormatException`) oder `*` in Matcher und `IsAllowRuleMatch` als „alle Mandanten“ behandeln. |
| R-GQL-1 ✔ | Maskierte Spalten kommen als Text-Literal (`'***'`) bzw. HMAC-String an, der Resolver wandelt aber nach dem Katalogtyp um. Int, Long, Float und Decimal enden in `FormatException` (Feld-Fehler). Boolean liefert **`false`**, also einen plausiblen Falschwert. | `CatalogGraphQlTypeModule.cs:305-321`, `TreeSqlCompiler.cs:170-181` | Ist der JSON-Wert keine Zahl bzw. kein Bool, `null` mit Fehlercode `MASKED` liefern, oder maskierbare Spalten im Schema als String führen. Test mit maskierter Int- und Bool-Spalte, auch HMAC. |
| R-GQL-2 | `usedTypeNames` kennt nur Objekttypnamen, nicht `{Typ}_filter` und `{Typ}_order_by`. Die Tabellen `x` und `x_filter` sowie Spalten `and`, `or`, `not` erzeugen doppelte Namen. Dann startet der Executor nicht, und der ganze `/graphql`-Endpunkt fällt aus. | `CatalogSchemaModel.cs:81-92, 219-220`, `CatalogGraphQlTypeModule.cs:70-79` | Alle abgeleiteten Namen in einem gemeinsamen Namensraum eindeutig machen. `and`, `or` und `not` reservieren. Fehler je Tabelle loggen und die Tabelle auslassen. |

## Weitere Befunde: GraphQL G1–G5 und GQL-1

| ID | Schwere | Befund | Fundstellen | Behebung |
|---|---|---|---|---|
| R-GQL-3 | hoch (Confidence mittel) | `GovernedTreeQueryService` ist scoped. Bei Queries über graphql-ws gilt der Scope der ganzen Verbindung (bis 8 h). Widerrufe greifen dann erst bei neuer Verbindung, und weitere Audit-Einträge werden unterdrückt. | `GovernedTreeQueryService.cs:55-58, 207-216, 229`, `CatalogGraphQlTypeModule.cs:367` | Memo und Audit pro Operation führen (z. B. `ContextData`) oder Queries über WebSocket ablehnen. |
| R-GQL-4 | mittel | Kein Gesamt-Zeilenbudget über alle Ebenen (Wurzel bis 5.000, jede Ebene bis 10.000 je Elternzeile, Tiefe 5) und kein `MaxOffset`. SQL2-7 ist damit offen. | `GraphQlTreeBuilder.cs:42-69, 104-111` | Produkt der Limits gegen ein Budget prüfen, `MaxOffset` einführen, verschachteltes `first` wie im Cost-Analyzer begrenzen. |
| R-GQL-5 | mittel | `offset` an Listenrelationen wird angeboten, aber still ignoriert (OFFSET nur an der Wurzel). | `CatalogGraphQlTypeModule.cs:122`, `GraphQlTreeBuilder.cs:58-64`, `TreeSqlCompiler.cs:397-418` | Argument entfernen oder verschachtelt `offset > 0` mit `INVALID_QUERY` ablehnen. |
| R-GQL-6 | mittel | Katalog aufzählbar: Ein unbekanntes Feld ist ein Validierungsfehler (`INTERNAL_SERVER_ERROR`), eine gesperrte Spalte `INVALID_QUERY`, eine gesperrte Tabelle `ACCESS_DENIED`. Ob `GET /graphql?sdl` trotz `DisableIntrospection()` das SDL liefert, ist ungeprüft. | `CatalogSchemaModel.cs`, `ErrorSanitizingFilter.cs:94-110`, `GatewayApplicationBuilderExtensions.cs:381` | Unbekannt und gesperrt einheitlich melden, `EnableSchemaRequests = false`. |
| R-GQL-7 | mittel | Der Schema-Build ruft `GetRelationsForTableAsync` je Tabelle für alle anderen Tabellen erneut auf, das sind O(N²) DB-Abfragen. | `CatalogSchemaModel.cs:166-176` | Relationen einmal je Tabelle laden und beide Richtungen daraus bilden. |
| R-GQL-8 ✔ | mittel | GQL-1 ist teilweise behoben. Gemischte Identitäten sind ausgeschlossen. Die Subjektprüfung kann aber nie greifen, weil ein Mandantenwechsel schon vorher abgewiesen wird. `SecurityPrincipalContext.FromPrincipal` dupliziert `SecurityContextFactory`: SID-Ersatz durch Name bzw. `ANONYMOUS` (verletzt RV-02), `IsAuthenticated ?? true`, Client-IP ohne `IClientIpResolver`. | `WebSocketAuthInterceptor.cs:131-139, 143-146`, `SecurityPrincipalContext.cs:133, 165` | Bei HTTP-Anmeldung und abweichendem Subjekt immer abweisen, Token ohne SID abweisen, eine gemeinsame Kontext-Fabrik in Domain nutzen. |
| R-GQL-9 | niedrig | Regressionen: Cluster-Admin mit `X-Tenant-ID` bekommt bei `requestTableAccess` `FORBIDDEN`. Bei approve, reject und revoke gilt der Claim statt des gewählten Mandanten. Kerberos-Upgrade plus JWT wird bei unterschiedlichem SID-Format abgewiesen. | `MutationTypes.cs:193-212, 383-402, 552-571, 654-673`, `WebSocketAuthInterceptor.cs:122` | `SecurityPrincipalContext.TenantId` als Quelle, Claim nur zum Abgleich, Admin-Ausnahme einheitlich. |
| R-GQL-10 | mittel | Filterwerte: Datum ohne Offset wird als Server-Ortszeit gelesen, nicht parsebare Werte werden still zu String, SQLite bindet `DateTimeOffset` mit Leerzeichen statt `T`, Decimal läuft über `double`. | `GraphQlTreeBuilder.cs:334, 377, 380` | `AssumeUniversal`, Parsefehler als `INVALID_QUERY`, Format je Dialekt, Decimal aus `fv.Value` parsen. |
| R-GQL-11 | niedrig | Null-Semantik in `where`: `{col: null}` wird ignoriert, `isNull: null` wird `IS NOT NULL`, `or: []` heißt „kein Filter“. | `GraphQlTreeBuilder.cs:205, 222, 347-354` | Null bei Spalten- und Operatorobjekten ablehnen, `or: []` als false weitergeben, dokumentieren. |
| R-GQL-12 | niedrig | `TypesChanged` ist ein No-op: Katalogänderungen erst nach Neustart sichtbar. Ist die Governance-DB beim ersten Request weg, scheitert GraphQL komplett. | `CatalogGraphQlTypeModule.cs:37-41` | An Katalog-Epoch koppeln; bei Build-Fehler leeres Katalogschema statt Ausfall. |
| R-GQL-13 | niedrig | Im Schema, aber nie abfragbar: bereinigte Spaltennamen (`a-b`), Oracle-/Databricks-Tabellen (Compiler wirft `NotSupported`). Außerdem: Spalten nur mit Groß-/Kleinunterschied fallen zusammen, bei Selbstrelation fehlt die Gegenrichtung, `bit(n)`/`varbit` werden Boolean. | `CatalogSchemaModel.cs:75, 98-109, 168-172, 273` | Beim Build auslassen, Typzuordnung präzisieren. |
| R-GQL-14 | niedrig | Session-Init im Baumpfad übergibt `userSid: null` und parst den Provider erneut (`sql_server` fällt auf SQLite zurück). | `GovernedTreeQueryService.cs:267-274` | Overload mit `DatabaseDialect`, `rootAccess.UserSid.Value` übergeben. |
| R-GQL-15 | info | Mögliche Build-Fehler: `event EventHandler<EventArgs>? TypesChanged` (CS8612/CS8615 bei abweichender Nullability). Die Sortierrichtung fällt über `ToString()` still auf ASC zurück. | `CatalogGraphQlTypeModule.cs:37, 117` | Beim ersten Build prüfen; Richtung über `EnumValueNode`/`StringValueNode.Value` auswerten, sonst Fehler. |

## Weitere Befunde: SQL, Sitzungskontext, Prozeduren

| ID | Schwere | Befund | Fundstellen | Behebung |
|---|---|---|---|---|
| R-SQL-2 | mittel | SQL2-3 liefert 500 statt 501 (`NotSupportedException` ist im Handler nicht gemappt). WebSQL behandelt `_environment == null` weiter als Dev/Test. | `GatewayExecutionService.cs:158`, `SqlDataSourceExecutor.cs:105`, `GatewayExceptionHandler.cs:76-85`, `GovernedSqlExecutionService.cs:800` | Eigene Gateway-Exception mit 501, WebSQL angleichen. |
| R-SQL-3 | mittel | WebSQL setzt die Mandanten-GUCs in PostgreSQL für die ganze Sitzung statt nur die Transaktion. Mit `No Reset On Close=true` oder PgBouncer (Transaction-Pooling) kann der Kontext an fremden Verbindungen hängen bleiben. Betrifft DB-RLS als zweite Verteidigungslinie. | `GovernedSqlExecutionService.cs:860-867`, `DbSessionContextInitializer.cs:70` | WebSQL immer in einer Transaktion ausführen (lokale GUCs) und diese an `ExecuteDmlInTransactionAsync` übergeben. |
| R-SQL-4 | mittel | `SET TRANSACTION READ ONLY` im Row-Scope-Resolver ist weggefallen. | `SqlProcedureRowScopeResolver.cs:149-156` | `readOnly`-Parameter im Initializer, Test. |
| R-SQL-5 | mittel | Das WebSQL-Concurrency-Gate liefert 500 statt 429 ohne `Retry-After`, plus Error-Log je Anfrage. | `GovernedSqlExecutionService.cs:835`, `WebSqlEndpoints.cs:322-375` | `GatewayThrottledException` auf 429 mit `Retry-After` mappen, Test. |
| R-SQL-6 | mittel | Der Prozeduraufruf nutzt den zentralen Initializer nicht (PostgreSQL ohne `app.tenant_id`/`TimeZone`). | `MssqlProcedureInvoker.cs:60-90` | Auf `InitializeSessionAsync` umstellen (`XACT_ABORT`/`LOCK_TIMEOUT` separat), SID durchreichen. |
| R-SQL-7 | niedrig | SQL2-18 teilweise: Rollback im catch ist ungeschützt und kann die Original-Exception verdecken. Im Initializer wird bei Rollback-Fehler `DisposeAsync` übersprungen. | `SqlDataSourceExecutor.cs:353-358`, `GovernedTreeQueryService.cs`, `DbSessionContextInitializer.cs:61-66` | Rollback in try/catch mit Log, Dispose in `finally`. |
| R-SQL-8 | niedrig | SQL2-8 teilweise: Einzelne LOBs werden ohne `SequentialAccess` komplett geladen, `byte[]` wird ohne Base64-Faktor gezählt, es gilt `GraphQL.MaxResponseBytes`. | `MssqlProcedureInvoker.cs:137-163, 406-413` | `SequentialAccess` mit Restbudget wie `ReadRowsAsync`, Faktor 4/3, eigenes Limit, Test. |
| R-SQL-9 | niedrig | SQL-1: Fehlt eine Tabelle in der Map (z. B. Tenant-exempt), gilt die Spalte der zuerst gesehenen Tabelle (fail-closed, kann INSERTs fälschlich ablehnen). Kein Test mit `TenantId`. | `GovernedSqlExecutionService.cs:477-482, 629-630`, `IRlsPolicyProvider.cs:167-181` | Tests für INSERT/UPDATE mit `TenantId` in beiden Engines plus Multi-Table-Fall. |
| R-SQL-10 | niedrig | SQL2-1: Ohne Browse-Quellinfo (PostgreSQL, declared mode) wird eine als `clear` deklarierte berechnete Spalte jetzt entfernt. Fail-closed, aber undokumentiert. | `GovernedProcedureExecutionService.cs:515-560` | Test „maskiert + clear bleibt Mask“, ADR-018 ergänzen. |
| R-SQL-11 | niedrig | `new TenantId(tenantVal)` kann bei ungültigem Claim werfen (500). | `SqlDataSourceExecutor.cs:329` | Tenant vorher validieren, 403. |
| R-SQL-12 | info | Reste von `SingleQueryAstCompiler`: Option `SingleQueryPushdown` (`GatewayOptions.cs:38, 1223`), `Domain/Model/SqlAstModels.cs`, `docs/features/f-perf-09-single-query-pushdown.md`, Benchmark-Konfigurationen. `SqlFilterProvider` ist weiter registriert, aber ungenutzt. Gelöscht wurden nur die 17 `AstCompiler_*`-Tests. | | Entfernen. |
| R-SQL-13 | info | `GetSessionInitializationSql` wird nicht mehr aufgerufen, `Round4ParserGatewayTests.cs:297-299` prüft also toten Code. Für `DbSessionContextInitializer` gibt es keinen Test. | `GovernedSqlExecutionService.cs:1272` | Methode entfernen, Initializer testen. |

Unauffällig:
- **SQL Server `SESSION_CONTEXT`:** Er leakt nicht über den Pool (`sp_reset_connection`).
- **SQL-1:** Der Fix wirkt in beiden Rewritern (`RlsListener`, `AstSecurityVisitor`) für UPDATE, INSERT VALUES, INSERT SELECT und UNION.
- **Angepasste Tests:** In `BypassSemanticsAndDmlGuardrailTests`, `IntegrationGapATests` und `ColumnMaskingTests` wurde nichts aufgeweicht.

## Weitere Befunde: Richtlinien, Erweiterungen, Konfiguration

| ID | Schwere | Befund | Fundstellen | Behebung |
|---|---|---|---|---|
| R-POL-4 | mittel | `new TenantId("*")` wirft (Regex), sobald `OnPolicyReloaded`/`OnPolicyReloadFailed` abonniert ist. Der Service hat keinen Logger, Reload-Fehler bleiben still. | `CasbinEnforcementService.cs:1130-1131, 1170, 1175`, `TenantId.cs:13` | Event-Signatur auf `string`, `ILogger` injizieren, Fehler loggen, Metrik/Health. |
| R-POL-5 | mittel | Die Startvalidierung prüft nur, dass die Dateien existieren und nicht leer sind. Parse-Fehler fallen erst beim ersten Request auf. Ein fehlender `ModelPath` fällt im Konstruktor still auf das Default-Modell zurück. | `GatewayServiceCollectionExtensions.cs:374-384, 995-1023`, `CasbinEnforcementService.cs:59-82` | In `ValidateGatewayOptions` Modell und Policy probeweise laden oder den Service nach `Build()` eager auflösen. |
| R-POL-6 | mittel | Die vier Load-Methoden sind Default-Interface-Methoden mit leerem Body. Andere Implementierungen ignorieren das Laden still (fail-open). | `IPolicyEnforcementService.cs:33-49` | Default `throw new NotSupportedException()` oder abstrakt lassen und Fakes anpassen. |
| R-POL-7 | niedrig | `GetOrCreateEnforcer` gibt für unbekannte Mandanten den `*`-Enforcer zurück. Mutationen landen dort (Rollen-Leak über Mandanten, heute ohne produktiven Aufrufer). `g`-Regeln der Datei gelten global. | `CasbinEnforcementService.cs:110-127, 1103, 1119-1122` | Wildcard-Enforcer nie für Mutationen zurückgeben, `g`-Scope dokumentieren. |
| R-POL-8 | info | Default ist jetzt `Enabled=false`: Casbin ist inert, ohne Hinweis. `Enabled=false` mit gesetztem `PolicyPath` wird still ignoriert. Die Doku verspricht weiter Enforcement auf allen Pfaden. | `GatewayOptions.cs:1113` | Startup-Warnung, Doku korrigieren. |
| R-POL-9 | niedrig | POL-15 nicht behoben: PostgreSQL setzt `grantee_sid` nur bei `GranteeType.User`. SQLite macht es richtig. | `PostgreSqlGovernanceRepository.Consent.cs:1395` | `granteeSid` für alle außer Role setzen. |
| R-POL-10 | niedrig | `Table.IsHighlySensitive` kennt nur HIGH, nicht RESTRICTED/SECRET. Unbekannte Klassifikationen gelten hier als niedrig, im Ratchet als HIGH. | `ConsentApprovalPolicy.cs:122-129`, `TableModels.cs:37` | Eine zentrale Funktion, unbekannt = hoch. |
| R-POL-11 | niedrig | POL-3 ist dreifach dupliziert (beide Repositories und GraphQL-Approve). | PG `Consent.cs:1418-1456`, SQLite `Consent.cs:816-854`, `MutationTypes.cs:440-455` | Gemeinsame Snapshot-Funktion. |
| R-POL-12 | niedrig | Lokale `IsHmacRule` in den Lakehouse-Executoren kennt `HASH` nicht; die Ausgabe weicht vom SQL-Pfad ab. | `LakehouseDataSourceExecutor.cs:380-382`, `DeltaLakeDataSourceExecutor.cs:240-242` | `GatewayExecutionService.ScopeRuleForTenant` verwenden. |
| R-POL-13 | niedrig | POL-7 vollständig. `group:x#member` funktioniert weiter. Alt-Tupel mit leeren Feldern lassen sich nur noch über `ClearTenantTuples` löschen (sind aber inert). | `RebacEndpoints.cs`, Stores, `ZanzibarRebacEvaluator.cs` | Optional Bereinigungsskript. |
| R-EXT-1 | mittel | EXT-1 teilweise: Approve setzt den Status vor der Typ-Prüfung auf `Approved`. RLS_FILTER/CASBIN_ROLES werden dann nur geloggt, ebenso ein Ratchet-Abbruch. Der Freigeber glaubt, der Filter sei aktiv. | `DbtMetadataIngestionService.cs:437-449, 460-470` | Für diese Typen Approve ablehnen oder eigener Status (`RequiresManualPolicy`, `Superseded`), alternativ echt anwenden. |
| R-EXT-2 | niedrig | Das neue `TableMetadata` übernimmt nur fünf Eigenschaften; weitere gehen beim Upsert verloren, falls persistiert. | `DbtMetadataIngestionService.cs:474-481` | `tableMeta with { ColumnMaskingRules = … }`. |
| R-DEP-1 | mittel | Die 32-Byte-Prüfung greift nur, wenn der Referenzname `hmac`, `audit` oder `forwardauth` enthält. Präfix-Aliase (`audit:*`, `forwardauth:*`) lösen fehlende Secrets still auf ein anderes Secret auf. Der ForwardAuth-KeyVaultRef wird erst pro Request geprüft. | `DefaultEnvironmentSecretProvider.cs:166-185`, `ForwardAuthAuthenticationHandler.cs:129` | Längenprüfung an den Verbrauchern beim Start, Aliase auf exakte Namen beschränken. |
| R-DEP-2 | info | Geprüft werden UTF-8-Bytes des Textes, nicht dekodierte Schlüsselbytes (32 Hex-Zeichen = 16 Byte Entropie). Der PoC-Schlüssel und `generate-env.sh` bestehen. Nur Development ist ausgenommen. | `ColumnMaskingProvider.cs:47-52` | Format dokumentieren oder dekodieren. |
| R-ERR-1 | niedrig | `INVALID_QUERY` steht auf der Whitelist; Spalten- und Tabellennamen gehen in Produktion an den Client (Enumeration). | `ErrorSanitizingFilter.cs:19-35`, `CatalogGraphQlTypeModule.cs:413` | Generischer Text oder nur geprüfte Meldungen durchlassen. |
| R-API-1 | info | Deployments mit `Itsm.LegacyGlobalWebhookSecret=true` starten nicht mehr. Der Test `SEM_ItsmLegacyGlobalWebhookSecret_IsNoLongerDanger` (frühere bewusste Entscheidung) wurde entfernt. | `GatewayOptions.cs:215-217` | Migrationshinweis in Release Notes oder Runbook, Begründung festhalten. |
| R-API-2 | niedrig | API-4 behoben. Der Claims-Fallback in `FinOpsBudgetMiddleware` ist praktisch tot und kann bei ungültigem Claim 500 werfen. | `FinOpsBudgetMiddleware.cs:47-82` | Fallback entfernen. |

## Status der Befunde aus dem Security-Review

| Status | Befunde |
|---|---|
| Behoben | SQL-1, SQL2-1, POL-2, POL-3, POL-4, POL-7, EXT-2, API-1, API-4 |
| Teilweise | POL-1 (R-POL-1 bis -6), GQL-1 (R-GQL-8/9), SQL2-3 (R-SQL-1/2), SQL2-8 (R-SQL-8), SQL2-18 (R-SQL-7), EXT-1 (R-EXT-1), DEP-6/POL-14 (R-DEP-1) |
| Offen | POL-15, SQL2-7, SQL2-11, SQL2-12, SQL2-13 |

| Spec-Punkt | Status | Offen |
|---|---|---|
| G1 Schema aus dem Katalog | teilweise | R-GQL-2, -7, -12, -13 |
| G2/G4 Auswahlbaum, Filter, Sortierung | teilweise | R-GQL-5, -10, -11 (Variablen, Aliase, Fragmente, `@skip`/`@include` sind korrekt) |
| G3 JSON durchreichen, Fehlercodes | teilweise | R-GQL-1 |
| G5 Restpunkte | teilweise | R-GQL-3, R-SQL-12 |

## Fehlende Tests

Der Commit bringt nur `CatalogGraphQlSchemaTests` und `CatalogSchemaModelTests` neu mit. Es fehlen:

1. **SQL-1:** UPDATE und INSERT mit Spalte `TenantId`, in beiden Rewritern, plus Multi-Table-Fall.
2. **SQL2-1:** Maskierte Spalte, als `clear` deklariert, bleibt maskiert.
3. **SQL2-3, SQL2-8, Gate:**
   - Environment `null` ergibt `NotSupported` bzw. 501.
   - Byte-Budget bei Prozeduren.
   - WebSQL-Drosselung ergibt 429.
4. **Sitzungsinitialisierung:**
   - Statement-Text, lokal und Sitzung, SQL Server `read_only`.
   - Rollback bei Init-Fehler.
   - SID im Baumpfad, Provider-Alias `sql_server`.
5. **Casbin (POL-1):**
   - `Enabled=true` mit fehlendem, nicht vorhandenem oder leerem Pfad ergibt `ValidationException`; `Enabled=false` ergibt keinen Fehler.
   - Ungültiges Modell bzw. ungültige Policy bricht den Start ab.
   - Ende-zu-Ende: Datei laden, Deny wirkt.
   - Mandanten-Isolation, Wildcard-Allow und -Deny, Scope der `g`-Regeln.
   - Hot Reload: Änderung wird übernommen, gestrichener Mandant wird entfernt, fehlerhafte Datei ergibt Last-known-good plus Log.
6. **POL-2:** `StatusAfterApproval` für HIGH, RESTRICTED und SECRET (auch Kleinschreibung, null). In beiden Repositories ergibt HIGH ohne `requires_four_eyes` den Status `PENDING_SECOND_APPROVAL`.
7. **POL-3:** `ActivateConsentAsync` legt Spaltenregeln für alle Spalten an, gleich wie im GraphQL-Pfad. Eine später hinzugefügte Spalte ist nicht Clear.
8. **POL-4:** Gleicher Wert ergibt in Mandant A und B verschiedene Pseudonyme, gleich wie im SQL-Pfad, kein Doppel-Masking.
9. **POL-7:**
   - API antwortet bei leeren Feldern mit 400, der Store wirft.
   - `GetTuplesAsync("")` liefert nichts.
   - Ein leerer User bzw. Parent gewährt nichts.
   - Das Userset `group:x#member` funktioniert weiter.
10. **POL-15:** Die PostgreSQL-Aktivierung mit Gruppe bzw. Service-Principal setzt `grantee_sid`.
11. **EXT-1, EXT-2:**
    - Approve von RLS_FILTER/CASBIN_ROLES schreibt keine Maske.
    - Ein schwächerer Vorschlag überschreibt keine stärkere Maske.
    - Batch und Webhook behalten `SourceName`.
12. **API-4:** Ein anonymer Request wird nicht verbucht. Der Mandant kommt aus `SecurityPrincipalContext`.
13. **DEP-6:**
    - In Produktion: 31 Byte werfen, 32 Byte sind ok, Development ist ok.
    - `ColumnMaskingProvider` wirft bei zu kurzem Schlüssel.
    - ForwardAuth unter 32 Byte ergibt eine `ValidationException`.
14. **GQL-1:**
    - Token-Mandant ≠ Upgrade-Mandant wird abgewiesen.
    - Gleicher Mandant mit anderem Subjekt: erwartetes Verhalten testen.
    - Token ohne SID.
    - `Items` und `SecurityPrincipalContext` zeigen nach Accept auf die Token-Werte.
    - Token-only-Client wird akzeptiert.
    - Die periodische Revocation-Prüfung läuft auf dem Token-Principal.
    - Mutationen mit abweichendem Mandanten, auch Cluster-Admin mit `X-Tenant-ID`.
15. **GraphQL-Schema:**
    - Kollisionen `x`/`x_filter` und Spalten `and`/`or`/`not`.
    - Bereinigte Namen, Reverse-Namen, Selbstrelation, Oracle/Databricks auslassen, Typzuordnung.
16. **GraphQL-Executor:**
    - Alle Operatoren, `in` mit Limit 1000.
    - Variablen, Aliase, Fragmente, `@skip`/`@include`.
    - `first` über dem Maximum, `offset` (auch verschachtelt).
    - Maskierte Int- und Bool-Spalte.
    - Fehlercodes `RESPONSE_TOO_LARGE`/`UNAVAILABLE` bleiben in Produktion erhalten.
17. **Laut Übergabe:**
    - Ende-zu-Ende mit SQLite über `WebApplicationFactory`.
    - Gleiche Zeilen wie OData und WebSQL.
    - Zwei Wurzelfelder derselben Tabelle mit verschiedenen Spalten.
    - Zwei Operationen über dieselbe WebSocket-Verbindung nach Consent-Widerruf.

## Empfohlene Reihenfolge

1. `dotnet build` und `dotnet test` ausführen, R-SQL-1 beheben (Tests anpassen, Test für Environment `null`).
2. Casbin: R-POL-2, R-POL-3, R-POL-1, dann R-POL-4 bis -6, jeweils mit Tests.
3. GraphQL: R-GQL-1, R-GQL-2, R-GQL-3, dann R-GQL-4 bis -8.
4. Tests für die bereits behobenen Befunde nachziehen (Liste oben, Punkte 1 bis 14).
5. Restliche mittlere und niedrige Punkte, Aufräumen (R-SQL-12, R-SQL-13).

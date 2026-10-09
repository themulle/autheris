# Security-Review Gesamtprojekt Autheris (2026-10-09)

**Dokument-ID:** `SEC-REVIEW-AUTHERIS-2026-10-09`
**Datum:** 2026-10-09
**Prüfstand:** Branch `feat/ast-target-dialect-generator`, `HEAD f6e756f` (während des Audits kamen `864e4ef`, `0ae77de`, `a63d3d2`, `f6e756f` hinzu; die Befunde wurden gegen diese Stände geprüft)
**Methode:** Statische Code-Analyse in 8 parallelen Teil-Audits (Auth/Ingress, Endpoint-Autorisierung, SQL-Engine/RLS/Masking, OLAP/Procedures/Connectors, Policy/PDP/Filter, GraphQL/MCP, Extensions, Infrastruktur/Deploy/Supply-Chain). Jeder Befund wurde bis zum Aufrufer, zur Registrierung und zum Konfig-Default verfolgt. Einzelne Befunde (SG-01, SG-02, SG-05, SG-06, SG-24) wurden zusätzlich per Test-Harness in der Scratchpad bzw. gegen einen Wegwerf-PostgreSQL-16 bzw. eine echte DuckDB-1.5.6-Instanz nachgestellt. Das Repository wurde dabei nicht verändert.
**Abgrenzung:** Befunde aus `2026-10-08-security-review-letzte-15h.md` (SR15-01 … SR15-52) werden nur dann aufgeführt, wenn sie noch offen oder unvollständig behoben sind; die ID wird dann referenziert.

Konfidenz: **bestätigt** = Code-Pfad vollständig nachvollzogen bzw. reproduziert; **plausibel** = Code-Pfad nachvollzogen, Ausnutzbarkeit hängt von Konfiguration/IdP/Betriebsmodell ab.

---

## 1. Zusammenfassung

| Schwere | Anzahl | IDs |
|---|---|---|
| Kritisch | 0 | – |
| Hoch | 6 | SG-01 … SG-06 |
| Mittel | 15 | SG-07 … SG-21 |
| Niedrig | 18 | SG-22 … SG-39 |

**Wichtigste Risiken:**

1. **DuckDB-OLAP-Pfad (SG-01, SG-02):** Jeder angemeldete Nutzer kann die Spill-Dateien fremder OLAP-Sessions lesen und den Gateway-Prozess mit einer Abfrage per OOM beenden. Für beides braucht er keine Tabellenrechte. `DuckDbOlap.Enabled` ist per Default `true`.
2. **Vier-Augen- und Identitätsmodell (SG-03, SG-04, SG-09):**
   - Ein zweiter Admin kann einen fremden, noch ausstehenden Filter umschreiben und danach selbst freigeben.
   - `name`, `preferred_username` und die MCP-Client-ID werden als Nutzer-SIDs für Consent-Matching behandelt.
3. **AST-Rewriter bei DML (SG-05, SG-06):** Ein `OR` im LIKE-Muster hebelt den Row-Filter bei UPDATE/DELETE aus, und eine qualifizierte Wildcard liest maskierte Spalten. Beides ist auf PostgreSQL 16 reproduziert. Es betrifft nur `WebSql.SqlRewriterEngine=AstCompiler`; der Default `LegacyTokenStream` ist nicht betroffen. Es ist trotzdem ein Blocker, bevor der AST-Pfad Default wird, was das Ziel dieses Branches ist.
4. **Ingress (SG-07, SG-08):**
   - `POST /v1/statement` hat keinen CSRF-Schutz, obwohl Kerberos-Credentials ambient mitgesendet werden.
   - Entra-App-only-Tokens beliebiger Apps im Tenant werden per Default als ReadWrite akzeptiert.

**Unvollständig behobene SR15-Befunde:** SR15-05 und SR15-10 (SG-05, SG-06), SR15-08 (SG-15), SR15-12 (SG-14), SR15-16 (SG-27), SR15-18 (SG-33), SR15-19 (SG-10), SR15-26 (SG-38), SR15-31 (SG-32), SR15-37 (SG-36), SR15-38 (SG-35), SR15-51 (SG-16, SG-25), SQL2-9 (SG-24).

**Als behoben verifiziert:** SR15-01, -02, -03, -04, -06 (Kernmechanik), -13, -17, -20, -21, -22, -24, -25, -27 (Code; Rotation und History-Rewrite offen als DEP-12), -28, -29, -30, -32, -33, -34, -35, -36, -39, -40, -41, -42, -47, -48, -49, -52.

---

## 2. Befunde – Hoch

### SG-01 OLAP: Cross-Session-Datenleck über das gemeinsame DuckDB-Temp-Verzeichnis
- **Schwere:** Hoch | **Konfidenz:** bestätigt (reproduziert mit DuckDB 1.5.6)
- **Fundstellen:** `src/Autheris.Application/Olap/DuckDbOlapEngine.cs:72-80` (Setup), `:288-305` (Keyword-Denylist)
- **Beschreibung:**
  - Mit `enable_external_access=false` setzt DuckDB das Temp-Verzeichnis automatisch auf `allowed_directories`; per Default ist das `<cwd>/.tmp`.
  - Alle In-Memory-Instanzen im Prozess spillen in dasselbe Verzeichnis, unverschlüsselt (`temp_file_encryption=false`).
  - Tabellenfunktionen wie `read_blob`, `read_text` und `glob` stehen nicht auf der Denylist.
- **Angriff:** Ein beliebiger authentifizierter Nutzer pollt `POST /api/v1/olap/query` mit `{"sql":"SELECT filename, hex(content) FROM read_blob('.tmp/*')"}`. Sobald eine fremde Session (auch aus einem anderen Tenant) über `MaxMemory` hinaus spillt, liest er deren bereits RLS-gefilterte und maskierte Daten mit, die aber für das Opfer bestimmt sind. Im Test tauchte ein Marker-Wert der Opfer-Session im Klartext auf.
- **Nebeneffekte:**
  - Gleichnamige Temp-Dateien mehrerer Sessions können kollidieren.
  - `max_temp_directory_size` liegt bei 90 % des freien Plattenplatzes, das erlaubt Disk-Fill.
- **Fix:**
  - Vor `enable_external_access=false` pro Session ein eigenes Temp-Verzeichnis setzen und danach löschen, oder Spilling abschalten (`SET temp_directory=''`).
  - `temp_file_encryption=true` setzen und `max_temp_directory_size` begrenzen.
  - Datei-Tabellenfunktionen verbieten (`read_*`, `glob`, `sniff_csv`, `parquet_*`, `duckdb_settings`), am besten über eine Allowlist.

### SG-02 OLAP: Speicher-DoS bis zum Prozessabsturz (Ergebnis-Materialisierung und unbegrenzte `tableNames`)
- **Schwere:** Hoch | **Konfidenz:** bestätigt (gemessen im verkleinerten Maßstab)
- **Fundstellen:** `DuckDbOlapEngine.cs:113-127`, `src/Autheris.Api/Endpoints/DuckDbOlapEndpoints.cs:25-28, 39, 150-243, 254-279`, `GatewayOptions.cs:1622-1632`
- **Beschreibung:**
  1. Ergebniszeilen werden vollständig als .NET-Objekte materialisiert. Es gibt nur ein Zeilenlimit (50 000), kein Byte-Limit. `max_memory` begrenzt nur DuckDBs Buffer-Manager, nicht die .NET-Kopien.
  2. `TableNames` ist weder begrenzt noch dedupliziert. Pro Eintrag werden bis zu 250 000 Zeilen in den Managed Heap gestagt, bevor DuckDB startet.
  3. Die Zahl gleichzeitiger OLAP-Sessions ist nicht begrenzt (je 1 GB, 2 Threads, 60 s).
- **Angriff:**
  - Erste Variante: `SELECT lpad('', 1000000, 'x') FROM (VALUES…) a,(VALUES…) b,(VALUES…) c`. Das braucht keinerlei Tabellenrecht. Bei 600 Zeilen wurden bereits 1,1 GB Heap und 2,4 GB Working Set gemessen; mit den Default-Limits wären es rund 100 GB.
  - Zweite Variante: `{"sql":"select 1","tableNames":["t","t",…]}` mit tausenden Einträgen im Rahmen des 2-MB-Bodys.
  - In beiden Fällen stürzt das Gateway per OOM ab, betroffen sind alle Tenants.
- **Fix:**
  - Byte-Budget beim Lesen, analog zu `MaxResponseBytes` und `BoundedValueReader` bei Procedures.
  - Globaler `SemaphoreSlim` für OLAP-Sessions.
  - `TableNames` auf maximal etwa 10 begrenzen und deduplizieren, dazu ein Gesamtbudget für gestagte Zeilen bzw. Bytes.
  - Eigenes Rate-Limit für den Endpoint.

### SG-03 Virtuelle Filter: Vier-Augen-Umgehung durch Bearbeiten und anschließendes Freigeben
- **Schwere:** Hoch | **Konfidenz:** bestätigt
- **Fundstellen:**
  - `src/Autheris.Application/VirtualFilters/VirtualFilterAdministrationService.cs:194, 215` (Filter) und `:386, 407` (Profile): `CreatedBy = existing?.CreatedBy ?? actor.Sid`.
  - `GetSubmitterSid` `:806-844`; Approve `:253`, `:445`.
- **Beschreibung:**
  - Speichert ein zweiter Admin ein noch ausstehendes Element, bleibt der ursprüngliche Ersteller als Einreicher stehen.
  - Approve vergleicht nur mit `CreatedBy`; `UpdatedBy` wird nie geprüft.
  - Verglichen wird außerdem nur die einzelne `actor.Sid`, nicht `GetUserIdentifiers()`.
- **Angriff:**
  1. Alice reicht Filter X ein.
  2. Bob (FilterAdmin) überschreibt X, z. B. mit `supersedes`, `uncovered: skip` oder einer Orakel-Subquery.
  3. Bob gibt X selbst frei. Die Selbstfreigabe-Sperre greift nicht, weil Alice als Einreicherin gilt.
  - Kombiniert mit SG-14 genügt eine Person, um einen Pflichtfilter auszuhebeln.
- **Fix:**
  - Bei jedem Speichern eines ausstehenden Elements den Einreicher auf den letzten Bearbeiter setzen, oder die Menge aller Bearbeiter speichern.
  - Approve ablehnen, wenn ein Identifier des Freigebenden (`GetUserIdentifiers()`) unter den Bearbeitern ist.

### SG-04 Freitext-Claims (`name`, `preferred_username`, `upn`) gelten als Grantee-Identität (Consent-Impersonation) [BEHOBEN]
- **Status:** Behoben
- **Schwere:** Hoch | **Konfidenz:** plausibel (abhängig vom IdP)
- **Fundstellen:**
  - `src/Autheris.Domain/Common/Sid.cs:41-57` (`UserIdentifierClaimTypes`), `:97-103` (`GetAllUserSids`)
  - `TableAccessPolicy.cs:70` (`ForPrincipal`), `:343-364` (`ResolveConsentAsync`)
  - `GranteeMatcher.cs:27`
  - `GatewayExecutionService.cs:489, 930`
- **Beschreibung:** Seit dem Fix zu SR15-46 werden Consents für alle Identifier aus `GetUserIdentifiers()` geladen und gematcht. Darunter sind auch vom Nutzer veränderbare Anzeige- bzw. Benutzernamen.
- **Angriff:** In einem IdP mit Self-Service-Profil (Keycloak-Account-Console, Auth0, B2C, generisches OIDC) setzt der Angreifer seinen `name` bzw. `preferred_username` auf die SID oder OID des Opfers. Er erbt damit alle User-Consents des Opfers.
- **Fix:**
  - `GetAllUserSids` auf unveränderliche, vom IdP ausgestellte IDs beschränken: PrimarySid, objectSid, onprem_sid, oid, sub, NameIdentifier.
  - `name`, `upn` und `preferred_username` nur für die Selbstfreigabe-Prüfung (`GetUserIdentifiers`) verwenden.
  - Siehe auch SG-09.

### SG-05 AST-Rewriter: Ein `OR` im LIKE-Muster bzw. in ESCAPE hebelt den Row-Filter bei UPDATE/DELETE aus (SR15-05 unvollständig) [BEHOBEN]
- **Status:** Behoben
- **Schwere:** Hoch (bedingt: nur mit `WebSql.SqlRewriterEngine=AstCompiler`, DML aktiv und Writer-Rolle) | **Konfidenz:** bestätigt (PostgreSQL 16 und SQLite)
- **Fundstellen:**
  - `src/TrinoSqlEngine/Ast/Generators/SqlDialectGeneratorBase.cs:420-428`: `LikeExpression` gibt Pattern und Escape ungeklammert aus.
  - `:722-738`: `NeedsParentheses` klammert nur `BinaryExpression` und `Between`.
  - `AstSecurityVisitor.cs:270-272, 346-349`: DML-Filter als `BinaryExpression(userWhere, And, rls)`.
- **Beschreibung:**
  - Der Builder verwirft die Klammern der Nutzereingabe.
  - Ein `OR` im Muster-Ausdruck erscheint im erzeugten SQL ungeklammert.
  - Der angehängte Mandanten- bzw. RLS-Filter bindet deshalb nur noch an den letzten OR-Zweig.
  - Reproduziert: Ein DELETE löschte die Zeilen aller Mandanten.
- **Fix:**
  - DML-Filter immer als `(userWhere) AND (rls)` ausgeben, über einen expliziten Klammerknoten.
  - Muster und Escape über `GeneratePredicateOperand` ausgeben.
  - `NeedsParentheses` um `Like`, `InList`, `IsDistinctFrom`, `Subscript` und `Unary` erweitern.
  - Regressionstest pro Dialekt.

### SG-06 AST-Rewriter: Maskierte Spalten des DML-Ziels über eine qualifizierte Wildcard lesbar (SR15-10 unvollständig) [BEHOBEN]
- **Status:** Behoben
- **Schwere:** Hoch (bedingt wie SG-05) | **Konfidenz:** bestätigt (PostgreSQL 16; SQLite nicht verwertbar; SQL Server nicht getestet)
- **Fundstellen:**
  - `AstSecurityVisitor.cs:620-707` (`EnsureNoMaskedColumnReferences` prüft nur `ColumnReference` und `UsingJoinCondition`), `:709ff` (`PushChildren`).
  - `GovernedSqlExecutionService` `ReferencesColumn` vergleicht nur Spaltennamen.
- **Beschreibung:**
  - Die DML-Zieltabelle wird nicht in eine maskierende Subquery gekapselt.
  - Eine korrelierte `ziel.*` in einer abgeleiteten Tabelle mit umbenannten Spalten erreicht deshalb die Rohwerte.
  - Damit lässt sich Klartext in eine lesbare Spalte kopieren, oder die Zahl der betroffenen Zeilen dient als Orakel.
  - Der Legacy-Rewriter lehnt dieselbe Eingabe ab („Whole-row reference …“).
- **Fix:**
  - `WildcardSelectItem` mit Qualifier auf Ziel bzw. Alias in die Prüfung aufnehmen.
  - Grundsätzlicher: Korrelierte Referenzen auf das DML-Ziel in Subqueries mit FROM ablehnen, wenn die Zieltabelle maskierte Spalten hat.

---

## 3. Befunde – Mittel

### SG-07 CSRF auf `POST /v1/statement` (Trino-API nimmt `text/plain` an) [BEHOBEN]
- **Status:** Behoben
- **Konfidenz:** Lücke bestätigt; dass Kerberos automatisch mitgesendet wird, ist plausibel.
- **Fundstellen:**
  - `src/Autheris.Api/Extensions/GatewayApplicationBuilderExtensions.cs:163-175`: CSRF-Prüfung nur für GraphQL, `/api`, `/odata` und MCP.
  - `WebSqlEndpoints.cs:70-73, 134-139`.
  - Ebenfalls ungeschützt: `POST /v1/{prefix}/namespaces/{ns}/tables/{t}/credentials` (Iceberg).
- **Beschreibung:** `/v1/...` fällt unter keine CSRF-Regel. `text/plain` ist ein „simple request“, der Browser schickt also keinen Preflight. Der Code selbst stuft ambiente Negotiate-Credentials als Bedrohung ein (SEC M-05).
- **Angriff:** Eine präparierte Seite sendet `fetch(".../v1/statement",{method:"POST",mode:"no-cors",credentials:"include",headers:{"Content-Type":"text/plain"},body:"UPDATE …"})`. Das SQL läuft mit der Identität des Opfers:
  - Bei `AllowDml` und einer Writer-Rolle führt das zu Datenmanipulation.
  - Sonst bleiben Last, belegte Statement-Slots und Audit-Einträge, die dem Opfer zugeschrieben werden.
- **Fix:**
  - CSRF-Logik umkehren: alle nicht sicheren Methoden prüfen, Ausnahmen explizit auflisten.
  - Auf `/v1/statement` den Header `X-Trino-User` verlangen.

### SG-08 Entra: App-only-Tokens ohne Rollen- und Client-Prüfung werden als ReadWrite akzeptiert; ID-Tokens gelten als Access-Tokens [BEHOBEN]
- **Status:** Behoben
- **Konfidenz:** plausibel (abhängig von „Assignment required“ in Entra)
- **Fundstellen:** `GatewayOptions.cs` (`EntraIdAuthOptions.AppOnlyTokens = ReadWrite`), `src/Autheris.Api/Security/EntraTokenPolicy.cs:66-80`, `GatewayServiceCollectionExtensions.cs:800-857`
- **Beschreibung:**
  - Ohne Assignment-Pflicht kann jeder Service Principal im Tenant ein Client-Credentials-Token für die API holen, auch zugestimmte Multi-Tenant-SaaS-Apps.
  - Es gibt keine `azp`/`appid`-Allowlist und keine Pflicht für einen `roles`-Claim.
  - Ein ID-Token mit `aud=ClientId` wird als Vollzugriff ohne `scp` akzeptiert und nie als read-only markiert.
- **Fix:**
  - Default auf `ReadOnly` oder `Deny` setzen und `AllowedClientIds` einführen.
  - Für App-only-Tokens `roles` verlangen.
  - Tokens ohne `scp` und ohne `roles` ablehnen, ebenso ID-Tokens.
  - `IncludeErrorDetails=false` setzen.

### SG-09 MCP: Der synthetische Principal setzt `sub`/`NameIdentifier` auf die OAuth-Client-ID (Confused Deputy) [BEHOBEN]
- **Status:** Behoben
- **Konfidenz:** plausibel (nur bei Tokens mit `client_id`-Claim, z. B. ADFS, ForwardAuth oder generischer IdP)
- **Fundstellen:** `src/Autheris.Api/Endpoints/McpEndpoints.cs:158-165`, `src/Autheris.GraphQL/Mcp/GatewayMcpQueryExecutor.cs:74-78`, `Sid.cs:41-57`, `TableAccessPolicy.cs:341, 350-374`
- **Beschreibung:**
  - Alle Nutzer eines Agent-Clients erben Consents, die der App-Identität erteilt wurden.
  - Die erweiterte Entscheidung wird unter der echten User-SID gecacht und wirkt danach auch auf REST- und GraphQL-Requests dieses Nutzers.
  - Das widerspricht dem Kommentar in `Sid.cs` („App-IDs bewusst ausgeschlossen“).
- **Fix:** `sub` und `NameIdentifier` aus dem echten Nutzer übernehmen und die Client-ID nur als `client_id`/`azp` führen. Am besten `HttpContext.User` direkt verwenden.

### SG-10 GraphQL: Kostenbudget über Variablen-Defaults umgehbar (SR15-19 unvollständig)
- **Konfidenz:** bestätigt (Pipeline-Reihenfolge)
- **Fundstellen:**
  - `src/Autheris.GraphQL/Interceptors/QueryCostAnalyzerRule.cs:76`
  - `CostAndQuotaMiddleware.cs:80-93`
  - `GatewayServiceCollectionExtensions.cs`: `CostAndQuotaMiddleware` in Zeile 1049, also vor `UseOperationVariableCoercion` in Zeile 1055.
- **Beschreibung:**
  - Die Middleware liest `VariableValues` vor der Coercion, die Werte sind dort also leer, und es zählt wieder der Default.
  - Sie prüft außerdem nur gegen `Tier.MaxCostPerQuery` mit festem `maxResponseRows=1000`.
  - Die Tests decken nur die statische Methode ab.
- **Angriff:** `query($n:Int=1){a:t1(first:$n){…} b:t1(first:$n){…} …}` mit `{"n":5000}` liefert etwa 500 000 Zeilen pro Request (DB- und Speicher-DoS). Der MCP-Pfad ist nicht betroffen.
- **Fix:** Kostenprüfung nach `UseOperationVariableCoercion` einhängen, gegen `min(Tier, MaxAllowedComplexity)`. Dazu ein Integrationstest über den echten Executor.

### SG-11 OpenAPI-Ingestion: Katalog-Einträge in fremden Tenant-Domains, sofort aktiv, mit Angreifer-BaseUrl
- **Konfidenz:** bestätigt (Cross-Tenant-Write); Folge durch Phantomspalten plausibel
- **Fundstellen:** `GovernanceEndpoints.cs:53-89`, `src/Autheris.Application/DataCatalog/Services/OpenApiIngestionService.cs:57-66, 183-224`, `GatewayRole.cs` (TenantAdmin impliziert SchemaPublisher)
- **Beschreibung:**
  - Geprüft wird `HasAnyRole([GovernanceAdmin, SchemaPublisher])` ohne Tenant-Bezug.
  - `domain` aus der Query und `servers[0].url` aus dem Body werden nicht validiert.
  - Neue `HttpDeclarative`-Tabellen sind sofort aktiv, entgegen `ActivateNewTables=false`.
  - Spätere Ingestions behalten die BaseUrl des ersten Einträgers.
  - Bestehende Tabellen bekommen neue (Phantom-)Spalten.
  - Es entsteht kein Audit-Event.
- **Angriff:** Ein Tenant-A-Admin legt `tenantB.api.*` bzw. `default.api.*` mit `https://attacker.example` als Quelle an (Data-Spoofing nach Consent, Name-Squatting) oder bricht B-Tabellen durch Phantomspalten.
- **Fix:**
  - `domain` an den Tenant des Aufrufers binden (außer ClusterAdmin).
  - Neue Tabellen mit `IsActive=false` anlegen.
  - Bei bestehenden Tabellen keine neuen Spalten zulassen.
  - `servers.url` gegen die Egress-Allowlist prüfen.
  - Ingestion auditieren.

### SG-12 Inaktive Tabellen bleiben über OData (direkter Pfad), WebSQL und Arrow abfragbar
- **Konfidenz:** fehlende Prüfung bestätigt; Ausnutzbarkeit plausibel (Consent nötig)
- **Fundstellen:** `GatewayExecutionService.cs:478-491` (`ResolveTableAccessAsync`), `PostgreSqlGovernanceRepository.Catalog.cs:26-28`, `SqliteGovernanceRepository.Catalog.cs:90-92`. Iceberg, MCP und das GraphQL-Schema prüfen `IsActive`.
- **Angriff:**
  - Ein Admin deaktiviert `hr.salaries` als Notmaßnahme. Über `GET /odata/v4/hr/dbo/salaries` wird die Tabelle trotzdem weiter ausgeliefert.
  - Mit `OpenMetadata.AutoCreateConsents=true` sind neu importierte, inaktive Tabellen lesbar, bevor ein Steward sie geprüft hat (`OpenMetadataSyncService.cs:115-135, 365-371`).
- **Fix:**
  - `!IsActive` zentral in `TableAccessPolicy.DecideAsync` bzw. `ResolveTableAccessAsync` und in `GovernedSqlExecutionService` wie „nicht gefunden“ behandeln.
  - Für inaktive Tabellen keine Auto-Consents anlegen.

### SG-13 dbt `validate-contract` und `health`: Rolle `Developer` sieht Schemas aller Tenants
- **Konfidenz:** bestätigt
- **Fundstellen:** `src/Autheris.Api/Endpoints/DbtEndpoints.cs:147-173, 205-228`, `src/Autheris.Extensions/Dbt/DbtContractValidator.cs:55-102`
- **Beschreibung:**
  - `Developer` ist eine normale Rolle, die per Default auch aus Proxy-Headern übernommen wird.
  - Der Validator nimmt `database`/`schema`/`name` aus dem hochgeladenen Manifest und prüft weder Tenant noch Consent noch `IsActive`.
  - Bei `DROPPED_COLUMN` gibt er alle Katalogspalten mit Typ zurück, auch Deny-Spalten.
  - Die Warnung „not found“ ist zusätzlich ein Existenz-Orakel.
- **Fix:** Endpoint nur für GovernanceAdmin/DbtAdmin freigeben, oder auf den Tenant des Aufrufers einschränken und nur Spalten mit Zugriff ≠ Deny melden. Unbekannte und verbotene Tabellen identisch beantworten.

### SG-14 `supersedes` entfernt Pflichtfilter anderer Profile (SR15-12 unvollständig)
- **Konfidenz:** bestätigt
- **Fundstellen:** `src/Autheris.Application/VirtualFilters/MandatoryRowFilterResolver.cs:236, 276, 309`
- **Beschreibung:**
  - `applying` wird per `TryAdd` über `Name|TargetPattern` dedupliziert; es gewinnt das alphabetisch erste Profil.
  - Die Supersede-Prüfung nutzt `Any`.
  - `effective` entfernt den Filter danach profilübergreifend nach Name.
- **Angriff:** Ein eigenes, nicht verwaltetes Profil `0-own` bindet den verwalteten Filter X plus den eigenen Filter Y mit `supersedes=[X]`. Damit fällt X auch im verwalteten Profil weg.
- **Fix:** Supersede pro (Profil, Binding) entscheiden und `All` statt `Any` verwenden. Nicht global nach Namen entfernen und nicht profilübergreifend deduplizieren.

### SG-15 Token-Claims `action`/`gql.action` fließen weiter in Casbin-Attribute (SR15-08 unvollständig)
- **Konfidenz:** bestätigt im Code; Voraussetzung ist ein solcher Claim im Token
- **Fundstellen:** `src/Autheris.Application/Streaming/Services/StreamRlsPolicyEnforcer.cs:170-174` (CDC-Streaming und GraphQL-Subscriptions pro Event), `src/Autheris.Extensions/Lakehouse/Services/IcebergRestCatalogFederationService.cs:298-301`, `CasbinEnforcementService.cs:586-610`
- **Angriff:** Mit `action=x` im Token greift ein `deny … read` nicht mehr, während ein `allow … *` weiter erlaubt.
- **Fix:**
  - Ausschließlich `TableAccessPolicy.BuildEvaluationContext` als gemeinsame Kontextquelle verwenden.
  - In `ResolveRequestedAction` nur `read` und `write` als Whitelist zulassen, alles andere auf `read` abbilden oder ablehnen.

### SG-16 Schema-Contracts: `AllowedTables` ignoriert die Domain, Tags werden gegen Sensitivity geprüft (SR15-51)
- **Konfidenz:** bestätigt
- **Fundstellen:** `TableAccessPolicy.cs:147-149`, `CatalogVisibility.cs:102-104`, `TableIdentifier.cs:36`
- **Beschreibung:**
  - `AllowedTables=["public.orders"]` erlaubt auch `hr.public.orders` und gleichnamige Tabellen in jeder anderen Domain.
  - `Included/ExcludedTags` werden mit `Table.Sensitivity` verglichen statt mit den `@tag`-Namen. `ExcludedTags=["internal"]` ist deshalb wirkungslos (fail-open).
  - Ist der Claim-Contract unbekannt, wird der Contract außerhalb von HTTP nicht durchgesetzt.
  - `UnifiedPolicyDecisionPoint` übergibt `Claims: null` und verwendet dadurch immer den Default-Contract.
- **Fix:**
  - Nur auf voll qualifizierte Namen `domain.schema.table` matchen.
  - Tags mit derselben Funktion prüfen wie der SDL-Filter.
  - Unbekannter Contract führt zu Deny.

### SG-17 Envoy ext_authz: HTTP-Methode und Read-only-Status werden ignoriert, abweichende Tenant-Auflösung
- **Konfidenz:** bestätigt (Methode); plausibel (Tenant, `X-Original-Method`)
- **Fundstellen:** `src/Autheris.Application/Mesh/Services/EnvoyExtAuthzService.cs:104-106, 178-196`, `EnvoyExtAuthzEndpoints.cs:30-32`, `ReadOnlyTokenMiddleware.cs:105-110`
- **Beschreibung:**
  - Die Aktion ist immer `read`. Ein Casbin-`read`-Grant genügt deshalb für `DELETE` auf einem per Mesh geschützten Upstream.
  - Bei Prüfanfragen per GET mit `X-Original-Method: DELETE` (nginx/Traefik-Stil) lässt die Read-only-Middleware auch `Agent.Read`-Tokens durch.
  - Die Claim-Reihenfolge `tenant_id → tenant → tid` weicht von `GetTenantId` ab.
- **Fix:**
  - Methode auf Aktion abbilden (GET/HEAD → `read`, sonst `write`).
  - Bei `IsReadOnly()` nicht sichere Originalmethoden ablehnen.
  - `GetTenantId()` und `GetUserSid()` verwenden.

### SG-18 HitL-Freigaben über Redis Pub/Sub ohne Integritätsschutz fälschbar
- **Konfidenz:** bestätigt (Code-Pfad); Voraussetzung ist Schreibzugriff auf Redis
- **Fundstellen:** `src/Autheris.Application/Mcp/Services/HitLStepUpApprovalService.cs:132-148, 478-481`, `src/Autheris.Infrastructure/State/RedisClusterStateProvider.cs:59, 145-150`
- **Beschreibung:**
  - Ein `PUBLISH hitl:events:<id> {"isApproved":true,"approverSid":"…"}` gibt eine wartende MCP-Step-up-Anfrage frei und schreibt die Freigabe einer beliebigen Person zu.
  - Tickets werden unsigniert gespeichert.
  - Der Consent-L2-Cache ist dagegen HMAC-geschützt, weil Redis dort als nicht vertrauenswürdig gilt.
- **Fix:** Tickets und Broadcasts per HMAC mit einem HKDF-Subkey signieren, wie im Consent-Cache, oder die Entscheidung vor der Ausführung aus der Governance-DB neu lesen.

### SG-19 PostgreSQL-Governance-Repo: Audit-HMAC-Key fällt bei `danger_allow_insecure_transport` auf eine öffentliche Konstante zurück
- **Konfidenz:** bestätigt
- **Fundstellen:** `src/Autheris.Infrastructure/Persistence/PostgreSqlGovernanceRepository.cs:174-185`; vgl. `SqliteGovernanceRepository.cs:166-180` (wirft außerhalb von Development).
- **Beschreibung:**
  - Ist das Transport-Flag gesetzt und der Audit-Key nicht konfiguriert, gilt `SHA256("autheris-dev-ephemeral-audit-hmac-salt-secure-fallback")` als Key.
  - Das passiert ohne Log-Eintrag.
  - Der Anchor-Key wird per HKDF aus diesem Wert abgeleitet. Wer DB-Zugriff hat, kann die Kette konsistent neu schreiben.
- **Fix:** Ausnahme in Zeile 176 entfernen; außerhalb von Development immer werfen.

### SG-20 Traffic-Shadowing leitet PII aus JSON-Werten (GraphQL-`variables`) unredigiert weiter
- **Konfidenz:** bestätigt (Feature per Default aus)
- **Fundstellen:** `src/Autheris.Application/Diagnostics/Shadowing/PiiShadowingRedactor.cs:43-44, 117-131`, `TrafficShadowingMiddleware.cs:56-81`
- **Beschreibung:**
  - Redigiert werden nur SQL-Literale, JSON-escapte Literale sowie Muster für E-Mail, Kreditkarte und SSN.
  - Namen, IBANs, Telefonnummern und Zahlen in `variables` gehen im Klartext an die Shadow-Umgebung. Das verstößt gegen DSGVO-Zweckbindung und Datenminimierung.
- **Fix:** Den Body als JSON parsen und alle Blätter außerhalb von `query`/`operationName` typgerecht ersetzen. Numerische SQL-Literale ebenfalls redigieren.

### SG-21 Federation: Die Signatur des Kontext-Headers deckt Rollen, Subject und Audience nicht ab
- **Schwere:** Niedrig–Mittel | **Konfidenz:** plausibel
- **Fundstellen:** `src/Autheris.Application/Federation/Services/SubgraphContextPropagationService.cs:56-58, 70, 125`
- **Beschreibung:**
  - Das HMAC-Payload ist `tenant:userSid:ts:nonce`.
  - Die Rollen-Header sind unsigniert.
  - Der Subject-Header kann vom signierten `userSid` abweichen.
  - Es gibt keine Bindung an Subgraph oder Audience. Ein kompromittierter Subgraph kann die Header mit anderen Rollen an andere Subgraphen weiterreichen (Replay).
- **Fix:** Alle propagierten Header-Werte, den Subgraph-Namen sowie Methode und Pfad signieren. Besser: OBO bzw. Token-Exchange pro Subgraph.

---

## 4. Befunde – Niedrig

| ID | Titel | Fundstelle | Kern / Fix |
|---|---|---|---|
| SG-22 | Vier-Augen bei virtuellen Filtern ist opt-in; Sync umgeht die Freigabe (SR15-06/07 Restrisiko) | `GatewayOptions.cs:1652` (`RequireApproval=false`), `VirtualFilterAdministrationService.cs:157,320,349,501,543ff` | In Production standardmäßig `true` bzw. DANGER-Warnung beim Start. `managed_by` ist nur eine Behauptung des Clients; Lockerungen per Sync sollten ebenfalls freigabepflichtig sein. |
| SG-23 | Canary-Router: Client-Header `X-Feature-Variant` umgeht `RequiredRole`/`AllowedTenants` | `SubgraphCanaryRouter.cs:67-81`, `SubgraphSecurityDelegatingHandler.cs:66-73` | Bedingungen per UND verknüpfen oder den Header am Eingang entfernen. |
| SG-24 | DuckDB-Keyword-Denylist per `$$…$$` umgehbar (SQL2-9 unvollständig) | `DuckDbOlapEngine.cs:268, 349, 401` | Rekursive CTE ohne Limit führt zu CPU-DoS bis zum Timeout (bestätigt). Den echten DuckDB-Parser verwenden (`json_serialize_sql`, genau ein SELECT). |
| SG-25 | `CanWriteTableAsync` prüft keine eigene Schreibberechtigung (SR15-51) | `TableAccessPolicy.cs` (`CanWriteTableAsync`), `GovernedSqlExecutionService.cs:565-573` | Ohne Casbin-Policies gilt Schreiben = Lesen; mit Policies ist die Prüfung redundant. `DmlWriterRoles` greift weiterhin. Explizites Write-Grant verlangen oder die Dokumentation korrigieren. |
| SG-26 | Nicht katalogisierte Spalten sind per WebSQL lesbar, wenn eine Tabelle weder RLS noch Maske hat | `GovernedSqlExecutionService.cs:681-685`, `RlsListener.SecureRelation`, `AstSecurityVisitor.VisitNamedTableSource` | Neue physische Spalten und Systemspalten (`ctid`, `rowid`) sind abfragbar. Jede Tabelle in die Katalog-Projektion kapseln. (plausibel) |
| SG-27 | Asynchroner Trino-Pfad gibt Exception-Texte aus (SR15-16) | `WebSqlStatementManager.cs:434-450`, `WebSqlEndpoints.cs` (`WriteTrinoStatementResponseAsync`) | Dasselbe generische Mapping wie im synchronen Pfad verwenden. (plausibel) |
| SG-28 | Webhook-Replay-Schutz für ITSM, dbt und OpenMetadata nur pro Knoten | `ItsmWebhookHandler.cs:46`, `DbtWebhookReceiver.cs:70`, `OpenMetadataSyncService.cs:38` | Über `IDistributedClusterStateProvider` deduplizieren, wie im `CatalogWebhookHandler`. |
| SG-29 | Differential Privacy: Budget-Reset ohne Audit, Selbst-Reset möglich, Fremdbudget verbrauchbar | `GovernanceEndpoints.cs:236-294`, `DifferentialPrivacyEngine.cs:35-47` | Reset auditieren, Selbst-Reset verbieten bzw. Vier-Augen verlangen, `SecurityAuditor` aus `perturb` entfernen. |
| SG-30 | Admin-Pfade: SQL-Endpoint-Register überschreibt ohne Konflikt-Prüfung und Audit; Tenant aus rohem Claim; globaler Lineage-Push durch DataOwner | `SqlEndpointRoutes.cs:67-121`, `InMemorySqlEndpointRegistry.cs:17`, `GovernanceEndpoints.cs:392-407`, `OpenLineageClient.cs:47-97`, `FinOpsEndpoints.cs:39` | Überall `EndpointSecurity.GetRequestTenant` verwenden, Lineage-Nodes nach Tenant filtern, Registrierungen auditieren und Überschreiben nur explizit erlauben. |
| SG-31 | GraphQL: Existenz-Orakel über `QUERY_TOO_COMPLEX` (umgeht R-GQL-6) | `GraphQlEnumerationShieldMiddleware.cs:30-33, 58-61`, `QueryCostAnalyzerRule.cs:76-87` | Bei jedem Validierungsfehler dieselbe generische Antwort liefern. (plausibel) |
| SG-32 | OData-Flat-Alias antwortet 404/403 unterschiedlich (SR15-31) | `ODataEndpoints.cs:299-318` vs. `ODataHandler.cs:288-301` | Immer 403 mit generischer Meldung. |
| SG-33 | Iceberg: Audit-JSON per String-Konkatenation, abgelehnte Credential-Anfragen nicht auditiert, `.` im Namen führt zu 500 (SR15-18) | `IcebergRestCatalogFederationService.cs:165, 189, 199-222`, `IcebergRestCatalogEndpoints.cs:100-108` | `JsonSerializer.Serialize` verwenden, alle Anfragen auditieren, `ArgumentException` als 403 behandeln, Details nur in Development. |
| SG-34 | Unauthentifizierte Webhook-Aufrufe können 500er auslösen | `ItsmWebhookHandler.cs` (`ParsePayload`), `WebhookEndpoints.cs:169, 268` | Fehler breiter abfangen und mit 400 antworten. Kein Bypass. |
| SG-35 | Basic-Auth: Timing-Orakel für Benutzernamen bei gemischten Hash-Parametern (SR15-38) | `BasicAuthenticationHandler.cs:57-59, 62-113, 191-192` | Einheitliche Hash-Parameter erzwingen oder eine konstante Mindestdauer einhalten. |
| SG-36 | Basic-Auth: Lockout-Prüfung und Fehlerzählung nicht atomar (SR15-37) | `BasicAuthenticationHandler.cs:179-205`, `BasicAuthAttemptGuard.cs:128-258` | Bis zu 100 statt 10 Versuche, dazu Argon2-Speicherspitzen. Versuch vor der Prüfung per INCR reservieren und Hashing per Concurrency-Limit begrenzen. |
| SG-37 | HTTPS für JWKS/Metadaten (ADFS/Entra) außerhalb von Development nicht erzwungen | `GatewayServiceCollectionExtensions.cs:796-797, 1513-1523` | Startup-Validierung: nur HTTPS und `RequireHttpsMetadata=true`. |
| SG-38 | Deploy/Benchmark-Stack | `deploy/podman-compose.yaml:36-37, 112, 230-242`, `deploy/containers/sqlserver/entrypoint.sh:18`, `deploy/scripts/seed_openmetadata.py:146-148`, `deploy/containers/load-generator/Containerfile:5` | Folgende Punkte: <br>• `crm_user` fällt auf das SA-Passwort zurück (SR15-26 Rest). <br>• Die PG-Datenquelle läuft als Superuser; ein RLS-Bypass führt damit per `COPY … TO PROGRAM` zu RCE. <br>• `GATEWAY_HMAC_SECRET` dient zugleich als Masking-Key und Webhook-Secret, dadurch ist De-Pseudonymisierung möglich. <br>• `sa`/`Password123!` ist hartkodiert. <br>• `k6:latest` ist ungepinnt. <br>• `azure-sql-edge` ist abgekündigt. <br>• DEP-11: Release- und Publish-Workflows sind von jedem Branch auslösbar. |
| SG-39 | Härtungshinweise Ingress | `RedisTokenRevocationService.cs:171-176`, JwtBearer `IncludeErrorDetails`, `ForwardAuth.TrustedProxies` vs. `ReverseProxy.KnownProxies`, `TenantResolutionMiddleware` | Folgende Punkte: <br>• Fällt Redis aus, greift Revocation nur noch lokal (fail-open). <br>• Validierungsdetails stehen in `WWW-Authenticate`. <br>• Fehlt die Proxy-IP in `KnownProxies`, wird das Pre-Auth-Limit zum globalen DoS-Hebel. <br>• Toter Code mit schwächeren Tenant-Wechsel-Regeln: entfernen. |

---

## 5. Verifizierte Positiv-Kontrollen (Auswahl)

- **Ingress:**
  - ForwardAuth prüft gegen die rohe TCP-IP, vergleicht das Secret konstantzeitig und verlangt mindestens 32 Byte.
  - Admin-Rollen kommen nie aus Headern.
  - Ein Tenant-Wechsel ist nur für den kanonischen ClusterAdmin möglich.
  - Dev- und Test-Handler, anonymer Zugriff und das Session-Cookie sind auf Development beschränkt und beim Start abgesichert.
  - Klartext-Passwörter sind außerhalb von Development verboten.
  - Die Pipeline-Reihenfolge ist korrekt.
- **JWT/WebSocket:**
  - Issuer, Audience, Laufzeit und Signatur werden immer geprüft.
  - Bei WebSocket werden SID- und Tenant-Bindung, Read-only-Vererbung (SR15-03) und eine periodische Revocation-Prüfung durchgesetzt.
  - Bei Ambient-Credentials mit Origin wird abgelehnt (Schutz gegen Cross-Site WebSocket Hijacking).
- **SQL-Engine (Legacy-Default):**
  - DML-Filter als `(rls) AND (userWhere)`, Ganzzeilen-Referenzen werden abgelehnt.
  - `__gql_`- und `p_rls_`-Parameter sind gesperrt.
  - Kurznamen werden nur bei Eindeutigkeit registriert.
  - Lexer-Härtung: Kommentare, `E''`, `UESCAPE` und Dollar-Quoting außerhalb von PostgreSQL werden abgelehnt.
  - Funktions-Allowlist mit vorrangiger Denylist.
  - Parser-DoS-Schutz: eigener Stack, Zeitbudget, Tiefenlimit.
- **Policy:**
  - Casbin mit zwei Gates (Enforcer und Matcher), fail-closed bei Ausnahmen und bei nicht auswertbaren Deny-Regeln.
  - Virtuelle Filter wirken nur einschränkend.
  - ReBAC verwendet Tenant-Partitionierung und voll qualifizierte Objekt-IDs.
  - Der Degraded-Mode ist für sensible Tabellen fail-closed.
  - Die Cache-Keys enthalten Gruppen-, Rollen- und Attribut-Hashes.
- **GraphQL/MCP:**
  - Filter, Sortierung und Join sind nur auf Clear-Spalten erlaubt.
  - `query_graphql` ist fail-closed: eine einzige Query-Operation, Auflösung von Relationen und Fragmenten.
  - Preflight-Header und Origin-Allowlist gegen CSRF; Introspection ist standardmäßig aus.
  - Versteckte und fehlende Datasets erhalten dieselbe Antwort.
- **Extensions:**
  - OData bindet alle Literale als Parameter, mit Tiefenlimit, Funktions-Allowlist und doppelter Clear-Prüfung.
  - Iceberg stellt keine Credentials aus und liefert rohe Metadaten nur bei uneingeschränktem Consent.
  - Ausgehendes HTTP: IP-Pinning gegen DNS-Rebinding, keine Redirects, begrenzte Antwortgrößen.
  - Webhooks prüfen HMAC konstantzeitig mit Timestamp-Bindung.
  - CDC prüft pro Event.
- **Procedures/Arrow/Tree:**
  - Procedures laufen als `CommandType.StoredProcedure` mit typisierten Parametern; Kontextparameter sind nicht überschreibbar.
  - Arrow-Flight-Tickets sind per HMAC an SID, Tenant und TTL gebunden und werden beim Einlösen erneut voll governed ausgeführt.
  - Tree-Queries binden alle Werte, mit RLS auf jeder Ebene.
- **Infrastruktur und Supply Chain:**
  - Plugins nur mit SHA-256-Allowlist und ohne TOCTOU.
  - Audit-Kette mit HMAC v2, Sequenzbindung, externem Anchor und optionalem asymmetrischem Signer.
  - Garnet mit `--auth`, außerhalb von Development nur auf Loopback.
  - DB-TLS: `VerifyFull` ist Pflicht.
  - GitHub Actions sind per SHA gepinnt, `permissions: read-all`, `--locked-mode`, SBOM und Provenance.
  - Das Base-Image ist per Digest gepinnt und läuft als non-root.
  - NuGetAudit läuft mit Warnungen als Fehler.
- **DuckDB:** Jede Session bekommt eine eigene `:memory:`-DB. `read_csv('/etc/passwd')` wird abgewiesen, `lock_configuration=true` ist aktiv.

## 6. Abhängigkeiten

- `dotnet list src/Autheris.Api/Autheris.Api.csproj package --vulnerable --include-transitive` (nuget.org, online) ergab **keine verwundbaren Pakete**. Das deckt Application, Infrastructure, GraphQL und Extensions transitiv ab.
- Die Prüfung über die gesamte Solution brach nach 300 s mit einem Timeout ab. Test- und Tool-Projekte wurden deshalb nicht einzeln geprüft.
- Die sicherheitsrelevanten Pakete sind aktuell: Npgsql 10.0.3, Microsoft.Data.SqlClient 7.1.1, IdentityModel 8.19.2, JwtBearer 10.0.12, HotChocolate 16.6.7, Garnet 2.2.0, MemoryPack 1.21.4, ModelContextProtocol 2.2.0, Azure.Identity 1.17.0.

## 7. Empfohlene Reihenfolge

1. **Sofort:**
   - **SG-01 und SG-02:** In einem gemeinsamen Fix: Temp-Verzeichnis pro Session, Datei-Funktionen verbieten, Byte-Budget, Session-Semaphore, Grenze für `TableNames`. Alternativ `DuckDbOlap.Enabled=false` als Default, bis der Fix steht.
   - **SG-03:** Einreicher bzw. Bearbeiter-Menge.
   - **SG-04 und SG-09:** Identitäts-Claims für Consent-Matching einschränken.
2. **Vor einem Wechsel des Defaults auf `AstCompiler`:** SG-05 und SG-06 mit Regressionstests pro Dialekt.
3. **Kurzfristig:**
   - SG-07 (CSRF umkehren) und SG-08 (Entra App-only).
   - SG-10 (GraphQL-Kosten), SG-11 und SG-13 (Cross-Tenant-Katalog), SG-12 (`IsActive`).
   - SG-14 und SG-15 (SR15-Restlücken), SG-16 (Schema-Contracts).
   - SG-17 bis SG-20.
4. **Mittelfristig:** SG-21 bis SG-39.

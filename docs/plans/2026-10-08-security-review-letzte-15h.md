# Security-Review der Änderungen vom 08.10.2026 (letzte 15 h)

**Umfang:** Alle Commits von `f0d57f8` bis `1448bbc` auf `feat/ast-target-dialect-generator`, rund 130 Commits. Ohne Doku und Tests sind das etwa 230 Dateien mit +12.500/−4.300 Zeilen. Dazu kommen die uncommitteten Änderungen im Arbeitsverzeichnis zum Zeitpunkt des Reviews.

**Methode:** Sieben parallele Reviews nach Bereich, nur durch Lesen von Code (`git show HEAD:`, `git diff -w`). Nichts wurde gebaut oder ausgeführt. Provider-Verhalten (Npgsql, Microsoft.Data.Sqlite, System.Text.Json) ist aus Bibliotheksquellen abgeleitet. Befunde mit **(plausibel)** sind nicht vollständig belegt.

**Stand:** Während des Reviews kamen weitere Commits hinzu (`a691f5e` bis `1448bbc`). Die Zeilenangaben beziehen sich auf den jeweils geprüften HEAD (`a691f5e`, `5ff70ab` oder `1448bbc`). Abweichungen um einige Zeilen sind möglich.

IDs: `SR15-NN`. Schwere: hoch / mittel / niedrig / info.

## 0. Sofort

| Punkt | Warum |
|---|---|
| **Build** | `VirtualFilterAdministrationService.cs:108/190` und `VirtualFilterEndpoints.cs:324` nutzen `VirtualFilterOptions.RequireApproval`. Die Eigenschaft fehlt in der committeten `GatewayOptions.cs`, sie liegt nur uncommittet im Arbeitsverzeichnis. Laut Review baut `HEAD` deshalb nicht; ein Build wurde nicht ausgeführt. |
| SR15-01, 02, 03, 04, 05 | Hoch: Umgehung von Zeilenfiltern oder Read-only bzw. Prozessabsturz durch beliebige angemeldete Nutzer. |
| WIP SR15-06 (Resolver) | Die uncommittete Änderung am `MandatoryRowFilterResolver` macht aus SR15-06 ein Fail-open in die andere Richtung. Nicht so committen. |

## 1. Hoch

### SR15-01 WebSQL: Client-Parameter überschreiben Row-Filter-Parameter (RLS-Umgehung)
- **Ursache:** Consent-Row-Filter binden Parameter als `@p_rls_N` (`RowFilterSqlBuilder.cs:262, 298`). Gesperrt ist für Client-Parameter aber nur das Präfix `__gql_` (`GovernedSqlExecutionService.cs:814-823`).
- **Ablauf:** Interne Parameter werden zuerst gebunden, Client-Parameter danach. Die Duplikatprüfung gilt nur unter den Client-Parametern (`:962-987`).
- **Angriff:** `POST /api/v1/sql {"sql":"SELECT * FROM t","parameters":{"p_rls_0":"US"}}`
  - SQLite: Der letzte Wert gewinnt. Der Nutzer setzt den Wert seines eigenen Zeilenfilters und sieht fremde Zeilen.
  - PostgreSQL: Der erste Wert gewinnt. Mit `SELECT @p_rls_0` lässt sich aber der interne Filterwert auslesen.
  - SQL Server: Fehler wegen doppelter Deklaration.
- **Kanäle:** WebSQL JSON, Parquet und Trino `/v1/statement`.
- **Fix:**
  - Interne Row-Filter-Parameter unter `__gql_` benennen.
  - Client-Parameter ablehnen, die mit einem internen Namen kollidieren.
  - Test: SQLite plus Consent-Filter plus `p_rls_0`.

### SR15-02 WebSQL: Kurzname-Kollision hebelt RLS und Masking aus (d77a069, R-SQL-9)
- **Ursache:** `RegisterTableLookup`/`RegisterTableSet` (`GovernedSqlExecutionService.cs:363-383`) registrieren jetzt für **jedes** Ziel auch den nackten `TableName` und `schema.table` und überschreiben dabei vorhandene Einträge.
- **Angriff:** `SELECT * FROM orders o JOIN archive.orders a ON …`
  - Der Eintrag `archive.orders` überschreibt `tableRlsFilters["orders"]` und `tableMaskingExpressions["orders"]`.
  - Hat `archive.orders` kein RLS, enthält `tablesWithoutRls` den Eintrag `orders`.
  - Je nach Reihenfolge der Ziele läuft die eingeschränkte Tabelle dann ohne Tenant-, Consent- oder virtuellen Filter und ohne Masken.
- **Fix:** Kurzformen nur registrieren, wenn kein anderes Ziel denselben Kurznamen hat, sonst fail-closed. Für R-SQL-9 reicht der Schlüssel `resolvedId`. Test mit gleichnamigen Tabellen in zwei Schemas.

### SR15-03 GraphQL-WebSocket: Read-only-Token führt Mutationen aus
- **Ursache:** Das Token aus `connection_init` wird in `JwtSocketTokenValidator.cs:72-89` direkt mit `JsonWebTokenHandler` validiert. Die JwtBearer-Events und damit `EntraTokenPolicy.Apply` laufen dabei nicht, der Marker `autheris:access_mode` fehlt.
- **Folge:** `WebSocketAuthInterceptor.cs:150` setzt `HttpContext.User` auf diesen Principal. `ReadOnlyOperationMiddleware` erkennt ihn deshalb nicht als Read-only.
- **Angriff:**
  1. Ein Agent mit `Agent.Read`-Token eines Admins öffnet den WebSocket-Upgrade auf `/graphql`. Das ist ein GET und wird durchgelassen.
  2. Er sendet `connection_init` mit demselben Bearer.
  3. Er schickt `subscribe` mit `mutation { approveConsentRequest(…) }`, `revokeConsent`, `reloadSchema` oder `syncDataCatalog`.
- **Fix:**
  - Im Socket-Validator dieselbe Pipeline wie bei HTTP verwenden: `EntraTokenPolicy` inklusive App-only-Deny und `ClaimsNormalizer`.
  - Zusätzlich die Read-only-Eigenschaft des Upgrade-Requests in die Session übernehmen.

### SR15-04 OData: StackOverflow durch verschachtelten `$filter` (Prozessabsturz)
- **Ursache:** Rekursiver Abstieg ohne Tiefenlimit in `ODataFilterParser.cs:395-403, 434-441, 473-485`. Pro Klammerebene entstehen etwa 5 Frames. 8 KB Request-Line reichen damit für rund 40.000 Frames.
- **Folge:** Eine `StackOverflowException` lässt sich nicht abfangen und beendet den Gateway-Prozess für alle Tenants. Der Parser läuft vor jeder Tabellenprüfung, jeder angemeldete Nutzer kann das auslösen.
- **Angriff:** `GET /odata/v4/x/y/z?$filter=((((…8000×…a eq 1)))…` oder `tolower(tolower(…))`. Rechnerisch geprüft, nicht ausgeführt.
- **Fix:**
  - Tiefenzähler mit einer Grenze von 32 bis 64, danach 400.
  - Längenlimit für `$filter` von 2 bis 4 KB. Das deckt auch die rekursiven Funktionen `ToSql`/`CollectReferencedColumns` bei langen OR-Ketten ab.

### SR15-05 AST-Engine: Nutzer-OR in UPDATE/DELETE hebelt den Row-Filter aus
- **Ursache:** Der Builder verwirft Klammerknoten (`SqlAstBuilder.cs:1273-1277`). `NeedsParentheses` klammert nur `BinaryExpression` und `Between` (`SqlDialectGeneratorBase.cs:708-726`). Die Operanden folgender Konstrukte werden ungeklammert ausgegeben:
  - `IN` (`:430-445`)
  - quantifizierter Vergleich (`:456-465`)
  - `BETWEEN` (`:466-472`)
  - `IS DISTINCT FROM` (`:671-676`)
  - SQLite `IS` (`SqliteDialectGenerator.cs:22-27`)
- **Folge:** Für DML hängt `AstSecurityVisitor.cs:270-272, 347-349` den Filter als `userWhere AND rls` an. Aus `(a OR b) IN (TRUE)` wird dann `a OR b IN (TRUE) AND (rls)`.
- **Wirkung:** UPDATE/DELETE treffen Zeilen anderer Mandanten (PostgreSQL, SQLite).
- **Voraussetzung:** `WebSql.SqlRewriterEngine = AstCompiler` und DML erlaubt. SELECT ist nicht betroffen, weil der Filter dort in einer gekapselten Subquery steht.
- **Fix:**
  - Den DML-Filter immer als `(userWhere) AND (rls)` ausgeben.
  - Zusammengesetzte Operanden in allen Prädikat-Knoten klammern.
  - Regressionstest pro Dialekt.

### SR15-06 Virtuelle Filter: Die Vier-Augen-Freigabe wirkt nicht (5701d51)
Committeter Stand:
- Der Resolver prüft `Status` nicht (`MandatoryRowFilterResolver.cs:202-208`).
- Ein Upsert mit `PendingApproval` ersetzt die aktive Fassung sofort.
- **Angriff:** Ein einzelner FilterAdmin entschärft einen Filter, zum Beispiel per Tautologie, `uncovered=skip` oder engerem Scope. Die Änderung gilt sofort.

Uncommitteter Stand im Arbeitsverzeichnis:
- Der Resolver ignoriert jetzt nicht aktive Profile und Filter.
- **Folge:** Ein bloßes erneutes Speichern, auch ohne Änderung, schaltet die Einschränkung bis zur Freigabe ab. Das Vier-Augen-Prinzip kehrt sich um.

Weitere Lücken:
- `Delete` umgeht die Freigabe.
- `approve` prüft weder `Status == PendingApproval` noch einen Definitions-Hash (TOCTOU, `VirtualFilterAdministrationService.cs:144, 226`).

Fix:
- Die zuletzt freigegebene Fassung aktiv halten, bis die neue freigegeben ist; Entwürfe in eigenen Spalten oder einer eigenen Tabelle.
- Löschen und Lockern ebenfalls freigabepflichtig machen.
- `approve` an `expected_hash` binden.

### SR15-07 Virtuelle Filter: Sync mit `force` umgeht Freigabe und `managed_by`
- **Ursache:** `sync/apply?force=true` verlangt nur FilterAdmin, setzt aber `IsSync: true` (`VirtualFilterEndpoints.cs:161-176`). Dadurch entfällt `EnsureWritable` (`AdministrationService.cs:432-436`).
- **Folge:** Der Status ist sofort `Active`. `ManagedBy` (Pfad, Commit) kommt ungeprüft vom Client.
- **Angriff:** Ein FilterAdmin überschreibt oder löscht alle Talos-verwalteten Filter, ohne Repo-Review und ohne zweite Person.
- **Fix:**
  - `IsSync` nur für FilterSync-Identitäten setzen.
  - Für `force` beide Rollen verlangen, oder die Freigabe eines abgelegten Plans per Hash durch eine zweite Person.

## 2. Mittel

| ID | Bereich | Befund | Fundstelle | Fix |
|---|---|---|---|---|
| SR15-08 | Casbin (`1448bbc`) | Deny-Regeln gelten nur noch für die angefragte Aktion oder `*`. Ein `deny … read` blockiert `write` also nicht mehr. Außerdem liest `ResolveRequestedAction` die Aktion aus `Attributes["gql.action"|"action"]`, und `TableAccessPolicy.BuildEvaluationContext` kopiert alle Token-Claims dorthin. **(plausibel)** Ein Claim `action=x` umgeht dann ein `deny read`, wenn es ein `allow *` gibt. | `CasbinEnforcementService.cs:586-610, 689`; `TableAccessPolicy.cs:269-283, 324` | Deny wieder für alle Aktionen matchen bzw. `read` impliziert `write`. Die Aktion nur aus Server-`ExtraAttributes` lesen, Claims dieses Namens verwerfen. |
| SR15-09 | AST DML (`65fffac`) | Die Tautologie-Erkennung für Spalte gegen Spalte greift nur noch bei `=`. `DELETE FROM t WHERE id >= id` geht durch. `EXISTS (SELECT 1)` gilt weiter pauschal als Spaltenbezug. RLS bleibt aktiv. | `AstSecurityVisitor.cs:498-499, 545ff` | `<=`/`>=` wieder erkennen, Subqueries rekursiv prüfen. |
| SR15-10 | AST DML | Maskierte Spalten lassen sich als Orakel nutzen: `PushChildren` durchläuft bei QuerySpecification weder `From` (JOIN ON, abgeleitete Tabellen) noch `GroupBy`, und LIKE `ESCAPE` fehlt ebenfalls. Ein UPDATE ohne Wirkung mit korrelierter Subquery auf die maskierte Spalte verrät deren Klartext über die Anzahl betroffener Zeilen. | `AstSecurityVisitor.cs:566-569, 642-646` | Einen generischen Walker über alle Knoten verwenden, unbekannte Knoten fail-closed behandeln. |
| SR15-11 | Virtuelle Filter | Filter-Subqueries wirken als Boolean-Orakel. SQL-Filter dürfen jede katalogisierte Tabelle der Datenquelle lesen, ohne Consent, RLS, Masking oder Tenant-Prädikat. Der Katalog ist global. Strukturierte Filter werden nicht gegen den Katalog geprüft. Ein FilterAdmin kann `… s.salary > 100000 …` an sich selbst binden und Werte per Binärsuche auslesen, auch aus anderen Tenants. | `SqlFilterCompiler.cs:97-101`, `StructuredFilterSqlBuilder.cs:53ff` | Referenztabellen per Allowlist pro Tenant beschränken, Tenant-Prädikat in die Subquery, Katalogprüfung auch für strukturierte Filter. |
| SR15-12 | Virtuelle Filter | `supersedes` wirkt über Profilgrenzen hinweg. Ein eigener, unverwalteter Filter mit `supersedes=[verwalteter_filter]` in einem eigenen Profil hebt die verwaltete Einschränkung auf. | `MandatoryRowFilterResolver.cs:255-258` | Nur innerhalb eines Profils oder bei gleichem `managed_by` zulassen. |
| SR15-13 | Virtuelle Filter | Fehlt eine benötigte Spalte (Umbenennung, unvollständiger Sync), gilt das Objekt still als „nicht abgedeckt“. Bei `uncovered=skip` greift dann kein Filter (fail-open). | `MandatoryRowFilterResolver.cs:332` | Bei passendem Muster und fehlenden Spalten ablehnen, mit Warnung und Audit. |
| SR15-14 | Virtuelle Filter | In strukturierten Filtern wird `"01"` zur Zahl. Auf SQL Server trifft `= 1` dann auch `'1'`, `'001'` und `' 1'` (zu weit), PostgreSQL bricht ab, SQLite trifft nur `'1'`. Das Ergebnis ist je nach Dialekt verschieden. | `StructuredFilterSqlBuilder.cs:123-126` | Den Typ aus Katalog oder Modell nehmen, Strings immer als String-Literal ausgeben. |
| SR15-15 | Virtuelle Filter | `MaxRemovals` zählt nur entfernte Bindungen pro Aufruf. Lässt sich mit mehreren Aufrufen umgehen, und Lockerungen (Definition, `uncovered`, Scope) zählen gar nicht. | `VirtualFilterAdministrationService.cs:356` | Lockerungen mitzählen, Limit über ein Zeitfenster. |
| SR15-16 | WebSQL/Trino | Der asynchrone Pfad gibt `ex.Message` ungefiltert aus: DB-Fehler mit Klarwerten (`Conversion failed … 'Alice'`) und Policy-Details, auch in Production (erweitert API-11). **(plausibel)** Weil PostgreSQL und SQL Server die RLS-Subquery einebnen dürfen, ließen sich Werte fremder Zeilen über Fehler auslesen. | `WebSqlStatementManager.cs:189-199` → `WebSqlEndpoints.cs:926-934` | Generische Meldung mit Trace-ID. RLS-Subquery gegen Einebnen sichern (`OFFSET 0` bzw. MATERIALIZED-CTE). |
| SR15-17 | WebSQL/Trino | Der Statement-Manager hat kein Mengen- oder Speicherlimit. Fertige Ergebnisse (bis 10.000 Zeilen) bleiben 15 min im RAM. Eine Schleife mit `wait_timeout=0` erschöpft den Speicher. | `WebSqlStatementManager.cs:57-83, 314-327` | Maximal N offene Statements pro Tenant und SID (429), Ergebnis nach FINISHED verwerfen, Byte-Budget. |
| SR15-18 | WebSQL | Audit-Lücken: Abgelehnte SELECTs (Probing, Guardrails, Denylist) werden nicht auditiert. `WEBSQL_QUERY` enthält `originalSql` im Klartext mit Literalen. Iceberg `LoadTable`/`credentials` werden gar nicht auditiert. | `GovernedSqlExecutionService.cs:832-869`, `IcebergRestCatalogFederationService.cs:108-166` | DENY-Events schreiben, Literale hashen, Iceberg auditieren. |
| SR15-19 | GraphQL/MCP | Kosten werden mit dem `DefaultValue` der Variablen berechnet. `query($n:Int=1){ t(first:$n) … }` mit `{"n":100000}` umgeht Budget und Quota. Gilt auch für MCP `query_graphql` (Regression aus 84fe2a6). | `QueryCostAnalyzerRule.cs:260-271`, `GatewayMcpQueryExecutor.cs:399-426` | Kosten nach der Variablen-Coercion mit den echten Werten berechnen, `MaxResponseRows` statt fest 1000. |
| SR15-20 | Read-only | `/api/v1/queries` steht pauschal auf der POST-Allowlist. Ein Read-only-Token kann damit `POST /api/v1/queries/` (RegisterSqlEndpoint) aufrufen und kuratierte Endpoints überschreiben. | `ReadOnlyTokenMiddleware.cs:28, 60`, `SqlEndpointRoutes.cs:36-38, 120` | Nur `POST /api/v1/queries/{name}` freigeben, Register mit `IsReadOnly()` sperren. |
| SR15-21 | Arrow-Export | **(plausibel)** Der ReBAC-Filter nimmt das erste case-insensitiv passende `table`, der Handler deserialisiert selbst (dort gewinnt das letzte). `{"table":"allowed","Table":"hr.dbo.salaries"}` prüft also eine andere Tabelle als exportiert wird. Nur relevant ohne `Rebac.EnforceOnQueryPaths`. | `RebacEndpointFilter.cs:163-168`, `ArrowExportEndpoints.cs:73-77` | Den Body einmal parsen und per `HttpContext.Items` weitergeben, doppelte Keys ablehnen. |
| SR15-22 | Federation | Wenn Fusion/Subgraphs aktiv sind, geht das eingehende Bearer-Token unverändert an Subgraphs, auch MCP-Tokens. Die MCP-Spezifikation verbietet Token-Passthrough. | `SubgraphSecurityDelegatingHandler.cs:95-103` | OBO oder Token-Exchange pro Subgraph. |
| SR15-23 | ReBAC (`20934a3`) | `can_query` wird jetzt von `viewer`, `editor` und `owner` geerbt. Jedes bestehende `viewer`-Tupel erlaubt damit sofort Abfragen (OLAP, Arrow, PDP). Das ist eine Policy-Ausweitung ohne Migrationshinweis. | `ZanzibarRebacEvaluator.cs:334` | Bewusst entscheiden, per Option schalten, dokumentieren. |
| SR15-24 | Audit SQLite (c1044e7) | Bei `existingTx` werden Speicherstand und WORM-Anker der Kette fortgeschrieben, bevor die äußere Transaktion committet. Ein Rollback (z. B. Abbruch nach Audit) führt zu dauerhaft `FlagAuditChainViolation`. | `SqliteGovernanceRepository.Audit.cs:303-321`, `…Consent.cs:881, 1146, 1223, 1333` | Fortschreiben erst nach dem Commit (Callback), bei Rollback verwerfen. |
| SR15-25 | Prozeduren | Hat ein Tenant eine ABAC-Policy und gibt es einen Allow-Consent ohne Spaltenregeln, werden alle Spalten explizit Clear. `IsSensitive`-Spalten wie `salary` kommen aus Prozeduren dann im Klartext, über REST maskiert. Bestand schon vorher und ist nicht behoben. | `GovernedProcedureExecutionService.cs:363, 423, 543` | Beim Festschreiben `GetEffectiveColumnAccess(col, meta)` verwenden. |
| SR15-26 | Deploy | `crm_user` bekommt das SA-Passwort (`-v MSSQL_CRM_PASSWORD="$MSSQL_SA_PASSWORD"`) und `db_datawriter`. Damit ist der DEP-15-Fix wirkungslos. | `deploy/containers/sqlserver/entrypoint.sh:18`, `podman-compose.yaml:237`, `init-db.sql:77` | Eigene Variable, nur `db_datareader`. |
| SR15-27 | DEP-12 (`6830f0c`) | Das Bereinigungsskript enthält die fünf historischen Secrets base64-kodiert und bringt sie damit wieder ins Repo. Scanner erkennen das nicht. | `scripts/dep12-clean-git-history.py:26-32` | Werte zur Laufzeit von außerhalb lesen oder nur Hashes vergleichen. Das Skript beim Rewrite mit bereinigen und die Secrets rotieren. |
| SR15-28 | OData | `@odata.nextLink` übernimmt `$filter` nicht. Ab Seite 2 kommen ungefilterte Zeilen, im Rahmen von RLS. Folgen: Datenminimierung verletzt, Importe in Power BI oder Excel verfälscht. | `ODataHandler.cs:376, 389-412` | `$filter` anhängen, Test. |
| SR15-29 | MCP OAuth (WIP) | **(plausibel)** `IsEnabled` ist schon wahr, wenn EntraId oder ADFS aktiv ist, auch ohne TenantId oder Authority. Dann ist kein MCP-Schema registriert, und `/mcp` antwortet mit 500. | `GatewayMcpOAuth.cs:18, 22` (uncommittet) | Die Bedingung `AuthorizationServers(options).Count > 0` beibehalten. |

## 3. Niedrig

| ID | Befund | Fundstelle |
|---|---|---|
| SR15-30 | OData: Spalten-Orakel. `$filter` auf eine Deny- oder Mask-Spalte ergibt 403, auf eine unbekannte Spalte 400. Bei `$orderby` ist das vereinheitlicht, hier nicht. | `GatewayExecutionService.cs` ValidateFilter (~362-378), `ODataHandler.cs:322-332` |
| SR15-31 | OData: Der Flat-Alias `/odata/v4/{entitySet}` sucht über alle Tenants (403 gegen 404 als Orakel). `{Domain}_{Schema}_{Table}` ist nicht eindeutig, `FirstOrDefault` kann eine fremde Tabelle treffen **(plausibel)**. | `ODataEndpoints.cs:299-318` |
| SR15-32 | OData: Das Audit `TABLE_QUERY ALLOW` wird vor ValidateFilter/OrderBy geschrieben. Abgelehnte Inferenzversuche erscheinen deshalb als ALLOW, Prädikate fehlen. | `GatewayExecutionService.cs` ~170 gegen ~233 |
| SR15-33 | OData: LIKE-Wildcards (`%`, `_`, `[`) in `contains`/`startswith`/`endswith` werden nicht escaped. Die Semantik weicht ab, teure Muster sind möglich. | `ODataFilterParser.cs:645-686` |
| SR15-34 | SQL-Endpoints und Arrow-Export geben `argEx`/`secEx`/`WebSqlPolicyException.Message` auch in Production aus (Tabellen, Spalten, Regeltypen). | `SqlEndpointRoutes.cs:327-337, 398-408`, `ArrowExportEndpoints.cs:159` |
| SR15-35 | MCP: Der synthetische Principal verliert den Read-only-Marker und Claims. Kuratierte Tools mit Mutation laufen trotz Read-only. **(plausibel)** Casbin-Regeln auf Claim-Attributen greifen über MCP nicht. | `GatewayMcpQueryExecutor.cs:74-103, 195, 431`, `McpDatasetCatalog.cs:143` |
| SR15-36 | MCP: Four-Eyes/HitL läuft vor der Sichtbarkeitsprüfung. Die Antwort verrät versteckte Tabellen, und für sie entstehen Freigabe-Tickets. | `AiDataGuardrailService.cs:292-371`, `CatalogGraphQlMap.cs:93` |
| SR15-37 | API-13 bleibt unvollständig, auch mit WIP. Der `IDistributedCache`-Pfad ist nicht atomar, Prüfen und Zählen sind getrennt (parallele Bursts). Die Keys ohne Hash-Tag führen in Redis Cluster zu CROSSSLOT **(plausibel)**. | `BasicAuthAttemptGuard.cs:178-212`, `BasicAuthenticationHandler.cs:170-219` |
| SR15-38 | BasicAuth: **(plausibel)** Benutzernamen lassen sich über Timing aufzählen, weil Dummy-Hash und echte Hashes unterschiedlich teuer sind. | `BasicAuthenticationHandler.cs:54-104, 182-202` |
| SR15-39 | MCP OAuth-Discovery bewirbt `<aud>/.default` statt `Agent.Read`. `/.well-known/oauth-authorization-server` ist nicht RFC-8414-konform. | `GatewayMcpOAuth.cs:28-33`, `McpEndpoints.cs:117-129` |
| SR15-40 | CI: `${{ github.event.inputs.tag }}` steht direkt in `run:` (Script-Injection mit `packages: write`, nur durch Schreibberechtigte). `release.yml` baut per Dispatch von jedem Branch (DEP-11). | `.github/workflows/docker-publish.yml:60-61`, `release.yml:78` |
| SR15-41 | Epoch im Degraded-Modus: Nur `IsHighlySensitive` ist fail-closed. Tabellen mit `IsSensitive`-Spalten liefern gecachte, eventuell widerrufene Entscheidungen. | `EpochValidationService.cs:157` |
| SR15-42 | Iceberg REST `LoadTable` hat keine ReBAC-Prüfung (`can_query`). Ein leerer Tenant wird zu `default`. | `IcebergRestCatalogFederationService.cs:174ff`, `IcebergRestCatalogEndpoints.cs:25-80` |
| SR15-43 | Fallback auf Metadaten der Domain `default`, wenn die Tabelle unter der Datenquelle nicht katalogisiert ist **(plausibel)**. Kritisch bei leerem `SourceName`. | `GovernedSqlExecutionService.cs:398-402, 433-438` |
| SR15-44 | Virtuelle Filter: Der Alias `autheris_target` ist nicht reserviert. Ein `from s.t autheris_target …` überdeckt den äußeren Alias, und der Filter wirkt nicht mehr. | `VirtualFilterModels.cs:245`, `SqlFilterCompiler.cs:81` |
| SR15-45 | Virtuelle Filter: Die Tautologie- und OR-Prüfung ist leicht zu umgehen (`IS NULL OR IS NOT NULL`, `coalesce`, EXISTS). Nicht als Schutz dokumentieren. | `SqlFilterCompiler.cs:166ff, 297` |
| SR15-46 | Virtuelle Filter **(plausibel)**: Profile matchen nur eine Identitätsform (SID oder `oid`). Ein Token nur mit `oid` umgeht ein SID-Profil. Einschränkende Regeln versagen also offen. | `GranteeMatcher`, `GetUserSid` |
| SR15-47 | Virtuelle Filter: Das Audit wird erst nach dem Commit geschrieben (vgl. POL-12), es enthält nur Hashes. Abgelehnte Admin-Aufrufe und `effective-filters` werden nicht auditiert. `config-sync/status` meldet fest `InSync`. | `VirtualFilterAdministrationService.cs`, `VirtualFilterEndpoints.cs` |
| SR15-48 | Demo-Seed: Davids Consent mit Access-Level 2 (Clear inkl. `customerEmail`) wird ohne Flag angelegt, nur der Redis-Teil hängt an `ENABLE_DEMO_REBAC_SEED`. Die `SeedTuples`-Sperre ist eine Substring-Blacklist (`david`/`prod`), und Seeds werden beim Entfernen nicht widerrufen. | `seed_governance.py:804-815`, `GatewayServiceCollectionExtensions.cs:161-163, 619-645` |
| SR15-49 | Die Fehler der VirtualFilter-API geben `InvalidOperationException.Message` als 400 aus. | `VirtualFilterEndpoints.cs:361` |
| SR15-50 | Die Casbin-Startprüfung macht `ModelPath` zur Pflicht, das eingebettete Default-Modell ist damit unbrauchbar. Die Prüfung, ob die Datei existiert, läuft lazy in der Singleton-Factory. | `GatewayServiceCollectionExtensions.cs:96-101, 395-415` |
| SR15-51 | Schema-Contracts (`6bc28a9`) wirken nur als Katalog-Slicing. Die Ausführung prüft den Contract nicht. Falls Contracts als Zugriffsgrenze gedacht sind, ist das unvollständig. `CanWriteTableAsync` (`1448bbc`) wird außerhalb von Tests nirgends aufgerufen. | `CatalogVisibility.cs`, `TableAccessPolicy.cs` |
| SR15-52 | MCP-1 (`5ff70ab`) ist nur teilweise behoben: Nur statische Deny-Regeln aus dem Katalog werden beachtet, keine Deny-Spalten aus dem Consent je Nutzer. | `SemanticMcpCompiler.cs:128-137` |

## 4. Info

- **Snowflake-Generator (latent):**
  - Bei Strings wird nur `'` verdoppelt, Backslash-Escapes werden nicht behandelt.
  - Identifier werden unquotiert ausgegeben (`AnalyticalDialectGenerators.cs:152-171`).
  - Derzeit nicht verdrahtet. Vor der Aktivierung korrigieren oder in der Factory sperren.
- **AST verlustbehaftet:**
  - `INSERT … SELECT … LIMIT n` verliert `WITH`, `ORDER BY` und `LIMIT` (`SqlAstBuilder.cs:643`).
  - Ein Dereference auf einen Nicht-Spalten-Ausdruck wird zum `ToString()`-Identifier (`:906`).
  - Literale wie `1_000` und `0x1F` gehen roh durch (`:931`). Hex-`LIMIT` wirft und führt zu 500 (`:328`).
  - TABLESAMPLE, PIVOT und benannte Argumente werden stillschweigend verworfen.
  - Empfehlung: fail-loud.
- **PostgreSQL-Literale** setzen `standard_conforming_strings=on` voraus. Abgedeckt, solange `RejectBackslashInStrings` gesetzt ist.
- **WebSQL:**
  - Die Statement-ID hat nur 24 Bit Zufall; die Eigentümerprüfung greift.
  - `ParseDuration` mit `NaN` oder `1e400` führt zu 500.
  - Flight-Tickets ohne SID werden an `""` gebunden.
  - DML ist auch über Lesekanäle (Trino, Flight, Arrow, SQL-Endpoints) möglich, wenn `IsWebSqlDmlAllowed` gesetzt ist.
- **OData:** Der Host-Header fließt in `@odata.context` und `nextLink`, es gibt kein `AllowedHosts` (DEP-16).
- **PG-Repo (POL-12):** Das Audit läuft auf einer eigenen Verbindung vor dem Commit. Schlägt der Commit fehl, bleiben Einträge `CONSENT_GRANTED` für Vorgänge stehen, die nie wirksam wurden.
- **Speicher:** Das Resolver-Memo ist pro Generation unbegrenzt.
- **Prompt-Injection:** Katalogbeschreibungen, dbt-Meta und Golden-Query-Texte gehen ungefiltert in MCP-Tool-Results.
- **Doku:** Klartext-Beispielpasswörter in `docs/features/f-sql-02-governed-stored-procedures.md:368, 380` und `docs/configuration-guide.md:460, 996`.

## 5. Geprüft ohne Befund (Auszug)

- **OData:**
  - Literale sind parametrisiert, Identifier per Regex geprüft und gequotet.
  - `$filter`/`$orderby` nur auf Clear-Spalten, damit kein Inferenz-Angriff über maskierte Spalten.
  - `$count` mit Tenant und RLS in derselben Transaktion.
  - `$metadata` verlangt Anmeldung und ist spaltengefiltert, `$skip`/`$top` sind begrenzt.
- **AST:**
  - Identifier-Quoting (`]]`, `""`), `N''`, Whitelists für Typed Literals, INTERVAL, CAST und EXTRACT.
  - Funktions-Deny- und Allowlist.
  - Ein Statement, keine Kommentare, kein Dollar-Quoting, kein `E''`.
  - Row-Filter bei SELECT in allen Konstrukten (CTE, UNION, LATERAL, Fenster, GROUPING SETS).
- **WebSQL:**
  - Statements sind an Tenant und SID gebunden, mit einheitlicher 404.
  - Flight-Tickets sind HMAC-signiert und laufen ab.
  - Datenquellen-Allowlist inklusive `X-Trino-Catalog`.
  - `set_config(…, true)` in der Transaktion, `sp_set_session_context @read_only=1`.
  - DuckDB-Sandbox.
- **Virtuelle Filter:**
  - Quoting und Escaping bei strukturierten Filtern.
  - Funktions-Allowlist.
  - `ObjectPattern` mit NonBacktracking und Timeout, kein ReDoS.
  - Lade- und Auswertungsfehler führen zu deny; In-Memory, Streaming, Iceberg, Delta und Envoy verweigern.
  - Der Memo-Key enthält Tenant und Principal.
  - Tenant-Grenze der Admin-API.
- **MCP:**
  - Die Route verlangt Authentifizierung.
  - Paging erst nach dem Sichtbarkeitsfilter.
  - `sample_rows` läuft über die volle Governance.
  - `query_graphql` erlaubt nur Query.
  - `danger_*`-Flags werden außerhalb von Development blockiert.
- **Refactorings:**
  - Der entfernte tote Code (Kernel, SemanticQueryCache, Vector-Connectoren, ChunkPiiRedactor, ConnectorRowMasker) hat keine Aufrufer mehr.
  - Die Prüfungen laufen jetzt über `TableAccessPolicy`, eher strenger als vorher.
  - Cache-Keys enthalten Tenant und Principal.
  - Webhook timing-safe.
  - Dockerfile ohne Root, Actions per SHA gepinnt.

## 6. Bekannte offene Punkte (unverändert)

INF-2, INF-3, DEP-11, DEP-12, DEP-15, DEP-16, SQL2-13 und SQL2-17 stehen in [security-review-2026-10-07.md](security-review-2026-10-07.md). SR15-16 erweitert API-11, SR15-26 ergänzt DEP-15, SR15-27 betrifft DEP-12, SR15-37 betrifft API-13.

## 7. Reihenfolge

1. Build reparieren (`RequireApproval`).
2. SR15-01, 02, 04 (Absturz und RLS-Umgehung für jeden Nutzer).
3. SR15-03, 05, 06, 07, 08.
4. Die übrigen mittleren Befunde, vorrangig SR15-11, 12, 13, 16, 17, 19, 20, 24 und 25.
5. Niedrig und Info nach Gelegenheit.

Regeln wie üblich: TDD, jeder Sicherheitstest muss ohne Fix rot sein, ein Thema pro Commit.

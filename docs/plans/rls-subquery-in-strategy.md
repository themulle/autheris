# Implementierungsplan: Zeilenfilter mit `IN` statt `EXISTS` (SQL Server)

Stand: 07.10.2026. Anlass: Abfragen von `david` auf `fms.air1` (1,5 Mio. Zeilen) laufen per OData in das 10-s-Limit.

## 1. Ausgangslage

`AdvancedRlsFilterGenerator.BuildCorrelatedSubquery` erzeugt für jeden Filter vom Typ `SubqueryCorrelated` (Einwilligung `CONSENT_ROW_FILTERS`, mit `additional_hops`) genau eine Form:

```sql
EXISTS (SELECT 1 FROM [conf].[client] AS [c]
        INNER JOIN [md].[crane] AS [cr] ON [c].[crane_serial_number] = [cr].[serial_number]
        WHERE [c].[client_id] = [autheris_target].[client_id] AND [cr].[is_delivered] IS NULL)
```

Der Filter wird in WebSQL, OData/GraphQL (`SqlDataSourceExecutor`), deklarativen Abfragen, Prozeduren (`row_scope_key`) und Casbin-Regeln verwendet. Aufrufer: `RowFilterSqlBuilder.FormatCondition` / `FormatConditionParameterized`, `CasbinEnforcementService` (Zeile 477), `SqlProcedureRowScopeResolver`.

### Messungen (PoC, Produktivdatenbank LWETEM_PROD, 10-s-Limit)

Alle Zeiten sind Wandzeit der HTTP-Antwort. `a` = `fms.air1`, Unterabfrage = `conf.client c JOIN md.crane cr`, `cr.is_delivered IS NULL`.

| Abfrage (als `admin`, handgeschrieben) | Ergebnis |
|---|---|
| `EXISTS`, `ORDER BY a.ts LIMIT 5` oder `LIMIT 1000` | 0,5 bis 0,7 s |
| `EXISTS`, `ORDER BY a.client_id LIMIT 5`, `50`, `500` | **Timeout** (10,4 s) |
| `EXISTS`, `ORDER BY a.client_id LIMIT 5000` oder `10000` | 0,5 s |
| `IN (unkorreliert)`, `ORDER BY a.client_id LIMIT 5` | **Timeout** |
| `IN (korreliert: ... WHERE c.client_id = a.client_id ...)`, `ORDER BY a.client_id LIMIT 5` | **0,5 s** |
| `EXISTS`, `COUNT(*)` | 0,6 s |

| Abfrage als `david` (Filter automatisch) | Ergebnis |
|---|---|
| WebSQL `ORDER BY ts LIMIT 5`, ohne `ORDER BY` `LIMIT 5`/`1000` | 0,4 bis 0,5 s |
| WebSQL `ORDER BY client_id LIMIT 5` | **Timeout** |
| OData `fms/air1` mit `$top=5`, `$top=100`, `$top=1000`, `$select=...`, `$orderby=ts`, ohne `$top`, mit `$count` | **Timeout** |
| OData `tem/crane_state` (24 Mio. Zeilen) `$top=5` | 0,6 s |
| OData `fms/air1` als `admin` (kein Filter) | 0,8 s |

### Trino-Subselect und Join-Variante (gemessen am 07.10.2026)

**Wie Trino/Autheris den Filter anwendet.** Die eingebaute Engine (`TrinoSqlEngine/RlsListener.cs`, Zeile 938) ersetzt jede Tabelle durch ein Subselect `(SELECT <Spalten> FROM <Tabelle> AS autheris_target WHERE <Filter>) AS <Alias>`. Das ist der Weg, den WebSQL heute schon geht (siehe `securedSql` im Audit-Log). Der OData-/GraphQL-Pfad (`SqlDataSourceExecutor`) setzt den Filter dagegen direkt als `WHERE` auf die Tabelle, ohne Subselect. Das ist kein Leistungsunterschied (WebSQL als `david` ist schnell, OData nicht), aber eine Abweichung zwischen den Pfaden.

**Original Trino (Java, `trinodb/trino`, Branch master).** `RelationPlanner.visitTable` plant erst den Table Scan und ruft dann `addRowFilters` und danach `addColumnMasks` auf. `addRowFilters` legt für jeden Filter einen `FilterNode` über den Scan; Unterabfragen im Filterausdruck (`IN`, `EXISTS`) plant `SubqueryPlanner.handleSubqueries` vor dem Umschreiben des Prädikats. Trino erzeugt dabei **kein SQL und kein Subselect**; "Tabelle durch Subselect ersetzen" ist die logische Beschreibung von `Filter(TableScan)`. Die Autheris-Engine bildet das als Text nach (`RlsListener.cs:938`). Quellen: `RelationPlanner.java` (visitTable, addRowFilters), `StatementAnalyzer.java` (`accessControl.getRowFilters`, Analyse im Scope der Tabelle).

Aus dem Trino-Verhalten ist wichtig (Trino-Planerwissen, nicht aus den abgerufenen Quelltexten belegt): `IN` und `EXISTS` landen dort im selben Plan, einem Semi-Join, bei dem die kleine Seite einmal in eine Hash-Menge gelesen und die große Tabelle dagegen geprüft wird. Der Unterschied zu Autheris ist also nicht die Schreibweise, sondern dass Trino die Unterabfrage **einmal auswertet** und dann streamt, statt SQL Server die Auswertung pro Zeile oder nach einem selbst gewählten Plan zu überlassen. Das entspricht der Alternative "erlaubte Schlüsselmenge einmal laden" in Abschnitt 7 und macht sie zur naheliegendsten Nachbildung des Trino-Verhaltens.

**Join statt Semi-Join** (`air1 INNER JOIN conf.client INNER JOIN md.crane`, als `admin`, 07.10.2026):

| Abfrage | Ergebnis |
|---|---|
| `COUNT(*)` mit `EXISTS` | 250 872 Zeilen, 1,0 s |
| `COUNT(*)` mit Join und `cr.is_delivered IS NULL` | 250 872 Zeilen, 0,5 s |
| `COUNT(*)` mit Join **ohne** `is_delivered`-Bedingung (wie im Beispiel) | **1 648 366** Zeilen (falsche Menge: auch ausgelieferte Krane) |
| Join, `ORDER BY client_id LIMIT 5` | **Timeout** |
| Join, `ORDER BY client_id LIMIT 1000` | 0,4 s |
| Doppelte `client_id` in `conf.client` / doppelte `serial_number` in `md.crane` | je 0 (heute) |

Daraus:
- **Der Join liefert nur mit der Bedingung `is_delivered IS NULL` dieselbe Menge**, und nur solange `conf.client.client_id` und `md.crane.serial_number` eindeutig sind. Beides ist in der Datenbank nicht erzwungen (keine Unique-Constraints, Spalten nullbar). Bei einem Duplikat würden Zeilen vervielfacht, ein Semi-Join (`EXISTS`/`IN`) tut das nie. Für einen allgemeinen Generator ist der Join deshalb nicht geeignet.
- **Der Join zeigt dasselbe Problem wie `EXISTS`:** `ORDER BY client_id` mit kleinem `n` läuft in das Limit, mit `n = 1000` nicht. Damit ist belegt, dass es nicht an der Schreibweise des Filters liegt, sondern am Plan, den SQL Server für kleine `n` mit Sortierung nach einer Nicht-Clustered-Spalte wählt (Row Goal).

### Was daraus folgt

- Das Problem sitzt im Zusammenspiel von Zeilenfilter, Sortierung und der Zeilenzahl (`FETCH NEXT n`). Kleine `n` mit Sortierung nach `client_id` lassen SQL Server einen Plan wählen, der den Filter für jede der 1,5 Mio. Zeilen einzeln auswertet. Ab `n = 5000` wählt er einen anderen Plan.
- `EXISTS` ist nicht grundsätzlich langsam (Zeile 1 und 6 der ersten Tabelle). Eine unkorrelierte `IN`-Unterabfrage ist es in dem einen getesteten Fall auch nicht schneller. Nur die **korrelierte `IN`-Form** (Ihr Vorschlag) war schnell. Das ist eine Messung an einer Abfrageform und kein Beleg, dass `IN` allgemein besser ist.
- Offen und **vor dem Bau zu klären**: Welches SQL der OData-Pfad genau erzeugt. `SqlDataSourceExecutor` sortiert nach dem ersten Primärschlüssel aus `TableMetadata.PrimaryKeyColumns` (Standardwert `{"id"}`, `air1` hat keine Spalte `id`). Ob dort `ORDER BY client_id` oder `(SELECT 1)` steht, ist nicht geloggt und aus dem Audit-Log nicht ablesbar (nur WebSQL schreibt `securedSql`). Das WebSQL-SQL mit `ORDER BY (SELECT NULL)` ist als `david` schnell, der OData-Pfad aber nicht. Die Ursache muss also im OData-SQL liegen (Sortierung oder Projektion).
- Frühere Aggregat-Timeouts (`COUNT(*)` als `david`) traten später nicht mehr auf. Sie sind nicht erklärt (vermutlich Plan-Cache oder Last).

### Konsequenz für den Plan

Vier gleichwertige Schreibweisen (`EXISTS`, `IN` unkorreliert, Join, `IN` korreliert) verhalten sich bei kleinem `n` und `ORDER BY client_id` verschieden, und nur eine ist schnell. Das spricht dafür, dass der Plan zufällig kippt und keine Schreibweise ihn verlässlich stabilisiert. Die Join-Variante war nur ein Beispiel und ist nicht Teil des Vorhabens.

**Anforderung (Vorgabe):** Alle lesenden Pfade laufen durch die Trino-Engine (siehe Abschnitt 0); OData/GraphQL arbeiten damit analog zu WebSQL. WebSQL ist als `david` auf `fms.air1` schnell, OData nicht, obwohl der Zeilenfilter derselbe ist. Der Unterschied liegt damit in der Form des erzeugten SQL und nicht im Filter. Das hat Vorrang vor der Strategie-Option und dem Optimizer-Hinweis; beide bleiben als nachgelagerte Werkzeuge im Plan.

## 0. Alle Pfade über die Trino-Engine (Vorrang)

Verglichen sind die SQL-Formen aus dem Audit-Log (WebSQL, `securedSql`) und aus `SqlDataSourceExecutor.ExecuteRealSqlQueryAsync` (Code, nicht geloggt):

| | WebSQL (`GovernedSqlExecutionService` + `RlsListener`) | OData/GraphQL (`SqlDataSourceExecutor`) |
|---|---|---|
| Tabelle | `FROM (SELECT <Spalten> FROM t AS autheris_target WHERE <Filter>) AS t` (Subselect) | `FROM t AS autheris_target WHERE (<Filter>)` (direkt) |
| Sortierung | `ORDER BY (SELECT NULL)` oder die Sortierung der Abfrage | `ORDER BY <erste Schlüsselspalte aus PrimaryKeyColumns>`, sonst `ORDER BY (SELECT 1)`; `$orderby` wird nicht übernommen (alle `$orderby=ts`-Tests liefen ebenfalls in das Limit) |
| Seitengröße | Literal: `OFFSET 0 ROWS FETCH NEXT 1000 ROWS ONLY` | **Parameter:** `OFFSET @gql_offset ROWS FETCH NEXT @gql_limit ROWS ONLY` |
| Spalten | nur die angeforderten, ohne Konvertierung | `BuildColumnProjection` mit Typabbildung (z. B. für `datetimeoffset`) und Maskierungsausdrücken |
| Plan-Cache | `CompiledSqlQueryPlanCache` | keiner |

Besonders verdächtig: Der Wert in `FETCH NEXT @gql_limit` ist für den Optimierer von SQL Server eine Variable, die er nur beim ersten Aufruf "schnuppert" (Parameter-Sniffing); mit Literal in WebSQL schätzt er die Zeilenzahl genau. Zusammen mit einer Sortierung, die nicht dem Clustered Key entspricht, kann das den gemessenen Planwechsel erklären. Das ist eine Hypothese, belegt wird sie mit dem Test aus Abschnitt 6 (Variable gegen Literal in SSMS).

### Zielbild (verbindlich): alles über die Trino-Engine

**Vorgabe:** Jeder lesende SQL-Zugriff auf eine Datenquelle wird von der Trino-Engine (`ISqlEngine`: `Analyze`, `RewriteRls`, `RlsListener`) erzeugt. Kein Pfad baut Zeilenfilter, Maskierung, Spaltenauswahl oder Seitengrößen mehr selbst als SQL-Text. Die Variante, nur die Form in `SqlDataSourceExecutor` anzugleichen ("Weg B"), entfällt.

**Heutiger Stand der Pfade**

| Pfad | Heute | Soll |
|---|---|---|
| WebSQL (`/api/v1/sql`) | Trino-Engine | unverändert |
| Deklarative SQL-Endpunkte (`/api/v1/queries`) | `SqlEndpointExecutionService` ruft `IGovernedSqlExecutionService` (Engine) | unverändert |
| **OData, GraphQL, MCP-Datenabfragen** | `SqlDataSourceExecutor` baut den SELECT selbst (eigener Filter-, Masken-, Paging-Aufbau) | **über die Engine** (Hauptarbeit dieses Plans) |
| **Prozeduren, Zeilenbereich (`row_scope_key`)** | `SqlProcedureRowScopeResolver.BuildKeyQuery` baut `SELECT keys FROM t AS autheris_target WHERE tenant AND (Filter) AND (Schlüsseltupel)` selbst | **über die Engine** (`SELECT keys FROM t WHERE (Schlüsseltupel)`, die Engine ergänzt Filter und Mandant) |
| Prozedur-Aufruf selbst (`MssqlProcedureInvoker`, `EXEC`) | kein SELECT | **bleibt**: die Engine kann `EXEC` nicht governen; die Ergebniszeilen werden weiter über den Zeilenbereich (oben) gefiltert |
| `DuckDbOlapEngine`, Katalog-/Governance-Repositories | eigene Datenhaltung, nicht LWETEM_PROD | außerhalb des Umfangs; im Rahmen der Umsetzung gegenprüfen |

**Neuer Baustein `IGovernedTableReader`** (Application):

```
ReadAsync(TableReadRequest request, Func<DbDataReader, CancellationToken, Task> rowWriter, ct)
TableReadRequest: Principal, Tenant, Table, Columns, EqualityArguments, OrderBy, Limit, Offset, DataSource
```

Er setzt aus der Anfrage einen **Trino-SQL-Text** zusammen, in dem nur Bezeichner (geprüft und gequotet) und gebundene Client-Parameter vorkommen, und übergibt ihn derselben Pipeline wie WebSQL (`RewriteCoreAsync`, Plan-Cache, `RlsListener`, Audit, Zeitlimit, Zeilenbegrenzung):

```sql
SELECT "ts", "serv_break_press1" ...
FROM lwetem_prod.fms.air1
WHERE "client_id" = @p0               -- nur Gleichheitsargumente, nur auf Clear-Spalten
ORDER BY "ts", "client_id"            -- $orderby bzw. Clustered Key in Schlüsselreihenfolge
OFFSET 0 LIMIT 1000                   -- Literale (Ganzzahlen), keine @gql_*-Parameter
```

`SqlDataSourceExecutor` delegiert für SQL-Datenquellen an diesen Baustein. Die Rückgabe in `ReadBatchAsync` (`SqlConnectorRecordSource`) bleibt in der Form, die GraphQL/OData erwarten.

**Offene Punkte, die vor dem Bau entschieden werden** (jeweils mit Test gegen WebSQL zu belegen):

1. **Zugriffsentscheidung nur einmal.** OData/GraphQL ermitteln `AccessDecision` vorab (Spaltenfreigabe, Antwortform), die Engine ermittelt sie je Tabelle erneut in `RewriteCore`. Es darf keine zwei Entscheidungen mit unterschiedlichem Ergebnis geben. Vorschlag: `RewriteCore` nimmt für die Wurzeltabelle eine bereits ermittelte Entscheidung entgegen (interne Überladung), der Reader reicht die Entscheidung des Aufrufers durch; die Engine wendet sie an.
2. **HMAC-Maskierung.** OData maskiert HMAC-Spalten nach dem Lesen im Gateway (`gatewayHmacColumns`, SEC H-13), WebSQL im SQL (`TryBuildKeyedHmacExpression`, Schlüssel als interner Parameter). Ziel: ein Verfahren. Zu klären, ob WebSQL und OData bisher dieselben Pseudonyme erzeugen (Konsistenz von Auswertungen). Test: gleicher Eingabewert ergibt in beiden Pfaden denselben Wert.
3. **Typabbildung der Ergebnisse.** `BuildColumnProjection` (z. B. `datetimeoffset`, `bigint`) entfällt im SQL. Die Serialisierung muss danach dieselben JSON-Werte liefern wie heute. WebSQL liefert `datetimeoffset` bereits korrekt (`2026-05-27T09:34:02.9103053+00:00`).
4. **Gleichheitsargumente und Sortierung** (`context.Arguments`, `$orderby`, `$filter`): Prüfungen (Spalte existiert, Zugriffsebene Clear, SEC-01 Seitenkanal) bleiben im Reader, bevor der SQL-Text entsteht. Werte gehen als Parameter in die Abfrage (`NormalizeClientParameters`), nie in den Text.
5. **Mandantenspalte** (`RequireTenantColumn`): bisher im Executor als `WHERE tenant = @p_tenant`; künftig von der Engine (`RlsOptions`) anzuwenden. Muss dieselbe Wirkung haben, auch bei Ausnahmetabellen.
6. **Dialekte.** Oracle und Databricks müssen unterstützt werden (Vorgabe). Es gibt keinen "alten Pfad" für sie; die Engine muss sie lernen. Stand, Aufgaben und Unbekannte stehen im Abschnitt "Dialekte" unten.
7. **Rückfallschalter.** Während der Umstellung `Gateway:DataSources:ReadPath = Engine | Legacy` (Standard `Engine` im PoC, `Legacy` für Bestand). Nach belegter Ergebnisgleichheit entfällt `Legacy`, und der eigene Aufbau in `SqlDataSourceExecutor` wird gelöscht.
8. **Zeilenfilter-Form:** Die `IN`-Strategie und der Optimizer-Hinweis (Abschnitte 3 und 7) gelten dann an **einer** Stelle, in der Engine/`AdvancedRlsFilterGenerator`, und wirken für alle Pfade.

### Dialekte: Oracle und Databricks

**Stand im Code (geprüft am 07.10.2026, mit Fundstellen)**

| Baustein | SQL Server / PostgreSQL / SQLite | Oracle | Databricks |
|---|---|---|---|
| `DatabaseDialect` (Domain, `DatabaseDialect.cs:5-12`) | ja | ja | ja (Backticks, `EscapeSqlLiteral` verdoppelt `\`, `:103-106`) |
| `TargetSqlDialect` (`IRlsPolicyProvider.cs:291-300`) | ja | ja | **nein** |
| Dialekt-Generator | ja | `OracleDialectGenerator`: Quoting mit Großschreibung, Alias ohne `AS`, `:pN`, `FETCH FIRST`/`OFFSET … FETCH NEXT`, Booleans als `1 = 1`; 6 Unit-Tests | **nein**; die Factory fiele auf Ansi zurück (`SqlDialectGeneratorFactory.cs:32`), WebSQL sperrt aber vorher (`GovernedSqlExecutionService.cs:540-546`, SEC P-05) |
| Zulassung WebSQL (`IsWebSqlSupportedDialect` `:1185`, `TryMapProviderToDialect` `:1160-1181`) | ja | gesperrt | gesperrt |
| Funktions-Allowlist (`SqlFunctionAllowlists.cs`) | ja | **vorhanden** (`:62-67`) | fehlt (Rückfall auf Ansi, `:91`) |
| Client-Parameter (`NormalizeClientParameters`/`RestoreClientParameters`) | `@name` | **falsch:** `RestoreClientParameters` setzt fest `"@" + name` (`:1325`), Bindung als `@name` (`:803-827`), `BindByName` nirgends gesetzt | ebenso |
| HMAC im SQL (`TryBuildKeyedHmacExpression` `:1393-1456`) | PG, SQL Server; SQLite über UDF | **falsch:** Standardzweig erzeugt die SQLite-UDF `gateway_hmac_sha256(... AS TEXT)` | ebenso |
| Verbindung (`SqlConnectionFactory.cs:26-32`) | ja | nein (`NotSupportedException`) | nein |
| Pakete | SqlClient, Sqlite, Npgsql | `Oracle.ManagedDataAccess.Core` 23.7 **nur in Tests** | keine (ODBC, ADBC, Spark: nichts) |
| Testinstanz | – | **vorhanden:** `OracleIntegrationTests.cs` mit Testcontainers `gvenzl/oracle-free:23-slim-faststart` | keine |
| Primärschlüssel aus der Datenbank | **keine Katalog-Introspektion in irgendeinem Dialekt**; `PrimaryKeyColumns` ist standardmäßig `["id"]` (`TableModels.cs:75`), nur CDC und Row-Scope lesen `sys.indexes` | keine | keine |

**D0 ist beantwortet: Es gibt heute keinen erreichbaren Produktionspfad zu Oracle oder Databricks.** Alle Wege (`SqlDataSourceExecutor`, `ProcedureConnectionProvider`) enden in `SqlConnectionFactory:31`. Die Oracle-/Databricks-Zweige in `SqlDataSourceExecutor` (`:253`, `:288-296`, Projektionen `:461-502`) sind toter Code und zudem fehlerhaft: Sie binden `@`-Parameter und Oracle-`OFFSET` ohne `ORDER BY`. Sie werden **nicht repariert, sondern mit dem Legacy-Pfad gelöscht**; Oracle und Databricks laufen ausschließlich über die Engine (es gibt keinen Bestand, der `ReadPath = Legacy` braucht).

**Sicherheitslücke unabhängig von den Dialekten (sofort beheben, Schritt D-1):** `SqlDataSourceExecutor` nimmt den Dialekt aus dem Katalog (`Table.SourceType`, `:117`), die Verbindung aus dem Provider der Datenquelle (`:121`); niemand prüft, ob beide zusammenpassen. Ein Katalogeintrag `oracle` auf einer SQL-Server-Verbindung erzeugt Filter und Quoting für den falschen Dialekt. Fail-closed: Abweichung zwischen Katalog-Dialekt und Provider-Dialekt → Abbruch, in Executor, Engine-Reader und `ProcedureConnectionProvider`. Ebenso darf `DatabaseDialect.ParseDialect` für unbekannte Werte nicht still auf PostgreSQL zurückfallen (`:71-87`), sondern muss werfen.

#### Entscheidung Databricks-Treiber (D1)

| | ODBC (Simba) | **REST: SQL Statement Execution API** | ADBC (`Apache.Arrow.Adbc.Drivers.Databricks` + `Apache.Arrow.Adbc.Client`) |
|---|---|---|---|
| Einbindung | nativer Treiber + unixODBC im Container, `System.Data.Odbc` | `HttpClient`, eigener `DbDataReader` | rein verwaltet, `Adbc.Client` liefert `DbConnection`/`DbDataReader` |
| Parameter | nur positional `?` | **benannt `:name`, typisiert, offiziell empfohlen** | für Databricks nicht belegt (zu prüfen, ohne Bindung ausgeschlossen) |
| Zeitlimit / Grenzen serverseitig | Treiber-Timeout | `wait_timeout` (5–50 s) + `on_wait_timeout=CANCEL`, `row_limit`, `byte_limit` | Treiber-Optionen |
| Katalog/Schema | Sitzung | **pro Anfrage** (`catalog`, `schema`): keine Sitzungsinitialisierung nötig | Verbindungseigenschaft |
| Durchsatz große Ergebnisse | gut (CloudFetch) | `EXTERNAL_LINKS` + `ARROW_STREAM` (vorsignierte URLs, ≤ 15 min gültig) | sehr gut (Thrift, CloudFetch, Arrow, LZ4); Standard in Power BI |
| Reife | GA | GA | **experimentell** (0.x, Stand 0.23, April 2026) |

**Empfehlung: REST (Statement Execution API) als einziger Produktionsweg, hinter einer schmalen Schnittstelle `IWarehouseStatementClient`, die einen `DbDataReader` liefert.** Gründe:
- **Sicherheit:** benannte, typisierte Parameter mit `:name` passen direkt zum Oracle-Schema der Engine (gleiche Marker); kein nativer Code im Container; Grenzen (`row_limit`, `byte_limit`, Abbruch nach Zeitlimit) werden **zusätzlich serverseitig** durchgesetzt, nicht nur im Gateway.
- **Leistung für den Hauptfall:** OData/GraphQL/MCP lesen Seiten ≤ 1000 Zeilen. Dafür reicht `INLINE` + `JSON_ARRAY` in **einem** HTTP-Aufruf (synchron mit `wait_timeout=10s`, gleich dem Gateway-Limit, `on_wait_timeout=CANCEL`). Der Arrow-/CloudFetch-Vorteil von ADBC zählt erst bei großen Exporten.
- **Große Ergebnisse** (Export, Arrow Flight): `EXTERNAL_LINKS` + `ARROW_STREAM`, gelesen mit dem vorhandenen Paket `Apache.Arrow` 23. Die vorsignierten URLs sind Geheimnisse: nie loggen, ohne `Authorization`-Header abrufen, nur an die Speicher-Domain des Arbeitsbereichs (Allowlist im HttpClient, SSRF-Schutz wie `PluginHttpDataSourceExecutor`).
- ODBC scheidet aus (native Abhängigkeit, nur positionale Parameter, kein Vorteil gegenüber den beiden anderen).
- ADBC bleibt Option für später: Sobald der Treiber nicht mehr experimentell ist und benannte Parameter nachweislich unterstützt, kann er hinter derselben Schnittstelle getauscht werden. Messung erst, wenn Exporte über Databricks tatsächlich gebraucht werden.

**Entschieden (07.10.2026): REST für den Anfang.** Weitere Alternativen, bewertet und vorerst nicht verfolgt:

| Alternative | Wie | Warum nicht jetzt | Wann wieder prüfen |
|---|---|---|---|
| ADBC (s. o.) | verwalteter Treiber, Thrift + CloudFetch + Arrow | experimentell, Parameterbindung unbelegt | bei großen Exporten; Tausch hinter `IWarehouseStatementClient` |
| ODBC (Simba) | nativer Treiber im Container | native Abhängigkeit, nur `?`-Parameter | nicht |
| JDBC über Brücke / Sidecar (Python-, Go-, Node-Connector als eigener Dienst) | zusätzlicher Prozess spricht Databricks, Gateway spricht den Sidecar | zusätzlicher Netzsprung und Dienst, eigene Authentifizierung, zweite Angriffsfläche | nicht |
| Delta Sharing | offenes REST-Protokoll, liefert Parquet-Dateien freigegebener Tabellen | **kein SQL-Pushdown** (nur unverbindliche Prädikat-Hinweise): Zeilenfilter und Paging müssten im Gateway auf vollständig übertragenen Dateien laufen; für 1,5-Mio.-Zeilen-Tabellen untauglich | nur für kleine, freigegebene Stammdatentabellen |
| Direktes Lesen der Delta-Dateien (vorhandener `DeltaLakeDataSourceExecutor`, DuckDB `delta`) mit Unity-Catalog-Credential-Vending | Gateway liest Speicher direkt, Rechenleistung lokal | umgeht die Unity-Catalog-Rechteprüfung auf der Compute-Seite; Gateway braucht Speicherzugriff; Leistung hängt von Dateilayout und Gateway-Ressourcen ab | wenn Warehouse-Kosten oder Kaltstart das Problem sind |
| Lakebase / synchronisierte Tabellen (Databricks-verwaltetes PostgreSQL) | Delta-Tabellen werden in PostgreSQL gespiegelt, Gateway nutzt den **vorhandenen PostgreSQL-Dialekt** | Synchronisationsverzug, Zusatzkosten, nicht für alle Tabellen; ändert das Datenmodell beim Betreiber | wenn niedrige Latenz für OData/GraphQL wichtiger ist als Aktualität; ganz ohne neuen Dialekt |

Wichtig für alle Wege: Der Gateway bleibt die Stelle, die Zeilenfilter und Masken durchsetzt. Unity-Catalog-eigene Row Filter/Column Masks sind keine Alternative, sondern höchstens eine zweite Linie, weil ihr Policy-Modell vom Autheris-Modell abweicht und doppelt gepflegt werden müsste.

Authentifizierung: OAuth M2M mit Service Principal (kein PAT), Token-Cache mit Ablauf; der Principal hat in Unity Catalog nur `SELECT` auf die freigegebenen Tabellen (zweite Verteidigungslinie hinter dem Gateway). Netz: Private Link bzw. IP-Allowlist des Arbeitsbereichs.

#### Querschnitt: was für beide Dialekte gilt

- **Ein Parameterschema statt Textersatz.** `RestoreClientParameters` und die Bindung (`:803-827`) nehmen den Marker vom Dialekt-Generator (`@name` für SQL Server/PG/SQLite, `:name` für Oracle/Databricks), nicht fest `"@"`. Für Oracle ist `OracleCommand.BindByName = true` **Pflicht** (Standard ist positional: bei mehrfach verwendeten Namen würden sonst Werte vertauscht, das ist ein Sicherheitsfehler, kein Schönheitsfehler). Gesetzt wird es typisiert, nicht per Reflection wie in `MssqlProcedureInvoker.cs:281-288`.
- **HMAC für Oracle und Databricks nur im Gateway** (Verfahren der Nicht-WebSQL-Pfade, `SqlDataSourceExecutor.cs:138-158`). Kein `DBMS_CRYPTO.MAC` und keine Spark-Konstruktion: Der Schlüssel als Bindevariable wird bei Oracle in `V$SQL_BIND_CAPTURE` stichprobenartig gespeichert, bei Databricks in der Query History sichtbar; `DBMS_CRYPTO` braucht zudem eine `EXECUTE`-Berechtigung, Spark SQL hat kein HMAC. Folge: Für diese Dialekte wirkt `PreventInDbHmacKeyExposure` immer; HMAC-Spalten sind in `WHERE`, `JOIN`, `GROUP BY` und `ORDER BY` gesperrt (die Engine lehnt ab, fail-closed). Der heutige Standardzweig von `TryBuildKeyedHmacExpression` (SQLite-UDF für jeden unbekannten Dialekt) wird zu einer expliziten Liste; unbekannter Dialekt → `null`. Das schließt auch Punkt 2 der offenen Punkte oben für diese Dialekte: ein Verfahren, gleiche Pseudonyme.
- **Literale in Zeilenfiltern.** `AdvancedRlsFilterGenerator` und `RowFilterSqlBuilder` setzen Werte als Literale ein (`FormatSafeLiteral`). Für Databricks ist die Backslash-Verdopplung korrekt, solange `spark.sql.parser.escapedStringLiterals=false` (Standard) gilt; das wird per Test gegen die echte Instanz festgehalten (Eingabe `a\'; --`), nicht angenommen. Oracle: leerer Text ist `NULL` (`col = ''` trifft nie); Filter mit `''` in Oracle-Quellen werden beim Speichern abgelehnt statt still nichts zu liefern.
- **Seitengröße als Literal** (wie im Reader oben, gegen Row-Goal-/Bind-Peeking-Effekte), aber nur aus einer festen Menge (z. B. 5, 10, 50, 100, 500, 1000), sonst kleinste passende Stufe mit `row_limit`/Abschneiden im Reader. Das hält bei Oracle die Zahl der Cursor im Shared Pool klein und bei SQL Server den Plan-Cache sauber.
- **Stabile Sortierung ist Pflicht** für jedes `OFFSET` (Oracle-Zweig hat heute keine). Ohne bekannten Schlüssel → Abbruch, nicht `ORDER BY (SELECT 1)`.
- **Keyset-Paging für Folgeseiten (Leistung, empfohlen):** `nextLink` trägt den letzten Schlüssel (signiert), die Folgeseite liest `WHERE (k1, k2) > (:k1, :k2) ORDER BY k1, k2 FETCH FIRST n`. Bei Databricks rechnet `OFFSET` jede Seite neu von vorn, bei Oracle und SQL Server wächst der Aufwand linear mit der Seitennummer. Gilt für alle Dialekte und ist unabhängig vom Zeilenfilter.
- **Schalter.** `Gateway:WebSql:EnabledDialects` passt nicht mehr, weil alle Lesepfade über die Engine laufen. Neu: `Gateway:Engine:EnabledDialects` (Standard: `SqlServer, PostgreSql, Sqlite`) **und** pro Datenquelle `Enabled`; geprüft an genau einer Stelle (Abbildung `DatabaseDialect` → `TargetSqlDialect`), die Meldung nennt die freigeschalteten Dialekte aus der Konfiguration statt eines festen Textes.

#### Aufgaben

| Nr | Aufgabe | Wesentliche Dateien |
|---|---|---|
| **D-1** | Dialekt-Abgleich Katalog ↔ Provider (fail-closed), `ParseDialect` ohne PostgreSQL-Rückfall. Klein, sofort, unabhängig. | `SqlDataSourceExecutor.cs:117-121`, `ProcedureConnectionProvider.cs`, `DatabaseDialect.cs:71-87` |
| D0 | erledigt (siehe oben) | – |
| D1 | **Oracle:** `Oracle.ManagedDataAccess.Core` in `Infrastructure`, `SqlConnectionFactory` um `oracle` erweitern. Verbindungsaufbau: `BindByName` (über `OracleConfiguration.BindByName` global), `StatementCacheSize` > 0, `FetchSize` aus `RowSize × Seitengröße` je Befehl, TLS (TCPS/Wallet), `ClientId` = Principal und `ModuleName = "autheris"` für die Oracle-eigene Auditierung. **Databricks:** `IWarehouseStatementClient` (REST, s. o.), `DataSourceProvider = databricks`, `WarehouseDbDataReader` für `JSON_ARRAY` (typisiert nach dem `manifest.schema`) und `ARROW_STREAM`. | `SqlConnectionFactory.cs`, neues `Infrastructure/Databricks/` |
| D2 | **Engine Databricks:** `TargetSqlDialect.Databricks`, `DatabricksDialectGenerator` (Vorlage `DuckDb`: Kleinschreibung, `LIMIT/OFFSET`; aber Backticks mit Verdopplung, `:name`-Marker, `TRUE/FALSE`), `SqlFunctionAllowlists.Databricks` nur mit geprüften, deterministischen Funktionen (keine `reflect`, `java_method`, `read_files`, `http_request`, `ai_*`, `current_*`-Sitzungsfunktionen in Filtern, keine Tabellenwertfunktionen). Differenzialtests wie für SQL Server. | `IRlsPolicyProvider.cs`, `AnalyticalDialectGenerators.cs`, `SqlDialectGeneratorFactory.cs`, `SqlFunctionAllowlists.cs` |
| D3 | **Engine Oracle freischalten:** `IsWebSqlSupportedDialect`, `TryMapProviderToDialect`, Abbildung `:540-546` auf den Schalter umstellen; Katalognamen-Abgleich mit Großschreibung (Gegenstück zu `MatchesPostgreSqlCatalogName`, `:1200`); `RejectBracketLexerDifferentials`/`RejectDollarQuoting` für Oracle prüfen (Oracle kennt `q'[...]'`-Literale: im Lexer ablehnen). **Zielversion 19c und neuer** (19c ist die verbreitete Langzeitversion; `FETCH FIRST` und 128-Zeichen-Bezeichner gibt es ab 12.2). Der Generator darf **keine 23ai-Syntax** erzeugen (`BOOLEAN`-Typ, `SELECT` ohne `FROM DUAL`, `GROUP BY`-Alias, `IF [NOT] EXISTS`, `VALUE`-Konstruktor); Test auf 21c XE sichert das ab (s. Tests). | `GovernedSqlExecutionService.cs`, `OracleDialectGenerator.cs` |
| D4 | **Sitzung:** Oracle nach dem Öffnen einmal je Pool-Verbindung: `ALTER SESSION SET NLS_COMP=BINARY NLS_SORT=BINARY TIME_ZONE='UTC'` (binäre Vergleiche: deterministische Filter und Indexnutzung; linguistische Einstellungen würden beides brechen). Werte typisiert lesen (`OracleDataReader`), keine `TO_CHAR`-Projektionen, dann sind NLS-Datumsformate irrelevant. Databricks: keine Sitzung; `catalog`/`schema` pro Anfrage, `ANSI_MODE` bleibt an. | `SqlConnectionFactory.cs`, `GetSessionInitializationSql` |
| D5 | **Zeilenfilter.** Filterform bleibt in beiden Dialekten `EXISTS` (Oracle und Databricks/Photon setzen korrelierte `EXISTS`/`IN` mit Gleichheitskorrelation selbst in Semi-Joins um). Databricks erlaubt korrelierte `IN`/`EXISTS` in `WHERE`; Filter dürfen daher nur im `WHERE` des Subselects landen, nie in Projektion oder Fensterfunktion (Engine-Test). | `AdvancedRlsFilterGenerator.cs` (inkl. Großschreibung in `FormatTableIdentifier` für Oracle, `:291`) |
| D6 | **Katalog-Introspektion** für alle Dialekte, nicht nur die neuen: Spalten, Typen, Primärschlüssel **mit Position** (SQL Server `sys.index_columns.key_ordinal`, PG `pg_index`, Oracle `ALL_CONS_COLUMNS.POSITION`, Databricks `information_schema.table_constraints`/`key_column_usage`, Schlüssel dort nur informativ, daher zusätzlich manuell bestätigbar). Ohne verlässlichen Schlüssel kein Paging (s. o.). Deckt auch Schritt 6 aus Abschnitt 4 ab. | Katalogaufnahme, `TableModels.cs:75` (Standard `["id"]` entfernen) |
| D8 | **Databricks-Emulator für Integrationstests** (kein Arbeitsbereich vorhanden): kleiner Testdienst im Container (Python, `pyspark` lokal, Spark 4), der die benötigte Teilmenge der Statement Execution API nachbildet: `POST /api/2.0/sql/statements` (synchron, `INLINE`/`JSON_ARRAY` mit `manifest.schema`; `EXTERNAL_LINKS`/`ARROW_STREAM` mit lokal ausgelieferten Links), `GET`/`cancel`, `row_limit`, `wait_timeout`/`on_wait_timeout`, Fehlerzustände. Ausführung über `spark.sql(statement, args=…)`: **echter Spark-SQL-Parser, echte benannte Parameter `:name`**, echte Escaping-Regeln für Literale, echte korrelierte `EXISTS`/`IN`. Testdaten als Delta-Tabellen. Start per Testcontainers wie Oracle. | `tests/Autheris.Tests.Integration/Databricks/`, Dockerfile im Testprojekt |
| D7 | **Toten Code entfernen:** Oracle-/Databricks-Zweige in `SqlDataSourceExecutor`, `@pN` für Databricks in `SqlFilterProvider.cs:306`, Rückfall in `CompositeKeySqlGenerator.cs:152-158` auf die Generator-Marker umstellen. | s. links |

#### Tests und Abnahme

**Rahmen (Stand 07.10.2026):** Es gibt **keine Oracle-Quelle** und **keinen Databricks-Arbeitsbereich**. Oracle wird ausschließlich im Container getestet, Databricks ausschließlich über Unit-Tests und den Emulator (D8). Beide Dialekte werden so weit gebaut, dass sie bei Bereitstellung einer echten Quelle nur noch die Abnahme-Suite durchlaufen müssen.

**Gemeinsame Abnahme-Suite (dialektunabhängig).** Eine Testklasse mit denselben Fällen für jeden Dialekt (SQL Server, PostgreSQL, SQLite, Oracle, Databricks-Emulator); neue Dialekte erben die Fälle, statt eigene zu schreiben:
- gleiche Zeilenmenge WebSQL ↔ OData/GraphQL/MCP (Äquivalenztests aus Schritt 6), `SubqueryCorrelated`-Filter mit mehreren Hops, DENY-Filter, Mandantenspalte;
- Masken und Spaltensperren; HMAC: gleicher Eingabewert ergibt in allen Dialekten dasselbe Pseudonym (Gateway-Verfahren); HMAC-Spalte in `WHERE`/`ORDER BY` wird abgelehnt;
- Injektionsfälle für Literale in Filtern (`'`, `\`, `a\'; --`, Unicode, Nullbyte) und für Client-Parameter; Bezeichner mit Sonderzeichen werden abgelehnt;
- Paging: Seitengrößen 5/100/1000, Keyset-Folgeseite, keine Lücken und Doppelungen bei gleichen Sortwerten; ohne Schlüssel → Abbruch;
- Typen: Ganzzahl, Dezimal (Genauigkeit), Text, leerer Text, `NULL`, Datum, Zeitstempel mit/ohne Zeitzone, Binär → gleiche JSON-Werte;
- Zeitlimit und Zeilenlimit greifen (Abbruch, kein Teilergebnis ohne Kennzeichnung).

**Oracle (Container, Abnahme im Rahmen des PoC möglich)**
- Matrix aus zwei Images: `gvenzl/oracle-free:23-slim-faststart` (vorhanden) und `gvenzl/oracle-xe:21-slim-faststart`. 21c fängt 23ai-Syntax ab, die auf 19c-Quellen scheitern würde; ein 19c-Image ist nicht frei verfügbar.
- Zusätzlich Oracle-spezifisch: `BindByName` mit mehrfach verwendetem Namen, leerer Text = `NULL`, Großschreibung von Bezeichnern und quotierte gemischte Schreibweise, `NLS_COMP`/`NLS_SORT` der Sitzung wirken auch bei abweichender Datenbank-Voreinstellung (Container mit `NLS_SORT=GERMAN` starten), `TIME_ZONE`, Zeichensatz AL32UTF8, Benutzer **ohne** `EXECUTE` auf `DBMS_CRYPTO` und nur mit `SELECT` auf die Testtabellen (Least Privilege).
- Leistung im Container: Ausführungsplan (`DBMS_XPLAN.DISPLAY_CURSOR`) für den `EXISTS`-Filter auf einer Tabelle mit ≥ 1 Mio. Zeilen zeigt einen Semi-Join (Hash oder Nested Loop mit Index), kein `FILTER` pro Zeile; Anzahl Cursor pro Abfrageform ≤ Anzahl Seitengrößen-Stufen.
- **Freischaltung:** Nach bestandener Suite darf Oracle in `Gateway:Engine:EnabledDialects` aufgenommen werden. Verbleibendes Risiko (in der Freigabe zu nennen): echte Quellen können ältere Versionen, andere Zeichensätze, VPD-Policies oder eingeschränkte Rechte haben. Erste echte Quelle wird mit der Suite im Lesemodus geprüft, bevor sie produktiv geht.

**Databricks (kein Arbeitsbereich, nicht abnehmbar)**
- Unit-Tests: Generator, Allowlist, Parameterbindung; `IWarehouseStatementClient` und `WarehouseDbDataReader` gegen dokumentierte API-Antworten (inkl. `truncated`, `CANCELED`, `FAILED`, abgelaufene Links, unbekannte Typen im Manifest → Abbruch, nicht Text).
- Integration gegen den Emulator (D8) mit der gemeinsamen Suite. Der Emulator belegt Syntax, Parameter- und Escaping-Verhalten und Filtersemantik von Spark SQL.
- **Was der Emulator nicht belegt** (Lücke bis zu einem Arbeitsbereich): Photon-Pläne und Leistung, Kaltstart und Latenz des Warehouse, Unity Catalog (Rechte, `information_schema`, Schlüssel-Metadaten für D6), OAuth M2M, vorsignierte Links auf Cloud-Speicher, Abweichungen von Databricks SQL gegenüber Open-Source-Spark bei Funktionen. Daher: Allowlist nur mit Funktionen, die in beiden gleich definiert sind; Primärschlüssel für Databricks vorerst **manuell im Katalog** gepflegt statt per Introspektion.
- **Freischaltung:** nicht möglich. Databricks bleibt außerhalb von `Gateway:Engine:EnabledDialects`; der Schalter wirft beim Start, wenn `Databricks` ohne zusätzliche Bestätigung (`Gateway:Engine:AllowUnverifiedDialects=true`, nur Nicht-Produktion) eingetragen ist.
- Sobald ein Arbeitsbereich verfügbar ist: serverless SQL Warehouse (ein klassisches braucht beim Kaltstart Minuten), nur synthetische Daten, Suite unverändert gegen die echte API. Leistungsziel realistisch: ein Statement kostet auch warm einige hundert Millisekunden; das 2-s-Ziel aus Schritt 7 gilt nur bei warmem Warehouse.
- Option ohne Firmenkonto: Databricks bietet eine kostenlose Edition mit serverlosem Warehouse. Ob deren Nutzung (Registrierung, Datenschutz, nur synthetische Daten) zulässig ist, entscheidet nicht das Projekt; im Plan nur als Möglichkeit vermerkt.
- **Aufwand:** D-1 klein. Oracle (D1, D3, D4, Tests) mittel, weil Generator, Allowlist, Treiber im Testprojekt und Container vorhanden sind; der Hauptteil ist das Parameterschema und HMAC, die ohnehin für alle Dialekte zu bereinigen sind. Databricks groß: Generator, Allowlist, REST-Client mit Reader, Authentifizierung, Emulator (D8, klein bis mittel: Teilmenge der API, Spark lokal); die Abnahme gegen einen Arbeitsbereich fehlt weiterhin. D6 mittel und für die SQL-Server-Leistung ohnehin nötig.

### Schritte

1. Log-Option für das generierte SQL (`LogGeneratedSql`); damit das heutige OData-SQL für `air1` und das künftige Engine-SQL vergleichbar sind.
2. Messung in SSMS (Abschnitt 6): Variable gegen Literal, mit/ohne Subselect, Sortierung. Sie sichert Annahmen für Schritt 4 ab, blockiert ihn aber nicht.
3. `IGovernedTableReader` und `TableReadRequest` anlegen, interne `RewriteCore`-Überladung mit vorgegebener Entscheidung für die Wurzeltabelle.
4. `SqlDataSourceExecutor` für SQL-Datenquellen auf den Reader umstellen (Schalter `ReadPath`), Typabbildung und HMAC wie unter 2 und 3 beschrieben.
5. `SqlProcedureRowScopeResolver` auf die Engine umstellen (`SELECT keys FROM t WHERE (Schlüsseltupel)`), `row_scope_key`-Tests (`SqlProcedureRowScopeResolverTests`, `PostgreSqlRowScopeContractTests`) weiterverwenden.
6. Äquivalenztests: für jede Tabelle der Testfixtures dieselbe Zeilenmenge, dieselben Masken und Spaltensperren zwischen WebSQL und OData/GraphQL/MCP (SQLite als Stand-in), dazu ein Fall mit `SubqueryCorrelated`-Filter und ein Fall mit Mandantenspalte.
7. PoC-Abnahme: `david` liest `fms/air1` per OData in unter 2 s (Seitengrößen 5, 100, 1000, ohne `$top`), ohne Abweichung der Zeilenmengen gegenüber WebSQL.
8. Dialekte (D-1 bis D8 oben): D-1 sofort. Oracle direkt nach Schritt 6 (Container 21c/23ai, gemeinsame Abnahme-Suite), danach freischaltbar. Databricks danach: Generator, REST-Client, Emulator (D8); bleibt mangels Arbeitsbereich gesperrt (`AllowUnverifiedDialects` nur außerhalb der Produktion).

### OData-Härtung (Befund 07.10.2026, `$select=amount` auf `fms/air1`)

**Was passiert ist** (Log `citizen-autheris`, 07:45:45–07:46:10 Uhr, drei Aufrufe):
1. `air1` hat keine Spalte `amount` (der Name stammt aus dem Beispiel der OpenAPI-Beschreibung, `DynamicOpenApiGenerator.cs:197`).
2. `GatewayExecutionService.cs:293-300` verwirft unbekannte und gesperrte `$select`-Spalten **still** und fällt bei leerer Liste auf **alle** freigegebenen Spalten zurück. Die Abfrage war damit identisch mit `fms/air1` ohne `$select`, also der bekannte langsame OData-Fall (Abschnitt 1).
3. Nach 12,6 s `SqlException: Execution Timeout Expired` → **unbehandelte Ausnahme**, HTTP 500 als `text/html`. Der Container läuft mit `ASPNETCORE_ENVIRONMENT=Development` und ohne `UseExceptionHandler`, die Antwort ist damit die Entwickler-Fehlerseite mit Stacktrace, Quellpfaden und SQL-Fehlertext.
4. Das laufende Image (`ghcr.io/themulle/autheris:latest`, 09:39 Uhr) ist älter als Commit `3c72fa6` (10:00 Uhr), der im `ODataHandler` Zeitüberschreitung → 504 und sonstige Fehler → 500 als JSON abfängt. **Neu bauen und ausrollen schließt Punkt 3 für OData**, nicht aber 2 und nicht die Entwickler-Fehlerseite für andere Endpunkte.

**Maßnahmen (OData-Härtung, vor oder mit Schritt 4 des Abschnitts 0)**

| Nr | Konstellation | Heute | Soll |
|---|---|---|---|
| O1 | `$select` mit unbekannter Spalte | still alle Spalten | **400** `InvalidQueryOption` („Property 'amount' does not exist“), wie OData v4 vorschreibt. Gesperrte (`Deny`) Spalte: dieselbe Antwort wie unbekannt (kein Existenz-Orakel), Audit-Eintrag mit dem echten Grund. |
| O2 | `$select` nur mit gemischten gültigen/ungültigen Spalten | ungültige still entfernt | 400, keine Teilprojektion |
| O3 | `$filter`, `$orderby`, `$expand`, `$search`, `$apply`, `$compute` | **still ignoriert**, obwohl `$filter` in der OpenAPI beworben wird (`DynamicOpenApiGenerator.cs:198`): Der Client bekommt ungefilterte Daten und hält sie für gefiltert | bis zur Umsetzung **501 Not Implemented** und aus der OpenAPI entfernen; `$orderby` kommt mit dem Reader (Abschnitt 0, Punkt 4) |
| O4 | unbekannte oder doppelte Systemoptionen (`$foo`, `$top=1&$top=1000`) | ignoriert bzw. erster Wert | 400 |
| O5 | `$count=true` | zählt die Zeilen **der Seite** (`ODataHandler.cs:263`), nicht die Gesamtzahl | echte Anzahl über die Engine (gleicher Filter) mit eigenem Zeitlimit, oder 501, bis sie existiert |
| O6 | `$skip` groß (`$skip=1400000`) | `OFFSET` über 1,4 Mio. Zeilen mit Zeilenfilter | Obergrenze für `$skip` (z. B. 100 000) → 400; Folgeseiten per `@odata.nextLink` mit Keyset (Abschnitt Dialekte, Querschnitt) |
| O7 | Zeitüberschreitung, Datenbank nicht erreichbar, Deadlock, Verbindungs-Pool erschöpft | 500 HTML (altes Image) | 504 bzw. 503 mit `Retry-After`, JSON, ohne Fehlertext der Datenbank; Erkennung über `SqlException.Number` (-2, 1205, 4060, 40613 …) statt `Message.Contains("Timeout")` (`ODataHandler.cs:278`) |
| O8 | Abbruch durch den Client (Verbindung zu) | `RequestAborted` wird durchgereicht | prüfen, dass `SqlCommand` dann wirklich abbricht und kein 500 geloggt wird |
| O9 | Entwickler-Fehlerseite | `Development` im PoC-Container, kein `UseExceptionHandler` | `UseExceptionHandler` + `ProblemDetails` immer registrieren; Entwickler-Seite nur, wenn zusätzlich explizit eingeschaltet **und** an Loopback gebunden; Compose auf `Production` (bzw. eigenes `Poc`) |
| O10 | Wiederholte teure Abfragen (der Client hat zweimal nachgefasst) | jede belegt 10 s einen SQL-Server-Worker und eine Pool-Verbindung | Nebenläufigkeitsgrenze je Benutzer und je Tabelle für Lesepfade (vorhandenen `ResourceGroupManager` prüfen, ob er OData/GraphQL/MCP abdeckt), danach 429 mit `Retry-After`; Zeitlimit-Überschreitungen je Benutzer zählen und bei Häufung drosseln |
| O11 | Sehr breite Tabelle ohne `$select` | alle Spalten, inkl. `NVARCHAR(MAX)`/Binär | Standardprojektion begrenzen (Antwortgröße in Byte, nicht nur Zeilen); `MaxResponseBytes` |
| O12 | Spaltentypen, die der Reader nicht kennt (`geography`, `hierarchyid`, `sql_variant`, UDT) ohne Typ im Katalog | `reader.GetValue` wirft (`Microsoft.SqlServer.Types` fehlt) → 500 | Typ aus der Katalog-Introspektion (D6); unbekannte Typen projizieren als Text oder Spalte ablehnen, nie 500 |
| O13 | Groß-/Kleinschreibung in `$select` | case-insensitiv gegen den Katalog | so lassen, aber Ausgabe immer mit dem Katalognamen (kein Echo der Eingabe) |

**Dieselben Konstellationen für GraphQL und MCP:** Beide gehen durch `GatewayExecutionService` (gleicher stiller Rückfall bei Feldern, gleiche Zeitlimit-Behandlung). GraphQL validiert Felder gegen das Schema, MCP nicht: MCP-Werkzeugaufrufe mit unbekannten Spalten ebenfalls mit Fehler beantworten statt alle Spalten zu liefern.

**Tests:** je Zeile der Tabelle ein Endpunkttest (`ODataEndpointsTests`, `ODataTests`); Zeitüberschreitung über einen Executor, der `SqlException` Nummer -2 wirft; Prüfung, dass keine Antwort `text/html` oder einen Stacktrace enthält (für alle Endpunkte, auch unbehandelte Ausnahmen).

**Umsetzungsstand (07.10.2026, Build und Tests stehen aus):**
- O1/O2/O13 in `GatewayExecutionService` (gilt für OData, Kernel, MCP): unbekannte oder gesperrte Spalte → `GatewayInvalidQueryException` (gleiche Meldung), Katalogschreibweise, Duplikate entfernt.
- O3/O4 in `ODataEndpoints.ValidateSystemQueryOptions`; OpenAPI bewirbt nur `$select`, `$top`, `$skip`.
- O5 `$count=true` → 501. O6 `$skip` > 100 000 → 400.
- O7 `DataAccessErrorClassifier` (Fehlernummer, SQLSTATE, `IsTransient`; Text nur ohne beides) → 504/503 mit `Retry-After`.
- O8 Abbruch durch den Client → 499 ohne Fehlerlog.
- O9 `GatewayExceptionHandler` (`IExceptionHandler`, `UseExceptionHandler()` als erste Middleware): Problem-JSON ohne Details in allen Umgebungen. Compose bleibt `Development`, weil `Production` im PoC an Key-Vault-Pflichten scheitert.
- O10 `TableReadConcurrencyGate`, `Gateway:DataSources:MaxConcurrentReadsPerUserAndTable` (Standard 4) → 429. Drosselung nach wiederholten Zeitüberschreitungen: offen.
- O11 Größenprüfung schon beim Lesen (`SqlDataSourceExecutor.ReadRowsAsync`, gleiches Limit `GraphQL.MaxResponseBytes`), OData meldet 400 statt 403.
- O12 nicht lesbarer Spaltentyp → `GatewayUnsupportedColumnTypeException` → 501 mit Spaltenname.
- D-1: `ParseDialect` ohne stillen Rückfall (leer, unbekannt, undefinierte Zahl), SQL-Tabellen fail-closed, Nicht-SQL-Quellen neutral; Abgleich in `SqlDataSourceExecutor` mit effektivem Provider; Katalog-Synchronisation (Alation, Collibra, Purview, OpenMetadata) überschreibt einen unterstützten Dialekt nicht mehr. WebSQL und Prozedur-Row-Scope hatten den Abgleich bereits.

### GraphQL im Vergleich zu Hasura (Befund 07.10.2026)

Hasura übersetzt den ganzen Abfragebaum in **ein** SQL, lässt die Datenbank das JSON bauen (`FOR JSON PATH`, `json_agg`/`json_build_object`), setzt Berechtigungen je Ebene als `WHERE` mit Bind-Parametern ein und reicht das JSON unverändert durch. Autheris macht heute nichts davon:

| | Hasura | Autheris heute (`QueryTypes.GetTableAsync`) |
|---|---|---|
| Abfrageform | ein SQL für den ganzen Baum | ein flaches SQL je Wurzelfeld, keine Joins |
| Projektion | nur angefragte Felder | **alle** freigegebenen Spalten (`requestedFields: null`); es gibt keine typisierten Felder, `DynamicTableType` ist nicht registriert, das Ergebnis ist eine Liste von JSON-Strings |
| Relationen | geschachtelt in einem Statement | **nicht vorhanden** (`TableRelation`/`GetRelationsForTableAsync` ungenutzt); einziger DataLoader ist Demo-Code ohne SQL |
| Filter/Sortierung | `where`/`order_by` im SQL | nur `first`/`after` (Offset); `SqlFilterProvider` registriert, aber ungenutzt |
| Berechtigungen | `WHERE` je Ebene, Bind-Parameter | Zeilenfilter als validierter Text im `WHERE` (Werte als Literale), danach zweite Masken-/Sperr-Runde im Speicher |
| JSON | von der DB, durchgereicht | Reader → Dictionary → Kopie → neue Dictionary → Größenschätzung → `JsonSerializer` je Zeile → HotChocolate serialisiert die Strings erneut |
| Vorhandener Baustein | – | `SingleQueryAstCompiler` (FOR JSON PATH / `json_agg` / `json_group_array`, RLS je Ebene Pflicht) ist registriert und aktiviert, **wird aber nirgends aufgerufen** |

Folgerung: Der Ansatz kann Hasura überlegen sein (Trino-Engine mit Plan-Cache, Policy-Modell mit Einwilligungen, Masken und Audit, mehrere Dialekte), ist es heute im GraphQL-Pfad aber nicht. Vorschlag als eigener Abschnitt nach Schritt 4 (Engine-Reader):
1. **G1 Typisiertes Schema** je Tabelle und Rolle (`DynamicTableType` registrieren; nur Spalten mit Zugriff), Feldauswahl → `requestedFields` (Projektion).
2. **G2 Relationen** aus `TableRelation` als Navigationsfelder; Kompilierung des ganzen Auswahlbaums über die Engine zu **einem** Statement je Wurzelfeld (Ausbau von `SingleQueryAstCompiler`: Wurzel ebenfalls als JSON, PostgreSQL mit `LEFT JOIN LATERAL`, Zeilenfilter je Ebene über `RewriteRls`, Werte als Parameter).
3. **G3 JSON-Durchreichung:** DB-JSON als Rohwert an HotChocolate (`JsonElement`/Raw-Value), Masken und HMAC dann in SQL bzw. im Gateway nur für HMAC-Spalten; keine Doppelserialisierung.
4. **G4 `where`/`order_by`** über `SqlFilterProvider` (parametrisiert), Keyset-Paging.
5. **G5 Kosten je Anfrage senken:** Audit-Schreiben asynchron gepuffert, Zugriffsentscheidung je Anfrage und Tabelle einmal (Memoization), Metadaten im Speicher.

Messlatte wie für OData: `fms/air1` mit zwei Ebenen in unter 2 s, ein Datenbank-Roundtrip je Wurzelfeld, gleiche Zeilenmengen wie WebSQL.

## 2. Ziel

SQL Server soll für Zeilenfilter dieser Art einen stabilen, schnellen Plan wählen, unabhängig von `FETCH NEXT n` und Sortierung. Dafür bekommt der Generator eine **konfigurierbare Strategie** für die Form des Filters. Die Standardform bleibt `EXISTS` (kein Verhaltenswechsel für bestehende Installationen).

Nicht Ziel: Indizes in LWETEM_PROD anlegen (Änderung an der Produktionsdatenbank, Sache der DBAs), Änderung des Einwilligungs- oder Filtermodells.

## 3. Entwurf

### 3.1 Konfiguration

Neue Option `Gateway:RowFilters:SubqueryStrategy` (Enum `RowFilterSubqueryStrategy`):

| Wert | Erzeugte Form |
|---|---|
| `Exists` (Standard) | wie heute |
| `InCorrelated` | `target.fk IN (SELECT dep.pk FROM dep AS d [joins] WHERE d.pk = target.fk AND <Prädikate>)` (Form aus Ihrem Vorschlag) |
| `In` | `target.fk IN (SELECT dep.pk FROM dep AS d [joins] WHERE <Prädikate>)` (unkorreliert) |

Die Strategie gilt nur für SQL Server (`DatabaseDialect.SqlServer`). Andere Dialekte erzeugen weiter `EXISTS`, weil dort nichts gemessen ist und Postgres/Oracle den Unterschied normalerweise wegoptimieren.

### 3.2 Ausnahmen (immer `EXISTS`)

1. **DENY-Filter.** Sie stehen als `NOT (...)` im Gesamtprädikat (`RowFilterSqlBuilder`, `NOT (combinedDeny)`). `NOT (x IN (...))` wird bei `NULL` zu `UNKNOWN` und blendet Zeilen aus oder ein, wo `NOT EXISTS` das Gegenteil tut (`x NOT IN` mit `NULL` in der Unterabfrage). Der Generator bekommt dazu einen Parameter `isDeny`, den `FormatCondition` und `FormatConditionParameterized` bereits kennen.
2. **Zeitliche Gültigkeit** (`TargetTemporalColumn`, `DependentValidFromColumn/-ToColumn`). Die Bedingungen vergleichen Spalten der Zieltabelle mit Spalten der abhängigen Tabelle und sind nur korreliert ausdrückbar. Bei `In` bleibt es dafür bei `EXISTS`, bei `InCorrelated` gehen sie in das `WHERE` der Unterabfrage wie heute.
3. **Casbin-Korrelationsfilter** (`CasbinEnforcementService.cs:477`): zunächst `Exists`, solange die Strategie dort nicht geprüft ist.

### 3.3 Semantik (Gleichwertigkeit für ALLOW)

`target.fk IN (SELECT d.pk ... )` und `EXISTS (... WHERE d.pk = target.fk ...)` liefern für ALLOW dieselbe Zeilenmenge, auch bei `NULL`: Ein `NULL`-Fremdschlüssel ergibt in beiden Fällen keinen Treffer. `NULL`-Werte in der Unterabfrage stören `IN` nicht (nur `NOT IN`). Das ist per Test festzuhalten.

### 3.4 Sicherheit

- Der Filter referenziert die Zieltabelle weiter ausschließlich über den reservierten Alias `autheris_target` (`RowFilterAliases.Target`). `RowFilterAliases.ReferencesTarget` muss auch für `In` (Alias nur auf der linken Seite) und `InCorrelated` wahr bleiben, sonst behandelt der Executor den Filter als unkorreliert.
- `SqlSecurityValidator.ValidatePredicateSql` und der AST-Visitor in `TrinoSqlEngine` (`AstSecurityVisitor`, Alias-Rebasing) müssen `IN (SELECT ...)` akzeptieren und den Alias korrekt verarbeiten. Das ist **Schritt 1 der Umsetzung**, weil davon abhängt, ob die neue Form überhaupt durch die Prüfung geht.
- Fail-closed bleibt: Bei einer unbekannten Strategie oder ungültigen Eingaben wirft der Generator wie heute (`InvalidOperationException`), nie ein offener Filter.

## 4. Umsetzungsschritte

| Nr | Schritt | Dateien |
|---|---|---|
| 0 | **Messung und Logging.** Option `Gateway:Logging:LogGeneratedSql` (Standard aus), die in `SqlDataSourceExecutor` (OData/GraphQL), `GovernedSqlExecutionService` (WebSQL) und der Prozedur-Row-Scope-Auflösung den ausgeführten SQL-Text auf `Debug` loggt (ohne Parameterwerte, ohne Masking-Schlüssel). Achtung: Zeilenfilter setzen Werte als Literale ein (`FormatSafeLiteral`), das Log enthält also Policy-Werte (z. B. erlaubte Schlüssel); nur `Debug`, eigene Log-Kategorie, nicht in zentrale Sammelsysteme, standardmäßig aus. Damit das OData-SQL für `air1` sichtbar wird. | `SqlDataSourceExecutor.cs`, `GovernedSqlExecutionService.cs`, `GatewayOptions.cs` |
| 1 | **Verträglichkeit prüfen.** Unit-Tests für `ValidatePredicateSql` und die AST-Rewrites mit `IN (SELECT ...)`-Filtern (SQL Server). Läuft das nicht, vor allem anderen dort anpassen. | `SqlSecurityValidator.cs`, `TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.cs`, `tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorRlsTests.cs` |
| 2 | **Enum und Option** `RowFilterSubqueryStrategy` und `RowFilterOptions.SubqueryStrategy`, Bindung unter `Gateway:RowFilters`. | `GatewayOptions.cs`, `Domain/Model` |
| 3 | **Generator.** `AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, dialect, strategy = Exists, isDeny = false)` erzeugt je Strategie die Form aus 3.1 und fällt für DENY, Zeit-Bedingungen und Nicht-SQL-Server auf `EXISTS` zurück. Der Aufbau von Join-Hops und Prädikaten wird wiederverwendet; nur der Rahmen (`EXISTS (SELECT 1 ... )` gegen `fk IN (SELECT pk ...)`) ändert sich. | `AdvancedRlsFilterGenerator.cs` |
| 4 | **Weiterreichen.** `IRlsFilterGenerator`/`RlsFilterGenerator` und `RowFilterSqlBuilder` erhalten die Strategie aus den Optionen (Konstruktor) und übergeben `isDeny`. Casbin-Aufruf bleibt bei `Exists`. | `IRlsFilterGenerator.cs`, `RlsFilterGenerator.cs`, `RowFilterSqlBuilder.cs`, `CasbinEnforcementService.cs` |
| 5 | **Plan-Cache.** Der Hash des kompilierten SQL (`CompiledSqlQueryPlanCache`, `policyHash`) muss die Strategie enthalten, damit ein Wechsel keine alten Pläne ausliefert. | `CompiledSqlQueryPlanCache.cs`, `GovernedSqlExecutionService.cs` |
| 6 | **Sortierung im Reader (Abschnitt 0):** `ORDER BY` nach dem Clustered Key in Schlüsselreihenfolge und `$orderby`; zuvor, falls das Log `ORDER BY client_id` zeigt, schon ein Befund. Die Reihenfolge der Primärschlüsselspalten muss der Reihenfolge im Schlüssel entsprechen (bei `air1`: `ts, client_id`), nicht der alphabetischen. Dazu bei der Katalogaufnahme die Schlüsselposition (`key_ordinal`) mitführen. | `SqlDataSourceExecutor.cs`, Katalogaufnahme (`OpenApiIngestionService.cs` und Aufnahme aus SQL Server) |
| 7 | **PoC.** Compose-Variable `AUTHERIS_ROWFILTER_STRATEGY` setzt `Gateway__RowFilters__SubqueryStrategy`. | `POC_Backstage_citizen_dev/autheris/docker-compose.autheris.yaml` |

## 5. Tests

Unit (`AdvancedRlsSubqueryTests`, `MultiDomainCrossDialectRlsTests`, `CorrelatedRowFilterAliasTests`, `PredicateBracketIdentifierTests`):
- Je Strategie die erwartete SQL-Server-Form, andere Dialekte unverändert `EXISTS`.
- DENY und zeitliche Gültigkeit erzeugen bei `In`/`InCorrelated` weiter `EXISTS`.
- Mehrere Hops, Prädikat-JSON, Alias-Rebasing (`target` → `autheris_target`) in beiden neuen Formen.
- `ReferencesTarget` für beide neuen Formen wahr.
- Standard (`Exists`) bleibt Byte-gleich: die bestehenden Tests mit `EXISTS (SELECT 1` laufen unverändert.

Integration (SQLite als Stand-in, soweit die Form dort gilt): gleiche Zeilenmenge für `Exists` und `InCorrelated`, auch bei `NULL`-Fremdschlüsseln.

Messung (nicht automatisierbar, gegen LWETEM_PROD): die Tabelle aus Abschnitt 1 mit den drei Strategien wiederholen, vor allem OData `fms/air1` als `david` mit `$top=5`, `$top=1000` und ohne `$top`.

## 6. Reihenfolge und Abnahme

1. Schritt 0 bauen und ausrollen. Mit dem Log das OData-SQL für `air1` holen und in SSMS mit tatsächlichem Ausführungsplan ausführen. **Zuerst der Vergleich aus Abschnitt 0:** dasselbe SQL einmal mit `DECLARE @gql_limit int = 1000, @gql_offset int = 0` und einmal mit den Literalen `0` und `1000`, ohne Subselect und mit WebSQL-Subselect. Danach: `EXISTS`, `InCorrelated`, `In` und zusätzlich mit `OPTION (USE HINT('DISABLE_OPTIMIZER_ROWGOAL'))`, jeweils mit der `ORDER BY` aus dem Log und mit `ORDER BY ts`. Das entscheidet, ob Schritt 6 (Sortierung), der Hinweis (Abschnitt 7) oder die Strategie (Schritt 3) den Ausschlag gibt. Nur die Variante, die in SSMS für `n = 5`, `500` und `1000` stabil schnell ist, wird gebaut.
2. Schritte 1 bis 5 bauen, PoC mit `InCorrelated` fahren.
3. Abnahme: `david` liest `fms/air1` per OData mit `$top=1000` in unter 2 s, ohne `$top` (Seitengröße 1000) in unter 2 s, und `crane_state` bleibt unter 1 s. Die Mengen sind mit `Exists` identisch (Stichprobe `COUNT`).

## 7. Risiken und Alternativen

- **Planinstabilität bleibt möglich.** SQL Server kann den Plan je nach Statistik und `n` wieder wechseln. Eine Umformung ersetzt keine Maßnahmen an der Ursache. Alternativen, falls die Messung es zeigt:
  - **Wahrscheinlich der wirksamste Hebel:** Hinweis `OPTION (USE HINT('DISABLE_OPTIMIZER_ROWGOAL'))` (oder `OPTION (HASH JOIN)`) an die von Autheris erzeugten SELECTs anhängen (SQL Server ab 2016 SP1). Er wirkt gegen die gemessene Ursache (Planwechsel durch das Row Goal bei kleinem `FETCH NEXT n`), unabhängig von der Schreibweise des Filters. Anzuhängen im OData-Pfad (`SqlDataSourceExecutor`), in WebSQL (SQL-Server-Dialekt-Generator) und bei Prozeduren, nur für SQL Server und per Option abschaltbar (`Gateway:DataSources:Connections:<name>:DisableRowGoal`). Vor dem Bau in SSMS zu belegen (Abschnitt 6).
  - **Der OData-Pfad sollte das Trino-Subselect übernehmen** (`FROM (SELECT <Spalten> FROM t AS autheris_target WHERE <Filter>) AS t`), damit alle Pfade dieselbe Form erzeugen. Kein Leistungsgewinn zu erwarten, aber weniger Abweichung.
  - **Trino-Nachbildung:** Erlaubte Schlüsselmenge einmal laden (eigene, schnelle Abfrage der Unterabfrage: im Test 0,4 s für alle `client_id`), zwischenspeichern und als `fk IN (@k1, ...)` oder temporäre Tabelle in die eigentliche Abfrage einsetzen. Das ist das Verhalten von Trinos Semi-Join (Build-Seite einmal, dann Probe) und unabhängig von Indizes und Row Goal. Ein größerer Eingriff: Gültigkeit des Caches (Widerruf, neue Zuordnung), Größenbegrenzung der Liste (SQL Server: höchstens 2100 Parameter, sonst temporäre Tabelle oder `OPENJSON`), Mandantentrennung im Cache-Schlüssel.
  - Indizes durch die DBAs: `conf.client(client_id) INCLUDE (crane_serial_number)` und `md.crane(serial_number) INCLUDE (is_delivered)`. `conf.client.crane_serial_number` ist `NVARCHAR(MAX)` und kein Indexschlüssel; als `INCLUDE`-Spalte ist es möglich.
- **Plan-Cache und Auditspuren** enthalten bei geänderter Strategie neuen SQL-Text; Auswertungen, die auf `EXISTS (SELECT 1` suchen, müssen angepasst werden.
- **Aufwand** grob: Schritt 0 klein, Schritte 1 bis 5 mittel (Generator ist kompakt, aufwendig sind Validator, AST und die Tests), Schritt 6 klein bis mittel je nach Katalogaufnahme. Es gibt keinen .NET-SDK-Zugriff auf dem Arbeitsrechner des Assistenten, gebaut und getestet wird in der CLI.

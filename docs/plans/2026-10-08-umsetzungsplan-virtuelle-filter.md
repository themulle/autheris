# Umsetzungsplan (TDD): Virtuelle Filter in Autheris, Schritte 2 und 3

**Bezug:** [Entwurf](2026-10-08-virtuelle-filter.md), Abschnitt 6, Schritte 2 („Autheris, Kern“) und 3 („Autheris, Erweiterung“).
**Stand:** 08.10.2026, Code-Stand `ec51cf4`. Der Plan beruht auf Lesen des Codes; gebaut und getestet ist nichts davon.

**Abhängigkeiten:**
- Talos-Schritt 1 (Definition als Datei, Bereitstellung über Einwilligungen je Tabelle) läuft unabhängig und liefert die fachlichen Testfälle.
- Talos-Schritt 4 (Bereitstellung über die API, `grant_row_scoped_user.py` entfernen) braucht Phase 2 (API mit `plan`/`apply`) und Phase 6 (Auswertung).

Jede Phase ist ein Commit und beginnt mit fehlschlagenden Tests.

---

## Befunde aus dem Code

### 1. Kanäle und Zeilenfilter

`ConsentResolutionService.ResolveAccess` wird nur an drei Stellen aufgerufen:
- `TableAccessPolicy.cs:231`
- `StreamRlsPolicyEnforcer.cs:319`
- `IcebergRestCatalogFederationService.cs:206`

Fast alle Kanäle laufen über `TableAccessPolicy.DecideAsync`. Die Klasse wird nicht per DI erzeugt, sondern an fünf Stellen direkt:
- `GatewayExecutionService.AccessPolicy()`
- `GovernedSqlExecutionService.AccessPolicy()`
- `GovernedProcedureExecutionService.cs:332`
- `UnifiedPolicyDecisionPoint.cs:78`
- `DefaultCrossDomainAccessResolver.cs:39`

| Kanal | Entscheidung | Wo der Zeilenfilter wirkt | Bewertung |
| :--- | :--- | :--- | :--- |
| WebSQL `/api/v1/sql`, Trino `/v1/statement` (gleicher Handler) | `GovernedSqlExecutionService` → TableAccessPolicy | `rlsParts`; als SQL im Zieldialekt eingesetzt (`PolicyFiltersAreTargetDialectSql`) | ok |
| Arrow-Export, Flight SQL, SQL-Endpunkte | über `IGovernedSqlExecutionService` | wie WebSQL | ok |
| OData, GraphQL-Tabellenpfad | `GatewayExecutionService` → TableAccessPolicy | `SqlDataSourceExecutor` (Alias `autheris_target`); Connector-Pfad | ok |
| GraphQL-Baumpfad | `GovernedTreeQueryService` → `ITableAccessResolver` (= GatewayExecutionService) | `TreeSqlCompiler.cs:459-463` | ok |
| GraphQL-CDC-Subscriptions | `CdcSubscriptionGovernor` über `ITableAccessResolver` | – | ok |
| MCP | `McpDatasetCatalog` (GatewayExecutionService), `query_graphql` (GraphQL); MCP-RAG über `UnifiedPolicyDecisionPoint` | wie oben | ok |
| OLAP/DuckDB | `DefaultCrossDomainAccessResolver` | `PushdownFilterSql` → `SqlConnector` → `SqlDataSourceExecutor` | ok |
| Prozeduren | `GovernedProcedureExecutionService.cs:332` | siehe Befund 5 | ok |
| Lakehouse/Delta | über GatewayExecutionService | im Speicher (`LakehouseDataSourceExecutor.cs:77-83`, `DeltaLakeDataSourceExecutor.cs:184-190`); `EXISTS` ergibt still ein leeres Ergebnis | fail closed, aber ohne Hinweis |
| HTTP- und andere Connectoren | `GovernedConnectorReader.cs:123-125`, im Speicher | wie Lakehouse | fail closed, aber ohne Hinweis |
| **CDC/Stream** | **`StreamRlsPolicyEnforcer` ruft `ResolveAccess` direkt auf** (gleicher Cache-Schlüssel wie TableAccessPolicy) | `StreamingRowFilterAstEvaluator`: Unterabfragen ergeben `false` | **läuft an TableAccessPolicy vorbei**, eigens anbinden |
| **Iceberg-REST-Katalog** | **`IcebergRestCatalogFederationService` direkt** | lehnt ab, wenn eingeschränkt (`:245-252`) | **läuft vorbei**, eigens anbinden |
| Casbin | eigene Filter, `TableAccessPolicy.Restrict` hängt sie per AND an | – | keine Umgehung |
| Envoy ext_authz | `EnvoyExtAuthzService.cs:195`: nur Casbin, keine Einwilligung, kein Zeilenfilter | – | bestehende Lücke, nicht Teil dieses Plans (Entscheidung 7) |

`RlsListener` und `AstSecurityVisitor` sind kein eigener Kanal, sondern die Rewriter von WebSQL; sie bekommen die Filter über `tableRlsFilters`.

### 2. Verknüpfung der Einwilligungen, Einhängepunkt, Zwischenspeicher

- **Verknüpfung heute:**
  - ALLOW-Filter werden per OR verbunden (`RowFilterSqlBuilder.cs:37-59`).
  - Eine einzige ALLOW-Einwilligung ohne Filter hebt jeden Filter auf (`:38-41`; Spaltengruppen `ConsentResolutionService.cs:88-91`).
  - DENY-Filter werden als `NOT (… OR …)` angehängt (`:61-82`).
- **`ResolveAccess` kennt nur den `TableIdentifier`, keine Spalten.** Den Spaltenbedarf einer Bindung kann es nicht prüfen.
- **Zwischenspeicher:**
  - Der Rückgabewert wird gecacht (`TableAccessPolicy.cs:214-238`, `StreamRlsPolicyEnforcer.cs:297-322`).
  - Schlüssel ist `tenant:consent:sid:context:table` (`ConsentCacheService.cs:336-341`), gültig solange die Epoche **je Tabelle** stimmt.
  - `IEpochValidationService` invalidiert nur einzelne Tabellen; eine geänderte Bindung betrifft aber eine unbekannte Menge.
  - **Folgerung:** Das Pflichtprädikat wird **nach** dem Consent-Cache angehängt, nicht in `ResolveAccess`.
- **L2-Cache:** `CachedConsentEnvelope.ToDecision` verliert `RowFilterParameters` (`:66-72`), deshalb verwenden Pflichtprädikate nur Literale.
- **Plan-Cache:** `ComputePolicyHash` hasht den Filtertext je Tabelle. Ein Prädikat in `CombinedRowFilterSql` ist damit automatisch Teil des Schlüssels. Zeitfunktionen bleiben Text (`SYSDATETIMEOFFSET()`), ein Zeitwert wird nie festgehalten.
- **Längengrenze:** `SqlSecurityValidator.ValidatePredicateSql` lässt höchstens 2000 Zeichen zu (`:61`). Das ist ein Risiko bei mehreren Bindungen (Entscheidung 9).

### 3. Was `ConsentRowFilter`, `RowFilterSqlBuilder` und `AdvancedRlsFilterGenerator` schon können

- **Vorhanden:**
  - Korrelation über genau **ein** Paar `dep.pk = autheris_target.fk` (`AdvancedRlsFilterGenerator.cs:105`).
  - Mehrfach-Hops als `INNER JOIN` (`:84-100`).
  - Bedingungen Spalte gegen Literal, auch `IS [NOT] NULL` (`:220-313`).
- **Der David-Fall passt ohne Erweiterung:**
  - `DependentTable=conf.client` (Alias `client`), PK und FK `client_id`;
  - Hop `md.crane` mit `crane.serial_number = client.crane_serial_number`;
  - Bedingung `[{"column":"crane.is_delivered","op":"EQ","value":null}]`.
- **Lücken:**
  - keine Mehrfach-Schlüssel (Spalte gegen Spalte);
  - `valid_to` wirkt nur zusammen mit `DependentValidFromColumn` (`:116`);
  - `In`/`InCorrelated` nur auf SQL Server, sonst immer `Exists` (`:62`);
  - die Strategie ist global (`RowFilterSqlBuilder.cs:29`);
  - Schema und Tabelle der Filterquelle müssen `^[a-zA-Z_]…$` erfüllen (`:323-324`), daran scheitert z. B. `LWEW2K\LWEMUM2`;
  - SQLite verwirft das Schema (`:330`).
- **Zielalias:** Das gefilterte Objekt heißt in allen SQL-Pfaden `autheris_target` (`RowFilterAliases.cs:12`).

### 4. Datumsfunktionen und Dialekte

- **`current_timestamp`** ist schon übersetzt:
  - SQL Server: `SYSDATETIMEOFFSET()` (`SqlServerDialectGenerator.cs:54`); damit ist Entwurf 3.8 Nr. 3 für SQL Server erledigt.
  - SQLite: `CURRENT_TIMESTAMP` (UTC-Text).
- **`date_add`, `date_diff`, `date_trunc`, `now()`** werden nicht übersetzt; Funktionsaufrufe gehen unverändert in Großbuchstaben durch (`SqlDialectGeneratorBase.cs:958-1010`).
- **`INTERVAL`** wirft auf SQL Server und SQLite eine `AstBuildException` (`SqlServerDialectGenerator.cs:157`, `SqliteDialectGenerator.cs:91`).
- **Allowlist:**
  - Sie wird beim AST-Aufbau mit der Liste des **Zieldialekts** geprüft (`SqlAstBuilder.cs:870-876`, `GovernedSqlExecutionService.cs:631`).
  - Für Filter braucht es eine eigene Trino-Liste, die vor der Übersetzung geprüft wird.
  - `now` und `date_trunc` stehen heute nur in der PostgreSQL-Liste (`SqlFunctionAllowlists.cs:39`).

### 5. Prozeduren

Eine Nachfilterung über Schlüssel gibt es schon:
- `GovernedProcedureExecutionService.cs:144-150` lässt einen Zeilenfilter nur auf der `ResultTable` mit `RowScopeKey` zu und lehnt sonst ab (`:345`).
- `SqlProcedureRowScopeResolver.cs:240-286` führt `SELECT keys FROM tabelle AS autheris_target WHERE tenant AND (CombinedRowFilterSql) AND (Schlüssel-Tupel)` aus:
  - der Schlüssel muss eindeutig sein;
  - nur SQL Server, PostgreSQL und SQLite;
  - auf PostgreSQL in einer READ-ONLY-Transaktion.

**Folgerung:** Steht das Pflichtprädikat in `CombinedRowFilterSql`, sind Prozeduren ohne Zwischenspeicher für Wertemengen abgedeckt, und fail closed.

### 6. Persistenz und Admin-API

- **Schema:** Partielle Klassen `SqliteGovernanceRepository.*.cs` und `PostgreSqlGovernanceRepository.*.cs` mit `CREATE TABLE IF NOT EXISTS` und `Ensure*Columns`. Es gibt kein Migrations-Framework.
- **Audit:** Hash-Kette in `AUDIT_LOG_ENTRIES` über `IAuditLogRepository.RecordAuditEventAsync`.
- **Epochen:** Nach Schreibvorgängen invalidiert das Repository die Epoche je Tabelle.
- **API:**
  - Minimal-API mit Rollenprüfung im Handler; Vorbild ist `RebacEndpoints.cs:53-105`.
  - Rollen in `GatewayRole.cs:10-23`; eine Rolle für Filter fehlt.
- **PostgreSQL-Kontrakttests** laufen über Testcontainers (`PostgreSqlGovernanceContractTests`).

### 7. Testkonventionen

- **Pakete:** xUnit, Shouldly, NSubstitute, FsCheck.Xunit.
- **Echtes SQLite:** `CorrelatedRowFilterAliasTests` (Temp-Datei), `GovernedTreeQueryServiceTests` (In-Memory).
- **Integration:** `WebApplicationFactory<Program>` mit SQLite (`EndToEndSqliteTests`). Dort ist aber `danger_bypass_consent_checks` gesetzt; Phase 0 braucht eine Fabrik ohne Bypass.
- **Gleichheit über Kanäle:** Für Zeilenfilter gibt es **keinen** Test (nur `PolicySimulationParityTests`).
- **Architektur:** Domain darf weder Application noch TrinoSqlEngine referenzieren (`ArchitectureTests.cs:18-22`).

---

## Einhängepunkt

Neuer Dienst `IMandatoryRowFilterResolver` (Application). Er arbeitet auf einem unveränderlichen Abbild aller Filter und Bindungen mit einer globalen **Generation**.

**Angehängt an drei Stellen:**
1. `TableAccessPolicy.DecideAsync`, nach `ResolveConsentAsync` und vor Casbin;
2. `StreamRlsPolicyEnforcer`, nach `ResolveConsentDecisionAsync`;
3. Iceberg, nach `ResolveAccess`.

**Änderung an der Entscheidung:**
- `TableAccessDecision` bekommt die optionalen Felder `MandatoryRowPredicateSql` und `AppliedVirtualFilters`.
- `CombinedRowFilterSql` wird zu `(Consent) AND (Pflicht 1) AND (Pflicht 2)`. Die Kanäle bleiben unverändert.
- Der Consent-Cache bleibt frei von Bindungen.
- Das Ergebnis des Resolvers wird je `(Generation, Tenant, SID, Kontext-Hash, Tabelle, Dialekt)` gemerkt.

---

## Phase 0: Vergleichstest über alle Kanäle (Ausgangslage)

**Tests:** `tests/Autheris.Tests.Integration/RowFilterChannelParityTests.cs`
- `WebApplicationFactory`, Governance-DB SQLite im Speicher, Daten-DB SQLite als Datei, **ohne** Consent-Bypass.
- **Daten:** Tabellen `client`, `crane`, `air1`; sechs Kunden: zwei Krane ausgeliefert, drei nicht, ein `client_id` ohne Kran.
- **Einwilligung:** David hat ALLOW auf `air1` mit dem heutigen korrelierten Filter (David-Fall).
- **Soll:** Die Menge `air1.id` ist in jedem Kanal gleich der direkt in SQLite berechneten Menge:
  - WebSQL, `/v1/statement`, SQL-Endpunkt;
  - OData, GraphQL-Tabelle, GraphQL-Baum (mit Relation zu `client`);
  - MCP (Tabellenpfad und `query_graphql`);
  - Arrow-Export, Flight SQL `stream`, OLAP.
- **Hilfsklasse:** `ChannelMatrix`, je Kanal ein Delegat mit Rückgabe `IReadOnlySet<long>`; wird in Phase 5 wiederverwendet.

**Produktivcode:** keiner.

**Abnahme:** Grün auf dem heutigen Code. Abweichungen werden als `[Fact(Skip = "…")]` mit Grund vermerkt, nicht verschwiegen.

**Risiken:**
- SQLite verwirft Schemas; mehrere Schemas (`fms`, `tem`) nur in Unit-Tests abbilden oder `ATTACH` prüfen.
- Flight SQL über das HTTP-Endpoint testen.

**Nicht Teil der Phase:** Stream, Iceberg, Lakehouse (Phase 5).

## Phase 1: Domänenmodell und Muster

**Tests:**
- `tests/Autheris.Tests.Unit/VirtualFilters/ObjectPatternTests.cs`
- `tests/Autheris.Tests.Unit/VirtualFilters/VirtualFilterModelTests.cs`

| Eingabe | Erwartung |
| :--- | :--- |
| `lwetem_prod.*.*.client_id` gegen `(lwetem_prod, fms, air1, client_id)` | trifft |
| dasselbe Muster gegen `lwetem_prodX…` oder Spalte `client_idx` | trifft nicht (verankert) |
| `lwetem_prod.(fms\|tem).*.client_id` | `fms` und `tem` treffen; `dm` und `fmsx` nicht |
| `src.(a.b\|c).*` | 3 Segmente (Punkt in Klammern trennt nicht), `HasColumnSegment = false` |
| `lwetem_prod.LWEW2K\LWEMUM2.*.*` | Backslash ist ein Zeichen, trifft wörtlich |
| `fm?`, `client_*` | `?` genau ein Zeichen, `*` beliebig viele |
| `FMS` gegen `fms` | trifft (OrdinalIgnoreCase wie `TableIdentifier`) |
| `a.b`, `a.b.c.d.e`, `a..b.c`, `a.(fms.c.d`, `a.fms).c.d`, `a.().c.d` | `ArgumentException` mit Position |
| Rückverweis `(a)\1`, Lookaround `(?=a)` | abgelehnt (mit `NonBacktracking` nicht möglich) |
| ReDoS `((a+)+)` gegen 50 000 × `a` + `b` | linear (`RegexOptions.NonBacktracking`), unter 200 ms; zusätzlich Timeout 100 ms → „trifft nicht“ **und** Bindung auf deny |
| Segment > 256 oder Muster > 1024 Zeichen | abgelehnt |
| Vier-Felder-Form `{source, schema, object, column}` | gleiches Ergebnis wie die String-Form |
| FsCheck: Segment ohne Platzhalter | trifft genau dann, wenn gleich |

**Regeln für das Modell:**
- Name `^[a-z][a-z0-9_]{0,63}$`;
- `key_columns` nicht leer;
- `supersedes` nicht auf sich selbst;
- `on_unmatched` ist Pflichtfeld (Entwurf 3.9: kein stiller Standard).

**Produktivcode:**
- `src/Autheris.Domain/Model/VirtualFilterModels.cs` mit:
  - `VirtualFilter`, `FilterBinding`;
  - `StructuredFilterDefinition` (From, Joins, Where, KeyColumns, ValidFrom, ValidTo);
  - `OnUnmatched { Deny, Skip }`, `[Flags] FilterObjectKinds`;
  - `ManagedBy` (Pfad, Commit, Hash).
- `src/Autheris.Domain/Governance/ObjectPattern.cs` mit `Parse`, `MatchesObject(TableIdentifier)` und `MatchesColumn(string)`. Kein Bezug auf TrinoSqlEngine.

**Abnahme:** Alle Fälle grün, `ArchitectureTests` grün.

**Risiko:** `NonBacktracking` mit `CultureInvariant` statt kulturabhängigem `IgnoreCase`.

## Phase 2: Speicherung, API, Audit, Sperre bei `managed_by`

**Tests:**
- `tests/Autheris.Tests.Unit/VirtualFilters/SqliteVirtualFilterRepositoryTests.cs` (echte SQLite-Datei):
  - Anlegen, Lesen, Ändern, Löschen;
  - Name eindeutig je Mandant;
  - Bindung auf unbekannten Filter wird abgelehnt;
  - ein Filter mit Bindungen lässt sich nicht löschen;
  - jede Änderung erhöht die Generation;
  - Audit-Eintrag je Änderung, Hash-Kette bleibt gültig;
  - Ändern einer Zeile mit `managed_by` ohne Abgleichs-Kennzeichen → `ManagedResourceLockedException`;
  - `definition_hash` ist stabil.
- `tests/Autheris.Tests.Integration/PostgreSqlVirtualFilterContractTests.cs` (Testcontainers): dieselben Fälle.
- `tests/Autheris.Tests.Integration/VirtualFilterEndpointsTests.cs`:
  - Consumer und DataOwner → 403; neue Rolle → 201; fremder Mandant → 403;
  - `PUT` auf eine Zeile mit `managed_by` → 409;
  - ungültiges Muster, Zyklus in `supersedes`, unbekannter Filter, unbekannte Tabelle → 400 mit Grund;
  - `POST …/sync/plan` liefert Anlegen, Ändern, Löschen und Drift;
  - `POST …/sync/apply` entfernt Fehlendes, nur mit Abgleichsrolle.

**Produktivcode:**
- `IVirtualFilterRepository` als Teil von `IGovernanceRepository`.
- `SqliteGovernanceRepository.VirtualFilters.cs` und `PostgreSqlGovernanceRepository.VirtualFilters.cs`.
- Schema:
  - `VIRTUAL_FILTERS(id, tenant_id, name, source, definition_kind, definition_json, key_columns_json, valid_from_column, valid_to_column, supersedes_json, managed_by, managed_commit, definition_hash, updated_by, updated_at)`;
  - `FILTER_BINDINGS(id, tenant_id, filter_id, target_pattern, grantee_type, grantee_sid, role_name, object_kinds, time_column, column_map_json, on_unmatched, managed_by, managed_commit, definition_hash, …)`;
  - Generation in einer eigenen Tabelle `VIRTUAL_FILTER_GENERATION`, nicht in `POLICY_EPOCHS` (die gilt je Tabelle).
- `src/Autheris.Api/Endpoints/VirtualFilterEndpoints.cs`:
  - Gruppen `/api/v1/governance/virtual-filters`, `/filter-bindings`, `/sync/plan`, `/sync/apply`;
  - nach dem Muster von `RebacEndpoints`.
- Neue Rollen in `GatewayRole.cs` (Entscheidung 2).
- Audit-Ereignisse `VIRTUAL_FILTER_*`, `FILTER_BINDING_*` und `VIRTUAL_FILTER_SYNC_APPLIED`, jeweils mit Hash vorher und nachher.
- `VirtualFilterValidator` (Application): Zyklenprüfung für `supersedes` per Tiefensuche.

**Abnahme:** Gleiches Verhalten auf SQLite und PostgreSQL; kein Schreibweg ohne Audit.

**Risiken:**
- Jede spätere Spalte braucht `Ensure*Columns`.
- Die Generation muss über Instanzen wirken (Redis-Invalidierung über `IEventBus`, wie `EpochValidationService`).

## Phase 3: Auflösung (Pflichtprädikat, AND, `on_unmatched`, `supersedes`, Zwischenspeicher)

**Tests:**
- `tests/Autheris.Tests.Unit/VirtualFilters/MandatoryRowFilterResolverTests.cs`: rein, Abbild im Speicher, Prädikat als Platzhalter-SQL.
- `tests/Autheris.Tests.Unit/VirtualFilters/TableAccessPolicyMandatoryFilterTests.cs`: echter `ConsentResolutionService`.

| Fall | Erwartung |
| :--- | :--- |
| Einwilligung `region='EU'` und Bindung | `CombinedRowFilterSql == "(region = 'EU') AND (P_A)"`, `AppliedVirtualFilters == [A]` |
| **zusätzliche ALLOW-Einwilligung ohne Filter** | P_A bleibt (Umgehung unmöglich) |
| Bindung ohne Einwilligung | `Denied` (Bindung gewährt nichts) |
| DENY auf Tabellenebene und Bindung | `Denied` |
| Bindung für andere SID, Gruppen-SID, Rolle | greift nur beim passenden Subjekt (Logik aus `ConsentResolutionService.cs:235-264` in `GranteeMatcher` auslagern) |
| Objekt im Muster ohne Spalte, `deny` | `Denied`, Grund nennt Filter und fehlende Spalten |
| dasselbe mit `skip` | kein Prädikat |
| Objekt außerhalb des Musters | unverändert |
| Objekt aus anderer Quelle als `filter.source` | gilt als nicht passend (`on_unmatched`) |
| A (`client_id`) und B (`client_id`, `ts`) | beide Spalten: A AND B; nur `client_id`: nur A |
| Reihenfolge der Bindungen vertauscht (FsCheck) | gleiches SQL (sortiert nach Filtername) |
| B `supersedes: [A]` | beide greifen: nur B; greift B nicht: A |
| Casbin-Filter vorhanden | `(Consent) AND (P_A) AND (Casbin)` |
| Consent-Cache | speichert die Entscheidung **ohne** P_A; Generation +1 → neue Bindung greift ohne Bump der Consent-Epoche |
| Plan-Cache (`GovernedSqlPlanCacheTests` erweitern) | verschiedene Bindungen → verschiedener `policyHash` |
| Stream | Bindung greift → Ereignis verworfen (fail closed) |
| Iceberg | Bindung greift → `SecurityException` |
| Länge | drei Bindungen plus Consent-Filter bestehen `ValidatePredicateSql`, sonst `Denied` mit Grund |

**Produktivcode:**
- `TableAccessDecision`: optional `MandatoryRowPredicateSql` und `AppliedVirtualFilters`, dazu `WithMandatoryPredicates(...)`.
- `src/Autheris.Application/VirtualFilters/`:
  - `IVirtualFilterSnapshotProvider`, lädt bei neuer Generation neu;
  - `MandatoryRowFilterResolver`;
  - `GranteeMatcher`.
- `TableAccessPolicy` bekommt den Resolver als **Pflicht**-Parameter; an allen fünf Erzeugungsstellen angepasst.
- `TableAccessQuery.ObjectKind`: Standard `Relation`, für Prozeduren `ProcedureResult`.
- Anbindung in `StreamRlsPolicyEnforcer` und `IcebergRestCatalogFederationService`.
- DI-Registrierung in `GatewayServiceCollectionExtensions`.

**Abnahme:** Alle Fälle grün; `ConsentResolutionTests` und `ConsentPropertyBasedTests` unverändert grün.

**Risiken:**
- Jede neue Erzeugung von `TableAccessPolicy` ohne Resolver wäre eine Umgehung. Abhilfe:
  - der Parameter ist nicht optional;
  - ein bewusster Verzicht geht nur über `NullMandatoryRowFilterResolver`;
  - ein Architekturtest prüft die Erzeugungsstellen.
- `table` und `view` sind im Katalog nicht unterscheidbar (Entscheidung 5).

## Phase 4: SQL für die strukturierte Definition (Stufe 1)

**Tests:**
- `tests/Autheris.Tests.Unit/VirtualFilters/StructuredFilterSqlTests.cs`: erwartetes SQL je Dialekt.
- `tests/Autheris.Tests.Unit/VirtualFilters/VirtualFilterSqliteExecutionTests.cs`: echtes SQLite über `SqlDataSourceExecutor` und WebSQL.

| Fall | Erwartung |
| :--- | :--- |
| David, SQL Server, `InCorrelated` | `[autheris_target].[client_id] IN (SELECT [client].[client_id] FROM [conf].[client] AS [client] INNER JOIN [md].[crane] AS [crane] ON [crane].[serial_number] = [client].[crane_serial_number] WHERE [client].[client_id] = [autheris_target].[client_id] AND [crane].[is_delivered] IS NULL)` |
| David, SQL Server, `In` | ohne Korrelationsbedingung |
| David, PostgreSQL oder SQLite | `EXISTS (SELECT 1 …)` |
| Schlüssel `(client_id, ts)` | `EXISTS` mit zwei Korrelationen auf **allen** Dialekten, auch wenn `In` verlangt ist |
| nur `valid_to` | `(f.valid_to IS NULL OR autheris_target.ts < f.valid_to)` |
| nur `valid_from`; beide | `>=` bzw. beides |
| `map: {client_id: cid}` | Korrelation auf `autheris_target.cid` |
| Spaltenname `client_id; drop` | `InvalidOperationException` |
| SQLite mit sechs Kunden | nur Zeilen nicht ausgelieferter Krane; `client_id NULL` nie sichtbar (beide Strategien); `ts` vor und nach `date_of_delivery` |

**Produktivcode:**
- `src/Autheris.Application/VirtualFilters/StructuredFilterSqlBuilder.cs`: Abbildung auf `ConsentRowFilter` (SubqueryCorrelated).
- `AdvancedRlsFilterGenerator`:
  - `AdditionalCorrelations` (Spalte zu Spalte);
  - `valid_to` ohne `valid_from`;
  - Strategie je Filter statt nur global.
- `CorrelatedRowFilterAliasTests` und `AdvancedRlsSubqueryTests` bleiben unverändert grün.

**Abnahme:** SQL-Erwartungen für SqlServer, PostgreSql, Sqlite und Oracle grün; echte SQLite-Ausführung grün.

**Risiko:** Schemanamen mit Sonderzeichen in der Filterquelle bleiben abgelehnt; prüfen, ob Quelltabellen im PoC betroffen sind.

## Phase 5: Abdeckung der Kanäle

**Tests:** `RowFilterChannelParityTests` aus Phase 0, jetzt mit Bindung statt Filter in der Einwilligung, plus zusätzliche Einwilligung ohne Filter (Umgehungsversuch).
- **Soll:** Dieselbe Menge in WebSQL, `/v1/statement`, OData, GraphQL-Tabelle, GraphQL-Baum, MCP (beide Wege), Arrow, Flight SQL, OLAP (Join zweier gebundener Tabellen) und SQL-Endpunkt.
- **Fail closed:**
  - Lakehouse/Delta und HTTP-Connector → **403** mit Grund statt stillem leerem Ergebnis;
  - Iceberg REST → 403;
  - Stream → Ereignis verworfen;
  - `md.crane` mit `deny` → 403 in allen Kanälen.

**Produktivcode:** In `GovernedConnectorReader.Apply`, `LakehouseDataSourceExecutor` und `DeltaLakeDataSourceExecutor` gilt: Ist `MandatoryRowPredicateSql` gesetzt und gibt es keinen Pushdown, wird `GatewayForbiddenException("Virtual filter cannot be enforced on this source")` geworfen.

**Abnahme:** Alle Kanäle gleich; jeder Kanal, der nicht in der Datenbank filtern kann, lehnt ausdrücklich ab.

## Phase 6: Auswertung `effective-filters` und Audit

**Tests:** `tests/Autheris.Tests.Integration/EffectiveFiltersEndpointTests.cs`
- `GET /api/v1/governance/effective-filters?user=S-1-5-21-LWE-DAVID&table=lwetem_prod.fms.air1` liefert:
  - `bindings[] {filter, pattern, matched, missingColumns, supersededBy}`;
  - `decision` (`allow`/`deny`/`unmatched-deny`);
  - `sql` im Zieldialekt und `generation`.
- Ohne `table`: Übersicht je Katalogobjekt, mit Seiten.
- Rechte: GovernanceAdmin, SecurityAuditor und Filterrolle → 200; Consumer → 403.
- Audit: `TABLE_QUERY`, `WEBSQL_QUERY` (je Tabelle), GraphQL-Baum und `PROCEDURE_EXECUTE` nennen `virtual_filters: [...]`.

**Produktivcode:** `EffectiveFilterService` (Resolver im Erklärmodus), Endpunkt, Audit-Felder.

**Abnahme:** Das SQL der Auswertung ist Zeichen für Zeichen gleich dem in WebSQL verwendeten.

## Phase 7: SQL-Definition (Stufe 2) und Datumsfunktionen

### 7a: Prüfung beim Speichern

**Tests:** `tests/Autheris.Tests.Unit/VirtualFilters/SqlFilterDefinitionValidatorTests.cs`
- **Angenommen:**
  - Beispiel 3.7 (korreliertes Prädikat mit `target`);
  - `select` mit genau einer Ausgabespalte plus `key_columns`.
- **Abgelehnt:**
  - DML; mehrere Anweisungen;
  - `target` als Alias oder CTE-Name;
  - Tabelle einer anderen Quelle oder unbekannte Tabelle;
  - Funktion außerhalb der Trino-Liste für Filter oder auf der Sperrliste;
  - Parameter `?` oder `@p`;
  - Funktion, die der Dialekt der Quelle nicht kann; die Meldung nennt Dialekt und Funktion (Entwurf 3.8 Nr. 5).
- **Ergebnis:**
  - `RequiredTargetColumns == {client_id, ts}`;
  - das erzeugte SQL besteht `ValidatePredicateSql`;
  - `target` wird zu `autheris_target`.

### 7b: Übersetzung der Datumsfunktionen

**Tests:** `tests/TrinoSqlEngine.Tests/DateFunctionTranslationTests.cs`

| Eingabe (Trino) | SQL Server | PostgreSQL | SQLite |
| :--- | :--- | :--- | :--- |
| `date_add('day', -1, current_timestamp)` | `DATEADD(day, -1, SYSDATETIMEOFFSET())` | `(CURRENT_TIMESTAMP + (-1) * INTERVAL '1 day')` | `datetime(CURRENT_TIMESTAMP, '-1 day')` |
| `date_trunc('day', x)` | `CAST(CAST(x AS date) AS datetimeoffset)` (kein `DATETRUNC` vor SQL Server 2022) | `DATE_TRUNC('day', x)` | `datetime(x, 'start of day')` |
| `now()` | wie `current_timestamp` | | |
| `x - interval '1' day` | wird vorher zu `date_add` umgeschrieben (heute Fehler auf SQL Server und SQLite) | | |
| `date_add('fortnight', 1, x)`, Einheit als Spalte, `n` kein Ganzzahl-Literal | `AstBuildException` | | |

- Oracle: `NUMTODSINTERVAL` bzw. `ADD_MONTHS`. DuckDB und Snowflake bekommen je einen Test.
- **Zeitzone SQLite:** `CURRENT_TIMESTAMP` ist UTC-Text mit Leerzeichen, ISO-Werte mit `T` und Offset vergleichen sich als Text falsch. Beide Seiten werden mit `datetime()` normalisiert; Test mit Zeilen 1 Minute vor und nach der Grenze.
- **Zeitzone SQL Server:** In `MsSqlIntegrationTests` Zeilen vom Typ `datetimeoffset` mit `+00:00` und `+02:00` an der Grenze.

### 7c: Zwischenspeicher

**Tests:** `TimeDependentFilterCacheTests.cs`
- Zweimal kompiliert: gleicher `policyHash` und gleiches `securedSql`, mit `SYSDATETIMEOFFSET()` und ohne Datumsliteral.
- Weder Consent-Cache noch Resolver-Gedächtnis enthalten einen Zeitwert.

**Produktivcode (Phase 7):**
- AST-Knoten `DateFunctionExpression(Kind, Unit, Args)`; `SqlAstBuilder` erzeugt ihn nur mit der Option `TranslateTrinoDateFunctions` (nur für Filter; WebSQL bleibt unverändert).
- `FormatDateFunction` in `SqlDialectGeneratorBase` mit Überschreibungen je Dialekt.
- `SqlFunctionAllowlists.TrinoFilter`.
- `SqlFilterDefinitionValidator` (Application): AST, Umschreiben von `target`, Einbettung als `EXISTS (SELECT 1 FROM … WHERE …)`.

**Risiko:** `date_diff` hat auf SQL Server eine andere Bedeutung: `DATEDIFF` zählt Grenzen, Trino volle Einheiten (Entscheidung 8).

## Phase 8: Prozedur-Ergebnisse

**Tests:** `tests/Autheris.Tests.Unit/Procedures/ProcedureVirtualFilterTests.cs` (`IProcedureInvoker` als Substitute, echter `SqlProcedureRowScopeResolver` auf SQLite).

| Fall | Erwartung |
| :--- | :--- |
| `ResultTable=air1`, `RowScopeKey=[id]`, Bindung greift | Zeilen ausgelieferter Krane entfallen, `rowsRemovedByScope` im Audit |
| ohne `RowScopeKey` | `PROCEDURE_DENIED` |
| gebundene Tabelle nur gelesen (nicht `ResultTable`) | abgelehnt |
| Schlüssel nicht eindeutig | abgelehnt |
| `deny` auf der `ResultTable` | abgelehnt |
| Bindung mit `object_kinds: [relation]` | greift für die Prozedur nicht |

**Produktivcode:**
- `ObjectKind = ProcedureResult` in `GovernedProcedureExecutionService`, Audit-Grund `virtual-filter`.
- **Kein** Zwischenspeicher für Wertemengen: Die vorhandene Schlüsselprüfung ist aktueller und schon fail closed.

**Abnahme:** Alle Fälle grün; `PostgreSqlRowScopeContractTests` unverändert grün.

## Phase 9: Leistung messen

1. **Benchmark für den Resolver** (`benchmarks/Autheris.Benchmarks`): 200 Bindungen × 150 Tabellen. Ziel: unter 50 µs je Entscheidung bei Treffer im Gedächtnis.
2. **SQL Server im PoC** (`fms.air1`, 46 700 Krane):
   - `Exists`, `InCorrelated` und `In` im Vergleich;
   - jeweils mit und ohne Index `(client_id, ts)`;
   - mit `SET STATISTICS IO, TIME` und tatsächlichem Plan;
   - dazu das Zeitfenster aus Entwurf 3.7.
3. **Ergebnisse** in `docs/plans/2026-10-xx-virtuelle-filter-messungen.md`, mit Entscheidung über die Standardstrategie je Filter.

---

## Offene Entscheidungen

| Nr. | Frage | Empfehlung |
| :--- | :--- | :--- |
| 1 | Gelten Bindungen auch bei `danger_bypass_consent_checks`? Heute gibt `TableAccessPolicy` dann ungefiltert zurück. | Ja. Bindungen schränken nur ein; ein Testsystem soll dieselben Filter zeigen. |
| 2 | Eine Rolle oder zwei (`FilterAdmin` für Pflege, `FilterSync` für `apply`)? | Zwei. Talos bekommt nur `FilterSync` (Dienstkonto); GovernanceAdmin erhält keine Schreibrechte automatisch. |
| 3 | Gehören Filter und Bindungen zu einem Mandanten (`tenant_id` wie Einwilligungen)? | Ja; global nur für ClusterAdmin. |
| 4 | Erstes Segment des Musters: `TableIdentifier.Domain` oder `Table.SourceName`? | `Domain`. Beim Speichern prüfen, dass Filtertabellen und Ziele dieselbe `SourceName` haben. |
| 5 | `object_kinds` `table` und `view`: Der Katalog unterscheidet sie nicht. | Zunächst nur `relation` und `procedure_result`; die Objektart bei Bedarf im Katalog nachrüsten. |
| 6 | Kanäle ohne Filter in der Datenbank (Lakehouse, HTTP): 403 oder leeres Ergebnis wie heute? | 403 mit Grund. Ein leeres Ergebnis sieht aus wie „keine Daten“. |
| 7 | Envoy ext_authz setzt keine Zeilenfilter durch. Berechtigte mit Bindung dort sperren? | Ja, ablehnen, sobald eine Bindung für das Subjekt greift. Eigener kleiner Commit nach Phase 5. |
| 8 | `date_diff` in Stufe 2 zulassen? | Zunächst nicht (abweichende Bedeutung auf SQL Server), bis es konkreten Bedarf gibt. |
| 9 | Grenze 2000 Zeichen in `ValidatePredicateSql` anheben oder Bindungen je Objekt begrenzen? | Für vom Gateway erzeugte Pflichtprädikate anheben (z. B. 8000), mit Test. |
| 10 | Rechte des Autors bei Stufe 2 prüfen (Entwurf 3.5)? Beim Abgleich ist der Autor ein Dienstkonto. | Maßgeblich sind die Rechte der Abgleichsrolle; im Audit stehen Commit und Pfad aus `managed_by`. |

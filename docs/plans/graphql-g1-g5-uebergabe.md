# Übergabe: GraphQL G1–G5 fertigstellen

Stand: 07.10.2026, Branch `feat/ast-target-dialect-generator`, letzter Commit `fe552bf`.
Hintergrund und Ziel: `docs/plans/rls-subquery-in-strategy.md`, Abschnitt „GraphQL im Vergleich zu Hasura“.

**Wichtig vorab:** Alle Commits ab `b8931ec` sind **ohne Compiler** entstanden (kein .NET-SDK auf dem Rechner des
Assistenten). Erster Schritt ist deshalb: bauen, testen, Fehler beheben. Die Build-Einstellungen sind streng
(`TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, `Nullable enable`, siehe `Directory.Build.props`).
CI (`.github/workflows/ci.yml`) läuft nur bei Push auf `main` oder bei einem PR nach `main`.

```
dotnet build
dotnet test
```

## 1. Was fertig ist (nur prüfen und reparieren)

| Commit | Inhalt | Tests |
|---|---|---|
| `b8931ec` | D-1 Dialekt-Abgleich, OData-Härtung O1–O13 (u. a. `GatewayExceptionHandler`, `DataAccessErrorClassifier`, `TableReadConcurrencyGate`, `$select`-Validierung in `GatewayExecutionService`) | `DialectAlignmentTests`, `GatewayExecutionRequestValidationTests`, `SqlDataSourceExecutorReaderTests`, `DataAccessErrorClassifierTests`, `GatewayExceptionHandlerTests`, angepasste `ODataTests`, `ODataEndpointsTests`, `DynamicOpenApiGeneratorTests` |
| `cd3eb41` | `src/Autheris.Application/Sql/Tree/TreeQueryModel.cs`, `TreeSqlCompiler.cs`: ganzer Auswahlbaum → **ein** SQL, das das fertige JSON liefert (SQL Server `FOR JSON PATH`, PostgreSQL `jsonb_agg`, SQLite `json_group_array`) | `tests/Autheris.Tests.Unit/GraphQL/TreeSqlCompilerTests.cs` (führt das SQL gegen SQLite aus) |
| `fe552bf` | `ITableAccessResolver` (herausgelöste Zugriffsauflösung aus `GatewayExecutionService.ResolveTableAccessAsync`, Verhalten von `ExecuteTableQueryAsync` unverändert); `GovernedTreeQueryService` (Zugriff je Tabelle und Anfrage einmal, ein Audit-Eintrag je Tabelle, ein Datenbank-Roundtrip, HMAC im Gateway, Größenlimit, Drosselung) | `tests/Autheris.Tests.Unit/GraphQL/GovernedTreeQueryServiceTests.cs` |

Bekannte Risiken beim ersten Build:
- **SQL Server und PostgreSQL** sind nur als Text geprüft (`SqlServer_UsesForJsonPath…`, `PostgreSql_UsesJsonbAggregation`); ausgeführt wurde nur SQLite. Gegen echte Instanzen prüfen (Testcontainers `mcr.microsoft.com/mssql/server`, `postgres`), besonders: korrelierte abgeleitete Tabellen, `ORDER BY` in `FOR JSON`-Unterabfragen, `JSON_QUERY(ISNULL(…, N'[]'))`, `jsonb_agg(… ORDER BY …)`.
- `ResolveTableAccessAsync` wurde per Skript aus `ExecuteTableQueryAsync` ausgeschnitten (`GatewayExecutionService.cs`); Einrückung und Nullability prüfen.
- Bestehende Tests, die eine unbekannte Spalte in `requestedFields` erwarteten (früher still ignoriert), schlagen jetzt mit `GatewayInvalidQueryException` fehl: Test anpassen, nicht die Prüfung lockern. `DocumentationSourcePriorityTests` nutzt `SourceType = "sql"`; liest ein Test `Table.Dialect`, wirft das jetzt (gewollt, D-1).
- Doppelte Code-Stellen für den tenant-gebundenen HMAC-Schlüssel (`CreateTenantScopedHmacRule` in `SqlDataSourceExecutor`, `GatewayExecutionService`, `GovernedTreeQueryService.ScopeHmacRule`): wenn Zeit ist, in eine gemeinsame Hilfsmethode ziehen.

## 2. Was noch fehlt

### G1 Typisiertes Schema aus dem Katalog

Neuer Ordner `src/Autheris.GraphQL/Catalog/`.

**a) `CatalogSchemaModel` (rein, ohne HotChocolate, gut testbar)**
- Eingabe: alle Tabellen (`ITableMetadataRepository.GetAllTablesAsync`) und Relationen (`ITableRelationRepository.GetRelationsForTableAsync(parent)` je Tabelle; beide als Singleton registriert).
- Nur aktive Tabellen mit `DataSourceType.Sql` und gültigem Dialekt (`DatabaseDialectExtensions.TryParseDialect`).
- Namen: `{domain}_{schema}_{table}` auf GraphQL-Namen bereinigt (`[_A-Za-z][_0-9A-Za-z]*`, kein führendes `__`), eindeutig machen (Suffix `_2`). Gleicher Name für Objekttyp und Wurzelfeld; Filtertyp `{Typ}_filter`, Sortiertyp `{Typ}_order_by`.
- Spalten: nur gültige GraphQL-Namen; Typzuordnung:
  - `bit`, `bool…` → Boolean
  - `bigint`, `int8`, `long` → Long
  - `int`, `integer`, `smallint`, `tinyint` → Int (vorher `interval`, `point` u. ä. als String abfangen)
  - `decimal`, `numeric`, `money` → Decimal
  - `float`, `double`, `real` → Float
  - Datums-/Zeittypen → **String** in der Ausgabe (ISO-Text unverändert aus der DB, G3), im Filter als Datum geparst (siehe G4)
  - Geodaten, Binär, sonst → String
- Relationen (`TableRelation`): Feld `RelationName` am Elterntyp (`IsList = Cardinality == OneToMany`), Gegenrichtung am Kindtyp als `{elterntabelle}_by_{RelationName}` (`IsList` umgekehrt). Joinschlüssel `JoinKeysParent`/`JoinKeysChild`; Relationen ohne Kind-Schlüssel oder zu Tabellen außerhalb des Schemas weglassen; Namenskollision mit Spalten → Suffix `_rel`.
- Spalten mit `Deny` stehen trotzdem im Schema (Einwilligungen sind je Benutzer, nicht je Rolle; ein Schema je Rolle ist nicht möglich). Der Zugriff wird zur Laufzeit geprüft, unbekannt und gesperrt geben dieselbe Meldung. Introspection ist außerhalb von Development aus (`GraphQL.EnableIntrospection`).

**b) `CatalogGraphQlTypeModule : ITypeModule`** (HotChocolate 16, `HotChocolate.Execution.Configuration.ITypeModule`):
- `ValueTask<IReadOnlyCollection<ITypeSystemMember>> CreateTypesAsync(IDescriptorContext, CancellationToken)`, Event `TypesChanged` (für späteres Neuladen bei Katalogänderungen; vorerst nur beim Start).
- Erzeugt mit Fluent-Descriptoren (in v16 per Reflection geprüft):
  - `new ObjectType(d => …)` je Tabelle; Spaltenfelder `d.Field(name).Type(new NamedTypeNode("Int")).Resolve(…)`, Relationsfelder mit Argumenten `where`, `orderBy`, `first` (Liste) bzw. ohne `first` (Einzelobjekt);
  - `new InputObjectType(d => …)` für `{Typ}_filter` (Felder je Spalte vom Operator-Typ, dazu `and: [{Typ}_filter!]`, `or: [...]`, `not: {Typ}_filter`) und `{Typ}_order_by` (Felder je Spalte vom Enum `AutherisSortDirection`);
  - gemeinsame Operator-Eingaben `AutherisStringFilter`, `AutherisIntFilter`, `AutherisLongFilter`, `AutherisDecimalFilter`, `AutherisFloatFilter`, `AutherisBooleanFilter`, `AutherisDateTimeFilter` mit `eq, neq, gt, gte, lt, lte, in, nin, isNull` (String zusätzlich `contains, startsWith, endsWith`); Präfix wegen möglicher Kollision mit `HotChocolate.Data`;
  - `new EnumType(d => { d.Name("AutherisSortDirection"); d.Value("ASC"); d.Value("DESC"); })`;
  - `new ObjectTypeExtension(d => { d.Name("Query"); … })` mit einem Wurzelfeld je Tabelle: `(where, orderBy: [{Typ}_order_by!], first: Int = 100, offset: Int = 0): [{Typ}!]!`.
- Registrierung in `GatewayServiceCollectionExtensions.AddGatewayGraphQL` (bei `gqlBuilder`):
  `.AddApplicationService<ITableMetadataRepository>().AddApplicationService<ITableRelationRepository>().AddTypeModule(sp => new CatalogGraphQlTypeModule(sp.GetRequiredService<ITableMetadataRepository>(), sp.GetRequiredService<ITableRelationRepository>()))`
  (`AddTypeModule(builder, Func<IServiceProvider, T>)` existiert in v16; ob der übergebene Provider die App-Dienste kennt, beim Bauen prüfen; `AddApplicationService` ist der vorhandene Weg dafür, siehe `IHostEnvironment`).
- Das bestehende Wurzelfeld `table(domain, name, …)` mit `JsonRows` bleibt für Abwärtskompatibilität.

### G2/G4 Auswahlbaum aus der GraphQL-Operation bauen

`GraphQlTreeBuilder` (HotChocolate-abhängig), aufgerufen im Resolver des Wurzelfelds:
- HotChocolate-16-API (per Reflection bestätigt):
  - `IResolverContext.Selection` (`Selection`), `IResolverContext.Operation` (`Operation`), `IResolverContext.IncludeFlags`, `IResolverContext.Variables` (`IVariableValueCollection`, Werte als `VariableValue(Name, Type, IValueNode Value)`), `IResolverContext.ResponseName`.
  - `Operation.GetSelectionSet(Selection)` und `GetSelectionSet(Selection, IObjectTypeDefinition)`; `SelectionSet.Selections` ist `ReadOnlySpan<Selection>` (nur synchron iterieren).
  - `Selection.IsIncluded(includeFlags)` (wegen `@skip`/`@include`), `Selection.Field.Name`, `Selection.ResponseName`, `Selection.Arguments` (`ArgumentMap`, `TryGetValue(name, out ArgumentValue)`), `ArgumentValue.ValueLiteral` (`IValueNode`).
- Ablauf: für jede Auswahl im Selection-Set des Objekttyps: `__typename` und andere `__`-Felder überspringen; Spaltenfeld → Spaltenname in `TreeQueryNode.Columns`; Relationsfeld → `TreeRelationNode(ResponseKey = Selection.ResponseName, …)` mit rekursivem Kindknoten. Aliase desselben Relationsfelds mit verschiedenen Argumenten werden so zu getrennten Schlüsseln.
- Argumente immer aus `ValueLiteral` lesen; `VariableNode` über `ctx.Variables.TryGetValue<IValueNode>(name, out var value)` auflösen (zu verifizieren; Alternativ-Weg: die Werte der Collection enumerieren).
- `where` → `TreeFilter` (`TreeAndFilter`/`TreeOrFilter`/`TreeNotFilter`/`TreeComparison`), Werte in CLR-Typen der Spalte wandeln (Int/Long → `long`, Decimal → `decimal`, Float → `double`, Boolean → `bool`, DateTime → `DateTimeOffset` per `InvariantCulture`, sonst `string`); `in`/`nin` als Liste; `isNull` als `bool`.
- `orderBy` → Liste `TreeOrder` in Argumentreihenfolge; je Eingabeobjekt genau ein Feld, sonst Fehler.
- `first` an der Wurzel ≤ `GraphQL.MaxResponseRows` (prüft auch der Dienst), in Relationen Standard 100, höchstens `TreeSqlCompiler.MaxLimit`.
- **Keyset-Paging** braucht keinen eigenen Cursor: `where: { id: { gt: <letzter Wert> } }, orderBy: [{ id: ASC }]` ist Keyset-Paging; in der Schema-Beschreibung des Wurzelfelds so dokumentieren.

### G3 JSON durchreichen statt doppelt serialisieren

- Wurzelresolver: `IGovernedTreeQueryService.ExecuteAsync(principal, node, headers, ct)` (scoped; Principal und Header aus `IHttpContextAccessor` wie in `QueryTypes.GetTableAsync`), Ergebnis `JsonDocument` → `RootElement.Clone()`, Dokument freigeben, Liste der Zeilen-`JsonElement` zurückgeben.
- Spaltenresolver lesen `ctx.Parent<JsonElement>()` und liefern den CLR-Wert passend zum Feldtyp (`GetInt32`, `GetInt64`, `GetDecimal`, `GetDouble`, Boolean auch aus `0/1` (SQLite), String: bei Nicht-Strings `GetRawText()`), `null` bei fehlender Eigenschaft oder `JsonValueKind.Null`.
- Relationsresolver lesen die Eigenschaft `ctx.ResponseName` (der Compiler schlüsselt Relationen nach Antwortnamen, Spalten nach Spaltennamen).
- Fehlerabbildung im Resolver: `GatewayInvalidQueryException` → `GraphQLException(ErrorBuilder.New().SetMessage(msg).SetCode("INVALID_QUERY").Build())`; `GatewayForbiddenException` → generisch „Access denied.“ mit Code `ACCESS_DENIED`; `GatewayThrottledException` → `TOO_MANY_REQUESTS`; `GatewaySecurityException` mit `RESPONSE_TOO_LARGE` → `RESPONSE_TOO_LARGE`; Datenbankfehler über `DataAccessErrorClassifier` (`TIMEOUT`, `UNAVAILABLE`). Mit `ErrorSanitizingFilter` (`src/Autheris.Api/Middleware/ErrorSanitizingFilter.cs`) abgleichen, damit die Codes nicht geschluckt werden.
- Mehrere Wurzelfelder in einer Operation laufen parallel; der Dienst ist dafür abgesichert (Sperre um Memo und Audit).

### G5 Restpunkte

- DI: `services.AddScoped<ITableAccessResolver>(sp => sp.GetRequiredService<GatewayExecutionService>())` und `services.AddScoped<IGovernedTreeQueryService, GovernedTreeQueryService>()` in `GatewayServiceCollectionExtensions` (bei der Registrierung von `GatewayExecutionService`, ca. Zeile 430). `ITableReadConcurrencyGate` ist bereits Singleton.
- **Audit gepuffert/asynchron** ist bewusst **nicht** umgesetzt: Ein Puffer verliert bei Absturz Audit-Einträge. Umgesetzt ist „ein Eintrag je Tabelle und Anfrage“. Wenn gepuffert gewünscht: begrenzter `Channel` mit Hintergrund-Flush, Flush beim Herunterfahren (`IHostApplicationLifetime`), Überlauf → synchron schreiben (nie verwerfen); vorher mit dem Fachbereich klären.
- Metadaten-Cache prozessweit: nur mit Invalidierung über die vorhandene Epoch-/Katalog-Ereignislogik; sonst beim Memo je Anfrage bleiben.
- `SingleQueryAstCompiler` (`src/Autheris.Application/Sql/SingleQueryAstCompiler.cs`) ist durch `TreeSqlCompiler` überholt und wird nirgends aufgerufen: entfernen samt Registrierung und Option `SingleQueryPushdown`, sofern keine Tests mehr darauf zeigen. `SqlFilterProvider` ebenso prüfen (ungenutzt).

## 3. Tests, die noch zu schreiben sind (zuerst schreiben, dann bauen)

1. `CatalogSchemaModelTests`: Namen und Bereinigung, Eindeutigkeit, Typzuordnung je Datentyp, ausgelassene Tabellen (inaktiv, nicht SQL, unbekannter Dialekt), Relationen in beide Richtungen, Kollisionen.
2. `CatalogGraphQlSchemaTests` mit echtem HotChocolate-Executor (Muster: `tests/Autheris.Tests.Unit/Security/RebacGraphQLSecurityTests.cs`, `services.AddGraphQLServer()…BuildRequestExecutorAsync()`) und einem Test-Double für `IGovernedTreeQueryService`, das den `TreeQueryNode` festhält und vorbereitetes JSON liefert:
   - Spalten, Relationen, `first`/`offset`, `where` (alle Operatoren, `and`/`or`/`not`), `orderBy` werden korrekt in den Baum übersetzt;
   - Variablen (`query($w: …_filter)`), Aliase (zwei Relationen mit verschiedenen Argumenten), Fragmente, `@skip`/`@include`;
   - Ausgabe: typisierte Werte, `null`, leere Listen, Einzelobjekt `null`;
   - Fehlercodes je Ausnahme;
   - Schema enthält die Typen und das bestehende Feld `table` weiterhin.
3. Ende-zu-Ende mit SQLite: `WebApplicationFactory<Program>` (vorhanden in `tests/Autheris.Tests.Integration`) mit einer kleinen SQLite-Datenquelle und Katalog, GraphQL-Abfrage mit zwei Ebenen, Zeilenfilter auf der Kindtabelle; prüfen, dass genau ein Kommando an die Datenquelle geht.
4. Gleichwertigkeit: dieselbe Zeilenmenge und dieselben Masken wie OData/WebSQL für eine Tabelle mit `SubqueryCorrelated`-Filter (vgl. Plan, Abschnitt 0, Schritt 6).

## 4. Abnahme

- `dotnet build` ohne Warnungen, `dotnet test` grün.
- GraphQL `lwetem_prod_fms_air1(first: 1000) { ts client_id … }` als `david` unter 2 s, mit einer Relationsebene ebenfalls; ein Datenbank-Roundtrip je Wurzelfeld (Log mit `Gateway:Logging:LogGeneratedSql=true`).
- Zeilenmengen identisch zu WebSQL für dieselbe Abfrage.
- Danach `docs/plans/rls-subquery-in-strategy.md` (Abschnitt GraphQL) auf den erreichten Stand bringen.

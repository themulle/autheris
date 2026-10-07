# Implementierungsplan: GraphQL-Schema-Stabilität (G-2 / R-GQL-2) und Zeilenbudget-Konfiguration (D-8 / SQL2-7)

**Datum:** 2026-10-07  
**Ziel-Branch:** `feat/ast-target-dialect-generator`  
**Referenzdokument:** `docs/plans/status-und-umsetzungsplan-2026-10-07.md` (Punkte G-2, R-GQL-2, D-8)

---

## 1. Problemanalyse & Architektur-Kontext

### 1.1 G-2 & R-GQL-2: Namenskollisionen und instabile Schema-Typnamen
1. **Nicht-deterministische Sortierung:**
   In `CatalogSchemaModel.BuildAsync` werden aktive SQL-Tabellen per `.OrderBy(t => t.Identifier.Domain).ThenBy(t => t.Identifier.Schema).ThenBy(t => t.Identifier.TableName)` sortiert, ohne `StringComparer.Ordinal` anzugeben. Unter unterschiedlichen System-Cultures kann die Verarbeitungsreihenfolge variieren.
2. **Kollisionsauflösung durch instabile Suffixe (`_2`, `_3`):**
   Wenn eine Tabelle mit einem bestehenden Typnamen oder abgeleiteten Typnamen (`{Table}_filter`, `{Table}_order_by`) kollidiert (z. B. Tabelle `orders` und Tabelle `orders_filter`, oder Tables mit Bindestrichen/Unterstrichen wie `sales-orders` und `sales_orders`), versieht der bisherige Code die spätere Tabelle mit einem fortlaufenden Suffix (`_2`, `_3`).
   - Ändert sich die Reihenfolge (z. B. wenn eine Tabelle `a_orders_filter` vor `orders` verarbeitet wird), wird die reguläre Tabelle `orders` plötzlich zu `orders_2` umbenannt. Bestehende GraphQL-Clients brechen.
   - Suffixe erzeugen implizite, flüchtige Schema-Verträge.
3. **Architektur-Vorgabe (G-2 / R-GQL-2):**
   - Deterministische Sortierung per `StringComparer.Ordinal`.
   - Reservierte Schema-Typen (`Query`, `Mutation`, `Subscription`, `AutherisSortDirection`, Filtertypen) und bereits vergebene Namen strikt schützen.
   - Wenn eine Tabelle einen Typ- oder Root-Feldnamen beansprucht, der bereits vergeben ist oder mit reservierten Wörtern kollidiert:
     **Fehler loggen (`logger?.LogError(...)`) und die kollidierende Tabelle auslassen (`continue;`)!**
   - Die ursprüngliche, kanonische Tabelle behält ihren stabilen Namen (`${domain}_${schema}_${table}`).
   - Das GraphQL-Schema startet ohne `SchemaException` (Kollisionstoleranz).

### 1.2 D-8 & SQL2-7: Zeilenbudget und MaxOffset nicht konfigurierbar und umgehbar
1. **Hardcodierte Konstanten im Builder:**
   `GraphQlTreeBuilder.MaxAggregateBudget = 50_000` und `MaxAllowedOffset = 10_000` sind hardcodierte Konstanten im GraphQL-Tree-Builder.
2. **Umgehungsschutz im Query-Service:**
   Andere Aufrufer von `IGovernedTreeQueryService` (z. B. REST, RPC, interne Workflows), die direkt `ExecuteAsync` mit einem `TreeQueryNode` aufrufen, umgehen die Budget- und Offset-Prüfungen vollständig.
3. **Architektur-Vorgabe:**
   - Hinzufügen von `MaxAggregateRowBudget` (Default: 50.000) und `MaxAllowedOffset` (Default: 10.000) zu `GraphQLOptions` (mit `[Range]`).
   - Verlagerung und Durchsetzung der Budget- und Offset-Validierung direkt in `GovernedTreeQueryService.ExecuteAsync`.
   - `GraphQlTreeBuilder` nutzt dieselben konfigurierten Obergrenzen.

---

## 2. Lösungsdesign & Umsetzungsstrategie

### 2.1 G-2 & R-GQL-2: Stabile Namensvergabe in CatalogSchemaModel
1. **Deterministische Sortierung:**
   ```csharp
   var activeSqlTables = allTables
       .Where(t => t.Table.IsActive &&
                   t.DataSourceType == DataSourceType.Sql &&
                   DatabaseDialectExtensions.TryParseDialect(t.Table.SourceType, out _))
       .OrderBy(t => t.Identifier.Domain, StringComparer.Ordinal)
       .ThenBy(t => t.Identifier.Schema, StringComparer.Ordinal)
       .ThenBy(t => t.Identifier.TableName, StringComparer.Ordinal)
       .ToList();
   ```
2. **Kollisionserkennung & Auslassen:**
   ```csharp
   var baseName = SanitizeGraphQlName($"{meta.Identifier.Domain}_{meta.Identifier.Schema}_{meta.Identifier.TableName}");
   var typeName = baseName;
   var filterTypeName = $"{typeName}_filter";
   var orderTypeName = $"{typeName}_order_by";

   if (usedSchemaTypeNames.Contains(typeName) ||
       usedSchemaTypeNames.Contains(filterTypeName) ||
       usedSchemaTypeNames.Contains(orderTypeName))
   {
       logger?.LogError(
           "GraphQL catalog schema: table '{Table}' conflicts with existing schema type name. Table is omitted to preserve schema stability.",
           meta.Identifier.ToQualifiedName());
       continue;
   }

   usedSchemaTypeNames.Add(typeName);
   usedSchemaTypeNames.Add(filterTypeName);
   usedSchemaTypeNames.Add(orderTypeName);
   ```
3. **Logging:**
   `CatalogSchemaModel.BuildAsync` und `CatalogGraphQlTypeModule` erhalten optional einen `ILogger`.

### 2.2 D-8: Konfigurierbares Zeilenbudget & Offset in GatewayOptions & GovernedTreeQueryService
1. **Erweiterung von `GraphQLOptions`:**
   ```csharp
   [Range(100, 1000000)] public int MaxAggregateRowBudget { get; init; } = 50000;
   [Range(0, 100000)] public int MaxAllowedOffset { get; init; } = 10000;
   ```
2. **Validierung in `GovernedTreeQueryService.ExecuteAsync`:**
   ```csharp
   var maxOffset = _options.GraphQL?.MaxAllowedOffset > 0 ? _options.GraphQL.MaxAllowedOffset : 10000;
   if (root.Offset < 0)
       throw new GatewayInvalidQueryException("The offset cannot be negative.");
   if (root.Offset > maxOffset)
       throw new GatewayInvalidQueryException($"The offset cannot exceed {maxOffset}.");

   var maxBudget = _options.GraphQL?.MaxAggregateRowBudget > 0 ? _options.GraphQL.MaxAggregateRowBudget : 50000;
   ValidateTreeBudget(root, maxBudget);
   ```
3. **`GraphQlTreeBuilder`:**
   Nimmt konfigurierte Optionen entgegen bzw. nutzt Fallback-Konstanten konsistent.

---

## 3. Test- & Verifikationsplan (TDD)

1. **G-2 Kollisionstests:**
   - `CatalogSchemaModelTests`:
     - Test: Zwei Tabellen, die denselben Sanitize-Namen erzeugen (`sales_dbo_orders_item` vs `sales_dbo.orders.item`) -> Kanonische Tabelle bleibt, kollidierende Tabelle wird ausgelassen (`count == 1`).
     - Test: Tabelle `orders` und Tabelle `orders_filter` -> `orders` bleibt erhalten, `orders_filter` wird ausgelassen (kein Umbenennen von `orders` zu `orders_2`).
   - `CatalogGraphQlSchemaTests`:
     - Test: Schema-Executor startet fehlerfrei (`CreateExecutorAsync()`), wenn Kollisionen im Katalog vorliegen.
2. **D-8 Zeilenbudget- & Offset-Tests:**
   - `GovernedTreeQueryServiceTests`:
     - Test: Query mit `Offset > MaxAllowedOffset` wirft `GatewayInvalidQueryException`.
     - Test: Verschachtelte Query mit `estimatedRows > MaxAggregateRowBudget` wirft `GatewayInvalidQueryException`.
     - Test: Benutzerdefiniertes Budget in `GatewayOptions.GraphQL.MaxAggregateRowBudget` wird respektiert.

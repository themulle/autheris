# Implementierungsplan: GraphQL-Behandlung maskierter Spalten & HMAC-Typisierung (G-8 / R-GQL-1)

**Datum:** 2026-10-07  
**Ziel-Branch:** `feat/ast-target-dialect-generator`  
**Referenzdokumente:** `docs/plans/status-und-umsetzungsplan-2026-10-07.md` (Punkte G-8, R-GQL-1), `docs/plans/review-c4f2c32.md`

---

## 1. Problemanalyse & Architektur-Kontext

### 1.1 G-8 & R-GQL-1: Maskierte Spalten und HMAC-Pseudonyme in GraphQL
1. **HMAC auf Nicht-String-Spalten verliert Information (`null` statt Pseudonym):**
   - Wenn eine Zahlenspalte (z. B. `id INT`, `account_no BIGINT`, `salary DECIMAL`) oder boolesche Spalte per HMAC maskiert wird, berechnet `GovernedTreeQueryService` ein kryptografisches HMAC-Pseudonym als Hex-String (z. B. `"H(42)"` oder `"a7b2..."`).
   - Da `CatalogSchemaModel` die Spalte bisher stur nach dem Datenbank-Datentyp typisiert (`CatalogFieldType.Int`), scheitert `int.TryParse` in `CatalogGraphQlTypeModule.ResolveColumnValue` auf dem Hash-String.
   - Der Wert wurde still `null` gesetzt, wodurch der Client das pseudonymisierte Token nie erhielt.
2. **Katalogschnittstelle für HMAC-Spalten:**
   - Spalten mit statischer HMAC-Regel in `ColumnMaskingRules` müssen im GraphQL-Schema als `String` deklariert werden (`CatalogFieldType.String`). Dadurch können GraphQL-Clients das Pseudonym typkonform abfragen und filtern.
3. **Fehlercode `MASKED` für Laufzeit-Redacting auf Nicht-String-Spalten:**
   - Erhält der Resolver für eine als `Int`, `Long`, `Float`, `Decimal` oder `Boolean` typisierte Spalte einen nicht-konvertierbaren String (z. B. `'***'` bei Redacting oder ungültige Strings), darf dieser nicht still zu `null` oder gar zu `false` (plausibler Falschwert bei Boolean!) werden.
   - Der Resolver muss über `ctx.ReportError(...)` einen GraphQL-Feldfehler mit Error-Code `MASKED` melden und für das Feld `null` zurückgeben.
4. **Strikte Zahlenformate & Vermeidung von `NumberStyles.Any`:**
   - In `CatalogGraphQlTypeModule.cs` wurde `int.TryParse(..., NumberStyles.Any)` verwendet. `NumberStyles.Any` erlaubt unerwünschte Formatierungen (Währungen, Klammern etc.).
   - Es müssen strikte `NumberStyles` (`NumberStyles.Integer` für Int/Long, `NumberStyles.Float | NumberStyles.AllowThousands` für Float, `NumberStyles.Number` für Decimal) mit `CultureInfo.InvariantCulture` verwendet werden.
5. **Robustheit bei Boolean-Zahlenwerten (`1.0`):**
   - `prop.GetInt32()` wirft `InvalidOperationException`, wenn die JSON-Zahl eine Nachkommastelle hat (z. B. `1.0` in JSON aus manchen Dialekten/Treibern).
   - Saubere Fallback-Logik über `TryGetInt64` und `TryGetDouble` ohne Exception.

---

## 2. Lösungsdesign & Architektur-Invarianten

### 2.1 Schema-Typisierung in `CatalogSchemaModel.cs`
Beim Erstellen der Spaltenfelder (`CatalogColumnField`):
```csharp
var fieldType = MapDataType(col.DataType);
if (meta.ColumnMaskingRules != null &&
    meta.ColumnMaskingRules.TryGetValue(col.ColumnName, out var maskRule) &&
    (string.Equals(maskRule.RuleType, "HMAC", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(maskRule.RuleType, "HMAC_SHA256", StringComparison.OrdinalIgnoreCase) ||
     string.Equals(maskRule.RuleType, "HASH", StringComparison.OrdinalIgnoreCase)))
{
    fieldType = CatalogFieldType.String;
}
```

### 2.2 Wert-Auflösung und Fehlerbehandlung in `CatalogGraphQlTypeModule.cs`
In `ResolveColumnValue`:
1. Hilfsmethode `ReportMasked(IResolverContext ctx, CatalogColumnField col)`:
   ```csharp
   private static object? ReportMasked(IResolverContext ctx, CatalogColumnField col)
   {
       ctx.ReportError(ErrorBuilder.New()
           .SetMessage($"Column '{col.ColumnName}' is masked.")
           .SetCode("MASKED")
           .SetPath(ctx.Path)
           .Build());
       return null;
   }
   ```
2. Strikte Typauflösung:
   - `CatalogFieldType.Int`:
     - Wenn Number: `prop.TryGetInt32(out var n) ? n : (int.TryParse(prop.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pi) ? pi : ReportMasked(ctx, col))`
     - Wenn String: `int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : ReportMasked(ctx, col)`
   - `CatalogFieldType.Long`:
     - Wenn Number: `prop.TryGetInt64(out var n) ? n : (long.TryParse(prop.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pl) ? pl : ReportMasked(ctx, col))`
     - Wenn String: `long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : ReportMasked(ctx, col)`
   - `CatalogFieldType.Float`:
     - Wenn Number: `prop.TryGetDouble(out var n) ? n : (double.TryParse(prop.GetRawText(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var pd) ? pd : ReportMasked(ctx, col))`
     - Wenn String: `double.TryParse(prop.GetString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var n) ? n : ReportMasked(ctx, col)`
   - `CatalogFieldType.Decimal`:
     - Wenn Number: `prop.TryGetDecimal(out var n) ? n : (decimal.TryParse(prop.GetRawText(), NumberStyles.Number, CultureInfo.InvariantCulture, out var pm) ? pm : ReportMasked(ctx, col))`
     - Wenn String: `decimal.TryParse(prop.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : ReportMasked(ctx, col)`
   - `CatalogFieldType.Boolean`:
     - Wenn True/False: `true`/`false`
     - Wenn Number: `prop.TryGetInt64(out var n) ? n != 0 : (prop.TryGetDouble(out var d) ? Math.Abs(d) > double.Epsilon : ReportMasked(ctx, col))`
     - Wenn String:
       - `"true"`, `"false"` (case-insensitive) -> `true`/`false`
       - `"1"`, `"0"` -> `true`/`false`
       - Andernfalls (z. B. `'***'` oder beliebige Maskenwerte) -> `ReportMasked(ctx, col)`

---

## 3. TDD-Teststrategie

1. **Unit-Tests `CatalogSchemaModelTests.cs`:**
   - `BuildAsync_ColumnWithHmacMaskingRule_IsTypedAsStringInSchema`:
     Prüft, dass eine `int`-, `decimal`- oder `boolean`-Spalte mit HMAC-Maskierungsregel im Schema als `CatalogFieldType.String` typisiert wird.
2. **Unit-Tests `CatalogGraphQlSchemaTests.cs`:**
   - `ResolveColumnValue_BooleanWithFloatingPointNumber_DoesNotThrowAndReturnsTrue`:
     Verifiziert, dass `1.0` in JSON für ein Boolean-Feld sauber zu `true` aufgelöst wird.
   - `ResolveColumnValue_MaskedNonStringField_ReportsMaskedErrorCodeAndReturnsNull`:
     Verifiziert, dass ein nicht-numerischer String auf `Int`- und `Boolean`-Spalten einen Fehler mit Code `"MASKED"` hinzufügt und `null` zurückgibt.
   - `Schema_WithHmacMaskedIntAndRedactedBool_ExecutesQueryCleanly`:
     Führt über SQLite eine vollständige GraphQL-Abfrage aus, bei der eine Int-Spalte per HMAC pseudonymisiert wird (und als String im Ergebnis ankommt) und eine Bool-Spalte per REDACT maskiert wird (und als `null` mit Code `MASKED` ankommt).
3. **Regressionstests:**
   - Ausführung der gesamten Unit- und Architektur-Testsuite.

# Implementierungsplan: SQL2-2 OLAP-Kapazität & SQL2-4 Keine Doppelmaskierung

**Status:** ✅ Vollständig umgesetzt & verifiziert  
**Datum:** 2026-10-07  
**Befunde:**
- **SQL2-2:** `SqlConnector` klemmt Zeilenlimit fest auf 5.000 (`Math.Clamp(..., 1, 5000)`). OLAP liest höchstens 5.000 Zeilen, Aggregate werden still über einen Teilausschnitt berechnet, und die Staging-Kapazitätsprüfung greift nie. (`SqlConnector.cs:155`, `DuckDbOlapEndpoints.cs:225-238`)
- **SQL2-4:** OLAP und CrossDomain maskieren ein zweites Mal (`HMAC(HMAC(x))`), wodurch Pseudonyme inkonsistent zu den anderen APIs (GraphQL, REST, WebSQL) werden. (`ConnectorRowMasker.cs:30-53`, `DuckDbOlapEndpoints.cs:248`, `CrossDomainJoinEngine.cs:170, 200, 294, 306`)
**Schweregrad:** Hoch (Laufzeitfehler / Datenkorruption in OLAP & analytischen Aggregaten)  
**Komponenten:**
- `src/Autheris.Infrastructure/Connectors/SqlConnector.cs`
- `src/Autheris.Api/Endpoints/DuckDbOlapEndpoints.cs`
- `src/Autheris.Application/Connectors/ConnectorRowMasker.cs`
- `src/Autheris.Application/Connectors/CrossDomain/CrossDomainJoinEngine.cs`
- `src/Autheris.Application/Connectors/Streaming/StreamingResultPipeline.cs`

---

## 1. Problemursachen (Root Cause Analysis)

### 1.1 SQL2-2: Falscher Clamp in `SqlConnector` & unzureichende Overflow-Erkennung in `DuckDbOlapEndpoints`
1. **Hartkodiertes Limit in `SqlConnector`:**
   In `SqlConnector.ReadBatchAsync`:
   ```csharp
   var clampedLimit = Math.Clamp(session.Limit ?? 1000, 1, 5000);
   ```
   Unabhängig davon, welches Limit `session.Limit` anfordert (z. B. `MaxStagedRowsPerTable = 250_000` oder `MaxExportRows = 1_000_000`), schneidet `SqlConnector` die Abfrage bei 5.000 Zeilen hart ab.
2. **Stille Datenkürzung bei OLAP-Aggregationen:**
   Hat eine Tabelle z. B. 20.000 Zeilen, liest DuckDB nur 5.000 Zeilen und berechnet `SUM`, `AVG`, `COUNT` etc. still über das 5.000-Zeilen-Fragment, ohne Fehler oder Warnung.
3. **Wirkungsloser Kapazitätscheck:**
   In `DuckDbOlapEndpoints`:
   ```csharp
   Limit: options.MaxStagedRowsPerTable,
   ...
   if (rawRows.Count + batch.Count > options.MaxStagedRowsPerTable)
   ```
   Weil das SQL-Query maximal `options.MaxStagedRowsPerTable` Zeilen (und durch den Clamp sogar maximal 5.000 Zeilen) anfordert, kann die Datenbank niemals mehr als `options.MaxStagedRowsPerTable` Zeilen liefern. `rawRows.Count + batch.Count > options.MaxStagedRowsPerTable` kann aus einem Split niemals `true` werden.

### 1.2 SQL2-4: Doppelmaskierung (`HMAC(HMAC(x))`)
1. **Maskierung in `SqlDataSourceExecutor`:**
   Wenn `SqlConnector.ReadBatchAsync` `SqlDataSourceExecutor.ExecuteAsync` aufruft, werden HMAC-Spalten (`gatewayHmacColumns`) bereits in `ReadRowsAsync` via `_maskingProvider.MaskValue(...)` pseudonymisiert und `context.Items["InDbColumnMaskingExecuted"] = true` gesetzt.
2. **Zweite Maskierung in `DuckDbOlapEndpoints` & `CrossDomainJoinEngine`:**
   Die Endpunkte / Engines rufen anschließend ungeprüft `ConnectorRowMasker.MaskRow(...)` auf.
   `ConnectorRowMasker` ruft erneut `_maskingProvider.MaskValue(...)` auf den bereits pseudonymisierten Wert auf.
   Folge: Die Pseudonyme in DuckDB und CrossDomain stimmen nicht mit jenen aus GraphQL/REST/WebSQL überein (`HMAC(HMAC(x))` statt `HMAC(x)`).

---

## 2. Architektur- & Lösungsentwurf

### 2.1 Dynamischer Limit-Ceiling in `SqlConnector`
- `SqlConnectorRecordSource` speichert `IOptions<GatewayOptions>? _options`.
- Bestimmung des maximal zulässigen Abfragelimits:
  ```csharp
  var maxAllowed = Math.Max(5000, Math.Max(
      _options?.Value?.Olap?.MaxStagedRowsPerTable ?? 250_000,
      _options?.Value?.Export?.MaxExportRows ?? 1_000_000));
  ```
- Wenn `session.Limit` gesetzt ist, wird es bis zu `maxAllowed` (bzw. bei Overflow-Checks bis `session.Limit`) erlaubt, anstatt es bei 5.000 abzuklemmen. Wenn `session.Limit` nicht gesetzt ist, bleibt der Standard bei 1.000.

### 2.2 Exakte Staging-Overflow-Erkennung in `DuckDbOlapEndpoints`
- In `DuckDbOlapEndpoints.cs` wird beim Erstellen des `ConnectorSessionContext` `options.MaxStagedRowsPerTable + 1` angefordert.
- Hat die Tabelle mehr Zeilen als `MaxStagedRowsPerTable`, liefert die Datenbank `MaxStagedRowsPerTable + 1` Zeilen zurück.
- Die Bedingung `if (rawRows.Count + batch.Count > options.MaxStagedRowsPerTable)` schlägt sofort und zuverlässig an und bricht mit `400 BadRequest` (`Table exceeds maximum allowed staging rows`) ab.

### 2.3 Idempotente Einzelmaskierung in `ConnectorRowMasker`, `DuckDbOlapEndpoints` & `CrossDomainJoinEngine`
- `ConnectorRowMasker.MaskRow` erhält einen Parameter `bool alreadyMasked = false` bzw. eine Überladung für `ConnectorSessionContext`.
- Wenn `alreadyMasked` wahr ist (oder `session.Items["InDbColumnMaskingExecuted"] == true`), werden `Deny`-Spalten weiterhin entfernt, aber bereits berechnete Masken / Pseudonyme werden **nicht** ein zweites Mal transformiert.
- In `DuckDbOlapEndpoints`, `CrossDomainJoinEngine` und `StreamingResultPipeline` wird `InDbColumnMaskingExecuted` ausgewertet und an `ConnectorRowMasker` weitergegeben.

---

## 3. Testgetriebene Umsetzung (TDD)

1. **Unit-Tests in `tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs`:**
   - `SQL202_SqlConnector_RespectsSessionLimitBeyond5000`: Testet, dass `SqlConnector` bei `session.Limit = 12000` nicht auf 5.000 klemmt.
   - `SQL202_DuckDbOlap_TableExceedingMaxStagedRows_FailsWith400`: Testet, dass Tabellen mit mehr als `MaxStagedRowsPerTable` mit HTTP 400 abgelehnt werden.
2. **Unit-Tests in `tests/Autheris.Tests.Unit/Security/GovernedDataPathsG4Tests.cs`:**
   - `SQL204_ConnectorRowMasker_WhenAlreadyMasked_DoesNotDoubleMask`: Testet, dass `ConnectorRowMasker` bei `alreadyMasked = true` (oder gesetztem `InDbColumnMaskingExecuted`) `MaskValue` nicht erneut aufruft.
   - `SQL204_DuckDbOlap_HmacColumns_AreNotDoubleMasked`: Testet, dass HMAC-Spalten im DuckDB-Staging exakt einmal pseudonymisiert werden.

---

## 4. Verifikation
- Unit-Tests: `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj` (2.593 Tests bestanden, 0 Fehler).
- Gezielte TDD-Tests: `SQL202_SqlConnector_RespectsSessionLimitBeyond5000`, `SQL202_DuckDbOlap_TableExceedingMaxStagedRows_FailsWith400`, `SQL204_ConnectorRowMasker_WhenAlreadyMasked_DoesNotDoubleMask`, `SQL204_ConnectorRowMasker_WithSession_RespectsInDbColumnMaskingExecuted` (4 Tests bestanden).
- Architektur-Tests: `dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj` (12 Tests bestanden, 0 Fehler).
- Gesamte Solution gebaut: `dotnet build Autheris.sln` (0 Warnungen, 0 Fehler).
- Dokumentation in `docs/plans/status-und-umsetzungsplan-2026-10-07.md` aktualisiert.

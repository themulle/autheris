# Umsetzungsplan: Beseitigung In-Memory Demo-Generierung in LoadInvoiceItemsBatchAsync (SQL2-17)

**Referenz:** [security-review-2026-10-07.md](security-review-2026-10-07.md), Befund SQL2-17; Architektur-Leitfaden `csharp-architect`.  
**Datum:** 2026-10-09  
**Branch:** `feat/ast-target-dialect-generator`

---

## 1. Problemstellung & Ist-Zustand (Befund SQL2-17)

In `src/Autheris.Application/Services/GatewayExecutionService.cs:948-1056` existiert die Methode `LoadInvoiceItemsBatchAsync`, welche vom GraphQL-DataLoader `InvoiceItemDataLoader` für Sub-Selects von Rechnungs-Positionen (`invoices.items`) aufgerufen wird.

Statt die Kind-Tabelle `finance.dbo.finance_items` über die registrierten Datenquellen-Executoren (`IDataSourceExecutor` / `SqlDataSourceExecutor`) abzufragen, erzeugt der Code in Zeile 981-985 hart kodierte synthetische Demo-Daten im RAM:
```csharp
for (int i = 1; i <= 2; i++)
{
    var rawNote = $"Confidential spec for item {i} of invoice {invId}";
    var rawProduct = $"Enterprise License Pack {i}";
    var rawPrice = 1250.00m * i;
    ...
```
Dieser Demo-Code umgeht in Produktions-Umgebungen reale Datenbanken, ignoriert Verbindungs-Konfigurationen und widerspricht dem Zero-Trust- und Clean-Architecture-Standard des Autheris-Gateways.

---

## 2. Ziel-Architektur & Design

1. **Governance & Zero-Trust Access Resolution:**
   - Prüfung von `CheckTableAccessAsync(principal, childTableId, ct)`. Wenn nicht erlaubt, Abbruch (Fail-Closed, 0 Queries, leeres Dictionary).
   - Abruf der Tabellen-Metadaten via `_metadataRepository.GetTableMetadataAsync(childTableId, ct)`.
   - Identifikation des Fremdschlüssel-Felds (`parent_id` oder `invoice_id`).

2. **Delegation an `IDataSourceExecutor` via `_chunkedQueryExecutor`:**
   - Ermittlung des passenden `IDataSourceExecutor` für `metadata.Table.DataSourceType` (oder Konnektor aus `_connectorRegistry`).
   - Wenn kein Executor vorhanden ist:
     - Wenn keine Verbindung konfiguriert ist und kein Dev-Mocking aktiv ist: Fail-Closed Rückgabe leerer Listen je angefragter ID.
   - Batch-Ausführung über `_chunkedQueryExecutor.ExecuteGroupedWithMetricsAsync`:
     - Pro Chunk von `invoiceIds`:
       - Erstellung eines parametrisierten `TableFilterClause` mit `IN (@p0, @p1, ...)` auf das Fremdschlüssel-Attribut.
       - Erstellung des `DataSourceExecutionContext` mit autorisierten Spalten (ohne `ColumnAccessLevel.Deny`).
       - Ausführung über `executor.ExecuteAsync(execContext, chunkCt)`.
       - Anwendung von `GovernedConnectorReader.Apply` (RLS-Filterung, Spaltenmaskierung, Byte-Limit).
       - Zuordnung der Ergebnis-Zeilen (`InvoiceItemRecord`) zum jeweiligen Rechnungs-Key (`invId`).
       - Sicherstellung, dass jeder Key des Chunks im Ergebnis-Dictionary vorhanden ist.

3. **Synthetischer Dev-Fallback in `SqlDataSourceExecutor`:**
   - In `SqlDataSourceExecutor.GenerateSyntheticRows`: Falls ein `TableFilterClause` für `parent_id` / `invoice_id` vorhanden ist, werden pro angefragter Parent-ID realistische Kind-Zeilen generiert, sodass Unit-Tests und Dev-Umgebungen konsistente relationale Mock-Daten erhalten, ohne dass Hardcoding im GatewayService nötig ist.

---

## 3. TDD-Schritte (Rot -> Grün -> Refactor)

1. **Rot (Failing Tests):**
   - Erstellung von Tests in `tests/Autheris.Tests.Unit/InvoiceItemsBatchExecutionTests.cs`:
     - Test 1: `LoadInvoiceItemsBatchAsync_DispatchesGovernedQueryToExecutor_WithParameterizedInFilter` (verifiziert, dass `IDataSourceExecutor.ExecuteAsync` mit `TableFilterClause` aufgerufen wird).
     - Test 2: `LoadInvoiceItemsBatchAsync_WhenNoExecutorConfiguredAndNoConnection_ReturnsEmptyListsFailClosed`.
     - Test 3: `LoadInvoiceItemsBatchAsync_CorrectlyAppliesMaskingAndColumnSecurityFromExecutorRows`.
2. **Grün (Implementation):**
   - Refactoring von `GatewayExecutionService.LoadInvoiceItemsBatchAsync` zur Beseitigung der Hardcoded Demo-Schleife.
   - Unterstützung von IN-Filter-Parametern in `SqlDataSourceExecutor.GenerateSyntheticRows` für Dev/Test-Konsistenz.
   - Verifikation bestehender Tests in `DataPathSecurityTests.cs`, `OddBatchDataLoaderEvaluationTests.cs` und `ChunkedQueryExecutorTests.cs`.
3. **Refactor & Verifikation:**
   - `dotnet test tests/Autheris.Tests.Unit` & Build mit 0 Warnings.
   - Ein Commit: `feat(graphql): replace synthetic in-memory demo generation in LoadInvoiceItemsBatchAsync with governed query execution (SQL2-17)`.

# Security Review & Härtungs-Vorgaben: Native Apache Arrow IPC & Flight SQL Egress (F-DATA-04)

**Datum:** 2026-10-03  
**Rolle:** Principal Security Expert  
**Status:** Genehmigt mit verbindlichen Auflagen  
**Referenz:** [`implementation-plan-f-data-04-arrow-flight-sql-2026-10-03.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-f-data-04-arrow-flight-sql-2026-10-03.md)

---

## 1. Bedrohungsanalyse (STRIDE) für spaltenorientierten Binär-Egress

| STRIDE | Bedrohungsszenario | Erforderliche Gegenmaßnahme |
| :--- | :--- | :--- |
| **Spoofing** | Angreifer fälscht Tenant-Header oder manipuliert gRPC-Flight-Metadaten, um Daten anderer Mandanten abzurufen. | Strikte Authentifizierung über JWT Bearer Tokens in HTTP-Headers bzw. gRPC-CallContext. TenantId muss mit dem Claim des Tokens übereinstimmen. |
| **Tampering** | Manipulation der SQL-Parameter oder Arrow-Offsets zur Erzeugung von Buffer Overflows. | Typsichere Parameterbindung; Apache.Arrow Memory Management nutzt verwaltete Speicher-Buffer; Schema-Inferenz wird serverseitig fixiert. |
| **Repudiation** | Massendaten-Abflüsse (Exfiltration) im Gigabyte-Bereich erfolgen ohne Audit-Spur. | Strukturierte Audit-Logs für jeden Arrow-Export mit `tenant_id`, `user_sid`, `query_hash`, `row_count`, `duration_ms` und WORM-Audit-Event. |
| **Information Disclosure** | Vektorisierte Arrow-Konvertierung umgeht versehentlich die RLS-Filter oder die dynamische PII-Spaltenmaskierung. | **Defense-in-Depth & Separation of Concerns:** Der `ArrowExportService` ist ein reiner Serializer. Er empfängt ausschließlich Datensätze, die die RLS-Filterkaskade und die `ColumnMaskingProvider`-Maskierung bereits vollständig durchlaufen haben. |
| **Denial of Service** | "Arrow-Bomb" / Memory Exhaustion: Anfragen nach 50 Mio. Datensätzen überlasten den Gateway-Arbeitsspeicher (OOM). | **Streaming Chunking & Bounding:** Feste Obergrenze `MaxExportRows` (Standard: 1.000.000), `BatchSize` (64.000 Zeilen pro Batch), sofortiges binäres Ausspülen (`FlushAsync`) in den Netzwerk-Stream ohne Akkumulation aller Batches im RAM. |
| **Elevation of Privilege** | Direkter Aufruf des Export-Endpunkts ohne ReBAC- oder ABAC-Berechtigungsnachweis für die Ziel-Tabelle. | Integration des `RebacEndpointFilter` (`.RequireRebac("viewer", "table", paramName: "table")`) und Casbin-Richtlinienprüfung vor der Abfrageausführung. |

---

## 2. Verbindliche Härtungsregeln für die Implementierung

1. **Zero-Bypass-Prinzip für Data Governance:**
   * Es darf unter keinen Umständen ein direkter SQL-Pass-Through zu Arrow existieren.
   * Jede Zeile muss vor der Überführung in Arrow-Arrays durch `StreamRlsPolicyEnforcer` / `GovernedSqlExecutionService` auf RLS-Gültigkeit geprüft und sensible Spalten (z. B. Gehalt, IBAN, Kreditkartennummern) müssen maskiert sein (`REDACTED` oder deterministischer Hash).

2. **Null-Bitmaps & Typsicherheit:**
   * Null-Werte in der relationalen Datenbank müssen in Arrow-Arrays korrekt als gesetzte Null-Bitmaps abgebildet werden, um Buffer-Interpretationfehler bei externen Clients (DuckDB, Pandas) zu verhindern.

3. **Multi-Tenant Boundary (Anti-IDOR):**
   * Alle Abfragen müssen zwingend auf den `tenant_id`-Geltungsbereich des Tokens beschränkt sein.

---

## 3. Geforderte Security Unit- & Integrationstests

Vor der Freigabe müssen folgende Tests in `tests/Autheris.Tests.Unit/Security/ArrowExportSecurityTests.cs` implementiert werden und erfolgreich sein:

1. **`ArrowExport_EnforcesRls_ExcludesUnauthorizedRows`**:
   * Bei einer Abfrage mit RLS-Filter (z. B. nur Abteilung `Finance`) dürfen keine Zeilen anderer Abteilungen in den Arrow RecordBatches enthalten sein.
2. **`ArrowExport_EnforcesColumnMasking_MasksSensitiveFields`**:
   * Maskierte PII-Spalten müssen im Arrow `StringArray` den maskierten Wert (z. B. `[REDACTED]`) enthalten, nicht den Klartext.
3. **`ArrowExport_EnforcesMaxRowLimit_ThrowsOnExcessiveRows`**:
   * Überschreitet ein Export das Limit `MaxExportRows`, muss der Vorgang Fail-Closed abgebrochen werden.
4. **`ArrowExport_UnauthenticatedCaller_Returns401`**:
   * Anonyme Requests an `/api/v1/export/arrow` müssen mit `HTTP 401 Unauthorized` abgewiesen werden.
5. **`ArrowExport_RebacDenial_Returns403`**:
   * Fehlt dem Benutzer die erforderliche ReBAC-Relation auf die Tabelle, muss `HTTP 403 Forbidden` zurückgegeben werden.
6. **`ArrowExport_NullAndTypeSafety_CorrectlyBuildsNullBitmaps`**:
   * Überprüfung von `null`-Werten über verschiedene Datentypen (Int32, Int64, Double, String, Boolean, DateTime) auf einwandfreie Arrow Null-Bitmaps.

---

## 4. Freigabe
Der Implementierungsplan wird unter Einhaltung obiger Vorgaben für die TDD-Umsetzung freigegeben.

# F-DATA-03: Embedded In-Memory OLAP via DuckDB.NET

## 1. Executive Summary & Problemstellung

Heterogene Cross-Domain-Joins und analytische Aggregationen über getrennte Quellsysteme (z. B. relationale SQL-Datenbanken, REST-APIs, S3-Parquet-Lakehouses und Microservices) stellen Daten-Gateways vor erhebliche Herausforderungen:
- Die Ausführung im Standard-.NET-Heap (verschachtelte Dictionaries, LINQ-Transformationen) führt bei wachsenden Datenmengen zu Garbage-Collection-Pausen (Gen2/LOH-Druck) und unvorhersehbarem RAM-Verbrauch.
- Der alternative Einsatz externer Virtualisierungs-Cluster (wie Apache Trino, Presto oder Denodo) verursacht hohe Infrastruktur-, Betriebs- und Netzwerklatenzkosten, die sich für viele Ad-hoc-Analysen im Mittelstand nicht amortisieren.

Mit **F-DATA-03** integriert **Autheris** die native, spaltenorientierte Vektorengine **DuckDB** (`DuckDB.NET.Data.Full`) direkt im Gateway-Prozess. Teilresultate aus heterogenen Konnektoren werden nach strikter Governance-Prüfung (Row-Level Security, Casbin-ABAC, ReBAC und PII-Maskierung) flüchtig in In-Memory DuckDB-Tabellen gestagt und mit hardwarebeschleunigter SIMD-Vektorisierung verarbeitet.

---

## 2. Architektur & Datenfluss

```
+-----------------------------------------------------------------------------------+
|                        Client / BI-Tool / Analytics Worker                        |
+-----------------------------------------------------------------------------------+
                                          |
                                          | POST /api/v1/olap/query
                                          | Header: Authorization: Bearer <token>
                                          v
+-----------------------------------------------------------------------------------+
| Autheris API & Governance Gate                                                  |
|   1. Authentication (JWT / Bearer Token)                                          |
|   2. ReBAC Check (OpenFGA can_query für jede referenzierte Tabelle)              |
|   3. Casbin ABAC & Tenant Isolation Filter Pushdown                               |
|   4. Dynamic Column Masking (PII / SHA256 / Full-Redaction)                       |
+-----------------------------------------------------------------------------------+
                                          | Governed In-Memory Row Sets
                                          v
+-----------------------------------------------------------------------------------+
| IDuckDbOlapEngine (DuckDbOlapEngine)                                              |
|   +-----------------------------------------------------------------------------+ |
|   | Flüchtige In-Memory DuckDB Session (DataSource=:memory:)                     | |
|   |   - Sandboxing: SET enable_external_access = false;                         | |
|   |   - Quota Guard: PRAGMA max_memory = '1GB'; PRAGMA threads = 2;             | |
|   |   - Table Staging: CREATE TEMP TABLE + Parameterized Multi-Row Inserts      | |
|   |   - SIMD-Vektorisierte Query Execution (Window Functions, Aggs, Joins)      | |
|   |   - CancellationToken Timeout Enforcement                                   | |
|   +-----------------------------------------------------------------------------+ |
+-----------------------------------------------------------------------------------+
                                          |
                                          v
                                   HTTP 200 OK (JSON)
```

---

## 3. Sicherheitskontrollen (SEC-OLAP-01 bis SEC-OLAP-05)

1. **Sandboxing (SEC-OLAP-01):** Unmittelbar beim Verbindungsaufbau wird `SET enable_external_access = false;` gesetzt. DuckDB-Befehle wie `read_csv`, `read_parquet` oder `COPY ... TO` auf Host-Dateipfade werden auf C++-Ebene deterministisch blockiert.
2. **Speicher- & Thread-Begrenzung (SEC-OLAP-02):** `PRAGMA max_memory = '1GB'` und `MaxStagedRowsPerTable` (Default: 250.000 Zeilen) verhindern Heap-Exhaustion und Denial-of-Service-Angriffe.
3. **Session-Isolation (SEC-OLAP-03):** Jede Abfrage erhält eine flüchtige In-Memory-Verbindung (`DataSource=:memory:`), die mit dem Ende des HTTP-Requests über `IDisposable` vollständig verworfen wird. Es existiert keine persistente Cross-Tenant-Kontamination.
4. **Identifier-Sanitization (SEC-OLAP-04):** Tabellen- und Spaltennamen werden gegen Regex-Whitelist (`^[a-zA-Z0-9_]+$`) validiert und doppelt gequotet. Zeilendaten werden typisiert als Parameter übergeben.
5. **Pre-Flight Governance Gate (SEC-OLAP-05):** Sämtliche Quelltabellen müssen vor dem Staging ReBAC (`can_query`) und ABAC-Konsistenzprüfungen bestehen. Maskierte PII-Werte bleiben unverändert maskiert.

---

## 4. API Endpunkte

### `POST /api/v1/olap/query`
Führt eine analytische SQL-Abfrage über eine oder mehrere governten Katalogtabellen in DuckDB aus.

#### Request Headers
- `Authorization: Bearer <token>`
- `Content-Type: application/json`

#### Request Body
```json
{
  "sql": "SELECT c.company_name, COUNT(i.invoice_id) as invoice_count, SUM(i.total) as total_spent FROM customers c JOIN invoices i ON c.customer_id = i.customer_id GROUP BY c.company_name ORDER BY total_spent DESC",
  "tableNames": [
    "crm.public.customers",
    "billing.public.invoices"
  ],
  "limit": 100
}
```

#### Response (HTTP 200 OK)
```json
{
  "columns": ["company_name", "invoice_count", "total_spent"],
  "rows": [
    ["Acme Corp", 2, 1000.0],
    ["Globex", 1, 200.0]
  ],
  "totalRows": 2,
  "executionDurationMs": 14.8
}
```

---

## 5. Konfiguration (`appsettings.json`)

```json
{
  "DuckDbOlap": {
    "Enabled": true,
    "MaxMemory": "1GB",
    "MaxStagedRowsPerTable": 250000,
    "QueryTimeoutSeconds": 60,
    "MaxThreads": 2,
    "EnableCrossDomainJoinOptimization": true
  }
}
```

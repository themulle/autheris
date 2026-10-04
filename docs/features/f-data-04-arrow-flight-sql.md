# F-DATA-04: Native Apache Arrow Flight SQL Egress & Zero-Copy Analytics Pipeline

## 1. Executive Summary & Zielsetzung

Mit **F-DATA-04** stellt das **Autheris** einen nativen, ultra-performanten analytischen Datenkanal bereit. Während klassische REST- und GraphQL-Endpoints Daten in serialisiertem JSON übermitteln – was bei Data-Science- und ML-Workloads (Python / Polars / Pandas / DuckDB) bis zu 80 % der CPU- und Memory-Ressourcen für String-Parsing und Objekterzeugung verschlingt –, transferiert der Arrow-Egress tabellarische Daten direkt im **Apache Arrow IPC Streaming Format** (`application/vnd.apache.arrow.stream`).

Gleichzeitig bleibt das Enterprise-Sicherheitsversprechen von Autheris kompromisslos gewahrt: Sämtliche Arrow-Exporte passieren denselben zentralen Governance-Stack (Row-Level Security / RLS Pushdown, dynamische Spalten- und PII-Maskierung, Casbin ABAC und ReBAC-Validierung via OpenFGA).

---

## 2. Architektur & Design

```
+-----------------------------------------------------------------------------------+
|                            Client (Python / Polars / DuckDB)                      |
+-----------------------------------------------------------------------------------+
                                          |
                                          | POST /api/v1/export/arrow
                                          | Header: Accept: application/vnd.apache.arrow.stream
                                          v
+-----------------------------------------------------------------------------------+
| Autheris API Layer                                                              |
|   1. Authentication (JWT / API-Key Bearer)                                        |
|   2. Relationship-Based Access Control (ReBAC OpenFGA Check: table#can_export)    |
|   3. Table / Query Dispatcher                                                     |
+-----------------------------------------------------------------------------------+
                                          |
                                          v
+-----------------------------------------------------------------------------------+
| Governed Execution Engine                                                         |
|   - Row-Level Security (RLS Tenant Filter Pushdown)                               |
|   - Dynamic Data Masking (PII / SHA256 / Full-Redaction)                          |
|   - Casbin Attribute-Based Access Control (ABAC)                                  |
+-----------------------------------------------------------------------------------+
                                          | Governed Object Row Stream
                                          v
+-----------------------------------------------------------------------------------+
| IArrowExportService (ArrowExportService)                                          |
|   - Arrow Schema Builder (Int32, Int64, Double, Decimal128, Boolean, Timestamp...) |
|   - Null-Bitmap Vectorization                                                     |
|   - Chunked RecordBatch Builder (BatchSize: 64,000 Rows)                          |
|   - Zero-Copy Streaming Serialization via ArrowStreamWriter                       |
|   - DoS Protection Guard (MaxExportRows Enforcement)                              |
+-----------------------------------------------------------------------------------+
                                          |
                                          v (HTTP 200 Binary Arrow Stream)
                                    Response Stream
```

---

## 3. Typ-Mapping & Null-Sicherheit

| .NET / SQL Typ | Apache Arrow Field Type | Nullable Handling |
| :--- | :--- | :--- |
| `int`, `short`, `byte` | `Int32Type` | Null-Bitmap bitwise tracking (`AppendNull()`) |
| `long` | `Int64Type` | Null-Bitmap bitwise tracking |
| `double`, `float` | `DoubleType` | Null-Bitmap bitwise tracking |
| `decimal` | `Decimal128Type(38, 18)` | Null-Bitmap bitwise tracking |
| `bool` | `BooleanType` | Null-Bitmap bitwise tracking |
| `DateTime`, `DateTimeOffset` | `TimestampType(Microsecond, UTC)` | Microsecond Epoch conversion |
| `byte[]` | `BinaryType` | Null-Bitmap bitwise tracking |
| `string`, `Guid`, fallback | `StringType` | UTF-8 Byte Offset Buffer |

---

## 4. API Endpoints

### `POST /api/v1/export/arrow`
Exportiert Daten einer Tabelle oder einer governten SQL-Abfrage direkt als binären Arrow-Stream.

#### Request Headers
- `Authorization: Bearer <token>`
- `Content-Type: application/json`
- `Accept: application/vnd.apache.arrow.stream`

#### Request Body
```json
{
  "tableName": "sales_records",
  "query": "SELECT order_id, customer_id, amount, status FROM sales_records WHERE status = 'COMPLETED'",
  "batchSize": 64000
}
```

#### Response
- `Content-Type: application/vnd.apache.arrow.stream`
- Binary Arrow IPC Stream (RecordBatches mit Schema-Header und EOS-Marker).

---

## 5. Security & Threat Mitigation (F-DATA-04)

1. **ReBAC & ABAC Enforcement:** Vor jeder Abfrage validiert `IRebacService.CheckAsync` das Recht `can_export` auf dem Objekt `table:<name>`. Bei Nichterfüllung wird der Request mit HTTP 403 Forbidden abgewiesen.
2. **Dynamic PII Masking Preservation:** Maskierte Werte aus dem Governance-Layer werden 1:1 in die Arrow String/Binary Arrays geschrieben. Es existiert keine Möglichkeit, unmaskierte Rohdaten über den Arrow-Kanal zu extrahieren.
3. **DoS & Resource Exhaustion Protection:** Konfigurierbare Obergrenzen (`Arrow:MaxExportRows`, Default: 1.000.000) verhindern Out-of-Memory Zustände. Überschreitet das Resultset das Limit, bricht der Export mit einer klaren Exception ab.
4. **Binary Validation:** Arrow-Streams werden per Unit-Test über den offiziellen `ArrowStreamReader` validiert.

---

## 6. Python / Polars Integration Example

```python
import requests
import polars as pl
import io

headers = {
    "Authorization": "Bearer <token>",
    "Content-Type": "application/json",
    "Accept": "application/vnd.apache.arrow.stream"
}

body = {
    "tableName": "telemetry_events",
    "query": "SELECT device_id, temperature, cpu_load, recorded_at FROM telemetry_events"
}

response = requests.post("https://gateway.internal/api/v1/export/arrow", headers=headers, json=body, stream=True)
response.raise_for_status()

# Zero-Copy Ingestion in Polars DataFrame
df = pl.read_ipc(io.BytesIO(response.content))
print(df.head())
```

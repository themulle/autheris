# F-DATA-03: Embedded In-Memory OLAP via DuckDB.NET

**Components:** [`DuckDbOlapEndpoints.cs`](file:///root/autheris/src/Autheris.Api/Endpoints/DuckDbOlapEndpoints.cs), [`IOlapStagingService.cs`](file:///root/autheris/src/Autheris.Application/Olap/IOlapStagingService.cs), [`OlapStagingService.cs`](file:///root/autheris/src/Autheris.Application/Olap/OlapStagingService.cs), [`IDuckDbOlapEngine.cs`](file:///root/autheris/src/Autheris.Application/Olap/IDuckDbOlapEngine.cs), [`DuckDbOlapEngine.cs`](file:///root/autheris/src/Autheris.Infrastructure/Olap/DuckDbOlapEngine.cs)

---

## 1. Overview & Problem Statement

Cross-domain analytical queries joining disparate data sources (e.g. relational Postgres, S3 Parquet, and REST APIs) cause high GC allocations and memory pressure if performed on the standard .NET heap. F-DATA-03 embeds DuckDB (`DuckDB.NET.Data.Full`) directly inside the gateway process. Governed row sets from multiple sources are staged into ephemeral, in-memory DuckDB tables and aggregated using SIMD-vectorized columnar execution.

---

## 2. Business Value

- **Sub-Second Cross-Source Analytics**: Executes complex aggregations, window functions, and joins across heterogeneous sources at vector speeds.
- **Zero GC Heap Pressure**: DuckDB handles intermediate analytical buffers in native unmanaged memory, preventing .NET Gen2 Garbage Collection pauses.
- **Ironclad Sandboxing**: Evaluates `SET enable_external_access = false;` to deterministically block unauthorized host filesystem or network access.

---

## 3. Architecture & Capabilities

- Sandboxed ephemeral in-memory sessions (`DataSource=:memory:`).
- Hardware-accelerated vectorized processing of multi-table joins.
- Strict memory quotas (`PRAGMA max_memory = '1GB'`) and thread pool capping.

### 3.1 Staging Use Case

`POST /api/v1/olap/query` only handles HTTP concerns (authentication, body parsing, response mapping). `IOlapStagingService` (Application layer) stages and executes the request:

1. Rejects more than `MaxTableCount` distinct tables (`400`).
2. Applies the tenant and global data-source allowlists before any access decision or read (`403`).
3. Takes one access decision per table (ReBAC when enabled, consents, Casbin, row filters) without exposing policy details (`403`).
4. Reads every table through the governed reader (row filter, masking applied exactly once) and enforces `MaxStagedRowsPerTable` and `MaxTotalStagedRows` (`400`).
5. Writes the required audits `OLAP_TABLE_STAGED` (per table) and `OLAP_QUERY` through `IAuditWriter`; an unavailable audit store rejects the request with `503`.
6. Runs the query in DuckDB and returns the result.

The endpoint's request and response contract is unchanged by this split. An architecture test forbids repository dependencies in `DuckDbOlapEndpoints`.

---

## 4. Usage Example

```bash
# Execute analytical aggregation across staged tables in DuckDB
curl -X POST http://localhost:8080/api/v1/olap/query \
  -H "Authorization: Bearer <user-token>" \
  -H "Content-Type: application/json" \
  -d '{
    "sql": "SELECT c.company_name, COUNT(i.invoice_id) as invoice_count, SUM(i.total) as total_spent FROM customers c JOIN invoices i ON c.customer_id = i.customer_id GROUP BY c.company_name ORDER BY total_spent DESC",
    "tableNames": ["crm.public.customers", "billing.public.invoices"],
    "limit": 50
  }'
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "DuckDbOlap": {
      "Enabled": true,
      "MaxMemory": "1GB",
      "MaxStagedRowsPerTable": 250000,
      "QueryTimeoutSeconds": 60,
      "MaxThreads": 2
    }
  }
}
```

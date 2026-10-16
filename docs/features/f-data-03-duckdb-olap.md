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

1. Bounds and parses the SQL text before any table is resolved: at most `DuckDbOlap:MaxSqlLength` characters (default 32768), no backslash, no `E'..'` or `U&'..'` string, and exactly one `SELECT` statement as judged by DuckDB's own parser (`json_serialize_sql`), not by a text scan (`400`).
2. Rejects more than `MaxTableCount` distinct requested tables (`400`).
3. Resolves and authorizes **every** requested table before any denial is returned and before any source is touched: tenant and global data-source allowlist, one access decision per table (ReBAC when enabled, consents, Casbin, row filters) and an active connector, without exposing policy details (`403`). An inactive table, an unknown table and a bare table name that matches more than one catalog table answer like an unknown table. Tables are de-duplicated on their resolved identity; staged names (table and `domain_table` view) that collide after sanitizing are denied.
4. Classifies the request (see 3.2). A cross-source request passes the federation gate and the SQL guardrails (3.3) before any read.
5. Takes an admission lease (global cap `MaxConcurrentQueries`, per-tenant cap `MaxConcurrentQueriesPerTenant`, `429` when exceeded), then reads every table through the governed reader (row filter, masking applied exactly once) under the request timeout and enforces `MaxStagedRowsPerTable`, `MaxTotalStagedRows`, `MaxStagedBytesPerTable` and `MaxTotalStagedBytes` (`400`). Columns the caller is denied are not staged at all: they behave like columns that do not exist. Masked columns are staged as `VARCHAR`. The predicates of the analytical query are not pushed to the sources: each table is staged with all rows the caller may see, and filtering happens in DuckDB. A table with more visible rows than `MaxStagedRowsPerTable` therefore fails the request even when the query would select only a few of them.
6. Writes the required audits through `IAuditWriter`: `OLAP_TABLE_STAGED` (per table), `OLAP_QUERY` (with `crossSource`, `transport`, source labels, connector ids, staged bytes, SQL hash and length, trace id), `OLAP_QUERY_FAILED` when execution fails after tables were staged, and `OLAP_DENIED` (decision `DENY`, reason code) for every denial after authorization. An unavailable audit store rejects the request with `503`; a failed denial record never turns a denial into an allow.
7. Runs the query in DuckDB and returns the result. No exception escapes the service: failures map to `400`, `403`, `429`, `502` or `504` with fixed bodies.

The request contract is unchanged; responses of policy denials carry an additional stable `code`. Outside Development and Quickstart, error bodies contain no table, column or rule names.

### 3.2 Cross-Source Classification and the Federation Gate

A request is cross-source when its resolved tables use more than one connector id or more than one source label (`SourceName`, or the domain when none is set), compared case-insensitively, or when any table is not a plain SQL table. Single-source requests (including joins of several tables of one source) are not federated and do not need the gate.

A cross-source request is denied with `403` (`OLAP_CROSS_SOURCE_DENIED`, fixed text "Cross-source queries are not enabled for this endpoint.") unless `Gateway:WebSql:CrossSource:Enabled` is `true` **and** `Olap` is listed in `Gateway:WebSql:CrossSource:AllowedTransports`. The text is identical for "disabled" and "transport not allowed", and it is produced only after every table passed authorization. `Olap` is never part of the default list (`WebSql`, `Trino`); a configured list replaces the default, so operators who also use WebSQL or Trino federation must list all three names. `HttpPlugin` and lakehouse tables are rejected for cross-source requests. A cross-source request may stage at most `WebSql:CrossSource:MaxTableCount` tables, and its staging byte budgets are the smaller of the `CrossSource` and `DuckDbOlap` values.

### 3.3 SQL Guardrails

For **every** request: one `SELECT` statement, no function on the engine blocklist, extension autoinstall and autoload disabled (`autoinstall_known_extensions` and `autoload_known_extensions` are `false`), `enable_external_access=false`, `lock_configuration`.

For **cross-source** requests the statement is additionally limited by a code-held policy (`OlapSqlPolicy`, changed by code, not by configuration):

- only functions on an explicit allowlist (aggregates, null handling, basic numeric, string and date functions); `columns`, `query`, `query_table`, `lpad`, `list_*` and every unknown name are rejected;
- no table functions, `NATURAL`/`POSITIONAL`/`LATERAL` joins, `TABLESAMPLE`, `QUALIFY`, `GROUP BY ALL`, `ORDER BY ALL`, `COLUMNS(...)`, `* EXCLUDE`, `* REPLACE` or positional `#n` references;
- only the staged tables of the request may be referenced;
- cross joins, comma joins without a predicate and non-equi joins are rejected unless `WebSql:CrossSource:AllowNonEquiJoins` is `true`;
- the text must parse in the gateway SQL dialect (`400 OLAP_SQL_NOT_ANALYZABLE`), and a join or filter on a masked or denied column is rejected (`403 OLAP_COLUMN_POLICY`). Columns masked with the `HMAC` rule remain joinable, exactly as in WebSQL; `HASH` and `HMAC_SHA256` are rejected.

The column guardrail is a policy-consistency control, not a confidentiality boundary. The boundary is that masking and column denial are applied before rows are staged into DuckDB. The OLAP endpoint is "gated and bounded"; client DuckDB SQL is still executed inside the sandbox for single-source and for cross-source statements that pass the checks.

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
    },
    "WebSql": {
      "CrossSource": {
        "Enabled": true,
        "AllowedTransports": ["WebSql", "Trino", "Olap"]
      }
    }
  }
}
```

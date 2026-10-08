# F-DATA-02: Governed WebSQL Engine & Trino REST Protocol

**Status:** [Done] (100% GA – Wave 1 & 100% Trino Compatibility)  
**Components:** [`WebSqlEndpoints.cs`](file:///root/autheris/src/Autheris.Api/Endpoints/WebSqlEndpoints.cs), [`WebSqlStatementManager.cs`](file:///root/autheris/src/Autheris.Application/Sql/Services/WebSqlStatementManager.cs), [`GovernedSqlExecutionService.cs`](file:///root/autheris/src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs), [`TrinoSqlEngine`](file:///root/autheris/src/TrinoSqlEngine)

---

## 1. Overview & Problem Statement

Direct database connections over proprietary ports (1433 for MSSQL, 5432 for Postgres) require broad firewall access and expose databases to uncontrolled connection exhaustion. F-DATA-02 introduces a governed HTTP-based WebSQL engine (`POST /api/v1/sql`, `POST /api/sql`, and `POST /v1/statement`) fully compatible with the Trino/Presto query language and REST client protocol.

Ad-hoc SQL queries are parsed using an ANTLR4 parser, checked against AST security rules, injected with Casbin ABAC and tenant RLS filters, rewritten for the specific target dialect (PostgreSQL, SQL Server, SQLite), and executed safely over standard HTTPS.

---

## 2. Business Value & Trino Ecosystem Compatibility

- **Zero Exposed Database Ports**: Operational databases remain isolated within private subnets; all analytics traffic passes through standard HTTPS (port 443).
- **100% Trino Protocol & Client Compatibility**: Works out-of-the-box with standard Trino clients (Trino CLI, Python `trino-python-client`, DBeaver Trino driver, Apache Superset) via `POST /v1/statement`.
- **Synchronous & Asynchronous Long-Polling (`wait_timeout`)**:
  - **Synchronous Fast-Path**: When queries finish within `X-Trino-Wait-Timeout` (or `?wait_timeout=...`), the server responds immediately with HTTP 200 and status `FINISHED` including all rows.
  - **Asynchronous Continuation**: Queries exceeding the timeout respond immediately with status `RUNNING` and a continuation `nextUri` (`GET /v1/statement/queued/{id}`), preventing gateway/proxy timeouts on long-running queries.
  - **Cancellation**: Running queries can be aborted via `DELETE /v1/statement/{id}`.
- **Canonical 3-Part Table Names (`<catalog>.<schema>.<table>`)**:
  - Queries can reference tables as `finance.dbo.invoices` or `schema.table`.
  - The `catalog` part selects or validates against the target data source.
  - The rewriter strips the catalog prefix when generating backend SQL (`"dbo"."invoices"` for PostgreSQL, `[dbo].[invoices]` for MSSQL, `[invoices]` for SQLite), preventing cross-database collision errors.
- **Deep AST SQL Injection Protection**: Prevents multi-statement attacks, system function execution, and comment-based evasion.
- **Controlled DML Guardrails**: High-risk statements (`UPDATE`, `DELETE`) require special writer roles, are bounded by max affected row limits, and are logged to the cryptographic WORM audit chain.

---

## 3. Architecture & Capabilities

```
┌────────────────────────────────────────────────────────┐
│ Client (Trino-CLI / DBeaver / REST / Python)           │
│ Query: SELECT * FROM finance.dbo.invoices LIMIT 10     │
│ Headers:                                               │
│   X-Trino-Catalog: finance                             │
│   X-Trino-Wait-Timeout: 5s                             │
└──────────────────────────┬─────────────────────────────┘
                           │
                           ▼
┌────────────────────────────────────────────────────────┐
│ Autheris WebSQL & Trino Protocol Engine                │
│ 1. Trino Headers & Body / Text / JSON parsed           │
│ 2. Catalog mapped & validated (Auto-Inference)         │
│ 3. Statement Manager (wait_timeout sync/async path)    │
│ 4. RLS & Column Masking AST injection                  │
│ 5. Target Dialect Catalog-Stripping                    │
└──────────────────────────┬─────────────────────────────┘
                           │
                           ▼
┌────────────────────────────────────────────────────────┐
│ Target DB (PostgreSQL / MSSQL / SQLite)                │
└────────────────────────────────────────────────────────┘
```

- **ANTLR4 AST Parsing**: Strict Trino syntax validation and rewriting.
- **Dynamic Policy Injection**: RLS clauses (`WHERE ... AND tenant_id = '...'`) and column masking applied deterministically per tenant and user identity.
- **Zero-Trust Multi-Tenancy**: Statement continuation via statement ID validates that calling user and tenant match the query creator (fail-closed).
- **Protection Against Cross-Catalog Queries**: Queries spanning multiple different catalogs/data sources are rejected to enforce isolation boundaries.

---

## 4. Usage Examples

### 4.1 Native Trino Protocol (`POST /v1/statement`)

```bash
# Execute query using Trino CLI or curl with synchronous wait_timeout:
curl -X POST http://localhost:8080/v1/statement \
  -H "Authorization: Bearer <user-token>" \
  -H "X-Tenant-Id: tenant-123" \
  -H "X-Trino-Wait-Timeout: 5s" \
  -H "X-Trino-Catalog: sales" \
  -H "Content-Type: text/plain" \
  -d "SELECT order_id, customer_id, total_amount FROM sales.dbo.orders WHERE order_date >= '2026-01-01' LIMIT 10"

# Synchronous Response (completed within 5s):
# {
#   "id": "20261008_080000_00001_abc123",
#   "infoUri": "/ui/query.html?20261008_080000_00001_abc123",
#   "stats": {
#     "state": "FINISHED",
#     "elapsedTimeMillis": 45
#   },
#   "columns": [
#     { "name": "order_id", "type": "varchar" },
#     { "name": "customer_id", "type": "varchar" },
#     { "name": "total_amount", "type": "varchar" }
#   ],
#   "data": [
#     ["ORD-1001", "CUST-42", 1450.00],
#     ["ORD-1002", "CUST-88", 980.50]
#   ]
# }
```

### 4.2 Asynchronous Continuation (`wait_timeout` Exceeded)

```bash
# Initial request with short timeout (e.g. 50ms):
# Response (HTTP 200 OK):
# {
#   "id": "20261008_080000_00002_def456",
#   "nextUri": "/v1/statement/queued/20261008_080000_00002_def456",
#   "stats": { "state": "RUNNING", "elapsedTimeMillis": 50 }
# }

# Poll continuation URI:
curl -X GET http://localhost:8080/v1/statement/queued/20261008_080000_00002_def456 \
  -H "Authorization: Bearer <user-token>" \
  -H "X-Tenant-Id: tenant-123" \
  -H "X-Trino-Wait-Timeout: 5s"

# Cancel running statement:
curl -X DELETE http://localhost:8080/v1/statement/20261008_080000_00002_def456 \
  -H "Authorization: Bearer <user-token>" \
  -H "X-Tenant-Id: tenant-123"
# Response: HTTP 204 No Content
```

### 4.3 Standard WebSQL JSON Endpoint (`POST /api/v1/sql`)

```bash
curl -X POST http://localhost:8080/api/v1/sql \
  -H "Authorization: Bearer <user-token>" \
  -H "Content-Type: application/json" \
  -d '{
    "sql": "SELECT order_id, total_amount FROM sales.dbo.orders LIMIT 10"
  }'

# Response:
# {
#   "columns": ["order_id", "total_amount"],
#   "rows": [
#     { "order_id": "ORD-1001", "total_amount": 1450.00 }
#   ],
#   "rowCount": 1
# }
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "WebSql": {
      "Enabled": true,
      "DefaultDataSourceName": "default",
      "AllowedDataSources": ["sales", "finance", "analytics"],
      "MaxResultRows": 5000,
      "AllowDml": false,
      "DmlWriterRoles": ["DatabaseOperator"],
      "MaxAffectedRows": 1000,
      "RejectUnfilteredDml": true,
      "ExecutionTimeoutSeconds": 30
    }
  }
}
```

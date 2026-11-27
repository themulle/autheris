# F-DATA-02: Governed WebSQL Engine & Trino REST Protocol

**Components:** [`WebSqlEndpoints.cs`](../../src/Autheris.Api/Endpoints/WebSqlEndpoints.cs), [`WebSqlStatementManager.cs`](../../src/Autheris.Application/Sql/Services/WebSqlStatementManager.cs), [`GovernedSqlExecutionService.cs`](../../src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs), [`TrinoSqlEngine`](../../src/TrinoSqlEngine)

---

## 1. Overview & Problem Statement

Direct database connections over proprietary ports (1433 for MSSQL, 5432 for Postgres) require broad firewall access and expose databases to uncontrolled connection exhaustion. F-DATA-02 introduces a governed HTTP-based WebSQL engine (`POST /api/v1/sql`, `POST /api/sql`, and `POST /v1/statement`) fully compatible with the Trino/Presto query language and REST client protocol.

Ad-hoc SQL queries are compiled by the governed AST compiler ([F-DIALECT-02](f-dialect-02-ast-sql-compiler.md)): parsed with an ANTLR4 parser, checked against AST security rules, secured with typed consent and tenant row filters and column masks, emitted for the target dialect (PostgreSQL, SQL Server, SQLite, Oracle) with every value bound as a parameter, and executed over standard HTTPS.

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
  - The compiler strips the catalog prefix when generating backend SQL (`"dbo"."invoices"` for PostgreSQL, `[dbo].[invoices]` for MSSQL, `[invoices]` for SQLite), preventing cross-database collision errors.
- **Deep AST SQL Injection Protection**: Prevents multi-statement attacks, system function execution, and comment-based evasion.
- **Granular Transport Row Limits & Truncation Accuracy**: Configurable row limits per egress transport (`RowLimits:Trino`, `RowLimits:Parquet`, `RowLimits:ArrowExport`) with accurate truncation reporting (only reported as truncated when rows were actually cut by the limit).
- **Cross-Channel Virtual Filters Pushdown**: Seamlessly integrates relation-scoped Virtual Filters ([`F-GOV-09`](f-gov-09-virtual-filters.md)) into the AST compiler along with data-owner consents.
- **Controlled DML Guardrails**: High-risk statements (`INSERT`, `UPDATE`, `DELETE`) require explicit approved write grants (`WriteRules` with operation and column scope), are bounded by max affected row limits and write budgets, and are logged to the cryptographic WORM audit chain.

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
- **Cross-Catalog Queries Only Through the Federated Path**: With `Gateway:WebSql:CrossSource:Enabled = false` (default), a query that spans several catalogs/data sources, or reads a non-SQL table, is rejected (`Cross-catalog queries across multiple data sources are not supported in WebSQL.`). With `Enabled = true` and an allowed transport, such a query is routed to the federated path described below instead of the single-source pushdown.

### Federated Query Transport Opt-In (`WebSql:CrossSource:AllowedTransports`)

Federated queries that span several data sources are only available when `Gateway:WebSql:CrossSource:Enabled` is true, and only for transports that are explicitly listed in `Gateway:WebSql:CrossSource:AllowedTransports`. Valid names are the constants of `SqlTransports`: `WebSql`, `Trino`, `Rest`, `Mcp`, `ArrowExport`, `SqlEndpoints` and `AsyncJob`. When nothing is configured, only `WebSql` and `Trino` are allowed. Every other channel is rejected on federated queries (generic "not supported" response) unless its name is listed. WebSQL itself is part of the default list; other channels that run federated queries (`Rest`, `Mcp`, `ArrowExport`, `SqlEndpoints`, `AsyncJob`) must be opted in. An explicitly empty list allows no transport at all (fail-closed). A configured list replaces the default; it is not merged with it.

#### How federated queries read their sources

The federated path stages every referenced table into an embedded DuckDB and runs the query there:

- Each table is read through the governed reader of its source with the caller's row filter, masking and the arguments of mandatory Virtual Filters. **The predicates of the query itself (`WHERE`, `ON`) are not pushed to the sources**; they are evaluated in DuckDB after staging.
- A table is therefore staged with all rows the caller may see, regardless of how selective the query is. Staging is bounded per query by `Gateway:WebSql:CrossSource`: `MaxTableCount` (default 5 tables), `MaxStagedRowsPerTable` (50,000), `MaxTotalStagedRows` (200,000), `MaxStagedBytesPerTable` (32 MiB) and `MaxTotalStagedBytes` (128 MiB). A table above `MaxStagedRowsPerTable` fails the query with `ROW_LIMIT_EXCEEDED`, the total row limit with `TOTAL_ROW_LIMIT_EXCEEDED`; results are never truncated silently.
- Joins, aggregation and functions run with DuckDB semantics (for example case-sensitive string comparison), not with the collation of the source.
- Timestamps are written as ISO 8601. A zone-less `timestamp` value (for example the result of `convert_timezone`, which is a wall-clock time in the target zone) is written without a `Z` suffix, for example `2023-06-01T10:30:00`; only values that are instants (`timestamp with time zone`, UTC) carry `Z`. Clients must not read a value without `Z` as UTC.

```json
{
  "Gateway": {
    "WebSql": {
      "CrossSource": {
        "Enabled": true,
        "AllowedTransports": [ "WebSql", "Trino", "Rest" ]
      }
    }
  }
}
```


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

The example is a stored statement with a list parameter, run with values (demo data set). The statement text stays fixed; only `parameters` change between runs.

```bash
curl -X POST http://localhost:8080/api/v1/sql \
  -H "Authorization: Bearer <user-token>" \
  -H "Content-Type: application/json" \
  -d '{
    "sql": "SELECT id, status, amount, created_at FROM finance.public.invoices WHERE status IN (@statuses) AND amount >= @minAmount ORDER BY id",
    "parameters": { "statuses": ["PAID", "PENDING"], "minAmount": 100 },
    "dataSource": "finance",
    "page": { "size": 50 }
  }'
```

The answer (values are illustrative, `next` is an opaque string):

```json
{
  "columns": [
    { "name": "id", "type": "integer", "nullable": false, "masked": false },
    { "name": "status", "type": "varchar", "nullable": true, "masked": false },
    { "name": "amount", "type": "decimal(12,2)", "nullable": true, "masked": false },
    { "name": "created_at", "type": "timestamp(3) with time zone", "nullable": true, "masked": false }
  ],
  "rows": [
    { "id": 1, "status": "PENDING", "amount": 147.42, "created_at": "2026-03-01T12:00:00Z" },
    { "id": 2, "status": "PAID", "amount": 284.84, "created_at": "2026-03-01T12:05:00Z" }
  ],
  "rowCount": 50,
  "truncated": true,
  "page": { "number": 1, "offset": 0, "size": 50 },
  "next": "<opaque token>"
}
```

For the next page send the same body with `"next": "<value of next>"` instead of `page`.

---

### 4.4 Execution Timeouts

A statement that exceeds `WebSql:ExecutionTimeoutSeconds` is answered with `504 Gateway Timeout` as RFC 9457 problem details (`Content-Type: application/problem+json`):

```json
{
  "type": "urn:autheris:problem:websql-execution-timeout",
  "title": "Gateway Timeout",
  "status": 504,
  "detail": "The SQL statement exceeded the execution time limit. Narrow the query (filter, fewer columns, LIMIT) or contact support with the trace id.",
  "code": "SQL_TIMEOUT",
  "traceId": "0HN..."
}
```

The body is written explicitly and never contains the SQL text, an `instance` with the query, the exception type or a stack trace. `code` is the stable error code.

WebSQL adds no ordering of its own. A user `ORDER BY` on a column without an index sorts the whole table in the source and can hit the timeout on large tables; this is by design. Remedy: add an index on the ordering column in the source, narrow the query, or raise `WebSql:ExecutionTimeoutSeconds`.

### 4.5 WebSQL Reference (`POST /api/v1/sql`)

The same content is served to agents in the query guide (`autheris://api/docs/query-guide`, shown only while `Gateway:WebSql:Enabled` is `true`). The OpenAPI document is `GET /api/v1/sql/openapi.json`; it requires authentication and answers like an unknown route while WebSQL is disabled.

#### 4.5.1 Entry points

- HTTP: `POST /api/v1/sql` with a JSON body `{ "sql", "parameters", "dataSource", "page", "next", "resultCache" }`, or the statement alone as `text/plain` or `application/sql`.
- MCP: `query_sql` takes `query` and optional `limit` and `cursor`; it takes no `parameters`. While `hasMore` is true, call it again with the returned `cursor`. `list_datasets` returns each table's `sqlName` (`<catalog>.<schema>.<table>`).

#### 4.5.2 Dialect and names

- Trino grammar, one statement per request. Only `SELECT` is accepted unless `Gateway:WebSql:AllowDml` is enabled; DML is then governed like reads.
- Use `LIMIT n`, not `TOP n`. Name tables `<dataSource>.<schema>.<table>`, or `<schema>.<table>` in the request's `dataSource` (or the default data source). Physical database- or server-qualified names are refused (`INVALID_IDENTIFIER_QUALIFICATION`).

#### 4.5.3 Parameters and lists

- Write `@name` in the statement and pass the value in `parameters` under `name` or `@name`. Names ignore case; two spellings of one name are refused (400 `BAD_REQUEST`). Inside a string literal or a quoted identifier, `@name` is text.
- Values are string, number, boolean or null; an object is refused (400). Every `@name` in the statement needs a value.
- A JSON array is a list parameter. It is allowed only as the single item of `IN (@ids)` or `NOT IN (@ids)` (`SQL_LIST_PARAM_POSITION`). All elements have one type: string, number or boolean (`SQL_LIST_PARAM_TYPE`). No element is null (`SQL_LIST_PARAM_NULL`). At most `MaxListParameterValues` distinct values (1000 by default, fewer on some data sources) and `MaxListParameters` lists (8) per request (`SQL_LIST_PARAM_TOO_LARGE`).
- Parameter names starting with `__` or with another gateway-internal prefix are reserved and refused (403 `SQL_POLICY_VIOLATION`).
- `LIMIT`, `OFFSET` and `FETCH` take integer literals only; a parameter in their place is not supported. Use `page.size` to choose the rows per call.

#### 4.5.4 Paging (`page`, `next`)

- Request a page with `"page": { "size": n }` (1 to `MaxAllowedRows`, else 400 `BAD_REQUEST`). The answer carries `page` (`number`, `offset`, `size`) and, while more rows exist, `next`.
- For the following page send the same `sql`, `parameters` and `dataSource` with `"next": "<token>"` (at most 512 characters). `page` may be left out; if sent, its `size` must match. The token is signed and bound to the statement, the parameter values, the caller, the tenant, the data source and the transport, and it expires after `Paging:TokenLifetimeMinutes` (15). Otherwise: 400 `SQL_PAGING_TOKEN_INVALID`.
- A paged statement needs a top-level `ORDER BY` and no `LIMIT` or `OFFSET` of its own. End the `ORDER BY` with a unique column (for example the primary key); the gateway adds no ordering.
- `pagingUnavailable` says why there is no `next`: `order_by_required`, `statement_has_limit` or `federated` (the rows come back unpaged under the normal row limit); `offset_limit` (the next offset would exceed `Paging:MaxOffset`, 10000); `caller_not_bindable` (the caller has no stable identity).
- Paging is refused together with `wait_timeout` and for statements other than `SELECT` (400 `SQL_PAGING_UNSUPPORTED`).

#### 4.5.5 Refusals

- Masked columns come back masked (`columns[].masked: true`). They cannot be used in `WHERE`, `JOIN ... ON` or `ORDER BY` (403 `FILTER_ON_MASKED_COLUMN`), nor grouped or aggregated (403 `AGGREGATION_ON_MASKED_COLUMN`).
- Only functions the gateway permits for the data source's dialect are accepted; any other function is refused (403 `SQL_POLICY_VIOLATION`).
- Comments (`--` and `/* */`) are accepted and never sent to the data source; a nested block comment is a syntax error. Optimizer hints are not passed on.

#### 4.5.6 Time stamps and time zones

- Date-time values are written in ISO 8601 with a `Z` suffix, for example `2026-03-01T12:00:00Z`. Values of `timestamp with time zone` columns are converted to UTC; values without a zone are written as stored, also with `Z`.
- `convert_timezone(from_zone, to_zone, ts)` and `convert_timezone(to_zone, zoned_ts)` return the wall-clock time in `to_zone`. Zones are IANA names (`Europe/Berlin`), given as string literals or scalar parameters. The result is a `timestamp` without zone, so it is also written with `Z`, although it is local time in `to_zone`. At most 8 calls per statement. Errors: `SQL_TIMEZONE_INVALID` (400), `SQL_TIMEZONE_ARGUMENT` (422), `SQL_FUNCTION_UNSUPPORTED_DIALECT` (422).

#### 4.5.7 Limits and response fields

- Statements up to `MaxQueryLength` characters (64000 by default); request bodies up to 2 MB. Execution stops after `ExecutionTimeoutSeconds` (30 by default; 504 `SQL_TIMEOUT`).
- Without `LIMIT` at most `DefaultMaxRows` rows (1000 by default); a `LIMIT` above `MaxAllowedRows` (10000 by default) is capped. `truncated: true` (and the header `X-Autheris-Truncated: true`) means that more rows existed.
- Response: `columns` (`name`, `type` as a Trino type name, `nullable`, `masked`), `rows` (objects by column name; arrays in column order with `?format=arrays`), `rowCount`, `truncated`, and when present `page`, `next`, `pagingUnavailable`, `resultCache` and `indexHints`. `indexHints` lists tables read without a usable index (`reason: NO_USABLE_INDEX`) and the columns an index would help with.

#### 4.5.8 Errors

Errors are RFC 9457 problem details (`Content-Type: application/problem+json`) with the members `type` (`urn:autheris:error:<CODE>`), `title`, `status`, `detail`, `code`, `traceId`, `correlationId` and `instance`; some also carry the optional members `line`, `column`, `limit`, `hint` or `retryAfterSeconds`. Branch on `code`, never on `detail`.

```json
{
  "type": "urn:autheris:error:SQL_PAGING_TOKEN_INVALID",
  "title": "Bad Request",
  "status": 400,
  "detail": "The paging token is not valid for this request.",
  "code": "SQL_PAGING_TOKEN_INVALID",
  "traceId": "0HN..."
}
```

| Status | Codes |
|---|---|
| 400 | `SQL_SYNTAX_ERROR`, `SQL_PARAMETER_MISSING`, `SQL_LIST_PARAM_*`, `SQL_PAGING_TOKEN_INVALID`, `SQL_PAGING_UNSUPPORTED`, `BAD_REQUEST` |
| 403 | `SQL_POLICY_DENIED`, `SQL_POLICY_VIOLATION`, `FILTER_ON_MASKED_COLUMN`, `AGGREGATION_ON_MASKED_COLUMN` |
| 422 | `SQL_CONSTRUCT_NOT_SUPPORTED`, `SQL_LIMIT_EXCEEDED`, `SQL_TIMEZONE_ARGUMENT` |
| 429 | `RATE_LIMIT_EXCEEDED`, `QUERY_COST_QUOTA_EXCEEDED` (with `Retry-After`) |
| 504 | `SQL_TIMEOUT` |

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "WebSql": {
      "Enabled": true,
      "DefaultDataSourceName": "default",
      "DefaultMaxRows": 1000,
      "MaxAllowedRows": 10000,
      "MaxQueryLength": 64000,
      "MaxListParameterValues": 1000,
      "MaxListParameters": 8,
      "AllowDml": false,
      "MaxAffectedRows": 1000,
      "ExecutionTimeoutSeconds": 30,
      "Paging": { "MaxOffset": 10000, "TokenLifetimeMinutes": 15 }
    },
    "RowLimits": {
      "Trino": { "DefaultMaxRows": 5000, "MaxAllowedRows": 25000 },
      "Parquet": { "DefaultMaxRows": 50000, "MaxAllowedRows": 100000 },
      "SqlEndpoints": { "DefaultMaxRows": 5000, "MaxAllowedRows": 20000 },
      "ArrowExport": { "DefaultMaxRows": 25000, "MaxAllowedRows": 100000 }
    }
  }
}
```

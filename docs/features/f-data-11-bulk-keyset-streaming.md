# F-DATA-11: Governed Bulk Keyset Streaming & Two-Phase Work Settlement

## 1. Executive Summary & Problem Statement

Autonomous AI agents, ETL synchronization pipelines, and batch analytics consumers operating with read-only tokens (`TokenAccessScope.ReadOnlyScopes`) require continuous, high-throughput extraction of large governed tables. Historically, extraction options in Autheris faced severe architectural bottlenecks:
1. **$O(N^2)$ OFFSET Degradation:** Standard REST Data API pagination relied on SQL `OFFSET` clauses, forcing database engines to scan and discard millions of preceding rows on deep pages, leading to exponential latency degradation and database connection pool starvation.
2. **Buffer Exhaustion & LOH Fragmentation:** Exporting large datasets via Apache Arrow or REST endpoints buffered entire result sets in memory, risking Large Object Heap (LOH) fragmentation, garbage collection pauses, and Out-Of-Memory (OOM) crashes under high concurrency.
3. **GraphQL Complexity and Row Caps:** GraphQL queries are bounded by `MaxAllowedComplexity` and `MaxResponseRows` (default: 5000), making bulk table extraction impossible through GraphQL.
4. **Multiplicity Blindness & One-Way Token Burn:** Pre-flight query cost estimation treated all nested relations as 10x fan-out collections, overcharging queries by 10x-100x for 1:1 lookups. Furthermore, rate limiters debited tokens upfront without refunding compute when queries returned fewer rows than estimated.

`F-DATA-11` resolves these limitations with an enterprise-grade streaming and quota settlement engine:
- **Governed Keyset Streaming Endpoint:** Exposes `POST /api/v1/data/{domain}/{schema}/{table}/stream` (with alias `/bulk`), strictly scoped to single tables and accessible with read-only scopes in [`ReadOnlyTokenMiddleware`](file:///root/autheris/src/Autheris.Api/Middleware/ReadOnlyTokenMiddleware.cs).
- **Deterministic Keyset Cursor Seeking:** Replaces `OFFSET` pagination with monotonic keyset cursor seek predicates (`WHERE pk > @cursor_pk`) derived from catalog primary key metadata ([`TableMetadata.PrimaryKeyColumns`](file:///root/autheris/src/Autheris.Domain/Model/TableModels.cs)), guaranteeing constant $O(1)$ seek performance across arbitrary table depths.
- **Tamper-Evident HMAC Cursors:** Generates opaque, URL-safe base64 cursors signed with HMAC-SHA256 ([`KeysetCursorEncoder`](file:///root/autheris/src/Autheris.Application/Data/Services/KeysetCursorEncoder.cs)) to prevent client tampering, injection, or cross-tenant replay.
- **True Chunked Streaming (Zero LOH):** Emits records row-by-row directly from the ADO.NET `DbDataReader` into ASP.NET Core `PipeWriter` / `Utf8JsonWriter` (NDJSON) or Apache Arrow IPC streams (`Transfer-Encoding: chunked`), keeping memory allocation bounded and constant regardless of stream length.
- **Multiplicity-Aware Cost Estimation:** Recognizes relation cardinality (1:1 and N:1 single-object relations) in [`QueryCostAnalyzerRule`](file:///root/autheris/src/Autheris.GraphQL/Interceptors/QueryCostAnalyzerRule.cs) to avoid artificial 10x multiplier compounding.
- **Two-Phase Token Bucket Work Settlement:** Implements atomic `ReserveCostQuotaAsync` (admission check) and `SettleCostQuotaAsync` (post-stream reconciliation) in [`IRateLimiterService`](file:///root/autheris/src/Autheris.Application/Interfaces/IRateLimiterService.cs), refunding unused quota or debiting additional compute based on actual rows delivered.

---

## 2. Architecture & Component Wiring

```mermaid
flowchart TD
    subgraph ClientLayer ["Client & Ingress Surface"]
        Client["AI Agent / ETL Consumer<br/>(Read-Only Token)"]
    end

    subgraph SecurityGateway ["Security & Admission Interceptors"]
        RoMiddleware["ReadOnlyTokenMiddleware<br/>(Permits /stream & /bulk Routes)"]
        RateLimiter["IRateLimiterService<br/>(Phase 1: ReserveCostQuotaAsync)"]
        RequiredFilters["GovernedTableReadPlanner<br/>(Required Filter Columns Validator)"]
    end

    subgraph StreamingPipeline ["Streaming Execution Pipeline"]
        Endpoint["TableStreamEndpoints<br/>(POST /api/v1/data/.../stream)"]
        CursorEncoder["KeysetCursorEncoder<br/>(HMAC-SHA256 Cursor Validation & Minting)"]
        Planner["GovernedTableReadPlanner.Keyset<br/>(Keyset Predicate AST Compiler)"]
        SqlExecutor["GovernedSqlExecutionService<br/>(ADO.NET DbDataReader Streaming)"]
        MaskingEngine["IColumnMaskingProvider<br/>(In-Stream Dynamic Masking)"]
    end

    subgraph WorkSettlement ["FinOps & Post-Stream Settlement"]
        Settler["RateLimiter.SettleCostQuotaAsync<br/>(Phase 2: Actual Row Compute Reconciliation)"]
        AuditSink["IAuditWriter<br/>(Emits TABLE_QUERY with Actual Row Metrics)"]
    end

    Client -->|POST /stream (Request + Cursor)| RoMiddleware
    RoMiddleware --> RateLimiter
    RateLimiter -->|Quota Reserved| Endpoint
    Endpoint --> RequiredFilters
    Endpoint --> CursorEncoder
    Endpoint --> Planner
    Planner --> SqlExecutor

    SqlExecutor -->|Streaming Rowset| MaskingEngine
    MaskingEngine -->|NDJSON / Arrow IPC Chunks| Client

    Endpoint -->|Reconcile Actual Rows Emitted| Settler
    Endpoint -->|Write Completion Audit| AuditSink
```

### 2.1 Component Responsibilities

| Component | Responsibility |
|---|---|
| [`TableStreamEndpoints`](file:///root/autheris/src/Autheris.Api/Endpoints/TableStreamEndpoints.cs) | Maps `/api/v1/data/{domain}/{schema}/{table}/stream` and `/bulk`; coordinates admission, streaming, cursor emission, and settlement. |
| [`ReadOnlyTokenMiddleware`](file:///root/autheris/src/Autheris.Api/Middleware/ReadOnlyTokenMiddleware.cs) | Whitelists stream endpoints for read-only tokens (`Agent.Read`, `App.Read`). |
| [`KeysetCursorEncoder`](file:///root/autheris/src/Autheris.Application/Data/Services/KeysetCursorEncoder.cs) | Encodes and decodes keyset pagination cursors with HMAC-SHA256 integrity verification, tenant bounding, and direction flags. |
| [`GovernedTableReadPlanner`](file:///root/autheris/src/Autheris.Application/Data/Services/GovernedTableReadPlanner.Keyset.cs) | Compiles table queries with keyset seek predicates (`WHERE pk > @cursor`), caller RLS filters, and column projections. |
| [`QueryCostAnalyzerRule`](file:///root/autheris/src/Autheris.GraphQL/Interceptors/QueryCostAnalyzerRule.cs) | Evaluates static query cost with cardinality awareness (multiplier 1 for 1:1/N:1 joins). |
| [`IRateLimiterService`](file:///root/autheris/src/Autheris.Application/Interfaces/IRateLimiterService.cs) | Implements two-phase token bucket accounting (`ReserveCostQuotaAsync` and `SettleCostQuotaAsync`) backed by Redis or in-memory stores. |

---

## 3. Technical Deep-Dive

### 3.1 Keyset Pagination vs. OFFSET

Traditional pagination (`SELECT * FROM table ORDER BY id OFFSET 1000000 ROWS FETCH NEXT 100 ROWS ONLY`) forces the storage engine to walk 1,000,000 index nodes and discard them. Keyset pagination converts the pagination cursor into a direct index seek:

```sql
SELECT column1, column2, id
FROM [sales].[dbo].[orders]
WHERE id > @cursor_id AND tenant_id = @tenant_id
ORDER BY id ASC
FETCH NEXT 100 ROWS ONLY;
```

For composite primary keys `(tenant_id, region, sequence_id)`, `GovernedTableReadPlanner` generates hierarchical tuple comparison predicates:
```sql
WHERE (region > @c_region) OR (region = @c_region AND sequence_id > @c_seq)
```

### 3.2 Keyset Cursor Integrity & Architecture

A keyset cursor is structured as:
```json
{
  "t": "sales.public.orders",
  "k": { "id": 104523 },
  "d": "asc",
  "u": 1729302400
}
```
The payload is serialized to compact JSON, signed with `HMAC-SHA256` using the gateway cluster key, and URL-safe base64 encoded. Attempting to alter cursor values, alter sort direction, or replay a cursor across different tables fails closed with HTTP 400 `INVALID_CURSOR`.

### 3.3 Two-Phase Token Bucket Work Settlement (Issue #177)

To prevent denial-of-service via massive query spikes while maintaining fair billing:
1. **Phase 1: Reserve:** Before query or stream execution begins, `CostAndQuotaMiddleware` or `TableStreamEndpoints` estimates maximum expected cost:
   $$\text{ReservedCost} = \text{BaseCost} + (\text{BatchLimit} \times \text{RowFactor})$$
   The reserve cost is debited via `ReserveCostQuotaAsync`. If client bucket tokens fall below the reserve, the request is rejected immediately with HTTP 429 `QUOTA_EXCEEDED`.
2. **Streaming Execution:** The query streams records to the client. If client disconnects or cancellation occurs after 45 rows out of 1,000, streaming stops immediately.
3. **Phase 2: Settle:** Post-execution, `SettleCostQuotaAsync` reconciles the account based on actual work:
   $$\text{ActualCost} = \text{BaseCost} + (\text{ActualRowsEmitted} \times \text{RowFactor})$$
   $$\text{Adjustment} = \text{ReservedCost} - \text{ActualCost}$$
   If $\text{Adjustment} > 0$, unused tokens are refunded to the client's token bucket. Capped at maximum bucket capacity ($\text{Tokens} \le \text{Capacity}$) to eliminate credit minting vulnerabilities.

---

## 4. Configuration & Options

Configure streaming limits and query cost parameters under `Gateway:DataApi` and `Gateway:Governance:QueryCost` in `appsettings.json`:

```json
{
  "Gateway": {
    "DataApi": {
      "Streaming": {
        "Enabled": true,
        "DefaultBatchLimit": 1000,
        "MaxBatchLimit": 10000,
        "MaxCursorTtlMinutes": 60,
        "AllowedFormats": [ "ndjson", "arrow" ]
      }
    },
    "Governance": {
      "QueryCost": {
        "BaseCost": 5.0,
        "CostPerRow": 0.05,
        "CostPerJoinDepth": 2.0,
        "DefaultListMultiplier": 10,
        "SingleObjectMultiplier": 1
      }
    }
  }
}
```

---

## 5. Usage Examples

### 5.1 Initial Bulk Stream Request (NDJSON)

```http
POST /api/v1/data/sales/public/orders/stream HTTP/1.1
Host: autheris.corp:443
Authorization: Bearer <token>
Content-Type: application/json

{
  "columns": ["order_id", "customer_id", "total_amount", "created_date"],
  "limit": 500,
  "format": "ndjson"
}
```

**Response (HTTP 200 Chunked NDJSON):**
```http
HTTP/1.1 200 OK
Content-Type: application/x-ndjson
Transfer-Encoding: chunked
X-Stream-Rows-Returned: 500
X-Stream-Next-Cursor: eyJ0Ijoic2FsZXMucHVibGljLm9yZGVycyIsImsiOnsiaWQiOjUwMH0sInNpZyI6Ilg3eVI...

{"order_id":1,"customer_id":12,"total_amount":142.50,"created_date":"2026-01-01T10:00:00Z"}
{"order_id":2,"customer_id":88,"total_amount":59.00,"created_date":"2026-01-01T10:02:15Z"}
...
{"order_id":500,"customer_id":419,"total_amount":1200.00,"created_date":"2026-01-02T14:22:00Z"}
```

### 5.2 Resuming Stream with Cursor

```http
POST /api/v1/data/sales/public/orders/stream HTTP/1.1
Host: autheris.corp:443
Authorization: Bearer <token>
Content-Type: application/json

{
  "cursor": "eyJ0Ijoic2FsZXMucHVibGljLm9yZGVycyIsImsiOnsiaWQiOjUwMH0sInNpZyI6Ilg3eVI...",
  "limit": 500,
  "format": "ndjson"
}
```

---

## 6. Security, Zero-Trust & Fail-Closed Behavior

1. **Read-Only Scope Gating:** Whitelisted in `ReadOnlyTokenMiddleware` exclusively for single-table reads. Cross-table operations, DML mutations, or ad-hoc SQL executions remain strictly blocked.
2. **Cryptographic Cursor Sealing:** Cursors are signed with HMAC-SHA256 incorporating table identity, ordering keys, and tenant ID. Modifying cursors, injecting SQL syntax into keys, or replaying across tenants triggers immediate rejection.
3. **Mandatory Required Filter Enforcement:** If the target table defines `RequiredFilterColumns` (such as `tenant_id` or `created_date`), the stream request is verified by `GovernedTableReadPlanner`. Requests omitting required filter constraints are denied with `REQUIRED_FILTER_COLUMN_MISSING`.
4. **Dynamic In-Stream Masking:** Masking rules are applied row-by-row during streaming. Redacted, hashed, or partially masked values are transformed before bytes are written to the network pipe.
5. **Two-Phase Quota Anti-Exploit:** Refund adjustments in token bucket settlement can never raise client token balance above the bucket's maximum capacity, preventing negative-debit balance inflation.

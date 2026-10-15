# F-DATA-06: Governed REST Data API & System Virtual Tables

## 1. Overview & Business Value

The **Governed REST Data API** (`GET /api/v1/data/{domain}/{schema}/{table}`) provides a lightweight, performant, and fully governed RESTful query interface over heterogeneous enterprise datasets. It bridges the gap between complex GraphQL queries, analytical WebSQL commands, and citizen developer / REST-centric enterprise applications.

Crucially, all REST requests pass through the exact same Zero-Trust Policy Decision Point (PDP) as GraphQL and WebSQL:
- **Zero-Trust Consent Enforcement:** Access is validated against data-owner consents and ReBAC (Zanzibar) policies.
- **Pre-Storage Dynamic Data Masking:** Column masking (HMAC pseudonymization, partial masking, nullification) is applied natively before serializing results.
- **Row-Level Security (RLS) Pushdown:** SQL predicates are pushed directly to the underlying SQL engines or evaluated in-memory for HTTP/REST sources.
- **Protocol Symmetry:** System virtual tables (`governance.system.consents`, `governance.system.audit`, `governance.system.costs`) are accessible identically across REST Data API, WebSQL, GraphQL, and OData.

## 2. API Specifications

### 2.1 Table Query Endpoints

```http
GET /api/v1/data/{domain}/{schema}/{table}?limit=50&offset=0&select=id,name,status&filter=status eq 'ACTIVE'&orderBy=name
GET /api/v1/data/{domain}/{schema}/{table}/{id}
GET /api/v1/data/{datasetId}            (e.g. /api/v1/data/sales.dbo.customers)
Host: autheris.company.internal
Authorization: Bearer <jwt-or-api-key>
Accept: application/json
```

#### Query Parameters:
- `limit` (int, default 50): Number of rows to return (capped at 1000).
- `offset` (int, default 0): Number of rows to skip.
- `select` (string): Comma-separated list of projected columns.
- `filter` (string): Filter expression translated to AST predicates.
- `orderBy` (string): Ordering expression. Ordering on masked or denied columns is rejected.
- `Accept` Header: Supports `application/json`, `application/vnd.apache.parquet`, or `application/vnd.apache.arrow.stream`.

The response envelope contains `offset`, `limit`, `hasMore`, `columns` and `data`.

#### Pagination:
**Single-source SQL tables** push the page down to the database. The service emits the Trino form `OFFSET n ROWS LIMIT m` (with `m = limit + 1`; the extra row populates `hasMore`) through the governed compile path, and each dialect generator emits its native pagination (`LIMIT/OFFSET`, `OFFSET .. ROWS FETCH NEXT .. ROWS ONLY`, and so on). No rows are skipped in the gateway, so a deep page costs O(limit) in the gateway. Row-level security is part of the compiled WHERE clause, so the offset applies after it.
- `offset` must not exceed `Analytics:DatasetApi:MaxOffset` (default `100000`, valid range `0` to `10000000`, validated at startup).
- `limit + 1` must not exceed the effective cap, the smaller of `10000` and `WebSql.MaxAllowedRows` (when greater than 0).
- Default ordering without an explicit `orderBy` follows [F-GOV-21](f-gov-21-declared-table-keys-and-read-ordering.md#52-default-read-ordering): declared primary key, declared unique index, column `Meta` marker key, the `(client_id, ts)` convention, then all columns that are readable in clear text and have an orderable type, in catalog order. A key or index with any column that is not readable in clear text for the caller is skipped as a whole. An explicit `orderBy` gets the default ordering columns it does not name as ascending tiebreaker. When no orderable clear column exists, a positive `offset` is rejected with `400`, and a first page is rejected with the same `400` when more rows exist; the message names no column. Catalogs do not invent a key: a table has no primary key unless one is declared.

**Cross-source tables** (lakehouse, HTTP declarative and HTTP plugin sources, served through federation) cannot take a pushed-down offset. A request reads `offset + limit + 1` rows with a plain `LIMIT` and the first `offset` rows are skipped in the gateway. `offset + limit` is therefore bounded by the smallest of `10000`, `WebSql.MaxAllowedRows` (when greater than 0) and `DuckDbOlap.MaxResultRows`.

Violations of these bounds are answered with `400 Bad Request`. If the SQL layer truncates the result before the requested rows are complete, the request fails closed with `400` instead of returning a short page with `hasMore=false`.

#### Cross-Source Transport:
REST reads of federated sources use the transport name `Rest`. Federation is only permitted for transports listed in `WebSql.CrossSource.AllowedTransports` (default: `WebSql`, `Trino`); `Rest` must be added explicitly to enable REST federation. A request without a transport is rejected. Policy denials are returned as a generic `403` that does not disclose the federation policy or the table.

### 2.2 System Virtual Tables

Virtual system tables are mapped under `domain = "governance"`, providing metadata introspection:
- `governance.system.consents`: Active, expired, and revoked consent grants and delegations.
- `governance.system.audit`: Cryptographically chained HMAC-SHA256 audit entries.
- `governance.system.costs`: FinOps compute, storage, and token accounting metrics.

## 3. Architecture & Implementation

- **Service:** `GovernedDataQueryService` in `src/Autheris.Application/Data/Services/GovernedDataQueryService.cs`
- **Hosted Registry:** `VirtualSystemTablesHostedService` registers system tables into `ITableMetadataRepository` during application startup.
- **Endpoints:** Registered in `src/Autheris.Api/Endpoints/DatasetDataEndpoints.cs`.

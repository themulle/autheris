# PRD: REQ-FED-01 - Cross-Source Access That Works Cleanly for All Consumers

**Document ID:** `REQ-FED-01`
**Date:** 2026-10-16
**Status:** PRD READY (Phase 1 complete); next: Phase 2 architecture plan (`csharp-architect`)
**Author:** Enterprise Product Manager (Phase 1)
**Source:** Owner requirement 2026-10-16: "The cross-source feature must work cleanly for all consumers." (original German: "das cross source feature muss bei allen abnehmern sauber funktionieren"). Related issue: #75 "Cross-source GraphQL relations" (falsely recorded as completed).
**Branch:** `feat/cross-source-all-consumers` (base `main` `70b75e27`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Related feature docs (read-only references):** [F-GOV-19](../features/f-gov-19-cross-source-graphql-relations.md), [F-DATA-02](../features/f-data-02-governed-websql.md), [F-DATA-03](../features/f-data-03-duckdb-olap.md), [F-DATA-04](../features/f-data-04-arrow-flight-sql.md), [F-DATA-06](../features/f-data-06-governed-rest-data-api.md), [F-DATA-07](../features/f-data-07-async-query-jobs.md), [F-GOV-14](../features/f-gov-14-federated-virtual-filters.md), [F-GOV-21](../features/f-gov-21-declared-table-keys-and-read-ordering.md), [F-AI-12](../features/f-ai-12-hybrid-mcp-gateway.md)

**Binding project constraints:** no upgrade or repair migrations (schema changes go into the `0001` baseline DDL of SQLite, PostgreSQL and SQL Server in place, plus the schema-shape snapshots); Autheris is not in production, breaking changes are allowed; least privilege on sources (SELECT only); fail closed.

---

## 1. Problem Statement

### 1.1 What the owner asked for

Every consumer (front door) of the gateway must handle data that lives in more than one data source in a clean, governed and predictable way: either it works with the same semantics and governance as a single-source request, or it refuses with a stable, non-disclosing error. Today the situation is the opposite: the feature exists in two places that are not connected, and one of them is documented as done but is not wired.

### 1.2 Verified state (code at `70b75e27`)

1. **Federation exists for SQL-text channels only.** WebSQL, Trino, Arrow export, Flight SQL, MCP `query_sql` / `invoke_api`, declarative SQL endpoints and async jobs all end in `GovernedSqlExecutionService` -> `CrossSourceRoutingException` -> `FederatedDuckDbExecutionService` (router, planner, governed staging, DuckDB join). It is off by default (`WebSql:CrossSource:Enabled=false`, `src/Autheris.Domain/Options/Protocols/RestApiOptions.cs:66`) and gated by `AllowedTransports` (default `WebSql`, `Trino`, `:76-79`; enforced only at `FederatedDuckDbExecutionService.cs:245-252`).
2. **GraphQL relations across sources do not work.** `CatalogSchemaModel.cs:240-322` exposes every declared relation without comparing data sources; `GovernedTreeQueryService.cs:223-235` then rejects the query at runtime with `INVALID_QUERY` "Relations across data sources are not supported in one query." Non-SQL tables are not in the schema at all (`CatalogSchemaModel.cs:118`).
3. **`CrossSourceRelationResolver` (issue #75, F-GOV-19) is dead code in production.** It is registered (`DataApiServiceExtensions.cs:25-26`) but injected nowhere. Its only `ICrossSourceRowReader` is `InMemoryCrossSourceRowReader`, a seedable test stub that returns an empty set in production. `TableRelation.AllowCrossDataSource` (`src/Autheris.Domain/Model/TableModels.cs:293`) is never persisted or mapped. `CrossSourceRelationOptions` is never bound. Only unit tests exercise it. Its governance is also not the governed pipeline: it reads raw rows and re-applies row filters in the gateway with the legacy string evaluator (`CrossSourceRelationResolver.Helpers.cs:232-267`), and the tenant check is skipped when the row's tenant value is `NULL` (fail-open).
4. **A second, ungoverned-by-compiler cross-source path exists:** `POST /api/v1/olap/query` stages client-named tables and runs client-supplied DuckDB SQL (`DuckDbOlapEndpoints.cs`, `OlapStagingService.cs`). It has no `CrossSource.Enabled`, no transport gate, no masked-column join/filter guardrails, and no AST analysis of the client SQL.
5. **Dead or inconsistent configuration:** `CrossSourceOptions.MaxMemory`, `MaxTempDirectorySize` and `MaxParallelSourceReads` are not enforced (DuckDB uses `DuckDbOlap:*`; staging is a sequential loop, `FederatedStagingService.cs:130`); `AllowedTransports` names are not validated (a typo silently denies a transport); the keys are missing from `docs/configuration-guide.md`.

### 1.3 Qualification

Accepted as a **cross-cutting reliability and security feature (P1)**. It is not a new capability for one consumer; it is the completion of the cross-source contract across all front doors, plus correcting documentation that claims more than the code does. It is classified a security-relevant change because it adds a second source read to the request path of every consumer and because the existing OLAP path bypasses the compiler guardrails.

---

## 2. Consumer Inventory (verified in code)

Legend: **A** = can one request reach tables of different sources; **B** = can it read non-SQL tables; **C** = governance applied; **D** = what the caller gets today when cross-source is attempted. Verdict: OK = works as specified, PARTIAL = works with caveats or only opt-in, BROKEN = advertised or exposed but fails, N/A = single table by design (must fail clearly).

| # | Consumer (front door) | A: multi-source | B: non-SQL | C: governance | D: error today | Verdict |
|---|---|---|---|---|---|---|
| 1 | WebSQL `POST /api/v1/sql` (`WebSqlEndpoints.cs:44-51`, `WebSqlEndpoints.Execution.cs:122`) | Yes, only if `CrossSource.Enabled` and `WebSql` allowed, no `dataSource`/catalog conflict, no DML (`CrossSourceQueryRouter.cs:96-155`) | `HttpDeclarative` only; `HttpPlugin`, `LakehouseIceberg`, `LakehouseDelta` rejected (`CrossSourceQueryRouter.cs:112-115`) | Full: per-table decision (consent, Casbin, ReBAC, RLS, tenant), federated virtual filters, masking, masked-column join/filter guardrails, budgets (`MaxStagedRowsPerTable` 50k, total 200k, bytes 32/128 MiB, 30 s), audit per source read (`WebSqlCrossSourceSourceRead`). No k-anonymity | 403 `SQL_POLICY_VIOLATION` with one fixed message for disabled, transport denied, unsupported type, DML, cartesian, table count (cannot be told apart); `SQL_POLICY_DENIED` for table denial | PARTIAL (opt-in; indistinguishable errors; `request.Parameters` ignored; `truncated` always false; 50k cap instead of WebSQL max rows) |
| 2 | Trino `/v1/statement` (`WebSqlEndpoints.Execution.cs:122-148`, `WebSqlStatementManager.cs:163`) | Yes, same gate (`Trino` allowed by default); a client that always sends `X-Trino-Catalog` is rejected for cross-catalog (`CrossSourceQueryRouter.cs:146-155`) | as 1 | as 1 | HTTP 200 with `FAILED`, `SYNTAX_ERROR`, fixed message (`WebSqlStatementManager.cs:383-391`) | PARTIAL |
| 3 | REST data API `GET /api/v1/data/...` (`DatasetDataEndpoints.cs:38-55,184`) | No (one table) | `HttpDeclarative` only via federation, needs `CrossSource.Enabled` and `Rest` listed (`GovernedDataQueryService.cs:~139-205`); plugin/lakehouse fail at the router | Per-table PDP, then as 1; window cap `min(10000, WebSql.MaxAllowedRows, MaxResultRows)` | 403 `SQL_POLICY_VIOLATION` fixed message | PARTIAL (cross-source N/A, single HTTP table needs two switches) |
| 4 | OData (`ODataEndpoints.Query.cs:251-253`, `GatewayExecutionService.cs:~210-300`) | No; `$expand` -> 501 `NotImplemented` | All types, single table, via executors; `$filter`/`$orderby`/`$count` SQL only (501 otherwise); virtual filter on HTTP table denied (`InMemoryRowFilterEvaluator.cs:22-30`) | Per-table PDP, audit, in-memory RLS and masking for non-SQL | 501 | N/A (clear error) |
| 5 | GraphQL catalog schema relations (`CatalogSchemaModel.cs:118-134,240-322`, `GovernedTreeQueryService.cs:223-235`) | **No, but the schema advertises it** | **No** (SQL tables only in schema) | Per-table consent/ReBAC/virtual filter, tenant predicate, row filters, masks (`TreeSqlCompiler.cs:254-267,476-483`), budgets (`MaxResponseRows` 5000, `MaxAggregateRowBudget` 50000), concurrency lease, audit per table | `INVALID_QUERY` "Relations across data sources are not supported in one query." (after access resolution; denial first as `ACCESS_DENIED`) | **BROKEN** |
| 6 | GraphQL filters and sorting on relations | No relation fields in `<T>_filter` or `<T>_order_by` (`CatalogGraphQlTypeModule.cs:~190-205`, `GraphQlTreeBuilder.ParseOrderEntry`) | n/a | n/a | GraphQL validation error | N/A; F-GOV-19 examples (`where:{customer:{...}}`) are invalid |
| 7 | GraphQL `<table>_aggregate` (`CatalogGraphQlTypeModule.cs:272-278,436`) | No | No via schema | k-anonymity, DP noise, masked-aggregation guard, privacy budget | n/a | N/A |
| 8 | GraphQL `queryMetrics` (`QueryTypes.cs:689-768`, `SemanticMetricQueryService.cs:~136-145`) | No; metrics of different models -> `ArgumentException` | n/a | metric-scoped consents | `INVALID_METRIC_QUERY` (REST 400) | N/A |
| 9 | GraphQL subscriptions / CDC (`Subscription.cs:60-97`, `CdcSubscriptionGovernor.cs`) | No (one topic per table) | CDC sources only | admission decision, per-event RLS and masking, revocation watch, audit | `FORBIDDEN`, `AUTH_TENANT_MISMATCH` | N/A |
| 10 | Arrow export (`ArrowExportEndpoints.cs:190,263-296`) | Yes via `sql` if `ArrowExport` allowed | `HttpDeclarative` only | as 1 plus ReBAC viewer on every table; `RowLimits.ArrowExport` | 403; non-production leaks the raw policy text | PARTIAL |
| 11 | Arrow Flight SQL (HTTP, `ArrowFlightSqlServer.cs:95,198`, `FederatedDuckDbExecutionService.cs:200-232`) | Yes if `FlightSql` allowed | `HttpDeclarative` only | as 1 | `/info` succeeds without transport gate, ticket issued, `DoGet` then fails at the gate | PARTIAL (inconsistent gate) |
| 12 | Async query jobs (`AsyncQueryJobBackgroundWorker.cs:240-262`) | Yes if `AsyncJob` allowed (not default) | `HttpDeclarative` only | as 1; result files plaintext in scratch directory | Submit accepted (202), job ends `Failed` with fixed message; no submit-time validation | PARTIAL |
| 13 | MCP `query_sql`, `invoke_api` POST `/api/v1/statement` (`McpToolExecutionHandler.cs:~95-110`, `ApiDispatcherService.cs:~165-180`) | Yes if `Mcp` allowed | `HttpDeclarative` only | guardrail resolves all tables, Casbin ABAC, four-eyes/step-up, PII scrubbing, token budget, federation pipeline | tool result `isError`, `SQL_POLICY_VIOLATION` fixed message | PARTIAL |
| 14 | MCP `query_dataset`, `invoke_api` GET data (`McpToolExecutionHandler.cs:~120-137`) | No | `HttpDeclarative` via `GovernedDataQueryService` if `Mcp` allowed | as 3 | as 13 | PARTIAL (N/A cross-source) |
| 15 | MCP `describe_dataset` / `search_catalog` (`McpToolExecutionHandler.cs:~158-166`) | Metadata only; advertises `join_relations` with targets across domains | n/a | catalog visibility | n/a | PARTIAL (advertises joins that may not be executable) |
| 16 | Declarative SQL endpoints (`SqlEndpointExecutionService.cs:~76-89`, `SqlEndpointRoutes.cs:370-378`) | In theory (`SqlEndpoints` allowed) | `HttpDeclarative` only | as 1 | 403; parameterised endpoints fail closed because the federated path ignores `request.Parameters` | **BROKEN** for parameterised endpoints |
| 17 | DuckDB OLAP `POST /api/v1/olap/query` (`DuckDbOlapEndpoints.cs`, `OlapStagingService.cs:155-163`, `DuckDbOlapEngine.cs:405-470`) | **Yes, always on, no gate** | SQL only in practice (HTTP: "No active connector registered") | table-level decision + governed read; **no** masked-column join/filter guardrails (client SQL is not compiled), no `CrossSource.Enabled` | 403/404/400/429/503 per cause | **BROKEN (governance gap)**: bypasses the federation guardrails |
| 18 | Analytics aggregate REST (`AnalyticsEndpoints.cs:55-115`, `GovernedAggregationService.ValidationAndExecution.cs:309-390`) | No | All types via executors, aggregated in memory (100k rows); SQL pushdown branch may be unreachable in production DI (to verify) | PDP, k-anonymity, DP, budget, cost bounds, audit; CR-10 virtual-filter guard not called on this branch | `INVALID_AGGREGATION_QUERY`, 403, 429 | N/A for cross-source; **needs verification** (see 3.3) |
| 19 | Change detection `change-token`, `changesSince` (`ChangeDetectionEndpoints.cs`, `DataApiServiceExtensions.cs:27`) | No | No | PDP, PK columns clear, RLS, signed cursor | 403 `ChangeTrackingNotSupported` (in-memory version store in production DI) | N/A for cross-source; adjacent defect (see 3.3) |
| 20 | PostgreSQL wire protocol | Not implemented (`2026-10-16-req-postgres-wire-protocol-endpoint.md` PROPOSED; no `PgWire` transport constant) | n/a | n/a | n/a | N/A (must inherit this contract when built) |

**Reading the matrix.** Two classes of consumers exist. (i) **SQL-text consumers** (rows 1, 2, 10-13, 16, 17) can join sources; they share one federation pipeline except OLAP, which is a separate, weaker path. (ii) **Object consumers** (rows 3-9, 14, 18, 19) address one table per request; their "clean" behaviour is a stable, documented refusal, except GraphQL, which advertises relations that cannot run and therefore must either work or not be advertised.

### 2.1 Documentation claims not backed by wiring (to be corrected)

| Location | Claim | Verdict |
|---|---|---|
| `docs/features/f-gov-19-cross-source-graphql-relations.md:1-94` (esp. `:14,17,20,21,52-61,72,90-94`) | GraphQL cross-source relations via batched key lookup, `AllowCrossDataSource` enablement, HTTP microservice as source 2, filtering by `where:{customer:{...}}`, `CROSS_SOURCE_*` errors, config section `CrossSourceRelations` with `MaxNestedRowBudget` | Not backed (resolver unwired; schema has no relation filters; option not bound, real name `MaxNestedRows`; GraphQL schema is SQL-only; errors never surfaced) |
| `docs/plans/features.md:19,50` | #75 COMPLETED & VERIFIED | Not backed |
| `docs/plans/implementation/impl-features-analytics.md:27` | #75 COMPLETED & VERIFIED | Not backed |
| `docs/plans/implementation/impl-features-analytics.md:62,239` | Cross-source aggregation excluded, cross-source tables answer `GatewayNotImplementedException` | Contradicted: aggregation reads non-SQL tables via executors |
| `docs/plans/implementation/00-implementation-roadmap.md:114` and master plan row for `IMPL-ROADMAP`/`PLAN-FEATURES-ROADMAP` | #75 COMPLETED & VERIFIED | Not backed |
| `README.md:18` | Unified GraphQL access to heterogeneous databases, lakehouses and REST APIs | Overstated: the dynamic catalog schema is SQL-only |
| `docs/features/f-data-03-duckdb-olap.md:9,15` | OLAP joins Postgres, S3 Parquet and REST APIs | Not backed: only the `default-sql` connector is registered; HTTP/lakehouse give 403 "no active connector" |
| `docs/features/f-data-06-governed-rest-data-api.md:42` | Lakehouse, HTTP declarative and HTTP plugin tables served through federation | Partly not backed: only `HttpDeclarative` |
| `docs/features/f-gov-14-federated-virtual-filters.md:14,342,399` | REST and MCP "do not federate", answer 403 for a Web-API table with a virtual filter | Contradicted for REST and MCP `query_dataset`/`invoke_api` (they federate when `Rest`/`Mcp` is listed) |
| `docs/features/f-data-02-governed-websql.md:65` | Cross-catalog queries are rejected | True only when `CrossSource.Enabled=false`; contradicts `:67-77` of the same file |
| `docs/features/f-data-07-async-query-jobs.md:9` | "federated multi-source aggregations" | Only federated SQL; no aggregate service in jobs |
| `README.md:75` | Cross-source joins only via OLAP endpoint | Incomplete (WebSQL/Trino federate when opted in) |
| `docs/configuration-guide.md` | (omission) | `WebSql:CrossSource:*` keys undocumented; `MaxMemory`, `MaxTempDirectorySize`, `MaxParallelSourceReads` are dead |
| Search results `join_relations` (`McpToolExecutionHandler.cs:~158-166`) | Advertises joins across domains | Not guaranteed executable |

Feature documents are corrected in Phase 6 (`docs/features/` is out of scope for Phases 1-3). Plan documents are corrected now (see Changelog).

---

## 3. Definition of "Works Cleanly"

A consumer satisfies the contract when **all** of the following hold. These are the cross-cutting requirements; per-consumer acceptance criteria are in section 8.

1. **Same semantics, same governance.** A cross-source request returns exactly what the same request would return if the data were in one source, subject to the documented consistency caveat (3.4). Every table read applies the full single-source governance (consent, Casbin, ReBAC, RLS, tenant, masking, virtual filters including federated ones, row/cost budgets, audit) through the **governed pipeline**, never through an ad-hoc reader or a second re-implementation of row filters.
2. **Fail closed.** Any missing decision, unreadable source, unsupported source type, exceeded budget, or governance error denies the whole request. Never a partial result presented as complete, never silent truncation.
3. **Consistent error contract.** One stable set of codes (`FEDERATION_DISABLED`, `FEDERATION_TRANSPORT_NOT_ALLOWED`, `FEDERATION_UNSUPPORTED_SOURCE`, `FEDERATION_LIMIT_EXCEEDED`, `FEDERATION_SORT_NOT_SUPPORTED`, `FEDERATION_KEY_NOT_CLEAR`, `FEDERATION_SOURCE_ERROR`) mapped per transport (HTTP 403/400/422, GraphQL `extensions.code`, Trino `errorName`, MCP `error.code`, job `ErrorCode`). Configuration and limit codes are returned **only after** the caller was authorized for every referenced table (no existence or type oracle); before that the existing generic denial applies. Database and upstream messages are never surfaced.
4. **Bounded memory and no unbounded staging.** Every staged or looked-up row set is bounded (rows, bytes, keys, nested rows, wall time); the limits are enforced, documented, and the dead options are either enforced or removed. Staged data never touches disk in clear text.
5. **Pagination and ordering.** The root source decides ordering and paging of the primary result (declared-key ordering per F-GOV-21); related rows are attached by key, 1:N children are ordered by their own source's declared key order; limits are applied before the second source is read. Sorting by a field of another source is rejected (`FEDERATION_SORT_NOT_SUPPORTED`) unless a federated SQL path computes it.
6. **Consistency caveat.** Sources are read one after the other without a global snapshot. This is documented, and each cross-source response carries machine-readable provenance (sources read, read timestamps, per-source row counts) in the transport's metadata channel (GraphQL `extensions`, HTTP header, Trino stats, MCP result field).
7. **Audit per source read.** One audit record per source and per read with a shared correlation id, in every consumer (event `WebSqlCrossSourceSourceRead` generalised; no row data in the audit).
8. **Advertise only what runs.** Schemas, catalog search and MCP descriptions never expose a relation or join that the active configuration cannot execute.
9. **Opt-in model (decision D-1).** See section 4.

### 3.1 Transport opt-in decision (D-1)

Owner intent is that all consumers work. The safe default is a **two-level model**:

- `WebSql:CrossSource:Enabled` **stays `false` by default** (master switch). Federation allocates DuckDB memory and issues multi-source reads; an operator must turn it on knowingly.
- When `Enabled=true`, `AllowedTransports` **defaults to all known transports** (`WebSql`, `Trino`, `Rest`, `Mcp`, `ArrowExport`, `FlightSql`, `SqlEndpoints`, `GraphQL`, plus `AsyncJob` and `PgWire` when their conditions below are met), instead of only `WebSql` and `Trino`. The list remains as an operator-controlled **deny/narrow mechanism**: an explicit list narrows, an explicit empty list denies everything (fail closed), unknown names abort startup.
- `AsyncJob` joins the default list only after the staged-data-at-rest and result-file protections (SEC-FED-07, SEC-FED-08) are implemented; until then it stays opt-in. `PgWire` is added by the PG wire feature, which must register its transport name in the same constant set.
- Justification: with the transport default narrowed to two names, every other consumer silently denies a feature the owner expects to work; the real risk controls are governance, budgets and audit, which are identical across transports, so a per-transport default list adds friction without adding safety. The master switch plus startup validation keeps the blast radius explicit.

---

## 4. Best-Practice Benchmark

| Product | Practice | Consequence for Autheris |
|---|---|---|
| **Hasura remote joins / remote relationships** | A relationship from a database table to a remote source is declared explicitly, joined on field mappings, executed as a lookup against the remote with the parent's keys; limitations are documented (no filtering or ordering by remote fields, remote must accept the keys, batching by the engine). | Explicit relation declaration (`AllowCrossDataSource`), key-batched lookup, documented limits; reject sort and (initially) filter across sources. |
| **Apollo Federation entity batching** | `_entities` resolves many keys in one request per subgraph; a query planner splits the operation at subgraph boundaries; query plan is cached; result stitching in the router; per-subgraph error attribution; `@key` fields must be available. | Split the GraphQL tree at source boundaries, batch parent keys, stitch in the gateway, bounded batch size, per-source error attribution (without leaking messages). |
| **Trino federation** | One engine, one SQL dialect, connectors are read-only governed splits, predicate and projection pushdown, dynamic filtering; no cross-connector snapshot consistency. | Reuse the existing SQL federation (DuckDB staging with minimal projection and filter pushdown); document no cross-source snapshot; plan limits in rows and bytes. |
| **Dremio** | Reflections and virtual datasets for cross-source speed; row/column access policies applied per source dataset before the join; cross-source joins run in the engine, large results spill; reflections are a governed cache. | Governance per source dataset before the join (already so); no caching of cross-source results in this PRD (non-goal); spill must be bounded and protected. |
| **GraphQL DataLoader pattern** | Per-request batching and de-duplication of key lookups; no cross-request cache of authorized data. | Per-request batching only; no key or row cache across requests. |
| **OWASP API / data-federation guidance** | Fan-out is an amplification vector; join keys are an oracle if masked values can be correlated; confused-deputy risk when the gateway reads source 2 with its own privileges. | Bounded fan-out (batch size x batches), clear-key rule, source-2 read always under the caller's governance. |

### 4.1 Rejected anti-patterns

1. **A separate cross-source implementation per consumer.** Divergent governance and error behaviour; rejected in favour of one federation core (decision D-2).
2. **Gateway-side re-implementation of governance on raw rows** (what `CrossSourceRelationResolver` does today with `StreamingRowFilterAstEvaluator`). Two policy engines drift; the tenant `NULL` bypass proves it. Rejected: source-2 reads go through the governed pipeline.
3. **Unbounded full-table staging** to resolve a relation. Rejected; key lookup with bounded batches and a hard row cap.
4. **Silent truncation** of nested or related rows. Rejected; exceed means `FEDERATION_LIMIT_EXCEEDED`.
5. **Gateway service account reading source 2** to "make the join work". Rejected; the caller's governance and (where configured) the caller's delegated identity apply.
6. **Cross-request caching of source-2 rows or keys.** Authorization-bearing data; rejected for this scope.
7. **Client-supplied engine SQL that bypasses the governed compiler** (current OLAP endpoint). Rejected; one compiler, one set of guardrails.
8. **Exposing relations in the schema that fail at runtime.** Rejected; schema gating mirrors executability.
9. **Specific "why not" errors before authorization** (type or existence oracle). Rejected; specific codes only after all tables are authorized.

---

## 5. Decisions

| ID | Decision |
|---|---|
| **D-1 Opt-in model** | Master switch `Enabled` default `false`; `AllowedTransports` default = all registered transports when enabled (AsyncJob/PgWire conditional), explicit list narrows, empty denies, unknown names abort startup. See 3.1. |
| **D-2 One federation core** | SQL-text consumers keep using the governed SQL federation pipeline (`GovernedSqlExecutionService` -> `FederatedDuckDbExecutionService`); object consumers (GraphQL, and optionally REST/OData later) use a **governed key-lookup reader** built on the same governed per-source read of `FederatedStagingService`/`GovernedConnectorReader`. Both share the transport gate, options, error contract, audit and provenance. The OLAP endpoint becomes a facade over the SQL pipeline. |
| **D-3 GraphQL strategy** | Tree splitting at source boundaries with batched key lookup (Apollo/Hasura style), not whole-tree-to-DuckDB: it keeps root pagination exact, bounds the second read by the page, and avoids staging. Supported: N:1, 1:N, nested chains; the root source query keeps today's single-statement tree compiler per source segment. |
| **D-4 Relation declaration** | `AllowCrossDataSource` is persisted on `TABLE_RELATIONS` (baseline DDL in place, shape snapshots updated), settable via admin API and MCP admin tools (four-eyes by existing flow); default `false`. A cross-source relation without the flag is not exposed in the schema. |
| **D-5 Non-SQL in GraphQL** | `HttpDeclarative` tables become visible in the catalog schema as relation targets (key lookup via the governed connector reader) and as roots (list read with the governed reader, limits as REST). `HttpPlugin` and lakehouse tables follow only if the owner confirms (open question Q-3); until then they stay out of the schema. |
| **D-6 Filter and sort across sources** | Sort across a source boundary: rejected with `FEDERATION_SORT_NOT_SUPPORTED`. Filter on a related field: bounded semi-join (target read first, at most `MaxCrossSourceFilterKeys` keys, exceed -> `FEDERATION_LIMIT_EXCEEDED`); delivered in the last increment and only with relation fields added to `<T>_filter`. |
| **D-7 OLAP endpoint** | Re-implemented as a thin facade that accepts the same request shape, routes the SQL through the governed compiler, router, planner and transport gate (transport name `Olap`), and deprecates client-named `tableNames` (ignored with a warning header). Breaking change accepted. |
| **D-8 Error contract** | Stable `FEDERATION_*` codes mapped per transport; specific only after authorization of all tables; messages fixed; details only in audit (stable code, never database text). |
| **D-9 Provenance** | Each cross-source response includes per-source read information (source names, timestamps, row counts) in the transport's metadata channel; no table data. |
| **D-10 Parameters** | The federated path binds client parameters like the single-source path (named parameters of SQL endpoints and REST/MCP filters), instead of ignoring them. |
| **D-11 Dead options** | `CrossSourceOptions.MaxMemory` and `MaxTempDirectorySize` are removed in favour of `DuckDbOlap:*` (documented); `MaxParallelSourceReads` is implemented with a bounded semaphore (default 4) or removed (Phase 2 decides). |
| **D-12 Documentation** | Phase 6 corrects every row of 2.1; plan documents are corrected now. |

---

## 6. Scope

### 6.1 In scope
- A shared federation contract (gate, options, error codes, provenance, audit) used by all consumers.
- Hardening of the SQL federation: parameters, `truncated`, row-cap semantics, Flight `/info` gate, transport validation, error contract, staging spill and async result protection.
- OLAP facade over the governed pipeline.
- GraphQL cross-source relations (N:1, 1:N, nested) on SQL sources, then `HttpDeclarative`, then bounded relation filters.
- Persistence and administration of `AllowCrossDataSource`.
- Clear, stable refusal for object consumers that never cross sources (REST, OData, aggregate, metrics, subscriptions, changes).
- Integration tests with two different SQL engines (PostgreSQL and SQL Server) plus an HTTP source, and a consumer parity matrix test.

### 6.2 Non-goals
- A global snapshot or two-phase read across sources.
- Cross-source writes or DML.
- Cross-request caching of source-2 data.
- Cross-source aggregation or k-anonymity over joined data (aggregates stay single-table).
- REST/OData `expand` across sources (owner question Q-5; the SQL channels cover multi-source reads).
- Subscription payload enrichment from a second source.
- Changing the CDC or change-detection design (adjacent defect tracked in 3.3 only).

### 6.3 Adjacent findings (not owned by this feature, must be tracked)
1. `GovernedAggregationService` may take the in-memory executor path for SQL tables in production DI (`GatewaySqlEngineServiceExtensions.cs:65`), aggregating up to 100,000 rows in memory and skipping the SQL pushdown branch and the CR-10 virtual-filter guard. Verify in Phase 2; if confirmed, open a separate fix.
2. `changesSince` in production DI binds `InMemoryChangeVersionStore`; F-DATA-09 claims may be overstated.
3. The WebSQL JSON `truncated` flag is always `false` for federated results (covered by FR-12).

---

## 7. Functional Requirements

- **FR-1 Contract module.** One component owns the federation gate (Enabled + transport), limits and error codes; no consumer evaluates them itself.
- **FR-2 Transport model.** Per D-1; startup validation of `AllowedTransports` names against the registered constants; `GraphQL`, `Olap` and (later) `PgWire` registered.
- **FR-3 Flight consistency.** Flight `/info` (and any submit-time validation, including async job submit) applies the same gate as execution; a job that can never run is rejected at submit with the stable code.
- **FR-4 Error contract.** `FEDERATION_*` codes per D-8, mapped on every transport listed in the matrix; generic denial before authorization.
- **FR-5 Parameters.** Per D-10.
- **FR-6 GraphQL relations.** A relation whose two tables are in different sources, with `AllowCrossDataSource`, both sides readable, and clear join keys, resolves N:1, 1:N and nested to the depth allowed by the tree budget; children missing, filtered or masked by governance resolve to `null`/empty identically to non-existing rows (no oracle).
- **FR-7 Schema gating.** A cross-source relation appears in the schema only if flagged, both tables active and visible to the caller's catalog view, keys clear, and federation enabled for `GraphQL`.
- **FR-8 Governed key lookup.** Child reads are executed through the governed pipeline with the key list as an additional predicate conjunct; batches of at most `MaxBatchSize` (default 500, bounded by the engine's bind-parameter limit), at most `MaxBatches` per request; total related rows at most `MaxNestedRows` (default 5000).
- **FR-9 Sort guard.** Sort by another source's field -> `FEDERATION_SORT_NOT_SUPPORTED`.
- **FR-10 Non-SQL.** `HttpDeclarative` tables appear in the GraphQL schema and resolve per D-5; `HttpPlugin`/lakehouse per Q-3.
- **FR-11 Relation filters.** Bounded semi-join per D-6.
- **FR-12 Truncation and caps.** Federated SQL results honour `WebSql.DefaultMaxRows`/`MaxAllowedRows` and report `truncated` like single-source results; staging caps keep their codes.
- **FR-13 OLAP facade.** Per D-7; no client DuckDB SQL reaches the engine unvalidated.
- **FR-14 Provenance and audit.** Per 3.6 and 3.7 for every consumer.
- **FR-15 Staging at rest.** DuckDB spill and async result files are created with owner-only permissions in a dedicated directory, spill is encrypted where the engine supports it or disabled otherwise, results are encrypted at rest, and cleanup failures are alerting events.
- **FR-16 Dead options.** Per D-11; `docs/configuration-guide.md` gains the `WebSql:CrossSource:*` section (Phase 6).
- **FR-17 Relation administration.** Per D-4 (REST, MCP, audit with before/after, epoch bump of both tables).
- **FR-18 Object consumers.** REST, OData, aggregate, metrics, subscriptions and change detection answer an attempt to cross sources (or an unsupported option) with a stable documented code; MCP `describe_dataset`/`search_catalog` list only executable relations.
- **FR-19 Telemetry.** Metrics: cross-source requests by consumer and outcome, sources per request, batches, rows staged and looked up, denials by code, durations; trace span per source read.

## 8. Security Requirements (summary; Phase 3 appends binding conditions)

- **SR-1** Source-2 reads run under the caller's governance through the governed pipeline; no gateway-service-account bypass.
- **SR-2** Join keys on both sides must be `Clear` (not masked, not pseudonymised, not hidden); checked before any read.
- **SR-3** A governed-away child is indistinguishable from a missing child.
- **SR-4** Tenant isolation on every source read; a `NULL` tenant value never passes.
- **SR-5** Fan-out bounded (batch size x batches x rows) and counted against the user's cost budget.
- **SR-6** No existence or type oracle through error codes or timing classes beyond what single-source access already allows.
- **SR-7** No staged or looked-up data in clear text on disk; no data in logs or audit.
- **SR-8** Every source read audited with a correlation id; audit failure denies the request.
- **SR-9** No client SQL or DuckDB function reaches an engine outside the governed compiler and function allowlist.

## 9. Acceptance Criteria (per consumer)

- **AC-1 WebSQL / Trino:** with `Enabled=true` and default transports a join of a PostgreSQL table and a SQL Server table returns the correct rows with identical governance to single-source (masking, RLS, tenant); parameters bind; `truncated` reflects the row limit; Trino with `X-Trino-Catalog` for one of the sources and a qualified second catalog behaves as specified (Q-4); error codes per FR-4.
- **AC-2 REST, MCP `query_dataset`:** single-table reads of `HttpDeclarative` work with default transports; attempts to cross sources via parameters are rejected with the stable code; MCP `query_sql` and `invoke_api` POST behave as WebSQL.
- **AC-3 Arrow export, Flight SQL, async jobs, SQL endpoints:** same join as AC-1 works; Flight `/info` and job submit fail early with the stable code when the gate denies; parameterised SQL endpoints run; async job result files are encrypted.
- **AC-4 OLAP:** the endpoint refuses a join over a masked column (SEC-JOIN-01) like WebSQL, honours the gate, and produces the same rows as WebSQL for the same SQL; `tableNames` ignored with a warning.
- **AC-5 GraphQL N:1 and 1:N:** a query `orders { id customer { name } }` (orders in PostgreSQL, customers in SQL Server) and the reverse `customers { orders { id } }` return correct data, exactly one key-lookup batch per 500 distinct keys, and a nested chain across three tables resolves; a denied, filtered or masked child yields `null`/empty with no distinguishing error.
- **AC-6 GraphQL limits:** exceeding `MaxNestedRows`/`MaxBatches` fails with `FEDERATION_LIMIT_EXCEEDED`; sorting by a related field fails with `FEDERATION_SORT_NOT_SUPPORTED`; masked join key fails with `FEDERATION_KEY_NOT_CLEAR` (only after authorization).
- **AC-7 GraphQL schema:** an unflagged or non-executable cross-source relation is absent from the schema; MCP `search_catalog` and `describe_dataset` list the same relations as the schema.
- **AC-8 GraphQL non-SQL:** an `HttpDeclarative` table can be queried as root and as relation target with governance equal to REST; `HttpPlugin`/lakehouse per Q-3.
- **AC-9 Relation filters:** `where:{customer:{country:{eq:"DE"}}}` resolves by bounded semi-join and fails closed above the key limit.
- **AC-10 Object consumers:** OData `$expand` and REST attempts return the documented stable code; aggregate and metrics reject cross-source input with `INVALID_*` plus `FEDERATION_*`; subscriptions reject a cross-source selection at subscription time.
- **AC-11 Provenance and audit:** every cross-source response carries per-source provenance; one `WebSqlCrossSourceSourceRead` audit record per source read with a shared correlation id in every consumer; audit write failure denies the request.
- **AC-12 Opt-in:** `Enabled=false` rejects with `FEDERATION_DISABLED` (after authorization) on every transport; an unknown transport name aborts startup; an explicit empty list denies all; all transports allowed by default when enabled (AsyncJob conditional on FR-15).
- **AC-13 Parity matrix test:** one test iterates all consumers over the same two-source fixture and asserts the expected outcome (works / stable refusal) per consumer; adding a consumer without a matrix row fails the test.
- **AC-14 Documentation:** all rows of 2.1 corrected (Phase 6); plan status corrected (done in this phase).

## 10. Telemetry

Metrics per FR-19 on `GatewayDiagnostics`; per-source read span with `source`, `batches`, `rows`, `consumer`; denial counter by `FEDERATION_*` code; dashboard hints in the feature doc (Phase 6).

## 11. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | Two federation mechanisms (SQL staging and key lookup) drift in governance | Both use the same governed per-source read; shared parity matrix and security tests |
| R-2 | Fan-out amplification (many batches per request) | `MaxBatches`, `MaxNestedRows`, user cost budget, concurrency lease |
| R-3 | Tree splitting changes the performance profile of single-source GraphQL | Splitting only when a boundary exists; single-source path untouched and covered by golden tests |
| R-4 | Broadening default transports raises exposure | Master switch stays off by default; governance identical across transports; startup validation; AsyncJob conditional |
| R-5 | OLAP facade breaks existing clients | Breaking changes allowed (not in production); warning header; migration note in feature doc |
| R-6 | Inconsistent reads across sources confuse consumers | Provenance in the response, documented caveat |
| R-7 | Spill encryption unsupported by DuckDB version | Phase 2/3: disable spill (`MaxTempDirectorySize=0`) and rely on bounded memory, or encrypt at the file-system layer |
| R-8 | HTTP sources have no key-lookup capability | Descriptor must declare a key query parameter or the relation is not executable (not advertised) |

## 12. Open Owner Questions

| ID | Question | PM recommendation |
|---|---|---|
| Q-1 | Flip the `CrossSource:Enabled` default to `true`? | No. Keep opt-in master switch; widen the transport list instead (D-1). |
| Q-2 | Accept the breaking OLAP change (facade, `tableNames` ignored, transport `Olap`)? | Yes; closes the compiler bypass. |
| Q-3 | Should GraphQL expose `HttpPlugin` and lakehouse tables? | Not in this feature; add after a connector read path with key lookup exists. |
| Q-4 | Trino clients that always send `X-Trino-Catalog`: allow cross-catalog when the other catalogs are fully qualified? | Yes, the header selects the default catalog only. |
| Q-5 | Do REST and OData need `expand` across sources? | No; use WebSQL/GraphQL. Revisit on demand. |
| Q-6 | Who may flip `AllowCrossDataSource` (GovernanceAdmin, or both table owners with four-eyes)? | Both table owners approve (four-eyes) because it joins two owners' data. |
| Q-7 | Is a per-user daily cross-source row budget wanted? | Yes, reuse the existing cost/quota middleware. |

## 13. Increments

| Increment | Content | Value |
|---|---|---|
| **INC-1 Truth and hygiene** | Plan corrections (done), transport validation and gate module, Flight `/info` and job-submit gate, error contract, dead options | Honest, predictable SQL consumers |
| **INC-2 Federation core hardening** | Parameters, `truncated`/caps, provenance, spill and result protection, OLAP facade, default transports | All SQL-text consumers clean |
| **INC-3 GraphQL SQL-to-SQL relations** | Relation persistence and admin, governed key reader, resolver rewiring, tree splitting, schema gating | Issue #75 delivered for real |
| **INC-4 Non-SQL and relation filters** | `HttpDeclarative` in GraphQL, bounded semi-join filters | Full F-GOV-19 scope |
| **INC-5 Parity and documentation** | Integration matrix, Phase 6 docs | Regression protection |

## 14. Lifecycle Hand-off

- **Phase 2 (`csharp-architect`):** verify this PRD against code, fix the architecture of the shared core, work packages with disjoint files and sequencing (`2026-10-16-plan-cross-source-all-consumers.md`).
- **Phase 3 (`csharp-security-expert`):** threat model and binding conditions SEC-FED-xx, mandatory security tests.
- **Phase 4-6:** TDD implementation, review, feature documentation (`docs/features/f-gov-19-...` rewrite plus a new federation feature doc; number confirmed in Phase 6), master plan update.

## 15. Changelog

- 2026-10-16: Initial PRD (Phase 1), verified against `70b75e27`. The false "COMPLETED & VERIFIED" status of #75 is corrected in `features.md`, `impl-features-analytics.md`, `00-implementation-roadmap.md` and the master plan.

# PRD: REQ-FED-01 - Cross-Source Access That Works Cleanly for All Consumers

**Document ID:** `REQ-FED-01`
**Date:** 2026-10-16
**Status:** PRD READY (Phase 1 complete, sections 7.1 and 7.2 included); SECURITY REVIEW pending for section 7.2 (Phase 3 placeholders SEC-FED-35..42 in PLAN-FED-01 12.7); next: Phase 2 for 7.2 is a brief design in PLAN-FED-01 4.12
**Author:** Enterprise Product Manager (Phase 1)
**Source:** Owner requirement 2026-10-16: "The cross-source feature must work cleanly for all consumers." (original German: "das cross source feature muss bei allen abnehmern sauber funktionieren"). Related issue: #75 "Cross-source GraphQL relations" (falsely recorded as completed).
**Branch:** `feat/cross-source-all-consumers` (base `main` `70b75e27`)
**Extended 2026-10-16:** owner issues #122, #123, #124 (federated query performance) and #125 (doc correction), see section 7.1; owner issues #131 (federated bound parameters) and #132 (co-located data sources), see section 7.2.
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
| `docs/features/f-data-02-governed-websql.md` (section "Protection Against Cross-Catalog Queries" versus the `Gateway:WebSql:CrossSource:Enabled` section a few lines later; issue #125, follow-up to #118) | Says queries spanning several data sources are rejected, then documents the opt-in federation | Self-contradicting; must say "rejected unless `CrossSource.Enabled`" and link the federation section (same defect as the `:65` row above, now with owner issue #125) |
| `docs/features/f-data-03-duckdb-olap.md` (issue #125) | (omission) Does not say that federated staging reads each table **without the query predicates** (`PushdownFilter: null`, `CrossSourcePlanner.cs:266`) and fails with `ROW_LIMIT_EXCEEDED` above `MaxStagedRowsPerTable` (`FederatedStagingService.cs:201`) | Omission; must state the current behaviour and, after #122-#124 land, the pushdown rules and their limits. Phase 6 (or a separate doc fix) |
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


### 7.1 Federated query performance: predicate pushdown, same-source subtrees, dynamic key-set filters (#122, #123, #124)

*Phase 1 addition (Product Manager), verified against `80eb6b97`. Source: owner issues #122, #123, #124 (all three are follow-ups of the SQL federation; #123 and #124 build on #122). #125 is the documentation correction listed in 2.1 and is not a feature.*

#### 7.1.1 Verified current behaviour

| Claim in the issues | Verified | Evidence |
|---|---|---|
| The planner always sets `PushdownFilter: null` | Confirmed | `src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs:266` (`new StagingTableRequest(..., PushdownFilter: null, VirtualFilterPlan: vfPlan)`) |
| The plumbing for a pushed filter exists | Confirmed | `StagingTableRequest.PushdownFilter` (`src/Autheris.Application/Olap/IFederatedStagingService.cs:19`); `FederatedStagingService.cs:164-167` sets `session.Items[TableQueryItems.Filter]` only for `DataSourceType.Sql`; audit flag `filterPushedDown` at `:232`; `TableQueryItems.Filter` (`src/Autheris.Application/Interfaces/IGatewayExecutionService.cs:135-142`) is read by `SqlTableReadExecutor.BuildFilter` (`src/Autheris.Application/Services/SqlTableReadExecutor.cs:299-362`) and AND-combined with argument equalities (`:327-362`, duplicate parameter names fail closed, SEC-ADG-02). The clause is a `TableFilterClause` (`IGatewayExecutionService.cs:66`): predicate text, parameters, referenced columns, and a Trino text factory (`GetTrinoPredicate`, a clause without it cannot be planned, fail closed) |
| Gateway row filter and Virtual Filter arguments are the only things that reach the source | Confirmed | `FederatedStagingService.cs:135-162`: `limit = MaxStagedRowsPerTable + 1`, `PushdownFilterSql: req.Decision.CombinedRowFilterSql`, Virtual Filter arguments from `VirtualFilterPlan.PushdownArguments` |
| Staging fails above the per-table cap although the filtered table would be small | Confirmed | `FederatedStagingService.cs:161,173,199-201` (`ConnectorRowLimitExceededException` -> `ROW_LIMIT_EXCEEDED`); budget defaults `RestApiOptions.cs:80-82` (`MaxTableCount` 5, `MaxStagedRowsPerTable` 50,000, `MaxTotalStagedRows` 200,000) wired at `FederatedDuckDbExecutionService.cs:285-322`. Table count is checked at `FederatedStagingService.cs:70-73` (`LIMIT_EXCEEDED`) |
| Staging is one read per table, sequential | Confirmed | `FederatedStagingService.cs:126` (`foreach (var req in tables)`); `MaxParallelSourceReads` (`RestApiOptions.cs:88`) is not enforced (already WP-FED-03) |
| All query predicates stay in DuckDB | Confirmed | The user AST is rewritten to staging names `s0, s1, ...` and regenerated for DuckDB (`CrossSourcePlanner.cs:277-282`); values are bound (`ParameterSource.Empty`, `:282`, i.e. request parameters are currently not carried, already D-10/WP-FED-03) |
| Masked columns: DuckDB compares the pseudonym | Confirmed, with a refinement | `GovernedSqlExecutionService.EnforceMaskedColumnGuardrails` (`GovernedSqlExecutionService.cs:290-342`) rejects a masked or denied column in a join or filter **except HMAC-masked columns** (`isEffectiveHmac`, `:300-307`, `:310`), which pass and are compared as pseudonyms in DuckDB. It is called per table at `CrossSourcePlanner.cs:127-130` (and `FederatedDuckDbExecutionService.cs:432,478`). Consequence: the HMAC exemption is the only case where a filter on a non-`Clear` column reaches the planner, and it is exactly the case #122 must exclude from pushdown |
| `ConnectorCapabilities.MaxBatchSize` bounds batches | Confirmed, but too large for SQL Server | `src/Autheris.Domain/Connectors/ConnectorCapabilities.cs:19,24` (SQL default 5000, HTTP 1000) exceeds the SQL Server bind limit 2100 (`src/TrinoSqlEngine/Ast/Capabilities/DialectCapabilityTable.cs:39`; PostgreSQL 65535, `:97`). Batch size must be `min(ConnectorCapabilities.MaxBatchSize, dialect MaxBindParameters, configured batch size)` |
| `MaxPushdownKeys` is the Virtual Filter key bound | Confirmed | `src/Autheris.Domain/Options/VirtualFilterFederationOptions.cs:37` (default 1000, range 1..10000); `VirtualFilterPushdownSelector.cs:84`; per binding `FilterBinding.HardMaxPushdownKeys` 1000 (`VirtualFilterModels.cs:309`). #124 needs its own knob (different trust and meaning); it must not reuse this option |
| Cross-source GraphQL relations read the other source with batched `IN` (#75) | **Not true in production**: resolver unwired (see 1.2.3). `CrossSourceRelationOptions.MaxBatchSize` default 500 exists (`src/Autheris.Application/Data/Relations/CrossSourceRelationModels.cs:39`) but is never bound. #124's statement "already an accepted pattern (#75)" rests on a feature that is not delivered. The pattern is accepted as a design (PLAN-FED-01 section 4.6), not as running code | `CrossSourceRelationModels.cs:39` |
| Query analysis (#79) can list pushed predicates | Not yet: no federation awareness | `src/Autheris.Application/Governance/Analysis/` has no reference to the cross-source planner (`QueryAnalysisService.Sql.cs`, `QueryAnalysisModels.cs`); the analysis returns `QueryValidationReport`/`QueryAccessRequirementsReport` per item. #122-#124 need a new, additive "federation plan" section in the analysis result computed by the same planner code (no second implementation) |
| Single-source path has a bound for tables inside a subtree (#123) | **Not found**: no explicit table-count bound for single-source WebSQL was located in `src/` (searched `MaxTables`, `MaxJoin*`). Owner question Q-10 |
| Function allowlist is applied to the whole query as DuckDB SQL | Confirmed | `CrossSourcePlanner.cs:108-117` checks the flat `metadata.FunctionCalls` against `SqlFunctionAllowlists.DuckDb`. With same-source subtrees (#123) the functions inside a subtree run in the source and must be checked against the source dialect's allowlist (as single-source does), the rest against DuckDB; a flat list cannot tell them apart, so scope-aware analysis is needed |

#### 7.1.2 Qualification and deduplication

All three are accepted as **P2 performance features with security impact (classified security-relevant)**: they change what a caller-controlled predicate and caller-derived key values do at the source, and who sees which values.

- **Not duplicates.** F-GOV-14 (federated virtual filters) pushes *authorized key sets of the policy* (`MaxPushdownKeys`) to restrict reads for governance; #122-#124 push *caller predicates and caller-derived keys* to reduce volume. They share mechanics (`TableFilterClause`, `TableQueryItems.Filter`, batch limits) but not intent, and must stay separate in audit (`vfTier`/`pushdownKeyCount` versus `pushedPredicates`/`keySetFilter`). F-GOV-19 / PLAN-FED-01 WP-FED-06 (batched governed key lookup for GraphQL) is the same *component* as #124's batched `IN`; see 7.1.4. REQ-GOV-107 (key-set cache for correlated row filters) is a cache of *policy* subquery results with its own invalidation; #124 is explicitly cacheless (PRD anti-pattern 4.1.6, SEC-FED-06) and must not share its cache.
- **Overlap with this PRD.** FR-8 (governed key lookup) and D-6 (bounded semi-join for relation filters) already define batched key reads for GraphQL. #124 extends the idea to the SQL staging path. WP-FED-03 (SQL federation hardening) owns `CrossSourcePlanner.cs`, `FederatedStagingService.cs`, `FederatedDuckDbExecutionService.cs`: the new work must be sequenced after it (shared files).

#### 7.1.3 Benchmark (brief)

| Product | Practice | Consequence |
|---|---|---|
| **Trino** | Predicate pushdown per connector with a domain that is a *superset* the engine re-applies (the engine keeps the filter above the scan unless the connector guarantees exactness); dynamic filtering: build side first, probe side restricted by collected values, bounded by a size limit and a timeout, falls back to the unfiltered read | Same contract as #122/#124: DuckDB keeps every predicate and join; the source read is only a superset. Bounded key set with fallback is the Trino model |
| **Presto connectors** | Connectors declare pushdown capabilities; unsupported expressions are not pushed; "remaining filter" returned to the engine | Per-dialect pushability table; unknown means not pushed |
| **Apache Calcite** (JDBC adapter) | Rule-based push of filter and whole join subtrees into a JDBC convention when the dialect supports it; one convention per subtree | #123 is the Calcite "JdbcToEnumerableConverter above a JDBC subtree" model: group by convention (source), push the maximal subtree |
| **Dremio** | Pushdown of filters and joins into the source plan; per-source row/column policies still evaluated at source dataset level before the join | Governance stays per source; pushed subtree uses the source's governed read |
| **Anti-patterns rejected** | (1) pushing a predicate the source evaluates *narrower* than the engine (silent row loss); (2) pushing predicates over masked columns (oracle); (3) dynamic filter without a bound (DoS); (4) caching key sets across requests | Superset rule, mask rule, key bound with stable error, no cache |

#### 7.1.4 Gatekeeper review

- **Superset semantics.** The dangerous direction is a source that is *narrower* than DuckDB, not broader. Case-insensitive collation and PAD SPACE comparison make `=`, `IN` and `LIKE` broader (safe) but make `<>`, `NOT IN`, `NOT LIKE` and ordered comparisons on strings **narrower or reordered** (`status <> 'closed'` on a case-insensitive SQL Server column drops `'CLOSED'` rows that DuckDB keeps, and `'closed '` equals `'closed'`). The issue example `status <> 'closed'` is therefore **not pushable on SQL Server (and Oracle `CHAR`) for string columns** unless the column is known to be binary-collated and unpadded; it is pushable for numeric, boolean, date and GUID columns. A per-dialect pushability table is part of the requirement (FR-22). Where uncertain: do not push (always safe, the query only becomes slower).
- **Masked and HMAC columns.** Never pushed, never used as a key set. Because `EnforceMaskedColumnGuardrails` lets HMAC columns through, the pushdown selector must test the effective access level (`Clear` only) itself and not rely on the guard.
- **Oracle risks.** (1) Predicate over a masked column (excluded). (2) A pushed predicate that can *raise* in the source (cast, overflow, division) evaluated on rows that the row filter would hide, if the source evaluates the caller predicate before the gateway row filter: an error versus success difference over hidden rows. Pushed predicates are restricted to column-versus-constant comparison forms without casts, arithmetic or functions (FR-22). (3) Row-count side channel: staging failure (`ROW_LIMIT_EXCEEDED`) now depends on the predicate, so a caller can probe counts; this is acceptable only if the count is taken after all governance. Gateway-side Virtual Filter enforcement removes rows *after* the read (`RowsBeforeVirtualFilter`, `FederatedStagingService.cs:~240`), so for a table with such an enforcement plan the cap would count rows the caller may not see: pushdown on such a table is not allowed in the first step (FR-24).
- **DoS via key batches.** A key set multiplies source reads: bound by key count, batch count, rows per table (cumulative over batches), total rows and bytes, wall clock, cost model, per-user concurrency lease. Fail with the stable error, never truncate.
- **Key-value disclosure across sources.** Clear key values from source A (already visible to the caller, governed) are sent to source B's database and appear in B's query text/logs and plans as bind values. That is a new data flow between two owners' systems. The same flow is accepted for #75 and Virtual Filters, but needs an explicit control (owner question Q-8): at least SQL sources only (never HTTP in the first step), parameter-bound, and never in the gateway audit or logs.
- **Duplicates across batches.** With a case-insensitive source, key `'a'` and key `'A'` in different batches return the same source row twice, so the staged table has duplicates and the join result is wrong. The design must filter each batch's rows by exact (ordinal, DuckDB-equivalent) key match against that batch's keys (PLAN-FED-01 4.11.4).

#### 7.1.5 Functional requirements

- **FR-20 Conjunct splitting (#122).** The planner splits the top-level `WHERE` and the `ON` of inner joins into conjuncts. A conjunct referencing exactly one table (one reference, one alias) and meeting the pushability rules (FR-22) becomes a `TableFilterClause` for that table's source read (SQL sources only; HTTP unchanged). DuckDB keeps the complete original query.
- **FR-21 Combination (#122).** Pushed clauses are combined with the gateway row filter and Virtual Filter arguments by `AND`; they never replace or widen them. A parameter name collision fails closed (existing SEC-ADG-02).
- **FR-22 Pushability rules (#122).** A per-dialect table decides pushability by column type class and operator; the effective column access must be `Clear`; only column-versus-literal/parameter forms (`=`, `<>`, `<`, `<=`, `>`, `>=`, `IN`, `NOT IN`, `BETWEEN`, `LIKE`, `IS [NOT] NULL`, combined by `AND`/`OR`/`NOT` within one table) are eligible; no functions, casts, arithmetic, subqueries or non-deterministic expressions in the first step; `NOT IN` is not pushed when any element is `NULL`; `LIKE` is not pushed with bracket classes or an unsupported `ESCAPE`; a literal whose type class differs from the column's type class is not pushed; string `<>`, `NOT IN`, `NOT LIKE` and ordered comparisons follow the per-dialect rule in 7.1.4. Unknown dialect, type or operator means not pushed.
- **FR-23 Join semantics (#122).** Inner join: `WHERE` and `ON` conjuncts of one table are pushable. Left join: `WHERE` conjunct on the preserved side and `ON` conjunct on the null-supplying side are pushable; a `WHERE` conjunct on the null-supplying side and an `ON` conjunct on the preserved side are not pushed in the first step; full join: nothing is pushed. Conjuncts inside CTEs, subqueries and derived tables are not pushed by #122 (#123 covers CTEs). A table referenced twice (self-join) gets a pushed predicate only if every reference has one (disjunction of the references) or not at all.
- **FR-24 Table-level exclusions (#122, #124).** No pushdown to a table whose plan needs gateway-side removal of rows after the read (Virtual Filter enforcement with `RowsBeforeVirtualFilter`, post-read masking that changes values of the referenced column).
- **FR-25 Parameters (#122).** Bound `@param` values, including list parameters for `IN`, are pushed as bound values like literals; this relies on WP-FED-03 carrying request parameters into the federated path (D-10).
- **FR-26 Audit and analysis (#122, #123, #124).** Each source read audit record lists pushed predicates per table as structure only (column names, operator kinds, count; no values), `pushedPredicateCount`, `keySetFilter` (kind, key count, batch count), `subtree` (table list). The #79 analysis result gains an additive federation section computed by the planner itself: per table the pushed and the remaining predicates, subtrees per source, key-set joins with order and bound.
- **FR-27 Same-source subtrees (#123).** The planner groups tables by data source. A subtree (maximal contiguous join group, or a CTE or derived table) that references only tables of one SQL source is compiled through the governed single-source path with row filters, masks, consents and tenant predicate of each table and executed in the source; only its result set is staged. Unsupported shapes stay table-by-table.
- **FR-28 Subtree semantics (#123).** For a subtree the source's semantics are authoritative (collation, comparison, functions), exactly as for a direct single-source query; documented. Window functions, aggregation and `DISTINCT` inside a subtree run in the source. Functions inside a subtree are checked against the source dialect allowlist, the remainder against the DuckDB allowlist.
- **FR-29 Subtree budgets (#123).** `MaxStagedRowsPerTable` and the byte caps count the staged subtree result; `MaxTableCount` counts staged inputs (subtrees plus single tables), with a separate bound for tables inside one subtree.
- **FR-30 Subtree governance (#123).** Masking is applied before rows leave the subtree; a subtree output column that originates from a masked column, or whose origin is unknown, is treated as masked and cannot be a cross-source join key, key-set source, or pushed predicate target (fail closed). The engine guards on masked columns in predicates (`AstSecurityVisitor`) apply inside the subtree. Every subtree gets its own audit record listing all its tables. All tables are authorized once, before any read.
- **FR-31 Key-set filter (#124).** For an inner join, or a left join whose right side is the large side, on a single-column equality `a.k = b.k` between clear columns of two tables, the planner may stage one side first and push its distinct non-null key values into the other side's read as batched `k IN (...)`, using the shared batched key component (7.1.6).
- **FR-32 Key-set ordering, bounds, fallback (#124).** Order: a side with pushed predicates (#122) or a smaller catalog size hint goes first; a per-table hint (`expected_rows` or `large`) in the catalog is enough, no statistics service. Bounds: `MaxKeySetKeys` (own option, default to be set in Phase 2), batch size `min(ConnectorCapabilities.MaxBatchSize, dialect bind limit, configured)`, `MaxKeySetBatches`. Above the key bound the planner falls back to the plain read; if that exceeds the staging budget the query fails with `ROW_LIMIT_EXCEEDED` (existing code, later `FEDERATION_LIMIT_EXCEEDED`), never truncated.
- **FR-33 Key-set safety (#124).** Join keys must be `Clear` on both sides in the caller's decision; a masked, HMAC or unknown-origin key never forms a key set and the join proceeds under the current rules. The source's matching is a superset; the gateway removes rows of each batch that do not match the batch's keys exactly (ordinal) so that no row is staged twice; the join itself stays in DuckDB. Key sets are per request, never cached, never logged, SQL sources only in the first step. Each batch read is a source read with its own audit record and cost.
- **FR-34 Switches.** `WebSql:CrossSource:PredicatePushdown:Enabled`, `SameSourceSubtrees:Enabled`, `KeySetFilter:Enabled` (all default `true` once the security tests pass, each independently switchable, so that result parity can be checked on/off and a dialect problem can be disabled without a release). Disabled behaves exactly as today.

#### 7.1.6 Dependencies and relation to the shared federation core

- **#123 and #124 build on #122**: conjunct splitting, the pushability rules, the per-table `TableFilterClause` and the audit/analysis fields come from #122. #123 additionally needs the compile-through-governed-path entry of the single-source engine. #124 additionally needs the two-phase staging order.
- **Shared federation core (D-2, PLAN-FED-01).** These features belong to the SQL-text strategy of the core and use `FederationGate`, error codes, provenance and the audit helper from WP-FED-01/02. They do not touch GraphQL.
- **WP-FED-03 (SQL federation hardening)** edits the same files (`CrossSourcePlanner.cs`, `FederatedStagingService.cs`, `FederatedDuckDbExecutionService.cs`): sequenced after it, and it already carries parameters and `MaxParallelSourceReads`, both prerequisites.
- **GraphQL splitter / WP-FED-06 (governed key lookup).** #124's batched `IN` and the key batching of `GovernedTreeSourceReader`/`KeyPredicate` are the same problem (distinct clear keys, bind limit aware batching, caps, injection safety, `NULL` never). Requirement: one shared `Federation/KeyBatching` component (key extraction, dedupe, dialect-bound batch split, `TableFilterClause` builder for `IN`), implemented once; WP-FED-06 consumes it for the tree path, the new SQL path consumes it for staging. This avoids two key-batch implementations with different guards.

#### 7.1.7 Acceptance criteria (from "Done when")

- **AC-15 (#122) Volume.** A cross-source query with selective pushable predicates on a table larger than `MaxStagedRowsPerTable` (few matching rows) succeeds; the same query without the predicates still fails with `ROW_LIMIT_EXCEEDED`.
- **AC-16 (#122) Parity.** For a test set covering `AND`, `OR`, `IN`, `NOT IN` with `NULL`, `LIKE`, `IS NULL`, inner and left joins, and a SQL Server source with case-insensitive collation and values with trailing spaces, the result is identical with pushdown on and off.
- **AC-17 (#122) Masks.** A predicate on a masked column, including an HMAC-masked one, is never pushed; row counts of a query filtering on such a column equal those without pushdown.
- **AC-18 (#122) Persons.** Two persons with different row filters on the same table get different, correct results.
- **AC-19 (#122/#123/#124) Analysis.** The #79 analysis lists pushed predicates per table, subtrees per source, and key-set joins with order and bound.
- **AC-20 (#123) Subtree.** A query joining five or more tables of one source with one table of another succeeds when the per-source joined result is small, and the same-source tables are read in one statement in their source; the result equals the same subtree sent as a single-source query (and equals the all-staged query where that fits the budget and keys are not collation-dependent); a window function inside a same-source CTE is computed in the source and the staged rows are the CTE's result; row filters and masks of every table inside a subtree apply as in a direct single-source query (two persons, different correct results); a masked subtree output cannot be a cross-source join key.
- **AC-21 (#124) Key set.** A join between a filtered small table (hundreds of rows) and a large table (more than `MaxStagedRowsPerTable`) on an equality key succeeds and reads only matching rows from the large table; identical result with key-set filtering on and off where both succeed; a key set above the bound falls back or fails with a stable code, nothing cut silently; a masked or HMAC join key prevents the key set and a test shows no masked key value was sent to the other source; row filters and masks of the large table apply to rows read through the key set as in a direct read; with a case-insensitive source and keys differing only by case no row is staged twice.
- **AC-22 (#125) Docs.** `f-data-02-governed-websql.md` no longer contradicts itself on cross-source; `f-data-03-duckdb-olap.md` states current staging behaviour (no query predicates before #122, cap and error). Phase 6.

#### 7.1.8 Size, risks, open questions

> **Owner decision 2026-10-16:** Q-8 decided — per-source opt-in `allow_key_set_import`, default off (see section 12).

- **Size.** #122: L (about 1,400 LOC production incl. pushability tables, 1,800 tests). #123: XL (about 1,300 LOC production, 1,500 tests; depends on a governed compile entry point). #124: L (about 700 LOC production, 1,000 tests). Detail in PLAN-FED-01 section 4.11 and 6 (WP-FED-11..14).
- **Risks.** R-9 a wrong pushability rule silently loses rows (mitigation: conservative table, on/off parity tests per dialect, kill switch). R-10 dialect drift (collation changes on a column after catalog ingestion; mitigation: pushability reads catalog type/collation info and treats unknown as not pushable). R-11 subtree semantics differ from the all-staged query (documented, source authoritative, owner accepts, Q-9). R-12 key-set amplification (bounds, cost). R-13 audit/analysis divergence from execution (one planner, analysis calls it).
- **Open owner questions.**

| ID | Question | PM recommendation |
|---|---|---|
| Q-8 | May clear key values of source A be sent to source B (query text and logs of B)? Per-source opt-in flag, or accepted globally for SQL sources as for #75 and Virtual Filters? | Per-source opt-in flag `allow_key_set_import` on the data source, default off (fail closed), SQL only. Without it the join still works but unfiltered |
| Q-9 | Accept that a same-source subtree uses the source's collation/semantics, i.e. a query can return different rows than the same query with all tables staged (documented)? | Yes, as stated in #123, with the documentation and the on/off switch |
| Q-10 | Which table bound applies inside a subtree (the single-source path has no explicit bound that we could find)? | Reuse `MaxTableCount` x 4 as a new `MaxTablesPerSubtree` (default 12), configurable |
| Q-11 | Is `status <> 'closed'` on a case-insensitive/PAD SPACE string column expected to be pushed (the issue example)? | No (silent row loss). Offer a per-column catalog hint `collation: binary` (or `exact_compare`) that enables it |
| Q-12 | WHERE conjunct on the null-supplying side of a left join (null-rejecting form, equals an inner join): push in a later step? | Later step, behind the same switch, after parity tests exist |
| Q-13 | Per-table hint name and place (`expected_rows` integer or `size_class` large/small) and who may set it (data owner)? | `expected_rows` on the table metadata, set by the data owner or admin, audited |

### 7.2 Federated parameters and co-located data sources (#131, #132)

*Phase 1 addition (Product Manager), verified against `5ae3c046`. Source: owner issues #131 and #132 and the owner comments on #122 ("parameterised cross-source queries are rejected today; see #131") and #123 ("#132 proposes co-located data sources so a same-source subtree may span several databases on one server"). Both belong to this track; #131 completes D-10, #132 extends #123.*

#### 7.2.1 Verified current behaviour

| Claim in the issues | Verified | Evidence |
|---|---|---|
| #131: client-named parameters are not usable in federated queries | **Confirmed, with a correction to the wording** | `docs/features/f-dialect-02-ast-sql-compiler.md:436` ("not supported ... fails closed"). In code the federated planner compiles the DuckDB statement with `ParameterSource.Empty` (`src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs:282`), i.e. request parameters are **not carried** (this PRD 2.1 row 16: parameterised endpoints fail; 7.1.1 row "All query predicates stay in DuckDB"). No dedicated rejection with a stable error code was found; the failure is a consequence of the missing values. The single-source path normalises and binds them (`GovernedSqlExecutor.cs:260-261`, `GovernedSqlExecutionService.NormalizeClientParameters`, binder `ICompiledSqlBinder.cs:196-207`) |
| #131: planner binds values as `$n` | Confirmed | `CrossSourcePlanner.cs:280-282` ("WP-X3: every query value is bound ($n)") |
| #131: #122 pushdown needs parameters | Confirmed | FR-25 of this PRD, PLAN-FED-01 4.3 and 4.11.2; `TableFilterClause` carries parameters (`IGatewayExecutionService.cs:66`) |
| #132: no co-location concept exists | **Confirmed** | no `CoLocation`/`co-locat` reference in `src/` or `docs/plans/`; a data source is one configured connection (`SqlDataSourceOptions.Connections`, `DataSourceConnectionOptions`: `Provider`, `ConnectionString`, session options, `src/Autheris.Domain/Options/Storage/DatabaseOptions.cs`); a table belongs to one source via `domain.schema.table` (`TableMetadata.Identifier`); sessions are pinned per source (`SqlSourceUnavailableException`, `ISqlConnectionFactory.cs`) |
| #132: a same-source subtree is the extension point | Confirmed (design only) | #123 is not implemented yet (PLAN-FED-01 4.11.5, WP-FED-13). The grouping key there is "data source" |
| #132: qualified database names are available in the compiler | **Unverified, spike needed** | the governed compiler addresses tables as `schema.table` inside one pinned session; whether `TableAccessTarget` and the SQL Server emitter can emit a `[database].[schema].[table]` form, and that a client three-part name is rejected, must be proven in Phase 2 (see FR-41) |

#### 7.2.2 Qualification and deduplication

- **#131 is mostly a completion of what this PRD already contains.** D-10, FR-5 (WP-FED-03, PLAN-FED-01 4.3) already require that the federated path binds client parameters in DuckDB; FR-25 already requires that bound values and list values are pushed as bound values. **Net new from #131:** (a) list parameters (depends on #128), (b) one-time type validation before any source read, (c) the guarantee that the pushed value and the DuckDB value are the same typed value, (d) parameter names in the audit of the query and of each source read, (e) correction of the f-dialect-02 limitation text (Phase 6). Verdict: **approved, small, split off as `WP-FED-15`**; the scalar part stays in WP-FED-03.
- **#132 is a new feature** on top of #123 (it changes the grouping key and the name qualification). It is not a duplicate of REQ-GOV-107 or of per-source configuration (BL-05); BL-05's per-source settings must not create a second place for co-location flags (the declaration lives next to the other connection options of the source, one place).
- Neither issue changes the consumer inventory (section 2): both affect only the SQL-text strategy of the shared core.

#### 7.2.3 Benchmark (brief)

| Product | Practice | Consequence |
|---|---|---|
| **Trino** | One catalog per database (SQL Server and PostgreSQL connectors address one database); a join across catalogs is executed in Trino; parameters are bound per query for all connectors | Same architecture as the staging path; #131 matches the prepared-statement model (`EXECUTE ... USING`) |
| **SQL Server** | Three-part names across databases of one instance in one statement; permission checks per object; cross-database ownership chaining is optional (`DB_CHAINING`) and a known privilege-escalation lever | #132 uses explicit permissions of the service account on each database; chaining must not be required or enabled |
| **PostgreSQL** | A connection is bound to one database; cross-database access only through `dblink`/`postgres_fdw` | Not supported for co-location in the first step (rejected at startup) |
| **Snowflake / Databricks (Unity)** | Three-level names (database/catalog, schema, table), cross-database queries natively with grants | Later candidates, as dialect support appears |
| **Dremio / Calcite JDBC adapter** | Push joins into a JDBC convention when the tables share a connection | Co-location is the explicit, administrator-declared version of "same convention" |
| **Anti-patterns rejected** | (1) detecting co-location implicitly from host names (aliases, listeners, DNS: a wrong guess runs a query under the wrong identity); (2) one broad service account silently serving every database; (3) relying on ownership chaining; (4) splicing parameter values to push them | Explicit declaration, verified at startup and at connect time; per-table authorization; explicit permissions; bound values |

#### 7.2.4 Gatekeeper review

**#131 (parameters).**
- **Bound, never spliced, in every engine.** DuckDB statement: `$n` (existing). Source reads for pushed predicates: bound typed parameters built by the pushdown builder (existing SEC-FED-22/27). A test with quotes, comment markers and statement separators in values (scalar and list) asserts no value in any generated text, log or audit.
- **Same value, same typed value.** The value bound in DuckDB and in a source statement is the same validated value; the source parameter is typed from the **column's catalog type** (SEC-FED-22), so the client's JSON type must be convertible without loss to that class; otherwise the conjunct is **not pushed** (superset rule, FR-22: a literal whose type class differs is not pushed) and DuckDB filters alone. A client value is never coerced differently in the two engines.
- **One validation, before any read.** Names, declared types (declared query headers) or inferred types (ad hoc WebSQL), list element types and bounds (#128 limits) are validated once in planning; a violation refuses the request before the first source read, with the stable code of #128 or `FEDERATION_PARAMETER_INVALID` (name to fix in Phase 2).
- **List parameters and budgets.** A list parameter in DuckDB uses the #128 expansion with the same `MaxListParameterValues`; a pushed list is pushed only when it fits `min(dialect bind budget minus policy parameters, MaxKeySetKeys-like bound)`; otherwise it is not pushed (never truncated), DuckDB applies it.
- **No new oracle.** Parameter validation errors are about the caller's own input; they never depend on governed data. A parameter that only matters for a table the caller may not read must not turn a `FEDERATION_*` denial into a different error (authorization of all tables precedes parameter-dependent errors, as in D-8).
- **Name collisions.** Client names cannot collide with gateway-generated names (pushdown `p_*`, policy, tenant, staging `s0..`): existing reserved-prefix rules of the compiler apply; a collision fails closed (SEC-ADG-02).
- **Audit.** Parameter **names** (and for lists the element count) in the query record and in each source read record; values only where the existing WebSQL audit configuration already records them for single-source queries, never in per-source-read records (Q-15).

**#132 (co-located sources).**
- **No authority gain.** The statement may reference only tables for which the gateway made an allow decision for this caller and this request; after compile, the final AST is checked: every table reference is in the authorized set of `(source, database, schema, table)` entries, nothing else (defence in depth against a qualification bug). A client-supplied two-, three- or four-part name never selects a database: names resolve through the catalog (`domain.schema.table`) to the physical database taken from the **data source configuration**, identifier-validated and dialect-quoted.
- **Per-table governance unchanged.** Consents, row filters (including Virtual Filters), masks, tenant predicate, `AstSecurityVisitor` guards, masked-column guardrails and the SEC-FED-24/25 classification apply per table exactly as in a direct read of that table; the shared service account never widens what the caller sees. Correlated row filters whose dependent table lies in another member database are **not** supported in the first step (refused at definition or planning, fail closed), so a policy never crosses a database boundary implicitly.
- **Declaration is explicit, symmetric and verified.** A named co-location group is set on **every** member (`CoLocationGroup`). A one-sided or mismatching declaration, or a group of one, aborts startup (fail closed). Startup validation requires equal `Provider` (SQL Server only in the first step), equal normalised server (host, port or instance), equal credential identity (authentication mode, login or Windows identity), equal session options (`ReadUncommitted`, command-timeout class), and no `Delegated*` enforcement mode (per-user identity at the source breaks the "one service account" premise) and no HTTP or lakehouse source. A **connect-time verification** compares the actual server identity (`@@SERVERNAME`/instance) and current database of each member; a mismatch disables co-location for that group at runtime with a logged error code (the query then runs through federated staging, which is always safe, never through a wrong-identity statement).
- **Owner consent.** Joining two owners' data in one statement under one account is a decision of both data owners (Q-16, four-eyes in the style of Q-6); because data sources are configuration in this release, the declaration is an operator-deployed configuration change that must be accompanied by the owners' approval, and the audit of the startup validation records the group and members.
- **No ownership chaining, explicit permissions.** The service account needs `SELECT` on the referenced objects in each member database through its own permissions (least privilege, no write); `DB_CHAINING` is neither required nor assumed; a permission error maps to a stable `FEDERATION_SOURCE_ERROR` without database text.
- **Audit per table and per statement.** One statement-level record lists all tables, sources and the co-location group; each table also gets its own access-audit entry with its own owner/source identity (so each owner's audit trail shows the read); audit failure denies the request (SR-8).
- **Limits.** The co-located statement counts every member for per-source concurrency leases, per-user-and-table read limits and circuit breakers (the NTLM login breaker of any member that is open prevents the statement; fallback to staging does not help because that member is unavailable, so the request fails with the stable source error); budgets as for a subtree (`MaxTablesPerSubtree`, row and byte caps of the staged result).
- **Interplay with #122/#124.** Pushdown inside a co-located subtree is plain compile-time predicate placement (as for any single-source statement). **Key-set import (#124, Q-8) is not needed between members of one group and is not a consequence of co-location**: it still needs `allow_key_set_import` across different groups or servers. Co-location never relaxes SEC-FED-26.
- **Semantics.** The server's collation, comparison and functions are authoritative for the whole statement, also across databases with different collations (SQL Server cross-database collation conflicts can raise errors: mapped to a stable code, documented; Q-9 applies).

#### 7.2.5 Functional requirements

- **FR-35 Federated parameters (#131).** `ParameterOrigin.ClientNamed` parameters, scalar and (after #128) list, are carried into the federated path: the planner receives the validated client values and compiles the DuckDB statement with a `ParameterSource` that binds them as `$n`. Extends FR-5/D-10 and WP-FED-03.
- **FR-36 Single validation (#131).** Names, types, list element types and bounds are validated once before any source read; the types are available to the pushdown selector.
- **FR-37 Pushed parameters (#131).** A pushable conjunct over a parameter pushes the same validated value as a bound, column-typed parameter into the source statement (FR-22/FR-25); when the type class differs or a list exceeds the source bind budget, the conjunct is not pushed. Result with pushdown on and off is identical (AC-16 extended).
- **FR-38 Audit and analysis (#131).** Query and per-source-read audit records list parameter names and list element counts; the #79 analysis lists parameters and shows for each pushed predicate which parameter feeds it.
- **FR-39 Co-location declaration (#132).** `CoLocationGroup` (string, optional) on `DataSourceConnectionOptions`; validated per 7.2.4; no declaration means separate sources (today's behaviour).
- **FR-40 Co-located subtrees (#132).** The #123 grouping key becomes `CoLocationGroup ?? data source`; a subtree may span the members of one group and is compiled and executed as **one statement** on the server through the governed single-source path with the decisions already made per table (FR-27, FR-30 apply to every member table).
- **FR-41 Qualification (#132).** The emitter qualifies member tables with the database name from the data source configuration (`[db].[schema].[table]` on SQL Server) only when the subtree spans more than one database; in-database references stay as today. A client-written qualified name is rejected exactly as today (qualified names only through the catalog).
- **FR-42 Verification and fallback (#132).** Startup validation and connect-time server-identity verification per 7.2.4; failure disables co-location for the group (staging path), with a stable code in the log/analysis, never a wrong-identity statement.
- **FR-43 Audit and analysis (#132).** Per 7.2.4; the #79 analysis shows, per subtree, `colocated: true`, the group and all member tables.
- **FR-44 Switch.** `WebSql:CrossSource:CoLocation:Enabled` (default `false` until the Phase 3 tests pass); disabled behaves exactly as today even when groups are declared (startup validation still runs).
- **FR-45 Provider support.** SQL Server in the first step; PostgreSQL, Oracle, Databricks, Snowflake groups are rejected at startup with a clear message (Q-19).

#### 7.2.6 Acceptance criteria

- **AC-23 (#131) Scalar and list.** A declared cross-source query with a scalar parameter and with a list parameter returns the correct rows (REST, WebSQL, async job, Flight SQL).
- **AC-24 (#131) Pushdown parity.** With #122 the parameterised predicate is pushed (assert via the source-read audit/spy executor) and the result equals the run without pushdown; a parameter whose type class differs from the column is not pushed and the result is still correct.
- **AC-25 (#131) Injection.** Values with quotes and comment markers (scalar and list elements) never appear in generated SQL text in DuckDB or in any source, nor in logs or audit.
- **AC-26 (#131) Validation before reads.** A wrongly typed parameter, a list above the bound and a missing required parameter are refused before any source read (spy executor shows zero reads); names appear in the audit, values do not.
- **AC-27 (#132) One statement.** A join between tables of two co-located SQL Server databases runs as one statement on the server (spy shows one statement with two qualified names) and returns the same rows as the federated path where that fits the budget.
- **AC-28 (#132) Declaration required.** Without the declaration on both sources the same query uses federated staging; a one-sided or mismatching declaration aborts startup; a group on different servers (identity mismatch at connect time) falls back to staging with the stable log code.
- **AC-29 (#132) Per-table governance.** Two persons with different row filters on one of the tables get different, correct results; a masked column stays masked and cannot be a join key; a table for which the caller has no consent makes the request fail with the stable denial even though the shared account could read it; a client-written three-part name is rejected.
- **AC-30 (#132) Audit.** The statement record lists all tables and sources; every table has its own access-audit entry; the analysis shows `colocated: true`.
- **AC-31 (#132) No authority gain.** A forged qualification attempt (name of a member database in a client name, linked-server four-part name) never reaches a table outside the authorized set; the post-compile reference check test fails closed on an injected unauthorized table reference.

#### 7.2.7 Dependencies and relation to the shared core

- **#131** depends on WP-FED-03 (parameter carrying), WP-FED-11 (pushdown builder) and on **REQ-SQL-128** for list parameters ([2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md), section 2). Without #128 the scalar part still ships; list parameters are refused with the stable parameter code.
- **#132** depends on WP-FED-13 (same-source subtrees, grouping, governed compile entry), WP-FED-12 (options, audit, analysis) and on the qualification spike (FR-41). It touches `DatabaseOptions.cs` (data source options), which REQ-GOV-107 and `WP-SC-15` also edit: land after 107 and coordinate with SC-15 (scale plan file-ownership table).
- **#132 and `WP-SC-15` (delegated enforcement):** a source in a `Delegated*` mode can never be a member (stated in FR-39); the registry reads the enforcement mode once SC-15 exists and rejects the combination at startup.

#### 7.2.8 Size, risks, open questions

- **Size.** #131: S-M (about 450 LOC production, 700 tests; `WP-FED-15`). #132: L (about 1,200 LOC production, 1,500 tests; `WP-FED-16`; the qualification spike may add emitter work in `TrinoSqlEngine`).
- **Risks.** R-14 a qualification bug lets a statement reach an unauthorized object (post-compile table-reference check, SEC-FED placeholders). R-15 co-location declared for sources that are not on one server (verification at connect time, fallback). R-16 cross-database collation conflicts (stable code, documentation). R-17 the type of a client value pushed to a source differs from DuckDB's interpretation (typed from the column, not pushed on mismatch). R-18 owners did not intend one account to serve both databases (Q-16, explicit declaration, startup audit).
- **Open owner questions.**

| ID | Question | PM recommendation |
|---|---|---|
| Q-14 | Maximum list size in federated queries: the #128 limit, or a lower limit for pushed lists? | #128 limit for the DuckDB side; a pushed list is capped by the source bind budget and not pushed above it (DuckDB still filters) |
| Q-15 | Audit of parameter values in the query record: "values as configured for audit today" (issue). Which configuration is meant, and are values ever allowed in per-source-read records? | Names and counts everywhere; values only in the existing single-source-equivalent query record when that option is on; never in per-source-read records |
| Q-16 | Who must approve a co-location group (both data owners, four-eyes)? | Both owners approve; recorded with the configuration change; startup audit entry |
| Q-17 | Declare co-location only in configuration, or also through admin API/MCP? | Configuration only in the first step (data sources are configuration in this release) |
| Q-18 | Should the per-table concurrency/cost limits of every member count (yes in this PRD), or only of the first source? | Every member counts |
| Q-19 | Providers beyond SQL Server (Snowflake, Databricks three-level names; PostgreSQL schemas inside one database as sources)? | Not in the first step; PostgreSQL different-database is impossible, same-database different-schema is a separate (simpler) request |
| Q-20 | On a failed connect-time verification: fall back silently to staging (recommended) or fail the request? | Fall back to staging with a stable log code and an analysis flag; fail only if staging is not possible either |

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

> **Owner decision 2026-10-16 (Q-1, Q-6, Q-8, decided):**
> - **Q-1:** `CrossSource:Enabled` **stays off by default** (opt-in master switch).
> - **Q-6:** `AllowCrossDataSource` requires approval by **both table owners (four-eyes)**. The same rule applies to co-location groups (Q-16). Rationale: linking, for example, telematics with finance data across two sources is sensitive.
> - **Q-8:** sending clear key values of source A to source B requires the **per-source opt-in flag `allow_key_set_import`, default off** (fail closed, SQL only). Rationale: data flow between sources must be a deliberate choice.
>
> All other questions remain open with their recommendation as default.

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
| **INC-6 Federated query performance** | Predicate pushdown (#122), same-source subtrees (#123), key-set filters (#124), `WP-FED-11..14`; after INC-2 (shares the staging files), independent of INC-3/4 | Cross-source queries that fit the budget after filtering |
| **INC-7 Federated parameters and co-located sources** | Federated parameter contract and list parameters (#131, `WP-FED-15`, needs REQ-SQL-128), co-located data sources (#132, `WP-FED-16`); after INC-6 (`WP-FED-13`) and #128 | Parameterised declared queries on the federated path; one-statement joins across databases of one server |
| **INC-5 Parity and documentation** | Integration matrix, Phase 6 docs | Regression protection |

## 14. Lifecycle Hand-off

- **Phase 2 (`csharp-architect`):** verify this PRD against code, fix the architecture of the shared core, work packages with disjoint files and sequencing (`2026-10-16-plan-cross-source-all-consumers.md`).
- **Phase 3 (`csharp-security-expert`):** threat model and binding conditions SEC-FED-xx, mandatory security tests.
- **Phase 4-6:** TDD implementation, review, feature documentation (`docs/features/f-gov-19-...` rewrite plus a new federation feature doc; number confirmed in Phase 6), master plan update.

## 15. Changelog

- 2026-10-16: Initial PRD (Phase 1), verified against `70b75e27`. The false "COMPLETED & VERIFIED" status of #75 is corrected in `features.md`, `impl-features-analytics.md`, `00-implementation-roadmap.md` and the master plan.
- 2026-10-16: Section 7.1 added (Phase 1 for issues #122, #123, #124; #125 registered in 2.1). FR-20..FR-34, AC-15..AC-22, Q-8..Q-13, INC-6.
- 2026-10-16: Section 7.2 added (Phase 1 for issues #131 and #132). FR-35..FR-45, AC-23..AC-31, Q-14..Q-20, R-14..R-18, INC-7. Finding: #131 mostly completes D-10/FR-5/FR-25 (scalar parameters are already WP-FED-03); net new are list parameters, single validation, pushed-value equality and audit names.

# Plan: PLAN-FED-01 - Cross-Source Access for All Consumers (Architecture and Work Packages)

**Document ID:** `PLAN-FED-01`
**Date:** 2026-10-16
**Status:** PLAN READY (Phase 2 complete); next: Phase 3 security review (`csharp-security-expert`)
**Author:** Solution Architect (Phase 2, `csharp-architect` role)
**PRD:** [2026-10-16-req-cross-source-all-consumers.md](2026-10-16-req-cross-source-all-consumers.md) (`REQ-FED-01`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Branch:** `feat/cross-source-all-consumers` (base `main` `70b75e27`)

**Binding constraints:** `.NET 10`, C# 13; no upgrade migrations (baseline `0001` DDL edited in place for SQLite, PostgreSQL, SQL Server, plus shape snapshots under `src/Autheris.Infrastructure/Persistence/Migrations/Shape/`); least privilege on sources; fail closed; all text English.

---

## 1. PRD Verification Against Code

The PRD claims were re-checked in the worktree. Result: confirmed, with these refinements that shape the design.

| PRD claim | Verified | Design consequence |
|---|---|---|
| Resolver unwired, reader is a stub | `DataApiServiceExtensions.cs:25-26` registers `ICrossSourceRowReader -> InMemoryCrossSourceRowReader` and the resolver; no other injection of `ICrossSourceRelationResolver` exists in `src/`. `TableRelation.AllowCrossDataSource` is `init`-only in `TableModels.cs:293` and never mapped. | Resolver is rewritten as a thin planner; the reader is replaced, not wrapped. |
| Resolver re-implements governance on raw rows | `CrossSourceRelationResolver.Helpers.cs:232-267` applies tenant (skipped on `NULL` tenant value), `CombinedRowFilterSql`, `MandatoryRowPredicateSql` with `StreamingRowFilterAstEvaluator` | The in-gateway governance code is deleted. Source-2 rows come from an already governed statement. |
| `GovernedTreeQueryService` is single-source | `GovernedTreeQueryService.cs:208-235` resolves access for all tables first, then enforces one `SourceName` and `DataSourceType.Sql` | The service stays unchanged for single-source trees; a decorator splits the tree before it reaches the service. The existing checks remain as the defensive backstop. |
| Tree model supports a key predicate | `TreeFilterOperator.In`/`Eq`, `TreeAndFilter`, `TreeOrFilter` exist (`TreeQueryModel.cs:8-35`); `TreeQueryNode.Limit` is "per parent row" for nested levels | A child segment is just another root `TreeQueryNode` with an `In` (or OR-of-AND `Eq` for composite keys) predicate. No compiler change needed for SQL. |
| Federation transport gate is in one place | `FederatedDuckDbExecutionService.cs:245-252`; `ValidateAsync` (`:200-232`) has no gate | New `FederationGate` called from both. |
| SQL-text channels share one pipeline | `GovernedSqlExecutionService.cs:104-155` -> `CrossSourceRoutingException` -> lazy `IFederatedQueryExecutionService` | Kept as the SQL federation engine. |
| OLAP is separate | `DuckDbOlapEndpoints.cs`, `OlapStagingService.cs:155-163`, `DuckDbOlapEngine.cs:405-470` | Becomes a facade over `IGovernedSqlExecutionService` (D-7). |

**Items to verify early in Phase 4 (spikes inside the owning work package):**
1. `GovernedAggregationService` executor-versus-pushdown selection in production DI (`GatewaySqlEngineServiceExtensions.cs:65`); outcome goes to a separate fix, not this feature (PRD 6.3).
2. Whether `FederatedStagingService` exposes a reusable governed single-table read (needed by WP-FED-09 for `HttpDeclarative` key lookup); if not, WP-FED-09 extracts it (the per-table block at `FederatedStagingService.cs:63-266`) behind `IGovernedSourceReader`.
3. Parameter binding in the federated path (`CrossSourcePlanner.cs:282` uses `ParameterSource.Empty`): confirm the compiler can emit DuckDB parameters from `ParameterSource` carrying request parameters.

---

## 2. Architecture Decision

### 2.1 Chosen architecture (five lines)

1. **One federation contract, two execution strategies.** A shared `Federation` module (gate, options, error codes, provenance, audit helper) is used by every consumer; SQL-text consumers execute through the existing SQL federation (router, planner, governed staging, DuckDB), object consumers (GraphQL) through a tree splitter with batched, governed key lookup.
2. **GraphQL:** `FederatedTreeQueryService` decorates `IGovernedTreeQueryService`: it partitions the `TreeQueryNode` tree at cross-source `TreeRelationNode`s, runs the root segment through the inner service, extracts distinct clear keys from the page, runs each child segment as a normal governed single-source tree with a key predicate (batches), and stitches the JSON in the gateway.
3. **No second policy engine.** Every source read, SQL or HTTP, goes through the governed pipeline (`ITableAccessResolver` decision + governed compile or `GovernedConnectorReader`); `CrossSourceRelationResolver` shrinks to a planner/guard (clear keys, limits, sort guard, audit, provenance) and its in-memory re-filtering is deleted.
4. **OLAP becomes a facade** over `IGovernedSqlExecutionService` so there is exactly one compiler, router and guardrail set for client SQL.
5. **Opt-in:** master switch `WebSql:CrossSource:Enabled=false` default; `AllowedTransports` default becomes all registered transports when enabled, validated at startup; relations need `AllowCrossDataSource` persisted per relation.

### 2.2 Alternatives considered

| Alternative | Verdict |
|---|---|
| Translate the whole GraphQL tree to SQL text and run it on the DuckDB federation | Rejected: needs full-table or large staging to keep root pagination exact, loses per-source tree compilation, and masked-key guardrails would be re-derived. Kept only as the model for SQL-text consumers. |
| Keep the resolver as is and only inject a real reader | Rejected: the resolver's gateway-side governance diverges from the governed compiler (tenant `NULL` bypass, string evaluator, no federated virtual filters). |
| Per-consumer implementations (REST expand, OData `$expand`, GraphQL) | Rejected by PRD D-2; non-goal Q-5. |
| Cache source-2 rows across requests | Rejected (PRD 4.1.6). |

### 2.3 Component map

```
Consumers
  SQL-text: WebSQL, Trino, REST/MCP data (HTTP table), Arrow export, Flight SQL, async jobs, SQL endpoints, OLAP facade
        |  GovernedSqlExecutionService -> (CrossSourceRoutingException) -> FederatedDuckDbExecutionService
        |       uses: FederationGate, FederationErrors, FederationProvenance   [WP-01, WP-02, WP-03]
  Object: GraphQL
        |  FederatedTreeQueryService (new, decorator)                           [WP-08]
        |       -> IGovernedTreeQueryService (unchanged, per source segment)
        |       -> CrossSourceRelationResolver (planner/guard)                   [WP-06]
        |       -> IGovernedSourceReader: SQL = tree service, HttpDeclarative = GovernedConnectorReader   [WP-06, WP-09]
        |       uses: FederationGate, FederationErrors, FederationProvenance
  Declaration: TABLE_RELATIONS.allow_cross_data_source (baseline DDL), admin API/MCP [WP-07]
```

---

## 3. Contracts

All new types live in `Autheris.Application.Federation` (`src/Autheris.Application/Federation/`) unless stated. Interfaces are fixed here so work packages can proceed in parallel.

```csharp
// WP-FED-01 -------------------------------------------------------------
public static class FederationTransports            // single source of truth, extends SqlTransports
{
    public const string GraphQl = "GraphQL";
    public const string Olap = "Olap";
    public static IReadOnlyList<string> All { get; }         // all registered names
    public static IReadOnlyList<string> DefaultAllowed(bool asyncJobProtected);
}

public interface IFederationGate
{
    // Throws FederationException(FEDERATION_DISABLED / FEDERATION_TRANSPORT_NOT_ALLOWED).
    // Callers MUST call it only after table authorization succeeded (generic denial before).
    void EnsureAllowed(string? transport);
    bool IsEnabledFor(string? transport);                      // schema gating, no throw
}

// WP-FED-02 -------------------------------------------------------------
public static class FederationErrorCodes { /* FEDERATION_DISABLED, ..._TRANSPORT_NOT_ALLOWED, ..._UNSUPPORTED_SOURCE,
                                              ..._LIMIT_EXCEEDED, ..._SORT_NOT_SUPPORTED, ..._KEY_NOT_CLEAR, ..._SOURCE_ERROR */ }
public sealed class FederationException : GatewaySecurityException { public string Code { get; } /* fixed message per code */ }

public sealed record FederationProvenance(IReadOnlyList<FederationSourceRead> Reads);
public sealed record FederationSourceRead(string Source, DateTimeOffset ReadAtUtc, int Rows, int Batches);

// WP-FED-06 -------------------------------------------------------------
public interface IGovernedSourceReader                      // replaces ICrossSourceRowReader
{
    // Returns rows that already passed governance (decision, tenant, row filters, masking) of the caller.
    Task<GovernedRowSet> ReadByKeysAsync(GovernedKeyLookup lookup, CancellationToken ct);
}
public sealed record GovernedKeyLookup(ClaimsPrincipal? Principal, IReadOnlyDictionary<string,string[]>? Headers,
    string OperationId, TableIdentifier Table, IReadOnlyList<string> SelectColumns, IReadOnlyList<string> KeyColumns,
    IReadOnlyList<CompositeKey> Keys, IReadOnlyList<TreeOrder> OrderBy, int MaxRows);
public sealed record GovernedRowSet(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool Truncated);

// WP-FED-08 -------------------------------------------------------------
public sealed class FederatedTreeQueryService : IGovernedTreeQueryService   // decorator, registered over the inner service
```

Behavioural contracts:

- `Truncated == true` from a key lookup always becomes `FEDERATION_LIMIT_EXCEEDED` (never a partial result).
- The reader never receives or returns rows that were not governed; there is no raw read API in the module.
- `IFederationGate.EnsureAllowed` is the only place that reads `CrossSourceOptions.Enabled` and `AllowedTransports`.

---

## 4. Detailed Design

### 4.1 Federation gate, options and startup validation (WP-FED-01)

- `CrossSourceOptions.AllowedTransports` stays nullable; effective list = configured list, else `FederationTransports.DefaultAllowed(...)`: `WebSql, Trino, Rest, Mcp, ArrowExport, FlightSql, SqlEndpoints, GraphQl, Olap`, plus `AsyncJob` only when `CrossSource.ProtectedStagingEnabled` is true (set by WP-04 once at-rest protection is active; default true after WP-04 lands, so the feature flag exists only to sequence delivery and is removed in Phase 6 if redundant).
- `IValidateOptions<CrossSourceOptions>`: unknown transport names abort startup; empty list is allowed (deny all); `MaxBatchSize`, `MaxBatches`, `MaxNestedRows`, `MaxCrossSourceFilterKeys` ranges; removes `MaxMemory`, `MaxTempDirectorySize` (PRD D-11).
- `FederationGate` is registered as a singleton; `FederatedDuckDbExecutionService.ExecuteFederatedInternalAsync` and `ValidateAsync` (both, closing the Flight `/info` gap) and the GraphQL decorator call it. Because `FederatedDuckDbExecutionService` is also owned by WP-03, WP-01 only creates the gate and options; WP-03 wires it.
- Options binding: `CrossSourceRelationOptions` is bound under `WebSql:CrossSource:Relations` (`MaxBatchSize` 500, `MaxBatches` 20, `MaxNestedRows` 5000, `MaxCrossSourceFilterKeys` 1000, `CostPerBatch`, `CostPerRow`).

### 4.2 Error contract and provenance (WP-FED-02)

- `SqlCompilerErrorMapper` and the endpoint mappers (`WebSqlEndpoints.Governance.cs`, `DatasetDataEndpoints.cs`, `ArrowExportEndpoints.cs`, `ArrowFlightSqlEndpoints.cs`, `SqlEndpointRoutes.cs`, `WebSqlStatementManager.cs`, `GatewayMcpQueryExecutor.cs`, async job failure code, GraphQL `ErrorFilter`) learn `FederationException`: HTTP 403 for disabled/transport/unsupported source, 422 for limit/sort/key errors (400 where the transport already uses 400 for limits), `error.code` = the stable code, fixed message. Trino: `errorName` = code, state `FAILED`.
- Ordering rule (anti-oracle): the router and `AuthorizeAllTablesAsync` run before `FederationGate.EnsureAllowed` signals a specific code; when authorization fails the generic `SQL_POLICY_DENIED`/`SQL_POLICY_VIOLATION` applies. Today the gate is checked first with the same generic text; the new behaviour is: gate failure is evaluated after authorization and then returns the specific code. Rationale: a caller who can read every table learns nothing new from "federation disabled"; a caller who cannot read a table sees only the generic denial.
- Provenance: `FederationProvenance` is attached to the result (`GovernedSqlQueryResult`/job result/GraphQL `extensions["federation"]`/HTTP header `X-Autheris-Federation: sources=a,b; rows=...` (names and counts only)/Trino `stats`/MCP result field). Source names are only included for tables the caller was authorized for.

### 4.3 SQL federation hardening (WP-FED-03)

- Wire `FederationGate` into `ExecuteFederatedInternalAsync` and `ValidateAsync`; async job submit calls `ValidateAsync` (job submit validation) so an impossible job is rejected at submit (`FEDERATION_*`).
- Parameters (PRD D-10): `CrossSourcePlanner.Plan` receives `request.Parameters` and compiles DuckDB SQL with a `ParameterSource` that carries them; staging reads stay parameter-free (filters pushed to sources are already compiled values or are not pushed).
- `truncated` and row limit: the federated result is wrapped in the same `RowLimitedDataReader` as the single-source path with `WebSql.DefaultMaxRows`/`MaxAllowedRows`/transport limits; the engine cap `DuckDbOlap.MaxResultRows` remains an upper bound.
- `MaxParallelSourceReads`: implemented in `FederatedStagingService` with a `SemaphoreSlim` and `Task.WhenAll` (audit records stay ordered by source index). Default 4, bounded by `MaxTableCount`.
- Provenance and `FEDERATION_SOURCE_ERROR` (upstream failure, stable code, no message).

### 4.4 Staging at rest (WP-FED-04)

- `DuckDbOlapEngine`: spill directory created under a configurable root (`DuckDbOlap:TempDirectory`, default under the application data directory, not `Path.GetTempPath()`), mode `0700` on Unix and an ACL restricted to the service account on Windows; DuckDB `temp_file_encryption=true` when the bundled DuckDB version supports it (spike), otherwise `max_temp_directory_size=0` (no spill) with documented memory bound. Directory deleted in `finally`; delete failure logs a warning with the stable code `FEDERATION_CLEANUP_FAILED` and increments a metric.
- `AsyncQueryJobBackgroundWorker`: result files encrypted with an authenticated envelope (AES-GCM, per-job random data key wrapped by the existing data-protection or key-management service already used for the CDC stream encryption, see #99), owner-only file permissions, tenant partitioned directory, deletion on expiry; the download endpoint decrypts after the owner/tenant check. Sets `CrossSource.ProtectedStagingEnabled = true` effective.

### 4.5 OLAP facade (WP-FED-05)

- `POST /api/v1/olap/query {sql, tableNames[], limit}` keeps its route and body. Handler builds a `GovernedSqlQueryRequest(sql, Transport: FederationTransports.Olap, RowLimit: limit)` and calls `IGovernedSqlExecutionService`; the SQL dialect is the gateway's SQL (same as WebSQL), not raw DuckDB SQL. `tableNames` is ignored with response header `Warning: 299 - "tableNames is deprecated"`. Single-source SQL runs single-source; multi-source routes to the federation (gate applies).
- `OlapStagingService` and the client-SQL validation in `DuckDbOlapEngine.ValidateUserSql` are no longer reachable from the endpoint; they are kept only if still referenced by the federated executor (Phase 4 removes dead code, Phase 5 reviews). The endpoint now requires authorization (`RequireAuthorization()`), removing the inline identity check except under the Quickstart profile.

### 4.6 Governed source reader and resolver rewrite (WP-FED-06)

- `IGovernedSourceReader` implementation `GovernedTreeSourceReader`: for a SQL table builds a `TreeQueryNode(table, selectColumns)` with `Where = KeyPredicate(keyColumns, keys)` and `Limit = MaxRows + 1`, runs it through the inner `IGovernedTreeQueryService` (same `operationId` so per-operation access memo and audit-once logic is reused), converts the JSON array to rows, sets `Truncated` when `MaxRows + 1` rows came back. `KeyPredicate`: single column -> `In`; composite -> `Or(And(Eq...))` bounded by `MaxBatchSize`; `NULL` keys never produced (`TryExtractKey` rejects them). Ordering = declared key order (F-GOV-21) merged with requested order.
- Because the inner service compiles the statement with tenant predicate, row filters, masks and federated virtual filters of the child table, the child side of the join is governed by the same engine as every other GraphQL read; `PassesTargetGovernance` is deleted. HTTP tables use the reader of WP-FED-09.
- `CrossSourceRelationResolver` after the rewrite:
  - `IsCrossDataSource(parent, child)` unchanged (keeps `DataSourceType`, `SourceName`, `Domain` comparison).
  - `PlanRelation(request)`: checks `AllowCrossDataSource`, resolves parent and child access via `ITableAccessResolver` (memoised by operation), requires **both join key columns Clear** (not masked, not HMAC-pseudonymised, not excluded) via the decisions, rejects sort (`FEDERATION_SORT_NOT_SUPPORTED`), extracts distinct non-null keys from the parent page, batches them, calls the reader, groups by child key, enforces `MaxBatches` and `MaxNestedRows` (cumulative, `FEDERATION_LIMIT_EXCEEDED`), applies per-parent `Limit` for 1:N after ordering (ordering is the reader's), audits per source read (event `WebSqlCrossSourceSourceRead`, `path=cross_source_relation`, no row data), returns `FederationSourceRead`.
  - `ResolveCrossSourceFilterKeysAsync` (WP-FED-09) becomes the semi-join helper: read child keys with the filter via the reader (`MaxRows = MaxCrossSourceFilterKeys + 1`), fail closed above the limit.
  - Cost: `CostPerBatch`/`CostPerRow` are fed into the existing cost/quota middleware (`QueryCost` returned to the GraphQL cost accounting) instead of being returned and ignored.
- DI: `ICrossSourceRowReader` and `InMemoryCrossSourceRowReader` leave production DI; the in-memory reader moves to `tests/` as a fake. `DataApiServiceExtensions.cs` registrations updated (WP-06 owns the change; no other package touches that file).

### 4.7 Relation declaration (WP-FED-07)

- `TABLE_RELATIONS` (name per existing baseline) gains `allow_cross_data_source` (bit/boolean, default 0) in the three baseline DDLs and shape snapshots; `TableRelation.AllowCrossDataSource` mapped in all three repositories (SQLite, PostgreSQL, SQL Server) and in the catalog cache.
- Admin: REST `PUT /api/v1/catalog/relations/{id}/cross-source` (`GovernanceAdmin`) and MCP admin tool through the existing two-phase plan/apply with TOTP (F-AI-13); the change requires approval by both table owners when owners are defined (PRD Q-6, default: four-eyes flow), writes audit before/after, bumps the policy epoch of both tables, invalidates the schema cache.
- Catalog ingestion upserts must not overwrite the flag (the flag is stored on the relation row written by admin, not by discovery; discovery upsert keeps the existing value).

### 4.8 GraphQL splitter and schema gating (WP-FED-08)

- **Schema gating (`CatalogSchemaModel`)**: `AddForward/Reverse` relation fields are emitted only if `!IsCrossDataSource` **or** (`AllowCrossDataSource` and `IFederationGate.IsEnabledFor("GraphQL")`). The schema cache key includes the federation state. This removes the "advertised but broken" behaviour (AC-7). MCP `search_catalog`/`describe_dataset` read the same predicate through a shared `RelationExecutability` helper (placed in `Application/Federation`, owned by WP-08; MCP files consume it).
- **Splitter (`FederatedTreeQueryService.ExecuteAsync`)**:
  1. Walk the `TreeQueryNode` tree; classify each `TreeRelationNode` as local or cross-source via `IsCrossDataSource` (metadata read once per table, memoised).
  2. No cross-source edge: delegate unchanged (zero overhead; guarantees single-source parity, covered by existing golden tests).
  3. Cross-source edge: authorize **all** tables first through the inner service's resolver path (so a denial on any table is `ACCESS_DENIED` before any read, as today), then `FederationGate.EnsureAllowed("GraphQL")`.
  4. Execute the root segment (tree with the cross-source subtrees pruned, but with the join key columns added to the select list and removed again before returning to the caller if not requested); then for each cross-source edge, depth-first, call the resolver with the page's rows; a child segment may itself contain local relations (executed by the inner service in the child read) and further cross-source edges (recursion on the child rows).
  5. Stitch: JSON of children attached under `ResponseKey` (`null` for N:1 miss, `[]` for 1:N miss).
  6. Budgets: the tree budget (`ValidateTreeBudget`) is evaluated on the **whole** tree before step 3 (child counts estimated from `MaxNestedRows`); `MaxResponseRows` applies to the stitched total.
  7. Provenance in GraphQL `extensions["federation"]`; metrics and spans per segment.
- **Errors**: `FederationException` surfaces as `GraphQLException` with `extensions.code` = the stable code (WP-02 provides the `ErrorFilter` mapping; WP-08 provides the exception types used by the decorator only).
- **Sort**: `GraphQlTreeBuilder` already has no related-field sort; the resolver's sort guard remains as a defensive check.
- **Subscriptions/aggregate/metrics**: schema already single-table; WP-08 adds a regression test that a cross-source relation selection in a subscription is rejected at validation time.
- The inner service lines 223-235 stay (defensive); their message is replaced by `FederationException(FEDERATION_UNSUPPORTED_SOURCE)` only if the decorator is bypassed (WP-08 owns that single-line change).

### 4.9 HTTP sources and relation filters in GraphQL (WP-FED-09)

- `IGovernedSourceReader` implementation `GovernedConnectorSourceReader` for `HttpDeclarative`: uses the governed per-source read of `FederatedStagingService`/`GovernedConnectorReader.ReadAsync` (decision, tenant parameter, virtual-filter federation, masking, caps `MaxStagedRowsPerTable`, auth-mode allowlist). The descriptor must declare a key-lookup capability (query parameter name for the key list or equality filter per key with batch size 1); a relation to an HTTP table without it is not executable and not advertised. Reader chosen by `DataSourceType` through a small registry (`IGovernedSourceReaderRegistry`) so lakehouse/plugin readers can be added later (Q-3).
- `CatalogSchemaModel` admits `HttpDeclarative` tables as roots and relation targets (read path as REST: governed reader, `MaxResponseRows`, no filter/sort pushdown beyond descriptor capabilities; unsupported filter fields are not exposed in `<T>_filter` for HTTP tables).
- Root reads of HTTP tables in GraphQL go through the same reader with `MaxRows`; root order is the descriptor's declared order.
- Relation filters: `<T>_filter` gains relation fields only for executable cross-source relations; the decorator evaluates them first (semi-join), then replaces them with an `In` predicate on the parent's key column (bounded by `MaxCrossSourceFilterKeys`); above the limit `FEDERATION_LIMIT_EXCEEDED`; filter columns must be Clear (existing `FilterOnMaskedColumnException` semantics map to `FEDERATION_KEY_NOT_CLEAR`/`ACCESS_DENIED`).

### 4.10 Object consumers, stable refusal (WP-FED-02 and WP-FED-10)

- OData `$expand` stays `501 NotImplemented` (documented); REST has no expand; aggregate and metrics reject cross-source input: `GovernedAggregationService` and `SemanticMetricQueryService` add one validation step that a dataset spanning sources returns `FEDERATION_UNSUPPORTED_SOURCE` after authorization. Subscriptions per 4.8. Change detection unchanged (documented single-table).
- Parity test (WP-FED-10) enumerates consumers from a registry (`FederationConsumers.All`, owned by WP-02) so a new consumer without a matrix row fails the build.

---

## 5. Data and Configuration Summary

| Item | Change |
|---|---|
| `TABLE_RELATIONS.allow_cross_data_source` | new column in three baseline DDLs, shape snapshots, repositories (WP-07) |
| `WebSql:CrossSource:Enabled` | unchanged default `false` |
| `WebSql:CrossSource:AllowedTransports` | default = all registered (AsyncJob after WP-04); validated |
| `WebSql:CrossSource:Relations:*` | new bound section (`MaxBatchSize`, `MaxBatches`, `MaxNestedRows`, `MaxCrossSourceFilterKeys`, costs) |
| `WebSql:CrossSource:MaxMemory`, `MaxTempDirectorySize` | removed (`DuckDbOlap:*` is authoritative) |
| `WebSql:CrossSource:MaxParallelSourceReads` | enforced |
| `DuckDbOlap:TempDirectory` | new (default under app data dir) |
| Audit | `WebSqlCrossSourceSourceRead` used by all consumers; new `path` values `cross_source_relation`, `graphql_federation` |

No upgrade migrations: the caller supplies the newest schema; `0001` checksum constants unchanged per the issue #94 rules (shape snapshot test updated).

---

## 6. File Ownership and Work Packages

Disjoint file sets so Sonnet TDD developers can run in parallel. Each WP owns its tests. Paths are relative to the repository root. "LOC" = production / test estimate.

| WP | Title | Owned files (create or modify) | Depends on | LOC (prod / test) |
|---|---|---|---|---|
| **WP-FED-01** | Gate, transports, options validation | new `src/Autheris.Application/Federation/FederationGate.cs`, `FederationTransports.cs`; `src/Autheris.Domain/Options/Protocols/RestApiOptions.cs` (`CrossSourceOptions`); new `CrossSourceOptionsValidator.cs` and its DI registration file `src/Autheris.Api/Extensions/DependencyInjection/FederationServiceExtensions.cs` (new); `SqlTransports` constants in `IGovernedSqlExecutionService.cs` (add `GraphQl`, `Olap`); `tests/.../Federation/FederationGateTests.cs`, `CrossSourceOptionsValidatorTests.cs` | none | 350 / 450 |
| **WP-FED-02** | Error contract, provenance, consumer registry | new `Federation/FederationErrorCodes.cs`, `FederationException.cs`, `FederationProvenance.cs`, `FederationConsumers.cs`; edits: `SqlCompilerErrorMapper.cs`, `WebSqlEndpoints.Governance.cs`, `WebSqlStatementManager.cs`, `DatasetDataEndpoints.cs`, `ArrowExportEndpoints.cs`, `ArrowFlightSqlEndpoints.cs`, `SqlEndpointRoutes.cs`, `GatewayMcpQueryExecutor.cs`, GraphQL error filter file (`src/Autheris.GraphQL/...ErrorFilter*.cs`), async job failure mapping (`AsyncQueryJobBackgroundWorker.cs` error-code lines only, coordinate with WP-04 by region); tests per mapper | 01 (constants) | 450 / 650 |
| **WP-FED-03** | SQL federation hardening | `FederatedDuckDbExecutionService.cs`, `CrossSourcePlanner.cs`, `CrossSourceQueryRouter.cs`, `FederatedStagingService.cs`, `GovernedSqlExecutionService.cs` (result wrapping), job submit validation call in `AsyncJobEndpoints.cs`, `ArrowFlightSqlServer.cs` (info path only); tests in `tests/.../Sql/CrossSource*` | 01, 02 | 600 / 800 |
| **WP-FED-04** | Staging and result at rest | `DuckDbOlapEngine.cs` (settings, spill), `DuckDbOlapOptions` (`DistributedCacheOptions.cs` region), `AsyncQueryJobBackgroundWorker.cs` (result writer), async job download endpoint file, new `Jobs/Services/JobResultProtector.cs`; tests | 01 | 450 / 550 |
| **WP-FED-05** | OLAP facade | `src/Autheris.Api/Endpoints/DuckDbOlapEndpoints.cs`, `src/Autheris.Application/Olap/OlapStagingService.cs` (removal/shrink), `DuckDbOlapEngine.ValidateUserSql` (removal if unreferenced; coordinate with WP-04 by region); tests | 01, 03 | 350 / 450 |
| **WP-FED-06** | Governed reader and resolver rewrite | `src/Autheris.Application/Data/Relations/*` (all five files, rewritten), new `Federation/IGovernedSourceReader.cs`, `GovernedTreeSourceReader.cs`, `KeyPredicate.cs`; `DataApiServiceExtensions.cs`; test helper `tests/.../Fakes/InMemoryGovernedSourceReader.cs`; replace `CrossSourceRelationsAndChangesSinceTests.cs` relation parts (changes-since tests stay) | 01, 02 | 800 / 900 |
| **WP-FED-07** | Relation declaration and admin | `src/Autheris.Domain/Model/TableModels.cs` (`TableRelation`), baseline DDL x3 and shape snapshots under `src/Autheris.Infrastructure/Persistence/Migrations/`, three repositories (relation mapping only), catalog admin endpoint file (new `RelationAdminEndpoints.cs`), MCP admin tool file (new tool class), cache invalidation hook; tests (repository round trip per engine, shape verification) | none | 550 / 600 |
| **WP-FED-08** | GraphQL splitter and schema gating | new `src/Autheris.Application/Sql/Tree/Federation/FederatedTreeQueryService.cs`, `TreeSegmenter.cs`, `TreeStitcher.cs`; new `Federation/RelationExecutability.cs`; `src/Autheris.GraphQL/Catalog/CatalogSchemaModel.cs`, `CatalogGraphQlTypeModule.cs` (error mapping, extensions), `GovernedTreeQueryService.cs` (lines 223-235 only); MCP catalog files (`search_catalog`, `describe_dataset` relation list); decorator DI registration in `FederationServiceExtensions.cs` (append, after WP-01 merged); tests | 01, 02, 06, 07 | 1100 / 1200 |
| **WP-FED-09** | HTTP sources and relation filters in GraphQL | new `Federation/GovernedConnectorSourceReader.cs`, `IGovernedSourceReaderRegistry.cs`; extract governed per-source read from `FederatedStagingService.cs` (coordinate with WP-03: WP-09 starts after WP-03 merge); `CatalogSchemaModel.cs`/`CatalogGraphQlTypeModule.cs` filter and HTTP admission regions (after WP-08 merge); `TreeSegmenter.cs` relation-filter step; tests | 03, 06, 08 | 700 / 800 |
| **WP-FED-10** | Integration and parity tests | `tests/Autheris.Tests.Integration/Federation/*` (new): fixtures (PostgreSQL + SQL Server Testcontainers, HTTP stub server), `ConsumerParityMatrixTests.cs`, `GraphQlCrossSourceIntegrationTests.cs`, `FederatedSqlIntegrationTests.cs`, `SecurityRegressionFederationTests.cs` | 03, 05, 08 (09 for HTTP cases) | 0 / 1600 |

Totals: about 5,350 LOC production and 8,000 LOC tests (estimates, plus or minus 25 percent).

### 6.1 Sequencing and parallelism

```
Wave A (parallel):  WP-01, WP-07
Wave B (parallel):  WP-02, WP-04   (after WP-01 merged)
Wave C (parallel):  WP-03, WP-06   (after WP-02 merged)
Wave D (parallel):  WP-05 (after 03), WP-08 (after 06, 07, 02)
Wave E:             WP-09 (after 03, 08)
Wave F:             WP-10 integration (incrementally: SQL cases after Wave D, GraphQL cases after 08, HTTP cases after 09)
```

Increments map to waves: **INC-1** = WP-01, WP-02 (error and gate parts); **INC-2** = WP-03, WP-04, WP-05; **INC-3** = WP-06, WP-07, WP-08; **INC-4** = WP-09; **INC-5** = WP-10 plus Phase 6.

Shared-file rule (from the memory rule on parallel agents): developers only implement; the lead compiles and tests once per wave; files touched by two packages (`AsyncQueryJobBackgroundWorker.cs`, `DuckDbOlapEngine.cs`, `CatalogSchemaModel.cs`, `FederationServiceExtensions.cs`, `FederatedStagingService.cs`) are listed with an owner-by-region and a merge order (WP-02 before WP-04 for the worker error lines; WP-04 before WP-05 for the engine; WP-08 before WP-09 for the schema model; WP-03 before WP-09 for the staging service).

---

## 7. Test Strategy

- **Unit (TDD per WP):** gate matrix (enabled x transport x configured/default/empty/unknown); options validator; error mapping per transport (golden JSON); segmenter classification and pruning (single-source tree is returned byte-identical, cross-source edges detected at depth 1-3, composite keys, `NULL` keys, duplicate keys); stitcher (N:1 miss -> `null`, 1:N miss -> `[]`, per-parent `Limit`, ordering); key predicate builder (single, composite, batch boundary at `MaxBatchSize`, 500/501 keys); resolver guard (masked key, pseudonymised key, flag off, sort, limits, cumulative `MaxNestedRows`, `Truncated` -> limit error); schema gating (flag, gate, per-transport); parameter binding in the federated planner; `truncated` flag; relation-flag repository round trip and shape snapshot.
- **Integration (Testcontainers):** fixture with **PostgreSQL** (orders, order items) and **SQL Server** (customers) with distinct tenants, masked columns, row filters and consents, plus an **HTTP stub** (declarative table with key lookup). Cases: GraphQL N:1, 1:N, nested three-table chain, miss and governed-away child indistinguishable, tenant isolation across both sources, masked/pseudonymised join key refused, limit errors, sort refusal, relation filter, schema absence when unflagged or disabled; WebSQL, Trino, Arrow export, Flight SQL (`/info` and stream), async job (submit rejection and encrypted result), MCP `query_sql`, SQL endpoint with parameters, OLAP facade produce the same rows for the same two-source join; `Enabled=false` and transport narrowing per consumer; audit record count equals source reads; `DOTNET_USE_POLLING_FILE_WATCHER=true` in WSL (memory rule).
- **Parity matrix (WP-10):** `FederationConsumers.All` x expected outcome table; fails on an unregistered consumer.
- **Security regression:** tests listed in section 12.2 registered in the regression traceability catalog.
- **Mutation testing:** segmenter, stitcher, key predicate, gate and resolver guard added to the Stryker scope.
- **Build and verification (lead, after each wave):** `dotnet build Autheris.sln -c Release -warnaserror`, `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj`, integration project with polling watcher.

## 8. Telemetry (all on `GatewayDiagnostics`)

`federation_requests_total{consumer,outcome,code}`, `federation_sources_per_request`, `federation_batches_total`, `federation_rows_total{stage=staged|lookup|stitched}`, `federation_denials_total{code}`, `federation_read_duration`, `federation_cleanup_failed_total`; spans `federation.source_read` (`source`, `consumer`, `batches`, `rows`). No table names of unauthorized tables, no key values.

## 9. Risks and Mitigations (architecture)

| Risk | Mitigation |
|---|---|
| Decorator changes GraphQL behaviour of single-source queries | Pass-through when no cross-source edge; golden tests unchanged; mutation tests |
| Per-segment access memo diverges from the inner service | Same `operationId` and same `ITableAccessResolver`; the segmenter never re-implements decisions |
| Composite key `OR` predicates blow bind limits | `MaxBatchSize` bounded by the dialect's `MaxBindParameters / keyColumns`; the parameter budget provider is reused |
| HTTP source without key lookup | Relation not executable and not advertised; documented descriptor field |
| OLAP facade regressions | Breaking change accepted (PRD Q-2); parity tests |
| Spill encryption not available | No-spill fallback (`MaxTempDirectorySize=0`) |

## 10. Definition of Done per Work Package

All unit tests green, new tests failed first (Red-Green-Refactor), no new analyzer warnings in Release with warnings as errors, security tests of section 12 for the package present, XML docs on public contracts, no WIP notes in `docs/`, handoff note to the lead listing files touched.

## 11. Hand-off

- Phase 3 (section 12) appends binding security conditions.
- Phase 6 rewrites `docs/features/f-gov-19-cross-source-graphql-relations.md`, corrects every row of PRD 2.1, adds a federation feature document and the `docs/configuration-guide.md` section, registers it in `docs/features/README.md`, and sets the master plan status.

## 12. Security Review (Phase 3)

Pending (Phase 3).

## 13. Changelog

- 2026-10-16: Phase 2 architecture and work packages, verified against `70b75e27`.

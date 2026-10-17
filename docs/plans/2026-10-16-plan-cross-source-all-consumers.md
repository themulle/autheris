# Plan: PLAN-FED-01 - Cross-Source Access for All Consumers (Architecture and Work Packages)

**Document ID:** `PLAN-FED-01`
**Date:** 2026-10-16
**Status:** PLAN READY (Phase 2 complete); security review appended in section 12 (Phase 3); extension for #122-#124 (4.11, WP-FED-11..14, 12.6) reviewed; extension for #131/#132 (4.12, WP-FED-15/16) is a brief Phase 2 design, **SECURITY REVIEW pending** (12.7); next: Phase 4 for the reviewed packages
**Author:** Solution Architect (Phase 2, `csharp-architect` role), Security Expert (Phase 3, section 12)
**PRD:** [2026-10-16-req-cross-source-all-consumers.md](2026-10-16-req-cross-source-all-consumers.md) (`REQ-FED-01`; performance addendum section 7.1 for #122-#125)
**Extension 2026-10-16:** section 4.11 and `WP-FED-11..14` (issues #122, #123, #124), security addendum 12.6 (`SEC-FED-19..34`).
**Extension 2026-10-16 (2):** section 4.12 and `WP-FED-15..16` (issues #131, #132), security placeholders 12.7 (`SEC-FED-35..42`).
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Follow-up pointer (intake 2026-10-17):** [`REQ-REL-151`](2026-10-17-req-issue-151-composite-key-relations.md) changes `TABLE_RELATIONS` storage to ordered key pairs (`TABLE_RELATION_KEYS`) and should merge before step I-18; composite cross-source relations stay supported by `CrossSourceRelationResolver` and are locked by a test.
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

> **Note (2026-10-16):** the gate type, `AllowedTransports` startup validation and the removal of the dead `CrossSourceOptions` (`MaxMemory`, `MaxTempDirectorySize`) are delivered early by the security fix [`PLAN-SEC-OLAP-01`](2026-10-16-plan-sec-olap-federation-gate.md) (priority 1.2); WP-FED-01 builds on it and must not duplicate it.

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

> **Note (2026-10-16):** the interim OLAP gate and masked-column guardrails come first in [`PLAN-SEC-OLAP-01`](2026-10-16-plan-sec-olap-federation-gate.md); WP-FED-05 replaces that interim path with the facade.

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

> **Note (2026-10-16):** omitting non-executable cross-source relation fields is delivered early by [`PLAN-BUG-75-GATING`](2026-10-16-plan-bug-75-graphql-relation-schema-gating.md) (`RelationExecutability.IsSameExecutionSource`, compares source type and `SourceName`, not `Domain`); WP-FED-08 extends that predicate with `AllowCrossDataSource` and the federation gate.

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

### 4.11 Federated query performance: pushdown, same-source subtrees, key-set filters (WP-FED-11..14; issues #122, #123, #124)

Principle: **DuckDB keeps the complete user query. The source only ever delivers a superset of the rows DuckDB needs.** Every optimization may remove source reads or rows, never change DuckDB's result, except the explicitly source-authoritative same-source subtree (4.11.5). All optimizations are individually switchable (`WebSql:CrossSource:PredicatePushdown:Enabled`, `SameSourceSubtrees:Enabled`, `KeySetFilter:Enabled`); disabled equals today's behaviour (every table staged whole, `CrossSourcePlanner.cs:266`).

#### 4.11.1 Planner structure (hook points in `CrossSourcePlanner.Plan`)

`Plan` (`src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs:92`) is extended by three optional passes between step 2 (decisions and guardrails, `:120-130`) and step 4 (staging requests, `:~200-270`). Each pass is a separate class in `src/Autheris.Application/Sql/Federation/Optimization/` so `Plan` only orchestrates:

1. `SubtreeExtractor` (WP-FED-13) runs first and may replace tables by subtree inputs.
2. `ConjunctSplitter` + `PushabilityEvaluator` (WP-FED-11) produce `IReadOnlyDictionary<StagingInputId, TableFilterClause>` for the remaining single tables, and, inside a subtree, the pushed WHERE conjuncts become part of the subtree statement (they are compiled by the single-source compiler, not as a `TableFilterClause`).
3. `KeySetPlanner` (WP-FED-14) annotates join edges with `KeySetEdge(sourceInput, targetInput, keyColumn)` and a staging order.

The result extends the plan: `CrossSourcePlan` gains `StagingInputs` (single tables or subtrees), `PushedPredicates` and `KeySetEdges` (additive; `StagingTableRequest` keeps `PushdownFilter`, which finally gets a value instead of `null` at `:266`). The same `Plan` method is what the #79 query analysis calls (4.11.7), so analysis and execution cannot diverge.

#### 4.11.2 Conjunct splitter and pushability (WP-FED-11, #122)

- **Scope of splitting.** Top-level `WHERE` of the outermost query block, and the `ON` of inner joins (and of a left join for its null-supplying side). Split on top-level `AND` only. Not split: `HAVING`, anything inside CTEs/subqueries/derived tables (WP-FED-13 covers CTEs by making them subtrees), `OR` across tables, `CASE`, `EXISTS`.
- **Attribution.** A conjunct is attributable to table `T` iff every column reference resolves (through the existing `ReferencesColumn`/alias map of the planner) to the same single staging input and no other table. Unresolvable or ambiguous references: not pushed.
- **Join rules.** Inner join: `WHERE` and `ON` conjuncts pushable. Left join `A LEFT JOIN B`: `WHERE` on `A` pushable; `ON` conjunct of `B` only pushable; `WHERE` on `B` and `ON` on `A` not pushed. Right join mirrors. Full join, cross join with predicates, `LATERAL`: nothing. Table referenced by several aliases: pushed only if every alias has a conjunct; the clause is the `OR` of the per-alias clauses; otherwise none.
- **Eligible expression forms** (first step): `column OP constant`, `column [NOT] IN (constants)`, `column [NOT] BETWEEN c1 AND c2`, `column [NOT] LIKE pattern [ESCAPE c]`, `column IS [NOT] NULL`, and `AND`/`OR`/`NOT` over those within the same table. Constant = literal or bound parameter (list parameters expand). No functions, casts, arithmetic, subqueries, `COLLATE`, or non-deterministic expressions. The restriction is both the superset guarantee and the error-oracle guard (no expression that can raise in the source on rows the row filter hides).
- **Column eligibility.** The effective access of the column in the caller's decision must be `ColumnAccessLevel.Clear` (`GetEffectiveColumnAccess`), checked in the evaluator itself. HMAC passes `EnforceMaskedColumnGuardrails` (`GovernedSqlExecutionService.cs:300-310`) and is therefore explicitly rejected here. Any ineligible column inside a conjunct makes the whole conjunct non-pushable. Tables with a Virtual Filter plan that removes rows after the read (`VirtualFilterTablePlan` enforcement, `FederatedStagingService.cs:~240`) get no pushdown (FR-24).
- **Pushability matrix** (`PushabilityMatrix`, data driven per `TargetSqlDialect`; unknown cell means not pushable). Type class comes from the catalog column type.

| Type class | `=`, `IN`, `IS [NOT] NULL` | `<>`, `NOT IN`, `NOT BETWEEN`, `NOT LIKE` | `<`,`<=`,`>`,`>=`,`BETWEEN` | `LIKE` |
|---|---|---|---|---|
| integer, decimal, boolean, GUID/UUID | all dialects | all dialects (`NOT IN` only without `NULL` element) | all dialects | not applicable |
| date, timestamp (`date`, `datetime2`, `timestamp`, `timestamptz`) | all dialects, literal converted to the column type exactly | same | same, except SQL Server legacy `datetime`/`smalldatetime` (rounding) which are not pushed for anything but `IS NULL` | n/a |
| float/real/double | `IS NULL` only (NaN, signed zero) | no | ranges yes | n/a |
| string (`varchar`, `nvarchar`, `text`, `char`) | all dialects: source equality is equal or broader than DuckDB's (case-insensitive collation, PAD SPACE) | PostgreSQL, SQLite: yes when the column has no non-binary collation in the catalog; SQL Server, Oracle (`CHAR` padding), Databricks: **no** (source narrower: `<>` under case-insensitive/padded comparison drops rows DuckDB keeps) unless the column carries the catalog hint `exact_compare` (Q-11) | no (ordering differs by collation) unless `exact_compare` | pattern without bracket classes (`[`), without `ESCAPE` or with a single-char escape; source `LIKE` is broader (case-insensitive) which is safe; Oracle/SQLite case rules likewise broader or equal; unsupported pattern means not pushed |
| binary, spatial, JSON, array, unknown | no | no | no | no |

  A literal whose type class differs from the column class is not pushed (no implicit conversion in the source, which can change index use and comparison semantics). `NULL` in `IN` lists: `IN` drops the `NULL` element (never matches); `NOT IN` with a `NULL` element is not pushed.
- **Emission.** The clause is built from the AST as a `TableFilterClause` with parameters named from a planner-owned counter (`@p_pd_<n>`, no collision with the executor's `@p_arg_<n>`, `SqlTableReadExecutor.cs:317`) and a `TrinoSqlFactory` (without it the clause cannot be planned, fail closed). Values never enter predicate text. A list parameter longer than the dialect bind budget (`DialectCapabilities.MaxBindParameters`: SQL Server 2100, `DialectCapabilityTable.cs:39`) is not pushed.
- **Combination.** `SqlTableReadExecutor.BuildFilter` ANDs the pushed clause with arguments and the governed row filter (`:327-362`); `FederatedStagingService` passes the combined predicate; nothing about governance changes. The row filter is never inside the user clause.
- **Parameters.** Request parameters are carried by WP-FED-03 (D-10); the evaluator reads values from the same `ParameterSource`.

#### 4.11.3 Staging flow with optimization (WP-FED-12 shared parts, WP-FED-13, WP-FED-14)

`FederatedStagingService.StageAsync` today loops once over tables (`FederatedStagingService.cs:126`). New shape: a **stage plan** of ordered waves. Wave 0: all inputs without a key-set edge (parallel up to `MaxParallelSourceReads`, delivered by WP-FED-03). Wave n: inputs whose key set depends on an input staged in an earlier wave. Subtree inputs run in wave 0 or later like single tables. One governed read implementation is used per input (`GovernedConnectorReader.ReadAsync`) for single tables, `SubtreeExecutor` for subtrees. Budgets (`MaxStagedRowsPerTable`, total rows, total bytes) accumulate across waves and batches; `MaxTableCount` counts inputs (`:70`).

#### 4.11.4 Key-set filter (WP-FED-14, #124)

- **Eligible edges.** Inner join, or left join where the large side is the right side; ON contains a single-column equality `a.k = b.k` (further conjuncts remain in DuckDB; composite equalities use the first column only, which is a superset); both columns `Clear` in both decisions, same type class, neither side has a Virtual Filter plan that removes rows after read, both inputs are SQL sources, the target data source has `allow_key_set_import = true` (Q-8). Expression keys: out of scope. A join edge where either key is not eligible leaves the join to the unfiltered read.
- **Ordering.** Score: an input with pushed predicates or `expected_rows` below `MaxKeySetKeys` is a candidate source (small side); the other side is the target. Ties: the smaller hint first; no hint on either side and no pushed predicate: no key set (no statistics service). One input receives at most one key set (the smallest source); chains are allowed (A small, B keyed by A, C keyed by B), depth bounded by `MaxKeySetDepth` (default 3).
- **Key extraction.** After the source input is staged and governed (masking and row filters applied, after the DuckDB-visible values): distinct non-null clear values of the key column, in ordinal distinct sense (no trimming, no case folding). If the distinct count exceeds `MaxKeySetKeys` (default 5,000, range 1..50,000, own option, **not** `VirtualFilterFederationOptions.MaxPushdownKeys`): fallback to plain read of the target (`KeySetFallback` audited with reason `too_many_keys`), the query then fails with the existing `ROW_LIMIT_EXCEEDED` if the plain read exceeds the cap. Zero keys: the target read is skipped for an inner join (empty result is correct) but still audited with `rowCount: 0` and `skippedNoKeys: true`; for a left join with the target on the right a skipped target read yields an empty staged table with the right column set.
- **Batching.** Shared component `Federation/KeyBatching/KeyBatchPlanner` (owned by WP-FED-06 `KeyPredicate.cs` region, extended by this plan): `Split(keys, keyColumn, dialect, requestedBatchSize) -> IReadOnlyList<KeyBatch>` with `batchSize = min(requestedBatchSize, ConnectorCapabilities.MaxBatchSize, DialectCapabilities.MaxBindParameters - reservedForOtherParameters)`; `KeyBatch.ToFilterClause()` builds `k IN (@p_ks_0 ... )` bound parameters with a Trino factory; column type of each bound value is the target column's catalog type (SEC-FED-14). WP-FED-06 (GraphQL tree path) and WP-FED-14 (SQL path) consume the same planner; their different callers only differ in which governed read they call.
- **Batch execution and de-duplication.** One governed read per batch (own audit record, own cost). The rows of each batch are filtered in the gateway to those whose key equals (ordinal, as DuckDB compares) one of **that batch's** keys; rows returned only by the source's broader matching (case-insensitive collation, trailing spaces) are dropped. This is required for correctness: without it key `'a'` (batch 1) and key `'A'` (batch 2) return the same row `'A'` twice on a case-insensitive source and the join duplicates rows. Then the join in DuckDB matches exactly as before.
- **Budgets.** `MaxKeySetBatches` (default 10; a key set needing more batches falls back like too many keys); staged rows of the target are cumulative over its batches against `MaxStagedRowsPerTable`; the wall clock covers all waves; cost model `CostPerBatch`/`CostPerRow` (existing options of 4.1) charged per batch; per-user concurrency lease is held for the whole request.
- **Fallback and errors.** Fallback is silent to the caller except in the audit and analysis; limit overruns use `ROW_LIMIT_EXCEEDED`/`TOTAL_ROW_LIMIT_EXCEEDED` today and `FEDERATION_LIMIT_EXCEEDED` after WP-FED-02; nothing is cut off.

#### 4.11.5 Same-source subtrees (WP-FED-13, #123)

- **Grouping.** `SubtreeExtractor` assigns every table to its data source and builds maximal **connected join groups** of the join tree whose tables share one SQL source: a group is a sub-expression of the `FROM` (a contiguous subtree of the join tree) in which all joins are inner joins or outer joins fully inside the group, plus CTEs and derived tables whose body references tables of exactly one source and no other CTE of another source. Only groups with at least two tables, or a CTE/derived table with aggregation/window/`DISTINCT`, or a table with pushed predicates and a non-trivial projection, become subtrees; a single table stays a single input. Non-contiguous same-source tables (`a JOIN x JOIN a2` with `x` from another source) are not reordered (inner-join reassociation is a later extension). Joins between groups of different sources stay in DuckDB.
- **Subtree statement.** Built as a normal single-source `SELECT` AST: the group's `FROM`/joins, the conjuncts that reference only group tables (including multi-table ones inside the group, which #122 alone could not push), and a projection of exactly the columns the outer query needs (join keys, projected columns, columns of remaining predicates), with deterministic output names. Window functions, aggregation and `DISTINCT` inside CTE/derived bodies remain in the body.
- **Compile and execute through the governed single-source path.** `SubtreeExecutor` calls an internal entry of `GovernedSqlExecutionService`/`GovernedSqlExecutor` (`src/Autheris.Application/Sql/Services/`), not the public API, with the decisions already made by the router (no second authorization round, no decision re-defaulting: `RequireDecision` semantics), so that `ISqlEngine.Compile` applies tenant predicate, row filters, masks, consents and the engine guards (`AstSecurityVisitor`) for every table of the subtree exactly as for a direct single-source query. The result streams into the staging sink with the row/byte cap of the staged input (`MaxStagedRowsPerTable + 1` detection); the staged table is the subtree result.
- **Function allowlist by scope.** `CrossSourcePlanner.cs:108-117` checks the flat function list against the DuckDB allowlist. With subtrees, the analysis is made per scope: functions inside a subtree are validated by the source dialect's single-source rules (inside the compile), functions of the remaining outer query against `SqlFunctionAllowlists.DuckDb`. WP-FED-13 introduces scope-aware function collection from the AST (the flat `metadata.FunctionCalls` stays for the non-subtree plan).
- **Output classification (fail closed).** The compiler/result metadata must expose, per output column, whether it is `Clear`, `Masked` (including HMAC), or `DerivedFromMasked`/`Unknown`. A spike inside WP-FED-13 confirms what `ISqlEngine.Compile` returns; if it cannot, the subtree projection is restricted to direct column references (classification from the base column decision) and expressions are not output from subtrees until it can. `Unknown` is treated as masked: the outer `EnforceMaskedColumnGuardrails` semantics (no join, no filter except HMAC-in-DuckDB for `Masked` HMAC outputs) apply to subtree outputs, and key sets and pushdown never use them.
- **Budgets and counts.** `MaxTableCount` counts inputs; `MaxTablesPerSubtree` (default 12, Q-10) bounds the tables inside one subtree; `MaxStagedRowsPerTable` and the byte caps count the result of the subtree; total caps accumulate. The staged table counts as one input in `MaxTableCount`.
- **Audit.** One `WebSqlCrossSourceSourceRead` record per subtree with `kind: subtree`, all tables, source name, row count, in-DB masking and RLS flags (as the single-table read at `FederatedStagingService.cs:~225-245`), plus the normal single-source audit semantics of the compile (the compile's own execution audit is suppressed in favour of the staging record to avoid double counting; WP-FED-13 decides the single place).
- **Semantics.** Source-authoritative inside the subtree; documented in Phase 6 (R-11, Q-9). The switch `SameSourceSubtrees:Enabled` restores table-by-table staging.

#### 4.11.6 Audit fields (WP-FED-12)

Extension of the existing per-read audit (`FederatedStagingService.cs:225-245`, field `filterPushedDown` exists): `pushedPredicateCount`, `pushedPredicates` (list of `{column, operator}`; **no values, no parameter names beyond the count**), `inputKind` (`table`|`subtree`), `subtreeTables`, `keySetFilter` (`none`|`applied`|`fallback`|`skippedNoKeys`), `keySetKeyCount`, `keySetBatch`/`keySetBatches`, `keySetSource` (staging name). Existing masks/RLS flags unchanged. Audit write failure denies the request (INV-17, existing behaviour, unchanged).

#### 4.11.7 Query analysis (#79) output (WP-FED-12)

`QueryAnalysisResult` (`src/Autheris.Application/Governance/Analysis/QueryAnalysisModels.cs:174`) gains an additive `Federation` section for SQL items that route cross-source: per input table or subtree: source, whether staged whole or filtered, pushed predicates (column and operator; values are not repeated, the caller wrote them), predicates that stay in DuckDB with the reason code (`MASKED_COLUMN`, `UNSUPPORTED_EXPRESSION`, `MULTI_TABLE`, `OUTER_JOIN_RULE`, `COLLATION_RISK`, `GATEWAY_ENFORCED_TABLE`, `NOT_SQL_SOURCE`), subtrees per source, key-set edges with order, bound and fallback reason, and the active switches. It is produced by calling `ICrossSourcePlanner.Plan` in "analysis mode" (no staging, no source access, same decisions the analysis already resolves) so analysis and execution use identical code. Reason codes never reveal anything about masked columns beyond "masked" for a column the caller can already see in the schema.

#### 4.11.8 Configuration (WP-FED-12)

| Key | Default | Meaning |
|---|---|---|
| `WebSql:CrossSource:PredicatePushdown:Enabled` | `true` after security tests, `false` until then | #122 |
| `WebSql:CrossSource:SameSourceSubtrees:Enabled` | idem | #123 |
| `WebSql:CrossSource:SameSourceSubtrees:MaxTablesPerSubtree` | 12 | bound inside one subtree |
| `WebSql:CrossSource:KeySetFilter:Enabled` | idem | #124 |
| `WebSql:CrossSource:KeySetFilter:MaxKeySetKeys` | 5000 (1..50000) | key bound |
| `WebSql:CrossSource:KeySetFilter:MaxKeySetBatches` | 10 | batch bound |
| `WebSql:CrossSource:KeySetFilter:MaxKeySetDepth` | 3 | chain depth |
| `WebSql:CrossSource:KeySetFilter:BatchSize` | 500 | requested batch size, clamped by connector and dialect |
| Table metadata `expected_rows` (nullable int), column hint `exact_compare`, data source flag `allow_key_set_import` | null / false / false | catalog hints; baseline DDL changed in place (no migrations), shape snapshots updated; owner-set and audited |

All validated at startup (`IValidateOptions`, WP-FED-01 validator extended by WP-FED-12). Shipping the three `Enabled` flags as `false` until the security tests (12.6) are green and flipping the default in the same increment as the parity tests is the recommended rollout.

### 4.12 Federated parameters and co-located data sources (WP-FED-15, WP-FED-16; issues #131, #132)

*Brief Phase 2 design, verified against `5ae3c046`. Detail to be completed when the packages enter Phase 4; the security conditions are placeholders until Phase 3 (12.7).*

#### 4.12.1 Federated parameters (WP-FED-15, #131)

- **Scope.** WP-FED-03 (4.3) already carries scalar client parameters into `CrossSourcePlanner.Plan` and compiles the DuckDB statement with a `ParameterSource` that binds them. WP-FED-15 adds on top: list parameters (REQ-SQL-128 expander), the single validation pass, the typed pushed value, and parameter names in audit and analysis.
- **Contract object.** `Federation/FederatedParameterContract` (new) is built once in `FederatedDuckDbExecutionService` before `Plan`: it takes the normalised client values and the declared or inferred types (from the declared query or from the `WebSqlParameterValues` normalisation of REQ-SQL-128), validates names, types, list element types and bounds, and exposes `ParameterSource` (for DuckDB `$n`) and `TryGetTyped(name, columnTypeClass, out value)` for the pushdown builder. Invalid input refuses the request before `StageAsync` (zero source reads).
- **Pushdown hook (WP-FED-11 region).** `PushedClauseBuilder` asks the contract for the value typed to the column's catalog type class; a type-class mismatch, a list above the source bind budget (`DialectCapabilityTable.MaxBindParameters` minus policy parameters) or an unsupported conjunct form returns "not pushable" and the conjunct stays only in DuckDB (superset rule, SEC-FED-21).
- **Subtrees (WP-FED-13 region).** The subtree compile receives the same client values through the governed single-source path (binder), so a parameter inside a same-source subtree is bound by the source's binder, not spliced.
- **Audit and analysis.** `WebSqlCrossSourceSourceRead` gains `parameterNames` and `listParameterCounts`; the query record gets the same; the #79 federation section shows per pushed predicate the feeding parameter name.
- **No new option** except reuse of `WebSql:MaxListParameterValues` (REQ-SQL-128).

#### 4.12.2 Co-located data sources (WP-FED-16, #132)

- **Configuration.** `DataSourceConnectionOptions.CoLocationGroup` (string, optional, `[A-Za-z0-9_-]{1,64}`) in `src/Autheris.Domain/Options/Storage/DatabaseOptions.cs` (after REQ-GOV-107 and coordinated with `WP-SC-15`). New `Federation/CoLocation/CoLocationRegistry` (immutable, built at startup) and `CoLocationOptionsValidator` (extends `CrossSourceOptionsValidator`, after WP-FED-01): group of at least two members, equal `Provider` (SQL Server only), equal normalised server and credential identity (connection string with `Initial Catalog`/`Database` removed, Windows auth settings equal), equal session options, no `Delegated*` mode (once SC-15 exists), no HTTP source, startup failure otherwise.
- **Connect-time verification.** `Federation/CoLocation/CoLocationVerifier` runs once per group on first use and on a configurable interval (default 10 minutes): one connection per member, reads `@@SERVERNAME` plus `SERVERPROPERTY('InstanceName')` and `DB_NAME()`, compares all members, caches the verdict; failure marks the group "not co-located" (staging fallback, stable log code, analysis flag). It reuses the login circuit breaker of each source (an open breaker counts as failure of that member).
- **Grouping.** `SubtreeExtractor` (WP-FED-13) uses the key `registry.GroupOf(source) ?? source`; a subtree may then contain tables of several members. `OutputColumnClassifier`, `ScopedFunctionCollector` and the budget rules are unchanged.
- **Qualification.** `SubtreeStatementBuilder` marks each table with its member source; for a subtree that spans more than one database the SQL Server emitter qualifies tables as `[database].[schema].[table]` using the database name from the data source configuration (identifier validation and dialect quoting in the emitter, never client text). **Spike (first task of WP-FED-16):** confirm that `TableAccessTarget`/`ISqlEngine.Compile` can carry a catalog part end to end (policy predicates, masks, row filter column references, tenant predicate, `AstSecurityVisitor`), or add it; if the compiler cannot, the package is blocked and the owner is told.
- **Authorization.** All member tables are authorized once before any read (unchanged FR-30). `SubtreeExecutor` receives the authorized `(source, database, schema, table)` set; after compile, a **reference check** walks the final AST and refuses any table reference outside the set (`FEDERATION_INTERNAL_GUARD`, fail closed, audited).
- **Connection and session.** One connection of a member, pinned as today; the tenant session context applies to the whole connection; the connection options (timeout, isolation) are those that the validator proved equal.
- **Audit and limits.** One statement record (`kind: subtree`, `colocated: true`, group, all tables and sources) plus one access-audit entry per table with that table's source and owner identity; per-source concurrency leases and per-user-and-table read limits are acquired for every member (deterministic order to avoid deadlock); the #79 analysis gets `colocated`, group and members.
- **Switch.** `WebSql:CrossSource:CoLocation:Enabled` (default `false` until the Phase 3 tests pass), `WebSql:CrossSource:CoLocation:VerifyIntervalSeconds` (default 600).

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
| `WebSql:CrossSource:PredicatePushdown/SameSourceSubtrees/KeySetFilter:*` | new sections, see 4.11.8 (WP-FED-12) |
| `TABLE_METADATA.expected_rows`, column `exact_compare`, data source `allow_key_set_import` | new nullable/boolean catalog hints in baseline DDL x3 and shape snapshots (WP-FED-12) |
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
| **WP-FED-11** | Conjunct splitter, pushability rules, planner hook (#122) | new `src/Autheris.Application/Sql/Federation/Optimization/ConjunctSplitter.cs`, `PushabilityEvaluator.cs`, `PushabilityMatrix.cs`, `PushedClauseBuilder.cs`; `CrossSourcePlanner.cs` (hook region: staging request creation at `:266`, plan record); `tests/.../Sql/Federation/Optimization/*` | 03 | 900 / 1300 |
| **WP-FED-12** | Federation performance options, catalog hints, audit fields, #79 analysis section | new `Federation/FederationOptimizationOptions.cs` and validator (extends `CrossSourceOptionsValidator.cs` after WP-01), `CrossSourceOptions` (properties region in `RestApiOptions.cs`, after WP-01), `FederatedStagingService.cs` audit region (`:225-245`), `QueryAnalysisModels.cs`, `QueryAnalysisService.Sql.cs`, analysis REST/MCP serialisation, catalog hints: `TableModels.cs` (`expected_rows`, `exact_compare`), data source flag `allow_key_set_import`, baseline DDL x3 and shape snapshots, three repositories (mapping only), admin API field; tests | 03, 11 | 700 / 900 |
| **WP-FED-13** | Same-source subtrees (#123) | new `Sql/Federation/Optimization/SubtreeExtractor.cs`, `SubtreeStatementBuilder.cs`, `SubtreeExecutor.cs`, `ScopedFunctionCollector.cs`, `OutputColumnClassifier.cs`; `GovernedSqlExecutionService.cs`/`GovernedSqlExecutor.cs` (new internal entry only, region owned), `FederatedStagingService.cs` (stage plan, subtree input region), `CrossSourcePlanner.cs` (subtree pass region, after WP-11 and WP-14 merges); tests | 03, 11, 12 | 1300 / 1500 |
| **WP-FED-14** | Key-set filters and shared key batching (#124) | new `Sql/Federation/Optimization/KeySetPlanner.cs`, `KeySetStagingCoordinator.cs`, `KeyExactMatchFilter.cs`; `Federation/KeyBatching/KeyBatchPlanner.cs` (contract owned by WP-06 `KeyPredicate.cs`; WP-14 adds the dialect-bound `Split`, WP-06 consumes it); `FederatedStagingService.cs` (waves and batch region), `CrossSourcePlanner.cs` (key-set pass region); tests | 03, 06, 11, 12 | 700 / 1000 |
| **WP-FED-15** | Federated parameter contract and list parameters (#131) | new `Federation/FederatedParameterContract.cs`; `CrossSourcePlanner.cs` (parameter region, `ParameterSource` at `:282`), `FederatedDuckDbExecutionService.cs` (contract construction before `Plan`), `PushedClauseBuilder.cs` (typed-value hook, after WP-11), `FederatedStagingService.cs` audit region (`parameterNames`), `QueryAnalysisService.Sql.cs` (parameter names, after WP-12); tests `tests/.../Sql/Federation/FederatedParameterTests.cs`, injection corpus, validation-before-read spy | 03, 11, 12; list parameters need **REQ-SQL-128** (the list expander and `WebSql:MaxListParameterValues`) | 450 / 700 |
| **WP-FED-16** | Co-located data sources (#132) | `DatabaseOptions.cs` (`CoLocationGroup`, after REQ-GOV-107 merge, coordinate with `WP-SC-15`), new `Federation/CoLocation/CoLocationRegistry.cs`, `CoLocationOptionsValidator.cs`, `CoLocationVerifier.cs`; `SubtreeExtractor.cs` (grouping-key region), `SubtreeStatementBuilder.cs` (qualification region), `SubtreeExecutor.cs` (reference check, multi-lease), SQL Server emitter and `TableAccessTarget` catalog part (`src/TrinoSqlEngine/...`, spike result decides), audit and analysis fields; tests incl. SQL Server two-database container fixture | 13, 12 (spike first) | 1200 / 1500 |

Totals WP-FED-01..10: about 5,350 LOC production and 8,000 LOC tests (estimates, plus or minus 25 percent). WP-FED-11..14 add about 3,600 LOC production and 4,700 LOC tests (plus 1,500 LOC of integration tests in WP-FED-10, see 7); WP-FED-10 grows from 1,600 to about 3,100 test LOC.

### 6.1 Sequencing and parallelism

```
Wave A (parallel):  WP-01, WP-07
Wave B (parallel):  WP-02, WP-04   (after WP-01 merged)
Wave C (parallel):  WP-03, WP-06   (after WP-02 merged)
Wave D (parallel):  WP-05 (after 03), WP-08 (after 06, 07, 02)
Wave E:             WP-09 (after 03, 08)
Wave F:             WP-10 integration (incrementally: SQL cases after Wave D, GraphQL cases after 08, HTTP cases after 09)
Wave G (serial):    WP-11 (after 03)                          [#122]
Wave H (serial):    WP-12 (after 11)                          [options, hints, audit, analysis]
Wave I (develop parallel, merge 14 before 13):  WP-14 (after 06, 11, 12)  [#124],  WP-13 (after 11, 12)  [#123]
Wave F':            WP-10 integration cases for G-I (SQL Server collation, two persons, parity on/off)
Wave J:             WP-15 (after 03, 11, 12; list parameters after REQ-SQL-128)   [#131]
Wave K:             WP-16 (after 13, 12; spike first; SQL Server two-database fixture)   [#132]
Wave F'':           WP-10 integration cases for J-K (parameter parity, co-located one statement, two persons, forged qualification)
```

Increments map to waves: **INC-1** = WP-01, WP-02 (error and gate parts); **INC-2** = WP-03, WP-04, WP-05; **INC-3** = WP-06, WP-07, WP-08; **INC-4** = WP-09; **INC-5** = WP-10 plus Phase 6. New **INC-6 (federated query performance)** = WP-FED-11, 12, 13, 14 plus their cases in WP-10; it follows INC-2 (WP-03 owns the same files) and needs only the key-batching contract of WP-06 from INC-3, so INC-6 can ship before INC-4 (HTTP/relation filters) if the owner prefers volume relief over GraphQL non-SQL. Within INC-6 the value order is #122 (Wave G/H), #124, #123. New **INC-7 (federated parameters and co-located sources)** = WP-FED-15, WP-FED-16 plus about 600 LOC of integration cases in WP-10 (WP-FED-10 grows to about 3,700 test LOC); it follows INC-6 and, for list parameters, the REQ-SQL-128 work package. The shared-file rule applies to `CrossSourcePlanner.cs`, `FederatedStagingService.cs`, `GovernedSqlExecutionService.cs`: WP-03 before WP-11 before WP-12 before WP-14 before WP-13; the new classes of 13 and 14 are developed in parallel, only their hook regions are merged in that order.

Shared-file rule (from the memory rule on parallel agents): developers only implement; the lead compiles and tests once per wave; files touched by two packages (`AsyncQueryJobBackgroundWorker.cs`, `DuckDbOlapEngine.cs`, `CatalogSchemaModel.cs`, `FederationServiceExtensions.cs`, `FederatedStagingService.cs`) are listed with an owner-by-region and a merge order (WP-02 before WP-04 for the worker error lines; WP-04 before WP-05 for the engine; WP-08 before WP-09 for the schema model; WP-03 before WP-09 for the staging service).

---

## 7. Test Strategy

- **Unit (TDD per WP):** gate matrix (enabled x transport x configured/default/empty/unknown); options validator; error mapping per transport (golden JSON); segmenter classification and pruning (single-source tree is returned byte-identical, cross-source edges detected at depth 1-3, composite keys, `NULL` keys, duplicate keys); stitcher (N:1 miss -> `null`, 1:N miss -> `[]`, per-parent `Limit`, ordering); key predicate builder (single, composite, batch boundary at `MaxBatchSize`, 500/501 keys); resolver guard (masked key, pseudonymised key, flag off, sort, limits, cumulative `MaxNestedRows`, `Truncated` -> limit error); schema gating (flag, gate, per-transport); parameter binding in the federated planner; `truncated` flag; relation-flag repository round trip and shape snapshot.
- **Integration (Testcontainers):** fixture with **PostgreSQL** (orders, order items) and **SQL Server** (customers) with distinct tenants, masked columns, row filters and consents, plus an **HTTP stub** (declarative table with key lookup). Cases: GraphQL N:1, 1:N, nested three-table chain, miss and governed-away child indistinguishable, tenant isolation across both sources, masked/pseudonymised join key refused, limit errors, sort refusal, relation filter, schema absence when unflagged or disabled; WebSQL, Trino, Arrow export, Flight SQL (`/info` and stream), async job (submit rejection and encrypted result), MCP `query_sql`, SQL endpoint with parameters, OLAP facade produce the same rows for the same two-source join; `Enabled=false` and transport narrowing per consumer; audit record count equals source reads; `DOTNET_USE_POLLING_FILE_WATCHER=true` in WSL (memory rule).
- **Federated performance (WP-FED-11..14).**
  - *Unit:* conjunct splitting (nested `AND`/`OR`/`NOT`, parentheses, aliases, self-join, ambiguous column, `ON` versus `WHERE`, left/right/full join rules); pushability matrix cell by cell per dialect and type class (SQL Server, PostgreSQL, Oracle, Databricks, SQLite), including `NOT IN` with a `NULL` element, `LIKE` with `[`, literal type mismatch, float equality, legacy `datetime`; HMAC/mask/deny/clear column levels; Virtual Filter tables; parameter lists above the bind budget; parameter-name collision fails closed; analysis reason codes; option validation; subtree grouping (contiguous versus non-contiguous, CTE of one source, CTE referencing another CTE, outer join inside/crossing the group, DML), scoped function collection, output classification; key-set planner (eligible edge, ordering by hint/pushed predicate, no hint no key set, chain depth, masked/HMAC key, Virtual Filter table, HTTP target), `KeyBatchPlanner` boundaries (batch at 500/501, dialect bind limit 2100 with reserved parameters), exact-match batch filter, zero keys, fallback.
  - *Result parity (pushdown on/off, subtrees on/off, key sets on/off):* a table-driven suite where every case runs with all switches off (reference: all tables staged whole) and with each switch on, and the result sets must be equal (ordered by a total order): the issue's set `AND`, `OR`, `IN`, `NOT IN` with `NULL`, `LIKE`, `IS NULL`, inner and left joins; plus string predicates against **SQL Server with a case-insensitive, accent-sensitive collation and values with trailing spaces and differing case** (`'closed'`, `'CLOSED'`, `'closed '`), where `<>` on strings must not be pushed and `=` may be; PostgreSQL with a case-sensitive deterministic collation; keys differing only by case/trailing space on both ends (no duplicate rows across batches). Parity for subtrees is against the same subtree sent single-source, and against the all-staged query on data without collation-dependent keys.
  - *Volume:* a table over `MaxStagedRowsPerTable` with few matching rows succeeds with pushdown and still fails with `ROW_LIMIT_EXCEEDED` without it (switch off or non-pushable predicate); a large side reached by key set; key set above `MaxKeySetKeys` falls back and then fails with the stable code; subtree with five same-source tables plus one other source, `MaxTableCount` respected.
  - *Persons:* two persons with different row filters and different masks on the same tables get different, correct results for pushdown, subtree and key-set queries (also through each of WebSQL, Trino and Arrow export).
  - *Integration (Testcontainers, WP-10):* the same cases on PostgreSQL + SQL Server, audit records (count of reads, `pushedPredicates` without values, `keySetFilter`, `subtree`), #79 analysis section equal to what ran.
  - *Mutation testing:* `ConjunctSplitter`, `PushabilityEvaluator`, `PushabilityMatrix`, `KeyBatchPlanner`, `KeyExactMatchFilter`, `SubtreeExtractor` in the Stryker scope.
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
| Wrong pushability cell loses rows silently | Conservative matrix (unknown is not pushable), on/off parity on every dialect in CI, three independent kill switches, mutation tests on the matrix |
| Collation of a column changes after catalog ingestion | Matrix reads catalog type and collation; missing collation info is treated as non-binary (string `<>`, ranges not pushed) |
| Key-set duplicates across batches on case-insensitive sources | Exact-match filter per batch (4.11.4), tested with key pairs differing by case and trailing spaces |
| Subtree output hides a masked origin | Output classification fail closed; unknown means masked |
| Planner passes grow `CrossSourcePlanner.cs` too large | Passes live in separate classes under `Sql/Federation/Optimization/`; `Plan` only orchestrates |

## 10. Definition of Done per Work Package

All unit tests green, new tests failed first (Red-Green-Refactor), no new analyzer warnings in Release with warnings as errors, security tests of section 12 for the package present, XML docs on public contracts, no WIP notes in `docs/`, handoff note to the lead listing files touched.

## 11. Hand-off

- Phase 3 section 12 is binding for Phase 4 and Phase 5.
- Phase 6 rewrites `docs/features/f-gov-19-cross-source-graphql-relations.md`, corrects every row of PRD 2.1, adds a federation feature document and the `docs/configuration-guide.md` section, registers it in `docs/features/README.md`, and sets the master plan status.

## 12. Security Review (Phase 3)

*Reviewer: Security Expert (`csharp-security-expert` role). Scope: PRD `REQ-FED-01` and sections 1-10 of this plan, audited against the code at `70b75e27`. Fail-closed, Casbin ABAC, HMAC audit and tenant isolation were checked as in earlier features.*

### 12.1 Threat Model

| ID | Threat | Vector | Present today? | Control (binding condition) |
|---|---|---|---|---|
| T-1 | **Cross-source oracle** | Caller infers existence or attributes of rows in source 2 from error codes, latency, empty-versus-null differences, or distinct errors for "denied" and "not found" | Partly: the resolver throws `GatewayForbiddenException` for the child table before reading (fine), but a governed-away child could differ from a missing one | SEC-FED-03, SEC-FED-04 |
| T-2 | **Key leakage via masked join keys** | Join on a masked or pseudonymised column reveals or lets the caller correlate masked values across sources (equality oracle, re-identification) | Guarded in SQL federation (SEC-JOIN-01); resolver checks Clear keys but nothing enforces it in the GraphQL decorator yet | SEC-FED-02 |
| T-3 | **RLS bypass on source 2** | Child rows read raw and filtered in the gateway with a weaker evaluator, or through a service account; federated virtual filters not applied; tenant `NULL` passes | **Yes** in the current resolver (`Helpers.cs:232-267`, `NULL` tenant passes; no federated virtual filters); also the OLAP endpoint lacks compiler guardrails | SEC-FED-01, SEC-FED-05, SEC-FED-12 |
| T-4 | **Tenant mixing** | Key lookups, staging or result files shared across tenants; DuckDB staging or spill directory reused; relation flag set for tenant A influences tenant B | Staging is per request; result files partitioned by tenant; schema cache could mix if keyed without tenant | SEC-FED-06, SEC-FED-07 |
| T-5 | **Amplification and DoS by fan-out** | One GraphQL page with many distinct keys causes many batches against source 2; deep nested chains multiply; large 1:N; concurrent requests exhaust DuckDB memory | Bounded only for SQL staging; resolver limits exist but are unwired; `MaxParallelSourceReads` not enforced | SEC-FED-08 |
| T-6 | **Staging data at rest** | DuckDB spill and async job result files in clear text in `Path.GetTempPath()` and the scratch directory; post-masking clear-text columns can spill | **Yes** | SEC-FED-09 |
| T-7 | **Audit gaps** | Source-2 reads without audit; audit failure ignored; key values or rows in audit; no correlation id across sources; GraphQL cross-source reads recorded once instead of per source | Resolver audits per target read (unwired); SQL federation audits per source | SEC-FED-10 |
| T-8 | **Confused deputy and privilege widening by relation flag** | An admin enables `AllowCrossDataSource` and thereby exposes owner B's data through owner A's table | New surface | SEC-FED-11 |
| T-9 | **Gate bypass** | A transport reaches federation without the gate: Flight `/info`, job submit, OLAP, GraphQL; typo in transport list silently denies or (with default expansion) silently allows | Yes for OLAP; Flight `/info` partly | SEC-FED-12 |
| T-10 | **Information disclosure through errors and metadata** | Specific federation codes before authorization reveal table type or configuration; provenance lists unauthorized sources; upstream messages in responses | Generic today (good); new codes could weaken it | SEC-FED-03, SEC-FED-13 |
| T-11 | **Injection through keys** | Parent key values (clear but attacker-influenced strings) concatenated into the child statement or the HTTP request | Child statement is compiled from AST with parameters; HTTP key parameter risk | SEC-FED-14 |
| T-12 | **Stale authorization** | Cached schema or relation executability survives a flag, policy or consent change | New caches | SEC-FED-15 |
| T-13 | **Consistency-based leakage** | Reading source 2 later than source 1 reveals a state change that the caller should not observe (TOCTOU between segments); a consent revoked between segment reads | Inherent | SEC-FED-16 (accepted, bounded) |

### 12.2 Binding Conditions (SEC-FED-xx)

Each condition is mandatory for Phase 4 and checked in Phase 5. Each lists a mandatory test.

- **SEC-FED-01 Governed pipeline only.** Every source read, SQL or HTTP, in every consumer goes through the governed pipeline (decision, tenant predicate, row filters, masking, federated virtual filters). `StreamingRowFilterAstEvaluator`-style re-filtering of raw rows for cross-source reads is removed. `IGovernedSourceReader` has no raw API. *Test:* architecture test that no type outside the governed readers references `ICrossSourceRowReader`-style raw reads; row-filter, tenant, virtual-filter and consent denial on the child table each hide the child row (parity with a single-source read of the same child).
- **SEC-FED-02 Clear join keys, both sides, before any read.** Parent and child key columns must be `Clear` in the caller's decision (not masked, not HMAC, not excluded, not hidden by column policy); the check precedes every read and also covers key columns used for relation filters. Failing returns `FEDERATION_KEY_NOT_CLEAR` only after authorization of both tables. *Test:* matrix of masking rule types (REDACT, HMAC, partial, hash) on parent key and child key for GraphQL, WebSQL join (SEC-JOIN-01 regression), OLAP facade.
- **SEC-FED-03 Anti-oracle ordering and codes.** All tables authorized first; denial of any table yields the generic `ACCESS_DENIED`/`SQL_POLICY_DENIED` identical for "does not exist", "inactive" and "no consent". Specific `FEDERATION_*` codes only after full authorization. Response timing classes: no early return that skips the authorization of remaining tables. *Test:* identical bodies and statuses for unknown versus unauthorized child table; specific codes appear only for a fully authorized caller.
- **SEC-FED-04 Indistinguishable misses.** A child that is missing, row-filtered, tenant-foreign, or consent-denied at row level resolves to `null`/empty with no extension, count or error difference. Table-level denial of the child fails the whole request with the generic code (as the parent). *Test:* same response for the three row-level cases; no `rows` or `count` in provenance that reveals filtered rows (provenance counts rows returned after governance only).
- **SEC-FED-05 No NULL tenant pass, no service account.** Tenant predicate is applied by the compiler on every source read; the resolver's in-gateway tenant check is deleted. No federation read uses a credential other than the governed read connection configured for the source (and, where configured, the caller-delegated identity); no escalation to a privileged identity. *Test:* child rows with `NULL` tenant value are excluded; two tenants with overlapping keys get only their own children (GraphQL and SQL federation).
- **SEC-FED-06 Per-request isolation.** No cross-request caching of source-2 rows, keys or lookup results; the per-operation access memo is keyed by (table, tenant, user sid) as today; schema cache key includes tenant where the schema is tenant-specific and federation state. *Test:* concurrent requests of two tenants/users never see each other's rows or schema fields; no static or singleton row holders (reflection test on the new types).
- **SEC-FED-07 Staging directory isolation.** Spill and result files are per request/job, in a dedicated directory with owner-only permissions, never shared across tenants, names unguessable (GUID plus tenant partition). *Test:* permission bits (Unix) / ACL (Windows) assertion; path traversal attempt through job id or tenant string rejected.
- **SEC-FED-08 Fan-out bounds and cost.** Enforced and tested: `MaxBatchSize` (also bounded by dialect bind limit and key column count), `MaxBatches`, cumulative `MaxNestedRows`, `MaxCrossSourceFilterKeys`, tree budget over the whole tree before any read, `MaxTableCount`, `MaxStagedRowsPerTable`/total rows/bytes, `MaxParallelSourceReads`, wall-clock timeout over authorization plus reads, per-user concurrency lease, cost reported to the quota middleware (`CostPerBatch`, `CostPerRow`). Exceed means `FEDERATION_LIMIT_EXCEEDED`, never truncation. *Test:* each limit exceeded individually and cumulatively across nested levels; a malicious page with 10,000 distinct keys cannot issue more than `MaxBatches` reads; timeout cancels outstanding reads.
- **SEC-FED-09 No clear-text staging at rest.** DuckDB spill encrypted or disabled; async job results encrypted with an authenticated envelope; files owner-only; deleted on completion/expiry; cleanup failure is a metric and alert, not silent. `AsyncJob` is not in the default transports until this condition holds (`ProtectedStagingEnabled`). *Test:* spill directory contains no plaintext marker row after a forced spill; result file is not readable as CSV/JSON without the key; deletion after expiry; `AsyncJob` absent from defaults when protection is off.
- **SEC-FED-10 Audit per source read, fail closed.** One `WebSqlCrossSourceSourceRead` (or successor) record per source read in every consumer, shared correlation id with the parent request record, containing source, table, consumer, batches, row counts, outcome; no keys, no rows, no SQL parameter values. Audit write failure denies the request (existing SQL federation behaviour extended to GraphQL). Denials after authorization (limit, key not clear) are audited. HMAC chain unchanged. *Test:* audit count equals source reads for each consumer; injected audit failure -> request denied and no data returned; audit payload contains no key or row values.
- **SEC-FED-11 Relation flag governance.** Setting `AllowCrossDataSource` requires `GovernanceAdmin`, is audited with before/after and actor, goes through the existing four-eyes flow for MCP admin tools, requires owner approval of both tables when owners exist, bumps the epoch of both tables, invalidates the schema cache, and is tenant-scoped like the relation. Discovery/ingestion never sets or clears it. Default false. *Test:* non-admin 403; audit record; epoch bump; flag survives catalog re-ingestion; cross-tenant relation id returns the fixed 404.
- **SEC-FED-12 Single gate for all transports; OLAP compiler path.** `FederationGate` is the only evaluator; called in execution and in every validate/submit/info path (Flight `/info`, async submit); OLAP uses the governed compiler and the SQL function allowlist, client DuckDB SQL is not executed; startup validation rejects unknown transport names; empty list denies all. *Test:* gate matrix for every transport and path; OLAP join over a masked column rejected; DuckDB-only function or `read_csv`/`COPY`/`ATTACH` through OLAP rejected; unknown transport name aborts startup.
- **SEC-FED-13 Provenance and error disclosure.** Provenance lists only sources of tables the caller was authorized for, with counts after governance; fixed messages; upstream and database messages never in responses or logs (stable code and exception type only); non-production environments do not echo raw policy text on Arrow export. *Test:* response bodies for forced source failures contain no driver text; provenance excludes unauthorized sources; Arrow export in Development returns the fixed message.
- **SEC-FED-14 Keys are data, never SQL.** Child statements are compiled from the AST with bound parameters typed from the child key column's catalog type; HTTP key lists are URL- or body-encoded by the connector's encoder with length limits and a key charset/format validation per column type; no string concatenation. *Test:* keys containing quotes, SQL fragments, `%`, CRLF, very long strings and Unicode confusables produce parameterised statements/encoded requests and no injection; oversized key rejected.
- **SEC-FED-15 Invalidation and freshness.** Relation executability and the GraphQL schema are recomputed when the relation flag, the federation options, or the catalog changes; decisions are never cached across requests by the new code (the per-operation memo lives for the operation only). *Test:* flag off removes the field at the next schema build; consent revoked between two requests denies the second.
- **SEC-FED-16 Consistency window accepted and bounded.** No global snapshot; the segment reads of one request run within one request timeout; the authorization of all tables happens before the first read and is not re-evaluated between segments (a revocation during the request window is accepted); documented in the feature doc and returned as `consistency: "per-source"` in provenance. *Test:* all tables authorized before the first read (order-of-calls test); documented limit.
- **SEC-FED-17 Fail-closed defaults.** No decision, no metadata, unsupported source type, unknown dialect, missing reader for a source type, descriptor without key lookup: the relation is not advertised and a direct attempt fails with `FEDERATION_UNSUPPORTED_SOURCE` (after authorization). Option parse errors abort startup. *Test:* each missing prerequisite denies; no partial results.
- **SEC-FED-18 Transport broadening review.** The widened default transport list is accepted only because SEC-FED-01..10 make governance transport-independent; AsyncJob and any future transport (`PgWire`) stay out of the default until their own conditions (SEC-FED-09, authentication/TLS for the wire protocol) are met. *Test:* default list unit test fails when a new transport name is added without an explicit decision entry.

### 12.3 Mandatory Security Tests (summary, to register in the regression catalog)

1. Row-filter, tenant, consent and virtual-filter parity between child read via federation and the same child read single-source (SEC-FED-01, 04, 05).
2. Masking-type matrix on join keys across GraphQL, WebSQL, OLAP (SEC-FED-02).
3. Unknown versus unauthorized table indistinguishability on every transport (SEC-FED-03).
4. Fan-out and limit tests (SEC-FED-08).
5. Staging at rest, permissions, encryption, cleanup (SEC-FED-07, 09).
6. Audit counts, correlation, fail-closed audit, payload hygiene (SEC-FED-10).
7. Relation flag authorization, audit, epoch, ingestion survival (SEC-FED-11).
8. Gate coverage for every transport and path, OLAP bypass closed, startup validation (SEC-FED-12, 18).
9. Error and provenance disclosure (SEC-FED-13).
10. Key injection corpus (SEC-FED-14).
11. Tenant isolation across two tenants and two sources (SEC-FED-05, 06).
12. Authorization-before-read ordering (SEC-FED-16).

### 12.4 Residual Risks (accepted)

- Inter-segment consistency window (T-13) within one request timeout.
- Per-source timing differences remain observable (single-source access has the same property).
- Delegated per-user identity on source 2 (PLAN-SCALE-5000 `WP-SC-15/17`) is out of scope; when it lands, the governed readers inherit it because they use the governed connection factory.

### 12.5 Verdict

**APPROVED WITH CONDITIONS.** The architecture is sound because it removes the second policy engine (SEC-FED-01), keeps both strategies on the governed pipeline, and closes the OLAP bypass. Phase 4 may start once the work packages carry the conditions above; Phase 5 must verify SEC-FED-01, 02, 05, 09, 10 and 12 line by line (critical), the rest by tests. Any deviation from a binding condition returns the work to Phase 2.

### 12.6 Security Review Addendum: federated query performance (#122, #123, #124; Phase 3)

*Reviewer: Security Expert. Scope: PRD section 7.1 and plan section 4.11, audited against the code at `80eb6b97` (`CrossSourcePlanner.cs`, `FederatedStagingService.cs`, `SqlTableReadExecutor.cs`, `GovernedSqlExecutionService.EnforceMaskedColumnGuardrails`).*

#### 12.6.1 Threat analysis

| ID | Threat | Analysis | Control |
|---|---|---|---|
| T-14 | **Predicate as oracle on masked/HMAC columns** | Today a filter on a masked column is refused, except HMAC columns, which are compared in DuckDB against the pseudonym (`GovernedSqlExecutionService.cs:300-310`). Pushing that predicate would evaluate it against the clear value in the source: the row count (and `ROW_LIMIT_EXCEEDED` versus success) then reveals the clear value by equality probing, defeating the HMAC. Same for subtree outputs derived from masked columns and for a masked key in a key set (the key would be sent in clear to the other source) | SEC-FED-19, 20, 26 |
| T-15 | **Can a superset violation leak rows that RLS should hide?** Reasoned through: a pushed predicate is `AND`-combined with the governed row filter and Virtual Filter arguments in the same statement (`SqlTableReadExecutor.BuildFilter`, `:327-362`), so it can only narrow what the row filter allows; it cannot reveal a row the row filter hides. Rows that DuckDB would discard but the source returns (broader collation) were already within the governed set, and DuckDB re-applies every predicate; so no leak. The real risks are the opposite and indirect: (a) **narrower** source evaluation silently drops rows (integrity, not confidentiality, but it makes results wrong without error: SEC-FED-21); (b) a predicate that **raises** in the source (cast, overflow, division, collation conflict) may be evaluated on rows the row filter hides if the optimizer evaluates the user predicate first, giving an error-versus-success oracle over hidden rows: therefore no functions, casts or arithmetic are pushed (SEC-FED-22); (c) the **count side channel**: `ROW_LIMIT_EXCEEDED` now depends on caller predicates; harmless when the cap counts only governed rows, but a table whose rows are removed gateway-side after the read (Virtual Filter enforcement, `FederatedStagingService.cs:~240` `RowsBeforeVirtualFilter`) would let the caller binary-search the number of hidden rows per predicate: excluded (SEC-FED-23); (d) plan/timing differences from indexes on the filtered column are the same as for any single-source WebSQL predicate, accepted | SEC-FED-21..23 |
| T-16 | **Key-value leakage across sources** | Key values from source A (clear, governed, visible to the caller) become bind values in source B's statements, visible to B's DBA, logs, plan cache, and monitoring; they are also copies of A-owner data inside B's owner domain. Masked, HMAC and unknown-origin keys would leak raw or pseudonymous identifiers (T-14). HTTP targets would put keys in URLs/logs | SEC-FED-26, 27 |
| T-17 | **Tenant mixing in key sets** | The key set is built from the caller's governed A rows (already tenant-filtered); a target read is itself tenant-filtered by the compiler. Risk: sharing key sets, batch results or the exact-match filter across requests/tenants, or static caches. A target source row from another tenant cannot appear because the tenant predicate is part of the governed read | SEC-FED-28 |
| T-18 | **Amplification via batches and subtrees** | Many distinct keys cause many source statements; a subtree with many tables causes a heavy source query (single-source path semantics but initiated through federation: join explosion on a production database); key waves multiply latency | SEC-FED-29 |
| T-19 | **Audit completeness** | Pushed predicates, each key batch, each subtree and fallbacks must be visible; values must not be. A subtree must not hide its member tables; an audit failure must deny | SEC-FED-30 |
| T-20 | **Subtree governance gaps** | A subtree compiled outside the governed path (hand-built SQL), or a re-defaulted decision, or functions checked against the DuckDB allowlist instead of the source's, or a masked column leaving the subtree through an expression or a window function (e.g. `max(masked)`), or a masked column used as a cross-source join key | SEC-FED-24, 25 |
| T-21 | **Injection through pushed values and keys** | Literals and parameters in pushed predicates, key values in `IN` lists | covered by SEC-FED-14 extended, SEC-FED-31 |
| T-22 | **Semantic divergence as an integrity attack** | A caller crafts case/space variants so a pushdown or key set loses or duplicates rows (duplicate rows across batches on case-insensitive sources inflate aggregates) | SEC-FED-21, 32 |

#### 12.6.2 Binding conditions (continue SEC-FED-xx)

- **SEC-FED-19 Mask rule, enforced by the pushdown selector itself.** A conjunct is pushed only if every referenced column has effective access `Clear` in the caller's decision. The selector must not rely on `EnforceMaskedColumnGuardrails` because HMAC columns pass it. Applies to single tables and to subtree outputs. One non-`Clear` reference taints the whole conjunct. *Test:* matrix of rule types (REDACT, partial, hash, HMAC, ABAC mask, deny) as filter column in `AND`, `OR`, `IN`, `LIKE`, `IS NULL`: no clause reaches the source (spy executor), result row count equals the non-optimized run; an HMAC-column filter is evaluated by DuckDB on the pseudonym only.
- **SEC-FED-20 Mask rule for join keys and key sets.** A key column that is not `Clear` on either side never forms a key set; no key value of a masked/HMAC/unknown-origin column is ever sent to a source. *Test:* spy executor records all bound parameter values per source: no masked or HMAC key (clear or pseudonym) appears; the join result equals the non-optimized result.
- **SEC-FED-21 Superset (no silent row loss).** Every pushed clause must select a superset of what DuckDB selects for the same conjunct. The pushability matrix is conservative data (unknown dialect, type class, operator, collation is not pushable); string `<>`, `NOT IN`, `NOT LIKE` and ordered comparisons are pushed only where the matrix proves the source is not narrower (4.11.2); numeric `NOT IN` with a `NULL` element is never pushed; literal/column type class mismatch is never pushed. *Test:* result parity pushdown on/off across the full case set including SQL Server case-insensitive collation, trailing spaces, accent variants, PostgreSQL, Oracle `CHAR`, SQLite `NOCASE` column; any matrix cell change requires a parity case.
- **SEC-FED-22 No error oracle through pushed expressions.** Pushed predicates contain only column-versus-constant comparison forms (4.11.2): no functions, casts, arithmetic, `COLLATE`, subqueries or non-deterministic expressions. Constants are bound parameters typed to the column's catalog type. *Test:* a conjunct such as `cast(x as int) = 1`, `1/(x-5) > 0`, `lower(name) = 'a'` is not pushed and DuckDB evaluates it; a source that would raise on hidden rows is never asked.
- **SEC-FED-23 No pushdown over gateway-side post-read enforcement.** No predicate pushdown and no key set to a table whose plan needs gateway-side removal of rows after the read (Virtual Filter enforcement with `RowsBeforeVirtualFilter`, any in-gateway row removal), until the staging cap is shown to count only governed rows. Where a cap counts rows before governance, the existing behaviour (unfiltered read, cap on the full table) stays. *Test:* a table with an enforced Virtual Filter plan receives no pushed clause; probing predicates produce the same error/success as without pushdown.
- **SEC-FED-24 Subtree through the governed single-source compile only.** A same-source subtree is compiled by the governed single-source engine (`ISqlEngine.Compile` with row filters, masks, consents, tenant predicate, `AstSecurityVisitor` guards) with the decisions already made for every table (missing decision denies, no default); no hand-assembled SQL text for a subtree. Functions inside the subtree are validated by the single-source rules of the source dialect, functions outside by the DuckDB allowlist, per scope. *Test:* parity of subtree output with a direct single-source query for two persons with different row filters, masks, consents and tenants; a function allowed only in DuckDB placed inside a subtree is rejected; one disallowed in DuckDB but valid in the source dialect is accepted only inside the subtree.
- **SEC-FED-25 Subtree output classification, fail closed.** Each subtree output column carries a classification (`Clear`, `Masked`, `DerivedFromMasked`, `Unknown`); anything other than `Clear` is treated as masked in the outer query: not a join key across sources, not a pushed-predicate or key-set column, and the outer masked-column guard applies (HMAC pseudonym only compared in DuckDB). A subtree must not expose masked columns through expressions, aggregates or window functions in a way that changes the classification (`max(masked)` is `DerivedFromMasked`). If the engine cannot classify, only direct column references may leave a subtree. *Test:* masked column joined across sources through a subtree output (direct, aliased, aggregated, windowed) is refused with the same stable error as the direct join (SEC-JOIN-01 regression).
- **SEC-FED-26 Key-set sink control.** A key set is sent only to SQL sources whose data source carries the explicit flag `allow_key_set_import` (default off, set by an owner/admin with audit, four-eyes as for other source settings, Q-8); never to HTTP, lakehouse or plugin sources in this step; key values only as bound parameters; never in logs, traces, metrics, error messages or audit. *Test:* flag off means no key set (join unchanged); flag change audited with before/after; HTTP target never receives keys; log and audit payload scan finds no key value.
- **SEC-FED-27 Keys are data, never SQL (extends SEC-FED-14).** Key lists and pushed constants are parameterized and typed from the target column; oversized or ill-typed values are not pushed (batch refused, fallback), never truncated or coerced. *Test:* the SEC-FED-14 injection corpus (quotes, SQL fragments, `%`, CRLF, very long strings, Unicode confusables, embedded NUL) against pushed predicates and key sets on every dialect.
- **SEC-FED-28 Per-request isolation of key sets and batches.** Key sets, batch results, exact-match filters, stage plans and subtree results exist only inside one request (no static, singleton or cache storage; extends SEC-FED-06), are tenant-bound through the request principal and the governed reads, and are released on completion and cancellation. REQ-GOV-107's key-set cache must not be shared. *Test:* reflection/architecture test (no static mutable state in the new types); two tenants with overlapping keys in one concurrent run see only their own rows; cancellation leaves no retained keys.
- **SEC-FED-29 Amplification bounds.** Enforced and tested: `MaxKeySetKeys`, `MaxKeySetBatches`, `MaxKeySetDepth`, batch size clamped by connector and dialect bind limit, cumulative staged rows/bytes per input across batches, `MaxTableCount` over inputs, `MaxTablesPerSubtree`, `MaxParallelSourceReads`, wall clock over all waves, per-user concurrency lease, `CostPerBatch`/`CostPerRow` through the quota middleware, subtree reads counted as source reads. Exceeding a bound falls back (key set) or fails with the stable limit code; never truncates. *Test:* each bound individually and cumulatively; 100,000 distinct keys issue at most `MaxKeySetBatches` reads (zero when above `MaxKeySetKeys`); a subtree above `MaxTablesPerSubtree` is rejected or split into single inputs.
- **SEC-FED-30 Audit per read, with structure only.** Each source read, each key batch and each subtree gets its audit record with a shared correlation id: `pushedPredicateCount`, `{column, operator}` lists, `inputKind`, `subtreeTables` (all member tables), `keySetFilter` state and counts, fallback reason; no predicate values, no keys, no rows. Audit write failure denies the request. #79 analysis output uses the same planner and carries no data. *Test:* audit count equals reads (batches and subtrees included); injected audit failure denies and returns no rows; payload scan for values and keys.
- **SEC-FED-31 Fail closed in planning.** Any unresolvable column reference, ambiguous alias, unknown dialect, unsupported construct, missing decision or classification: the conjunct/join/subtree is not optimized (executed the unoptimized way), never partially optimized. An unsafe or ambiguous case never turns into a query failure that differs from the unoptimized run. *Test:* fuzz/property test over generated predicates: optimized and unoptimized results are equal or the optimizer declines.
- **SEC-FED-32 Exact-match de-duplication per key batch.** Rows returned by a batch read are retained only if their key equals (ordinal) a key of that batch; the staged target contains each source row at most once. *Test:* case-insensitive source with keys `'a'`, `'A'`, `'a '` across two batches: no duplicates, identical join result with key sets on/off.
- **SEC-FED-33 Switches and rollout.** The three optimization switches default to `false` until all tests of 12.6.3 are green; each is independently switchable; disabled equals today's behaviour; an invalid option aborts startup. Phase 5 verifies SEC-FED-19, 20, 21, 22, 23, 24, 25, 26 line by line (critical).
- **SEC-FED-34 Subtree semantics disclosure.** Source-authoritative semantics of a subtree are documented (Phase 6) and the audit marks the input as `subtree`, so that a result that differs from the all-staged query can be explained. No weaker governance is acceptable for the sake of the semantics. *Test:* documentation present; audit field present.

#### 12.6.3 Mandatory security tests (add to the regression catalog)

13. Mask/HMAC predicate never pushed (SEC-FED-19), masked/HMAC key never in a key set (SEC-FED-20), subtree output classification (SEC-FED-25).
14. Superset parity on/off across dialects and collations (SEC-FED-21, 32), including fuzz parity (SEC-FED-31).
15. Error-oracle shapes not pushed (SEC-FED-22); gateway-enforced tables excluded (SEC-FED-23).
16. Subtree parity with two persons, function scope (SEC-FED-24).
17. Key-set sink control, injection corpus, isolation (SEC-FED-26, 27, 28).
18. Amplification and fallback bounds (SEC-FED-29); audit counts, structure-only payload (SEC-FED-30).

#### 12.6.4 Residual risks (accepted)

- Source-side timing and plan differences caused by pushed predicates equal those of single-source WebSQL on `Clear` columns.
- Key values of source A exist in source B's logs when the owner enabled `allow_key_set_import`; this is a deliberate owner decision (SEC-FED-26).
- Subtree results follow the source's collation; results can differ from the all-staged query (documented, switchable).
- Left join `WHERE` on the null-supplying side and inner-join reassociation are not optimized in this step (performance only).

#### 12.6.5 Verdict (addendum)

**APPROVED WITH CONDITIONS** for #122, #123 and #124 as designed in 4.11. #122 is acceptable because a pushed clause can only narrow an already governed read and DuckDB re-applies every predicate; the conditions that matter are the mask rule (HMAC passes the existing guard), the conservative superset matrix, and the exclusion of expressions and gateway-enforced tables. #124 is acceptable only with the owner-controlled sink flag (Q-8) and per-batch exact-match de-duplication. #123 is acceptable only if the subtree is compiled through the governed engine and output classification fails closed; if `ISqlEngine.Compile` cannot expose per-output classification, expressions stay out of subtree outputs until it can. Implementation order #122, #124, #123; Phase 4 must not enable any switch by default before the tests of 12.6.3 are green. Deviation from SEC-FED-19..34 returns the work to Phase 2.

### 12.7 Security Review Placeholder: federated parameters and co-located sources (#131, #132; Phase 3 pending)

*Not a review. Phase 1/2 name the conditions that Phase 3 (`csharp-security-expert`) must turn into binding `SEC-FED` conditions with tests. Numbering continues after SEC-FED-34. Until Phase 3 is complete the verdict for WP-FED-15/16 is **SECURITY REVIEW pending**; the optimisation switches stay `false`.*

| ID | Topic (to be defined in Phase 3) | Source of the condition |
|---|---|---|
| SEC-FED-35 | Parameters are bound, never spliced, in DuckDB and in every source statement; injection corpus for scalar and list values | REQ-FED-01 7.2.4 |
| SEC-FED-36 | Pushed value equals the DuckDB value and is typed from the column; type-class mismatch is not pushed; list above the bind budget is not pushed, never truncated | 7.2.4, SEC-FED-21/22 |
| SEC-FED-37 | One validation before any source read; no parameter-dependent error before all tables are authorized (no oracle) | 7.2.4, D-8 |
| SEC-FED-38 | Co-location declaration: explicit on every member, startup validation, connect-time server-identity verification, fallback to staging never to a wrong-identity statement; no `Delegated*`, no HTTP, SQL Server only | 7.2.4 |
| SEC-FED-39 | No authority gain: statement references only the authorized table set; post-compile AST reference check; client qualified names never select a database; database names only from configuration | 7.2.4 |
| SEC-FED-40 | Per-table governance inside a co-located statement (consents, row filters, masks, tenant, guards); correlated filters across member databases refused; masked-output classification (SEC-FED-25) per member | 7.2.4, SEC-FED-24/25 |
| SEC-FED-41 | Explicit permissions, no ownership chaining, SELECT only; permission errors mapped to a stable code without database text | 7.2.4 |
| SEC-FED-42 | Audit per table and per statement, audit failure denies; leases, limits and breakers of every member; switch default false until tests are green | 7.2.4 |

Mandatory tests (to register in the regression catalog by Phase 3): parameter injection corpus on all three engines; validation-before-read spy; pushdown on/off parity with parameters; forged-qualification attempts; unauthorized-table post-compile check; two-persons parity inside a co-located statement; startup validation negative cases; verification-failure fallback.

## 13. Changelog

- 2026-10-16: Phase 2 architecture and work packages, verified against `70b75e27`.
- 2026-10-16: Phase 3 security review appended (SEC-FED-01..18), verdict APPROVED WITH CONDITIONS.
- 2026-10-16: Section 4.11 (design for #122, #123, #124), `WP-FED-11..14`, increment INC-6, test strategy additions; Phase 3 addendum 12.6 (`SEC-FED-19..34`, verdict APPROVED WITH CONDITIONS). Code facts verified against `80eb6b97`.
- 2026-10-16: Section 4.12 (brief design for #131 and #132), `WP-FED-15`, `WP-FED-16`, waves J and K, INC-7; Phase 3 placeholders 12.7 (`SEC-FED-35..42`, review pending). Verified against `5ae3c046`.

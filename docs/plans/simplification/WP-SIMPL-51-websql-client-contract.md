# WP-SIMPL-51 WebSQL client contract: cost and quota, typed columns, syntax positions, paging
Status: Ready for implementation (Phase 3 approved; owner confirmed all defaults 2026-11-07; implementation not started, waits for an explicit owner go)
Parent: [plan](../2026-11-05-plan-project-simplification.md) | Issues: [#527](https://github.com/themulle/autheris/issues/527), [#528](https://github.com/themulle/autheris/issues/528), [#529](https://github.com/themulle/autheris/issues/529), [#533](https://github.com/themulle/autheris/issues/533) (all opened 2026-11-05, v1.7.0 observations) | Related: #530 and #558 (closed, error-code fixes), WP-SIMPL-45 (WebSQL write path), WP-SIMPL-42 (usage accounting) | Freeze: feature work, runs under `freeze-exception` like WP-SIMPL-38..48 or after the freeze (see D-51e)

How to read this plan: section 5 is the single design source per item; every Phase 3 condition is folded into the sentence it amends and tagged `(SEC-51-xx)`. Section 6 is the single test list, grouped per PR. Section 9 lists per PR its files, dependencies and binding conditions. Section 13 is only an index.

## 1. Goal and outcome
`POST /api/v1/sql` becomes a client contract that SQL-editor and BI-style clients can build on without re-implementing gateway policy: expensive statements are priced and limited per caller from the same budget as GraphQL (#527), every output column carries a type, a nullability and a mask flag (#528), syntax errors carry a position and a trailing semicolon and comments are accepted (#529), and a plain ordered statement can be paged without the client rewriting it (#533). Rights, masks, row filters, cost, quota and audit apply unchanged to every page.

## 2. Preconditions and dependencies
- PR 1 (I3) and PR 3 (quota extraction) have no dependency. PR 2 (I2) touches `WebSqlEndpoints.Execution.cs` and the executor result path: after WP-SIMPL-45 PR 4. PR 4 and PR 5 touch `GovernedSqlExecutor`, `GovernedSqlRewriter` and `WebSqlOptions`: after WP-SIMPL-45 PR 5 (audit sequence, budgets) and PR 6.
- WP-SIMPL-42 is optional: the quota key is `ClientQuotaContext.SubjectId` (tenant, SID, scope) as for GraphQL today; when WP-SIMPL-42 is merged, the `IConsumerUsageRecorder` deny records of 5.4 carry its group attribution without code changes here.
- No dependency on WP-SIMPL-50 (edited in parallel; no shared files).

## 3. Scope
### In scope
- **I1 (#527)**: static cost estimate of every client SQL statement (WebSQL JSON, Parquet, Trino sync and async, cross-source) before execution; per-statement ceiling; reservation and settlement against the caller's existing cost bucket (`IRateLimiterService`), shared with GraphQL through one extracted service; measured execution seconds charged at settlement.
- **I2 (#528)**: `columns` becomes `[{ "name", "type", "nullable", "masked" }]`.
- **I3 (#529)**: `SQL_SYNTAX_ERROR` problem details gain `line` and `column`; one trailing `;` is accepted; comments are accepted on the client SQL path and reach neither the source nor the audit text.
- **I4 (#533)**: request `page`, response `next` (HMAC-bound continuation token), OFFSET injected on the governed AST.
### Out of scope
- Server-side cursors or snapshots (D-51b); `EXPLAIN` round trips (D-51a).
- GraphQL paging extension (dropped, D-51j); Trino `errorLocation` (follow-up, D-51k); Arrow export endpoints.

## 4. Current state (verified 2026-11-07 on `origin/main` 5bbb0576)
Line numbers in this plan refer to `origin/main` at verification (5bbb0576; the D-51n facts were re-verified on 6aacba08). Symbols are authoritative: PR 2 and PR 4 must re-locate their anchors after WP-SIMPL-45 PR 4 and PR 5 are merged.
- `WebSqlEndpoints.cs:24` `WebSqlRequestDto(Sql, Parameters, DataSource)`; `:49/:56/:63` map `/api/v1/sql`, `/api/sql`, `/v1/statement` to one handler `HandleWebSqlRequest` (`Execution.cs:40`). Trino route and `wait_timeout` go to `IWebSqlStatementManager` (`Execution.cs:136-165`); Parquet to `HandleParquetWebSqlRequestAsync` (`:304`). JSON is streamed lazily: `columns` as strings from `SqlResultColumns.UniqueNames` (`:213-220`), then `rows`, then `rowCount` and `truncated` (`RowLimitedDataReader.HasMoreRows`, `:259`, `:276-277`).
- Errors: `WriteWebSqlErrorAsync` (`Governance.cs:28`) -> `SqlCompilerErrorMapper.TryMap` -> `SqlErrorResponse.WriteAsync` (fixed message, optional `limit` extension). `SqlErrorMapping(Status, Code, Title, Limit)` (`SqlCompilerErrorMapper.cs:23`); `ParseCanceledException`/`RecognitionException` -> 400 `SQL_SYNTAX_ERROR` (`:116`). `ApiErrorCodes` registers every `GovernedSqlErrorCodes` constant by reflection (`ApiErrorCodes.cs:167-173`); `ApiErrorCodes.ServiceUnavailable = "SERVICE_UNAVAILABLE"` exists (`:43`).
- Parse: `GovernedSqlRewriter.AnalysisTokenOptions` (`:49-53`, `Analyze`), `CompileRequestFactory.TokenGuards` (`:59-72`, `Compile`) and `FederatedDuckDbExecutionService.AnalysisTokenOptions` (`:42`) all set `RejectComments = true`: comments are rejected today (the draft assumed "accepted"). Grammar `singleStatement: statement EOF` (`SqlBase.g4:31`); `SEMICOLON` is a token (`:1474`), so `SELECT 1;` fails. Comments and whitespace are hidden-channel (`:1556-1565`). `ThrowingErrorListener` (`FastSqlEngine.cs:17-29`) throws `ParseCanceledException("line L:C: msg")`; the position survives only as text and the mapper drops it. `SyntaxErrorNegativeCache` stores the message only (`Parsing/SyntaxErrorNegativeCache.cs:22`). Nested block comments are always rejected (`FastSqlEngine.cs:528-535`). Every `EnsureTokensAreSafe` positional throw is catalog-independent (UESCAPE, brackets, `$$`, non-ASCII, dotted quoted identifiers, time travel, nesting depth).
- Audit text: `CompiledSqlAuditDigester.Anonymize` -> `AstSecurityVisitor.RedactSqlLiterals` (`AstSecurityVisitor.Support.cs:271`) keeps every hidden token, including comment text.
- Row cap: `SqlRowLimit.Effective`; rewriter `:650-654` computes `deliveredRowLimit` and `enforcedMaxRows = max + 1` (probe row); `AstSecurityVisitor.Select.cs:111-123` clamps the root `PaginationClause.Limit` (`ClampLimit`, `WithTies = false`) or injects `new PaginationClause(null, new LiteralExpression(EnforcedMaxRows, Integer))`. Every generator renders `Offset`/`Limit` through `SqlDialectGeneratorBase.GenerateStructuralInteger` (`:137`), which inlines an integer literal as text (`AppendInlineInteger`), never as a bind parameter (`PostgreSqlDialectGenerator:336`, `SqlServerDialectGenerator:643`, `OracleDialectGenerator:531`, `DatabricksDialectGenerator:76`, `DuckDbDialectGenerator` in `AnalyticalDialectGenerators.cs:336`).
- Template cache: `GovernedSqlCompiler.cs:128` keys the cache with `CompileCacheKey.Material(request, rawSql, caps, MaxQueryLength)`, which serializes every public property of `CompileRequest` (so `EnforcedMaxRows` is a key component). `CompiledSqlTemplateCache(int maxEntries = 512)` is LRU-bounded and exposes `Stats` (`Hits`, `Misses`, `Entries`). `CompiledSqlTemplate.Rebind` re-supplies only slots of origin `Tenant`/`Policy`/`Mask` and throws `SecurityException` on a missing or mistyped value; `ParameterOrigin { ClientNamed, ClientPositional, QueryLiteral, Tenant, Policy, Mask }` (`CompiledSql.cs:10`).
- Masks in WebSQL are `MaskExpression` projections of the secured table source (`AstSecurityVisitor.Tables.cs:97-101`); lineage helper `ExpressionReferencesMaskedColumn` (`MaskGuards.AggregateAndDerived.cs:394`). `TreeSqlCompiler.CollectMaskedColumns` serves the GraphQL tree path only and is not usable for WebSQL (draft corrected). `SELECT *` expands to visible catalog columns (`EnforceCatalogProjection = true`). `SqlResultColumns.Describe` and `TrinoColumnTypes.Map` already type results for the Trino protocol.
- Cost: `CostAndQuotaMiddleware` (GraphQL only): `IClientTierResolver.ResolveAsync` -> `ClientQuotaContext(SubjectId, Tier, Policy, Scope)`; over-cost -> 400 `QUERY_COST_QUOTA_EXCEEDED`; `ReserveCostQuotaAsync` denied -> 429 `RATE_LIMIT_EXCEEDED` with `Retry-After`; `SettleCostQuotaAsync(reserved, actual)` in `finally`; failure fee `max(BaseCost, FailureFee)` (`CostAndQuotaMiddleware.cs:384`). Weights: `QueryCostOptions` (`Gateway:QueryCost`: `BaseCost 1` `[1,1000]`, `RowCostFactor 0.5`, `FailureFee 1` `[1,1000]`, validated in `GatewayStartupValidator.cs:734`); the SQL dry-run analysis has its own formula (`QueryAnalysisService.Sql.cs:371-376`, `QueryAnalysisOptions`: `CostPerJoin 25`, `CostPerSubqueryDepth 30`, `UnboundedScanPenalty 50`). WebSQL is bounded only by timeout, row cap, `ResourceGroupMiddleware` concurrency and `QueryGovernorOptions` connection limits. `TableMetadata.EstimatedRowCount` exists (`TableModels.cs:419`). `SqlQueryMetadata` (`TrinoSqlEngine/Analysis/ISqlQueryAnalyzer.cs`) has `JoinCount`, `MaxSubqueryDepth`, `HasExplicitLimit`, `TableFunctionCalls`. Free tier `MaxTokensCapacity` is 100. `WITH RECURSIVE` is enablable for reads.
- GraphQL offsets: `GraphQlOptions.MaxAllowedOffset` `[Range(0, 10000)] = 10000`, enforced in `GatewayExecutionService.cs:164`, `GovernedTreeQueryService.cs:272`, `Query.Catalog.cs:37` (`INVALID_OFFSET`); GraphQL already pages with `first`/`offset` and keyset connections.
- Keys: `CompiledSqlAuditDigester` resolves the audit key (`GovernanceDb:AuditHmacKeyVaultRef`, else HKDF of `DataMasking:HmacSecretKeyVaultRef`) with no ephemeral fallback outside Development/Test (`:119-164`); every WebSQL statement already requires it. `KeysetCursorEncoder` (BulkStream key, per-process fallback) and `AccessPlanningService.ResolveSigningKey` (HKDF of a secret ref) are the existing token patterns. `GetCallerIdentity` falls back to `Identity.Name` when no SID exists.
- Audit: `GovernedSqlExecutor.cs:343-367` writes `WebSqlQuery` ALLOW (`originalSql` redacted, `originalSqlHash`, `dataSource`, compiled fields) before the connection opens; `:274-320` writes `WebSqlQueryDenied` / `WebSqlDmlRejected` for audited rejections.

## 5. Design
### 5.1 I3 syntax position, terminator, comments (PR 1)
1. `src/TrinoSqlEngine/SqlSyntaxErrorException.cs` (new): `public sealed class SqlSyntaxErrorException : ParseCanceledException { public int Line { get; } public int Column { get; } }` (both 1-based; column = `charPositionInLine + 1`). Thrown only by the lexer, the parser (both `ThrowingErrorListener.SyntaxError` overloads) and the token guard (every token-position throw in `EnsureTokensAreSafe`, all catalog-independent) (SEC-51-01). Policy, unknown-object, compile-not-supported, cost, paging and DML errors never carry a position, and no semantic error is ever converted into a `SqlSyntaxErrorException` (SEC-51-01). Parse-budget timeouts and capacity rejections stay plain `ParseCanceledException` without position (SEC-51-01). `SyntaxErrorNegativeCache.Entry` gains `int Line, int Column` and rethrows a `SqlSyntaxErrorException`.
2. `SqlTokenSecurityOptions.AllowTrailingStatementTerminator` (bool, default false). The whole `SqlTokenSecurityOptions` record, including the new flag, stays part of the negative-cache key, so an entry created under lenient options is never served to a strict parser (SEC-51-04). In `FastSqlEngine.ParseOnCurrentThread`, after `tokens.Fill()` and before `EnsureTokensAreSafe`: when set and the last default-channel token before EOF is `SEMICOLON`, move that one token to the hidden channel. Only the last default-channel token is considered, so a comment cannot hide a second statement (SEC-51-04): `SELECT 1;;` and `SELECT 1; SELECT 2` stay `SQL_SYNTAX_ERROR`, positioned at the second `;` or the second `SELECT`.
3. `src/Autheris.Application/Sql/Policy/ClientSqlTokenPolicy.cs` (new): `static SqlTokenSecurityOptions Analysis` and `Compile(TargetSqlDialect)`, used by `GovernedSqlRewriter` (`:49`), `CompileRequestFactory` (`:59`) and `FederatedDuckDbExecutionService` (`:42`); identical to today's options except `RejectComments = false`, `AllowTrailingStatementTerminator = true`. Comments are accepted only through `ClientSqlTokenPolicy`; every path that forwards client text to a source or staging engine (OLAP staging, `SqlSecurityValidator` fragments, policy predicates, SQL endpoints) keeps `RejectComments = true` (SEC-51-02). Nested block comments stay rejected. The emitted SQL is regenerated from the AST and contains no hidden-channel token in any dialect, so `/*+ ... */` optimizer hints never reach SQL Server, Oracle or Databricks (SEC-51-02). Comment and whitespace tokens are excluded from the other token checks only because they are never emitted (SEC-51-03).
4. Audit and logs: `RedactSqlLiterals` emits one space for each `SIMPLE_COMMENT`/`BRACKETED_COMMENT` token (comment text would bypass literal redaction), and this redaction runs before any audit, log or negative-cache-message use of the text (SEC-51-03). Raw client SQL is not logged at Information or above (SEC-51-03).
5. Error body: `SqlErrorMapping` gains `int? Line = null, int? Column = null`, filled by `TryMap` only for `SqlSyntaxErrorException` (SEC-51-01); `SqlErrorResponse.WriteAsync` adds extension members `line`, `column` when set. Example: `{"type":"...","title":"Invalid SQL syntax.","status":400,"code":"SQL_SYNTAX_ERROR","line":3,"column":14,"traceId":"..."}`. Never the ANTLR message, token text or a fragment.

### 5.2 I2 typed columns and mask flags (PR 2)
1. `SqlResultColumn` gains `bool? AllowDbNull = null` (from `DbColumn.AllowDBNull` in `Describe`).
2. `CompiledSql` gains `public ImmutableArray<bool> OutputColumnMasks { get; internal init; }` (one entry per root output column). It lives in the same template-cache entry as the compiled template, whose key already includes the caller's policy fingerprint; it is never cached by statement text alone (SEC-51-06). `AstSecurityVisitor` sets it after the root select is secured: per root select item (after `*` expansion) `true` when `ExpressionReferencesMaskedColumn` finds a `MaskExpression` in its lineage through derived tables, CTEs and set operations (masked in any branch = masked). Lineage that cannot be resolved yields `true` (fail toward masked) (SEC-51-05). Empty for DML.
3. Federated: `FederatedDuckDbExecutionService` computes the same array on the root DuckDB statement via `IsMaskedByName` over `SourceSubtreeModel.MaskedColumns`; a name or alias the matcher cannot resolve yields `masked = true` (SEC-51-05).
4. Row writer becomes `Func<DbDataReader, GovernedResultInfo, CancellationToken, Task>` with `public sealed record GovernedResultInfo(IReadOnlyList<SqlResultColumn> Columns, ImmutableArray<bool> MaskedColumns)` (`Autheris.Application.Sql`). Callers: `WebSqlEndpoints.Execution.cs:198, :333`, `TableStreamEndpoints.cs:362, :483` (ignore it), `GovernedSqlExecutionService.cs:139, :144`. Mask array length != `FieldCount` -> `SQL_COMPILER_INVARIANT` 500 (fail closed).
5. JSON: `{"name":"email","type":"varchar","nullable":true,"masked":true}`. `masked`, `type` and `nullable` exist only for delivered columns; hidden columns never appear (SEC-51-05). `type` = `TrinoColumnTypes.Map(column, []).Name` (delivered type; for a masked column the mask output type). `nullable` = `masked ? true : (AllowDbNull ?? true)`: a masked column is always `nullable: true`, so source nullability does not leak through the mask (SEC-51-05). Same for `format=arrays`; empty result keeps `"columns":[]`; OpenAPI spec updated; Parquet unchanged. Breaking, no compatibility flag (no production).

### 5.3 I1 shared quota service (PR 3)
`src/Autheris.Application/Common/Quota/IQueryCostQuota.cs` (new):
```csharp
public enum QueryCostOutcome { Admitted, OverCost, QuotaExhausted, Unmetered }
public sealed record QueryCostRequest(ClaimsPrincipal Principal, string? ApiKey, string? ClientIp, int EstimatedCost, int TransportMaxCost, bool CapByTierMaxCostPerQuery);
public sealed record QueryCostAdmission(QueryCostOutcome Outcome, string SubjectId, ClientQuotaPolicy Policy, int ReservedCost, int MaxAllowedCost, int RetryAfterSeconds, string? Scope);
public interface IQueryCostQuota
{
    Task<QueryCostAdmission> AdmitAsync(QueryCostRequest request, CancellationToken ct);
    Task<CostQuotaResult?> SettleAsync(QueryCostAdmission admission, int actualCost, CancellationToken ct); // null unless Admitted
    Task ChargeFailureFeeAsync(ClaimsPrincipal principal, CancellationToken ct); // settle-only debit, SEC-51-11
}
```
`QueryCostQuota` (singleton) takes over tier resolution, the ceiling `maxAllowed = CapByTier ? min(policy.MaxCostPerQuery, TransportMaxCost) : TransportMaxCost`, further capped at `policy.MaxTokensCapacity` (a never-admissible statement is `OverCost`, not an endless 429), reservation and settlement. `Unmetered` only when rate limiting is disabled by configuration (`IsRateLimitingDisabled`; the ceiling still applies) (SEC-51-10). If tier resolution or reservation throws (for example Redis down), `AdmitAsync` propagates the exception and never returns `Unmetered` (SEC-51-10). `FailureFee` is the existing `QueryCostOptions.FailureFee` (`Gateway:QueryCost:FailureFee`, default 1, `[1, 1000]`); the fee charged is `failureFee = max(QueryCostOptions.BaseCost, QueryCostOptions.FailureFee)` (default 1), computed once in `QueryCostQuota` and reused by GraphQL. `ChargeFailureFeeAsync` resolves the caller's bucket and debits `failureFee` without a reservation check (SEC-51-11). GraphQL keeps its cost calculation, error shaping, headers and `extensions.cost` (`CapByTier = true`, `TransportMaxCost = EffectiveGatewayMaxCost`). `SettleCostQuotaAsync` must debit `actual - reserved` even into a negative balance (verify `InMemoryRateLimiterService` and `RedisRateLimiterService`; fix here if clamped).

### 5.4 I1 SQL cost estimate and gate (PR 4)
1. `SqlQueryMetadata` gains init properties, set by `SqlQueryAnalyzer` over the whole client statement (SEC-51-07, SEC-51-08): `CrossJoinCount` (X, see below), `SetOperationCount` (U), `HasRecursiveCte`, `HasRootOrderBy`, `HasRootOffset`. `JoinCount` and `MaxSubqueryDepth` are recounted with the same whole-statement rules.
2. `src/Autheris.Application/Sql/Cost/SqlStatementCostEstimator.cs` (new, static, pure). The estimate is computed from the analysis of the client statement, never from the secured AST, so `estimatedCost` cannot reveal injected row filters, masks or KeySets; only `EstimatedRowCount` of tables the rewrite admitted is read (SEC-51-07).
   `C_est = BaseCost + ceil(RowCostFactor * R) + CostPerJoin * (J - X) + CrossJoinCost * sum(k_i, i = 1..X) + CostPerSubqueryDepth * D + SetOperationCost * U + (bounded ? 0 : UnboundedScanPenalty) + (recursive ? UnboundedScanPenalty + 4 * CrossJoinCost : 0) + ceil(O / OffsetRowsPerCostUnit)` (SEC-51-08).
   Symbols. `BaseCost`, `RowCostFactor`: `QueryCostOptions`. `CostPerJoin`, `CostPerSubqueryDepth`, `UnboundedScanPenalty`: `QueryAnalysisOptions`. `CrossJoinCost`, `SetOperationCost`, `OffsetRowsPerCostUnit`: `WebSqlOptions.Cost` (5.4.4). R = rows delivered at most (`SqlRowLimit.Effective(explicitLimit)`, or the page size when paged). J = all join operators (explicit joins, comma joins, `LATERAL`, `CROSS APPLY`/`OUTER APPLY`, `UNNEST` and table-valued functions in `FROM`), counted over the whole statement: every subquery, every set-operation branch, and each CTE body once per reference (a CTE referenced twice counts twice) (SEC-51-08); J includes every cross join, so `J - X >= 0`. X = cross joins: `CROSS JOIN`, comma joins, joins whose `ON` is a constant, plus every `LATERAL`/`APPLY`/`UNNEST`/table-valued function, counted with the same whole-statement rules (SEC-51-08). k_i = 1 when both relations of cross join i have a known `EstimatedRowCount <= 1000`, else 4 (unknown = large); `LATERAL`/`APPLY`/`UNNEST`/table-valued functions always have unknown cardinality, so k = 4 (SEC-51-08). D = `MaxSubqueryDepth` (deepest nesting of subqueries and CTE bodies; a CTE body counts as depth 1). U = number of `UNION`/`INTERSECT`/`EXCEPT` operators over the whole statement. bounded = explicit root LIMIT/FETCH or paged. recursive = `WITH RECURSIVE` present: adds `UnboundedScanPenalty + CrossJoinCost * 4` regardless of LIMIT (SEC-51-08). O = page offset (0 unpaged). Views are priced as one relation (their own `EstimatedRowCount`, unknown = large) and not expanded; measured execution seconds at settlement are the backstop (SEC-51-08). All terms saturating, result clamped to `[1, int.MaxValue]`. DML (WP-SIMPL-45): `BaseCost + DmlStatementCost + ceil(RowCostFactor * effective MaxAffectedRows)`: never free, never paged. `QueryAnalysisService.Sql.cs:371-376` switches to the estimator, so dry-run analysis reports what WebSQL enforces (its field term is dropped).
   Example 1: no LIMIT, 1000-row cap, one inner join: `1 + 500 + 25*1 + 50 = 576`.
   Example 2: `WITH o AS (SELECT .. FROM orders o JOIN customers c ON ..) SELECT .. FROM o a JOIN o b ON a.id = b.id UNION ALL SELECT .. FROM sales s CROSS JOIN region r LIMIT 100`, `region` 12 rows, `sales` 5M rows: R = 100, J = 2 (CTE join, two references) + 1 (a-b) + 1 (cross) = 4, X = 1 with k = 4 (sales > 1000), D = 1, U = 1, bounded, not recursive, O = 0: `1 + 50 + 25*3 + 250*4 + 30*1 + 25*1 + 0 + 0 + 0 = 1181`.
3. Actual cost at settlement: `BaseCost + ceil(RowCostFactor * rowsDelivered) + ceil(ExecutionSecondCost * elapsedSeconds)` (DML: affected rows); on failure, timeout or abort `max(failureFee, that value)` with `failureFee` from 5.3. Elapsed runs from command start to reader close, timeouts included.
4. Options `WebSqlOptions.Cost` (`Gateway:WebSql:Cost`, `[Range]` validated at start): `Enabled` true; `MaxStatementCost` 2000 `[1, 1_000_000]`; `CrossJoinCost` 250 `[0, 100_000]`; `SetOperationCost` 25 `[0, 10_000]`; `OffsetRowsPerCostUnit` 100 `[1, 1_000_000]`; `ExecutionSecondCost` 10 `[0, 10_000]`; `DmlStatementCost` 50 `[0, 100_000]`. Other weights come only from `QueryCostOptions` / `QueryAnalysisOptions`. `Enabled = false` fails the start outside Development (SEC-51-12).
5. Gate: in `GovernedSqlExecutor` after the rewrite and before the ALLOW audit or the DML intent audit, and in `FederatedDuckDbExecutionService` after its `Analyze`, for `Transport is WebSql or Trino && !PlannedTableRead`: `AdmitAsync(new(user, null, null, C_est, MaxStatementCost, CapByTierMaxCostPerQuery: false))`. `PlannedTableRead` is set only by server code, never from request data (SEC-51-12). Quota key `ClientQuotaContext.SubjectId`; window and capacity are the caller's tier bucket, shared with GraphQL. If `AdmitAsync` throws, the statement is rejected before any source connection with 503 `SERVICE_UNAVAILABLE` and a fixed message, no internal detail; it is never executed unmetered (SEC-51-10, default taken: existing code).
6. Settlement: in `finally` with `CancellationToken.None` on success, error, timeout, client abort, Trino `DELETE` cancel and abandoned async sessions (SEC-51-09). For Trino async, the admission (subject, tier) is captured when the statement is submitted and carried into the background job, never resolved when the job runs (SEC-51-09). A failing settle is logged and never turns a denied or failed statement into success (SEC-51-09).
7. Charging of denied statements (D-51m): policy-denied statements (`SQL_POLICY_DENIED`, forbidden table, DML rejection) call `ChargeFailureFeeAsync` (settle-only debit of `failureFee`, no reservation check), so schema probing drains the bucket (SEC-51-11). Syntax errors, token-guard errors and paging-token failures are not charged (no catalog oracle; bounded by `PostAuthSid` rate limiting and the parse budget) (SEC-51-11).
8. Errors (new `GovernedSqlErrorCodes`, auto-registered): `QueryCostExceeded = "QUERY_COST_QUOTA_EXCEEDED"` -> 400, fixed message "The statement cost exceeds the allowed limit. Add a LIMIT, a filter or fewer joins.", extensions `estimatedCost`, `maxAllowedCost`; `RateLimitExceeded = "RATE_LIMIT_EXCEEDED"` -> 429 with `Retry-After` and `retryAfterSeconds`. Exceptions `SqlCostExceededException`, `SqlQuotaExhaustedException(int retryAfterSeconds)` and `SqlQuotaUnavailableException` (-> 503 `SERVICE_UNAVAILABLE`) mapped in `SqlCompilerErrorMapper`; the Trino route returns its standard error with the same code.
9. Audit: ALLOW `WebSqlQuery` gains `cost: { estimated, maxAllowed, tier }`; denies write `WebSqlQueryDenied` (DML `WebSqlDmlRejected`) with `reason` = code and `estimatedCost`; `IConsumerUsageRecorder` records `DenyCost` / `DenyRateLimit` like GraphQL. The JSON body ends with `"cost":{"estimated":576,"actual":12}` only when `policy.ExposeCostExtensions`.

### 5.5 I4 paging (PR 5)
1. Request: `WebSqlRequestDto(..., WebSqlPageRequest? Page = null, string? Next = null)`, `public sealed record WebSqlPageRequest(int? Size)`. First page `{"sql":"SELECT id, email FROM crm.customers ORDER BY id","page":{"size":500}}`; next `{"sql":"<same text>","parameters":{...same...},"next":"eyJ2Ijox...Q.kM3x..."}` (`page` optional; if present its size must equal the token size, else `SQL_PAGING_TOKEN_INVALID`) (SEC-51-15).
2. Response: `{"columns":[...],"rows":[...],"rowCount":500,"truncated":true,"page":{"number":1,"offset":0,"size":500},"next":"eyJ2Ijox..."}`; the last page has no `next`. If paging is impossible, page 1 is served with the normal row cap, no `next`, and `"pagingUnavailable":"order_by_required" | "statement_has_limit" | "federated" | "offset_limit" | "caller_not_bindable"` (the last for a principal without SID, SEC-51-14).
3. Validation order. Endpoint, before any token decoding (SEC-51-15): paging only on JSON `POST /api/v1/sql` and `/api/sql` without `wait_timeout`; `page` or `next` on the Trino route, Parquet or with `wait_timeout` -> 400 `SQL_PAGING_UNSUPPORTED` (new code); `page.size` in `[1, SqlRowLimit.For(WebSql).MaxAllowedRows]` else 400 `BAD_REQUEST`; `next` longer than 512 chars -> `SQL_PAGING_TOKEN_INVALID`. Executor: the token is decoded only after the statement class is known; `next` or `page` with any DML statement -> `SQL_PAGING_UNSUPPORTED` without decoding (SEC-51-15).
4. `IWebSqlPageTokenCodec` (`src/Autheris.Application/Sql/Paging/WebSqlPageTokenCodec.cs`, singleton): token = `base64url(payload) "." base64url(MAC)`, payload `{"v":1,"o":<offset>,"n":<size>,"p":<page>,"exp":<unix s>}`; page size and page number are inside the MAC'd payload (SEC-51-15). `MAC = HMAC-SHA256(K, label || len(payload) || payload || len(f1) || f1 || ... || len(f6) || f6)` with `label = "autheris-wsql-page/v1"` and every length a fixed 4-byte big-endian prefix (unambiguous framing) (SEC-51-13). Binding fields f1..f6, never in the payload: SHA-256 of the exact UTF-8 statement, SHA-256 of canonical parameter JSON (sorted names, typed values), caller SID from `SecurityPrincipalContext.UserSid` (never the `Identity.Name` fallback of `GetCallerIdentity`), tenant, the resolved data-source id (not the requested string), transport (SEC-51-14). A principal without SID gets no `next` (`caller_not_bindable`), never a name-bound token (SEC-51-14). K = `HKDF-SHA256(auditKey, info = "autheris/websql-page-token/v1")` over the audit key `CompiledSqlAuditDigester` resolves (new `DerivePurposeKey(string info)`); the info string differs from every existing HKDF info in the code base; key bytes are never logged (SEC-51-13). No new secret, same on every node, no ephemeral fallback outside Development/Test (D-51g). Checks: fixed-time MAC comparison (SEC-51-13), `v == 1`, `exp > now`, `o <= MaxOffset`, `n` within limits. Every failure -> 400 `SQL_PAGING_TOKEN_INVALID` (new code, one fixed message) plus `WebSqlQueryDenied` audit with `reason` only; not charged (SEC-51-11).
5. Options `WebSqlOptions.Paging` (`Gateway:WebSql:Paging`), validated at start (SEC-51-12): `Enabled` true; `MaxOffset` 10_000 `[0, 1_000_000]` (like `GraphQL:MaxAllowedOffset`); `TokenLifetimeMinutes` 15 `[1, 1440]`. A next offset above `MaxOffset` -> no `next`, `offset_limit`.
6. Governed AST (D-51n). `GovernedSqlQueryRequest` gains `SqlPageWindow? Page` (`public sealed record SqlPageWindow(long Offset, int Size, int Number)`) with `RowLimit = new SqlRowLimit(Size, Size)`. Rewriter: DML -> `SQL_PAGING_UNSUPPORTED`; `!HasRootOrderBy`, root LIMIT/FETCH/OFFSET or federated -> page 1 runs unpaged with `pagingUnavailable`, page > 1 -> `SQL_PAGING_TOKEN_INVALID`. Otherwise:
   - Template key: `CompileRequest` and `AstSecurityVisitorOptions` gain `bool BindPageOffset` (key component, value-free); the offset value is never a public property of `CompileRequest` and never in `CompileCacheKey.Material` (SEC-51-17). `EnforcedMaxRows = size + 1` stays as today (probe row; an inline literal and key component, bounded by `MaxAllowedRows` distinct values and the LRU cap of 512).
   - `AstSecurityVisitor.Select.cs:111-123` (inject branch) sets the root `PaginationClause(Offset: new PageOffsetParameterExpression(), Limit: LiteralExpression(size + 1))` when `BindPageOffset`; page 1 also uses the slot (value 0), so all pages of one statement share one template.
   - Rendering: `SqlDialectGeneratorBase.GenerateStructuralInteger` emits `PageOffsetParameterExpression` through a new `SqlEmitterContext.BindPageOffset()` that adds a `BoundParameter` with new `ParameterOrigin.PageOffset`, type BIGINT, `Value = null`, in the dialect's existing marker style. PostgreSQL `ORDER BY .. LIMIT 501 OFFSET <marker>`; SQL Server `ORDER BY .. OFFSET <marker> ROWS FETCH NEXT 501 ROWS ONLY`; Oracle `ORDER BY .. OFFSET <marker> ROWS FETCH NEXT 501 ROWS ONLY`; Databricks and DuckDB `ORDER BY .. LIMIT 501 OFFSET <marker>`. `EmittedSqlInvariantChecker` accepts exactly one `PageOffset` slot, only in the root OFFSET position.
   - Binding after lookup: `CompiledSqlTemplate.Rebind(policyValues, long? pageOffset)` (and the miss path through the same method) fills the `PageOffset` slot with the server-issued, MAC-verified offset; a `PageOffset` slot without a value, a value without a slot, or a value outside `[0, MaxOffset]` throws `SecurityException` (fail closed). Databricks fallback: if the Databricks binder or driver rejects a marker in OFFSET (checked by the first red test of PR 5), the Databricks binder replaces that one marker with the invariant-culture digits of the validated `long` at bind time, still after template lookup, so the cache does not grow either way.
7. Row filters, masks, KeySets, cost and quota run per page (D-51c; R = size, O = offset). Every page re-runs authorization, rewrite, row filters, masks, KeySets, cost gate and ALLOW audit with the current policy; a revoked grant fails the next page with the normal deny; there is no per-token server state (SEC-51-16). Audit `WebSqlQuery` gains `page: { number, offset, size }`; `originalSql` stays the client text.

## 6. Tests (red first; single list; S = `[Trait("Category","Security")]`)
Each line: name, S tag, arrange -> assert, conditions proven. Paths below are under `tests/Autheris.Tests.Unit/`.

### PR 1 (I3)
`TrinoSqlEngine/FastSqlEngineSyntaxPositionTests.cs`
- `SyntaxError_CarriesLineAndColumn`: `SELECT\n  FROM` -> `SqlSyntaxErrorException` 2/3.
- `NegativeCacheHit_KeepsPosition`: same bad text twice -> second (cached) throw has 2/3.
- `TrailingSemicolon_Accepted_WhenAllowed`: `SELECT 1;` with client options -> parses.
- `TrailingSemicolonThenComment_Accepted`: `SELECT 1; -- x` -> parses.
- `TrailingSemicolon_Rejected_WhenNotAllowed`: strict options -> syntax error.
- S `TwoSemicolons_Rejected_AtSecond`: `SELECT 1;;` -> error at second `;`. SEC-51-04.
- S `SecondStatement_Rejected_AtSecondStatement`: `SELECT 1; SELECT 2` -> error at second `SELECT`. SEC-51-04.
- S `PolicyDeniedColumn_HasNoPosition`: denied column -> mapped error has no `line`/`column`. SEC-51-01.
- S `UnknownTable_HasNoPosition`: unknown table -> no position. SEC-51-01.
- S `ParseTimeout_HasNoPosition`: exhausted parse budget -> plain `ParseCanceledException`. SEC-51-01.
- S `StrictOptions_NotServedFromLenientNegativeCacheEntry`: cache entry under client options, then strict parse of `SELECT 1;` -> strict rejection. SEC-51-04.

`Sql/Errors/SqlCompilerErrorMapperTests.cs` (extend)
- S `SyntaxError_Body_HasPosition_AndNoStatementText`: body has `line`/`column`, no token of the statement, no ANTLR text. SEC-51-01.

`Security/WebSqlCommentPolicyTests.cs`
- S `Comments_Accepted_ForClientSql`: `--` and `/* */` in client SQL -> compiles. SEC-51-02.
- S `Comments_NotForwardedToSource_AllDialects`: `CompiledSql.Sql` has no `--` or `/*` for PG, MSSQL, Oracle, Databricks, DuckDB. SEC-51-02.
- S `OptimizerHint_NotEmitted_SqlServerOracleDatabricks`: `/*+ FULL(t) */` -> absent from emitted SQL. SEC-51-02.
- S `NestedBlockComment_StillRejected`: `/* /* */ */` -> syntax error. SEC-51-02.
- S `CommentCannotHideSecondStatement`: `SELECT 1 /* x */ ; DROP TABLE t` -> rejected. SEC-51-04.
- S `FragmentValidator_StillRejectsComments`: `SqlSecurityValidator` fragment with comment -> rejected. SEC-51-02.
- S `OlapStagingAndSqlEndpoints_StillRejectComments`: each non-client path's options have `RejectComments = true`. SEC-51-02.
- S `CommentWithNonAsciiAndDollar_NotForwardedOrAudited`: comment with `ü` and `$$` -> accepted, absent from emitted SQL and audit text. SEC-51-02, SEC-51-03.
- S `RawClientSql_NotLoggedAtInformationOrAbove`: capture logger over a WebSQL run -> no Information+ entry contains the statement. SEC-51-03.

`Sql/CompiledSqlAuditDigesterTests.cs` (extend)
- S `Anonymize_DropsCommentText`: comment text -> one space in `Anonymize` output and in the negative-cache message. SEC-51-03.

### PR 2 (I2)
`Sql/WebSqlOutputMaskTests.cs`
- S `MaskedColumn_IsFlagged`: masked `email` -> `masked = true`.
- S `ExpressionOverMaskedColumn_IsFlagged`: `upper(email)` -> true.
- S `MaskedInOneUnionBranch_IsFlagged`: mask in one branch -> true.
- S `MaskedThroughCteAndDerivedTable_IsFlagged`: CTE plus derived table -> true.
- S `HiddenColumn_NotInOutput_NoFlagRevealsIt`: hidden column -> not in `columns`, star expands without it. SEC-51-05.
- S `MaskedColumnType_IsDeliveredType`: masked int column with string mask -> `varchar`.
- S `MaskedColumn_NullableAlwaysTrue`: `NOT NULL` source column masked -> `nullable = true`. SEC-51-05.
- S `MaskArrayLengthMismatch_FailsClosed`: forced length mismatch -> 500 `SQL_COMPILER_INVARIANT`.
- S `Federated_UnresolvedAlias_FlagsMasked`: alias the matcher cannot resolve -> true. SEC-51-05.
- S `MaskFlags_NotSharedAcrossPolicies`: same text, two policies (masked / clear) -> different flags, two cache entries. SEC-51-06.
- `ClearColumn_NotFlagged`: unmasked column -> false.
- `Federated_MaskedColumn_IsFlagged`: federated masked column -> true.

`Api/WebSqlColumnsContractTests.cs`
- `Columns_HaveNameTypeNullableMasked`: JSON columns are objects with the four members.
- `ArraysFormat_SameColumnObjects`: `format=arrays` -> same objects.
- `EmptyResult_EmptyColumns`: empty result -> `"columns":[]`.

### PR 3 (I1a)
`Quota/QueryCostQuotaTests.cs`
- `GraphQl_BehaviourUnchanged_AfterExtraction`: GraphQL over-cost and exhausted -> same 400/429, headers, settlement as before.
- S `NeverAdmissible_IsOverCost_Not429`: estimate > `MaxTokensCapacity` -> `OverCost`.
- S `Settle_DebitsAboveReservation`: reserve 10, settle 50 -> balance debited 40 more, negative allowed (both limiter implementations).
- `RateLimitingDisabled_Unmetered_CeilingApplies`: disabled -> `Unmetered`, over-ceiling still `OverCost`. SEC-51-10.
- S `ReservationThrows_NeverUnmetered`: limiter throws -> `AdmitAsync` throws. SEC-51-10.
- S `ChargeFailureFee_DebitsWithoutReservationCheck`: empty bucket -> debit of `max(BaseCost, FailureFee)` applied. SEC-51-11.

### PR 4 (I1b)
`Sql/SqlStatementCostEstimatorTests.cs`
- `DefaultStatement_Is576`: example 1 -> 576.
- `SecondWorkedExample_Is1181`: example 2 with fake row counts -> 1181. SEC-51-08.
- `CrossJoin_LargeOrUnknown_WeightsFour`, `CrossJoin_SmallDimension_WeightsOne`, `ConstantOn_CountsAsCrossJoin`: k and X rules.
- `SetOperations_Add`: U operators add `SetOperationCost` each.
- S `SetOperationBranches_CountJoins`: join in second branch -> counted. SEC-51-08.
- S `View_PricedAsOneRelation`: view over a 3-way join -> J = 0. SEC-51-08.
- `Offset_AddsCost`: O = 10_000 -> +100.
- `Saturates_AtIntMax`: extreme counts -> `int.MaxValue`.
- `Dml_NeverZero`: DML -> `>= BaseCost + DmlStatementCost`.
- `Analysis_And_Enforcement_SameNumber`: dry-run analysis estimate == gate estimate.

`Security/WebSqlCostQuotaTests.cs`
- S `OverCost_RejectedBeforeSourceConnection`: fake connection factory never opened.
- S `QuotaExhausted_429_WithRetryAfter`.
- S `RepeatedTimeouts_ExhaustQuota`: fake clock, 4 x 30 s -> 429.
- S `Quota_IsPerCallerAndTenant`; S `Quota_SharedWithGraphQl`.
- S `TrinoRoute_And_Parquet_AreMetered`; S `CrossSource_IsMetered`.
- S `PlannedTableRead_NotDoubleCharged`; S `PlannedTableRead_NotClientSettable`: no request field sets it. SEC-51-12.
- S `Deny_IsAudited_WithEstimatedCost`; S `Dml_GatedBeforeIntentAudit_AndCharged`.
- S `EstimatedCost_SameWithAndWithoutRowFilter`: same statement, row filter on/off -> same `estimatedCost`. SEC-51-07.
- S `LateralUnnestRecursive_PricedAsCrossJoin`: `LATERAL`, `UNNEST`, TVF -> k = 4; `WITH RECURSIVE .. LIMIT 10` -> recursive term added. SEC-51-08.
- S `CteReferencedTwice_CountedTwice`: CTE with one join referenced twice -> J = 2. SEC-51-08.
- S `ClientAbort_StillSettles`; S `AsyncCancel_StillSettles`: settle called with `CancellationToken.None`. SEC-51-09.
- S `AsyncAdmission_CapturedAtSubmit`: tier changes between submit and run -> settles against submit-time subject and tier. SEC-51-09.
- S `SettleFailure_DoesNotTurnFailureIntoSuccess`: settle throws on failed statement -> response stays the failure. SEC-51-09.
- S `QuotaStoreFailure_FailsClosed`: limiter throws -> 503 `SERVICE_UNAVAILABLE`, no connection opened. SEC-51-10.
- S `PolicyDenied_DebitsFailureFee`; S `SyntaxError_NotCharged`. SEC-51-11.
- S `CostDisabled_FailsStartOutsideDevelopment`. SEC-51-12.

### PR 5 (I4)
`Security/WebSqlPagingTokenTests.cs`
- S `OtherPrincipal_Rejected`, `OtherTenant_Rejected`, `OtherStatement_Rejected` (one whitespace changed), `OtherParameters_Rejected`, `OtherDataSource_Rejected` (same requested string, other resolved id): -> `SQL_PAGING_TOKEN_INVALID`. SEC-51-14.
- S `Expired_Rejected`; S `TamperedOffset_Rejected`; S `TamperedPageNumber_Rejected`; S `SizeMismatch_Rejected`. SEC-51-15.
- S `Oversized_RejectedBeforeDecode`; S `NextWithDml_Rejected`: -> `SQL_PAGING_UNSUPPORTED`, codec never called. SEC-51-15.
- S `Payload_RevealsNoStatementOrCaller`; S `AllFailures_SameCodeAndMessage`; S `InvalidToken_IsAudited` (reason only, not charged). SEC-51-11.
- S `PrincipalWithoutSid_GetsNoNext`: name-only principal -> `caller_not_bindable`. SEC-51-14.
- S `KeyDiffersFromAuditDigestKey`; S `HkdfInfo_IsUnique`: info string differs from every HKDF info constant in `src/`; S `MacFraming_IsLengthPrefixed`: moving bytes between two binding fields changes the MAC. SEC-51-13.
- S `AuditKeyRotation_InvalidatesToken`. D-51g.

`Sql/WebSqlPagingTests.cs`
- S `RowFilterAndMask_ApplyOnEveryPage`; S `CostAndQuota_CheckedPerPage`; S `Audit_RecordsUserStatementAndPage`; S `RevokedGrant_FailsNextPage`. SEC-51-16.
- S `OffsetAboveMax_NoNext`; S `Dml_WithPage_Unsupported`; S `PagingOptions_ValidatedAtStart`. SEC-51-12, SEC-51-15.
- S `Offsets_DoNotGrowTemplateCacheUnbounded`: one statement, policy, size; 1,000 offsets (0, 500, .., 499_500 with `MaxOffset` raised) -> `CompiledSqlTemplateCache.Stats.Entries` grows by exactly 1, `Misses == 1`, identical `Sql` text, each bound `PageOffset` value equals its offset. SEC-51-17, D-51n.
- S `PageOffset_NotInCacheKeyMaterial`: `CompileCacheKey.Material` equal for offsets 0 and 9_500. SEC-51-17.
- S `PageOffsetSlot_MissingOrOutOfRange_FailsClosed`: `Rebind` without value, or with -1 / `MaxOffset + 1` -> `SecurityException`. D-51n.
- `OffsetRendering_GoldenPerDialect`: 5 dialects, offset rendered as the dialect marker, limit `size + 1` (Databricks fallback asserted if taken). D-51n.
- `WithoutOrderBy_FirstPageWithoutNext`; `RootLimit_PagingUnavailable`; `Federated_PagingUnavailable`; `LastPage_HasNoNext`; `TrinoRoute_WithPage_Unsupported`.

## 7. Verification commands
- `dotnet build Autheris.sln`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj --filter "FullyQualifiedName~WebSql|FullyQualifiedName~QueryCost|FullyQualifiedName~SyntaxPosition|FullyQualifiedName~SqlCompilerErrorMapper|FullyQualifiedName~CostEstimator|FullyQualifiedName~CompiledSqlAuditDigester"`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj --filter "Category=Security"`

## 8. Acceptance criteria
- [ ] #527: an over-cost statement is rejected (400) before any source connection; repeated expensive or timing-out statements are throttled per caller (429, `Retry-After`); GraphQL and WebSQL share `IQueryCostQuota` and one bucket; analysis and enforcement report the same estimate.
- [ ] #528: every output column has `name`, `type`, `nullable`, `masked`; flags follow mask lineage; nothing about hidden columns is revealed.
- [ ] #529: syntax errors carry `line` and `column` and no statement text; one trailing `;` and comments are accepted; comments reach neither source nor audit.
- [ ] #533: an ordered statement pages via `next` without text changes; policies, cost, quota and audit apply per page; tokens bind statement, parameters, caller, tenant, data source, transport and expiry; the template cache does not grow with the offset.

## 9. PR slicing (<= 400 net added src lines each)
Line anchors refer to `origin/main` at verification (section 4). PR 2 and PR 4 must re-locate their anchors after WP-SIMPL-45 PR 4 and PR 5 are merged; the symbols given are authoritative.
1. **PR 1 I3**, ~170 net src. Depends on: nothing. Files: `SqlSyntaxErrorException.cs`, `FastSqlEngine.cs`, `SqlTokenSecurityOptions.cs`, `SyntaxErrorNegativeCache.cs`, `ClientSqlTokenPolicy.cs`, `GovernedSqlRewriter.cs`, `CompileRequestFactory.cs`, `FederatedDuckDbExecutionService.cs`, `AstSecurityVisitor.Support.cs`, `SqlCompilerErrorMapper.cs`, `SqlErrorResponse.cs`. Closes #529. Binding: SEC-51-01, -02, -03, -04.
2. **PR 2 I2**, ~330 net src. Depends on: WP-SIMPL-45 PR 4. Files: `CompiledSql.cs`, `AstSecurityVisitor.Select.cs`, `MaskGuards.AggregateAndDerived.cs`, `SqlResultColumns.cs`, `IGovernedSqlExecutionService.cs`, `GovernedSqlExecutor.cs`, `GovernedSqlExecutionService.cs`, `FederatedDuckDbExecutionService.cs`, `WebSqlEndpoints.Execution.cs`, `WebSqlEndpoints.Governance.cs` (OpenAPI), `TableStreamEndpoints.cs`. Closes #528. Binding: SEC-51-05, -06.
3. **PR 3 I1a**, ~160 net src. Depends on: nothing. Files: `IQueryCostQuota.cs`, `QueryCostQuota.cs`, `CostAndQuotaMiddleware.cs` (shrinks), DI registration, settle fix if needed. Closes nothing. Binding: SEC-51-10 (no `Unmetered` on failure), SEC-51-11 (`ChargeFailureFeeAsync`).
4. **PR 4 I1b**, ~395 net src. Depends on: PR 3, WP-SIMPL-45 PR 5 and PR 6. Files: `ISqlQueryAnalyzer.cs`, `SqlQueryAnalyzer.cs`, `SqlStatementCostEstimator.cs`, `RestApiOptions.cs` (`WebSqlCostOptions`), `GovernedSqlException.cs` (codes), new exceptions, `SqlCompilerErrorMapper.cs`, `GovernedSqlExecutor.cs`, `FederatedDuckDbExecutionService.cs`, `WebSqlStatementManager` (async admission capture), `QueryAnalysisService.Sql.cs`, startup check. Closes #527. Binding: SEC-51-07, -08, -09, -10, -11, -12.
5. **PR 5 I4**, ~410 net src. Depends on: PR 2, PR 4, WP-SIMPL-45 PR 6. Files: `WebSqlPageTokenCodec.cs`, `CompiledSqlAuditDigester.cs`, `WebSqlEndpoints.cs`, `WebSqlEndpoints.Execution.cs`, `IGovernedSqlExecutionService.cs`, `GovernedSqlRewriter.cs`, `CompileRequest.cs`, `CompileRequestFactory.cs`, `AstSecurityVisitorOptions.cs`, `AstSecurityVisitor.Select.cs`, `CompiledSql.cs` (`ParameterOrigin.PageOffset`), `CompiledSqlTemplate.cs` (`Rebind`), `SqlEmitterContext.cs`, `SqlDialectGeneratorBase.cs`, `EmittedSqlInvariantChecker.cs`, `ICompiledSqlBinder.cs` (Databricks fallback only if needed), `GovernedSqlExecutor.cs`, `RestApiOptions.cs` (`WebSqlPagingOptions`). Closes #533. Binding: SEC-51-12 (paging options), -13, -14, -15, -16, -17. If it exceeds 400 lines at review, the engine offset slot (`CompiledSql`, `CompiledSqlTemplate`, `SqlEmitterContext`, `SqlDialectGeneratorBase`, `EmittedSqlInvariantChecker`, binder) lands first as PR 5a with `PageOffset_NotInCacheKeyMaterial`, `PageOffsetSlot_MissingOrOutOfRange_FailsClosed` and `OffsetRendering_GoldenPerDialect`.

## 10. Threat notes for Phase 3
- Quota bypass: the gate sits in the executors, not the endpoint, so JSON, Parquet, Trino sync/async and cross-source are metered; `PlannedTableRead` is excluded only because the planning transport prices it and it is never client-settable; one bucket across transports. Policy-denied statements are charged the failure fee (SEC-51-11, D-51m); syntax and token failures are not.
- Mask-flag leak: flags exist only for delivered columns; hidden columns never appear (star = visible set, explicit reference = `SQL_POLICY_DENIED`); `type` is the delivered type, so a masked column's catalog type is not revealed; flags expose no more than the masked values and GraphQL `maskedFields` already do.
- No statement echo: only integer `line`/`column`; fixed message; ANTLR text stays in the server log.
- Token replay and binding: replay by the same caller within the lifetime only re-reads a page and is charged again; any change of statement, parameters, caller, tenant, source or transport fails the MAC; key from the audit key chain, no per-process fallback in production.
- Offset DoS: `MaxOffset`, offset cost term, per-page quota, timeout and resource groups; no raw client offset; the offset is a bind slot, so it cannot grow the template cache (D-51n).
- Comment smuggling: comments are hidden-channel and never emitted (SQL is regenerated from the AST); nested block comments rejected; terminator handling looks at default-channel tokens only, so a comment cannot hide a second statement; comment text is removed from audit details.

## 11. Decisions (defaults taken; owner may change)
- D-51a: static estimate (AST plus `EstimatedRowCount`) plus measured execution seconds at settlement; no `EXPLAIN`. Kept; statistics only weight cross joins.
- D-51b: paging requires a root `ORDER BY`; no appended key, no snapshot. Kept.
- D-51c: cost and quota per page. Kept.
- D-51d: opaque HMAC-bound token only, no raw offset. Kept.
- D-51e: `freeze-exception` after WP-SIMPL-45. Kept.
- D-51f (code-forced): codes follow GraphQL as implemented: over-cost 400 `QUERY_COST_QUOTA_EXCEEDED`, exhausted bucket 429 `RATE_LIMIT_EXCEEDED` (the draft had them swapped).
- D-51g: page-token key = HKDF of the existing audit key (no new secret) instead of a dedicated `SigningKeyRef`.
- D-51h (code-forced): comments are rejected today; they become accepted on the client SQL path only; other parsers stay strict.
- D-51i: the WebSQL per-statement ceiling is `WebSql:Cost:MaxStatementCost`, not the tier `MaxCostPerQuery` (GraphQL-shaped; Standard 250 would reject a default 1000-row statement); the tier still governs the shared bucket.
- D-51j (code-forced): GraphQL paging part dropped; GraphQL already has `first`/`offset` and keyset connections.
- D-51k: Trino `errorLocation` out of scope (follow-up issue).
- D-51l: statements with their own root LIMIT/FETCH/OFFSET are not paged (`statement_has_limit`); `type` is the delivered reader type, not a catalog or inferred type (code-forced: it is the only type known before rows stream).
- Default taken (consolidation 2026-11-07): a quota-store failure maps to the existing 503 `SERVICE_UNAVAILABLE` (SEC-51-10); a principal without SID is reported as `pagingUnavailable: "caller_not_bindable"` (SEC-51-14).

## 12. Estimate
About 8 pd (unchanged): Phase 3 0.5, PR 1 1, PR 2 2, PR 3 1, PR 4 1.5, PR 5 2. About 1,465 net src lines in five PRs plus about 1,700 test lines (GraphQL paging removed; mask lineage, cost wiring and the offset bind slot larger than drafted).

## 13. Phase 3 security conditions (index)
Verdict 2026-11-07 (csharp-security-expert): APPROVED WITH CONDITIONS. The full text of each condition is folded into section 5 at the tagged sentence; the tagged text is binding.

| ID | Design | PR | Mandatory test(s) |
|---|---|---|---|
| SEC-51-01 | 5.1.1, 5.1.5 | 1 | `PolicyDeniedColumn_HasNoPosition`, `UnknownTable_HasNoPosition`, `ParseTimeout_HasNoPosition`, `SyntaxError_Body_HasPosition_AndNoStatementText` |
| SEC-51-02 | 5.1.3 | 1 | `Comments_NotForwardedToSource_AllDialects`, `OptimizerHint_NotEmitted_SqlServerOracleDatabricks`, `OlapStagingAndSqlEndpoints_StillRejectComments`, `FragmentValidator_StillRejectsComments`, `NestedBlockComment_StillRejected` |
| SEC-51-03 | 5.1.3, 5.1.4 | 1 | `Anonymize_DropsCommentText`, `CommentWithNonAsciiAndDollar_NotForwardedOrAudited`, `RawClientSql_NotLoggedAtInformationOrAbove` |
| SEC-51-04 | 5.1.2 | 1 | `StrictOptions_NotServedFromLenientNegativeCacheEntry`, `TwoSemicolons_Rejected_AtSecond`, `SecondStatement_Rejected_AtSecondStatement`, `CommentCannotHideSecondStatement` |
| SEC-51-05 | 5.2.2, 5.2.3, 5.2.5 | 2 | `MaskedColumn_NullableAlwaysTrue`, `Federated_UnresolvedAlias_FlagsMasked`, `HiddenColumn_NotInOutput_NoFlagRevealsIt` |
| SEC-51-06 | 5.2.2 | 2 | `MaskFlags_NotSharedAcrossPolicies` |
| SEC-51-07 | 5.4.1, 5.4.2 | 4 | `EstimatedCost_SameWithAndWithoutRowFilter` |
| SEC-51-08 | 5.4.1, 5.4.2 | 4 | `LateralUnnestRecursive_PricedAsCrossJoin`, `CteReferencedTwice_CountedTwice`, `SetOperationBranches_CountJoins`, `View_PricedAsOneRelation`, `SecondWorkedExample_Is1181` |
| SEC-51-09 | 5.4.6 | 4 | `ClientAbort_StillSettles`, `AsyncCancel_StillSettles`, `AsyncAdmission_CapturedAtSubmit`, `SettleFailure_DoesNotTurnFailureIntoSuccess` |
| SEC-51-10 | 5.3, 5.4.5 | 3, 4 | `ReservationThrows_NeverUnmetered`, `QuotaStoreFailure_FailsClosed`, `RateLimitingDisabled_Unmetered_CeilingApplies` |
| SEC-51-11 | 5.3, 5.4.7, 5.5.4 | 3, 4, 5 | `ChargeFailureFee_DebitsWithoutReservationCheck`, `PolicyDenied_DebitsFailureFee`, `SyntaxError_NotCharged`, `InvalidToken_IsAudited` |
| SEC-51-12 | 5.4.4, 5.4.5, 5.5.5 | 4, 5 | `PlannedTableRead_NotClientSettable`, `CostDisabled_FailsStartOutsideDevelopment`, `PagingOptions_ValidatedAtStart` |
| SEC-51-13 | 5.5.4 | 5 | `KeyDiffersFromAuditDigestKey`, `HkdfInfo_IsUnique`, `MacFraming_IsLengthPrefixed` |
| SEC-51-14 | 5.5.2, 5.5.4 | 5 | `PrincipalWithoutSid_GetsNoNext`, `OtherPrincipal_Rejected`, `OtherTenant_Rejected`, `OtherDataSource_Rejected` |
| SEC-51-15 | 5.5.1, 5.5.3, 5.5.4 | 5 | `SizeMismatch_Rejected`, `TamperedPageNumber_Rejected`, `NextWithDml_Rejected`, `Oversized_RejectedBeforeDecode` |
| SEC-51-16 | 5.5.7 | 5 | `RevokedGrant_FailsNextPage`, `RowFilterAndMask_ApplyOnEveryPage`, `CostAndQuota_CheckedPerPage` |
| SEC-51-17 | 5.5.6 (D-51n) | 5 | `Offsets_DoNotGrowTemplateCacheUnbounded`, `PageOffset_NotInCacheKeyMaterial` |

## 14. Phase 3 recommendations on open owner questions
- D-51i: keep `MaxStatementCost = 2000` independent of tier `MaxCostPerQuery`; the effective ceiling is additionally capped by `MaxTokensCapacity`, so Free (100) can still run bounded statements (`LIMIT 50` costs about 26) but no default 1000-row statement. Fail-closed; acceptable. Optional later: per-tier `WebSqlMaxStatementCost` override.
- Policy-denied charging: charge the failure fee (SEC-51-11).
- D-51g: deriving from the audit key with a distinct HKDF label is acceptable (key separation holds, no new secret, multi-node consistent). Rotation invalidates tokens for at most `TokenLifetimeMinutes`; clients restart from page 1. A dedicated key is not required.
- D-51l: keep. Not paging statements with their own LIMIT/FETCH/OFFSET avoids nested-offset semantics and offset arithmetic overflow; it is the safer default.

### Owner confirmation (2026-11-07)
The owner confirmed every default of this WP (D-51a..l) and the Phase 3 recommendations in this section. Binding from now on:
- D-51i confirmed: `WebSql:Cost:MaxStatementCost = 2000`, independent of the tier `MaxCostPerQuery` and additionally capped by `MaxTokensCapacity`; Free-tier callers can run small bounded statements only. A per-tier override is a later, separate change.
- D-51m (new, from Phase 3): statements denied by policy are charged the failure fee (SEC-51-11); syntax and token-guard errors are not charged.
- D-51g confirmed: the page-token key is derived from the audit key with a distinct HKDF label; audit-key rotation invalidates open page tokens (at most their lifetime of 15 minutes).
- D-51l confirmed: statements with their own root LIMIT/FETCH/OFFSET are not paged (`statement_has_limit`).
- D-51n (owner decision 2026-11-07, resolves SEC-51-17 vs the former 5.5.6): the page offset is rendered as a bind parameter (`ParameterOrigin.PageOffset` slot) and filled after template lookup; it is not part of the template key. Only the value-free flag `BindPageOffset` and the existing `EnforcedMaxRows = size + 1` (probe row) are key components. Proven by `Offsets_DoNotGrowTemplateCacheUnbounded` (1,000 offsets -> one cache entry). Details in 5.5.6.
- Implementation is not started by this confirmation; it starts on an explicit owner go and after the WP-SIMPL-45 gates in section 9.

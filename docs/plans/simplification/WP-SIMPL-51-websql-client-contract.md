# WP-SIMPL-51 WebSQL client contract: cost and quota, typed columns, syntax positions, paging
Status: Draft (Phases 1-2 in this spec; Phase 3 security review pending before implementation)
Parent: [plan](../2026-11-05-plan-project-simplification.md) | Issues: [#527](https://github.com/themulle/autheris/issues/527), [#528](https://github.com/themulle/autheris/issues/528), [#529](https://github.com/themulle/autheris/issues/529), [#533](https://github.com/themulle/autheris/issues/533) (all opened 2026-11-05, v1.7.0 observations) | Related: #530 and #558 (closed, error-code fixes), WP-SIMPL-45 (WebSQL write path), WP-SIMPL-42 (usage accounting) | Freeze: feature work, runs under `freeze-exception` like WP-SIMPL-38..48 or after the freeze (see D-51e)

## 1. Goal and outcome
`POST /api/v1/sql` becomes a client contract that SQL-editor and BI-style clients can build on without re-implementing gateway policy: expensive statements are priced and limited per caller like GraphQL (#527), every output column carries a type and a mask flag (#528), syntax errors carry a position and the common trailing semicolon and comments are accepted (#529), and a plain statement can be paged without the client rewriting it (#533). Rights, masks, row filters, cost, quota and audit apply unchanged to every page.

## 2. Preconditions and dependencies
- WP-SIMPL-45 PR 4-6 (WebSQL write path) merged first; this WP touches the same endpoint (`WebSqlEndpoints.*`) and must not conflict with the write pipeline.
- WP-SIMPL-42 (usage accounting per user and AD group) supplies the per-caller identity for quota keys if merged; otherwise the quota key is SID plus tenant (as GraphQL today).
- No dependency on WP-SIMPL-50.

## 3. Scope
### In scope
**I1 Cost estimate and per-caller quota (#527)**
- Extract the quota part of `CostAndQuotaMiddleware` (`src/Autheris.GraphQL/Interceptors/CostAndQuotaMiddleware.cs`, uses `IRateLimiterService`) into a transport-neutral `IQueryCostQuota` in Application; GraphQL and WebSQL both call it (no second implementation).
- Static cost estimate from the governed AST before execution: relations, joins without join predicate (cross joins), set operations, missing `LIMIT`, estimated rows from catalog statistics when present. Above `Gateway:WebSql:MaxStatementCost` the statement is rejected before it reaches the source.
- Per-caller quota (SID, tenant, scope) on cost units per window and on execution seconds consumed (measured, charged after execution), so repeated statements that hit `SQL_TIMEOUT` are throttled.
- Rejection returns 429 `QUERY_COST_QUOTA_EXCEEDED` (same code as GraphQL) with `Retry-After`; over-cost single statement returns 400 `QUERY_COST_EXCEEDED`. Both are audited as DENY with the computed cost.
**I2 Column types and mask flags (#528)**
- Response `columns` becomes an array of objects: `{ "name", "type", "nullable", "masked" }`. `type` is the gateway's logical type (catalog type for column references, inferred type for expressions, `unknown` otherwise); `masked` is true when the caller's effective column access for the output column is not `Clear` (source: the masked-column set `TreeSqlCompiler` already computes for GraphQL `extensions.maskedFields`).
- Breaking change to the response shape (allowed, no production yet); Arrow and Parquet export keep their own schema and are unchanged.
**I3 Syntax error position and statement terminators (#529)**
- `SQL_SYNTAX_ERROR` problem details carry `line` and `column` of the first syntax error from the ANTLR error listener (`FastSqlEngine.SyntaxError`), never the offending token text or any statement fragment.
- One trailing `;` (with optional whitespace or comments after it) is accepted and dropped before governance; more than one statement stays rejected with detail "single statement expected". Comments are accepted (hidden channel) and are not forwarded to the source.
**I4 Paging without rewriting the statement (#533)**
- Request option `page: { "size": n }` and a continuation token: a truncated response returns `next` (opaque); the client sends `{ "sql", "parameters", "next" }` to get the next page. No client-supplied raw offset.
- The gateway applies `OFFSET … FETCH NEXT … ROWS ONLY` on the governed AST when rendering for the source (grammar and dialect rendering already exist in `SqlBase.g4`); the user's text is unchanged and is what the audit records, plus page number and offset.
- Stable order: paging requires a deterministic top-level `ORDER BY`; without it the first page is served and `next` is omitted with `pagingUnavailable: "order_by_required"` (D-51b).
- The token is HMAC-protected and binds statement hash, parameter hash, SID, tenant, offset, page size and expiry; a token presented by another principal, for another statement or after expiry is rejected (400 `SQL_PAGING_TOKEN_INVALID`).
- Limits: `size` <= `MaxAllowedRows`; total offset <= `Gateway:WebSql:MaxPagingOffset` (analogous to `GraphQL:MaxAllowedOffset`); cost and quota are checked per page (D-51c).
- GraphQL: same mechanism for a root field without `first`/`offset` in the text, via a gateway-reserved request extension; implemented in the same PR only if the shared paging service fits the PR size limit, otherwise a follow-up PR in this WP.
### Out of scope
- Server-side cursors or snapshots across pages (D-51b).
- Cost-based optimizer statistics collection; I1 uses only statistics already in the catalog.
- Changing Arrow/Parquet export endpoints.

## 4. Current state (verified 2026-11-07 on main 5bbb0576)
- `WebSqlEndpoints.cs:49` maps `POST /api/v1/sql`; `WebSqlEndpoints.Execution.cs` ~194-270 writes `columns` as a string array (`SqlResultColumns.UniqueNames`), `rowCount` and `truncated` (`RowLimitedDataReader.HasMoreRows`).
- `CostAndQuotaMiddleware` and `QueryCostAnalyzerRule` exist only in `Autheris.GraphQL`; WebSQL is bounded only by timeout, row cap and resource groups (`QueryGovernorOptions`).
- `GovernedSqlErrorCodes.SyntaxError = "SQL_SYNTAX_ERROR"` with fixed message "Invalid SQL syntax." (`GovernedSqlException.cs:15`, `:77`); the ANTLR listener receives line and column but they are dropped.
- `SqlRowLimit.Effective` applies `DefaultMaxRows` only without an explicit `LIMIT`.

## 5. Implementation steps
1. Phase 3 security review of this spec (I1 quota bypass, I2 mask-flag information leak, I3 no statement echo, I4 token binding and offset abuse); conditions appended here.
2. PR 1: I3 (smallest, independent).
3. PR 2: I2.
4. PR 3: I1 (shared `IQueryCostQuota`, GraphQL switched to it with unchanged behaviour, WebSQL wired).
5. PR 4: I4 for WebSQL; PR 5: I4 for GraphQL if not in PR 4.
6. Close #527, #528, #529, #533 with the merging PRs.

## 6. Tests
- Security (`[Trait("Category","Security")]`): `WebSqlCostTests.Cross_join_over_max_cost_is_rejected_before_execution`, `Repeated_timeouts_exhaust_quota`, `Quota_is_per_caller_and_tenant`; `WebSqlColumnsTests.Masked_column_is_flagged`, `Mask_flag_does_not_reveal_columns_hidden_by_policy`; `WebSqlSyntaxTests.Error_position_contains_no_statement_text`, `Multiple_statements_still_rejected`, `Comments_are_not_forwarded_to_source`; `WebSqlPagingTests.Token_from_other_principal_is_rejected`, `Token_for_other_statement_is_rejected`, `Expired_token_is_rejected`, `Row_filter_and_mask_apply_on_every_page`, `Offset_above_max_is_rejected`, `Audit_records_user_statement_and_page`.
- Functional: `Trailing_semicolon_is_accepted`, `Column_types_from_catalog_and_expressions`, `Paging_without_order_by_returns_first_page_without_next`, `GraphQL_quota_behaviour_unchanged_after_extraction`.

## 7. Verification commands
- `dotnet build Autheris.sln`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj --filter "FullyQualifiedName~WebSql"`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj --filter "Category=Security"`

## 8. Acceptance criteria
- [ ] #527: an over-cost statement is rejected before execution; repeated expensive statements are throttled per caller with `QUERY_COST_QUOTA_EXCEEDED`; GraphQL and WebSQL share one quota implementation.
- [ ] #528: every output column has `name`, `type`, `nullable`, `masked`; masked values are distinguishable from real nulls.
- [ ] #529: syntax errors carry line and column and no statement text; a trailing `;` and comments are accepted.
- [ ] #533: a client pages a plain ordered statement through `next` tokens without changing its text; policies, cost, quota and audit apply per page; tokens are bound to principal, statement and expiry.

## 9. Decisions (defaults taken; owner may change)
- D-51a: cost estimate is static (AST plus existing catalog statistics) plus measured execution seconds charged to the quota; no `EXPLAIN` round trip to the source.
- D-51b: paging requires a top-level `ORDER BY`; the gateway does not append a table key and keeps no snapshot or server cursor (consistency like `first`/`offset` today).
- D-51c: cost and quota are checked per page, not once per result.
- D-51d: paging uses an opaque HMAC-bound continuation token only; no client-supplied raw offset.
- D-51e: the WP is feature work under the freeze; it runs as `freeze-exception` after WP-SIMPL-45 because the issues report missing gateway policy (#527) on a core transport (OD-3), not convenience only.

## 10. Estimate
About 8 pd: Phase 3 0.5, I3 1, I2 1.5, I1 2.5, I4 2.5 (WebSQL and GraphQL). Net lines about +900 (new shared quota service, paging service, tests), partly offset by moving GraphQL quota code.

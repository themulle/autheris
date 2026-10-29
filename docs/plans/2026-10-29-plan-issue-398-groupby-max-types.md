# Triage #398: groupBy on nvarchar(max) fails with AGGREGATION_ARGUMENT_INVALID

Verdict: valid defect (functional gap), not a security issue. Class: bug / over-broad type guard (small).

## Root cause (origin/main)
- src/Autheris.Application/Data/Services/GovernedTableReadPlanner.cs:160-163: group-by planning calls
  `DefaultReadOrdering.IsOrderableType(colMeta.DataType, dialect)` and throws
  `ArgumentException("... non-groupable type ...")` when false. The aggregation path reuses the ORDER BY predicate as a "groupable" check.
- DefaultReadOrdering.cs:158: `t.Contains("(max)")` returns false for every (max) type, so nvarchar(max)/varchar(max)/varbinary(max) are refused. Same file also refuses json/jsonb/xml/blob/array/etc. (:132-141) and text/ntext/image/clob on SQL Server/Oracle (:174-180).
- Reporter's suspicion confirmed (DefaultReadOrdering.cs:158), but the throw is in the planner, not in GovernedAggregationService.ValidateAndResolveGroupBy (ValidationAndExecution.cs:88-120), which has no type check. The orderBy variant fails at the same place (orderBy over group columns is in-memory, Buckets.cs:583).
- Code after #396: unchanged. AggregateErrors.cs:65 maps `GatewayInvalidQueryException or ArgumentException` to AGGREGATION_ARGUMENT_INVALID. NOT_SUPPORTED (AggregateErrors.cs:71) is only used for AstBuildException/SqlCompileNotSupportedException/SqlTimeZoneException. A type refusal is therefore still a misleading "argument invalid" (wrong client fix hint).
- No test or doc covers "non-groupable" (only the planner string).

## Dialect facts (from vendor docs, not re-verified here)
- SQL Server: GROUP BY works on varchar/nvarchar(max) and varbinary(max); fails only on legacy text/ntext/image/xml/sql_variant-like types, geography, CLR. COUNT_DISTINCT works as reported.
- PostgreSQL: text, jsonb, arrays groupable; json (not jsonb), xml (no equality) not. SQLite: everything groupable. Databricks: string groupable; map/variant not (array/struct ok). Oracle: CLOB/NCLOB/BLOB/LONG not groupable. MySQL: TEXT groupable but compares only up to max_sort_length (default 1024 bytes), so long values can merge.

## Options
(a) Dedicated `IsGroupableType(dataType, dialect)`: allow (max) strings on SQL Server (varchar/nvarchar), keep refusing text/ntext/image/xml/binary/LOB/spatial/json; ordering predicate untouched. Exact, no semantic change.
(b) CAST/LEFT to a bounded length: truncation merges distinct values (wrong counts, and a small group can be hidden inside a merged group; breaks min-group-size reasoning). Reject.
(c) Governed refusal for truly ungroupable types: throw a typed exception mapped to AGGREGATION_NOT_SUPPORTED with fixed message; also covers the REST/MCP aggregation paths consistently.

## Recommendation: (a) + (c)
Split groupability from orderability; (max) strings groupable on SQL Server; for types that stay refused, return AGGREGATION_NOT_SUPPORTED (fixed message, no type name) instead of ARGUMENT_INVALID. Size: S (about 40-80 LOC src, 8-12 tests, 1 PRD/addendum page).

Acceptance criteria
1. SQL Server groupBy on nvarchar(max)/varchar(max) returns grouped rows; generated SQL is `GROUP BY [col]` with no CAST; result equals GROUP BY on an equivalent nvarchar(200) column.
2. DefaultReadOrdering.IsOrderableType behavior unchanged (default read ordering still skips (max)).
3. text/ntext/image/xml/varbinary(max)/geography on SQL Server, CLOB/BLOB on Oracle, json on PG, map/variant on Databricks: refused with AGGREGATION_NOT_SUPPORTED, fixed message, identical for all refused types (no type oracle); audit entry keeps detail.
4. Unknown types stay allowed (current fail-open for unknown) - or document; open question below.
5. Masked/unavailable columns still yield the existing masked/unavailable errors before any type check (order of checks unchanged; type refusal must not reveal type of a column the caller cannot see).
6. Min-group-size, suppression/merge-into-other, DP noise, row cap, MaxGroups apply identically to (max) group keys.
7. COUNT_DISTINCT/orderBy over group columns work with (max) keys (in-memory comparer handles strings).

Tests: unit for IsGroupableType matrix per dialect; planner SQL golden (SqlServer, no CAST); service test with min-group-size over a (max) key; error-mapping test (NOT_SUPPORTED, fixed message); masked-column precedence test; SQL Server Testcontainers integration (nvarchar(max) column).

## Phase 3 (light) questions
1. Privacy: confirm no privacy control keys off column type (min-group-size, noise, suppression, negation guard); (max) keys must not bypass them. High-cardinality text key = near-unique groups: does AggregationNegationGuard / min-group-size already suppress these, and is the merged "other" bucket safe?
2. DoS: groups per query capped by MaxGroups and row cap (AggregationRowCapExceeded); is the cap applied before shipping large text keys (memory = rows x key length)? Need a max key byte length, or acceptance of current caps? Recommend: no new limit unless the row cap does not bound bytes.
3. Masking: group on masked column stays refused (MaskedAggregationException, ValidationAndExecution.cs:101-104); never group on masked value. Confirm refusal order leaks nothing about type.
4. Row filters/RLS: applied in WHERE before GROUP BY, unaffected; confirm test covers (max) key.
5. Open question (default): unknown/unparsable data types stay groupable (matches current IsOrderableType). Default: keep.

## Phase 3 binding conditions (csharp-security-expert, 2026-10-29)

Verdict: APPROVED WITH CONDITIONS. Verified: planner (GovernedTableReadPlanner.cs:146-163) already checks unknown column, then mask, then non-Clear access, and only then the type; that order is correct and must stay.

Rulings
- Q1 Privacy: no privacy control may key off column type; (max) keys get the same min-group-size, suppression, noise and negation guard as any key. Near-unique text keys are suppressed by min-group-size by design; no new control is needed. The "other" bucket is safe only if it is built after suppression exactly as for bounded keys.
- Q2 DoS: the row cap bounds rows, not bytes. nvarchar(max) keys can be up to 2 GB, so memory = rows x key length is unbounded. A new byte bound is required (I398-SC-04). No new group-count limit; MaxGroups stays.
- Q3 Masking: refusal order unchanged; type refusal is reachable only for Clear columns, so no type oracle for hidden columns.
- Q4 RLS/row filters stay in WHERE before GROUP BY; must be tested with a (max) key.
- Unknown/unparsable type: decide FAIL-CLOSED for groupBy only (refuse with the fixed NOT_SUPPORTED), because the new predicate is an allow-list per dialect. IsOrderableType stays as is (fail-open there is unchanged, out of scope). Exception: SQLite (everything groupable) and known-groupable base types.

Binding conditions
- I398-SC-01: IsGroupableType is a separate dialect-aware allow-list; IsOrderableType behavior is byte-for-byte unchanged. Only varchar/nvarchar(max) are newly allowed (SQL Server); varbinary(max), xml, text/ntext/image, geography/geometry, sql_variant, CLR stay refused.
- I398-SC-02: Refused types throw one typed exception mapped to AGGREGATION_NOT_SUPPORTED on GraphQL, REST and MCP paths with one fixed message: no column name, no type name, identical for all refused types; detail only in the audit entry (type name, no data values).
- I398-SC-03: Type check stays after unknown/mask/access checks (order unchanged); a masked or unavailable column never yields the type refusal.
- I398-SC-04: Enforce a fixed byte budget for group-key strings while materializing aggregation results (per-key cap and total cap, constants, no per-request override); exceeding it throws the existing AggregationRowCapExceeded (fixed message, no values). Reading must be streaming/capped so a 2 GB value is not fully buffered. If the existing reader already caps bytes, prove it with I398-T-06 and add nothing.
- I398-SC-05: No CAST/LEFT/SUBSTRING of group keys (option b stays rejected); SQL must be `GROUP BY [col]`.
- I398-SC-06: Group-key values never appear in logs, audit, exception messages or metrics labels.
- I398-SC-07: Unknown/unparsable type on a non-SQLite dialect is refused for groupBy with SC-02 behavior (fail-closed); MySQL TEXT stays refused (max_sort_length merges distinct values and can hide small groups below min-group-size).

Mandatory tests (all [Trait("Category","Security")])
- I398_T_01_IsGroupableType_Matrix_PerDialect_AllowsOnlyDocumentedTypes (incl. unknown type per dialect refused, SQLite allowed)
- I398_T_02_IsOrderableType_Unchanged_MaxStillNotOrderable
- I398_T_03_RefusedTypes_AllMapToNotSupported_IdenticalFixedMessage_NoTypeOrColumnName
- I398_T_04_MaskedMaxColumn_GroupBy_RefusedAsMasked_BeforeTypeCheck; I398_T_04b_UnavailableColumn_NoTypeOracle
- I398_T_05_MaxKey_MinGroupSize_SuppressesSmallGroups_And_OtherBucketNotReidentifying (near-unique keys)
- I398_T_06_OversizedMaxKey_ExceedsByteBudget_ThrowsRowCapExceeded_NoBufferingOfFullValue
- I398_T_07_RowFilter_AppliedBeforeGroupBy_WithMaxKey_NoRowsOutsideScope
- I398_T_08_PlannerSql_SqlServer_MaxKey_NoCast_GroupByColumnOnly
- I398_T_09_AuditAndLogs_DoNotContainGroupKeyValues
- I398_T_10 (integration, SQL Server Testcontainers): nvarchar(max) group result equals nvarchar(200) equivalent.

## Changelog

**2026-10-29: implemented** on branch `fix/issue-398-groupby-max-types`; Phase 5 review approved.

- **Groupable types:** `DefaultReadOrdering.IsGroupableType` is a dialect-aware allow-list, separate from `IsOrderableType`, which is unchanged.
  - SQL Server additionally allows `varchar(max)` and `nvarchar(max)`.
  - PostgreSQL and Databricks additionally allow `text`.
  - On SQLite every type is groupable.
  - On every other dialect, unknown or blank types are refused (fail-closed).
- **Refused types:** they return `AGGREGATION_NOT_SUPPORTED` with one fixed message.
- **Byte budget:** `ResultByteBudget.GroupKeys` caps each value at 16 KiB and each result at 32 MiB. Exceeding it raises the existing `AggregationRowCapExceeded`.
- **Breaking changes:**
  - A column with an unknown or blank type on a non-SQLite dialect can no longer be used in groupBy.
  - A refused type now answers `AGGREGATION_NOT_SUPPORTED` (REST 422) instead of `AGGREGATION_ARGUMENT_INVALID`.
- **Deviation from I398-SC-02:** the audit entry does not record the refused type name, because the planner has no audit hook. A follow-up issue is needed if the owner wants it.

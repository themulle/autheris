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

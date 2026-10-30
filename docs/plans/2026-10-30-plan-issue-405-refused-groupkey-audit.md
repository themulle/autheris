# Plan #405: audit the refused group-key type family on AGGREGATION_NOT_SUPPORTED

Follow-up of #398/#399 ([plan](2026-10-29-plan-issue-398-groupby-max-types.md), deviation from I398-SC-02). Phase 2, verified against origin/main c57ea685.

## Verified facts (origin/main)

- `GovernedTableReadPlanner.PlanAggregate` (GovernedTableReadPlanner.cs:161-164) refuses a non-groupable key with `SqlCompileNotSupportedException(Construct, "aggregation group key type")`, after the unknown, mask and access checks.
- The planner runs inside `ExecuteAndBuildBucketsAsync` (GovernedAggregationService.ValidationAndExecution.cs:361), which `ExecuteAggregationAsync` calls at GovernedAggregationService.cs:224. That call is outside the validation `try` (lines 175-198) that writes the deny audit. So today a refused group key writes **no** `AGGREGATE_QUERY` deny entry at all, not only no type name.
- REST (`AnalyticsEndpoints.IsNotSupportedRefusal`, :77), GraphQL (`AggregateErrors.cs:71`) and the metric path (through `ExecuteAggregationAsync`) map any `SqlCompileNotSupportedException` to `AGGREGATION_NOT_SUPPORTED` with one fixed message.
- The deny audit goes through `RecordAuditAsync` (Buckets.cs:601) to `AuditEventTypes.GovernedAggregation` = `AGGREGATE_QUERY`, tier Required, decision DENY. Its details keys are snake_case: `is_allowed`, `reasons`, `group_by`, `filter`.
- `DatabaseDialect` has five values: PostgreSql, SqlServer, Sqlite, Databricks and Oracle. There is no MySQL.
- `AGGREGATE_QUERY` details are not documented anywhere in `docs/` today.

## Decisions

1. **No DDL, no new decision literal, no new event type.** The family travels as two new keys in the existing `details_json` of the existing `AGGREGATE_QUERY` / DENY entry. `details_json` is already part of the HMAC payload (`v2|...|<details_json>|<tenant_id>`), so the chain stays intact and append-only is untouched. No migration step is needed, so the step numbers 0003/0004 are not affected.
2. **Closed family vocabulary.** New `public enum GroupKeyTypeFamily { Json, Xml, Spatial, Binary, Lob, Structured, Unknown }` and `public static class GroupKeyTypeFamilies`, in a new file `src/Autheris.Application/Data/Services/GroupKeyTypeFamilies.cs` (existing namespace `Autheris.Application.Data.Services`, about 90 lines).
   - `string ToAuditLiteral(GroupKeyTypeFamily)` is a switch over `const` strings: `json`, `xml`, `spatial`, `binary`, `lob`, `structured`, `unknown`.
   - `GroupKeyTypeFamily Classify(string? dataType, DatabaseDialect)` parses the base type the same way as `IsGroupableType` (trim, lower-invariant, `[]` suffix, cut at `(`/space/`<`). It never returns or logs input text. Anything unmatched, null or blank maps to `Unknown`.
   - SQL Server: xml -> xml; geography and geometry -> spatial; varbinary (any length) and binary -> binary; text, ntext and image -> lob; sql_variant, hierarchyid and a CLR/UDT -> structured.
   - PostgreSQL: json and jsonb -> json; xml -> xml; bytea -> binary; geometry, geography, point, line, lseg, box, path, polygon and circle -> spatial; `[]` arrays, hstore, tsvector and range types -> structured.
   - Oracle: clob, nclob, blob, long and bfile -> lob; raw -> binary; xmltype -> xml; json -> json; sdo_geometry -> spatial.
   - Databricks: map, struct, array and variant -> structured; binary -> binary.
   - SQLite: never refused, so it is never classified.
3. **Typed refusal carrying the family (no planner port).** Remove `sealed` from `TrinoSqlEngine.SqlCompileNotSupportedException` (CompileRequest.cs:172). Add `public sealed class AggregationGroupKeyNotSupportedException : SqlCompileNotSupportedException` in `src/Autheris.Application/Analytics/AggregationModels.cs`.
   - Its constructor calls `base(Construct, "aggregation group key type")`, so `Message` and `Reason` stay byte-identical to today, and every existing mapping site (REST, GraphQL, metrics, `SqlCompilerErrorMapper`) matches it unchanged.
   - Its only extra member is `GroupKeyTypeFamily Family { get; }`. Family and type text never go into `Message`.
   - A shared helper `GroupKeyTypeFamilies.EnsureGroupable(string? dataType, DatabaseDialect)` throws it when `!DefaultReadOrdering.IsGroupableType(...)`.
4. **Hoist the type check into the audited validation stage.** `ValidateAndResolveGroupBy` (ValidationAndExecution.cs:88) gets a `bool plannedSql` argument and calls `EnsureGroupable` after the mask and access checks. This keeps the I398-SC-03 order.
   - `plannedSql` comes from one new private helper `UsesPlanner(metadata)` (`_sqlExecutionService != null && DataSourceType == Sql`). `ExecuteAndBuildBucketsAsync` line 296 reuses the same helper, so the two cannot drift.
   - Non-SQL sources (in-memory path) keep today's behavior: they are not refused.
   - The planner keeps its check as defense in depth, now through `EnsureGroupable`. On the planned path the planner check becomes unreachable.
5. **Audit details.** In the `catch` at GovernedAggregationService.cs:188, use pattern matching on `AggregationGroupKeyNotSupportedException e`. Only in that case add `["refusal_reason"] = "GROUP_KEY_TYPE_NOT_GROUPABLE"` and `["group_key_type_family"] = GroupKeyTypeFamilies.ToAuditLiteral(e.Family)`.
   - Only the first refused key is recorded, because validation stops at the first one.
   - The existing keys stay unchanged.
   - There is no new log line, no metric and no type text.
6. **Responses unchanged.** Same governed ProblemDetails: REST 422 `AGGREGATION_NOT_SUPPORTED` and GraphQL `NotSupported` with the fixed message, identical for every family. There is no change to `ErrorSanitizingFilter` and no bare error body (`NoNewBareErrorBodiesTests` stays green).
7. **SIEM documentation.** Add a new section `### 4.al Aggregation group-key refusal (AGGREGATE_QUERY details)` to `docs/operations-runbook.md`, after §4.ak (line 326).
   - It describes the event type and decision, the two keys, and the closed literal table per dialect.
   - It states that the value is never raw type text or a column name.
   - It is reference text only, with no status notes.
8. **Ratchets.**
   - Clock: no clock use.
   - Size: all touched files stay well under 1000 lines (`GovernedAggregationService.cs` 602 + about 8, `AggregationModels.cs` 203 + about 20, `GroupKeyTypeFamilies.cs` new at about 90), so no baseline change.
   - Namespace: only the existing namespaces `Autheris.Application.Data.Services` and `Autheris.Application.Analytics` are used.

## Work packages (dialects: SQL Server, PostgreSQL, Oracle, Databricks; SQLite unaffected)

- **WP-1 Vocabulary and exception** (Decisions 2-3). Files: `GroupKeyTypeFamilies.cs` (new), `AggregationModels.cs`, `src/TrinoSqlEngine/CompileRequest.cs` (unseal). Tests: `tests/Autheris.Tests.Unit/DataApi/Issue405GroupKeyAuditTests.cs` (new).
- **WP-2 Audited refusal** (Decisions 4-6). Files: `GovernedAggregationService.cs`, `GovernedAggregationService.ValidationAndExecution.cs`, `GovernedTableReadPlanner.cs` (use `EnsureGroupable`). Tests: same file, plus `AnalyticsAggregationErrorCodesEndpointTests.cs` for parity.
- **WP-3 SIEM doc and doc-sync guard** (Decision 7). Files: `docs/operations-runbook.md`, plus a doc-sync test in the WP-1 test file.

Size: S, about 120-160 LOC src and 13 tests. One sequential lane: WP-1, then WP-2, then WP-3.

## Tests (all `[Trait("Category","Security")]`)

- I405_T_01_Classify_Matrix_PerDialect_MapsToDocumentedFamily: a theory over the Decision 2 table, plus null, blank and `udt_x` -> unknown.
- I405_T_02_AuditLiterals_AreExactlyTheClosedSet: the enum and literals are a bijection onto exactly the 7 literals.
- I405_T_03_Classify_NeverEchoesInput: a sentinel corpus of 50 random type strings with `SENTINEL`; each literal is in the set and none contains the sentinel.
- I405_T_04_EnsureGroupable_AgreesWithIsGroupableType: it throws if and only if `!IsGroupableType`, over the I398_T_01 matrix.
- I405_T_05_Exception_MessageAndReason_ByteIdenticalToPre405: `Message` equals the old text; it `is SqlCompileNotSupportedException { Reason: Construct }`.
- I405_T_06_RefusedKey_WritesDenyAudit_WithFamily_PlannerAndSqlNotCalled: SQL Server xml -> exactly one `AGGREGATE_QUERY` DENY entry with `group_key_type_family=xml` and `refusal_reason=GROUP_KEY_TYPE_NOT_GROUPABLE`. The planner, the SQL executor and the DP engine are not called.
- I405_T_07_TypeSentinel_AbsentFrom_Audit_Logs_Exception_ProblemDetails: a type `udt_SENTINEL_9f` -> family `unknown`. A captured logger, the serialized details, `ex.Message` and the REST body contain no `SENTINEL`.
- I405_T_08_ResponseBody_IdenticalAcrossFamilies_Rest_GraphQl_Metrics: xml, spatial and unknown -> byte-equal bodies, 422 `AGGREGATION_NOT_SUPPORTED`.
- I405_T_09_MaskedOrUnavailableKey_NoFamilyKey_OrderPreserved: masked -> masked refusal; unavailable -> unavailable refusal. Neither details carries a `group_key_type_family` key.
- I405_T_10_NonSqlSource_InMemoryPath_NotRefused: an HTTP-executor table with an `xml` key aggregates as before.
- I405_T_11_Sqlite_NeverRefused_NoFamily.
- I405_T_12_Planner_DefenseInDepth_ThrowsTypedException: `PlanAggregate` directly with a geography key -> `Family == Spatial`.
- I405_T_13_RunbookSection_ListsEveryLiteral: §4.al of `docs/operations-runbook.md` contains each literal and both key names.

## Open questions (default taken unless the owner rules otherwise)

1. Unseal `SqlCompileNotSupportedException`, or add a standalone exception and two mapping-site edits (REST :77, GraphQL :71)? **Default: unseal.** All mappings then match automatically, so no mapping site can be missed (fail-closed parity).
2. The existing `group_by` detail already lists the requested column names in the deny entry. Keep it? **Default: keep.** It is pre-existing, audit-only request detail. The new keys carry no name.
3. Multiple refused keys: record all families or only the first? **Default: first only.** Validation stops at the first refusal, and this keeps details cardinality fixed.
4. A metric counter labelled by family? **Default: no.** It is out of scope, and it keeps I398-SC-06 simple.
5. A container integration test for the audit entry? **Default: no.** The path is dialect-independent and the classifier is pure. The unit tests above cover it. The existing `SqlServerMaxGroupKeyTests` stays unchanged.

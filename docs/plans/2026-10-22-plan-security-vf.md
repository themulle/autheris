# Plan: Security Findings Area VF (Virtual Filters, Cross-Source Relations, changesSince)

Source: `docs/plans/security.txt` (audit 2026-10-21). Scope: every HIGH/MEDIUM finding with ID `VF-*` plus `TREE-01`:
VF-01, VF-02, VF-03, VF-04, VF-05, VF-11, VF-13, TREE-01. LOW findings (VF-06..VF-10, VF-12) are out of scope.
Verified against branch `fix/security-review-open-findings` @ 55dc7e27. No production yet: breaking changes are accepted.

## 1. Findings

### VF-01 SQL VF tenant binding bypassed by alias shadowing in a subquery (HIGH) - WP-VF-1
- **Verified, still open (authoring).** `SqlFilterCompiler.RequireTenantBinding/IsTenantBound` (`SqlFilterCompiler.cs:279-305`) match a
  top-level WHERE conjunct by qualifier text only; `ValidateAstPredicates` (`:362`) has no case for `ExistsExpression`,
  `InSubqueryExpression`, `ScalarSubqueryExpression`, so subqueries are accepted. `BindTenant` (`:316-360`) repeats the same check.
  Runtime mitigation exists only on the typed path: `PolicySubqueryTenantRewriter` (`TrinoSqlEngine/Ast/Security/...:27-31`) rejects a
  nested select (deny). The text predicate (`MandatoryRowFilterResolver.cs:504`, `MandatoryRowPredicateSql`) is still spliced by the
  keyset bulk planner (`GovernedTableReadPlanner.Keyset.cs:253`) and evaluated by `StreamingRowFilterAstEvaluator`, so the authoring
  gap must close at the source.
- **Fix.** Default taken (fix hint option "reject subqueries"; consistent with the runtime "one level deep" rule):
  1. New `private static void RejectNestedQueries(SelectStatement select, VirtualFilter filter)` (a `SqlAstRewriter`-based walker or
     recursive switch) that throws `Reject(...)` on any `ExistsExpression`, `InSubqueryExpression`, `ScalarSubqueryExpression`,
     `SubqueryTableSource`, `LateralTableSource` or a second `QuerySpecification` anywhere in the definition (WHERE, FROM, JOIN ON, CASE).
  2. New `private static void RejectDuplicateQualifiers(IReadOnlyList<TableAccessTarget> tables, VirtualFilter filter)`: the effective
     qualifier (`Alias ?? TableName`) must be unique (OrdinalIgnoreCase) across all referenced tables, and no alias may equal the bare
     table name of another referenced table.
  3. Both run in `Validate` before `RequireTenantBinding` (`:217`) and in `BindTenant` before `:351` (stored definitions created
     before the fix are denied at compile with `InvalidOperationException`, never skipped).
- **Fail-closed.** Any subquery or ambiguous qualifier -> `ArgumentException` (authoring, 400) / `InvalidOperationException` (compile,
  filter denies the read). No partial acceptance.
- **Red tests** (`VirtualFilterTenantBindingTests`): `Validate_InSubqueryReusingOuterAlias_Rejected` (audit payload),
  `Validate_ExistsSubquery_Rejected`, `Validate_ScalarSubquery_Rejected`, `Validate_DerivedTableInFrom_Rejected`,
  `Validate_SelfJoinWithoutAliases_Rejected`, `Validate_AliasEqualsOtherTableName_Rejected`,
  `Compile_StoredDefinitionWithSubquery_ThrowsInvalidOperation`, `Validate_JoinWithBothTenantConjuncts_Accepted` (regression).
- **Breaking.** SQL VF definitions with subqueries/derived tables become invalid (already runtime-denied on the typed path).
- **Dependencies.** None. VF-06 (LOW, keyset text splice) stays separate.

### VF-02 Cross-source relation reader runs raw `SELECT *` outside governance (HIGH) - WP-VF-2
- **Verified, open.** `GovernedCrossSourceRowReader.ReadRowsByKeysAsync` (`:32-106`) opens a plain connection, no
  `IDbSessionContextInitializer`, no transaction, `SELECT * FROM` + unquoted schema/table/join columns, marker hard-coded `@p{i}`.
  Additionally `CrossSourceRelationResolver.ResolveRelationAsync` never calls
  `InMemoryRowFilterEvaluator.EnsureInMemoryFilterIsEnforceable(childDecision)`; a child with an active VF is evaluated in memory
  via `StreamingRowFilterAstEvaluator` on an `EXISTS` text (unenforceable).
- **Fix.**
  1. Change `ICrossSourceRowReader.ReadRowsByKeysAsync` to
     `Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsByKeysAsync(CrossSourceKeyReadRequest request, CancellationToken ct)`
     with `sealed record CrossSourceKeyReadRequest(TableMetadata Target, IReadOnlyList<string> JoinKeyColumns, IReadOnlyList<string> ProjectedColumns, IReadOnlyList<CompositeKey> Keys, TenantId Tenant, string? UserSid)`
     in `CrossSourceRelationModels.cs`. `InMemoryCrossSourceRowReader` follows.
  2. Reader: every join key and projected column must exist in `Target.Columns` (OrdinalIgnoreCase lookup, use the catalog spelling)
     else `InvalidOperationException`; `ProjectedColumns` = projection set plus join keys plus tenant column (no `SELECT *`).
     Table via `dialect.FormatTableIdentifier`, columns via `dialect.QuoteIdentifier`, markers via `FormatParameterMarker` and
     provider names via the matching provider-name helper (`DatabaseDialect.cs:43-46`).
  3. When `Target.TenantColumnName != null`, append `AND <quoted tenant col> = <marker vf_tenant>` bound to `Tenant.Value`.
  4. Session: PostgreSQL read-only transaction (`SET TRANSACTION READ ONLY`, never committed) and
     `IDbSessionContextInitializer.InitializeSessionAsync(connection, tx, dialect, tenant, userSid, purpose: null, ct)`, same pattern as
     `SqlVirtualFilterKeySetProvider.cs:248-263`. Inject `IDbSessionContextInitializer` via constructor (required, no `new` fallback).
  5. Resolver: call `EnsureInMemoryFilterIsEnforceable(childDecision)` before reading (403 when the child carries a VF).
  6. Architecture allowlist entry (`CutoverArchitectureTests.cs:424`) text updated: quoted catalog identifiers, bound tenant, session init.
- **Fail-closed.** Unknown column, missing connection, session-init failure -> exception (request fails), never an unscoped read.
- **Red tests** (`CrossSourceRelationsAndChangesSinceTests` / new `GovernedCrossSourceRowReaderTests` with a recording
  `DbConnection` fake): `ReadByKeys_QuotesTableAndColumns`, `ReadByKeys_UnknownJoinColumn_Throws`,
  `ReadByKeys_TenantedTarget_AddsBoundTenantPredicate`, `ReadByKeys_InitializesSessionWithTenantAndUser`,
  `ReadByKeys_Oracle_UsesColonMarkers`, `ReadByKeys_ProjectsExplicitColumns_NoSelectStar`,
  `ResolveRelation_ChildWithVirtualFilter_Forbidden`; integration (PostgreSQL Testcontainers):
  `CrossSourceRelation_MixedCaseIdentifiers_ReturnsRows`, `CrossSourceRelation_RlsKeyedOnSessionTenant_OnlyOwnRows`.
- **Breaking.** Cross-source relations into VF-protected tables return 403 instead of (wrong) in-memory results.
- **Dependencies.** Shares files with VF-03, VF-04, TREE-01 (same WP).

### VF-03 In-memory tenant check passes NULL/missing tenant (MEDIUM) - WP-VF-2
- **Verified, open** (`CrossSourceRelationResolver.Helpers.cs:239-246`; same pattern in VF-11 `GovernedChangeDetectionService.Cursors.cs:289-296`).
- **Fix.** New `internal static class TenantRowGuard` (`Autheris.Application/Data/TenantRowGuard.cs`):
  `public static bool Matches(IReadOnlyDictionary<string, object?> row, string? tenantColumn, TenantId tenant)` - `tenantColumn == null`
  -> true; otherwise the column must be present (case-insensitive key lookup as `TryGetColumnValue`), non-null, non-`DBNull`, and
  `string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), tenant.Value, StringComparison.Ordinal)`. Used by
  `PassesTargetGovernance` and `PassesCallerGovernance` (VF-11). Defense in depth behind the SQL tenant predicate of VF-02.
- **Red tests** (`TenantRowGuardTests`): `Matches_NullTenantValue_False`, `Matches_MissingColumn_False`, `Matches_DbNull_False`,
  `Matches_CaseDifferentTenant_False`, `Matches_ExactTenant_True`, `Matches_NonTenantedTable_True`; resolver:
  `ResolveRelation_ChildRowWithNullTenant_Dropped`.
- **Breaking.** Shared rows with `tenant_id IS NULL` are no longer attached; tenant ids differing by case no longer match.

### VF-04 Latent SQL injection via cross-source filter text (MEDIUM) - WP-VF-2
- **Verified, open but unreachable**: `ReadRowsForFilterAsync` (`GovernedCrossSourceRowReader.cs:129-189`) appends
  `TargetFilterSql` verbatim; `ResolveCrossSourceFilterKeysAsync` has no production caller (only
  `CrossSourceRelationsAndChangesSinceTests.cs:340`).
- **Fix.** Default taken (YAGNI, strongest fail-closed): delete `ICrossSourceRowReader.ReadRowsForFilterAsync`,
  `ICrossSourceRelationResolver.ResolveCrossSourceFilterKeysAsync`, `CrossSourceFilterKeySetRequest/Result` and both
  implementations; remove the related unit tests. Also drop `CrossSourceRelationRequest.TargetFilterSql` (and
  `ReferencedTargetColumns`) if a grep shows no production setter; otherwise its value is evaluated only in memory (unchanged).
  A future feature must use a typed filter tree compiled with bound parameters (new plan).
- **Red tests.** Architecture test `CrossSourceRowReader_HasNoClientTextEntryPoint` (reflection: no public method on
  `ICrossSourceRowReader` takes a `string` parameter).
- **Breaking.** Public interface members removed (no callers).

### TREE-01 Reverse-stored cross-source relation passed unswapped (MEDIUM) - WP-VF-2
- **Verified, open** (`GovernedTreeQueryService.cs:527-531` accepts the reverse match; `:545` passes it unchanged).
- **Fix.** New `public TableRelation Inverted()` on `TableRelation` (`Autheris.Domain/Model/TableModels.cs:454`): swaps
  Parent/Child identifiers, ids and join keys, keeps `RelationName`, `AllowCrossDataSource`, maps `OneToMany<->ManyToOne`,
  `OneToOne` unchanged. In `GovernedTreeQueryService` split the predicate: forward match used as is, reverse match -> `.Inverted()`.
  Before building the request assert `relModel.ParentTableIdentifier == root.Table && relModel.ChildTableIdentifier == cr.Child.Table`
  (else `InvalidOperationException`).
- **Red tests.** `TableRelationTests.Inverted_SwapsSidesAndCardinality`; `GovernedTreeQueryServiceTests.CrossSource_ReverseStoredRelation_ReadsChildTable`
  (fake resolver captures request: Child == orders, JoinKeysParent == root columns),
  `CrossSource_ForwardStoredRelation_Unchanged`.

### VF-05 Approval does not bind the reviewed definition (MEDIUM) - WP-VF-3
- **Verified, open.** `expectedHash` optional in `ApproveFilterAsync` (`VirtualFilterAdministrationService.cs:263-294`) and
  `ApproveProfileAsync` (`.Evaluation.cs:160-191`); endpoints read it from an optional query (`VirtualFilterEndpoints.Crud.cs:57,93`).
  Read API already returns `definition_hash`/`draft_hash` (`:121-125,153-157`); a `pending_hash` is not exposed explicitly.
- **Fix.**
  1. Remove the hash-less overloads (`:263-264`, `.Evaluation.cs:160-161`); signature becomes
     `ApproveFilterAsync(TenantId, string name, VirtualFilterActor actor, string expectedHash, CancellationToken ct)` (same for profile),
     `ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash)`; comparison `StringComparison.Ordinal` on the lower-case hex hash.
  2. Endpoints: missing `hash`/`expected_hash` -> 400 `{"error":"expected_hash_required"}`; mismatch stays 409.
  3. Read DTOs add `pending_hash` (the exact value approval compares: deletion -> definition hash, draft -> draft hash, else definition).
     Extract `internal static string PendingHash(VirtualFilter)` / `(VirtualFilterAccessProfile)` used by both service and DTO.
  4. Atomicity between compare and write: PERS-13 (`ExpectedGeneration` CAS) - approval passes `snapshot.Generation`.
- **Fail-closed.** No hash -> no approval; stale snapshot -> conflict, no retry.
- **Red tests** (`VirtualFilterFourEyesApprovalTests`, `VirtualFilterEndpointsTests`): `ApproveFilter_WithoutHash_Rejected`,
  `ApproveProfile_WithoutHash_Rejected`, `ApproveFilter_DraftChangedAfterReview_Conflict`, `Endpoint_ApproveWithoutHash_Returns400`,
  `Endpoint_Get_ReturnsPendingHashMatchingApproval`, `ApproveFilter_HashCaseDiffers_Conflict`.
- **Breaking.** Approve calls without hash fail; existing tests/clients must pass `pending_hash`.
- **Dependencies.** PERS-13 (WP-PERS-E) first or in the same branch; EP-01 touches the same endpoint file (rebase).

### VF-11 changesSince `knownKeys` as cross-scope removal oracle (MEDIUM) - WP-VF-4
- **Verified, open** (`GovernedChangeDetectionService.Cursors.cs:125-153`: `knownKeySet.Contains(pk)` and `AllDeletedKeys` return true
  unconditionally; `!hasRowLevelFilter && isRlsExit` ignores tenant; tenant check `:289-296` as VF-03).
- **Fix.** `ShouldDiscloseRemovedKey` rewritten; `knownKeySet` only narrows (a removal is reported only if no `KnownKeys` were sent
  or the key is in the set), never authorizes:
  - scoped table (`TenantColumnName != null` or row-level filter present): disclose only when `preImage != null &&
    PassesCallerGovernance(preImage, ...)`; `AllDeletedKeys` is ignored for scoped tables.
  - unscoped table: disclose (existing `AllDeletedKeys`/default behaviour).
  - Provider capability: `IChangeVersionProvider.ProvidesPreImages` (bool). Scoped table + provider without pre-images ->
    `ChangeTrackingNotSupported` before any change is read (no silent drop of removals).
  - `PassesCallerGovernance` uses `TenantRowGuard.Matches` (VF-03).
- **Red tests** (`CrossSourceRelationsAndChangesSinceTests` or new `ChangesSinceDisclosureTests`):
  `ChangesSince_KnownKeyOfOtherTenant_NotInRemoved`, `ChangesSince_RlsExitOfInvisibleRow_NotInRemoved`,
  `ChangesSince_DeleteWithVisiblePreImage_InRemoved`, `ChangesSince_AllDeletedKeysOnTenantedTable_OnlyOwnTenant`,
  `ChangesSince_NullTenantRow_NotUpserted`, `ChangesSince_ScopedTableWithoutPreImages_NotSupported`,
  `ChangesSince_KnownKeys_NarrowRemovedList`.
- **Breaking.** Clients no longer get removals for keys they merely claim; scoped tables need a pre-image capable provider.

### VF-13 changesSince returns empty deltas and advances the cursor (MEDIUM) - WP-VF-4
- **Verified, open.** `DataApiServiceExtensions.cs:27` registers `InMemoryChangeVersionStore`; its `GetCurrentVersionAsync`
  (`InMemoryChangeVersionStore.cs:60-75`) returns the MSSQL watermark while `GetChangesBetweenAsync` (`:85`) reads the empty in-process
  list; `MaxChangesPerCall` (`ChangeDetectionModels.cs:33`) unused, `HasMore` hard-coded false (`GovernedChangeDetectionService.cs:144,265`).
- **Fix.**
  1. New `NoChangeLogVersionProvider : IChangeVersionProvider` (all versions null) is the production default at `:27`;
     `GetChangesSinceAsync` already maps null to `ChangeTrackingNotSupported` (`:127-130`). `InMemoryChangeVersionStore` loses the
     watermark-store dependency and is registered only by tests (moved to `Autheris.Tests.Common` if no production use remains).
  2. `IChangeVersionProvider.GetChangesBetweenAsync(table, sinceVersion, upToVersion, int maxRecords, ct)`; the service requests
     `MaxChangesPerCall + 1`, orders by `ChangeVersion`, and when over the cap cuts at the last complete version `v <= cap` (records of one
     version are never split), sets `HasMore = true` and issues the cursor at `v`. A single version larger than the cap ->
     `ChangeTrackingNotSupported` with reason `change_batch_too_large` (never an unbounded read, never a skipped version).
  3. `ReadCurrentRowsByPrimaryKeysAsync` receives at most the capped key set.
- **Red tests:** `ChangesSince_DefaultRegistration_NotSupported` (DI test on `AddAutherisDataApi`),
  `ChangesSince_OverCap_HasMoreAndCursorAtLastCompleteVersion`, `ChangesSince_FollowUpCall_ReturnsRemainder`,
  `ChangesSince_SingleVersionOverCap_NotSupported`, `ChangesSince_ProviderReceivesCapPlusOne`.
- **Breaking.** `/changes` / `changesSince` returns `CHANGE_TRACKING_NOT_SUPPORTED` until a real change-log provider exists.

## 2. Work packages (order)

| WP | Findings | Scope | Size |
|----|----------|-------|------|
| WP-VF-1 | VF-01 | `SqlFilterCompiler` + tests | S (~150 LOC) |
| WP-VF-2 | VF-03, VF-04, VF-02, TREE-01 | Relations reader/resolver, `TenantRowGuard`, `TableRelation.Inverted`, tree service | L (~600) |
| WP-VF-3 | VF-05 | Admin service, endpoints, DTOs (after WP-PERS-E) | S (~200) |
| WP-VF-4 | VF-13, VF-11 | Change detection service, provider interface, DI | M (~450) |

WP-VF-1 and WP-VF-2 are independent and can run in parallel lanes; WP-VF-4 reuses `TenantRowGuard` from WP-VF-2 (run after it).

## 3. Security conditions for Phase 3
1. No SQL VF definition with any nested query construct passes `Validate` or `BindTenant`; qualifiers are unique.
2. The cross-source reader never executes catalog/relation identifiers unquoted, never `SELECT *`, always binds the tenant
   predicate for tenanted targets and initializes the session before the read; no client text reaches native SQL.
3. All in-memory tenant checks deny NULL/missing tenant values and compare ordinal (single `TenantRowGuard`).
4. VF/profile approval without an expected hash, or with a stale snapshot generation, never changes state.
5. A removal is disclosed only with pre-image proof of visibility on scoped tables; `knownKeys` never authorize.
6. `changesSince` never advances a cursor past undelivered changes and never materializes more than `MaxChangesPerCall + 1` records.
7. Phase 3 to verify: whether VF-protected children should later be supported on the relation path via the governed executor
   (out of scope here; 403 now).

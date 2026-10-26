# Plan: Remove orphaned code left by #344-#359

**Input:** owner decision 2026-10-26 ("Orphanes Code entfernen"). Phase 1 skipped (lean removal). Base `origin/main` 87d33b74, branch `chore/remove-orphaned-code`. No production yet; no behaviour change intended.

## Context
PR #356 removed the `finance`/`hr` root fields and `InvoiceItemDataLoader` but kept the application batch path (Q1 of [`2026-10-25-plan-remove-demo-finance-hr-fields.md`](2026-10-25-plan-remove-demo-finance-hr-fields.md)). That path now has no production caller: `GatewayExecutionService.LoadInvoiceItemsBatchAsync` is only called from unit tests, and `BatchRelationLoader` exposes no other public method. The sweep over #344-#359 (src, tests, docs, benchmarks, scripts, deploy, *.json/*.yaml/*.js, wwwroot) found no further orphans beyond the items below.

## Delete / edit table
| # | File | Symbol | Action | Evidence (grep on 87d33b74) |
|---|---|---|---|---|
| D1 | `src/Autheris.Domain/Model/InvoiceModels.cs` | `InvoiceItemRecord` (only type in file) | delete file | Used only by D2, E1, E2; `DemoRootFieldsRemovedTests` holds it as an SDL string literal only. |
| D2 | `src/Autheris.Application/Services/BatchRelationLoader.cs` | whole class (`LoadInvoiceItemsBatchAsync`, `LastDispatchedChildQueryCount`, `Reasons`, `LastRefusalReason`, `RecordReason`) | delete file | Only constructed in `GatewayExecutionService` ctor (line 90) and `BatchRelationLoaderGovernanceTests`; no DI registration. `RelationKeyMasked` stays (used by `GovernedTreeQueryService`). |
| E1 | `src/Autheris.Application/Interfaces/IGatewayExecutionService.cs` | `LastDispatchedChildQueryCount`, `LoadInvoiceItemsBatchAsync` | remove members | Only implementer is `GatewayExecutionService`; NSubstitute mocks in Extensions/Unit tests never configure them. |
| E2 | `src/Autheris.Application/Services/GatewayExecutionService.cs` | fields `_batchLoader` (32), `_chunkedQueryExecutor` (23); ctor lines 81 and 90; property `LastDispatchedChildQueryCount` (41); method `LoadInvoiceItemsBatchAsync` (687-691) | remove | `_chunkedQueryExecutor` is read only on line 90. Keep the `chunkedQueryExecutor` ctor parameters (Q2). `CheckTableAccessAsync` overloads stay (interface members). |
| T1 | `tests/Autheris.Tests.Unit/InvoiceItemsBatchExecutionTests.cs` | 2 tests | delete file | Tests only D2 via E2. |
| T2 | `tests/Autheris.Tests.Unit/GraphQL/BatchRelationLoaderGovernanceTests.cs` | 4 tests | delete file | Tests only D2. |
| T3 | `tests/Autheris.Tests.Unit/ChunkedQueryExecutorTests.cs` | `GatewayExecutionService_LoadInvoiceItemsBatchAsync_WhenBatchIsLarge_DispatchesMultipleChunkQueries` (174-251) | delete method | Keep the other 8 `ChunkedQueryExecutor` tests. |
| T4 | `tests/Autheris.Tests.Unit/OddBatchDataLoaderEvaluationTests.cs` | region "DataLoader Batch Evaluation with Odd Counts in GatewayExecutionService" (156-238, theory with 3 cases) | delete region | Keep the 2 executor/budget regions. |
| T5 | `tests/Autheris.Tests.Unit/DataPathSecurityTests.cs` | `ChildRelations_AllColumnsRespectAccessLevelAndMasking` (257-311), `LoadInvoiceItemsBatchAsync_WhenAccessLevelIsDenyOrUndefined_FailsClosed`, `..._WhenAccessLevelIsClear_DoesNotMaskEvenIfMaskingRuleConfigured`, `..._WhenChildTableHasRowFilter_FiltersChildItemsCorrectly`, `..._RowFilterIsEvaluatedOnRawValues_BeforeMasking` (582-end) | delete 5 methods | All call `LoadInvoiceItemsBatchAsync`; the other 14 tests stay. |
| X1 | `docs/plans/security.txt` | GQL-01 (line 89) and GQL-02 (line 102) | append note, keep `[RESOLVED ...]` tags | GQL-01: "Code removed: `InvoiceItemDataLoader` in #356, `BatchRelationLoader` in this PR." GQL-02: "The `finance` root field and its `invoicesWithItems` mapping were removed in #356." |
| X2 | `docs/architecture/arc42.md:188` | sequence label `Query FinanceRecords` | edit to `Query table(domain, name)` | `FinanceRecords` field no longer exists (system reference must match behaviour). |

After WP1, remove `using` directives that become unused in the edited files (D1 namespace `Autheris.Domain.Model` stays in use elsewhere in E1/E2).

## Checked and kept
- `IDemoDataSwitch` / `DemoDataSwitch`, `SeedDemoData`, `Gateway:Dev:DemoData`: still read by `Program.cs`, `GoldenQueryService`, `McpToolRegistry`, `DeltaLakeDataSourceExecutor`.
- Seeded demo catalog incl. `finance.dbo.finance_items` relation (`SqliteGovernanceRepository.Schema.cs`, `deploy/sql/*`, `seed_governance.py`) and `InfrastructureTests`: governance demo data, reached by the generic `table`/catalog path.
- `Issue341*` tests (unit + integration), `DemoRootFieldsRemoved*Tests`: guard current behaviour (generic field, removed roots).
- DevPortal persona cards (Alice Finance / Bob HR): test-auth users, not demo fields. Benchmarks/k6 use `table(domain: "finance", ...)` or self-contained `DummyQuery` schemas.
- `RecertificationService.GetItemsAsync` and similar: name collision only.
- Generic `invoices`/`employees` tables in SQL/OData/WebSQL tests and docs/features examples: fixtures, not the removed fields. docs/features contain no description of `finance`/`hr`/`invoicesWithItems` root fields; ADR-003 already carries the removal note.
- `tests/Autheris.Tests.Simulation/KnownGaps/drafts/ISSUE-341-*.md`: historical issue record (simulation lane, excluded).

## Uncertain - left in place
- `IChunkedQueryExecutor` / `ChunkedQueryExecutor` + singleton DI registration (`GatewayExecutionServiceExtensions.cs:48`) and `GraphQlOptions.MaxInClauseBatchSize` (bound from config, `appsettings.Benchmark.json:121`, configuration-guide row): no production caller after D2, but DI- and config-reachable and named in ADR-008/ADR-009/developer-guide. Follow-up (Q2).
- `GatewayExecutionService` ctor parameter `chunkedQueryExecutor` (both ctors, DI factory line 84): public signature with positional callers in 16 files.
- `CHILD_RELATION_CHECK` audit event type in `CheckTableAccessAsync`: emitted on every access check, not only child relations; audit consumers may filter on it.

## WP1 (Sonnet TDD developer, deletions only)
No new tests (no behaviour change; the removed tests covered only the removed path). Order: T1-T5, then D1, D2, E1, E2, X1, X2. Tests deleted: 13 methods (one is a 3-case theory) in 5 files; tests kept: all others in those files. Unit tests of `GovernedTreeQueryService` and `CrossSourceRelationsSecurityTests` keep `RELATION_KEY_MASKED` fail-closed coverage. Do not touch `scripts/sim/run-parser-fuzz.sh`, `.github/workflows/*`, or historical plan files.

## Verification
- `dotnet build Autheris.sln -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj`
- `dotnet test tests/Autheris.Extensions.Tests/Autheris.Extensions.Tests.csproj` (mocks of `IGatewayExecutionService`)
- `dotnet test tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj --filter "FullyQualifiedName~DemoRootFieldsRemoved|FullyQualifiedName~Issue341"`
- Residual grep returns nothing in `src/ tests/ benchmarks/`: `grep -rnE "LoadInvoiceItemsBatchAsync|BatchRelationLoader|LastDispatchedChildQueryCount|class InvoiceItemRecord"`.

## Open questions (defaults taken)
- **Q1:** Delete `BatchRelationLoader` governance tests although they encode fail-closed rules (GQL-01)? **Default: yes**; the rules are covered on the live path by `GovernedTreeQueryService` and `CrossSourceRelationsSecurityTests`.
- **Q2:** Also remove `ChunkedQueryExecutor`, its DI registration, the ctor parameter and `MaxInClauseBatchSize`? **Default: no** in this PR (DI/config-reachable, public ctor change across 16 files); log as follow-up.

## Phase 3 security review (csharp-security-expert, 2026-10-26)
**Verdict: APPROVED WITH CONDITIONS.** Q1 default (delete) and Q2 default (keep `ChunkedQueryExecutor`) confirmed; no security reason to change either.

Verified facts: `BatchRelationLoader` is constructed only in `GatewayExecutionService` ctor (no DI registration); `InvoiceItemRecord`/`BatchRelationLoader`/`LastDispatchedChildQueryCount` have no reflection, `typeof`/`nameof`, JSON/YAML/config or serialization reference (only an SDL `ShouldNotContain("InvoiceItemRecord")` literal in `DemoRootFieldsRemovedTests`). The loader's `RelationKeyMasked` emission is the only deleted fail-closed code with a live twin (`GovernedTreeQueryService`, `ErrorSanitizingFilter` allow-list); the constant stays. Deleted deny/clear/row-filter-before-masking behaviour now has no caller at all, so the loss is unreachable-code removal, not a control regression.

Binding conditions
- **ORC-S1:** Keep `CrossSourceRelationErrorCodes.RelationKeyMasked`, its `ErrorSanitizingFilter` entry and the `GovernedTreeQueryService` emission untouched; the surviving tests `T_SEC_DATA75_01_ClearJoinKeysEnforced_MaskedParentKey_FailsClosed` and `..._MaskedChildKey_FailsClosed` (`CrossSourceRelationsSecurityTests`) must be unchanged and green. Do not delete or edit any other test in that file or in `GovernedConnectorReader*` tests (`GovernedConnectorReaderArch2Tests`, `GovernedDataPathsG4Tests`), which are the live-path cover for deny/mask/row-filter-before-mask (also `EX04_LakehouseExecutor_EvaluatesRlsFilter_BeforeColumnMasking`).
- **ORC-S2:** Delete only the test methods listed in T1-T5. In `DataPathSecurityTests` the range 582-end must not remove unrelated tests (verify method names against the list before cutting); the PR description must state per deleted test that it exercised only `BatchRelationLoader`.
- **ORC-S3:** X1 note for GQL-01/GQL-02 must keep the `[RESOLVED ...]` tags and Location lines, and state that the fix is moot because the code was removed (no live path), not that a control was relocated. The `CHILD_RELATION_CHECK` event in `CheckTableAccessAsync` stays unchanged (audit consumers).
- **ORC-S4:** Residual grep (plan Verification) must be empty in `src/ tests/ benchmarks/ deploy/ scripts/` and `dotnet build -warnaserror` must pass; if any DI/reflection reference surfaces, stop and re-plan rather than keep a stub.

Mandatory tests: no new tests (pure deletion). Existing `[Trait("Category","Security")]` suites named in ORC-S1 must pass in the Unit run.

## Addendum 2026-10-26: Q2 changed to YES (owner)
The items of the "Uncertain" list for `IChunkedQueryExecutor`/`ChunkedQueryExecutor`, its DI registration, `GraphQlOptions.MaxInClauseBatchSize` (appsettings, configuration guide), the `chunkedQueryExecutor` ctor parameter (both ctors, DI factory, test call sites) are now in the delete table (D3-D7). Deleted with it: `ChunkedQueryExecutorTests`, `OddBatchDataLoaderEvaluationTests`, `ParameterBudgetChunkerTests`, and benchmark section [6] (composite key batching) in `ScaleCatalogBenchmark`. `IParameterBudgetProvider` stays (used by `CrossSourceRelationResolver`). `CHILD_RELATION_CHECK` stays (emitted on every access check). ADR-008/ADR-009 carry a removal note instead of a rewrite.

| # | File | Action |
|---|---|---|
| D3 | `Interfaces/IChunkedQueryExecutor.cs`, `Services/ChunkedQueryExecutor.cs` | delete (incl. `ParameterBudget` record, used only by the executor and the benchmark) |
| D4 | `GatewayExecutionServiceExtensions.cs` | remove singleton registration and factory argument |
| D5 | `GraphQlOptions.MaxInClauseBatchSize` | remove option and config rows |
| D6 | `GatewayExecutionService` | remove `chunkedQueryExecutor` ctor parameter |
| D7 | unit tests / benchmark | delete executor tests; fix ctor call sites; drop benchmark section [6] |

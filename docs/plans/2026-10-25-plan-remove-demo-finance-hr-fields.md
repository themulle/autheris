# Plan: Remove the demo GraphQL root fields `finance` and `hr`

**Input:** owner decision 2026-10-25 ("Felder entfernen"). Phase 1 (PRD) skipped as a lean removal; the owner decision is the requirement. No production yet, so the breaking change is accepted. Branch `fix/remove-demo-finance-hr-fields` (base `322fde4e`).

## Scope
Remove the `finance` and `hr` root fields and the GraphQL types that only they reach (`FinanceQuery`, `HrQuery`, `InvoiceRecord` with its `items` relation). **Keep:** the generic `table` field, `/graphql/{domain}` (`DomainScopeRewriteMiddleware`), `IDemoDataSwitch`/`DemoDataSwitch` (still used by MCP tools, golden queries, lakehouse executors and seeding), and all `TableIdentifier("finance", ...)` test data and `table(domain: "finance", ...)` queries.

## Decisions
1. **GQL-02 mapping is demo-only.** `FinanceQuery.MapInvoice` maps only for `invoicesWithItems`; `table` returns serialized rows without mapping. It is deleted together with its tests.
2. **The GraphQL nested relation goes; the application batch loader stays (default taken, see Q1).** `InvoiceRecordExtensions.GetItemsAsync` is the only consumer of `InvoiceItemDataLoader`, so both are deleted. `BatchRelationLoader.LoadInvoiceItemsBatchAsync`, `IGatewayExecutionService.LoadInvoiceItemsBatchAsync`, `InvoiceItemRecord` and their unit tests (governance, chunking, consent) stay unchanged.
3. **The demo switch stops gating GraphQL.** `AddGatewayGraphQL` loses the `demoDataEnabled` parameter. `Program.cs` keeps the `DemoDataSwitch.Resolve` call and the `IDemoDataSwitch` singleton.
4. **The DevPortal "Finance Scope" button is removed (default taken).** The `/graphql/{domain}` route itself stays; `ReadOnlyTokenTests` `/graphql/finance` stays.

## Inventory: production code (src/)
- `src/Autheris.GraphQL/Types/QueryTypes.cs:771-883`: delete `DemoQueryExtensions`, `FinanceQuery` (incl. `MapInvoice`, `ToDecimalOrNull`) and `HrQuery`. Afterwards remove usings that are no longer needed.
- `src/Autheris.GraphQL/Types/InvoiceTypes.cs`: delete the file.
- `src/Autheris.GraphQL/Loaders/InvoiceItemDataLoader.cs`: delete the file.
- `src/Autheris.Domain/Model/InvoiceModels.cs:3-9`: delete `InvoiceRecord`; keep `InvoiceItemRecord`.
- `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs:548,615-621`: drop the `demoDataEnabled` parameter and the `if` block that registers both type extensions.
- `src/Autheris.Api/Program.cs:68`: change to `AddGatewayGraphQL(gatewayOptions)`; keep lines 66-67.
- `src/Autheris.Api/Endpoints/DevPortalAssets.cs:173`: delete the `/graphql/finance` "Finance Scope" anchor.
- `src/Autheris.Domain/Options/DemoDataSwitch.cs:7`: drop "the demo GraphQL types (finance, hr, InvoiceRecord)" from the summary.
- `src/Autheris.GraphQL/Interceptors/DbtHealthExecutionMiddleware.cs:287`: make the comment example generic, e.g. "nested selections, e.g. `table { jsonRows }` under fragments".

## Inventory: tests
| File:line | Action | Reason |
|---|---|---|
| new `tests/Autheris.Tests.Unit/GraphQL/DemoRootFieldsRemovedTests.cs` | add (red first) | `Schema_HasNoFinanceOrHrRootFields`: build the schema via `AddGatewayGraphQL(new GatewayOptions())` and assert that the SDL contains none of `finance:`, `hr:`, `FinanceQuery`, `HrQuery`, `InvoiceRecord`, `InvoiceItemRecord`, but still contains `table(`. `FinanceOrHrRootSelection_FailsValidation` (theory `{ finance { __typename } }`, `{ hr { __typename } }`): errors are non-empty, `data` is null, and no `INTERNAL` code appears. Red today because the default `demoDataEnabled = true` registers the fields. |
| new `tests/Autheris.Tests.Integration/DemoRootFieldsRemovedIntegrationTests.cs` | add | POST `/graphql` `{ finance { invoices { jsonRows } } }` as Reader gives a GraphQL validation error, not an internal error, and no rows. It is a new file to avoid the Lane A overlap. |
| `Tests.Integration/QueryDefenseStressTests.cs:45,72,99,125,158,187,251,279` | **rewrite** to `table(domain:"finance", schema:"dbo", name:"finance_table_1", first: N) { jsonRows }` | The cost and depth defenses are not demo features, so every status and code assertion stays unchanged. Omitted-limit test: use several aliased `table` selections without `first`, costed at `DefaultPayloadPageSize` 50 each, so the total exceeds 250. Depth test: use a real nested path of depth 7 or more from the current schema; if none exists, use an introspection `__type { fields { type { ofType ... } } }` chain. Fragment and inline-fragment tests: wrap `table(first: 5000)`. |
| `Tests.Integration/WalkingSkeletonIntegrationTests.cs:236,320,378` | delete 3 tests | They test exactly the removed `invoicesWithItems { items }` relation. Unit coverage of the loader stays in `BatchRelationLoaderGovernanceTests`, `InvoiceItemsBatchExecutionTests`, `OddBatchDataLoaderEvaluationTests` and `DataPathSecurityTests`. Remove only private helpers that become unused. |
| `Tests.Integration/FourEyesAndDelegationIntegrationTests.cs:309` | delete `DataLoader_OddParentBatchEvaluation_WithSensitiveMasking_EndToEnd` | Same reason. |
| `Tests.Integration/Issue341GraphQlInternalErrorTests.cs:57-58` | delete 2 theory rows | **Lane A overlap.** The rows test the removed fields. Edit only these 2 lines after rebasing on Lane A. |
| `Tests.Unit/GovernanceSecurityTests.cs:861-886` | delete `FinanceAndHrQuery_Unauthenticated_ThrowsUnauthorized` | The resolvers are removed. |
| `Tests.Unit/GraphQL/Gql02InvoiceMappingTests.cs` | delete the file | Decision 1. |
| `GoldenQuerySecurityTests.cs:64,327`, `ReadOnlyTokenTests.cs:237`, `GraphQLTests.cs` | no change | These are query text never run against the schema, the domain-scope route, and the generic schema snapshot. |

## Inventory: non-code
- `docs/schema/gateway-schema.graphql`: this is a hand-written, already stale file. Remove `financeRecords` (11), `finance` (14), `hrEmployees` (17) and the types `FinanceRecord` (65), `HrEmployee` (73), `FinanceQuery` (112), `InvoiceRecord` (120) and `InvoiceItemRecord` (128). Do not regenerate the file (Q2).
- `docs/adr/ADR-003-dynamic-object-type-mapping.md:12`: append one English "Update 2026-10-25" line saying that domain namespace root fields were removed and that scoping uses `/graphql/{domain}` plus `table`.
- `deploy/containers/load-generator/k6-benchmark.js:241-263`: rewrite the nested query to `table(..., first: $first) { jsonRows }` and change the check to `jsonRows`.
- `deploy/README.md:177` and `deploy/scripts/generate_report.py:98`: update the query mix text to match.
- No hits in `README.md`, `benchmarks/` (`SlaValidationBenchmark` uses an unrelated root `invoices`, so leave it), `scripts/` or `.github/`.
- **Lane A overlap:** `docs/plans/security.txt` (the GQL-02 entry refers to the removed mapping) is not edited here; flag it for the tracker update after merge. `00-master-plan-overview.md` gets one appended row only.

## TDD order (WP1, one work package)
1. Red: add `DemoRootFieldsRemovedTests` and the integration test, and confirm that they fail.
2. Rewrite `QueryDefenseStressTests` to `table` and confirm it is green on the current code.
3. Delete the demo-only tests listed above.
4. Remove the production code listed above, so the tests go green.
5. Make the non-code edits, run one batched build and test run, then commit and push.

## Open questions (defaults taken)
- **Q1:** Remove the application-layer `LoadInvoiceItemsBatchAsync` path and `InvoiceItemRecord` as well? **Default: no.** Keep them in WP1 and log a follow-up, because they are governance-tested infrastructure outside the owner's scope.
- **Q2:** Regenerate `docs/schema/gateway-schema.graphql` from the live schema? **Default: no.** Remove only the demo entries.
- **Q3:** Remove the DevPortal "Finance Scope" button or relabel it? **Default: remove it.**

## Phase 3 binding conditions (security review, 2026-10-25)
**Verdict: APPROVED WITH CONDITIONS.** Removal narrows the attack surface; the `finance` resolver's own authentication check (`UNAUTHORIZED`) disappears with it, so `table` must carry equivalent coverage. No blocker.
- **RDF-S1 (defenses unchanged):** In `QueryDefenseStressTests` only the query text may change. Status codes, error codes (`QUERY_TOO_COMPLEX`, depth error), thresholds and the expected pass/fail outcome of each of the 8 rewritten tests stay identical. Do not raise `MaxAllowedComplexity`/depth, relax limits or use `AreQueryLimitsRelaxed` to make a rewritten test pass.
- **RDF-S2 (depth test must stay depth-limit based):** `MaxExecutionDepth_ExceedingDepthLimit6` must still fail on `AddMaxExecutionDepthRule` with a query that is genuinely deeper than the limit (nested fragments/inline fragments/aliased nesting on `table`/`Query`). A flat `table { jsonRows }` is not acceptable. Assert the same depth error code as before.
- **RDF-S3 (cost tests keep their meaning):** The excessive-limit, omitted-limit, fragment, inline-fragment and variable-default bypass tests must still fail due to cost (not due to an unknown field, auth or validation of a different rule), and the "within budget" tests must still succeed with HTTP 200 and rows/no errors. Assert the error code, not just a non-200 status. Omitted-limit test: the aliased selections must exceed the budget only through the default page size.
- **RDF-S4 (no false pass via unknown field):** Every rewritten query must be run once against the final code to prove the field/args (`domain`, `schema`, `name`, `first`) resolve. Fail-closed tests assert on the specific code, never on "any error".
- **RDF-S5 (removed fields fail closed):** `{ finance ...}`/`{ hr ...}` as Reader and as unauthenticated caller yield a GraphQL validation error (field not found), no `data` rows, no stack trace/exception text, and no HTTP 500. The error must not echo the schema SDL or suggest `table` internals beyond the standard "field does not exist". Also check with the trusted-documents-only mode that the removed fields are rejected before execution.
- **RDF-S6 (auth parity on `table`):** Keep or add one test that unauthenticated `table(...)` is rejected the same way the deleted `FinanceAndHrQuery_Unauthenticated_ThrowsUnauthorized` was (existing `table` auth test acceptable if it asserts the same `UNAUTHORIZED`/401 outcome); verify by grep before deleting, and name it in the PR. Likewise one existing integration test on the `table` path must still cover sensitive masking and consent/odd-batch governance end to end (the deleted `DataLoader_OddParentBatchEvaluation_WithSensitiveMasking_EndToEnd` and the 3 WalkingSkeleton tests); if none exists, do not delete the corresponding test, port it to `table` instead.
- **RDF-S7 (controls untouched):** `DomainScopeRewriteMiddleware`, `/graphql/{domain}` domain-scope enforcement (`ReadOnlyTokenTests` `/graphql/finance`), `CostAndQuotaMiddleware`, `QueryCostAnalyzerRule`, `TrustedDocumentsOnlyMiddleware` and `DbtHealthExecutionMiddleware` logic have zero diff apart from the one comment line. `IDemoDataSwitch`/`DemoDataSwitch.Resolve` semantics unchanged (default value, parsing, MCP/seed/lakehouse consumers); only the XML summary changes. Removing the `demoDataEnabled` parameter must not change any other registration order in `AddGatewayGraphQL` (middleware pipeline order stays).
- **RDF-S8 (no residual surface):** After the change, `grep` over `src/` finds no `InvoiceRecord`, `FinanceQuery`, `HrQuery`, `DemoQueryExtensions`, `InvoiceItemDataLoader`, and no leftover `AddTypeExtension` for them; the SDL contains no `InvoiceItemRecord` type (it is only an application-layer record). The DevPortal asset change removes only the anchor (no inline script/CSP change).
- **RDF-S9 (docs/k6):** The k6 rewrite uses `table(...first: $first)` within the cost budget (no limit increases, no relaxed-limits environment flag).

**Mandatory tests** (`[Trait("Category","Security")]`): `Schema_HasNoFinanceOrHrRootFields`, `FinanceOrHrRootSelection_FailsValidation` (unit, theory incl. unauthenticated and fragment-wrapped selections), `FinanceRootSelection_AsReader_ReturnsValidationErrorNotInternalError`, `FinanceRootSelection_Unauthenticated_NoRowsNoException` (integration), `MaxExecutionDepth_ExceedingDepthLimit6_FailsOnAstValidation` (kept, on `table`, asserts depth error), `QueryCostAnalyzer_*` (kept, on `table`, assert `QUERY_TOO_COMPLEX`), `Table_Unauthenticated_IsRejected` (or name of the existing equivalent), `Table_SensitiveMasking_EndToEnd` (or existing equivalent).

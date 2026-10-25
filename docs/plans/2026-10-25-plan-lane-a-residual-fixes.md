# Lane A residual fixes (Phase 2 plan)

Branch `fix/lane-a-residual-fixes` (base `origin/main` 8cdd91de). Owner: no GitHub issues, fix directly; no PRD beyond this plan (default taken). Code facts verified against the branch on 2026-10-25.

## File overlap
A1, A2, A3 and A4 touch disjoint files and can run in parallel. One soft overlap: A1 and A2 both run GraphQL integration tests; build and test them one after the other (disk budget).

## A1: legacy `table`/`finance` GraphQL path maps gateway exceptions to governed errors
Cause: `Query.GetTableAsync` / `GetInvoicesAsync` / `GetEmployeesAsync` (`src/Autheris.GraphQL/Types/QueryTypes.cs`) call `IGatewayExecutionService.ExecuteTableQueryAsync`. That method throws `GatewayInvalidQueryException` (`GatewayExecutionService.cs:169` offset, 393/414/535 unknown columns with the caller's text in the message) and `GatewayNotImplementedException` (227 no executor for the `DataSourceType`, 239, 372; Lakehouse/DeltaLake executors). No resolver catches them. `ErrorSanitizingFilter.OnError` maps neither type, so they surface as `INTERNAL_SERVER_ERROR`. The tree path (`CatalogGraphQlTypeModule.cs:682`) catches them itself and is out of scope.
1. `src/Autheris.Api/Middleware/ErrorSanitizingFilter.cs` `OnError`: add type mappings next to the #341 `WebSqlTableDeniedException` block, keyed on `error.Exception`. They never use `ex.Message` or `ex.ErrorCode`:
   - `GatewayPageRangeException` maps to `INVALID_QUERY` with `GatewayPageRangeException.FixedMessage`.
   - Other `GatewayInvalidQueryException` maps to `INVALID_QUERY` with the fixed "The query is invalid.".
   - `GatewayNotImplementedException` maps to a new code `NOT_IMPLEMENTED` with the fixed "The requested feature or data source capability is not implemented." (the REST text in `GatewayExceptionHandler.cs:157`).
   Add `NOT_IMPLEMENTED` to `WhitelistedSafeCodes` and `FixedClientMessages` (C341-5). Do not add `INVALID_QUERY` to `FixedClientMessages`, because that would change the tree path texts. Log both types at Information, not Error: they are client or capability outcomes.
2. Tests:
   - Unit `tests/Autheris.Tests.Unit/GraphQL/Issue341ErrorMappingTests.cs` (extend):
     - `GatewayInvalidQueryException_MapsToInvalidQuery_FixedMessage_NoInputEcho` (the message carries a canary that must not appear)
     - `GatewayPageRangeException_MapsToInvalidQuery_PageRangeFixedMessage`
     - `GatewayNotImplementedException_MapsToNotImplemented_FixedMessage`
     - `NotImplementedCode_IsWhitelisted_WithFixedMessage`
     - `TreePathGraphQlException_InvalidQuery_MessageUnchanged`
   - Integration `tests/Autheris.Tests.Integration/Issue341GraphQlInternalErrorTests.cs`:
     - `LegacyTable_SourceTypeWithoutExecutor_IsNotImplemented_NotInternalError` (catalog entry whose `DataSourceType` has no executor, queried via `table(...)`)
   - Mark the tests `[Trait("Category","Security")]`.
3. Acceptance: no `INTERNAL_SERVER_ERROR` for either type on any GraphQL path, and no caller text in `message`.

## A2: flaky GraphQL parity fixtures (schema not yet swapped in)
Cause (test readiness, found in code):
- The fixtures (`RowFilterChannelParityTests.Fixture.InitializeAsync:376-377`, `McpDatasetToolsEndToEndTests:111`) seed the catalog and then call `CatalogGraphQlTypeModule.CheckForCatalogChangesAsync` by reflection. That method only raises `TypesChanged`.
- HotChocolate 16.6.8 rebuilds the executor asynchronously and serves the old one until the swap (comment at `CatalogSchemaRefreshRGql12Tests.cs:80`).
- The fixtures hide this with a retry loop on `body.Contains("does not exist")` (`RowFilterChannelParityTests.cs:413`, `TypedAggregationParityEndToEndTests.cs:72`, `RequiredFilterColumnsParityIntegrationTests.cs:110`, `McpDatasetToolsEndToEndTests.cs:167`). `ErrorSanitizingFilter` now replaces every validation message with the fixed "The GraphQL request is invalid." (`GRAPHQL_VALIDATION_FAILED`), so that predicate never matches. The first request under CI load hits the stale executor.
- The schema is not shared across hosts: the module is a per-host singleton with a 60 s timer (`CatalogSchemaRefreshSeconds`).

Two product races make it worse, and the developer confirms each with a Red test first:
- (P1) `CheckForCatalogChangesAsync` returns `false` when another check is running (`Interlocked` guard, line 114). A caller that starts after a completed catalog write can therefore be dropped while a concurrent check that read the earlier state reports "no change".
- (P2) The start-up warm-up (`GraphQlExecutorGate`) can run `CreateTypesAsync` on a partly seeded catalog while the fixture's check raises `TypesChanged`. If HotChocolate ignores an eviction during executor creation, the stale schema stays until the next timer tick.

Changes:
1. P1, `src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs`: replace the drop guard with a `SemaphoreSlim(1,1)`. A second caller waits and then runs its own check, so it always sees writes that completed before it was called.
   - Test `tests/Autheris.Tests.Unit/GraphQL/CatalogSchemaRefreshRaceTests.cs`: `CheckForCatalogChanges_WhileAnotherCheckRuns_ObservesTheLaterWrite`. A gated `ITableMetadataRepository` (TaskCompletionSource) holds the first check after its read.
2. P2, test-first in the same file: `CatalogChange_DuringInFlightExecutorBuild_ServedSchemaConvergesToNewCatalog`. It uses a real executor, as `CatalogSchemaRefreshRGql12Tests` does, and gates `CreateTypesAsync`.
   - If the test is Red, the module counts a change generation. A check that fires during a build sets `_recheckAfterBuild`. A subscriber to `IRequestExecutorEvents` (Created, the pattern in `GraphQlExecutorGate.cs:282`) re-runs the check once the executor is registered.
   - If the test is Green on the current code, keep the test and skip the change.
3. Fixture readiness:
   - Add `InternalsVisibleTo Autheris.Tests.Integration` to `src/Autheris.GraphQL/Autheris.GraphQL.csproj`; the reflection calls go away.
   - New helper `tests/Autheris.Tests.Integration/TestSupport/GraphQlSchemaReadiness.cs` with `AwaitCatalogSchemaAsync(IServiceProvider, IReadOnlyCollection<string> requiredQueryFields, TimeSpan timeout)`. It subscribes to `IRequestExecutorEvents` before calling `CheckForCatalogChangesAsync`, then checks `IRequestExecutorProvider.GetExecutorAsync()` and its `Schema.QueryType` fields. It waits for the next Created event until every required field (including `*_aggregate` fields where used) is present. On timeout (30 s) it fails the fixture and names the missing fields. There is no request retry and no `Task.Delay`.
   - Use the helper in `RowFilterChannelParityTests.Fixture` (new virtual `RequiredGraphQlFields`; the derived fixtures in TypedAggregation, VirtualFilter, RowFilterKeySet, CallerAttribute, Issue143RestrictedScope and RequiredFilterColumns override it when they add tables), and in `McpDatasetToolsEndToEndTests`.
   - Delete the four `does not exist` / `GRAPHQL_VALIDATION_FAILED` retry loops.
   - The developer greps `CheckForCatalogChangesAsync"` and `does not exist", StringComparison` under `tests/` for other users, `SqlVirtualFilterChannelParityTests` included.
4. Acceptance: no GraphQL request retry or sleep remains in these fixtures, the race tests are deterministic (no timing), and the four classes pass 3 consecutive full integration runs.

## A3: INF-13, CDC resync `discardUnreadable` fails closed on transient key failures
`docs/plans/2026-10-22-plan-security-inf.md` has no INF-13 decision; this plan follows the `security.txt:1933` fix hint (default taken).
1. `src/Autheris.Application/Streaming/Interfaces/ICdcKeyEncryptionKeyProvider.cs`:
   - Add `enum CdcKekLookup { Available, Unknown, Unavailable }` and `CdcKekLookup ResolveKek(string kekId, out ReadOnlyMemory<byte> key)`.
   - `TryGetKek` becomes a default member, `ResolveKek(...) == Available`, so every existing call site keeps failing closed.
   - Update `tests/Autheris.Tests.Unit/CdcStream/CdcStreamFakes.cs` `FakeKekProvider`.
2. `src/Autheris.Infrastructure/Security/KeyVaultCdcKekProvider.cs`:
   - `Unknown` only when the `kekId` is not in `CdcStream.Encryption.Keks` (and is not the Development ephemeral id).
   - `Unavailable` when the secret provider throws, the secret is too short, the reference is reserved, or the id is empty.
3. `src/Autheris.Application/Streaming/Crypto/CdcStreamKeyring.cs`: new `ClassifyDekAsync(CdcStreamId, CdcWrappedKey, ct)` that returns `Readable`, `Unreadable` or `Indeterminate`.
   - `Unreadable` only when (a) the lookup is `Unknown` and the active KEK is `Available`, (b) the wrapped blob is malformed, or (c) AES-GCM authentication fails under an `Available` KEK.
   - Everything else is `Indeterminate`.
4. `src/Autheris.Application/Streaming/Services/CdcStreamAdminService.cs` `ResyncAsync` with discard:
   - Requires the active KEK to be `Available`.
   - Wraps `keyStore.GetKeysAsync` failures (except OperationCanceledException).
   - Returns the new `CdcResyncStatus.KeyUnavailable = 4` on any `Indeterminate` DEK, before any discard and without restarting.
   - Discards only `Unreadable` DEKs. The non-discard path is unchanged.
5. `src/Autheris.Api/Endpoints/CdcStreamAdminEndpoints.cs`: `KeyUnavailable` answers 503 `{ error: "CDC_KEY_UNAVAILABLE" }` (`CdcActivationCodes.KeyUnavailable`) and writes the resync audit with `refused = "CDC_KEY_UNAVAILABLE"`, `discardedEvents = 0`, `restarted = false`.
6. Tests in `tests/Autheris.Tests.Unit/CdcStream/` (Security trait):
   - `Resync_Discard_SecretProviderThrows_AbortsKeyUnavailable_NoDiscard_NoRestart`
   - `Resync_Discard_KeyStoreReadFails_AbortsKeyUnavailable_NoDiscard`
   - `Resync_Discard_ActiveKekUnresolvable_AbortsKeyUnavailable_NoDiscard`
   - `Resync_Discard_MixedPermanentAndTransient_AbortsWholeResync`
   - `Resync_Discard_KekIdRemovedFromConfig_DiscardsOnlyThatDek`
   - `Resync_Discard_AesGcmAuthFailure_DiscardsDek`
   - `Resync_WithoutDiscard_Unchanged`
   - `KeyVaultCdcKekProviderTests.ResolveKek_SecretProviderThrows_Unavailable`
   - `ResolveKek_UnknownId_Unknown`
   - `ResolveKek_ShortSecret_Unavailable`
   - `TryGetKek_FalseForUnknownAndUnavailable`
   - `CdcStreamAdminEndpointsTests.Resync_KeyUnavailable_Returns503StableCode_AuditsRefusal`
7. Acceptance: a transient key or store failure never deletes events, images or keys, and the resync fails with `CDC_KEY_UNAVAILABLE`. Add a "RESOLVED" line for INF-13 in `security.txt` after Phase 5.

## A4: SIM-GAP-101, maskable columns are nullable in the OData OpenAPI document
Verified:
- GraphQL already declares every catalog column nullable (`CatalogSchemaModel.cs:276` passes `IsNullable: true`).
- CSDL already omits `Nullable` for non-key properties, and the CSDL default is `true` (`ODataCsdlGenerator.cs:58`).
- Only `DynamicOpenApiGenerator.MapColumnToJsonSchema` (`src/Autheris.Application/OData/Services/DynamicOpenApiGenerator.cs:377`) emits a single `type`. The document is `openapi: 3.1.0`.

Changes:
1. `BuildEntitySchemaNode` emits `"type": ["<t>", "null"]` (JSON Schema 2020-12) for every non-key column and keeps `format`. Columns in `keyView.PrimaryKeyColumns` keep a single type and stay `required`, matching CSDL `Nullable="false"`. This covers monolithic, modular and per-entity schemas and YAML output; check that `ConvertJsonNodeToYaml` renders arrays inline.
2. Recommendation (fail closed, no leak):
   - Nullability is caller-independent and does not look at masking rules, because consent `ColumnRules` (`Mask`) and `MaskingRule` (`NULLIFY`) can null any column for some caller.
   - A per-caller schema would reveal which columns are masked for that caller, and the document is cached per structure version, not per caller.
3. Remove the `SIM-GAP-101` entry from `tests/Autheris.Tests.Simulation/ApiFuzz/api-known-gaps.json` (leave `"gaps": []`) and delete `tests/Autheris.Tests.Simulation/KnownGaps/drafts/SIM-GAP-101.md`.
4. Tests in `tests/Autheris.Tests.Unit/DynamicOpenApiGeneratorTests.cs`:
   - `EntitySchema_NonKeyColumn_IsTypeArrayWithNull`
   - `EntitySchema_KeyColumn_SingleTypeAndRequired`
   - `EntitySchema_NullableDateTime_KeepsFormat`
   - `EntitySchema_NullabilityIndependentOfMaskingRules` (the same table with and without a NULLIFY rule gives an identical schema)
   - `OpenApiYaml_RendersNullableTypeArray`
   Update existing assertions on `"type":"string"` in `DynamicOpenApiGeneratorTests` and `OmnichannelDocumentationTests` where they hit non-key columns.
5. Acceptance: a Schemathesis read run against `sim_accounts` (seed 20261024) reports no ST108 for masked columns, and the known-gaps list is empty (strict xfail).

## Open questions (defaults taken)
1. A1 code for not-implemented: new whitelisted `NOT_IMPLEMENTED` (default). The alternative, reusing `UNAVAILABLE`, would wrongly suggest a retry.
2. A3: a KEK id removed from configuration while the active KEK resolves counts as permanent, because that is the documented KEK-loss signal (SEC-CDC99-18; `confirmStreamId` still required).
3. A4: primary-key columns stay non-null. If masking can ever null a key, file a new sim gap rather than weaken CSDL.
4. A2/P2: implement only if its Red test fails on the current HotChocolate 16.6.8 behaviour.

# Plan: Issue #282 - Register SQL data source tables via dbt sync

PRD: [2026-10-23-prd-issue-282-catalog-register-sql-tables.md](2026-10-23-prd-issue-282-catalog-register-sql-tables.md). Phase 2 (csharp-architect).

**Schema impact: none.** No governance DDL or migration change. New entries are written through the existing `ITableMetadataRepository.UpsertTableMetadataAsync` (same table/column rows the OpenMetadata and catalog sync already create).

## Open questions
- OQ-1: live-introspection admin endpoint deferred to a follow-up issue (default taken).
- OQ-2: `CreateMissingTables` defaults to `true` (default taken).
- OQ-3: exact case-insensitive connection-name match plus optional `DatabaseMappings`; unresolved means skip (default taken).
- OQ-4: no automatic mask or consent proposals for new tables (default taken).

## Files
- `src/Autheris.Domain/Options/Integrations/DataCatalogOptions.cs` (`DbtOptions`): add `CreateMissingTables` (true), `ActivateNewTables` (false), `DatabaseMappings` (`Dictionary<string,string>`, OrdinalIgnoreCase). Bound as `Gateway:Dbt:*` through `GatewayOptions.Dbt`.
- `src/Autheris.Domain/Model/DbtMetadataModels.cs`: `DbtSyncResult` gets the optional members `CreatedTables` (`IReadOnlyList<string>`) and `SkippedItems` (`IReadOnlyList<DbtSyncSkippedItem>`), following the `RejectedModels` init pattern. Add `record DbtSyncSkippedItem(string Name, string Kind, string Reason)` and `static class DbtSyncSkipReasons` (string constants `NoDataSourceMapping`, `DataSourceNotSql`, `InvalidIdentifier`, `CreationDisabled`, `Ineligible`, `Duplicate`, `CreateFailed`).
- New `src/Autheris.Extensions/Dbt/DbtCatalogTableFactory.cs` (internal static, pure): `TryBuild(DbtModelDefinition, GatewayOptions, out TableMetadata?, out string? reason)`.
- `src/Autheris.Extensions/Dbt/DbtMetadataIngestionService.cs` (`IngestManifestStreamAsync`, model loop at step 3): add the creation branch. The constructor stays the same, because `_gatewayOptions` already carries `DataSources` and `Dbt`.
- `src/Autheris.Api/Endpoints/DbtEndpoints.cs` (`/api/extensions/dbt/sync`): add audit details. Authorization stays as it is.

## Design decisions
1. **Data source resolution** (`DbtCatalogTableFactory`): the connection name is `DatabaseMappings[model.Database]` when that key exists, otherwise `model.Database`. Look it up in `DataSources.Connections`, which is already OrdinalIgnoreCase. If the lookup misses, the reason is `NoDataSourceMapping`. There is never a fallback or default source.
2. **SQL check**: `SourceType = DatabaseDialectExtensions.ResolveCatalogSourceType(connection.Provider, null, out supported)`. When `supported` is false, the reason is `DataSourceNotSql`. No connection is opened and no SQL is executed.
3. **Identifier validation**: schema, table and every column name must match `^[A-Za-z_][A-Za-z0-9_]*$` (source-generated regex, max 128 characters). `DbtIdentifierSanitizer.IsValidIdentifier` only rejects control characters, so it is not enough for AC-7. If any single name fails, the whole table is skipped with `InvalidIdentifier` (fail-closed, never a partial table).
4. **Eligibility**: `Materialization == "ephemeral"` gives `Ineligible`. Disabled nodes never reach `nodes` in a dbt manifest; the parser only reads `model.`/`seed.` nodes and `sources`. Kind is `source` when `Materialization == "source"`, otherwise `model`. A second manifest item with the same `TableIdentifier` in one run is skipped with `Duplicate` (HashSet per run).
5. **Entity shape**:
   - `Table { Id = Guid.NewGuid(), SourceType, SourceName = connection name, SchemaName, TableName, DisplayName = model.Name, Description, Sensitivity = "NORMAL", RequiresFourEyes = false, IsActive = Dbt.ActivateNewTables, DataSourceType = Sql }`.
   - `Identifier = model.ToTableIdentifier()`, so the domain is the dbt database and `dbt/governance` resolves the table the same way.
   - Columns come from the manifest. A missing `data_type` is stored as `"string"`, never guessed (default taken).
   - No masks, consents, RLS or keys at creation. Those come from the existing update path and `dbt/governance`.
6. **Flow in the model loop**:
   - `GetTableMetadataAsync` now also runs in dry-run mode (read only).
   - `TableMetadataUnavailableException` keeps SEC-94-01: no creation.
   - If the entry is `null`: when `CreateMissingTables` is false, skip with `CreationDisabled`; otherwise run `TryBuild`.
   - In dry run, a successful build is only added to `CreatedTables`.
   - Otherwise `UpsertTableMetadataAsync(created)` runs. The created metadata then becomes `existingMeta` for the unchanged step-3 update path (descriptions, keys, tags, samples, ReBAC seed). One sync call is enough (REQ-282-9), and relations import afterwards sees the new tables.
   - An upsert exception becomes `CreateFailed` and the loop continues (REQ-282-10).
   - Each skip logs one warning with `ForLog(name)` and the reason.
   - Tenant-rejected models stay in `RejectedModels` and are not repeated in `SkippedItems`.
7. **Idempotency**: an existing entry never enters the creation branch, so `IsActive`, sensitivity, masks and consents keep the existing ratchet and copy semantics (REQ-282-6).
8. **Audit** (`DbtEndpoints` sync handler): after the call, write `created_tables_count`, `created_tables` (qualified names, capped at 100, then `created_tables_truncated = true`), `skipped_items_count` and `dry_run` to `AuditContext.AdditionalDetails`. This is the same pattern as the semantic manifest handler at line 451. No column data and no secrets.
9. **Authorization**: unchanged. The caller must be `IsGlobalGovernanceAdmin` or have the `DbtAdmin` role; anyone else gets 403 before the service runs (AC-6). The webhook-triggered sync path, if present, uses the same service behavior with the system caller.

## Test list
Unit tests:
- New file `tests/Autheris.Tests.Unit/Dbt/DbtSyncTableCreationTests.cs` (fakes as in `DbtSourcesManifestIngestionTests`):
  - `Sync_EmptyCatalog_TwoModelsOneSource_CreatesThreeInactiveTables` (AC-1)
  - `Sync_ActivateNewTablesTrue_CreatesActiveTables` (AC-2)
  - `Sync_UnmappedDatabase_SkipsWithNoDataSourceMapping` (AC-3)
  - `Sync_DatabaseMappingsEntry_ResolvesConnection`
  - `Sync_UnsupportedProvider_SkipsWithDataSourceNotSql`
  - `Sync_SecondRun_CreatesNothingAndKeepsIsActiveAndSensitivity` (AC-4)
  - `Sync_TableNameWithQuoteOrSemicolon_SkipsWithInvalidIdentifier` and `Sync_InvalidColumnName_SkipsWholeTable` (AC-7)
  - `Sync_DryRun_ReportsListsAndWritesNothing` (AC-8)
  - `Sync_CreateMissingTablesFalse_SkipsWithCreationDisabled`
  - `Sync_EphemeralModel_SkipsWithIneligible`
  - `Sync_DuplicateIdentifier_SkipsSecondWithDuplicate`
  - `Sync_UpsertThrows_RecordsCreateFailedAndContinues`
  - `Sync_MissingDataType_StoresString`
  - `Sync_CreatedTable_GetsManifestDescriptionsAndTags`
  - `Sync_MetadataUnavailable_NeverCreates`
- `tests/Autheris.Tests.Unit/Api/DbtSyncEndpointCreationTests.cs`:
  - `Sync_NonAdmin_Returns403AndServiceNotCalled` (AC-6)
  - `Sync_AuditDetailsContainCreatedNamesWithoutSecrets` (AC-9, unit level)

Integration test (WP-2):
- `tests/Autheris.Tests.Integration/DbtSyncCatalogRegistrationPostgreSqlTests.cs` with `PostgreSqlContainerFixture`:
  - `Sequence_SyncGovernanceConsents_YieldsQueryableTablesWithMasksAndConsents` (AC-5): empty PostgreSQL governance DB, `POST dbt/sync`, `dbt/governance`, `/api/v1/governance/consents/apply`, activate, then query.
  - `Sync_AuditEventContainsCreatedTables` (AC-9).

## Work packages
- **WP-1** (code and unit tests): options, DTO, factory, service branch, endpoint audit, and all unit tests above. Done when the unit suite is green.
- **WP-2** (integration test): the PostgreSQL Testcontainers test above. It is only compiled locally (`dotnet build`) and runs on CI. It depends on WP-1.

## Phase 3 security conditions
Reviewer: csharp-security-expert, 2026-10-23. Verified in code: `Table.Sensitivity` default `NORMAL` (rank 2, ratchet forbids lowering via manifest, `DbtManifestRatchet.cs:128-148`); `IsTenantAllowed` gates models by `TableIdentifier.Domain` (`DbtMetadataIngestionService.cs:241`); DbtAdmin/global-admin gate at `DbtEndpoints.cs:30,226`.

**Verdict: APPROVED WITH CONDITIONS.** No existing control is weakened: creation only happens for `null` entries, never overwrites governance fields, and new tables are inactive and unmasked-by-nobody-yet (invisible until activated).

### Binding conditions
- **SEC-282-1 (identifiers, decision: keep strict regex).** Schema, table and column names must match `^[A-Za-z_][A-Za-z0-9_]*$` with explicit ASCII classes (no `\w`/`\d`, no `RegexOptions.CultureInvariant`-only reliance; use `[0-9]`), anchored with `\z` (not `$`, which admits a trailing `\n`), max 128 chars, `RegexOptions.None`, source-generated with a match timeout irrelevant (linear). Mixed case is allowed and preserved as given. Quoted identifiers with spaces, hyphens, dots or non-ASCII are rejected (`InvalidIdentifier`); this is accepted, as such tables stay registrable manually. One bad column skips the whole table (no partial table). `model.Database`, `Name` and `Alias` are validated too when they form `SchemaName`/`TableName`/`Identifier`. Schema-qualified names must arrive as separate fields; a dot in any single field is invalid.
- **SEC-282-2 (default sensitivity).** Keep `NORMAL` (rank 2, equal to existing creation paths) because the table is inactive and carries no consents. Because the ratchet blocks later lowering, do not choose a stricter default. The step-3 update path must still apply a manifest-declared sensitivity in the same sync (raise only). When `ActivateNewTables=true`, log one startup/sync warning that new tables go live without masks; default stays `false` and must not be changed by `appsettings` defaults in any profile.
- **SEC-282-3 (fail-closed mapping).** Resolution is exactly `DatabaseMappings[model.Database]` else `model.Database`, matched against `DataSources.Connections`; miss, empty/whitespace key, or unsupported provider gives skip. No fallback, no default or first connection, no wildcard mapping key. A mapping value pointing to a connection that does not exist is a skip, never a created entry with a dangling `SourceName`. `SourceName` is always the configured connection key (canonical casing from the configuration), not the manifest string.
- **SEC-282-4 (no SQL, no connection).** Creation never opens a connection, runs introspection or interpolates manifest text into SQL. The catalog row is data only.
- **SEC-282-5 (no overwrite).** Creation branch only when `GetTableMetadataAsync` returns `null`. `TableMetadataUnavailableException` or any read error means no creation (SEC-94-01). Race: if `UpsertTableMetadataAsync` hits an existing row between read and write, it must not replace `IsActive`/sensitivity/masks/consents; use create-if-absent semantics or re-read and treat as existing. Tested with a fake that returns `null` then a populated entry.
- **SEC-282-6 (tenant).** Tenant check (`IsTenantAllowed`) runs before the creation branch; created identifiers must equal `model.ToTableIdentifier()` with the caller's tenant domain. A model for another tenant is rejected, never created. Duplicate detection key includes tenant domain.
- **SEC-282-7 (dry run).** Dry run performs only reads (`GetTableMetadataAsync`); no `UpsertTableMetadataAsync`, no key/tag/ReBAC seed, no relations write, no audit-side mutation beyond the response. Test asserts zero write calls on every repository fake.
- **SEC-282-8 (authorization and abuse bounds).** DbtAdmin creating catalog rows is accepted: the rows are inactive, unmasked-by-default is harmless while inactive, and activation stays with existing governance endpoints. `ActivateNewTables` is configuration only, never a request parameter. Bound per run: max 5000 created tables and max 1000 columns per table (excess skips with `Ineligible`), so a hostile manifest cannot exhaust the governance DB. `SkippedItems` capped at 500 in the response with a truncated flag.
- **SEC-282-9 (audit and logs).** Audit details (`created_tables_count`, `created_tables` capped 100 + truncated flag, `skipped_items_count`, `dry_run`) are written even when the run partially fails (try/finally) and also on dry run. Skip reasons use the fixed `DbtSyncSkipReasons` constants; manifest-derived names in logs and audit pass through `ForLog`/sanitizer (no control characters, truncated to 128). No column names, descriptions, sample values, connection strings or mapping targets in audit or response (connection name only).
- **SEC-282-10 (webhook/system path).** The system caller path must apply the same factory rules and tenant check; creation via an unauthenticated webhook is only allowed if that path already requires its existing signature/secret check; verify and note in the PR.

### Mandatory security tests (`[Trait("Category","Security")]`)
- `Sync_TableNameWithNewlineSuffix_SkipsWithInvalidIdentifier` (`\z` anchoring)
- `Sync_UnicodeDigitOrLetterInIdentifier_SkipsWithInvalidIdentifier`
- `Sync_QuotedIdentifierWithSpaceOrDot_SkipsWithInvalidIdentifier`
- `Sync_InvalidColumnName_SkipsWholeTable`
- `Sync_MappingTargetMissingFromConnections_SkipsAndCreatesNothing`
- `Sync_UnmappedDatabase_NoFallbackToDefaultConnection`
- `Sync_NewTable_IsInactiveNormalAndHasNoMasksOrConsents`
- `Sync_ExistingEntry_IsActiveSensitivityMasksConsentsUnchanged`
- `Sync_UpsertRaceExistingRowAppears_DoesNotOverwriteGovernance`
- `Sync_MetadataUnavailable_NeverCreates`
- `Sync_OtherTenantModel_RejectedAndNotCreated`
- `Sync_DryRun_PerformsNoRepositoryWrites`
- `Sync_ExceedsTableOrColumnCap_SkipsExcess`
- `Sync_AuditDetailsOnPartialFailure_ContainCreatedNamesWithoutColumnsOrSecrets`
- `Sync_LogsAndAuditSanitizeControlCharsInNames`
- `Sync_NonAdmin_Returns403AndServiceNotCalled`

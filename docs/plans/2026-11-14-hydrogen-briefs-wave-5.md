# Phase 4 Hydrogen Developer Briefs — Wave 5

- **Date:** 2026-11-14
- **Baseline:** `origin/main` @ `2b2876bd` (sequenced immediately after Wave 4 `docs/plans/2026-11-14-hydrogen-briefs-wave-4.md`).
- **Global Constraints for All Hydrogen Workers:**
  1. **English Only:** All identifiers, comments, error messages, logs, and commit messages MUST be in English.
  2. **Constrained Local Build & Filtered Test Execution Only:**
     - Always set `export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget`.
     - Build only the target test/source `.csproj` with `-m:1 -nodeReuse:false` (NEVER run `dotnet build Autheris.sln`).
     - Run `dotnet test` ONLY with `--no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s` and a tight `--filter "<Filter>"` on both `Autheris.Tests.Unit` (`-p:AutherisIncludeTestAuth=true`) and `Autheris.Tests.Architecture`.
     - Compile-check `Autheris.Tests.Integration` with `dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --no-dependencies -m:1 -nodeReuse:false` (NEVER execute integration tests or unfiltered test suites locally).
  3. **Zero `file`+`://` Links in Markdown:** Never write literal `file`+`://` URI strings in any `.md` file (`DocsIntegrityTests.No_absolute_file_links`).
  4. **Clean `bin/obj` After Push:** Run `find src tests -maxdepth 2 \( -name bin -o -name obj \) -exec rm -rf {} +` after pushing your branch to keep `/work` disk usage low.

---

## Wave 5 Conflict & DDL Serialization Matrix

| Slot | Work Package | Branch / Worktree | DDL Step | Shared-File Serialization Rules |
| :--- | :--- | :--- | :--- | :--- |
| **W5-1** | **`WP-OI-SCALE-4 PR 2 (#675)`** (`POLICY_EPOCHS.change_version` + `GetTablesChangedSinceAsync` + `CatalogSnapshot.WithDelta`) | `feat/oi-scale-675-pr2-delta` (`/work/wt/oi-scale-675-pr2-delta`) | **`0015_policy_epochs_change_version`** *(Single DDL slot in Wave 5; starts after Wave 3 `#675 PR 1`, Wave 4 `0014_consent_api`, and Wave 4 `#673` merge)* | Touches `src/Autheris.Infrastructure/Persistence/Migrations/Steps/{postgresql,sqlserver}/0015_policy_epochs_change_version*`, `src/Autheris.Infrastructure/Persistence/Migrations/{PostgreSqlBaselineSchema.cs,SqlServerBaselineSchema.cs}`, `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Catalog*.cs`, `src/Autheris.Application/Catalog/{CatalogSnapshotProvider.cs,CatalogSnapshot.cs}`. Disjoint from W5-2, W5-3, W5-4, W5-5a. |
| **W5-2** | **`WP-OI-CAT-4 (#671)`** (Declarative Consent Prune Scoped to `DECLARATIVE`) | `feat/oi-cat-671-declarative-prune` (`/work/wt/oi-cat-671-declarative-prune`) | None *(Starts after Wave 4 Brief 1 `WP-SIMPL-47 PR 1` `CONSENTS.grant_source` merges)* | Touches `src/Autheris.Application/Governance/Services/DeclarativeConsentApplyService.cs`, `src/Autheris.Application/Governance/Interfaces/IDeclarativeConsentApplyService.cs`, `src/Autheris.Domain/Model/DeclarativeConsentModels.cs`, `src/Autheris.Api/Endpoints/GovernanceEndpoints.Catalog.cs`, `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Consent*.cs`. Disjoint from W5-1, W5-3, W5-4, W5-5. |
| **W5-3** | **`WP-OI-SQL-479-B`** (`#479` Dialect Semantics PR B: `date_add('quarter', ...)`, Oracle Month-End `ADD_MONTHS`, `Concat` Parenthesization) | `feat/oi-sql-479b-date-concat` (`/work/wt/oi-sql-479b-date-concat`) | None *(Starts after Wave 4 Brief 3 `WP-OI-SQL-479-A` merges on `TrinoSqlEngine`)* | Touches `src/TrinoSqlEngine/Ast/Generators/{PostgreSqlDialectGenerator.cs,OracleDialectGenerator.cs,SqlDialectGeneratorBase.Functions.cs,SqlDialectGeneratorBase.Expressions.cs}`. Note: do NOT run in parallel with `WP-SIMPL-31`. Disjoint from W5-1, W5-2, W5-4, W5-5. |
| **W5-4** | **`WP-SIMPL-45 PR 5`** (Governed Write Execution: Intent/Outcome Audit, Idempotency, Write Budgets, `WriteConnection` & Required-Privileges Report) | `feat/simpl-45-pr5-write-exec` (`/work/wt/simpl-45-pr5-write-exec`) | None *(Starts after Wave 3 `WP-SIMPL-45 PR 4A & PR 4B` merge)* | Touches `src/Autheris.Application/Sql/Services/GovernedSqlExecutor.cs`, `src/TrinoSqlEngine/Ast/Emit/CheckedDmlExecutor.cs`, `src/Autheris.Domain/Options/Protocols/RestApiOptions.cs`, `src/Autheris.Api/Endpoints/GovernanceEndpoints.Catalog.cs`. Disjoint from W5-1, W5-2, W5-3, W5-5. |
| **W5-5a** | **`WP-OI-SCALE-5 (#676 PR 1–3)`** (Incremental BM25 + Vector Search Index & Embedding Cache) | `feat/oi-scale-676-incremental-search` (`/work/wt/oi-scale-676-incremental-search`) | None *(Runs in parallel with W5-1..W5-4; PR 3 wires `GetTablesChangedSinceAsync` once W5-1 lands)* | Touches `src/Autheris.Application/Catalog/Search/{CatalogSearchEngine.cs,CatalogSearchWarmupService.cs,Bm25SearchIndex.cs,VectorSearchIndex.cs}`. |
| **W5-5b** | **`WP-OI-SCALE-6 (#677 PR 1)`** (Stale-Only `GetAllTablesAsync` & Internal Unfiltered `GetTablesAsync`/`QueryTablesAsync`) | `feat/oi-scale-677-pr1-paged-reads` (`/work/wt/oi-scale-677-pr1-paged-reads`) | None *(Starts after W5-1 `#675 PR 2` merges on `GovernanceRepository.Catalog.cs`)* | Touches `src/Autheris.Application/Common/Interfaces/IGovernanceRepository.cs`, `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Catalog.cs`, `src/Autheris.Infrastructure/Persistence/GovernanceRepository.CatalogSql.cs`, `src/Autheris.Application/Catalog/Services/CatalogDiscoveryService.cs`, `src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs`. |

---

## Brief 1 — `WP-OI-SCALE-4 PR 2 (#675)`: `POLICY_EPOCHS.change_version` (`0015_policy_epochs_change_version`), `GetTablesChangedSinceAsync` & `CatalogSnapshot.WithDelta`

- **Worktree:** `/work/wt/oi-scale-675-pr2-delta`
- **Branch:** `feat/oi-scale-675-pr2-delta` (branch from `origin/main` after Wave 3 `#675 PR 1`, Wave 4 `0014_consent_api`, and Wave 4 `#673` merge)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-scale.md` (`WP-OI-SCALE-4` / `#675` PR 2, lines 75–116)
  - `docs/plans/2026-11-13-sec-issues-group-7.md` (`SEC-OI-675-1..6`, lines 66–81)

### Mandatory Security Conditions (`SEC-OI-675-1..6`)
- **`SEC-OI-675-1` (Every Catalog-Mutating Writer Updates `change_version` Atomically):** Every repository path that mutates table metadata, column metadata, masking rules, visibility (`TABLE_VISIBILITY`, `DiscoveryHidden`), active state (`is_active`), data owners, or table key definitions MUST update the affected table's `POLICY_EPOCHS.change_version` in the **same transaction** as the catalog version increment. Enforce this with an architecture/unit test (`CatalogMutatingWritersBumpChangeVersionTests`) that enumerates all catalog-mutating methods on `IGovernanceRepository` / `ITableMetadataRepository` and verifies each one advances `change_version`.
- **`SEC-OI-675-2` (High-Watermark Read Before Delta Query):** `GetTablesChangedSinceAsync(long sinceVersion, CancellationToken ct)` MUST read the current `CATALOG_STATE` version (`newVersion`) **first**, then query `POLICY_EPOCHS` for `scope = 'TABLE'` and `change_version > @sinceVersion` (`ORDER BY scope_key`), and return `(long NewVersion, IReadOnlyList<TableIdentifier> ChangedOrRemovedTables)`. A concurrent write that commits with `change_version > newVersion` is either included now or picked up on the next delta pass (`> newVersion`) — never skipped.
- **`SEC-OI-675-3` (Missing Row = Deletion / Inactive Removal):** After calling `GetTablesChangedSinceAsync`, `CatalogSnapshotProvider` loads the current rows for the returned `TableIdentifier`s (`GetTablesAsync(changedIds)`). Any identifier in `ChangedOrRemovedTables` that is missing from the returned table rows or has `IsActive == false` MUST be removed from the new `CatalogSnapshot` (`WithDelta(upserted, removedIds, newVersion)`).
- **`SEC-OI-675-4` (Fallback to Full Reload on Large Delta or Error):** If `ChangedOrRemovedTables.Count` exceeds **20%** of the current snapshot size (or `> 500` when snapshot is small), or if `_lastLoadedVersion == 0`, or if `_dirtyFlag.IsDirty` is set (`#672`), or if any delta step throws, `CatalogSnapshotProvider` MUST fall back to a full reload (`GetAllTablesAsync`).
- **`SEC-OI-675-5` (Copy-On-Write Immutable Snapshot):** `CatalogSnapshot.WithDelta` MUST construct a new immutable dictionary/lookup snapshot and publish it via a single atomic reference swap (`Interlocked.Exchange` / `Volatile.Write`); in-flight readers holding the previous `CatalogSnapshot` reference must never observe partial mutation.
- **`SEC-OI-675-6` (Migration `0015_policy_epochs_change_version` Backfill & Index):** Add `change_version BIGINT NOT NULL DEFAULT 1` to `POLICY_EPOCHS` in both PostgreSQL and SQL Server, backfill existing rows with the current `(SELECT COALESCE(MAX(version), 1) FROM CATALOG_STATE)` (NEVER `0` or `NULL`), and create index `IX_POLICY_EPOCHS_SCOPE_CHANGE_VERSION (scope, change_version)`.

### Exact Changes to Make
1. **Migration Step `0015_policy_epochs_change_version` & Baseline Schemas:**
   - Create `src/Autheris.Infrastructure/Persistence/Migrations/Steps/postgresql/0015_policy_epochs_change_version.sql` (+ `.shape.json`) and `src/Autheris.Infrastructure/Persistence/Migrations/Steps/sqlserver/0015_policy_epochs_change_version.sql` (+ `.shape.json`), and update `PostgreSqlBaselineSchema.cs` and `SqlServerBaselineSchema.cs`:
     - Add column `change_version BIGINT NOT NULL DEFAULT 1` to `POLICY_EPOCHS`.
     - Backfill `UPDATE POLICY_EPOCHS SET change_version = COALESCE((SELECT MAX(version) FROM CATALOG_STATE), 1) WHERE change_version <= 1;`.
     - Create index `IX_POLICY_EPOCHS_SCOPE_CHANGE_VERSION ON POLICY_EPOCHS (scope, change_version)`.
2. **Repository Writer & Reader Wiring (`src/Autheris.Infrastructure/Persistence/GovernanceRepository.Catalog.cs`, `src/Autheris.Infrastructure/Persistence/GovernanceRepository.CatalogRows.cs`, `src/Autheris.Infrastructure/Persistence/GovernanceRepository.CatalogRelations.cs`, `src/Autheris.Infrastructure/Persistence/GovernanceRepository.CatalogSql.cs`, `src/Autheris.Application/Common/Interfaces/IGovernanceRepository.cs`):**
   - Update `IncrementTableEpochInternalAsync` (and batched table-epoch upsert in `UpsertTableMetadataBatchAsync`, `DeleteTableMetadataAsync`, `SetTableVisibilityAsync`, `AddMaskingRuleAsync`, `RemoveMaskingRuleAsync`, `AssignDataOwnerAsync`, `RevokeDataOwnerAsync`, `UpdateTableKeyDefinitionAsync`) so every table epoch bump also sets `change_version = @changeVersion` (using the new `CATALOG_STATE` version inside the transaction, or when inside an additive `CatalogWriteScope`, updating `change_version` for the scope's recorded tables when `FlushPendingAsync` bumps `CATALOG_STATE`).
   - Add to `ITableMetadataRepository` (`src/Autheris.Application/Common/Interfaces/IGovernanceRepository.cs:9-30`):
     ```csharp
     Task<(long CurrentVersion, IReadOnlyList<TableIdentifier> ChangedTables)> GetTablesChangedSinceAsync(
         long sinceVersion,
         CancellationToken ct = default);
     ```
   - Implement `GetTablesChangedSinceAsync` in `GovernanceRepository.Catalog.cs` (`SEC-OI-675-2`).
3. **Incremental `CatalogSnapshot.WithDelta` (`src/Autheris.Application/Catalog/CatalogSnapshotProvider.cs` & `src/Autheris.Application/Catalog/CatalogSnapshot.cs`):**
   - Add `CatalogSnapshot.WithDelta(IReadOnlyList<TableMetadata> upserted, IReadOnlyCollection<TableIdentifier> removedOrChangedIds, long version)` on `src/Autheris.Application/Catalog/CatalogSnapshot.cs` (`153` lines) that clones the immutable maps, removes all `removedOrChangedIds`, inserts active `upserted` tables, and returns a new immutable `CatalogSnapshot` (`SEC-OI-675-3`, `SEC-OI-675-5`).
   - In `src/Autheris.Application/Catalog/CatalogSnapshotProvider.cs` (`144` lines) reload path: when `_currentSnapshot is not null && _lastLoadedVersion > 0 && !_dirtyFlag.IsDirty`, call `GetTablesChangedSinceAsync(_lastLoadedVersion, ct)`. If `changed.Count == 0`, update `_lastLoadedVersion = currentVersion` and return. If `changed.Count <= Math.Max(1, _currentSnapshot.TableCount / 5)` (`<= 20%`), load only those tables, apply `WithDelta`, and swap atomically; otherwise fall back to full `GetAllTablesAsync` (`SEC-OI-675-4`).

### Required Unit & Architecture Tests
- `tests/Autheris.Tests.Unit/Persistence/Migrations/PolicyEpochsChangeVersionMigration0015Tests.cs`:
  - `Migration_0015_Passes_StepSqlLint_And_Matches_ShapeJson`
  - `Migration_0015_Backfills_ChangeVersion_NeverZeroOrNull` (`[Trait("Category","Security")]`, `SEC-OI-675-6`)
- `tests/Autheris.Tests.Unit/Catalog/CatalogSnapshotDeltaReloadTests.cs`:
  - `SingleTableDeactivated_RemovedOnDeltaReload` (`[Trait("Category","Security")]`, `SEC-OI-675-3`)
  - `VisibilityChanged_ReflectedOnDeltaReload` (`[Trait("Category","Security")]`, `SEC-OI-675-1`)
  - `ConcurrentWriteDuringDeltaRead_NotSkippedOnNextReload` (`[Trait("Category","Security")]`, `SEC-OI-675-2`)
  - `DeltaExceeding20Percent_FallsBackToFullReload` (`SEC-OI-675-4`)
  - `InFlightReader_SeesConsistentSnapshotDuringDeltaSwap` (`[Trait("Category","Security")]`, `SEC-OI-675-5`)
- `tests/Autheris.Tests.Architecture/CatalogMutatingWritersBumpChangeVersionTests.cs` (`[Trait("Category","Security")]`, `SEC-OI-675-1`).

### Constrained Local Verification Commands
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~PolicyEpochsChangeVersion|FullyQualifiedName~CatalogSnapshotDelta|FullyQualifiedName~StepSqlLint|FullyQualifiedName~MigrationShape"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~CatalogMutatingWriters|FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests"
dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 2 — `WP-OI-CAT-4 (#671)`: Declarative Consent Prune (`prune=true|false|force`, `dryRun=true|false`) Scoped to `DECLARATIVE`

- **Worktree:** `/work/wt/oi-cat-671-declarative-prune`
- **Branch:** `feat/oi-cat-671-declarative-prune` (branch from `origin/main` after Wave 4 Brief 1 `WP-SIMPL-47 PR 1` merges)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-and-governance.md` (`WP-OI-CAT-4` / `#671`, lines 181–227)
  - `docs/plans/2026-11-13-sec-issues-group-3.md` (`SEC-OI-671-1..6`, lines 37–46) & `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-CAT-4`, `Q-SEC3-1..3`)

### Mandatory Security Conditions (`SEC-OI-671-1..6`)
- **`SEC-OI-671-1` (Never Prune Non-Declarative Consents):** Prune MUST filter strictly by `GrantSource == ConsentGrantSource.Declarative` (`grant_source = 'DECLARATIVE'`). Consents with `grant_source` in `{'CONSENT_REQUEST', 'ACCESS_PROFILE', 'OWNER', 'BULK', 'ACCESS_PLAN'}` MUST NEVER be revoked by declarative manifest prune.
- **`SEC-OI-671-2` (Caller Tenant + Manifest Grantees Scope Only):** Prune is strictly bounded to:
  1. `consent.TenantId == callerTenantId` (never cross-tenant), AND
  2. `consent.Grantee` is one of the canonical grantee SIDs/principals explicitly declared in the submitted manifest (`manifestGrantees`), AND
  3. `consent.Table` is NOT in the manifest's declared `(grantee, table)` pairs.
  An empty manifest (`consents: []`) has zero manifest grantees and therefore prunes **0** consents (or is rejected before prune when `prune=true`).
- **`SEC-OI-671-3` (Authority Per Pruned Table):** If the caller is not a tenant-scoped `GovernanceAdmin`, the caller MUST hold `ConsentRevocationAuthority` (active data owner or active `ApproveConsents` delegate) on **every** table whose consent would be pruned; if any candidate table fails the authority check, abort the entire transaction before any mutation (`403 Forbidden`).
- **`SEC-OI-671-4` (>50% Prune Threshold & Hard Cap of 10,000):**
  - If the number of candidate consents to prune exceeds `10,000` (`MaxDeclarativePruneCount = 10_000`, `Q-SEC3-3`), reject the call before any mutation.
  - If the candidate prune count exceeds **50%** of the active `DECLARATIVE` consents for `manifestGrantees` in `callerTenantId` (when total active `DECLARATIVE` consents for those grantees `>= 2`), `prune=true` MUST reject with `409 Conflict` (or `422 UnprocessableEntity`) requiring `prune=force`. Only `prune=force` proceeds above the 50% threshold.
- **`SEC-OI-671-5` (`dryRun=true` Side-Effect Free):** When `dryRun=true`, execute the exact same validation, authority check, and diff computation, return the full `DeclarativeConsentApplyResult` (including `PrunedCount` and `PrunedConsents`), and perform **zero** database writes and **zero** epoch bumps.
- **`SEC-OI-671-6` (Transactional Revocation, Audit & Epoch Bump):** Execute upserts and prune revocations in one transaction, record `RevokeReason = "PRUNED_BY_MANIFEST"` (with actor SID and manifest SHA-256 hash) in the audit trail for every pruned consent, and bump `CONSENTS` grant epoch + per-table epochs upon commit.

### Exact Changes to Make
1. **Models & Service (`src/Autheris.Domain/Model/DeclarativeConsentModels.cs`, `src/Autheris.Application/Governance/Interfaces/IDeclarativeConsentApplyService.cs`, `src/Autheris.Application/Governance/Services/DeclarativeConsentApplyService.cs:20-310`):**
   - Add `public enum DeclarativePruneMode { False = 0, True = 1, Force = 2 }`.
   - Extend `DeclarativeConsentApplyRequest` (or `ApplyAsync` parameters) with `DeclarativePruneMode PruneMode = DeclarativePruneMode.False` and `bool DryRun = false`.
   - Extend `DeclarativeConsentApplyResult` with `int PrunedCount` and `IReadOnlyList<DeclarativePrunedConsentDto> Pruned` (`ConsentId`, `Grantee`, `Domain`, `Schema`, `Table`).
   - Ensure every consent created/updated by `DeclarativeConsentApplyService` sets `GrantSource = ConsentGrantSource.Declarative`.
   - When `PruneMode != DeclarativePruneMode.False`:
     - Query active consents in `callerTenantId` where `GrantSource == ConsentGrantSource.Declarative` and `Grantee` is in `manifestGrantees`.
     - Identify candidates whose `(Grantee, TableIdentifier)` is not in the manifest's normalized entries.
     - Enforce `SEC-OI-671-3` (authority check per candidate table), `SEC-OI-671-4` (`<= 10_000` cap and `> 50%` threshold requiring `DeclarativePruneMode.Force`).
     - If `!DryRun`, revoke all candidate consents inside the transaction with reason `"PRUNED_BY_MANIFEST"`, write audit records with manifest SHA-256, and bump epochs.
2. **Endpoint Wiring (`src/Autheris.Api/Endpoints/GovernanceEndpoints.Catalog.cs:40-154`):**
   - Bind query parameters `string? prune = "false"` (`"false"` | `"true"` | `"force"`, rejecting unknown values with `400 BadRequest`) and `bool dryRun = false` on `POST /api/v1/governance/consents/apply`.

### Required Unit Tests
- `tests/Autheris.Tests.Unit/Catalog/Services/DeclarativeConsentApplyPruneSecurityTests.cs` (`[Trait("Category","Security")]`):
  - `Prune_NeverTouches_NonDeclarativeConsents` (`SEC-OI-671-1`)
  - `Prune_NeverTouches_OtherTenant_Or_GranteeNotInManifest` (`SEC-OI-671-2`)
  - `Prune_EmptyManifest_PrunesNothing` (`SEC-OI-671-2`)
  - `Prune_NonAdminCallerWithoutOwnerAuthorityOnPrunedTable_DeniedAtomically` (`SEC-OI-671-3`)
  - `Prune_Above50Percent_RejectedWithoutForce_AllowedWithForce` (`SEC-OI-671-4`)
  - `Prune_Above10000Cap_Rejected` (`SEC-OI-671-4`)
  - `DryRun_ReturnsDiffAndPrunedList_WithoutMutatingStoreOrEpochs` (`SEC-OI-671-5`)
  - `Prune_WritesAuditWithManifestHash_And_BumpsGrantEpoch` (`SEC-OI-671-6`).

### Constrained Local Verification Commands
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~DeclarativeConsentApply"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests"
dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 3 — `WP-OI-SQL-479-B`: Dialect Semantics PR B (`date_add('quarter', ...)`, Oracle Month-End `ADD_MONTHS`, and `Concat` Arithmetic Parenthesization)

- **Worktree:** `/work/wt/oi-sql-479b-date-concat`
- **Branch:** `feat/oi-sql-479b-date-concat` (branch from `origin/main` after Wave 4 Brief 3 `WP-OI-SQL-479-A` merges)
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-sql-engine-dialects.md` (`WP-OI-SQL-479` PR B: lines 105–138 & 171)
  - `docs/plans/2026-11-13-sec-issues-group-5.md` (`SEC-OI-479-1`, `SEC-OI-479-4`, lines 28–39) & `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-SQL-2`, `Q-SEC5-2`)

### Mandatory Security Conditions (`SEC-OI-479-1` & `SEC-OI-479-4`)
- **`SEC-OI-479-1` (Non-Duplicating Operand Rule):**
  - For `date_add('quarter', n, ts)` / `date_diff('quarter', ...)`: emit `(n) * 3` months (`MAKE_INTERVAL(months => (n) * 3)` in PostgreSQL, `ADD_MONTHS(ts, (n) * 3)` in Oracle) so `n` and `ts` are each emitted **exactly once**.
  - For Oracle month-end day clamping on `date_add('month' | 'quarter' | 'year', n, ts)`: Oracle `ADD_MONTHS(date '2026-01-31', 1)` returns `2026-02-28`, whereas `ADD_MONTHS(date '2026-02-28', 1)` returns `2026-03-31` (Oracle preserves end-of-month when the source date is the last day of February, whereas Trino `date_add('month', 1, DATE '2026-02-28')` returns `2026-03-28`). To prevent quadratic AST expansion (`SEC-OI-479-1`), apply the clamping wrapper ONLY when `ts` is a simple non-nested operand (`ColumnReference`, `LiteralExpression`, `ParameterExpression`, or `CastExpression` over a simple operand), or emit standard `ADD_MONTHS(ts, months)` when `ts` is a compound expression so nested `date_add` calls never duplicate `ts`.
- **`SEC-OI-479-4` (Parenthesize Arithmetic Children of `Concat` `||` at Emission Time):**
  - In Trino/PostgreSQL/Oracle/Databricks, `||` (`BinaryOperator.Concat`) has the same or looser precedence relative to `+`/`-` in some dialects, while in SQL Server `Concat` emits `+` (`SqlServerDialectGenerator.cs`), where `'a' || (1 + 2)` vs `'a' + 1 + 2` changes type coercion and evaluation order!
  - In `SqlDialectGeneratorBase.Expressions.cs:413-466` (`NeedsParentheses` / `FormatBinaryExpression`), whenever `parent.Operator == BinaryOperator.Concat` and a child is a `BinaryExpression` with `Operator` in `{ Add, Subtract, Multiply, Divide, Modulo }` (or `UnaryExpression`), ALWAYS wrap the child in parentheses `( ... )` across all dialects, and do NOT strip those parentheses during AST normalization.

### Exact Changes to Make
1. **`date_add` / `date_diff` Quarter Translation (`src/TrinoSqlEngine/Ast/Generators/PostgreSqlDialectGenerator.cs:82-103`, `src/TrinoSqlEngine/Ast/Generators/OracleDialectGenerator.cs:239-261`, `src/TrinoSqlEngine/Ast/Generators/SqlDialectGeneratorBase.Functions.cs:424-434`):**
   - In `PostgreSqlDialectGenerator.GenerateDateAdd` (`:82-103`): handle `DateUnit.Quarter` by emitting `({ts} + MAKE_INTERVAL(months => ({amount}) * 3))` (and in `GenerateDateDiff` `:105-135`, divide month diff by `3` for `DateUnit.Quarter`).
   - In `OracleDialectGenerator.GenerateDateAdd` (`:239-261`): handle `DateUnit.Quarter` via `ADD_MONTHS` with `({amount}) * 3` (and `DateUnit.Year` with `({amount}) * 12`). For simple `ts` operands (`ColumnReference`, `LiteralExpression`, `ParameterExpression`), clamp the day to `LEAST(EXTRACT(DAY FROM ts), EXTRACT(DAY FROM LAST_DAY(ADD_MONTHS(ts, months))))` or keep `ADD_MONTHS` non-duplicating on compound expressions per `SEC-OI-479-1`.
2. **`Concat` (`||`) Arithmetic Child Parenthesization (`src/TrinoSqlEngine/Ast/Generators/SqlDialectGeneratorBase.Expressions.cs:413-466`):**
   - Update `NeedsParentheses(BinaryOperator parentOp, Expression child, bool isRightChild)` so that when `parentOp == BinaryOperator.Concat` and `child` is a `BinaryExpression` with `Operator` in `{ BinaryOperator.Add, BinaryOperator.Subtract, BinaryOperator.Multiply, BinaryOperator.Divide, BinaryOperator.Modulo }`, it returns `true`.

### Required Unit Tests (`tests/TrinoSqlEngine.Tests/`)
- `DialectSemantics/DateAddQuarterAndConcatPrecedenceTests.cs`:
  - `PostgreSql_DateAddQuarter_EmitsMakeIntervalMonthsTimesThree`
  - `Oracle_DateAddQuarter_EmitsAddMonthsTimesThree`
  - `SqlServer_And_Databricks_DateAddQuarter_EmitsQuarterUnit`
  - `Concat_WithArithmeticChild_ParenthesizesChildAcrossAllDialects` (`[Trait("Category","Security")]`, `SEC-OI-479-4`)
  - `NestedDateAdd_DoesNotDuplicateComplexTimestampOperand_And_RespectsLengthCap` (`[Trait("Category","Security")]`, `SEC-OI-479-1`).

### Constrained Local Verification Commands
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/TrinoSqlEngine.Tests/TrinoSqlEngine.Tests.csproj -c Release -m:1 -nodeReuse:false
dotnet test tests/TrinoSqlEngine.Tests/TrinoSqlEngine.Tests.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~DateAddQuarter|FullyQualifiedName~ConcatPrecedence"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests"
```

---

## Brief 4 — `WP-SIMPL-45 PR 5`: Governed Write Execution (Intent/Outcome Audit, Idempotency, Write Budgets, `WriteConnection` & Required-Privileges Report)

- **Worktree:** `/work/wt/simpl-45-pr5-write-exec`
- **Branch:** `feat/simpl-45-pr5-write-exec` (branch from `origin/main` after Wave 3 `WP-SIMPL-45 PR 4` merges)
- **Plan & Security References:**
  - `docs/plans/simplification/WP-SIMPL-45-governed-write-path.md` (Section 5.4, Section 6a `SEC-SIMPL-128..133`, Section 7 PR 5)

### Mandatory Security Conditions (`SEC-SIMPL-128..131`)
- **`SEC-SIMPL-128` (Mandatory `Idempotency-Key` on WebSQL DML):** When a WebSQL request executes a DML statement (`INSERT`, `UPDATE`, `DELETE`), an `Idempotency-Key` header (`1..128` ASCII printable characters) MUST be present; missing or malformed `Idempotency-Key` on WebSQL DML is rejected with `400 BadRequest` (`IDEMPOTENCY_KEY_REQUIRED`) before compilation/execution. Replaying the same `(tenant, callerSid, surface, Idempotency-Key)` with a different statement fingerprint returns `409 Conflict` (`IDEMPOTENCY_KEY_PAYLOAD_MISMATCH`).
- **`SEC-SIMPL-129` (Two-Phase Intent + Pre-Commit Outcome Audit):**
  - Before opening the DB transaction, write and flush `WEBSQL_DML_INTENT` (`correlationId`, `tenant`, `callerSid`, `targetTable`, `operation`, `statementHash`, `consentIds`). If the intent audit write fails, abort before opening the target DB connection.
  - Extend `ICheckedDmlExecutor.ExecuteInTransactionAsync` (`src/TrinoSqlEngine/Ast/Emit/CheckedDmlExecutor.cs:36-90`) with an optional `Func<int, CancellationToken, Task>? beforeCommit = null` callback invoked after row-count verification passes and **before** `await transaction.CommitAsync(ct)`.
  - Invoke `WEBSQL_DML_EXECUTED` audit inside `beforeCommit`: if the outcome audit write throws, `CheckedDmlExecutor` rolls back the target DB transaction and rethrows (so a committed write never lacks an outcome audit record).
- **`SEC-SIMPL-130` (Per-Caller & Per-Tenant Write Budgets):** Implement `IWriteBudgetStore` (`InMemoryWriteBudgetStore` + `DistributedCacheWriteBudgetStore`) enforcing `RestApiOptions.WriteStatementsPerMinute` (default `60`, max `600`) and `RestApiOptions.WriteRowsPerHour` (default `10_000`, max `1_000_000`) per `(tenantId, callerSid)` before and during `beforeCommit`. If `affectedRows` would push the caller over `WriteRowsPerHour`, throw inside `beforeCommit` so the transaction rolls back.
- **`SEC-SIMPL-131` (Dedicated `WriteConnection` Separation & Admin Required-Write-Privileges Report):**
  - Add `string? WriteConnectionString` to `DataSourceConnectionConfig`. Read queries (`SELECT`) ALWAYS use `ConnectionString` (read-only DB login). Governed DML (`INSERT`/`UPDATE`/`DELETE`) uses `WriteConnectionString` when configured; if `AllowWrites == true` on the data source and `WriteConnectionString` is null/empty, fail closed (`DATA_SOURCE_WRITE_CONNECTION_NOT_CONFIGURED`).
  - Add `GET /api/v1/admin/datasources/{name}/required-write-privileges` (restricted to `GovernanceAdmin`), returning the exact SQL `GRANT SELECT, INSERT (cols), UPDATE (cols), DELETE ON schema.table` statements derived from active write-enabled policies/consents on that data source, without exposing connection strings or credentials.

### Exact Changes to Make
1. **`ICheckedDmlExecutor` & `CheckedDmlExecutor` (`src/TrinoSqlEngine/Ast/Emit/CheckedDmlExecutor.cs:36-160`):**
   - Add `Func<int, CancellationToken, Task>? beforeCommit = null` parameter to `ExecuteInTransactionAsync`. Call `await beforeCommit(affectedRows, ct)` after row-count cap validation and before `await transaction.CommitAsync(ct)`. On any exception from `beforeCommit`, roll back the transaction and rethrow.
2. **Write Budget Store (`src/Autheris.Application/Policy/WriteGrants/IWriteBudgetStore.cs`, `InMemoryWriteBudgetStore.cs`, `src/Autheris.Domain/Options/Protocols/RestApiOptions.cs`):**
   - Add `WriteStatementsPerMinute = 60` and `WriteRowsPerHour = 10_000` to `RestApiOptions` (`src/Autheris.Domain/Options/Protocols/RestApiOptions.cs`).
   - Implement `TryAcquireStatementSlotAsync(tenantId, callerSid, ct)` and `TryRecordAffectedRowsAsync(tenantId, callerSid, affectedRows, ct)`.
3. **`GovernedSqlExecutor` Write Path (`src/Autheris.Application/Sql/Services/GovernedSqlExecutor.cs:440-530`):**
   - Enforce `Idempotency-Key` presence and payload-hash binding on WebSQL DML (`SEC-SIMPL-128`).
   - Check `IWriteBudgetStore.TryAcquireStatementSlotAsync` (`SEC-SIMPL-130`).
   - Resolve `WriteConnectionString` for DML (`SEC-SIMPL-131`).
   - Emit `WEBSQL_DML_INTENT` before execution and `WEBSQL_DML_EXECUTED` + `TryRecordAffectedRowsAsync` inside `beforeCommit` (`SEC-SIMPL-129`, `SEC-SIMPL-130`).
4. **Admin Endpoint (`src/Autheris.Api/Endpoints/GovernanceEndpoints.Catalog.cs`):**
   - Map `GET /api/v1/admin/datasources/{name}/required-write-privileges` requiring `GovernanceAdmin` (`SEC-SIMPL-131`).

### Required Unit Tests
- `tests/Autheris.Tests.Unit/WebSql/GovernedSqlWriteExecutionSecurityTests.cs` (`[Trait("Category","Security")]`):
  - `WebSqlDml_MissingIdempotencyKey_RejectedBeforeExecution` (`SEC-SIMPL-128`)
  - `WebSqlDml_ReusedIdempotencyKeyWithDifferentSql_ReturnsConflict` (`SEC-SIMPL-128`)
  - `WebSqlDml_IntentAuditFailure_AbortsBeforeOpeningDbConnection` (`SEC-SIMPL-129`)
  - `WebSqlDml_OutcomeAuditFailureInBeforeCommit_RollsBackTransaction` (`SEC-SIMPL-129`)
  - `WebSqlDml_WriteBudgetExceeded_RollsBackAndRejects` (`SEC-SIMPL-130`)
  - `WebSqlDml_UsesWriteConnectionString_And_FailsClosedWhenMissing` (`SEC-SIMPL-131`)
  - `RequiredWritePrivilegesEndpoint_ReturnsLeastPrivilegeGrants_RequiresAdmin` (`SEC-SIMPL-132`).

### Constrained Local Verification Commands
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~GovernedSqlWriteExecution|FullyQualifiedName~CheckedDmlExecutor"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests"
dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 5 — `WP-OI-SCALE-5 (#676 PR 1–3)` & `WP-OI-SCALE-6 (#677 PR 1)`: Incremental Search Index & Stale-Only / Paged Catalog Reads

### Part 5A — `WP-OI-SCALE-5 (#676)`: Embedding Cache + Incremental BM25/Vector Delta Segments & Tombstones
- **Worktree:** `/work/wt/oi-scale-676-incremental-search`
- **Branch:** `feat/oi-scale-676-incremental-search`
- **Target Files (`src/Autheris.Application/Catalog/Search/`):** `Bm25SearchIndex.cs` (`115` lines), `VectorSearchIndex.cs` (`57` lines), `CatalogSearchEngine.cs` (`510` lines), `CatalogSearchWarmupService.cs` (`324` lines), plus new `CatalogSearchEmbeddingCache.cs`.
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-scale.md` (`WP-OI-SCALE-5` / `#676`, lines 95–107)
  - `docs/plans/2026-11-13-sec-issues-group-7.md` (`SEC-OI-676-1..5`, lines 83–98)

#### Mandatory Security Conditions (`SEC-OI-676-1..5`)
- **`SEC-OI-676-1` (Tombstone Mask Before Scoring):** When a table is updated, deactivated, hidden, or deleted in `ApplyDelta`, its old document ID in the base BM25 and vector segments MUST be added to the immutable `_tombstones` bitset, and both BM25 and vector search MUST skip tombstoned doc IDs **before** scoring or top-k collection so old column names/descriptions never match or influence ranks.
- **`SEC-OI-676-2` (Stable Term IDF Across Base + Delta):** Keep base segment `avgdl` and `idf` fixed during delta updates, and bound delta segment size to `< 10%` of base tables and `< 5,000` tombstones (`MaxTombstonesBeforeRebuild = 5_000`); when exceeded, trigger a full index rebuild.
- **`SEC-OI-676-3` (Immutable Snapshot Swap):** `ApplyDelta` returns a new immutable index instance published via `Interlocked.Exchange`; concurrent readers never see a partially mutated postings list or tombstone set.
- **`SEC-OI-676-4` (Scrubbed-Text Embedding Cache Key):** `CatalogSearchEmbeddingCache` (bounded `100,000` entries) MUST key entries by `(providerName, modelId, dimension, sha256HexOfScrubbedFocusedText)` — always scrub hidden/denied catalog text **before** hashing, and store only `float[]` vectors (never raw text).
- **`SEC-OI-676-5` (Fallback to Full Rebuild on Failure):** Any exception in delta indexing or >10% table change falls back to a full rebuild while keeping the previous index serving (`#501` invariant).

#### Required Unit Tests (`tests/Autheris.Tests.Unit/Catalog/Search/`)
- `CatalogSearchIncrementalIndexSecurityTests.cs` (`[Trait("Category","Security")]`):
  - `TombstonedDocument_RemovedColumnText_NeverMatchesInBm25OrVector` (`SEC-OI-676-1`)
  - `TombstoneCountExceeding5000_Or_DeltaAbove10Percent_TriggersFullRebuild` (`SEC-OI-676-2`, `SEC-OI-676-5`)
  - `EmbeddingCache_KeyIncludesProviderModelDimensionAndScrubbedTextHash` (`SEC-OI-676-4`)
  - `ApplyDelta_PublishesNewImmutableSnapshot_ConcurrentReadersUnblocked` (`SEC-OI-676-3`).

### Part 5B — `WP-OI-SCALE-6 (#677 PR 1)`: Stale-Only `GetAllTablesAsync` & Unfiltered Internal `GetTablesAsync` / `QueryTablesAsync`
- **Worktree:** `/work/wt/oi-scale-677-pr1-paged-reads` (run after Brief 1 `W5-1` merges on `GovernanceRepository.Catalog.cs`)
- **Branch:** `feat/oi-scale-677-pr1-paged-reads`
- **Plan & Security References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-scale.md` (`WP-OI-SCALE-6` / `#677`, lines 158–218)
  - `docs/plans/2026-11-13-sec-issues-group-7.md` (`SEC-OI-677-1..6`, lines 100–120) & `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-SCALE-6`, `Q-SEC7-3`)

#### Mandatory Security Conditions (`SEC-OI-677-1..6`)
- **`SEC-OI-677-1` (Repository is Raw/Unfiltered; Paging/Limit/Sort Strictly After Caller Visibility Filtering):** The catalog repository has NO tenant concept and returns raw, unfiltered `TableMetadata` internally. Every caller-facing service (`CatalogDiscoveryService`, `DiscoveryService`, `McpDatasetCatalog`) MUST apply tenant scope, `DiscoveryHidden`, masking, and consent/decision filtering **before** computing `totalCount`, `offset`, `limit`, cursor slice, or sort order.
- **`SEC-OI-677-2` & `SEC-OI-677-6` (Parameterized Keyset & Escaped `LIKE`):** In `QueryTablesAsync(CatalogTableQuery query, CancellationToken ct)`, clamp `PageSize` to `1..500`, validate `After` cursor parts, and escape `%`, `_`, `[`, and `\` with `ESCAPE '\'` on `NamePrefix` / `Domain` / `Schema` filters.
- **`SEC-OI-677-3` (Batch Identifier Cap):** In `GetTablesAsync(IReadOnlyCollection<TableIdentifier> identifiers, CancellationToken ct)`, cap or chunk identifier lists at `500` per query and verify exact `(Domain, Schema, TableName)` tuple equality in memory so cross-domain combinations of the `IN` clauses are never returned.
- **`SEC-OI-677-4` (Epochs Read Before Data in `GetAllTablesAsync`):** In `GovernanceRepository.Catalog.cs`, `GetAllTablesAsync` MUST read `GetCatalogStateVersionAsync` and all `POLICY_EPOCHS` (`scope = 'TABLE'`) **before** comparing against `_metadataCache` and loading only stale/missing tables from the DB, then stamping each cached entry with the epoch read before the table query.
- **`SEC-OI-677-5` (Active Tables Only by Default):** `IncludeInactive` defaults to `false`; only explicit governance admin paths pass `IncludeInactive = true`.

#### Constrained Local Verification Commands (Parts 5A & 5B)
```bash
export MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget
dotnet build tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~CatalogSearchIncremental|FullyQualifiedName~EmbeddingCache|FullyQualifiedName~GovernanceRepositoryPagedCatalog"
dotnet build tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release --no-build -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s --filter "FullyQualifiedName~ProgramRatchetTests|FullyQualifiedName~SourceSizeRatchetArchitectureTests|FullyQualifiedName~CleanArchitectureTests|FullyQualifiedName~DocsIntegrityTests"
dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release --no-dependencies -m:1 -nodeReuse:false
```

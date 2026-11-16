# Phase 4 Hydrogen Developer Briefs — Wave 7 (Final Residual Queue) & Open PR Security Audit

- **Date:** 2026-11-14
- **Author:** Solution Architect (`csharp-architect`) & Security Reviewer (`csharp-security-expert`)
- **Target Execution Model:** Gemini 4 Hydrogen (`csharp-tdd-developer`)
- **Baseline Verified:** `origin/main` @ `6de0553d` (`WP-SIMPL-53 PR 4 (#573)` merged) + `20278388` (Wave 3–6 briefs)

---

## Global Execution Rules for All Wave 7 Hydrogen Workers

1. **Constrained Local Build & Test Rules (Mandatory):**
   - Prefix every `dotnet` command with `MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget`.
   - Always pass `-c Release -m:1 -nodeReuse:false --no-restore` (or `-p:RestoreConfigFile=/work/NuGet.Offline.Config` when restore is needed).
   - **NEVER** run `Autheris.Tests.Integration` locally (`dotnet test` on integration tests is forbidden; compile-only check via `dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj`).
   - Run **ONLY** filtered unit tests (`--filter "FullyQualifiedName~..."`) and filtered architecture tests (`--filter "FullyQualifiedName~..."`).
   - Pass `-p:AutherisIncludeTestAuth=true` whenever a unit test references `TestAuthHandler`.
2. **Ratchet & Hygiene Gates (Every PR Must Pass Before Reporting):**
   - **Zero Plan-ID Tokens in `src/` (`PlanIdCommentRatchetTests`):** `grep -rnE '\b(WP-[A-Z0-9-]+|SEC-[A-Z0-9-]+|OD-[0-9]+)\b' src/` on touched/created files must return `0` matches.
   - **Source Size Ceiling (`SourceSizeRatchetArchitectureTests`):** Every modified or created `src/**/*.cs` file must stay `<= 1000` lines (and below its `tests/Autheris.Tests.Architecture/Baselines/source-size-ceilings.txt` entry if listed). Every modified or created `tests/**/*.cs` file must stay `<= 800` lines.
   - **Oversized Files Requiring Partial Splits (Never Grow Existing File):**
     - `src/Autheris.Application/Procedures/Services/GovernedProcedureExecutionService.cs` (`1102` lines on `6de0553d`) -> extract new logic into `GovernedProcedureExecutionService.<Feature>.cs`.
     - `src/Autheris.Extensions/Dbt/DbtMetadataIngestionService.cs` (`1759` lines on `6de0553d`) -> add new logic in a separate partial file (`DbtMetadataIngestionService.<Feature>.cs`).
     - `src/Autheris.GraphQL/Types/Mutation.Consent.cs` (`963` lines), `src/Autheris.Api/Endpoints/CatalogApiEndpoints.cs` (`951` lines), `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Consent.cs` (`942` lines) -> place new methods/endpoints in new partial/extension files.
   - **Docs Integrity (`DocsIntegrityTests`):** Never include literal `file` + `://` links in any `.md` file.
   - **Net `src/` Diff Budget:** Each PR slice below is sized at `<= 400` net added `src/` lines (deletions and generated `.shape.json` files excluded).
3. **Sequential DDL Migration Queue (Single DDL Slot Discipline):**
   - On `main` (`6de0553d`), migrations `0001`–`0012` are merged.
   - Wave 3–6 reserve:
     - `0013_catalog_discovery` (`W3-1`: `WP-SIMPL-41 PR 2`)
     - `0014_consent_api` (`W4-1`: `WP-SIMPL-47 PR 1`)
     - `0015_policy_epochs_change_version` (`W5-1`: `WP-OI-SCALE-4 PR 2` / `#675`)
     - `0016_dbt_unique_id` (`W6-0`: PR `#748` / `#666` rebased)
   - Wave 7 DDL slots continue strictly sequentially starting at **`0017`**:
     - `0017_usage_accounting` (`W7-42-2`: `WP-SIMPL-42 PR 2`)
     - `0018_consent_row_scopes` (`W7-48-2`: `WP-SIMPL-48 PR 2`)
     - `0019_pattern_grants` (`W7-48-5`: `WP-SIMPL-48 PR 5`)
     - `0020_data_classifications` (`W7-52-1A`: `WP-SIMPL-52 PR 1a`)
     - `0021_consent_classification_access` (`W7-52-3`: `WP-SIMPL-52 PR 3`)
     - `0022_drop_is_sensitive` (`W7-52-6A`: `WP-SIMPL-52 PR 6a`)
     - `0023_drop_superseded_governance_columns` (`W7-52-6B`: `WP-SIMPL-52 PR 6b`)
     - `0024_data_products` (`W7-49-A1`: `WP-SIMPL-49 PR A1`, post-freeze)
     - `0025_golden_queries` (`W7-54-1`: `WP-SIMPL-54 PR 1`, `#580`, post-freeze)
   - Every DDL PR must branch from `main` **after** the immediately preceding DDL step merges, build its `.shape.json` from the preceding step's `.shape.json` with `"version": N`, update `StepSqlLintTests.cs`, and append the SHA-256 checksums to `tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.{postgresql,sqlserver}.txt`.

---

## Section 0: Independent Security & Merge-Readiness Audit of Open PRs (`#734`, `#744`, `#747`, `#748`) vs `main` (`6de0553d`)

### 0.1 Summary Table

| PR | Branch (`SHA`) | Issue / WP | Merge Tree vs `6de0553d` | `src/` Files & Line Ceilings | `SEC-*` Conditions | Verdict |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **PR `#734`** | `origin/pr-734` (`cbcec9de`) | `#624` (`WP-OI-SRCH-624`) | Clean (`542eaa75`, `0` conflicts) | `0` `src/` files touched (`+1286` in `tests/` & `docs/`); `CatalogSearchRelevanceTests.cs` = `243` LOC (`<= 800`), `RelevanceFixture.cs` = `200` LOC (`<= 800`) | `SEC-OI-624-1` (`RestrictedCaller_FixtureHiddenColumnText_NeverProducesHit` `[Trait("Category","Security")]`) verified | **`SAFE TO MERGE AS-IS`** |
| **PR `#744`** | `origin/pr-744` (`9a279ca9`) | `#669` (`WP-OI-WEB-669`) | Clean (`16ddb9de`, `0` conflicts) | `GovernedSqlRewriter.cs` (`+15/-2`, `970` -> `983` LOC `<= 1000`); `WebSqlSqlServerSynonymTests.cs` = `252` LOC (`<= 800`) | `SEC-OI-669-1` (client error response identical across missing/inactive/zero-column tables; server-side warning log distinguishes reason) verified | **`SAFE TO MERGE AS-IS`** |
| **PR `#747`** | `origin/pr-747` (`e3078e14`) | `#668` (`WP-OI-WEB-668`) | Clean (`be23e658`, `0` conflicts) | `GovernedSqlRewriter.cs` (`+18/-6`, `970` -> `982` LOC; **combined `#744` + `#747` = `995` LOC `<= 1000`**); `GatewayStartupValidator.cs` (`872` -> `903` LOC); `SqlDataSourceResolver.cs` (`188` -> `226` LOC); `GovernedCatalogProvider.cs` (`315` -> `327` LOC); `DataSourceAllowlistGuard.cs` (`95` -> `94` LOC) | `SEC-OI-668-1..5` (single `SqlDataSourceResolver.ResolveConnectionKey`, one-hop validation, `GovernedCatalogProvider` `(DataSource, ConnectionKey, Schema, Table)` cache key, uniform client denial) verified | **`SAFE TO MERGE AS-IS`** *(leaves `5` LOC headroom in `GovernedSqlRewriter.cs` for `W3-3B`)* |
| **PR `#748`** | `origin/pr-748` (`56edc825`) | `#666` (`WP-OI-CAT-1`) | **CONFLICTS** (`checksums.{postgresql,sqlserver}.txt` + step `0012` collision) | `DbtMetadataIngestionService.cs` (`1760` LOC), `GovernanceRepository.Catalog*.cs` (`<= 795` LOC); missing `dbt_unique_id` in `PostgreSqlBaselineSchema.cs` & `SqlServerBaselineSchema.cs` | `SEC-OI-666-1..3` logic present (`DbtPhysicalTableNameTests.cs` `[Trait("Category","Security")]`), but migration step `0012_dbt_unique_id` collides with `main`'s `0012` | **`REQUIRES REBASE & RENUMBER (`W6-0`)`** |

### 0.2 Detailed Per-PR Findings

1. **PR `#734` (`origin/pr-734` @ `cbcec9de`, `#624` Catalog Search Relevance Benchmark Set):**
   - **Diff Scope:** 8 files changed (`+1286/-0`): `docs/features/f-ai-02-semantic-catalog-search.md`, `tests/Autheris.Tests.Unit/CatalogSearch/CatalogSearchRelevanceTests.cs`, `tests/Autheris.Tests.Unit/CatalogSearch/RelevanceFixture/RelevanceFixture.cs`, and 5 embedded JSON fixture resources.
   - **Ratchet & Hygiene Checks:** Zero `src/` files modified; zero plan-ID tokens in `src/`; zero `file` + `://` links in `.md` files; all test files `<= 243` lines (`<= 800` ceiling).
   - **Security Verification (`SEC-OI-624-1`):** `RestrictedCaller_FixtureHiddenColumnText_NeverProducesHit` (`[Trait("Category","Security")]`) verifies that hidden/denied column descriptions in the benchmark fixture never produce search hits for restricted callers. Includes commit `ea5ea13a` enabling the `full-identifier` cases after `#627`.
   - **Verdict:** **`SAFE TO MERGE AS-IS`**.

2. **PR `#744` (`origin/pr-744` @ `9a279ca9`, `#669` SQL Server Synonyms Regression Test & Unusable Table Log):**
   - **Diff Scope:** 6 files changed (`+301/-2`), touching only `src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs` (`+15/-2`, net `+13` lines: `970` -> `983` lines `<= 1000`), `docs/configuration-guide.md`, `docs/features/f-data-02-governed-websql.md`, `tests/Autheris.Tests.Unit/Sql/GovernedSqlRewriterCoverageTests.cs`, and `tests/Autheris.Tests.Unit/Sql/WebSqlSqlServerSynonymTests.cs` (`252` lines).
   - **Ratchet & Hygiene Checks:** Zero plan-ID tokens in `src/`; zero `file` + `://` links in `.md` files; all file sizes within ceiling.
   - **Security Verification (`SEC-OI-669-1`):** `GovernedSqlRewriter.cs` continues to throw the exact same `SqlDataSourceResolver.TableDenied(target)` client exception across missing, inactive, and zero-column tables (preventing any client-visible existence oracle), while logging the specific server-side reason at `Warning`.
   - **Verdict:** **`SAFE TO MERGE AS-IS`**.

3. **PR `#747` (`origin/pr-747` @ `e3078e14`, `#668` WebSQL Mapped Connection Resolution):**
   - **Diff Scope:** 21 files changed (`+908/-32`). Three-dot diff against `6de0553d` merges cleanly (`be23e6584b83a403c6fd237e087bdedfe573a646`) including the 3 startup validator snapshot files (`startup-check-order.txt`, `startup-validator-failures.txt`, `startup-validator-warnings.txt`).
   - **Ratchet & Hygiene Checks:**
     - Zero plan-ID tokens (`WP-`/`SEC-`/`OD-`) in `src/`; zero `file` + `://` links in `.md` files.
     - `GovernedSqlRewriter.cs` increases by net `+12` lines (`970` -> `982`). Because `#744` (`+13` lines at `:442-458`) and `#747` (`+12` lines at `:372, :744`) touch disjoint regions, **merging both `#744` and `#747` puts `GovernedSqlRewriter.cs` at `995` lines (`<= 1000` ceiling)**. *(Note for Wave 3 `W3-3B` / `WP-SIMPL-45 PR 4B`: `GovernedSqlRewriter.cs` will have `5` lines of headroom after `#744` + `#747` merge, so `W3-3B` must replace `CanWriteTableAsync` in place or extract a helper into `GovernedSqlRewriter.WriteGrants.cs` to stay `<= 1000` lines.)*
   - **Security Verification (`SEC-OI-668-1..5`):**
     - `SEC-OI-668-1`: Single resolver `SqlDataSourceResolver.ResolveConnectionKey(GatewayOptions, string?)` used across `DataSourceAllowlistGuard.MapDataSource`, `FederatedStagingService`, `GovernedSqlRewriter`, and `GovernedCatalogProvider` (pinned by `DataSourceMappingResolverArchitectureTests.cs`). Enforces one-hop resolution (`TryGetMapping(options.WebSql, target, out _)` rejects chains/cycles) and startup validation (`GatewayStartupValidator.ValidateDataSourceMappings`).
     - `SEC-OI-668-2..5`: `GovernedCatalogProvider` verifies `metadata.Table.SourceName == dataSourceName` against the mapped connection's dialect (`connectionKey`), keys its cache by `(DataSource, ConnectionKey, Schema, Table)` (`:386, :425`), and emits detailed mismatch reasons only to server logs (`LogAdmissionDenied`) while returning the fixed client denial message.
   - **Verdict:** **`SAFE TO MERGE AS-IS`**.

4. **PR `#748` (`origin/pr-748` @ `56edc825`, `#666` dbt Physical Table Name & `0012_dbt_unique_id`):**
   - **Exact Conflicts vs `6de0553d`:**
     1. **Content Conflict:** `tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.postgresql.txt` and `checksums.sqlserver.txt` conflict on step `0012`.
     2. **Migration Step Number Collision:** `#748` introduces `0012_dbt_unique_id.{sql,shape.json}`, colliding with `main`'s `0012_scim_external_identity_and_group_links` (and Wave 3–5 slots `0013`–`0015`).
     3. **Baseline Schema Drift:** `#748` adds `dbt_unique_id` in the step migration and `GovernanceRepository.Catalog*.cs`, but omits adding `dbt_unique_id VARCHAR(512) NULL` to `PostgreSqlBaselineSchema.cs` and `SqlServerBaselineSchema.cs`.
   - **Verdict:** **`REQUIRES REBASE & DDL RENUMBERING (`W6-0` in `2026-11-14-hydrogen-briefs-wave-6.md`)`** — rebase onto `main` after Wave 5 `0015`, rename `0012_dbt_unique_id.{sql,shape.json}` -> `0016_dbt_unique_id.{sql,shape.json}` (derived from `0015`'s `.shape.json` with `"version": 16`), add `dbt_unique_id` to `PostgreSqlBaselineSchema.cs` and `SqlServerBaselineSchema.cs`, update `StepSqlLintTests.cs`, and regenerate `checksums.{postgresql,sqlserver}.txt`.

---

## Section 1: Ground-Truth Verification of `WP-SIMPL-44`, `WP-SIMPL-50`, `WP-SIMPL-51`, `WP-SIMPL-53` on `main` (`6de0553d`)

| Work Package | Plan File | Verified Status on `main` (`6de0553d`) | Remaining Code Gaps |
| :--- | :--- | :--- | :--- |
| **`WP-SIMPL-51`** | `WP-SIMPL-51-websql-client-contract.md` | **100% COMPLETE on `main`** (`PR 1` `#566` `1d6fa34b`, `PR 2` `#570` `e96ee892`, `PR 3–5` `#581` `edeca24b`, `#529` `0bf0b50e`; `SqlStatementCostEstimator.cs` and `WebSqlPageTokenCodec.cs` present and tested). | **None (`0` PRs remaining).** |
| **`WP-SIMPL-53`** | `WP-SIMPL-53-mcp-catalog-client-hints.md` | **100% COMPLETE on `main`** (`PR 1` `ddb53afb`, `PR 2` `554ace53`, `PR 3` `9b3e8852`, `PR 4` `f45f7104`/`6de0553d`, `PR 5` `ba5dd11d`). | **None (`0` PRs remaining).** |
| **`WP-SIMPL-44`** | `WP-SIMPL-44-no-source-metadata-reads.md` | `PR 1` (`#619` `d37d96f2` `SourceMetadataGuard` & `MetadataGuardedConnection`) and `PR 2` (`#645` `a8c248fd` declared catalog & column reader removal) are merged on `main`. `PR 4 (#459)` is briefed in Wave 3 (`W3-C1/C2`). **`PR 3` (Steps 9 & 10) is NOT yet on `main`**: `StoredProcedureCatalogValidator.cs` (`538` LOC) and `SqlProcedureRowScopeResolver.EnsureUniqueKeyAsync` (`:345`) still exist in `src/`. | **`W7-44-3` (`WP-SIMPL-44 PR 3`)** briefed in Section 2.1 below. |
| **`WP-SIMPL-50`** | `WP-SIMPL-50-review-remediation-v1-7-1.md` | `PR A` (`#565`), `PR B1a` (`#592`), `PR B1b` (`#597`), `PR B2` (`#606`), and `PR B3` (`#629` `10d5b6eb`) are merged on `main`. **`PR B5` (`B14`), `PR B6` (`B16, C1`), and `PR C` (`C2, C3, C4, C8, C11, C15`) are NOT yet on `main`** (verified: `tools/Autheris.ModuleSigner` absent; `NoisyCacheKey.cs` `184` LOC still present; `ExposeSuppressedGroupsCount` still defaults to `true`; `UnknownGatewayKeyGuard.cs:113` still has `|| entry.Value is null`; `ODataEndpoints.Query.cs:39` still has `IsWriteMethod`; `vf:webhook:` replay store absent). | **`W7-50-B5`, `W7-50-B6`, `W7-50-C`** briefed in Section 2.1 below. |

---

## Section 2: Self-Contained Hydrogen Developer Briefs (Wave 7 Residual Queue)

### 2.1 Group A — Remaining Core Simplification Fixes (`WP-SIMPL-44 PR 3` & `WP-SIMPL-50 PR B5, B6, C`)

---

#### Brief `W7-44-3` — `WP-SIMPL-44 PR 3`: Procedure Row-Scope Key Closure & Declared-Only Procedure Validation

- **Branch / Worktree:** `feat/simpl-44-pr3-procedures-declared` in `/work/wt/simpl-44-pr3-procedures-declared`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-44-no-source-metadata-reads.md` (Steps 9 & 10, `SEC-SIMPL-140..147`, `SEC-SIMPL-31`, `SEC-SIMPL-03`)
- **DDL Step:** None (`0` migrations).
- **Preconditions:** Do not run in parallel with any PR editing `GovernedProcedureExecutionService.cs`.
- **Target `src/` Files (Verified on `6de0553d`):**
  1. `src/Autheris.Application/Procedures/Services/SqlProcedureRowScopeResolver.cs` (`403` lines):
     - Delete `EnsureUniqueKeyAsync` (`:345`), `BuildUniqueIndexQuery`, `UniqueIndexTableArgument`, `_uniquenessCache`, `UniquenessCacheTtl`, and `NotUnique`.
     - Rewrite `BuildKeyQuery` (`:250`) to use the key-closure query:
       `SELECT {keys} FROM {table} AS autheris_target WHERE ({key tuples}) GROUP BY {keys} HAVING COUNT(*) = SUM(CASE WHEN {tenant} AND ({filter}) THEN 1 ELSE 0 END)`
       so a key tuple is admitted iff **every** row carrying that key tuple satisfies both the tenant predicate and the caller's row filter (no physical uniqueness index query required).
  2. **Delete** `src/Autheris.Application/Procedures/Services/StoredProcedureCatalogValidator.cs` (`538` lines) and remove its DI registration in `src/Autheris.Api/Extensions/DependencyInjection/`.
  3. `src/Autheris.Domain/Model/ProcedureModels.cs` (`253` lines):
     - Delete `ProcedureValidationMode` (validation is always declared); update browse-mode XML doc (`:185`).
  4. `src/Autheris.Domain/Options/Protocols/RestApiOptions.cs` (`316` lines) & `src/Autheris.Api/Configuration/UnknownGatewayKeyGuard.cs` (`402` lines):
     - Delete `RestApiOptions.AllowDeclaredValidation` and add `"Gateway:Procedures:AllowDeclaredValidation"` / `"Gateway:Rest:AllowDeclaredValidation"` to `UnknownGatewayKeyGuard` (`SEC-SIMPL-31`).
  5. `src/Autheris.Application/Procedures/Services/ProcedureConnectionProvider.cs` (`90` lines):
     - Delete the `Catalog` validation branch.
  6. `src/Autheris.Application/Procedures/Services/ProcedureDefinitionLoader.cs` (`321` lines) & `ProcedureDefinitionParser.cs` (`684` lines):
     - Reject the YAML key `validation:` in the strict deserializer.
     - Require `result_table`, non-empty `outputs`, `parameters` (name, type, direction, optional default), and `tables:` (referenced governed tables; any unregistered table leaves the procedure `Pending` with reason `TABLE_NOT_REGISTERED`).
     - A procedure with `rls: session_context` on tables that have row filters for the caller must also declare `row_scope_key`; otherwise load fails with `ROW_SCOPE_REQUIRED`.
  7. `src/Autheris.Application/Procedures/Services/GovernedProcedureExecutionService.cs` (`1102` lines — **oversized file ceiling**):
     - Extract result-column governance (`:907-915`) into a new partial file `src/Autheris.Application/Procedures/Services/GovernedProcedureExecutionService.ResultGovernance.cs` so `GovernedProcedureExecutionService.cs` shrinks below `1102` lines: drop undeclared columns; govern columns with `source: <table>.<column>` by that source table (surviving only with explicit column-level `Clear`); govern columns without `source` by `result_table`.
  8. `tests/Autheris.Tests.Architecture/ArchitectureNoSourceMetadataReadsTests.cs` (or `ArchitectureNoSourceKeyCatalogQueriesTests.cs`):
     - Remove `SqlProcedureRowScopeResolver` and `StoredProcedureCatalogValidator` from the allowlist; add pattern `DeriveParameters`.
- **Required Unit & Architecture Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Procedures/ProcedureRowScopeKeyClosureTests.cs`: `BuildKeyQuery_GroupsByKey_HavingAllRowsPassFilterAndTenant` `[S]`, `UniqueKey_SameResultAsBefore`.
  - `tests/Autheris.Tests.Unit/Procedures/ProcedureDeclaredOnlyTests.cs`: `ValidationKey_Rejected` `[S]`, `MissingTablesOrOutputs_Pending` `[S]`, `UnregisteredTable_Pending` `[S]`, `RlsSessionContextWithRowFilters_RequiresRowScopeKey` `[S]`, `UndeclaredResultColumn_Dropped` `[S]`, `DeclaredSourceColumn_GovernedBySourceTable` `[S]`, `AllowDeclaredValidationKey_FailsStartup`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ProcedureRowScopeKeyClosure|FullyQualifiedName~ProcedureDeclaredOnly|FullyQualifiedName~ProcedureDefinition|FullyQualifiedName~GovernedProcedureExecution|FullyQualifiedName~UnknownGatewayKeyGuard" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~NoSource|FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id|FullyQualifiedName~Options"
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -m:1 -nodeReuse:false --no-dependencies -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-50-B5` — `WP-SIMPL-50 PR B5`: Signed Module Release Path & `Autheris.ModuleSigner` (`B14`)

- **Branch / Worktree:** `feat/simpl-50-pr-b5-module-signer` in `/work/wt/simpl-50-pr-b5-module-signer`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-50-review-remediation-v1-7-1.md` (Item `B14`, Section 6 `PR B5`, `SEC-50-08`, `SEC-50-15`)
- **Target Files:**
  1. **Create** `tools/Autheris.ModuleSigner/Autheris.ModuleSigner.csproj` and `tools/Autheris.ModuleSigner/Program.cs` (`Autheris.Tools.ModuleSigner` namespace):
     - Writes the canonical manifest format accepted by `ManifestSignatureVerifier` (canonical JSON, SHA-256 file hashes, ECDSA P-256 signature, `KeyId` matching the `B3` keyring).
     - `SEC-50-15`: Reads the signing private key strictly from an environment variable (`AUTHERIS_MODULE_SIGNING_KEY_PEM`) or `stdin` (`--key-stdin`), **never** from a CLI argument (reject any `--key` value that looks like PEM or a raw key), and never logs key material.
  2. `.github/workflows/release.yml` & `.github/workflows/docker-publish.yml`:
     - Add `module-package` job on protected release tags (`permissions: contents: write` only, never triggered on `pull_request`) that packs `modules/*`, signs with `Autheris.ModuleSigner`, and uploads the signed module archive.
     - Update `docker-publish.yml` to copy signed modules into the container image and add `module-e2e` job asserting HTTP `401` on `/modules/lakehouse/iceberg/v1/config` and HTTP `404` on `/v1/config` (`SEC-50-08`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Hosting/ModuleSignerRoundTripTests.cs`: `Signed_manifest_verifies_in_ManifestSignatureVerifier` `[S]`, `Tampered_file_rejected_after_signing` `[S]`, `Cli_key_argument_rejected_without_logging_value` `[S]` (`SEC-50-15`).
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ModuleSigner|FullyQualifiedName~ManifestSignatureVerifier" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id"
  ```

---

#### Brief `W7-50-B6` — `WP-SIMPL-50 PR B6`: Aggregation Suppressed Count Default (`B16`) & DP Leftovers Deletion (`C1`)

- **Branch / Worktree:** `feat/simpl-50-pr-b6-aggregation-dp` in `/work/wt/simpl-50-pr-b6-aggregation-dp`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-50-review-remediation-v1-7-1.md` (Items `B16` and `C1`, Section 6 `PR B6`)
- **Target `src/` Files (Verified on `6de0553d`):**
  1. `src/Autheris.Domain/Options/AnalyticsOptions.cs` (`101` lines):
     - Change `ExposeSuppressedGroupsCount` (`:90`) default from `true` to `false` (`D-50d`).
  2. `src/Autheris.Application/Analytics/GovernedAggregationService.cs` (`375` lines) & `src/Autheris.Application/Analytics/AggregationModels.cs` (`258` lines):
     - Add audit fields `filter_shape` (sorted `column:operator` pairs, zero literal values) and `exposed_suppressed_counts` (`B16`).
     - Delete DP leftover fields on `AggregationModels.cs` (`:128-130, :148`), delete `noise_applied` in `GovernedAggregationService.cs` (`:313-315, :346-348, :364`).
  3. **Delete** `src/Autheris.Application/Analytics/NoisyCacheKey.cs` (`184` lines) and its unit tests.
  4. `src/Autheris.Api/Errors/ApiErrorCodes.cs` & `src/Autheris.Api/Filters/ErrorSanitizingFilter.cs`:
     - Delete `PRIVACY_BUDGET_EXHAUSTED`.
  5. `src/Autheris.GraphQL/Types/AggregateGraphQlTypes.cs` & `MetricGraphQlTypes.cs`:
     - Delete the DP noise fields from GraphQL aggregation/metric types and mappers; regenerate `docs/reference/configuration.md` if `AnalyticsOptions` default is documented there.
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Analytics/GovernedAggregationServiceSecurityTests.cs`: `Suppressed_groups_count_hidden_by_default` `[S]`, `Audit_contains_filter_shape_without_literals_and_exposed_flag` `[S]`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~GovernedAggregation|FullyQualifiedName~Analytics" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ConfigurationReference|FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id"
  ```

---

#### Brief `W7-50-C` — `WP-SIMPL-50 PR C`: Startup Guards, OData `$batch` 405, Config-Sync Webhook Replay & Secret Policy (`C2, C3, C4, C8, C11, C15`)

- **Branch / Worktree:** `feat/simpl-50-pr-c-cleanup` in `/work/wt/simpl-50-pr-c-cleanup`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-50-review-remediation-v1-7-1.md` (Items `C2, C3, C4, C8, C11, C15`, `SEC-50-13`)
- **Target `src/` Files (Verified on `6de0553d`):**
  1. `src/Autheris.Api/Configuration/UnknownGatewayKeyGuard.cs` (`402` lines):
     - Item `C2`: Drop `|| entry.Value is null` at `:113` so null-bound keys of uninstalled modules or removed keys are not skipped.
  2. `src/Autheris.Api/Extensions/DependencyInjection/DataSourceDialectValidator.cs` (`56` lines) & `src/Autheris.Api/Extensions/DependencyInjection/GatewayStartupValidator.cs` (`872` lines):
     - Item `C3`: Use `static readonly FrozenSet<DatabaseDialect> Admitted` with `AdmittedDialects` derived from it; throw `ValidationException` naming the key when a connection entry is `null`; add null guard in `GatewayStartupValidator`.
  3. `src/Autheris.Api/Endpoints/ODataEndpoints.Query.cs` (`426` lines) & `ODataEndpoints.cs`:
     - Item `C4`: Remove redundant `IsWriteMethod` checks in `ODataEndpoints.Query.cs:39-43, :108-111`; map `GET /odata/v4/$batch` explicitly to return HTTP `405` with the batch-specific message.
  4. `src/Autheris.Api/Endpoints/VirtualFilterEndpoints.Seeding.cs` (`267` lines) & `src/Autheris.Application/VirtualFilters/VirtualFilterAdministrationService.cs` (`375` lines):
     - Item `C11` (`SEC-50-13`): Deduplicate config-sync webhooks by `vf:webhook:<sha256-of-body>` in `IDistributedClusterStateProvider` (24 h TTL) **only after** HMAC verification succeeds. Replayed body with a new delivery ID returns `202` without re-triggering; cluster store failure returns `503` without triggering.
  5. `src/Autheris.Infrastructure/Persistence/ConnectionSecretPolicy.cs` (`43` lines) & `src/Autheris.Infrastructure/Persistence/SqlConnectionFactory.cs` (`388` lines):
     - Item `C15`: Throw `SecurityException` in `ConnectionSecretPolicy` **without** setting `InnerException` (log only the exception type); set `_productionSecrets = !environment.IsDevelopment()` in `SqlConnectionFactory` (independent of `_requireOracleTcps`). *(Note: `ValidateGovernanceConnectionTls` is already wired in `GovernanceProviderGuard.cs:188`.)*
  6. `Directory.Build.props` (`:17`):
     - Item `C8`: Remove `CS0108` and `CA2012` from `<NoWarn>` and fix any resulting compiler/analyzer hits.
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Configuration/DialectConfigurationTests.cs`: `Null_connection_entry_fails_startup`.
  - `tests/Autheris.Tests.Unit/RestMcpHitlAndWebhookSecurityTests.cs`: `ConfigSyncWebhook_replayed_body_with_new_delivery_id_is_not_retriggered` `[S]`, `ConfigSyncWebhook_invalid_signature_does_not_store_hash` `[S]`, `ConfigSyncWebhook_store_failure_returns_503` `[S]`.
  - `tests/Autheris.Tests.Unit/Security/ConnectionPolicyTests.cs`: `Parse_failure_has_no_inner_exception` `[S]`, `Production_secrets_independent_of_oracle_tcps` `[S]`, `Governance_connection_without_tls_fails_startup_in_production` `[S]`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~DialectConfiguration|FullyQualifiedName~RestMcpHitlAndWebhookSecurity|FullyQualifiedName~ConnectionPolicy|FullyQualifiedName~UnknownGatewayKeyGuard" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id"
  ```

---

### 2.2 Group B — Usage Accounting per User, AD Group & Grant (`WP-SIMPL-42 PR 2..7`)

*(Note: `WP-SIMPL-42 PR 1` is already merged on `main` in `#598` `6c9e9ac1` — `src/Autheris.Application/Usage/GrantRef.cs`, `UsageAggregator.cs`, `UsageAttribution.cs`, `UsageRecorder.cs`, `UsageRequestScope.cs`, `UsageSeriesKey.cs`, `UsageGrantSeriesKey.cs`, `UsageAccountingOptions.cs` exist on `6de0553d`.)*

---

#### Brief `W7-42-2` — `WP-SIMPL-42 PR 2` (Single DDL Slot `0017_usage_accounting`): Usage Persistence, Dialect `UpsertAccumulate` & `UsageFlushWorker`

- **Branch / Worktree:** `feat/simpl-42-pr2-usage-storage` in `/work/wt/simpl-42-pr2-usage-storage`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-42-usage-accounting-users-groups.md` (§5.2 Steps 7–10, `SEC-SIMPL-21`, `SEC-USAGE-04..05`, `SEC-USAGE-09`)
- **DDL Slot:** **`0017_usage_accounting`** (runs after `0016_dbt_unique_id`).
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Infrastructure/Persistence/Migrations/Steps/{postgresql,sqlserver}/0017_usage_accounting.{sql,shape.json}`:
     - Create `USAGE_DAILY`, `USAGE_GRANT_DAILY`, `USAGE_GROUP_SET`, `USAGE_GRANT_REF`, `USAGE_TARIFF` with exact columns, PKs, and indexes from `WP-SIMPL-42` §5.2 Step 7.
     - `DROP TABLE CONSUMER_APP_DAILY; DROP TABLE CONSUMER_APP_ACTOR_DAILY;` (keep `CONSUMER_APP_QUERYNAME_DAILY`).
     - Update `StepSqlLintTests.cs` and `tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.{postgresql,sqlserver}.txt`.
  2. `src/Autheris.Infrastructure/Persistence/Dialects/GovernanceTable.cs` (`145` lines) & `IGovernanceSqlDialect.cs` (`45` lines), `PostgreSqlGovernanceDialect.cs` (`140` lines), `SqlServerGovernanceDialect.cs` (`246` lines):
     - Add `UsageDaily`, `UsageGrantDaily`, `UsageGroupSet`, `UsageGrantRef`, `UsageTariff` to `GovernanceTable` (`SEC-SIMPL-21`).
     - Add `string UpsertAccumulate(GovernanceTable table, ReadOnlySpan<string> keyColumns, ReadOnlySpan<string> setOnInsertColumns, ReadOnlySpan<string> counterColumns)` (PG `INSERT ... ON CONFLICT DO UPDATE`, SQL Server `MERGE ... WITH (HOLDLOCK)`).
  3. **Create** `src/Autheris.Application/Usage/IUsageRepository.cs` & `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Usage.cs`:
     - Implement `AccumulateAsync`, `QueryAsync` (closed `GroupBy` enum, never caller strings in SQL), `PurgeAsync`, `EraseUserAsync` (re-key to `"erased"` in one transaction), `GetTariffsAsync`, `AddTariffVersionAsync` (`LockRowsForUpdate`), `QueryConsumerAppDailyAsync`, and `QueryDistinctActorCountsAsync`.
  4. **Create** `src/Autheris.Infrastructure/Usage/UsageFlushWorker.cs`:
     - Flushes `UsageAggregator.Swap()` every `FlushIntervalSeconds`; on failure merges snapshot back into current generation up to `MaxFlushRetries` (`autheris_usage_dropped_total`, rate-limited log without SIDs); daily `PurgeAsync`; bounded 5s final flush on `StopAsync`.
- **Required Unit & Architecture Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Usage/UsageFlushWorkerTests.cs`: `Failed_flush_requeues_generation`, `Drops_after_max_retries_and_counts`, `Final_flush_on_stop`, `Purges_daily_with_retention`, `Logs_never_contain_user_sids` `[S]`.
  - `tests/Autheris.Tests.Architecture`: `GovernanceMigrationStepParityTests`, `MigrationShapeTests`, `DialectSqlGoldenTests`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~UsageFlushWorker|FullyQualifiedName~StepSqlLint" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~GovernanceMigrationStepParity|FullyQualifiedName~MigrationShape|FullyQualifiedName~DialectSql|FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id"
  ```

---

#### Brief `W7-42-3A` — `WP-SIMPL-42 PR 3a`: `UsageMeteringMiddleware`, Call-Site Migration & Old Recorder Removal

- **Branch / Worktree:** `feat/simpl-42-pr3a-metering-middleware` in `/work/wt/simpl-42-pr3a-metering-middleware`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-42-usage-accounting-users-groups.md` (§5.2c Step 9c, §5.3 Steps 11–14, `SEC-USAGE-01`, `SEC-USAGE-04`, `SEC-USAGE-05`, `SEC-USAGE-07`)
- **Depends On:** `W7-42-2` (`WP-SIMPL-42 PR 2`).
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Api/Middleware/UsageMeteringMiddleware.cs` & `CountingResponseBodyFeature.cs`:
     - Register in `GatewayApplicationBuilderExtensions.cs` at `:108` (replacing `FinOpsBudgetMiddleware` in the pipeline).
     - Skip `/health`, `/alive`, `/ready`, `/metrics`; for authenticated `SecurityPrincipalContext`, begin `UsageRequestScope` with `SecurityPrincipalContext` (`UserSid`, `TenantId`, `GroupSids`), `IIngressTransportContext.Current`, and `Caller`; count uncompressed response bytes via `CountingResponseBodyFeature`; complete in `finally`. Anonymous requests increment `autheris_usage_unattributed_total` (`SEC-USAGE-01`).
  2. Rewire `IConsumerUsageRecorder` call sites (`TableStreamEndpoints.cs`, `GovernedQueryStages.cs`, `GatewayExecutionService.cs`, `GovernedTreeQueryService.cs`, `CostAndQuotaMiddleware.cs`) + add `GovernedProcedureExecutionService.cs` (via partial helper) to `IUsageRecorder`, passing `DataSource = metadata.SourceName` and inline `SourceExecutionStats` (`DatabricksStatementExecutionClient.cs:380` `statement_id`).
  3. Add non-HTTP `UsageRequestScope` in `AsyncQueryJobBackgroundWorker.cs` (capture `SubmittedByGroupSids` at job submission in `AsyncQueryJobManager.cs`).
  4. Switch `LineageImpactAnalyzerService.cs` to `IUsageRepository`.
  5. **Delete** `IConsumerUsageRecorder.cs`, `ConsumerUsageRecorder.cs`, `ConsumerUsageFlushWorker.cs`, the daily/actor methods of `ConsumerAggregationStore.cs`, and the hard-coded default pseudonymization secret block in `GatewayGovernanceServiceExtensions.Audit.cs:59-100` (`SEC-USAGE-05`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Usage/UsageMeteringMiddlewareTests.cs`: `Counts_stream_and_pipewriter_bytes`, `Measures_duration`, `Skips_health_and_metrics`, `Restores_original_body_feature`, `Streaming_flush_semantics_unchanged`.
  - `tests/Autheris.Tests.Unit/Usage/UsageAttributionSecurityTests.cs` `[S]`: `Group_sids_only_from_security_context`, `Tenant_from_principal_not_from_header_or_query`, `Anonymous_requests_not_attributed`, `No_default_pseudonymization_secret_in_source`, `Metering_adds_no_await_on_hot_path`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~UsageMeteringMiddleware|FullyQualifiedName~UsageAttributionSecurity|FullyQualifiedName~UsageResilience|FullyQualifiedName~LineageImpact" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id"
  ```

---

#### Brief `W7-42-3B` — `WP-SIMPL-42 PR 3b`: Grant Linkage from `TableAccessDecision.Grants` & Data-Access Audit `Grants` Array

- **Branch / Worktree:** `feat/simpl-42-pr3b-grant-linkage` in `/work/wt/simpl-42-pr3b-grant-linkage`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-42-usage-accounting-users-groups.md` (§5.2d Step 9f, `OD-29`, `OD-42`, `SEC-USAGE-11`)
- **Depends On:** `W7-42-3A` (`WP-SIMPL-42 PR 3a`). (`WP-SIMPL-46` PDP `Grants` list is already on `main`.)
- **Target `src/` Files:**
  1. `src/Autheris.Application/Sql/Execution/GovernedQueryStages.cs` & `IUsageRecorder` call sites:
     - Map every `DecisionGrantRef` in the `AccessDecision`/`TableAccessDecision.Grants` list to `GrantRef(grantId, validFrom, validUntil)` and pass into `UsageFact.Grants`.
     - Assert in `UsageRecorder`: an `Allow` fact with an empty grant list or a grant where `ValidUntil < now` or `ValidFrom > now` records `GrantRef.Invalid` and increments `autheris_usage_allow_without_grant_total` (`SEC-USAGE-11`).
     - Include `Grants` (`[{ grantId, grantValidFrom, grantValidUntil }]`) in the data-access audit entry written by `RecordTableReadAsync`.
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Usage/UsageGrantAttributionTests.cs` & `UsageGrantSecurityTests.cs` `[S]`: `Allow_fact_carries_grant_from_decision`, `Allow_fact_counts_every_contributing_grant_in_full_and_user_total_once`, `Owner_access_attributed_to_owner_grant_id`, `Allow_fact_without_grant_recorded_invalid_and_counted`, `Deny_fact_has_no_grants`, `Expired_grant_on_allow_is_flagged_invalid`, `Grant_id_never_taken_from_request`, `Data_access_audit_entry_contains_grant_fields`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~UsageGrant" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-42-4A` — `WP-SIMPL-42 PR 4a`: Versioned Tariffs, `UsageTariffCalculator`, `UsageTariffService` & Tariff Admin API

- **Branch / Worktree:** `feat/simpl-42-pr4a-tariffs` in `/work/wt/simpl-42-pr4a-tariffs`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-42-usage-accounting-users-groups.md` (§5.2b Step 9b, `OD-29`, `SEC-USAGE-10`)
- **Depends On:** `W7-42-2` (`WP-SIMPL-42 PR 2`).
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Application/Usage/UsageTariffVersion.cs`, `UsageTariffCalculator.cs`, `IUsageTariffService.cs`, `UsageTariffService.cs`:
     - Implement the linear tariff formula of §5.2b Step 9b (`computeSeconds`, `base_per_query`, `per_row`, `per_mib`, `per_compute_second`, `per_analysis_cost`, `source_cost_factor`), lookup order `(t, s) -> (t, *) -> (*, s) -> (*, *)` for `valid_from <= d < valid_until`.
     - Enforce immutability and future-only `valid_from >= today UTC + 1 day` (`D-42k`); seed `Gateway:UsageAccounting:InitialTariffs` only when `USAGE_TARIFF` is empty for `(tenant, data_source)`.
  2. **Create** `src/Autheris.Api/Endpoints/UsageEndpoints.Tariffs.cs`:
     - `GET /api/v1/usage/tariffs`, `GET /api/v1/usage/tariffs/{dataSource}/versions` (`GatewayPolicies.UsageAdmin`), `POST /api/v1/usage/tariffs/{dataSource}/versions` (tenant tariffs: `TenantAdmin`; `*` wildcard: `ClusterAdmin` only).
     - Write `IAuditWriter.RecordRequiredAsync` (`UsageTariffVersionAdded`) before commit; fail-closed `503` on audit failure (`SEC-USAGE-10`). `PUT`/`DELETE` return `405`.
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Usage/UsageTariffCalculatorTests.cs`, `UsageTariffServiceTests.cs`, `UsageTariffSecurityTests.cs` `[S]`: all 12 calculator tests + `Tenant_admin_cannot_add_wildcard_tariff`, `Tenant_admin_cannot_add_tariff_for_other_tenant`, `Consumer_gets_403_on_tariff_routes`, `Put_and_delete_tariff_return_405`, `Retroactive_tariff_rejected_for_all_roles`, `Tariff_change_audited_and_fails_closed_503_without_audit`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~UsageTariff" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-42-4B` — `WP-SIMPL-42 PR 4b`: `UsageReportService`, Report & Grant Endpoints, CSV Writer & Group Name Resolver

- **Branch / Worktree:** `feat/simpl-42-pr4b-usage-reports` in `/work/wt/simpl-42-pr4b-usage-reports`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-42-usage-accounting-users-groups.md` (§5.2d Step 9g, §5.4 Steps 15–18, `SEC-USAGE-02`, `SEC-USAGE-03`, `SEC-USAGE-06`, `SEC-USAGE-09`)
- **Depends On:** `W7-42-4A` (`WP-SIMPL-42 PR 4a`). (`WP-SIMPL-43 PR 5a` `IDENTITY_GROUPS.object_sid` is already merged on `main`.)
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Application/Usage/IUsageReportService.cs`, `UsageReportService.cs`, `UsageCsvWriter.cs`, `IUsageGroupNameResolver.cs`, `UsageGroupNameResolver.cs`:
     - Tenant always bound from `SecurityPrincipalContext.TenantId` (`ClusterAdmin` must pass explicit `tenant=`, audited).
     - `UsageGroupNameResolver` resolves `GroupDisplayNames` option -> `IIdentityStoreRepository.GetGroupDisplayNameBySidAsync(TenantId, Sid, ct)` (`IDENTITY_GROUPS.object_sid` with `SidFormat.IsCanonical`) -> raw SID; 10-min bounded cache (`10,000` entries).
     - `UsageCsvWriter`: RFC 4180 + formula-injection prefix `'` on cells starting with `=`, `+`, `-`, `@`, `\t`, `\r` (`SEC-USAGE-06`); `# multi-counted` header comment on group reports.
  2. **Create** `src/Autheris.Api/Endpoints/UsageEndpoints.cs`:
     - Map `/api/v1/usage/me`, `/users`, `/groups`, `/tenant`, `/tables/{tableId}`, `/sources`, `/applications`, `/grants/{grantId}`, and `DELETE /api/v1/usage/users/{userSid}` (`EraseUserAsync`).
     - Every report read except `/me` calls `IAuditWriter.RecordRequiredAsync` (`UsageReportRead`) before writing response body; audit failure returns `503 USAGE_AUDIT_UNAVAILABLE` (`SEC-USAGE-03`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Usage/UsageReportAuthorizationTests.cs` `[S]`, `UsageReportAuditTests.cs` `[S]`, `UsageCsvSecurityTests.cs` `[S]`, `UsageGroupNameResolverTests.cs`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~UsageReport|FullyQualifiedName~UsageCsv|FullyQualifiedName~UsageGroupName" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-42-5` — `WP-SIMPL-42 PR 5`: Complete FinOps Deletion, Removed Routes 404 & Removed Config Keys Guard

- **Branch / Worktree:** `feat/simpl-42-pr5-delete-finops` in `/work/wt/simpl-42-pr5-delete-finops`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-42-usage-accounting-users-groups.md` (§5.5 Steps 19–21, `SEC-SIMPL-30`, `SEC-SIMPL-31`)
- **Depends On:** `W7-42-3A` and `W7-42-4B`.
- **Target `src/` & `docs/` Files:**
  1. **Delete** `src/Autheris.Application/FinOps/Interfaces/IFinOpsAccountingService.cs`, `src/Autheris.Application/FinOps/Services/FocusCostAccountingService.cs`, `src/Autheris.Api/Middleware/FinOpsBudgetMiddleware.cs`, `src/Autheris.Api/Endpoints/FinOpsEndpoints.cs`, `src/Autheris.Domain/Model/FinOpsModels.cs`, `src/Autheris.Domain/Options/Resilience/FinOpsOptions.cs`, and `docs/features/f-ai-08-focus-finops-accounting.md`.
  2. Remove `GatewayOptions.FinOps`, `GatewaySections.FinOps`, `ApiErrorCodes.FinOpsBudgetExceeded`, `X-FinOps-Budget-Warning`, and FinOps DI/endpoint registrations.
  3. Add `Gateway:FinOps` and all child keys to `UnknownGatewayKeyGuard.cs` (`SEC-SIMPL-31`) and add `Autheris.Application.FinOps` + `/api/v1/finops/*` routes to `DeletedFeaturesArchitectureTests` / `RemovedRoutesTests` (`SEC-SIMPL-30`).
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~UnknownGatewayKeyGuard|FullyQualifiedName~RemovedConfigKeys" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~DeletedFeatures|FullyQualifiedName~DocsIntegrity|FullyQualifiedName~Options|FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id"
  ```

---

#### Brief `W7-42-7` — `WP-SIMPL-42 PR 7`: Late Source-Cost Collector Framework & `DatabricksSourceCostCollector`

- **Branch / Worktree:** `feat/simpl-42-pr7-source-cost-collector` in `/work/wt/simpl-42-pr7-source-cost-collector`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-42-usage-accounting-users-groups.md` (§5.2c Steps 9d–9e, `SEC-USAGE-12`)
- **Depends On:** `W7-42-3A`.
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Application/Usage/ISourceCostCollector.cs`, `PendingSourceQuery.cs`, `SourceCostSample.cs`.
  2. **Create** `src/Autheris.Infrastructure/Usage/SourceCostCollectorWorker.cs` & `DatabricksSourceCostCollector.cs`:
     - Bounded queue (`50,000` entries, never blocks request path); every 5 minutes batches up to 100 `statement_id`s per call to `GET /api/2.0/sql/history/queries?include_metrics=true` using the tenant's Databricks principal (`WP-SIMPL-37`), bounds `metrics.total_time_ms` (`[0, 1e12]`), and accumulates only `source_*` counters into `USAGE_DAILY`. Drops samples older than 7 days (`autheris_usage_source_cost_expired_total`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Usage/SourceCostCollectorWorkerTests.cs`, `DatabricksSourceCostCollectorTests.cs`, `SourceCostCollectorSecurityTests.cs` `[S]`: `Collector_uses_tenant_principal_only`, `Collector_response_values_bounded`, `Collector_failure_never_affects_requests`, `Source_query_ids_not_in_logs_or_metrics`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~SourceCostCollector|FullyQualifiedName~DatabricksSourceCost" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-42-6` — `WP-SIMPL-42 PR 6` (Optional Sub-Package `42-Q`, Owner-Gated): Usage Quotas per User, Group & Tenant

- **Branch / Worktree:** `feat/simpl-42-pr6-usage-quotas` in `/work/wt/simpl-42-pr6-usage-quotas`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-42-usage-accounting-users-groups.md` (§5.6 Steps 22–23, `SEC-USAGE-08`)
- **Depends On:** `W7-42-5` + explicit owner go.
- **Target `src/` Files:**
  1. Extend `UsageAccountingOptions.cs` with `Quotas : List<UsageQuota>` and `QuotaStalenessSeconds = 120`.
  2. **Create** `src/Autheris.Application/Usage/IUsageQuotaService.cs` & `UsageQuotaService.cs`, wired in `UsageMeteringMiddleware.cs` before `next`: evaluate all applicable quotas (user, caller groups, billing group, tenant), most restrictive wins; exceeded -> `429 USAGE_QUOTA_EXCEEDED` with `Retry-After`; stale totals (`> QuotaStalenessSeconds`) fail closed with `503 USAGE_QUOTA_UNAVAILABLE` only for callers subject to a quota (`SEC-USAGE-08`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Usage/UsageQuotaSecurityTests.cs` `[S]`: `Exceeded_returns_429_code_scope_and_retry_after`, `Most_restrictive_group_quota_applies`, `Stale_totals_fail_closed_503_for_quota_subjects`, `Callers_without_quota_unaffected_by_store_outage`, `Usage_me_exempt_from_quota`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~UsageQuota" -p:AutherisIncludeTestAuth=true
  ```

---

### 2.3 Group C — External Catalog, Consent Decision Pipeline & Signed Grants (`WP-SIMPL-47 PR 3..7`)

*(Note: `WP-SIMPL-47 PR 1` is briefed in Wave 4 `W4-1` (`0014_consent_api`), and `WP-SIMPL-47 PR 2` (`2a/2b` `IGrantWriter` & hash-chained `GRANT_RECORDS`) is briefed in Wave 6 `W6-1A/1B`.)*

---

#### Brief `W7-47-3` — `WP-SIMPL-47 PR 3`: `IConsentDecisionPipeline`, `ExternalDecisionGuard`, `GrantValidityPolicy` & ITSM Leftover Removal

- **Branch / Worktree:** `feat/simpl-47-pr3-decision-pipeline` in `/work/wt/simpl-47-pr3-decision-pipeline`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-47-external-catalog-and-consent-api.md` (Steps 3, 6, 7; `SEC-SIMPL-47-01..04`, `SEC-SIMPL-47-08`)
- **Depends On:** Wave 6 `W6-1B` (`WP-SIMPL-47 PR 2b`).
- **Target `src/` Files (Verified on `6de0553d`):**
  1. **Create** `src/Autheris.Application/ConsentRequests/Decisions/IConsentDecisionPipeline.cs`, `ConsentDecisionCommand.cs`, `ConsentDecisionPipeline.cs`, and `ExternalDecisionGuard.cs`:
     - Tenant-scoped request load (absent/cross-tenant -> `404 CONSENT_REQUEST_NOT_FOUND`), optimistic concurrency check (`ExpectedVersion` -> `PRECONDITION_FAILED`), mode check (`Internal` requires human data owner/delegate + four-eyes/self-approval; `External` requires enabled service principal whose `client_id` maps to `request.ExternalSystemId`, else `404` for foreign system and `WORKFLOW_MODE_MISMATCH` for mode cross-call, `SEC-SIMPL-47-01`).
     - `ExternalDecisionGuard`: fail-closed checks for at least one approver, SCIM directory resolution when `require_resolvable_approver`, self-approval prohibition across SID/UPN/externalId/SCIM group membership/requesting system principal (`SEC-SIMPL-47-02`), distinct approvers, and `decidedAt` in `[request.CreatedAt, now + 5m]`.
  2. **Create** `src/Autheris.Application/Governance/Grants/GrantValidityPolicy.cs`:
     - Options `Gateway:Governance:Grants:MaxDuration`, `DefaultDuration`, and `DeclarativeMaxDuration` (ISO-8601, mandatory at startup, `DefaultDuration <= MaxDuration`).
     - Compute `EffectiveMax = min(route.max_duration_days, tenant MaxDuration, IndividualGrantOptions ceiling)`, enforce `valid_from = max(decidedAt, requested_valid_from)` and `valid_until <= min(requested, valid_from + EffectiveMax)`.
     - Add `DateTimeOffset ValidUntil` to `CachedConsentEnvelope` so L1/L2 cache hits past `ValidUntil` (or envelopes missing `ValidUntil`) are treated as cache misses (`SEC-SIMPL-47-04`).
  3. `src/Autheris.Application/ConsentRequests/ConsentRequestService.cs` (`362` lines), `src/Autheris.Infrastructure/Persistence/ConsentApprovalPolicy.cs` (`190` lines), and `src/Autheris.GraphQL/Types/Mutation.Consent.cs` (`963` lines — keep `<= 1000` lines):
     - Route GraphQL `ApproveConsentRequestAsync` / `RejectConsentRequestAsync` through `IConsentDecisionPipeline`.
     - Delete ITSM overloads, `ConsentApprovalPolicy.IsItsmActor`, `itsmApproverAccount` parameters, `GetConsentRequestByTicketIdAsync`, and `IConsentRequestDispatcher`; replace the German message in `ConsentApprovalPolicy.cs:20` with English + `FOUR_EYES_SAME_APPROVER`.
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/ConsentRequests/Decisions/ConsentDecisionPipelineTests.cs`, `ExternalDecisionGuardTests.cs` `[S]`, `GrantValidityPolicyTests.cs`, `ConsentCacheExpiryTests.cs` `[S]`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ConsentDecisionPipeline|FullyQualifiedName~ExternalDecisionGuard|FullyQualifiedName~GrantValidityPolicy|FullyQualifiedName~ConsentCacheExpiry|FullyQualifiedName~ConsentApproval" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ConsentDecisionResolver|FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id"
  ```

---

#### Brief `W7-47-4` — `WP-SIMPL-47 PR 4`: Workflow Routing, `ConsentEvent` Outbox, Webhook Dispatcher & `GrantExpiryHostedService`

- **Branch / Worktree:** `feat/simpl-47-pr4-routing-events-expiry` in `/work/wt/simpl-47-pr4-routing-events-expiry`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-47-external-catalog-and-consent-api.md` (Steps 5, 8, 9; `SEC-SIMPL-47-06`, `SEC-SIMPL-47-11`)
- **Depends On:** `W7-47-3`.
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Application/ConsentRequests/Routing/IConsentWorkflowRouter.cs`, `ConsentWorkflowRouter.cs`, and `src/Autheris.Api/Endpoints/ConsentWorkflowAdminEndpoints.cs`:
     - Route resolution (`DATA_SOURCE` -> `DATA_OWNER` -> `TENANT` -> `Internal`; conflicting owner routes fall back to `Internal` with `CONSENT_ROUTE_CONFLICT`).
     - Admin endpoints `PUT/GET/DELETE /api/v1/admin/consent/routes/{scopeKind}/{scopeRef}`, `PUT/GET /api/v1/admin/consent/external-systems/{id}`, `POST .../{id}:disable` (atomically re-routes open undecorated requests to `Internal` with `CONSENT_REQUEST_REROUTED`), and `POST /api/v1/admin/consent/requests:reroute` (refuses internal requests with a recorded first four-eyes step: `REROUTE_NOT_ALLOWED`, `SEC-SIMPL-47-11`).
  2. **Create** `src/Autheris.Application/ConsentRequests/Events/ConsentEventTypes.cs`, `IConsentEventOutbox.cs`, `ConsentWebhookDispatcherHostedService.cs`, and `src/Autheris.Infrastructure/Persistence/GovernanceRepository.ConsentEvents.cs`:
     - CloudEvents 1.0 thin payload (`tenantId`, `requestId|grantId`, `objectId`, `status`, `version`, `workflowMode`, `occurredAt`, `links` — zero justification text, column values, or approver identities, `SEC-SIMPL-47-06`).
     - Signed with `WebhookSignature.Sign` (`X-Autheris-Signature-256`, `X-Autheris-Timestamp`, `X-Autheris-Event-Id`), delivered through `WebhookDeliveryClient` egress guard with lease-based retry and dead-lettering after `MaxAttempts = 12`.
  3. **Create** `src/Autheris.Application/Governance/Grants/GrantExpiryHostedService.cs` and **delete** `ConsentRecertificationHostedService.cs`:
     - Every 5 min (lease-guarded): emit `grant.expiring` at `valid_until - lead`, write `EXPIRED` grant records past `valid_until`, and expire pending requests older than `request_timeout_days`.
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/ConsentRequests/Routing/ConsentWorkflowRouterTests.cs`, `ConsentEventPayloadTests.cs` `[S]`, `GrantExpiryHostedServiceTests.cs`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ConsentWorkflowRouter|FullyQualifiedName~ConsentEventPayload|FullyQualifiedName~GrantExpiryHostedService" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-47-5` — `WP-SIMPL-47 PR 5`: REST `/api/v1/consent` Endpoints, Permissions, `Idempotency-Key`/`If-Match` & Proof API

- **Branch / Worktree:** `feat/simpl-47-pr5-consent-rest-api` in `/work/wt/simpl-47-pr5-consent-rest-api`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-47-external-catalog-and-consent-api.md` (Steps 10, 11; `SEC-SIMPL-47-01..05`, `SEC-SIMPL-47-08..10`)
- **Depends On:** `W7-47-4`.
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Api/Security/ExternalApiPermissions.cs` & `ExternalSystemRequirementHandler.cs`, and register policies `CatalogRead`, `CatalogReadAdmin`, `ConsentRead`, `ConsentDecide`, `GrantRevoke`, `GrantVerify` in `GatewayPolicies.cs`.
  2. **Create** `src/Autheris.Api/Endpoints/ConsentApiEndpoints.cs` (` ConsentApiEndpoints.Requests.cs` and `ConsentApiEndpoints.Grants.cs` if needed to stay well under `1000` lines):
     - `GET /api/v1/consent/requests`, `GET /requests/{id}` (with `ETag: "r{version}"`, `404` outside caller visibility), `POST /requests` (users/agents only; service principals `403`), `POST /requests/{id}/decision` (mandatory `Idempotency-Key` -> `428 IDEMPOTENCY_KEY_REQUIRED` and `If-Match` -> `428 PRECONDITION_REQUIRED` / `412 PRECONDITION_FAILED`, body hash bound in `IIdempotencyStore` -> `422 IDEMPOTENCY_KEY_REUSED`, `SEC-SIMPL-47-05`), `POST /requests/{id}/withdraw`, `GET /events?since={seq}` (`410 CURSOR_EXPIRED` when older than retention), `GET /grants`, `GET /grants/{id}`, `POST /grants/{id}/revoke`, and `/grant-records/proof`, `/grant-records/{seq}`, `POST /grant-records/verify`, `GET /grant-records/keys`.
     - Partition rate limits by `(tenant, client_id|sid)` (`600/min` reads, `60/min` decisions/revokes, `120/min` proof -> `429 RATE_LIMITED` with `Retry-After`, `SEC-SIMPL-47-10`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Consent/ConsentApiEndpointSecurityTests.cs` `[S]`: `External_decision_from_unregistered_system_returns_404`, `Replay_with_same_idempotency_key_creates_no_second_grant`, `SameKeyDifferentBody_Returns422`, `MissingIfMatch_Returns428`, `StaleIfMatch_Returns412`, `DelegatedUserToken_WithDecideScope_Returns403`, `RateLimit_Decisions_Returns429`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ConsentApiEndpointSecurity" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-47-6` — `WP-SIMPL-47 PR 6`: External Catalog Read (`/api/v1/catalog/objects` & `/changes`), Change Indexer & OpenAPI Contract

- **Branch / Worktree:** `feat/simpl-47-pr6-external-catalog-api` in `/work/wt/simpl-47-pr6-external-catalog-api`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-47-external-catalog-and-consent-api.md` (Steps 12, 13; `SEC-SIMPL-47-07`, `SEC-SIMPL-47-12`)
- **Depends On:** Wave 4 `W4-1` (`WP-SIMPL-47 PR 1`) and `W4-2` (`WP-SIMPL-41 PR 3` `IDiscoveryService`), plus `W7-47-5`.
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Application/Catalog/External/ExternalCatalogReader.cs` & `CatalogChangeIndexerHostedService.cs`, plus `src/Autheris.Infrastructure/Persistence/GovernanceRepository.CatalogChanges.cs`.
  2. **Create** `src/Autheris.Api/Endpoints/CatalogApiEndpoints.External.cs` (do **not** add to `CatalogApiEndpoints.cs` which is already `951` lines):
     - `GET /api/v1/catalog/objects`, `GET /api/v1/catalog/objects/{id}`, and `GET /api/v1/catalog/changes?since={seq}` with strong caller-specific `ETag` / `If-None-Match` `304`.
     - `SEC-SIMPL-47-07`: Hidden objects (`DiscoveryHidden`) are included with `hidden: true` **only** for (a) an app-only token of a registered, enabled external system of the tenant with `CatalogReadAdmin`, or (b) a user who is `GovernanceAdmin` of the tenant or data owner/delegate of that object. A delegated user token with `scp = Autheris.Catalog.ReadAdmin` **never** reveals hidden objects.
  3. **Create** `src/Autheris.Api/Contracts/autheris-external-v1.openapi.json` (embedded resource served at `GET /api/v1/openapi/external.json`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Catalog/ExternalCatalogApiTests.cs` `[S]` & `OpenApiContractTests.cs`: `HiddenObjectsAndColumns_Excluded_ForCatalogRead`, `Hidden_Included_WithFlags_ForAppOnlyExternalSystemWithCatalogReadAdmin`, `DelegatedReadAdminScope_DoesNotRevealHidden`, `DelegatedToken_DataOwner_SeesOwnHiddenObjectOnly`, `IfNoneMatch_Returns304`, `ChangesFeed_HidingObject_EmitsRemoved`, `CrossTenant_ObjectId_Returns404`, `Document_Matches_Mapped_Routes_And_Methods`, `Dto_Schemas_Match_Document`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ExternalCatalogApi|FullyQualifiedName~OpenApiContract" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-47-7` — `WP-SIMPL-47 PR 7`: Access-Profile Assignment Approval (`OD-32`), Owner Grants (`OD-34`) & Approval Effect Preview (`OD-43`)

- **Branch / Worktree:** `feat/simpl-47-pr7-profile-owner-grants` in `/work/wt/simpl-47-pr7-profile-owner-grants`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-47-external-catalog-and-consent-api.md` (Steps 9a, 9b, 9c; `SEC-AZ-04`, `SEC-AZ-12`, `SEC-AZ-19`)
- **Depends On:** `W7-47-5` and `W7-48-6` (`WP-SIMPL-48 PR 6` `IGrantEffectPreview`).
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Application/ConsentRequests/ProfileAssignments/ProfileAssignmentRequestService.cs` & `ProfileGrantMaterializer.cs`:
     - Reject role assignees with `400 ROLE_ASSIGNEE_UNSUPPORTED` (`OD-30`).
     - Create `request_kind = PROFILE_ASSIGNMENT` routed through `IConsentDecisionPipeline`; in `Internal` mode create `PROFILE_ASSIGNMENT_ITEMS` per profile item and distinct data owner of matched objects; on approval materialize one grant record per approved profile item (`grant_source = ACCESS_PROFILE`) through `IGrantWriter`.
  2. **Create** `src/Autheris.Application/Governance/Grants/OwnerGrantService.cs`:
     - Hook every write path of `IDataOwnershipRepository` (including dbt/catalog owner ingestion) to write/revoke an owner grant record (`grant_source = OWNER`, `Read`, all columns `Clear`, `valid_until = null` while ownership is active) in the same transaction via `IGrantWriter` (`SEC-AZ-12`).
  3. Wire `IGrantEffectPreview.PreviewAsync` onto `GET /api/v1/consent/requests/{id}` (`effectPreview`) and GraphQL `ConsentRequest` type (`SEC-AZ-19`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/ConsentRequests/ProfileAssignments/ProfileAssignmentTests.cs` `[S]`, `EffectPreviewTests.cs` `[S]`, `tests/Autheris.Tests.Unit/Governance/Grants/OwnerGrantTests.cs` `[S]`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ProfileAssignment|FullyQualifiedName~EffectPreview|FullyQualifiedName~OwnerGrant" -p:AutherisIncludeTestAuth=true
  ```

---

### 2.4 Group D — Row Policies, Dynamic Coverage & Per-Cell Masking (`WP-SIMPL-48 PR 1..6`)

*(Verified on `6de0553d`: none of `WP-SIMPL-48 PR 1..6` is merged on `main` yet; all 6 slices are briefed below.)*

---

#### Brief `W7-48-1` — `WP-SIMPL-48 PR 1`: Row-Policy Typed Parameters, `Live|KeySet` Mode & Four-Eyes SQL Policy Approval

- **Branch / Worktree:** `feat/simpl-48-pr1-row-policy-params` in `/work/wt/simpl-48-pr1-row-policy-params`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-48-row-policies-dynamic-coverage.md` (§5 Step 1, `SEC-AZ-15`)
- **DDL Step:** None in PR 1 (persistence DDL is in `W7-48-2` `0018_consent_row_scopes`).
- **Target `src/` Files:**
  1. `src/Autheris.Domain/Model/VirtualFilterModels.cs` (`535` lines):
     - Add `RowPolicyParameterType { String, Int64, Decimal, Guid, Date }`, `RowPolicyParameter(string Name, RowPolicyParameterType Type, bool IsList = false, int MaxValues = 100)`, `UncoveredPolicy { Deny, Skip }`, `RowPolicyEvaluationMode { Live, KeySet }`, and extend `VirtualFilter` with `Parameters`, `EvaluationMode`, `KeySetMaxAge`, `Status` (`PendingApproval | Active`), `ApprovedBySid`, `ApprovedAt`.
  2. `src/Autheris.Application/VirtualFilters/VirtualFilterAdministrationService.cs` (`375` lines) & `src/Autheris.Api/Endpoints/VirtualFilterEndpoints.Crud.cs` (`386` lines):
     - Enforce parameter binding (`:name` in structured `Where` and SQL definitions, never inlined; invalid/missing/over-long parameters rejected on save and fail closed with `ROW_POLICY_PARAMETER_INVALID` at decision time).
     - Enforce cross-source `KeySet` rule (`ROW_POLICY_CROSS_SOURCE_REQUIRES_KEYSET`).
     - Enforce SQL policy four-eyes approval (`SEC-AZ-15`): authored only by `GovernanceAdmin`, `PendingApproval` until a data owner/delegate of **every** referenced table (extracted via AST parser) approves, and author `!=` approver.
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Policy/RowScopes/RowPolicyParameterTests.cs` `[S]`, `RowPolicyApprovalTests.cs` `[S]`, `RowPolicyEvaluationTests.cs` `[S]`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~RowPolicyParameter|FullyQualifiedName~RowPolicyApproval|FullyQualifiedName~RowPolicyEvaluation" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-48-2` — `WP-SIMPL-48 PR 2` (Single DDL Slot `0018_consent_row_scopes`): `GrantRowScopeEntry` & Removal of Subquery/Free-SQL Filters

- **Branch / Worktree:** `feat/simpl-48-pr2-grant-row-scopes` in `/work/wt/simpl-48-pr2-grant-row-scopes`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-48-row-policies-dynamic-coverage.md` (§5 Steps 2 & 7, `WP-SIMPL-52` §6 DDL for `consent_row_scopes` and `virtual_filters`)
- **DDL Slot:** **`0018_consent_row_scopes`** (runs after `0017_usage_accounting`).
- **Depends On:** `W7-48-1`.
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Infrastructure/Persistence/Migrations/Steps/{postgresql,sqlserver}/0018_consent_row_scopes.{sql,shape.json}`:
     - Create `CONSENT_ROW_SCOPES(tenant_id, consent_id, entry_no, policy_name, parameters_json, column_map_json, uncovered, PK(tenant_id, consent_id, entry_no))` and alter `VIRTUAL_FILTERS` to add `parameters_json`, `evaluation_mode`, `key_set_max_age_seconds`, `status`, `approved_by_sid`, `approved_at`.
     - Convert expressible `SubqueryCorrelated` consent filters into generated policies (`migrated-<consentId>-<n>`) and revoke unconvertible ones with reason `OD38_ROW_FILTER_NOT_CONVERTIBLE`; clear `AccessProfile.RowFilterPredicate`.
  2. Add `GrantRowScopeEntry` to `Consent` and `AccessProfile` items; delete `RowFilterType.SubqueryCorrelated`, `SubqueryJoinHop`, `AdditionalHops`, `RowFilterSubqueryStrategy`, `AdvancedRlsPredicateBuilder`, `AdvancedRlsFilterGenerator`, `TypedRowFilterBuilder`, `RowFilterSqlBuilder`, `RowFilterEvaluationResolver`, and `AccessProfile.RowFilterPredicate` / `RowPolicyFactory.FromAccessProfile`.
- **Required Unit & Architecture Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Policy/RowScopes/RowPolicyMigrationTests.cs` `[S]`.
  - `tests/Autheris.Tests.Architecture/AuthorizationArchitectureTests.cs`: `No_free_sql_predicate_on_grant_models` `[S]`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~RowPolicyMigration|FullyQualifiedName~StepSqlLint" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~AuthorizationArchitecture|FullyQualifiedName~GovernanceMigrationStepParity|FullyQualifiedName~MigrationShape|FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id"
  ```

---

#### Brief `W7-48-3` — `WP-SIMPL-48 PR 3`: `RowScopeCompiler` AST Builder, Parameter Merging, Hard Policies & Composite Keys

- **Branch / Worktree:** `feat/simpl-48-pr3-row-scope-compiler` in `/work/wt/simpl-48-pr3-row-scope-compiler`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-48-row-policies-dynamic-coverage.md` (§5 Step 3, `SEC-AZ-15`, `SEC-AZ-16`)
- **Depends On:** `W7-48-2`.
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Application/Policy/RowScopes/RowScopeCompiler.cs` & wire into `src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs` (`309` lines):
     - Produces typed AST predicates (never raw SQL strings).
     - ANDs entries within a grant (`Uncovered = Deny` drops the grant for that object; `Skip` drops the entry); ORs across contributing grants (unfiltered grant = `TRUE`; merges parameter lists for single-entry grants sharing policy + column map up to `MaxValues`).
     - ANDs hard row policies (`VirtualFilterAccessProfile` bindings for user or effective groups; `Uncovered = Deny` is an explicit deny `HARD_ROW_POLICY_UNCOVERED`; applies to owner grants and `OwnRows` writes too; rejects role grantees).
     - Compiles composite keys pair-wise (`Live` -> `EXISTS`; `KeySet` -> row-value `IN` on PG/Oracle/Databricks/DuckDB and `EXISTS (SELECT 1 FROM (VALUES ...) v WHERE ...)` on SQL Server/Ansi; `NULL` key never matches).
- **Required Unit & Architecture Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Policy/RowScopes/RowScopeCompilerTests.cs` `[S]` (all 10 tests from §6.1) + `AuthorizationArchitectureTests.Row_scopes_built_only_by_RowScopeCompiler` `[S]`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~RowScopeCompiler" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~AuthorizationArchitecture"
  ```

---

#### Brief `W7-48-4` — `WP-SIMPL-48 PR 4`: Per-Cell Column State (`CASE WHEN` Compilation & Streaming Evaluator)

- **Branch / Worktree:** `feat/simpl-48-pr4-per-cell-column-state` in `/work/wt/simpl-48-pr4-per-cell-column-state`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-48-row-policies-dynamic-coverage.md` (§5 Step 4, `SEC-AZ-18`)
- **Depends On:** `W7-48-3`.
- **Target `src/` Files:**
  1. Extend `ColumnAccessLevel` with `RowDependent` and per-signature scope metadata on `AccessDecision`.
  2. Update the governed query builder and `StreamingRowFilterAstEvaluator`:
     - Group contributing grants by column signature. Single signature -> plain `WHERE <union>`, no `CASE`.
     - Multiple signatures -> compute each signature's scope flag once (`LEFT JOIN (SELECT DISTINCT <keys> FROM ...) f_i` for `Live`, bound predicate for `KeySet`/simple) and project `CASE WHEN <clear flags> THEN col WHEN <masked flags> THEN mask(col) ELSE NULL END` with `WHERE <OR of all flags>`.
     - Treat any column that is `Mask` or absent in **any** contributing signature as `Mask` for `WHERE`/`JOIN`/`GROUP BY`/`ORDER BY`/aggregate guards (`D-48d`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Policy/RowScopes/PerCellColumnStateTests.cs` `[S]`: `Single_signature_compiles_without_case`, `Clear_only_in_rows_of_clear_grant`, `Column_outside_covering_grants_is_null`, `Flag_join_never_duplicates_rows`, `Masked_in_any_signature_counts_as_masked_for_predicates_and_aggregates`, `Randomized_no_clear_value_outside_clear_scope` (1,000 seeded cases).
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~PerCellColumnState" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-48-5` — `WP-SIMPL-48 PR 5` (Single DDL Slot `0019_pattern_grants`): Dynamic Object Coverage, Catalog Epoch Cache Key & `PatternCoverageMonitor`

- **Branch / Worktree:** `feat/simpl-48-pr5-dynamic-coverage` in `/work/wt/simpl-48-pr5-dynamic-coverage`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-48-row-policies-dynamic-coverage.md` (§5 Steps 5 & 7, `WP-SIMPL-52` §6 DDL for `consents` pattern columns & `consent_approving_owners`, `SEC-AZ-07`, `SEC-AZ-17`)
- **DDL Slot:** **`0019_pattern_grants`** (runs after `0018_consent_row_scopes`).
- **Depends On:** `W7-48-4` and `W7-47-3`.
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Infrastructure/Persistence/Migrations/Steps/{postgresql,sqlserver}/0019_pattern_grants.{sql,shape.json}`:
     - Alter `CONSENTS`: make `table_id` nullable; add `object_pattern VARCHAR(512) NULL`, `data_source VARCHAR(128) NULL`, `column_set_mode VARCHAR(16) NOT NULL DEFAULT 'EXPLICIT'`, `workflow_mode VARCHAR(16) NOT NULL DEFAULT 'INTERNAL'`, `profile_item_no INT NULL`; add `CHECK ((table_id IS NULL) <> (object_pattern IS NULL))` and `CHECK (object_pattern IS NULL OR data_source IS NOT NULL)`.
     - Create `CONSENT_APPROVING_OWNERS(tenant_id, consent_id, owner_sid, owner_sid_norm, PK(tenant_id, consent_id, owner_sid_norm))`.
  2. **Create** `src/Autheris.Application/Policy/RowScopes/ObjectPatternMatcher.cs` & `PatternCoverageMonitor.cs`:
     - Literal `data_source` segment + glob `*` only in schema and object segments (no regex).
     - New/later-classified PII columns without explicit `Clear` stay `Mask`; hidden objects/columns never covered; in `Internal` mode pattern covers only objects whose current owner/delegate is in `CONSENT_APPROVING_OWNERS` (`PatternCoverageMonitor` submits pending approval items for new owners on catalog epoch change).
     - Include catalog epoch in the PDP decision cache key (`SEC-AZ-07`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Policy/RowScopes/DynamicCoverageTests.cs` `[S]` (all 9 tests from §6.1).
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~DynamicCoverage|FullyQualifiedName~StepSqlLint" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~GovernanceMigrationStepParity|FullyQualifiedName~MigrationShape|FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id"
  ```

---

#### Brief `W7-48-6` — `WP-SIMPL-48 PR 6`: Effective-Access Report (`/api/v1/authz/subjects/{subject}/effective-access`) & `IGrantEffectPreview`

- **Branch / Worktree:** `feat/simpl-48-pr6-effective-access-preview` in `/work/wt/simpl-48-pr6-effective-access-preview`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-48-row-policies-dynamic-coverage.md` (§5 Step 6, `SEC-AZ-06`, `SEC-AZ-19`)
- **Depends On:** `W7-48-5`.
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Application/Policy/Pdp/IEffectiveAccessService.cs`, `EffectiveAccessService.cs`, `IGrantEffectPreview.cs`, `GrantEffectPreviewService.cs`, and `src/Autheris.Api/Endpoints/EffectiveAccessEndpoints.cs`:
     - `GET /api/v1/authz/subjects/{subject}/effective-access?object=<pattern>&cursor=&pageSize=`: structural detection of `REDUNDANT_GRANT` and `EMPTY_HARD_INTERSECTION` (`D-48b`); never emits predicate SQL or parameter values of other subjects (`SEC-AZ-06`); cross-tenant returns uniform `404`.
     - `IGrantEffectPreview.PreviewAsync`: non-blocking, audited (`AUTHZ_EFFECT_PREVIEW`, `SEC-AZ-19`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Policy/RowScopes/EffectiveAccessReportTests.cs` `[S]` (all 6 tests from §6.1).
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~EffectiveAccessReport" -p:AutherisIncludeTestAuth=true
  ```

---

### 2.5 Group E — Governance Data Model & Classification-Based Masking (`WP-SIMPL-52 PR 1a..6b`)

*(Phase 3 `APPROVED WITH CONDITIONS` `SEC-52-01..10` in `docs/plans/simplification/WP-SIMPL-52-governance-data-model.md` §15.)*

---

#### Brief `W7-52-1A` — `WP-SIMPL-52 PR 1a` (Single DDL Slot `0020_data_classifications`): Classification Schema, Backfill & Domain Model

- **Branch / Worktree:** `feat/simpl-52-pr1a-classifications-ddl` in `/work/wt/simpl-52-pr1a-classifications-ddl`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-52-governance-data-model.md` (§5.1–5.4, §6 S1 DDL, §8 Step 1, `SEC-52-01`, `SEC-52-02`, `SEC-52-10`)
- **DDL Slot:** **`0020_data_classifications`** (runs after `0019_pattern_grants`).
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Infrastructure/Persistence/Migrations/Steps/{postgresql,sqlserver}/0020_data_classifications.{sql,shape.json}`:
     - Create `DATA_CLASSIFICATIONS`, `CLASSIFICATION_MASK_RULES` (including `catalog_text VARCHAR(8) NOT NULL DEFAULT 'VISIBLE'` with `CHECK (catalog_text IN ('VISIBLE','HIDDEN'))` for `#571`), `COLUMN_CLASSIFICATIONS`, and `COLUMN_MASK_OVERRIDES` (including `catalog_text`).
     - Seed `*` built-ins (`PUBLIC` rank 0, `CONFIDENTIAL` 20, `FINANCIAL` 30, `PII` 40, `PII_SPECIAL` 60 parent `PII` approver `DATA_OWNER_AND_STEWARD`, `CREDENTIALS` 90 `clear_grantable = false`) and default `*` rules (`_DEFAULT` = `TYPE_SAFE_REDACT '***'`).
     - Backfill `COLUMN_CLASSIFICATIONS(*, c, 'PII', 'MIGRATION')` for every column with `is_sensitive = 1` **or** a `COLUMN_MASKING_RULES` row, and backfill `COLUMN_MASK_OVERRIDES(*, c, NULL, ...)` mapping every `rule_type` per §8 Step 1 (`SEC-52-10`).
  2. **Create** `src/Autheris.Domain/Model/ClassificationModels.cs`:
     - `DataClassification`, `MaskFunction` enum (`Nullify, Redact, TypeSafeRedact, Partial, Hash, Tokenize, TruncateDate, BucketNumber, RoundNumber, EmailDomain, GeoJitter`), `CatalogTextVisibility { Visible, Hidden }`, `ColumnClassification`, `ClassificationMaskRule`, `ColumnMaskOverride`, hierarchy/rank/cycle validator (`SEC-52-02`).
- **Required Unit & Architecture Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Governance/Classification/ClassificationVocabularyTests.cs` `[S]`: `Star_scope_not_a_valid_tenant`, `Child_rank_below_parent_rejected`, `Tenant_row_overrides_builtin_approver`, `Cycle_in_hierarchy_rejected`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ClassificationVocabulary|FullyQualifiedName~StepSqlLint" -p:AutherisIncludeTestAuth=true
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~GovernanceMigrationStepParity|FullyQualifiedName~MigrationShape|FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id"
  ```

---

#### Brief `W7-52-1B` — `WP-SIMPL-52 PR 1b`: Classification Repository Partials, `GovernanceTable` Allowlist & `CatalogTablePersistedProjection`

- **Branch / Worktree:** `feat/simpl-52-pr1b-classifications-repo` in `/work/wt/simpl-52-pr1b-classifications-repo`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-52-governance-data-model.md` (§5.10.7, `SEC-52-07`, `SEC-52-08`)
- **Depends On:** `W7-52-1A`.
- **Target `src/` Files:**
  1. `src/Autheris.Infrastructure/Persistence/Dialects/GovernanceTable.cs` (`145` lines):
     - Add `DataClassifications`, `ClassificationMaskRules`, `ColumnClassifications`, `ColumnMaskOverrides`, `ConsentClassificationAccess`.
  2. **Create** `src/Autheris.Infrastructure/Persistence/GovernanceRepository.Classifications.cs` & `GovernanceRepository.ClassificationsSql.cs` (do **not** grow `GovernanceRepository.Catalog.cs` `791` LOC):
     - Load/save classifications, rules, column assignments, and overrides; enforce `CatalogWriteClassification.Restrictive` immediate version flush (`#672` `SEC-52-08`) on restrictive mutations.
  3. Update `CatalogTablePersistedProjection.cs` (`#674`) to include persisted `*`-scope column classifications and mask overrides (`SEC-52-08`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Governance/Classification/ClassificationMutationFlushTests.cs` `[S]`: `Restrictive_classification_or_mask_change_flushes_catalog_version_immediately_inside_write_scope`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ClassificationMutationFlush|FullyQualifiedName~CatalogTableProjection" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-52-2` — `WP-SIMPL-52 PR 2`: Mask Function Catalogue (`MaskSpecRenderer` & `MaskSpecEvaluator` across 6 Dialects)

- **Branch / Worktree:** `feat/simpl-52-pr2-mask-functions` in `/work/wt/simpl-52-pr2-mask-functions`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-52-governance-data-model.md` (§5.3, `SEC-52-04`, `SEC-52-05`, `SEC-52-06`)
- **Depends On:** `W7-52-1B`.
- **Target `src/` Files (Verified on `6de0553d`):**
  1. `src/TrinoSqlEngine/Governance/PolicyProviders.cs` (`42` lines):
     - Extend `MaskKind` with `TruncateDate, BucketNumber, RoundNumber, EmailDomain, Tokenize`.
  2. `src/Autheris.Application/Sql/Masking/MaskSpecRenderer.cs` (`88` lines), `MaskSpecEvaluator.cs` (`96` lines), `SqlDataMaskingProvider.cs` (`513` lines), and `src/Autheris.Application/Services/ColumnMaskingProvider.cs` (`541` lines):
     - Render and evaluate `TRUNCATE_DATE {YEAR|MONTH|DAY}`, `BUCKET_NUMBER {width}`, `ROUND_NUMBER {digits}`, `EMAIL_DOMAIN`, and `TOKENIZE {key_ref, domain}` across `PostgreSql, SqlServer, Oracle, Databricks, DuckDb, Ansi` using bound parameters only (`SEC-52-04`).
     - Enforce `PARTIAL` `keep_first + keep_last <= 16` (`MaskingRule.MaxRevealedChars`), tenant-isolated key derivation `DeriveHexKey(masterKey, tenantId, keyRef)`, `PreventInDbHmacKeyExposure` for `HASH`/`TOKENIZE` (`SEC-52-05`), and fallback to `TYPE_SAFE_REDACT` on column type mismatch (`autheris_mask_type_fallback_total`).
     - Keep `AstSecurityVisitor.MaskGuards*.cs` blocking all non-`Clear` columns in predicates, joins, `GROUP BY`, `ORDER BY`, and aggregates (`SEC-52-06`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Governance/Classification/MaskFunctionTests.cs` `[S]` (all 10 tests from §9).
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~MaskFunction|FullyQualifiedName~MaskSpec" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-52-3` — `WP-SIMPL-52 PR 3` (Single DDL Slot `0021_consent_classification_access`): `ClassificationAccessResolver` & PDP Integration

- **Branch / Worktree:** `feat/simpl-52-pr3-classification-resolver` in `/work/wt/simpl-52-pr3-classification-resolver`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-52-governance-data-model.md` (§5.5, §6 S2 DDL, §8 Step 2, `SEC-52-01..03`, `SEC-52-10`)
- **DDL Slot:** **`0021_consent_classification_access`** (runs after `0020_data_classifications`).
- **Depends On:** `W7-52-2`.
- **Target `src/` Files:**
  1. **Create** `src/Autheris.Infrastructure/Persistence/Migrations/Steps/{postgresql,sqlserver}/0021_consent_classification_access.{sql,shape.json}`:
     - Create `CONSENT_CLASSIFICATION_ACCESS(tenant_id, consent_id, code, access_level, valid_until, approved_by_sid, approved_at, PK(tenant_id, consent_id, code))` and backfill `(tenant, consent, 'PII', CLEAR|MASK)` from `CONSENT_COLUMN_RULES`.
  2. **Create** `src/Autheris.Application/Policy/Classification/ClassificationAccessResolver.cs` and wire into `AccessDecisionPoint.cs` (`309` lines) and `TableAccessPolicy.Consent.cs` (`344` lines):
     - Enforce `SEC-52-01` (unknown/inactive classification, unknown mask function, or unresolvable `key_ref` -> `ColumnAccessLevel.Deny` with `MASK_KEY_UNAVAILABLE`), `SEC-52-02` (`CREDENTIALS` never `Clear`; child classifications never inherit `Clear` from parent), and `SEC-52-03` (strict restrictiveness order across non-`Clear` classifications and across contributing grants; per-column override `(c, k)` replaces only `k`'s candidate).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Governance/Classification/ClassificationAccessResolverTests.cs` `[S]` (all 13 tests from §9), `ClassificationOverrideIsolationTests.cs` `[S]`, `PerCellClassificationTests.cs` `[S]`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ClassificationAccessResolver|FullyQualifiedName~ClassificationOverrideIsolation|FullyQualifiedName~PerCellClassification|FullyQualifiedName~StepSqlLint" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-52-4A` — `WP-SIMPL-52 PR 4a`: Migrate Consumers off `IsSensitive`, Additive Ingestion Mapping, Audit & Discovery Fields

- **Branch / Worktree:** `feat/simpl-52-pr4a-consumers-off-issensitive` in `/work/wt/simpl-52-pr4a-consumers-off-issensitive`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-52-governance-data-model.md` (§5.8, §10 PR 4, `SEC-52-05`, `SEC-52-07`)
- **Depends On:** `W7-52-3`.
- **Target `src/` Files:**
  1. Switch `GrantClassifier`, catalog/MCP/OData/federation/profiling consumers from `TableColumn.IsSensitive` / `ColumnMaskingRules` to `ColumnClassifications` and `ClassificationAccessResolver`.
  2. **Create** `src/Autheris.Extensions/Dbt/DbtMetadataIngestionService.Classifications.cs` (do **not** grow `DbtMetadataIngestionService.cs` `1759` LOC):
     - Map dbt/OpenMetadata `meta.pii` / `meta.classification` to `COLUMN_CLASSIFICATIONS(scope = '*', source = 'INGESTION')` strictly additively (`SEC-52-07`: re-ingestion never deletes existing `OWNER`, `ADMIN`, `MIGRATION`, or prior `INGESTION` classifications).
  3. Update data-access audit to record `{column, classifications[], maskFunction, keyRef}` (never key material or constant values, `SEC-52-05`) and Discovery (`WP-SIMPL-41`) to expose `classifications` and `clearRequestable` on visible columns only.
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Governance/Classification/ClassificationIngestionSyncTests.cs` `[S]`, `ClassificationAuditTests.cs` `[S]`, `DiscoveryClassificationTests.cs` `[S]`, `WebSqlMaskedFlagTests.cs` `[S]`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ClassificationIngestionSync|FullyQualifiedName~ClassificationAudit|FullyQualifiedName~DiscoveryClassification|FullyQualifiedName~WebSqlMaskedFlag" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-52-4B` — `WP-SIMPL-52 PR 4b (#571)`: Catalog Text Suppression (`catalog_text = 'HIDDEN'`) for Masked Columns

- **Branch / Worktree:** `feat/simpl-52-pr4b-masked-catalog-text` in `/work/wt/simpl-52-pr4b-masked-catalog-text`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-52-governance-data-model.md` (§5.8 `#571`, §10 PR 4b, `D-52p`, `SEC-52-09`)
- **Depends On:** `W7-52-4A`.
- **Target `src/` Files:**
  1. `ClassificationAccessResolver.cs`: compute `CatalogTextVisible` per column (`HIDDEN` wins over `VISIBLE` across the column's classifications and contributing grants when effective level is `Mask`; `Clear` callers see text; `Deny` columns stay completely invisible).
  2. Filter MCP (`describe_dataset` full and `brief`, `list_datasets`), catalog REST detail, GraphQL catalog, and catalog search snippets so a `Mask` column with `CatalogTextVisible == false` emits **only** column name and technical data type (omitting description, display name, semantic tags, value profile, and sample values, and dropping any golden query or GraphQL example query selecting that column).
  3. Exclude description/display text of any column that has a `HIDDEN` rule or override from catalog search indexing (`#628c` per-column vectors and lexical search, `SEC-52-09`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `tests/Autheris.Tests.Unit/Governance/Classification/MaskedCatalogTextTests.cs` `[S]`: `Hidden_text_omitted_on_every_catalog_path` (Theory over MCP full/brief, REST, GraphQL catalog), `Examples_selecting_hidden_text_column_dropped`, `Hidden_wins_over_visible_across_classifications`, `Clear_caller_sees_text`, `Default_visible_keeps_today_output`, `Search_does_not_match_hidden_column_text`.
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~MaskedCatalogText" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-52-5A` & `W7-52-5B` — `WP-SIMPL-52 PR 5a & 5b`: Classification Admin API & Per-Classification Clear-Access Requests

- **Branches / Worktrees:**
  - `W7-52-5A`: `feat/simpl-52-pr5a-classification-admin-api` in `/work/wt/simpl-52-pr5a-classification-admin-api`
  - `W7-52-5B`: `feat/simpl-52-pr5b-classification-clear-requests` in `/work/wt/simpl-52-pr5b-classification-clear-requests`
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-52-governance-data-model.md` (§5.6, §5.9, `SEC-52-07`, `SEC-52-08`)
- **Depends On:** `W7-52-3` and `W7-47-3`.
- **Target `src/` Files:**
  - **PR 5a (`W7-52-5A`):** Create `src/Autheris.Api/Endpoints/ClassificationAdminEndpoints.cs` (`GET/PUT /api/v1/governance/classifications/{code}`, `GET/PUT .../mask-rule`, `PUT/DELETE /api/v1/catalog/columns/{columnId}/classifications/{code}?scope=tenant|global`, `PUT/DELETE .../mask-overrides/{code|_all}`, `.WithAudit`). Enforce `ClusterAdmin` for `*` scope, tenant `GovernanceAdmin` for tenant scope (`404` cross-tenant), owner/delegate or `GovernanceAdmin` for column assignment/removal, and immediate restrictive catalog flush (`SEC-52-07`, `SEC-52-08`).
  - **PR 5b (`W7-52-5B`):** Extend `IConsentDecisionPipeline` and `ConsentRequest` with `requested_clear: [{classification, columns[]}]`: reject `CREDENTIALS` (`SEC-52-02`), route `DATA_OWNER`, `STEWARD` (`steward_group_sid`), or sequential four-eyes `DATA_OWNER_AND_STEWARD` (`PII_SPECIAL`), forbid self-approval, cap `valid_until <= min(grant.valid_until, approved_at + max_clear_days)`, and enforce narrowing-only in external mode (`SEC-52-07`).
- **Required Unit Tests (`[Trait("Category","Security")]`):**
  - `ClassificationAdminAuthorizationTests.cs` `[S]` (`PR 5a`) and `ClassificationGrantWriteTests.cs` `[S]` (`PR 5b`).
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~ClassificationAdminAuthorization|FullyQualifiedName~ClassificationGrantWrite" -p:AutherisIncludeTestAuth=true
  ```

---

#### Brief `W7-52-6A` & `W7-52-6B` — `WP-SIMPL-52 PR 6a & 6b` (DDL Slots `0022` & `0023`): Drop `is_sensitive`/`COLUMN_MASKING_RULES` & Drop Superseded Governance Columns

- **Branches / Worktrees:**
  - `W7-52-6A`: `feat/simpl-52-pr6a-drop-issensitive` in `/work/wt/simpl-52-pr6a-drop-issensitive` (DDL Slot **`0022_drop_is_sensitive`**)
  - `W7-52-6B`: `feat/simpl-52-pr6b-drop-superseded-columns` in `/work/wt/simpl-52-pr6b-drop-superseded-columns` (DDL Slot **`0023_drop_superseded_governance_columns`**)
- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-52-governance-data-model.md` (§8 Steps 3 & 4, `SEC-52-10`)
- **Depends On:** `W7-52-4B`, `W7-52-5B`, `W7-47-7`, and `W7-48-5`.
- **Target `src/` Files:**
  - **PR 6a (`W7-52-6A`):** Remove remaining `TableColumn.IsSensitive` and `ColumnMaskingRules` properties from `src/`, add migration `0022_drop_is_sensitive.{sql,shape.json}` dropping `table_columns.is_sensitive` and `column_masking_rules`, and add architecture test `No_IsSensitive_reference_outside_migrations` + `Mask_keys_never_persisted`.
  - **PR 6b (`W7-52-6B`):** Add migration `0023_drop_superseded_governance_columns.{sql,shape.json}` with pre-check aborting if any unrevoked role-based consent exists (`SEC-52-10`), then dropping `consents.role_id`, `consents.role_name`, `roles`, `role_members`, superseded `consent_row_filters` subquery columns, `consent_requests.itsm_ticket_id`, `access_profiles.{row_filter_predicate, masking_mode, target_tables}`, and `access_policies*` (plus removing `ACCESS_POLICIES` asset type from `RecertificationService`).
- **Constrained Verification Commands:**
  ```bash
  MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~GovernanceMigrationStepParity|FullyQualifiedName~MigrationShape|FullyQualifiedName~No_IsSensitive|FullyQualifiedName~SourceSizeRatchet|FullyQualifiedName~Plan_id"
  ```

---

### 2.6 Group F — Post-Freeze Feature Packages (`WP-SIMPL-49 PR A1..A6` Data Products & `WP-SIMPL-54 PR 1..3` Golden Queries `#580`)

*(Both packages are marked post-Stage-3 feature-freeze exit per PRD D8 / Q26 and are briefed here so the queue is 100% ready for dispatch upon Stage 3 exit.)*

---

#### Briefs `W7-49-A1` .. `W7-49-A6` — `WP-SIMPL-49`: Data Products (Views & Stored Procedures with Product Owner)

- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-49-data-products.md` (§5 Steps 1–9, `SEC-PROD-01..14`, `OD-44`)
- **Depends On:** Stage 3 feature-freeze exit + Groups B, C, D, E above.
- **6 Atomic Sub-PR Briefs (`<= 400` net `src/` LOC each):**
  1. **`W7-49-A1` (`WP-SIMPL-49 PR A1` — Single DDL Slot `0024_data_products`, Model, Roles, Authority, Notifications & Column Classification):**
     - Branch `feat/simpl-49-pr-a1-model-authority` in `/work/wt/simpl-49-pr-a1-model-authority`.
     - Migration `0024_data_products.{sql,shape.json}` creating `DATA_PRODUCTS`, `DATA_PRODUCT_VERSIONS`, `DATA_SOURCE_OWNERS` (`WP-SIMPL-52` §6 DDL).
     - Create `src/Autheris.Domain/Model/DataProductModels.cs`, add tenant-scoped roles `GatewayRole.DataProductAdmin = 34` and `DataProductSync = 35` (**never** implied by `ClusterAdmin`, `TenantAdmin`, or `GovernanceAdmin` in `GatewayRole.Implies`, `SEC-PROD-05`), privileged functional right `DataProduct.Admin` (four-eyes), `/api/v1/products` admin routes, creation-authority checks (`SEC-PROD-01..04`: admin/sync creator can never be product owner -> `PRODUCT_OWNER_MUST_DIFFER`), base-owner notifications (`SEC-PROD-14`), and lineage PII classification (`SEC-PROD-08`).
     - Unit tests: `ProductAuthorityTests.cs` `[S]`, `ProductRoleTests.cs` `[S]`, `ProductColumnTests.cs` `[S]`.
  2. **`W7-49-A2` (`WP-SIMPL-49 PR A2` — Two-Stage DuckDB Views, Load Modes `schedule|onDemand|live` & PDP Definer Exposure):**
     - Branch `feat/simpl-49-pr-a2-views-load-modes` in `/work/wt/simpl-49-pr-a2-views-load-modes`.
     - Create `src/Autheris.Application/Products/ProductLoadService.cs`: stage-1 definer execution over DuckDB federation staging checking `DefinitionHash` before every load, shadow swap in table cache, cluster lease + single-flight for `onDemand(ttl)`, `ProductLimits`, `MaxStaleness` (`PRODUCT_STALE`), and stage-2 caller evaluation in `AccessDecisionPoint.cs` (`DataProduct` in reserved source `products`, caller grants/denies/hard policies on product only, `SEC-PROD-06, 07, 09, 10`).
     - Unit & architecture tests: `ProductDecisionTests.cs` `[S]`, `ViewLoadTests.cs` `[S]`, `ProductArchitectureTests.cs` `[S]`.
  3. **`W7-49-A3` (`WP-SIMPL-49 PR A3` — Read Stored Procedures as Data Products):**
     - Branch `feat/simpl-49-pr-a3-read-procedures` in `/work/wt/simpl-49-pr-a3-read-procedures`.
     - Replace base-table consent lookup and `RowScopeKey` semi-join in `GovernedProcedureExecutionService` (via partial `GovernedProcedureExecutionService.Products.cs`) with `AccessOperation.Execute` on the procedure product object + `ProcedureResult` row policies (`SEC-PROD-11`).
  4. **`W7-49-A4` (`WP-SIMPL-49 PR A4` — Write Stored Procedures & `ExecuteWrite` Grant Operation):**
     - Branch `feat/simpl-49-pr-a4-write-procedures` in `/work/wt/simpl-49-pr-a4-write-procedures`.
     - Enable `ProcedureMode.Write` gated strictly by `AccessOperation.ExecuteWrite` (`Execute` grant never allows a `write` procedure, `SEC-PROD-12`), wired on REST procedure route and non-readonly MCP `call_procedure` only, with `RecordRequiredAsync` intent/outcome audit and per-product rate limit.
     - Unit tests: `ProcedureProductTests.cs` `[S]`.
  5. **`W7-49-A5` (`WP-SIMPL-49 PR A5` — Git Synchronisation `/api/v1/products/sync/plan|apply|webhook`):**
     - Branch `feat/simpl-49-pr-a5-git-sync` in `/work/wt/simpl-49-pr-a5-git-sync`.
     - Callable only by `DataProductSync`; `ManagedBy = Git` definitions are read-only in REST API while product-owner grant/policy/masking operations remain active; drift reported without silent overwrite (`SEC-PROD-13`).
     - Unit tests: `ProductSyncTests.cs` `[S]`.
  6. **`W7-49-A6` (`WP-SIMPL-49 PR A6` — Discovery, Usage, Audit, Explain & Parity Rows `P1..P13`):**
     - Branch `feat/simpl-49-pr-a6-discovery-usage-parity` in `/work/wt/simpl-49-pr-a6-discovery-usage-parity`.
     - Wire product visibility into Discovery (hiding base objects invisible to caller), usage attribution per product, data-access audit `(productId, definitionHash)`, explain/effective-access "used by products" (`SEC-PROD-14`), and parity tests `P1..P13`.
     - Unit tests: `ProductTransparencyTests.cs` `[S]`.

---

#### Briefs `W7-54-1` .. `W7-54-3` — `WP-SIMPL-54 (#580)`: Golden Queries Maintained by Data Owners (dbt Meta & Governance Store)

- **Plan Reference:** `docs/plans/simplification/WP-SIMPL-54-golden-queries-from-dbt.md` (§4 Steps 1–3, §10 Phase 3 `SEC-54-01..07`)
- **Depends On:** Stage 3 feature-freeze exit + `W7-52-4B` (`#571` hidden catalog text).
- **3 Atomic Sub-PR Briefs (`<= 400` net `src/` LOC each):**
  1. **`W7-54-1` (`WP-SIMPL-54 PR 1` — Single DDL Slot `0025_golden_queries` & `IGoldenQueryRepository`):**
     - Branch `feat/simpl-54-pr1-golden-queries-store` in `/work/wt/simpl-54-pr1-golden-queries-store`.
     - Migration `0025_golden_queries.{sql,shape.json}` creating `GOLDEN_QUERIES(id, tenant_id, table_id, source, source_key, title, description, query_text, variables_json, content_hash, updated_at, UNIQUE(tenant_id, table_id, source, source_key))`.
     - Add `GovernanceTable.GoldenQueries` and create `src/Autheris.Application/Mcp/Interfaces/IGoldenQueryRepository.cs` + `src/Autheris.Infrastructure/Persistence/GovernanceRepository.GoldenQueries.cs` (`ReplaceForTableAsync` atomic per `(tenant_id, table_id, source)` with mandatory `WHERE tenant_id = @tenantId`, `SEC-54-01`, `SEC-54-05`).
  2. **`W7-54-2` (`WP-SIMPL-54 PR 2` — dbt `meta.golden_queries` Ingestion, GraphQL AST Validation & Rune Sanitizer):**
     - Branch `feat/simpl-54-pr2-dbt-golden-queries` in `/work/wt/simpl-54-pr2-dbt-golden-queries`.
     - Create `src/Autheris.Extensions/Dbt/DbtMetadataIngestionService.GoldenQueries.cs` (do **not** add to `DbtMetadataIngestionService.cs` `1759` LOC) and extend `DbtArtifactStreamingParser.cs` (`742` LOC):
       - Parse `meta.golden_queries: [{title, description, query, variables}]`; enforce hard caps (`title <= 256`, `description <= 2048`, `query <= 16 KiB`, `variables <= 4 KiB` flat JSON depth `<= 3`, max `20` entries per dataset, `SEC-54-05`).
       - Parse `query` with GraphQL AST parser (`query` operation only, every root selection field belongs to the owning dataset, `SEC-54-03`).
       - Sanitize text through the shared catalog-text rune sanitizer (`#500`, `SEC-54-04`); log skipped entries with `(datasetId, entryIndex, reasonCode)` only — **never** raw query/title/description/variables values (`SEC-54-06`).
     - Unit tests: `tests/Autheris.Tests.Unit/Dbt/GoldenQueryMetaImportTests.cs` (`Valid_entries_imported`, `Removed_in_dbt_removed_after_sync`, `Empty_query_rejected_with_reason_code`, `Query_over_other_dataset_rejected` `[S]`, `Oversized_text_rejected`, `Warning_contains_no_values` `[S]`, `More_than_20_entries_truncated_with_warning`).
  3. **`W7-54-3` (`WP-SIMPL-54 PR 3` — `GoldenQueryService` Store Read Path, Per-Caller Column/Visibility Filter & Cache Key):**
     - Branch `feat/simpl-54-pr3-golden-query-read` in `/work/wt/simpl-54-pr3-golden-query-read`.
     - Update `src/Autheris.Application/Mcp/Services/GoldenQueryService.cs` (`192` lines):
       - Merge store entries with `InitialQueries` (config wins on equal `id`; store wins on equal `source_key`).
       - Evaluate caller visibility before applying any cap (`SEC-54-02`): owning dataset and every referenced relation dataset must be discoverable for the caller (`!DiscoveryHidden` and in tenant scope), no selected/filtered column may have `ColumnAccessLevel.Deny` or `DiscoveryHidden = true`, and any column with `CatalogTextVisible == false` (`WP-SIMPL-52 PR 4b` `#571`) drops the example query for that caller.
       - Key any cache by `(TenantId, CatalogVersion, CallerDecisionFingerprint)` (`SEC-54-07`) and emit MCP output inside untrusted-content wrappers (`SEC-54-04`).
     - Unit tests: `tests/Autheris.Tests.Unit/Mcp/GoldenQueryStoreReadTests.cs` (`Describe_dataset_examples_after_sync_without_restart`, `Entry_with_invisible_relation_hidden` `[S]`, `Entry_selecting_denied_column_hidden` `[S]`, `Config_entries_still_served`).
     - Constrained verification:
       ```bash
       MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -m:1 -nodeReuse:false --filter "FullyQualifiedName~GoldenQueryMetaImport|FullyQualifiedName~GoldenQueryStoreRead" -p:AutherisIncludeTestAuth=true
       ```

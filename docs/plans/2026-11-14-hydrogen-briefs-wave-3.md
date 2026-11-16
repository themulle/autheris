# Phase 4 Hydrogen Task Briefs — Wave 3 (2026-11-14)

**Status:** Phase 3 APPROVED WITH CONDITIONS — Ready for Phase 4 Execution (`csharp-tdd-developer` on Gemini 4 Hydrogen)
**Baseline Verified:** `origin/main` @ `75bcf65a` (2026-11-14)

---

## 0. Baseline Verification, Deduplication & File-Overlap Matrix

Before scheduling Wave 3, all candidate items from `00-master-plan-overview.md`, `2026-11-14-handover-agent-continuation.md`, and the Phase 2/3 cluster plans were checked against `origin/main` (`75bcf65a`), open GitHub PRs, and Wave 2:

| Candidate Item | Status on `main` / Branches | Action in Wave 3 |
|---|---|---|
| `WP-OI-CAT-5 (#680)` (`AutoSyncFromDbt` follows `SqlEndpoints.Enabled`) | **ALREADY MERGED** on `main` (`8d1f5d04`, PR `#690`) | **Do NOT schedule** (complete) |
| `WP-OI-CAT-6 (#610)` (`TABLE_COLUMNS.ordinal` migration step `0010`) | **ALREADY MERGED** on `main` (`f98dbe3c`, PR `#693`, step `0010_table_column_ordinal`) | **Do NOT schedule** (complete) |
| `WP-OI-CAT-2 (#667)` (`consents/apply` canonical role & tenant gate) | **ALREADY MERGED** on `main` (`1e926a8e`) | **Do NOT schedule** (complete) |
| `WP-OI-SCALE-1 (#672 PR 1+2)` (`CatalogWriteScope` + restrictive immediate flush + dirty flag) | **ALREADY MERGED** on `main` (`906e3664`, `968c670e`) | **Do NOT schedule** (complete) |
| `WP-OI-SCALE-3 (#674)` (`CatalogTablePersistedProjection` no-op diff) | **ALREADY MERGED** on `main` (`9d07967f`) | **Do NOT schedule** (complete) |
| `WP-OI-SRCH-501 (#501)` & `WP-OI-SRCH-628a (#628a)` | **ALREADY MERGED** on `main` (`ebd42501`, `e28c8cd6`) | **Do NOT schedule** (complete) |
| `WP-SIMPL-41 PR 1` (`OpenSchema` removal) | **ALREADY MERGED** on `main` (`OpenSchema` in `LegacySwitchGuard.Removed`) | **Do NOT schedule** (complete) |
| `WP-SIMPL-46 PR 6a (#733)` & `PR 6b (#730)` | PR 6a **MERGED** (`75bcf65a`); PR 6b **COMPLETED** on `feat/simpl-46-pr6b-sqlendpoint-parity` (`9700bdd7`) | **Do NOT schedule** (complete) |
| `WP-OI-CAT-1 (#666)`, `WP-OI-WEB-668 (#668)`, `#669`, `#624` | Open GitHub PRs `#748`, `#747`, `#744`, `#734` | **Do NOT schedule** (in flight) |
| `WP-SIMPL-53 PR 4 (#573)`, `WP-OI-CAT-7 (#612)`, `WP-OI-CAT-3 (#670)`, `WP-OI-SQL-478-A/B (#478)` | `#612` **MERGED** on `main` (`173703d6`); `#670` (`5bfa220a`), `#478-A` (`6ccbd417`), `#573` (`f45f7104`), `#478-B` in Wave 2 | **In Wave 2 / Merged** (note file overlap below) |
| **`WP-SIMPL-41 PR 2`** (Discovery domain, step `0013_catalog_discovery`, flag API) | Reference diff on `origin/feat/simpl-41-pr2` (`bfc86f73`); do **NOT** use local `feat/simpl-41-pr2` (`4861b991`) | **Wave 3 Brief 1 (Single DDL Slot `0013`)** |
| **`WP-OI-SQL-486 (#486)`** (Required filter column satisfaction across all paths) | Open on `main`; Phase 3 `APPROVED WITH CONDITIONS` (`SEC-OI-486-1..5`) | **Wave 3 Brief 2** |
| **`WP-SIMPL-45 PR 4A & PR 4B`** (PR 4A: Steps 3 + 13 consent write workflow & `DmlWriterRoles` removal; PR 4B: Steps 5 + 8 `AccessDecisionPoint` + `GovernedSqlRewriter` write wiring) | Open on `main`; Phase 3 `APPROVED` (`SEC-SIMPL-120..127`) | **Wave 3 Brief 3A & Brief 3B** |
| **`WP-OI-SCALE-4 PR 1 (#675)`** (`CatalogSnapshotProvider` event coalescing, no DDL) | Completed on `feat/oi-scale-675-pr1-coalescing` (`9b0e8de8`); Phase 3 `APPROVED WITH CONDITIONS` (`SEC-OI-675-3..5`) | **Wave 3 Brief 4** |

### File-Overlap & Sequencing Rules Across Wave 2, Wave 3, and Open PRs
- **`ODataFilterParser.cs` overlap:** Wave 2 Brief 4B (`WP-OI-SQL-478-B`) and Wave 3 Brief 2 (`WP-OI-SQL-486`) both touch `src/Autheris.Application/OData/ODataFilterParser.cs`. Run Wave 2 Brief 4B first (or rebase whichever merges second).
- **`GovernedSqlRewriter.cs` overlap:** `WP-SIMPL-46 PR 6b` (`feat/simpl-46-pr6b-sqlendpoint-parity` @ `9700bdd7`) touches `GovernedSqlRewriter.cs`. Merge `feat/simpl-46-pr6b-sqlendpoint-parity` into `main` **before** starting Wave 3 Brief 3 (`WP-SIMPL-45 PR 4`).
- **Single DDL Slot (`0013`):** Wave 3 Brief 1 (`WP-SIMPL-41 PR 2`) is the **only** PR in Wave 2 + Wave 3 that adds a governance migration step (`0013_catalog_discovery`). No other Wave 2 or Wave 3 brief touches `src/Autheris.Infrastructure/Persistence/Migrations/Steps/**`.
- **`DbtMetadataIngestionService.cs` overlap:** Open PR `#748` (`#666`) touches `DbtMetadataIngestionService.cs`; none of the 4 Wave-3 briefs below need to edit `DbtMetadataIngestionService.cs` (Brief 1's dbt meta ratchet is in `CatalogGovernanceRatchet.cs`).

---

## Global Execution Rules for All Wave-3 Briefs (Mandatory)

1. **Dedicated Git Worktree & Branch:**
   - Never work in `/root/autheris` directly. Create the dedicated worktree under `/work/wt/<worktree-name>` branching from `origin/main`.
2. **Strict Local Resource Guardrails (`csharp-tdd-developer`):**
   - Always prefix `dotnet` commands with `MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget`.
   - Always pass `-c Release -p:AutherisIncludeTestAuth=true -m:1 -nodeReuse:false` on build/test commands, and `--blame-hang --blame-hang-timeout 120s` on `dotnet test`.
   - **NEVER** run the full unfiltered unit test suite or any integration test locally. Run **only** the targeted `--filter` unit and architecture tests specified in your brief.
   - Compile affected integration test projects locally with `dotnet build ... --no-dependencies` only.
3. **PR Size & Hygiene:**
   - Each PR must stay `<= 400` net added lines in `src/` (generated `.shape.json` files are excluded/exempt per migration policy).
   - Never write plan-ID tokens (`WP-`, `SEC-`, `OD-`) in code comments under `src/` (`PlanIdCommentRatchetTests`).
   - Never write absolute URI links (the literal scheme `file` followed by colon-slash-slash) inside any `.md` file (`DocsIntegrityTests.No_absolute_file_links`).

---

## Brief 1 (Single DDL Slot `0013`): `WP-SIMPL-41 PR 2` — Authorized Data Discovery Domain Model, Persistence (`0013_catalog_discovery`), and Flag API

- **Worktree:** `/work/wt/simpl-41-pr2-discovery-flags`
- **Branch:** `feat/simpl-41-pr2-discovery-flags`
- **Plan & Phase 3 References:**
  - `docs/plans/simplification/WP-SIMPL-41-authorized-data-discovery.md` (§5 steps 2–4, lines 94–125; §13 `SEC-DISC-01` .. `SEC-DISC-16`)
  - **Reference Diff ONLY:** `origin/feat/simpl-41-pr2` (`bfc86f73`). **CRITICAL:** Do **NOT** branch from or cherry-pick local `feat/simpl-41-pr2` (`4861b991`) — commit `4861b991` contains literal unresolved git conflict markers (`<<<<<<< HEAD`, `=======`, `>>>>>>> bfc86f73`) committed into `src/` and `WP-SIMPL-41` comments in SQL!

### 1.1 Context & Current State on `main` (`173703d6`)
- Commit `bfc86f73` on `origin/feat/simpl-41-pr2` implemented `WP-SIMPL-41 PR 2` (`DiscoveryModels.cs`, `IDiscoveryArtifactStore.cs`, `DiscoveryArtifactStore.cs`, `DatasetCatalogStore.cs` overlay for `DiscoveryHidden`/`SamplesDisabled`/`HiddenColumns`, `DatasetCatalogAdminService.cs`, `PUT /api/v1/catalog/datasets/{id}/discovery`, and unit tests) against an earlier `main` (`24b53481`).
- Since `24b53481`, `main` has landed `WP-SIMPL-45 PR 3`, steps `0010_table_column_ordinal`, `0011_role_grantee_and_profile_assignment_retirement`, and `0012_scim_external_identity_and_group_links`, unified `GovernanceRepository`, `CatalogApiEndpoints.Definition.cs`, `#672` (`CatalogWriteScope`), and `#717` (endpoint-count ratchet).

### 1.2 Exact Implementation Steps & Binding Security Conditions

1. **Port `bfc86f73` Cleanly Onto `origin/main` and Number Migration Step `0013_catalog_discovery`:**
   - Create worktree `/work/wt/simpl-41-pr2-discovery-flags` branching fresh from `origin/main`.
   - Use `git show bfc86f73` strictly as a diff reference and port the changes cleanly onto current `main` (`src/Autheris.Api/Endpoints/CatalogApiEndpoints.Definition.cs`, unified `src/Autheris.Infrastructure/Persistence/GovernanceRepository*.cs`, and `src/Autheris.Application/Catalog/`).
   - Create migration step `0013_catalog_discovery`:
     - `src/Autheris.Infrastructure/Persistence/Migrations/Steps/postgresql/0013_catalog_discovery.sql`
     - `src/Autheris.Infrastructure/Persistence/Migrations/Steps/postgresql/0013_catalog_discovery.shape.json`
     - `src/Autheris.Infrastructure/Persistence/Migrations/Steps/sqlserver/0013_catalog_discovery.sql`
     - `src/Autheris.Infrastructure/Persistence/Migrations/Steps/sqlserver/0013_catalog_discovery.shape.json`
   - Build `0013_catalog_discovery.shape.json` directly from `0012_scim_external_identity_and_group_links.shape.json` (both `postgresql` and `sqlserver`, setting `"version": 13`), update `PostgreSqlBaselineSchema.cs` and `SqlServerBaselineSchema.cs`, and update `tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.{postgresql,sqlserver}.txt`.
   - Ensure **zero** `WP-` / `SEC-` / `OD-` tokens appear in any comment under `src/**/*.cs` or `src/**/*.sql` (`PlanIdCommentRatchetTests`, `StepSqlLintTests`).

2. **Verify Binding Security Conditions (`SEC-DISC-01..16` & `#672` `SEC-OI-672-2`):**
   - `DiscoveryOwner(string DisplayName, string? Email)` MUST NEVER expose `AdSid` or `AdAccount`.
   - Degraded overlay (`DatasetCatalogStore.ReadOverlayAsync` failure) MUST fail closed (`RowOverlay.Secure` treats every table as hidden and increments `CatalogVisibilityDegradedCounter`).
   - `PUT /api/v1/catalog/datasets/{id}/discovery` requires `GovernanceAdmin` of the tenant or the table's data owner/delegate (`IsAuthorizedApproverForTableAsync`); out-of-tenant tables return `404` (no existence oracle) and unauthorized same-tenant callers get `403`.
   - `CATALOG_VISIBILITY_CHANGED` audit is `FailClosed` (audit failure rolls back the flag change).
   - `CatalogGovernanceRatchet`: dbt meta `autheris_discovery_hidden: true` is a one-way ratchet (ingestion never un-hides a table or column that an owner/admin hid).
   - `#672` integration: setting `DiscoveryHidden = true`, adding `HiddenColumns`, or setting `SamplesDisabled = true` via `DatasetCatalogStore` / `GovernanceRepository` flushes any active `CatalogWriteScope` immediately (`FlushScopePendingAsync`) and increments the table epoch.

### 1.3 Local Verification Commands
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DiscoveryDomainModelTests|FullyQualifiedName~DatasetCatalogStoreOverlayTests|FullyQualifiedName~DatasetCatalogAdminServiceTests|FullyQualifiedName~CatalogTagsVisibilityEndpointTests|FullyQualifiedName~CatalogGovernanceRatchetTests|FullyQualifiedName~StepSqlLintTests|FullyQualifiedName~GovernanceMigrationStepParityTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~CutoverArchitectureTests|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id|FullyQualifiedName~Endpoint" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 2: `WP-OI-SQL-486` — Required Filter Column Satisfaction on All Read & DML Paths (`#486`)

- **Worktree:** `/work/wt/oi-sql-486-required-filters`
- **Branch:** `feat/oi-sql-486-required-filters`
- **Issue:** `#486`
- **Sequencing Note:** Shares `src/Autheris.Application/OData/ODataFilterParser.cs` with Wave 2 Brief 4B (`WP-OI-SQL-478-B`). Start after `478-B` merges or rebase onto it.
- **Plan & Phase 3 References:**
  - `docs/plans/2026-11-13-plan-issues-sql-engine-dialects.md` (§ `#486`, lines 69–80, and `Q-SQL-1` confirmed at line 98)
  - `docs/plans/2026-11-13-sec-issues-group-5.md` (§ `#486`, `SEC-OI-486-1` .. `SEC-OI-486-5`, lines 24–33)
  - `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-SEC5-3` confirmed: comparisons against another column of the same table `a.col = a.other` never satisfy a required filter).

### 2.1 Context & Current State on `main` (`75bcf65a`)
- `src/Autheris.Application/DataSources/Services/GovernedTableReadPlanner.cs:542` (`EnsureRequiredFilterColumnsSatisfied`) passes `filter?.ReferencedColumns` to `RequiredFilterColumnRule.IsColumnSatisfied` (`src/Autheris.Application/DataSources/Services/RequiredFilterColumnRule.cs:71-107`), which accepts **any mention** of the column (e.g. `col <> 1`, `col IS NOT NULL`, `col LIKE '%'`, `NOT (col = 1)`, `col = 1 OR other = 2`).
- Keyset path `src/Autheris.Application/DataSources/Services/GovernedTableReadPlanner.Keyset.cs:437-438` also uses `ReferencedColumns` with only the VF-06 empty-`notin` special case.
- Meanwhile, `TableFilterClause.ConstrainedColumns` (`src/Autheris.Application/Common/Interfaces/IGatewayExecutionService.cs:74`, `"null = none, fail closed"`) is only populated by federation (`PushedClauseBuilder.cs:182`, `CrossSourcePlanner.cs:442`), and NOT by `TreeFilterClauseBuilder.Build` (`src/Autheris.Application/Sql/Tree/TreeFilterClauseBuilder.cs:31`), `ODataFilterParser.cs:62`, `SqlTableReadExecutor.cs:356,393`, or `SemanticMetricQueryService.ConsentAndEvaluation.cs:309`.
- Furthermore, per confirmed owner decision **`Q-SQL-1`** and **`SEC-OI-486-2`**, `LIKE` and `IS NULL` must NO LONGER count as constraining a required filter column on **any** path — including WebSQL (`src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.RequiredFilters.cs:256-267`) and federation (`src/Autheris.Application/Sql/Federation/Pushdown/RequiredFilterConstraint.cs:73-78`)!

### 2.2 Files to Modify / Create
- **Modify:**
  - `src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.RequiredFilters.cs` (remove `LikeExpression` and `UnaryExpression { Operator: UnaryOperator.IsNull }` from `IsColumnConstrained` at lines 256–267; ensure function-wrapped columns like `lower(col) = 'x'`, comparison to `NullLiteral`, and column-to-column comparisons `a.col = a.other` return `false` per `SEC-OI-486-2` and `Q-SEC5-3`)
  - `src/Autheris.Application/Sql/Federation/Pushdown/RequiredFilterConstraint.cs` (remove `LikeExpression` and `IsNull` at lines 73–78 and reject ` NullLiteral` / function-wrapped / column-to-column comparisons so federation mirrors `AstSecurityVisitor.RequiredFilters` 1:1)
  - `src/Autheris.Application/DataSources/Services/RequiredFilterColumnRule.cs` (rename/tighten parameter `clientConstrainedColumns` and document that this is a cost/DoS guard while row-policy and tenant-column satisfaction are separate, `SEC-OI-486-3`)
  - `src/Autheris.Application/DataSources/Services/GovernedTableReadPlanner.cs` (`EnsureRequiredFilterColumnsSatisfied`: pass `filter?.ConstrainedColumns` — never `filter?.ReferencedColumns`!)
  - `src/Autheris.Application/DataSources/Services/GovernedTableReadPlanner.Keyset.cs` (use `filter?.ConstrainedColumns` and delete the VF-06 special case; keyset cursor bounds in `KeySetClauseBuilder` never set `ConstrainedColumns`)
  - `src/Autheris.Application/Sql/Tree/TreeFilterClauseBuilder.cs` (compute and set `ConstrainedColumns` on the returned `TableFilterClause`: `TreeAndFilter` = union of children's constrained columns; `TreeOrFilter` = intersection of all non-empty children's constrained columns; `TreeNotFilter` = empty; `TreeComparison` = `[column.CatalogName]` iff operator is `eq`, `gt`, `gte`, `lt`, `lte`, or non-empty `in` with non-null value)
  - `src/Autheris.Application/OData/ODataFilterParser.cs` (compute and set `ConstrainedColumns` on the parsed filter result using the exact same boolean/operator rule: `AND` = union, `OR` = intersection, `NOT` = empty, `ComparisonNode` with `=`, `<`, `<=`, `>`, `>=` against a non-null literal parameter = `[ColumnName]`, `InNode` non-empty and not negated = `[ColumnName]`, `FunctionCallNode` / `NullCheckNode` / `<>` = empty)
  - `src/Autheris.Infrastructure/DataSources/SqlTableReadExecutor.cs` and `src/Autheris.Application/Catalog/Semantic/SemanticMetricQueryService.ConsentAndEvaluation.cs` (populate `ConstrainedColumns` on structured equality/range/IN filter clauses)
- **Create / Extend Tests:**
  - `tests/Autheris.Tests.Unit/Data/RequiredFilterColumnRuleTests.cs`
  - `tests/Autheris.Tests.Unit/Sql/TreeFilterClauseBuilderConstrainedColumnsTests.cs`
  - `tests/Autheris.Tests.Unit/OData/ODataRequiredFilterConstraintTests.cs`
  - `tests/Autheris.Tests.Architecture/CleanArchitectureTests.cs` (architecture test grepping `IsColumnSatisfied` / `RequiredFilterColumnRule` callers so none pass `.ReferencedColumns`, `SEC-OI-486-1`)

### 2.3 Binding Security Conditions (`SEC-OI-486-1` .. `SEC-OI-486-5`)
- **SEC-OI-486-1:** `TableFilterClause.ConstrainedColumns` is `null` by default (= nothing constrained, fail closed). The fallback to `ReferencedColumns` and the VF-06 special case are deleted. An architecture test scans `src/**/*.cs` to assert no caller of `RequiredFilterColumnRule.IsSatisfied` or `IsColumnSatisfied` passes `ReferencedColumns`.
- **SEC-OI-486-2:** Only `=`, `<`, `<=`, `>`, `>=` with a non-null literal/parameter, `BETWEEN` (not negated, non-column bounds), and non-empty `IN` (not negated) count. Never counted on ANY path (REST, GraphQL, MCP, OData, WebSQL, federation, DML `WHERE`): `col = col` or column-to-column comparison (`Q-SEC5-3`), comparison to `NULL` (`col = NULL`), function-wrapped column (`lower(col) = 'x'`), `<>`, `NOT`, `NOT IN`, `LIKE`, `IS NULL`, `IS NOT NULL` (`Q-SQL-1`). `AND`: any branch; `OR`: every branch must constrain the column; `NOT`: none. Keyset cursor bounds never count.
- **SEC-OI-486-3:** Row-policy and tenant-column satisfaction stay as today; document in a code comment on `RequiredFilterColumnRule` that this is a cost guard and a policy predicate mention counts because policies are operator-authored.
- **SEC-OI-486-4:** DML (`AllowDml` `UPDATE`/`DELETE` `WHERE`) uses the same rule through `AstSecurityVisitor.RequiredFilters`; pin with test `Dml_Where_Like_On_Required_Column_Is_Rejected`.
- **SEC-OI-486-5:** The error stays the fixed `REQUIRED_FILTER_COLUMN_MISSING` message (`GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing)`) and never names the missing column in client output.

### 2.4 Required Unit & Architecture Tests (`[Trait("Category", "Security")]`)
1. `RequiredFilterColumnRuleTests`:
   - `Neq_Does_Not_Satisfy`
   - `IsNull_Does_Not_Satisfy` (`Q-SQL-1`)
   - `Like_Percent_Does_Not_Satisfy` (`Q-SQL-1`)
   - `NotIn_Does_Not_Satisfy`
   - `Or_With_Unconstrained_Branch_Does_Not_Satisfy`
   - `Not_Eq_Does_Not_Satisfy`
   - `Eq_In_Range_Satisfy`
   - `Or_Constrained_On_Both_Branches_Satisfies`
   - `Null_ConstrainedColumns_Fails_Closed` (`SEC-OI-486-1`)
   - `ColumnToColumn_And_NullComparison_Do_Not_Satisfy` (`SEC-OI-486-2`, `Q-SEC5-3`)
   - `FunctionWrappedColumn_Does_Not_Satisfy` (`SEC-OI-486-2`)
   - `Between_And_NonEmptyIn_Satisfy` (`SEC-OI-486-2`)
   - `New_Filter_Producer_Without_ConstrainedColumns_Fails_Closed` (`SEC-OI-486-1`)
   - `Dml_Where_Like_On_Required_Column_Is_Rejected` (`SEC-OI-486-4`)
   - `Error_Does_Not_Name_Column` (`SEC-OI-486-5`)
2. `GovernedTableReadPlannerKeysetRequiredFilterTests`, `ODataRequiredFilterConstraintTests`, and `TreeFilterClauseBuilderConstrainedColumnsTests`: cover the same operator matrix on keyset, OData, and TreeFilter paths.
3. Architecture test: `RequiredFilterColumnRule_NeverCalledWithReferencedColumns` (`SEC-OI-486-1`).

### 2.5 Local Verification Commands
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~RequiredFilter|FullyQualifiedName~TreeFilterClauseBuilder|FullyQualifiedName~ODataRequiredFilter" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/TrinoSqlEngine.Tests/TrinoSqlEngine.Tests.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~RequiredFilter" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~RequiredFilter|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 3: `WP-SIMPL-45 PR 4A & PR 4B` — Governed Write Access: Consent Write Workflow (`PR 4A`) and Decision-Point Rewriter Wiring (`PR 4B`)

- **Plan & Phase 3 References:**
  - `docs/plans/simplification/WP-SIMPL-45-governed-write-access.md` (§5 steps 3, 5, 8, 13; §6 tests; §9 PR 4; §12 `SEC-SIMPL-120` .. `SEC-SIMPL-127`)
  - `docs/plans/simplification/WP-SIMPL-45-pr3-ci-repair-addendum.md`

### 3.1 Context & Verified Split into `PR 4A` and `PR 4B` on `main` (`173703d6`)
- `WP-SIMPL-45 PR 1-3` (`#651`, `#652`, `#653`) and `WP-SIMPL-46 PR 1-6b` (`2b2876bd`) are merged on `main`, providing `WriteRule`, `WriteOperation`, `WriteRowScope` (`src/Autheris.Domain/Model/WriteGrantModels.cs`), migration `0009_write_grants`, `IWriteGrantEvaluator` / `WriteGrantEvaluator` (`src/Autheris.Application/Policy/WriteGrants/WriteGrantEvaluator.cs`), and `DmlWritePermit` in `TrinoSqlEngine`.
- **Why `WP-SIMPL-45 PR 4` is split into two `<= 400` `src/` line PRs (`PR 4A` and `PR 4B`):**
  - On `main` today, `AccessDecisionPoint.cs` still calls `_policy.CanWriteTableAsync` and `DmlWriteDecisionArchitectureTests` asserts that `GovernedSqlRewriter.cs` calls `CanWriteTableAsync(`.
  - **Brief 3A (`WP-SIMPL-45 PR 4A` — Steps 3 + 13, `/work/wt/simpl-45-pr4a-write-workflow`, branch `feat/simpl-45-pr4a-write-workflow`):**
    1. **Step 3 (Consent Write Workflow + `CONSENT_WRITE_RULES` Persistence/Hydration):**
       - `ConsentRequestSubmission` (`src/Autheris.Application/ConsentRequests/IConsentRequestService.cs:21`) gains `IReadOnlyList<WriteRule>? RequestedWriteRules = null`.
       - `ConsentRequestService.SubmitAsync` (`src/Autheris.Application/ConsentRequests/ConsentRequestService.cs`) validates requested write rules against the catalog: rejects unknown columns, write-protected columns (`column.WriteProtected` or primary-key columns on `Update`), policy-dependency tables (`IRowPolicyDependencyIndex`), and `WriteOperation.Delete` while `WebSql:Dml:AllowDeleteGrants == false`. A write request needs no read request (`OD-33`).
       - Approval copies the approved write rules (the approver may narrow columns/operations/maxRows, never widen beyond `RequestedWriteRules`) onto the created `Consent` record (`Consent.WriteRules`), persists/hydrates `CONSENT_WRITE_RULES` and `CONSENT_REQUESTS.requested_write_rules_json` in `GovernanceRepository.Consent*.cs`, and emits `WRITE_GRANT_CHANGED` required audit events on grant/approval/revocation.
    2. **Step 13 (`DmlWriterRoles` Removal):**
       - Delete `WebSqlOptions.DmlWriterRoles` (`src/Autheris.Domain/Options/Protocols/RestApiOptions.cs:95`), `InertOptionWarnings.cs:29-35`, and the writer-role startup check in `GraphQlAndSwitchStartupChecks.cs:151-154`.
       - Add `"WebSql:DmlWriterRoles"` to `LegacySwitchGuard.Removed` / `UnknownGatewayKeyGuard` so configuring `Gateway:WebSql:DmlWriterRoles` fails startup with the replacement message (`SEC-SIMPL-31`).
  - **Brief 3B (`WP-SIMPL-45 PR 4B` — Steps 5 + 8, `/work/wt/simpl-45-pr4b-rewriter-wiring`, branch `feat/simpl-45-pr4b-rewriter-wiring`, runs after `PR 4A`):**
    1. **Step 5 (`AccessDecisionPoint` `IWriteGrantEvaluator` Wiring & `GovernedSqlRewriter` `DmlWritePermit`):**
       - Wire `IWriteGrantEvaluator` into `AccessDecisionPoint` (`src/Autheris.Application/Authorization/AccessDecisionPoint.cs`) for `AccessOperation.Insert | Update | Delete`.
       - In `src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs`, replace `CanWriteTableAsync` (`lines 581-585`) with `IAccessDecisionPoint.DecideAsync` for `AccessOperation.Insert | Update | Delete` (passing the written column set) and build `DmlWritePermit(operation, writableColumns, writeProtectedColumns)` on `GovernancePolicy.Write`.
       - Delete `TableAccessPolicy.CanWriteTableAsync` (both overloads) and `DmlWriteDecisionTestSeam`, and update `DmlWriteDecisionArchitectureTests` / `ArchitectureAndCleanCodeTests.DmlWritePermit_IsConstructedOnlyInTheEngine_UntilTheDecisionPointIsWired`.
       - Enforce `OwnRows` vs `AllRows` row scope (`SEC-SIMPL-122`).
    2. **Step 8 (`DmlTransports` Gate):**
       - Enforce `DmlTransports.Allowed = { SqlTransports.WebSql }` in `GovernedSqlRewriter.cs` (`dml_transport_not_wired`, audited as `WEBSQL_DML_REJECTED`), and ensure OData/GraphQL/REST/Trino/MCP reject writes fail-closed (`SEC-SIMPL-125`).

### 3.2 Required Unit Tests (`[Trait("Category", "Security")]`)
1. `tests/Autheris.Tests.Unit/ConsentRequests/WriteConsentWorkflowTests.cs`:
   - `Owner_MayNarrowButNotWiden_RequestedWriteRules`
   - `NonOwner_CannotApproveWriteRules`
   - `WriteRequest_WithoutReadRequest_IsAccepted`
   - `ApprovedWriteGrant_HasValidFromAndValidUntil`
   - `WriteRule_OnWriteProtectedColumn_IsRefused`
   - `WriteRule_OnPolicyDependencyTable_IsRefused`
   - `GrantChange_IsAuditedRequired`
2. `tests/Autheris.Tests.Unit/Configuration/WriteGrantStartupTests.cs`:
   - `DmlWriterRolesKey_FailsStartup`
3. `tests/Autheris.Tests.Unit/Sql/DmlTransportMatrixTests.cs` & `WriteGrantTests.cs`:
   - `Dml_IsAccepted_OnWebSqlWithGrant`
   - `Insert_allowed_without_read_grant`
   - `Write_grant_never_grants_read`
   - `Update_own_rows_applies_callers_rls_predicate`
   - `Update_own_rows_without_read_grant_uses_write_grant_row_filter`
   - `Delete_off_by_default`

### 3.3 Local Verification Commands
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~WriteConsentWorkflowTests|FullyQualifiedName~WriteGrantStartupTests|FullyQualifiedName~WriteGrantTests|FullyQualifiedName~DmlTransportMatrixTests|FullyQualifiedName~WriteGrantEvaluatorTests|FullyQualifiedName~DmlSecurityTests" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~DmlWritePermit|FullyQualifiedName~Options|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet build tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj -c Release -p:AutherisIncludeTestAuth=true --no-dependencies -m:1 -nodeReuse:false
```

---

## Brief 4: `WP-OI-SCALE-4 PR 1` — `CatalogSnapshotProvider` Change-Event Coalescing & Backstop (`#675` PR 1)

- **Worktree:** `/work/wt/oi-scale-675-pr1-coalescing`
- **Branch:** `feat/oi-scale-675-pr1-coalescing`
- **Issue:** `#675` (PR 1 — event coalescing & refresh loop; no DDL in PR 1)
- **Plan & Phase 3 References:**
  - `docs/plans/2026-11-13-plan-issues-catalog-scale.md` (§ `#675`, lines 82–93)
  - `docs/plans/2026-11-13-sec-issues-group-7.md` (§ `#675`, `SEC-OI-675-3`, `SEC-OI-675-4`, `SEC-OI-675-5`, lines 63–76)
  - `docs/plans/2026-11-14-owner-decisions-sec-groups-1-9.md` (`Q-SEC7-2` confirmed: full snapshot reload at least every 10 minutes as a constant backstop).

### 4.1 Context & Current State on `main` (`75bcf65a`)
- `src/Autheris.Application/Catalog/CatalogSnapshotProvider.cs:39-51` subscribes to `ICatalogVersionProvider.CatalogChangedEventChannel` and calls `await RefreshAsync()` directly on every event.
- During a burst of events, callers queue on `_refreshLock` (`SemaphoreSlim(1, 1)`) and run back-to-back full reloads (`GetAllTablesAsync` + `GetAllRelationsAsync`), or if `InProcessChannelEventBus` drops/times out a slow subscriber after 2 s, the last signal in a burst can be lost.

### 4.2 Files to Modify / Create
- **Modify:**
  - `src/Autheris.Application/Catalog/CatalogSnapshotProvider.cs`
  - `src/Autheris.Application/Catalog/CatalogSnapshot.cs` (verify immutability of exposed collections per `SEC-OI-675-4`)
- **Create Tests:**
  - `tests/Autheris.Tests.Unit/Catalog/CatalogSnapshotProviderCoalescingTests.cs`

### 4.3 Exact Implementation Requirements & Binding Security Conditions (`SEC-OI-675-3` .. `SEC-OI-675-5`, `Q-SEC7-2`)

1. **Coalescing Signal Channel & 1 s Minimum Interval (`SEC-OI-675-5`):**
   - Replace the direct `async _ => await RefreshAsync()` event callback in `CatalogSnapshotProvider` with a capacity-1 `Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest })` signal channel and a single background drain loop (injecting `TimeProvider? timeProvider = null`, defaulting to `TimeProvider.System` so unit tests control time deterministically without real sleeps!).
   - Define constants on `CatalogSnapshotProvider`:
     - `public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(1);`
     - `public static readonly TimeSpan FullReloadBackstopInterval = TimeSpan.FromMinutes(10);` (`Q-SEC7-2`, `SEC-OI-675-3`)
     - `public const int DegradedFailureThreshold = 3;` (`SEC-OI-675-5`)
   - **No Lost Last Event (`SEC-OI-675-5`):**
     - When a signal is received from the channel, if less than `MinimumRefreshInterval` has elapsed since the last refresh completed, wait the remaining interval via `_timeProvider`, drain any queued token, and then call `RefreshAsync` (which reads `_versionProvider.GetCatalogVersionAsync` **after** the wait/signal). If another event arrives *during* `RefreshAsync`, the capacity-1 channel holds that pending signal so the loop immediately performs one follow-up check after the minimum interval and always converges to the final catalog version!
   - **10-Minute Full-Reload Backstop (`SEC-OI-675-3`, `Q-SEC7-2`):**
     - Even if the version appears unchanged on a periodic poll, if `_timeProvider.GetUtcNow() - _lastFullReloadUtc >= FullReloadBackstopInterval`, force a full reload.
   - **Failure Tracking & Health (`SEC-OI-675-5`):**
     - Track `_consecutiveFailures`; increment on table/relation/version read failure and reset to `0` on success. Expose `bool IsDegraded => Volatile.Read(ref _consecutiveFailures) >= DegradedFailureThreshold;` while continuing to serve the last valid `_currentSnapshot` until a refresh succeeds.
   - **Immutability (`SEC-OI-675-4`):**
     - Ensure `CatalogSnapshot` exposes only immutable collections (`IReadOnlyList` / `IReadOnlyDictionary` backed by `ImmutableArray` / `FrozenDictionary` / `ReadOnlyCollection`) so no consumer can mutate a published snapshot in place.

### 4.4 Required Unit Tests (`tests/Autheris.Tests.Unit/Catalog/CatalogSnapshotProviderCoalescingTests.cs`)
Mark security tests with `[Trait("Category", "Security")]` (and use `FakeTimeProvider` — zero `Task.Delay` wall-clock sleeps per `TestSleepRatchetTests`!):
1. `Burst1000Events_AtMostOneRefreshPerInterval`
2. `RefreshAfterBurst_SeesFinalVersion` (`[Trait("Category", "Security")]`, `SEC-OI-675-5` / `LastEventOfBurst_IsNotLost`)
3. `EventDuringRefresh_TriggersFollowUpRefreshAfterMinimumInterval` (`[Trait("Category", "Security")]`, `SEC-OI-675-5`)
4. `ThreeConsecutiveRefreshFailures_MarksProviderDegraded_AndKeepsPreviousSnapshot` (`[Trait("Category", "Security")]`, `SEC-OI-675-5`)
5. `FullReloadBackstop_ForcesReloadAfterTenMinutesEvenWhenVersionUnchanged` (`[Trait("Category", "Security")]`, `SEC-OI-675-3`, `Q-SEC7-2`)
6. `Entries_AreImmutable` (`[Trait("Category", "Security")]`, `SEC-OI-675-4`)

### 4.5 Local Verification Commands
```bash
MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~CatalogSnapshotProviderCoalescingTests|FullyQualifiedName~CatalogSnapshot" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s

MSBUILDCOPYTASKPARALLELISM=1 NUGET_PACKAGES=/work/nuget dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj -c Release -p:AutherisIncludeTestAuth=true --filter "FullyQualifiedName~TestSleepRatchetTests|FullyQualifiedName~DocsIntegrityTests|FullyQualifiedName~Plan_id" -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s
```

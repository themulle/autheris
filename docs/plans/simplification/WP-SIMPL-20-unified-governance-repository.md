# WP-SIMPL-20 Unified governance repository (AD-1)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-20-unified-governance-repository-ad-1---persistence-lane-serial-steps) | Issues: #515 | Owner decisions: OD-1 (binding, PostgreSQL + SQL Server only), D4 (keep SQL Server) | Security conditions: SEC-SIMPL-05, SEC-SIMPL-20..26, SEC-SIMPL-16, SEC-SIMPL-03, SEC-SIMPL-01

## 1. Goal and outcome
Replace the two per-engine governance repository families (`PostgreSqlGovernanceRepository*`, `SqlServerGovernanceRepository*`) and the `*SqlCore` helpers with one `GovernanceRepository` (partial per aggregate) plus an `IGovernanceSqlDialect` fragment provider with two sealed implementations. Measurable: AD-1 LOC metric (`*GovernanceRepository*.cs` + `*SqlCore.cs` + `*GovernanceDialect.cs`) <= 7,000 at the end of 20d (program exit <= 6,000 after WP-SIMPL-33); duplicated-code ratio < 15%; 0 types prefixed `PostgreSql`/`SqlServer` that end in `GovernanceRepository`; the WP-SIMPL-19 contract suite green on both engines before and after every PR.

## 2. Preconditions and dependencies
- Merged first (SEC-SIMPL-05, binding order): WP-SIMPL-11 (SQLite governance family deleted), WP-SIMPL-12 (ITSM deleted, so `ItsmOutboxSqlCore` is gone), WP-SIMPL-19 (contract suite green on PostgreSQL and SQL Server against the current two families).
- Serial persistence lane: 20a -> 20b -> 20c -> 20d -> 20e, one PR each, each rebased on the previous merge. No other WP may edit `src/Autheris.Infrastructure/Persistence/*Governance*`, `*SqlCore.cs` or `GatewayGovernanceServiceExtensions.cs` while the lane is open.
- May run in parallel: WP-SIMPL-21, 22, 24 and Stage 3 module WPs that do not touch the files above.

## 3. Scope
### In scope
- New `IGovernanceSqlDialect`, `PostgreSqlGovernanceDialect`, `SqlServerGovernanceDialect` in `src/Autheris.Infrastructure/Persistence/Dialects/`.
- New `GovernanceRepository.cs` + `GovernanceRepository.<Aggregate>.cs` partials (the 26 partial names of the current families).
- Folding `ConsentSqlCore`, `ConsentGrantSqlCore`, `CatalogSqlCore`, `CdcStreamSqlCore`, `DataOwnerAssignmentSqlCore`, `WebhookOutboxSqlCore` into the partials; `SqlCoreBinding` becomes a private helper of `GovernanceRepository`.
- `AuditSqlCore` stays a separate narrow component (SEC-SIMPL-24), consumed by `GovernanceRepository.Audit.cs`.
- DI rewiring in `GatewayGovernanceServiceExtensions.cs` (currently lines 88 and 94, generic `RegisterGovernanceAggregateStores<TRepository,TMigrationRunner>` at 124).
- 20e: one forward-only migration step per engine dropping orphaned tables of deleted features; `GovernanceSchemaShapeVerifier` shape files updated.
### Out of scope
- Migration runners, baselines and step trees stay hand-written per engine (AD-1 item 5); no generator.
- Data-source dialect providers in `SqlDialectProviders.cs` (`ISqlDialectProvider`), `TrinoSqlEngine` generators.
- EF Core, Dapper or any new package (AD-1 option D rejected).
- Changing audit hash input fields (needs ADR addendum, SEC-SIMPL-24).

## 4. Current state (verified on simpl-refine @ 63cbb893, before WP-SIMPL-11/12)
- Families in `src/Autheris.Infrastructure/Persistence/`: PostgreSql 26 files / 5,243 lines; SqlServer 26 / 5,276; Sqlite 26 / 5,241 (deleted by WP-11). Largest PG partials: `.Audit.cs` 1,064, `.Catalog.cs` 1,031, `.Consent.cs` 809, `.cs` 357, `.ConsentGrants.cs` 327, `.Schema.cs` 313.
- Cores: `AuditSqlCore.cs` 629, `CatalogSqlCore.cs` 840, `CdcStreamSqlCore.cs` 689, `ConsentGrantSqlCore.cs` 160, `ConsentSqlCore.cs` 997, `DataOwnerAssignmentSqlCore.cs` 305, `ItsmOutboxSqlCore.cs` 136 (deleted by WP-12), `SqlCoreBinding.cs` 35, `WebhookOutboxSqlCore.cs` 125.
- Baseline for the AD-1 metric after WP-11/12: 5,243 + 5,276 + 3,780 = 14,299 lines (plan rounds to ~14,480; the developer records the measured value in the 20a PR).
- `Persistence/Dialects/SqlDialectProviders.cs` (147 lines) holds data-source providers (`SqlServerDialectProvider` :59, `PostgreSqlDialectProvider` :77, `SqliteDialectProvider` :95, `OracleDialectProvider` :113, `DatabricksDialectProvider` :131); `DialectValueCodec.cs`, `DatabaseParameterBudgetProvider.cs` exist and are reused.
- Migrations: steps 0002-0005 exist in `Migrations/Steps/{postgresql,sqlserver}` (`.sql` + `.shape.json`); `Shape/GovernanceSchemaShapeVerifier.cs` (43 lines); `tests/Autheris.Tests.Architecture/GovernanceMigrationFreezeTests.cs` exists.

## 5. Implementation steps (one PR per step; contract suite green before and after)
1. **20a dialect + Consent.** Create `IGovernanceSqlDialect` (internal, in `Persistence/Dialects/IGovernanceSqlDialect.cs`) with exactly: `string Upsert(GovernanceTable table, ReadOnlySpan<string> keyColumns, ReadOnlySpan<string> valueColumns)`, `string InsertReturning(string column)`, `string LockRowsForUpdate`, `string UtcNow`, `string JsonValue(string column, string path)`, `string StringAggregate(string expr, string separator)`, `string LimitWithoutOrder(int n)`, `bool IsUniqueViolation(DbException ex)`, `bool IsTransient(DbException ex)`, `IParameterBudgetProvider ParameterBudget`, `DbConnection CreateConnection()`. Identifiers are `static readonly` names from a closed `GovernanceTable`/column allowlist (SEC-SIMPL-21). `SqlServerGovernanceDialect.Upsert` emits `MERGE ... WITH (HOLDLOCK)`; `CreateConnection` asserts `QUOTED_IDENTIFIER ON` (run `SET QUOTED_IDENTIFIER ON` on open and throw `InvalidOperationException` if `SESSIONPROPERTY('QUOTED_IDENTIFIER')` is not 1). Create `GovernanceRepository.cs` (ctor takes `IGovernanceSqlDialect`, `ILogger`, options; shared executor logs fragment id + duration only, SEC-SIMPL-25) and the `TenantScope` builder: every tenant-scoped statement requires a non-null `TenantId` argument, throws `ArgumentException` when absent; global tables listed in `GovernanceGlobalTables` with a comment (SEC-SIMPL-22). Move Consent, ConsentGrants, ConsentPage: fold `ConsentSqlCore`/`ConsentGrantSqlCore` into `GovernanceRepository.Consent.cs`, `.ConsentGrants.cs`, `.ConsentPage.cs`; SQL written once (double-quoted identifiers, `OFFSET/FETCH`, booleans and timestamps as parameters via `DialectValueCodec`). Delete the PG/SS Consent* partials and both cores. Page-size caps and batch maxima copied unchanged (SEC-SIMPL-23). During 20a-20c the PG/SS family classes stay registered and delegate the moved aggregates to an internal `GovernanceRepository` instance (constructed with the matching dialect) so DI does not change until 20d.
2. **20b Catalog + Schema.** Fold `CatalogSqlCore` into `GovernanceRepository.Catalog.cs`; move `.Schema.cs`, `.PhysicalIndexes.cs`. Delete PG/SS Catalog/Schema/PhysicalIndexes partials and `CatalogSqlCore.cs`.
3. **20c Audit (+ anchor stores).** `GovernanceRepository.Audit.cs` delegates writes to `AuditSqlCore` (kept, renamed nothing, made dialect-neutral by taking `IGovernanceSqlDialect`; batch insert path unchanged). Anchor store in `AuditChainAnchorStores.cs`: one `GovernanceAuditChainAnchorStore` using the dialect; startup fails if no `IAuditChainAnchorStore` registration (SEC-SIMPL-12 stays). Hash input fields untouched.
4. **20d remaining partials + DI switch.** Move AccessProfiles, CallerAttributes, CdcStreamKeys, CdcStreams (fold `CdcStreamSqlCore`), CdcWatermarks, CloudEventSubscriptions, ConsumerAggregation, DataOwnerAssignment (fold core), Identity, Outbox, Recertification, RightsAndPolicies, SemanticManifests, TenantRegistry, TenantTables, TrustedDocuments, VirtualFilters, WebhookOutbox (fold core). Delete all `PostgreSqlGovernanceRepository*.cs`, `SqlServerGovernanceRepository*.cs`, `SqlCoreBinding.cs` (logic moves into `GovernanceRepository.cs`). DI: `RegisterGovernanceAggregateStores<GovernanceRepository, TMigrationRunner>` with the dialect chosen by provider (`PostgreSql` -> `PostgreSqlGovernanceDialect`, `SqlServer` -> `SqlServerGovernanceDialect`); unknown provider keeps failing via `GovernanceProviderGuard` (WP-11).
5. **20e orphan-table step.** Add `Migrations/Steps/postgresql/0006_drop_orphaned_feature_tables.{sql,shape.json}` and `Migrations/Steps/sqlserver/0006_drop_orphaned_feature_tables.{sql,shape.json}` dropping only tables whose feature was deleted (ITSM outbox; Casbin and FinOps tables only if they exist in the baseline - the developer lists each table with the deleting WP in the PR). `DROP TABLE IF EXISTS` on PG; `IF OBJECT_ID(N'...', N'U') IS NOT NULL DROP TABLE` on SQL Server. Do not modify 0002-0005 or the baselines (SEC-SIMPL-16).

## 6. Tests
- Contract (existing from WP-19, `Autheris.Tests.Integration/Governance/Contract/`): every aggregate class runs on both fixtures before and after each PR; no contract test is edited to make a step green.
- New, `Autheris.Tests.Architecture`:
  - `NoPerDialectGovernanceRepositoryArchitectureTests.No_dialect_prefixed_governance_repository_types` (20d): no type in Infrastructure matches `^(PostgreSql|SqlServer|Sqlite)GovernanceRepository`.
  - `GovernanceMigrationStepParityTests.Every_step_exists_for_both_engines` (20a, tightened in 20e): step ids in `Steps/postgresql` equal those in `Steps/sqlserver`.
  - `GovernanceRepositoryDuplicationTests.Duplicated_code_ratio_below_15_percent` (20d).
  - `ArchitectureTests.Governance_sql_is_built_only_from_constants_and_parameters` [Security] (20a, SEC-SIMPL-26): source scan of `GovernanceRepository*.cs`, `AuditSqlCore.cs`, `*GovernanceDialect.cs` fails on `$"`, `string.Format`, non-constant `+`, `StringBuilder.Append(<non-constant>)` outside the explicit allowlist.
  - `GovernanceRepositoryLocBudgetTests.Ad1_metric_at_most_7000_lines` (20d).
- New, `Autheris.Tests.Integration/Governance/` [Security]:
  - `GovernanceTenantScopeTests.Tenant_scoped_statement_without_tenant_throws` and `.Global_table_allowlist_is_exact` (20a, SEC-SIMPL-22).
  - `SqlServerGovernanceDialectTests.CreateConnection_asserts_quoted_identifier_on` and `.Upsert_uses_holdlock` (20a).
  - `GovernanceExecutorLoggingTests.Logs_contain_no_parameter_values` (20a, SEC-SIMPL-25).
  - `GovernanceRepositoryBoundsTests.Page_size_and_batch_caps_unchanged` (20a-20d, SEC-SIMPL-23, one case per aggregate moved).
  - `AuditChainCompatibilityTests.V1_5_0_chain_verifies_after_unification` (20c, SEC-SIMPL-24).
  - `GovernanceMigrationOrphanTableTests.Step_0006_drops_only_listed_tables` on both engines (20e).
- Delete: per-engine repository unit/integration tests whose cases are covered by the contract suite; each deleted Security test gets a coverage-map row (SEC-SIMPL-03, SEC-SIMPL-60).

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror` (one build at a time; `df -h` before).
- `dotnet test tests/Autheris.Tests.Integration --filter "FullyQualifiedName~Governance.Contract"` (both fixtures; podman host override).
- `dotnet test tests/Autheris.Tests.Architecture --filter "FullyQualifiedName~Governance|FullyQualifiedName~ArchitectureTests"`
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj`
- `git ls-files 'src/**/*GovernanceRepository*.cs' 'src/**/*SqlCore.cs' 'src/**/*GovernanceDialect.cs' | xargs cat | wc -l` -> <= 7,000 after 20d.
- `git grep -nE '(PostgreSql|SqlServer)GovernanceRepository' -- src` -> empty after 20d.

## 8. Acceptance criteria
- [ ] Contract suite green on PostgreSQL and SQL Server on each of the five PR merge commits.
- [ ] AD-1 LOC <= 7,000 after 20d (value recorded in PR); duplication ratio < 15%.
- [ ] No `PostgreSql*`/`SqlServer*GovernanceRepository*` and no `*SqlCore.cs` except `AuditSqlCore.cs`.
- [ ] `IGovernanceSqlDialect` has <= 15 members; two sealed implementations only.
- [ ] SEC-SIMPL-21..26 tests present, tagged Security, green; Security test count not lower than before 20a (or mapped).
- [ ] Migration steps 0002-0005 and baselines byte-identical; `GovernanceMigrationFreezeTests` unchanged and green; 0006 present for both engines.

## 9. PR slicing (`size-exception` not granted; src additions <= 400 net per PR, deletions unlimited)
1. PR 20a: dialect interface + 2 implementations, `GovernanceRepository.cs` executor/tenant scope, Consent/ConsentGrants/ConsentPage, SEC-SIMPL-21..26 tests. If > 400 added, split into 20a-1 (dialect + executor + tests) and 20a-2 (Consent aggregates).
2. PR 20b: Catalog, Schema, PhysicalIndexes.
3. PR 20c: Audit via `AuditSqlCore`, anchor store, chain-compatibility test.
4. PR 20d: remaining 18 partials, DI switch, delete both families, duplication + LOC tests. Split 20d-1 (CDC/Webhook/Outbox/CloudEvents) and 20d-2 (rest + deletion + DI) if > 400 added.
5. PR 20e: step 0006 per engine + shape files + orphan test.

## 10. Risks, rollback, and things the developer must NOT do
- Risks: SQL Server semantic drift (contract suite catches; fix SQL, never the test); MERGE concurrency (HOLDLOCK + unique-violation retry via `IsUniqueViolation`); `QUOTED_IDENTIFIER` off in pooled connections (assert in `CreateConnection`).
- Rollback: each PR is independently revertible because the family classes keep delegating until 20d; 20d is reverted as a whole.
- Do NOT: concatenate request input into SQL; fold `AuditSqlCore` into the generic executor; change audit hash fields; edit migration steps 0002-0005 or baselines; weaken or skip a contract test; add EF Core/Dapper; reintroduce any SQLite branch; log parameter values; mention advisory details (SEC-SIMPL-01).

## 11. Estimate
Src: about +4,300 / -11,800 (net about -7,500); tests about +900. 11 person-days (20a 3, 20b 3, 20c 3, 20d 1.5, 20e 0.5).

## Decisions taken in this spec
1. SEC-SIMPL-24 overrides AD-1 item 3 for audit: `AuditSqlCore` keeps its name and stays a separate component; all other `*SqlCore` names disappear.
2. Transitional delegation (family classes call into `GovernanceRepository`) keeps DI unchanged until 20d so every PR is green and revertible alone.
3. Orphan-table step is numbered 0006 in both trees and uses idempotent drops; it lists each dropped table with the deleting WP in the PR body.
4. LOC gate test at 7,000 lands in 20d; the 6,000 program target is enforced by WP-SIMPL-33.

# WP-SIMPL-20 Unified governance repository (AD-1)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-20-unified-governance-repository-ad-1---persistence-lane-serial-steps) | Issues: #515 | Owner decisions: OD-1 (binding, PostgreSQL + SQL Server only), D4 (keep SQL Server) | Security conditions: SEC-SIMPL-05, SEC-SIMPL-20..26, SEC-SIMPL-16, SEC-SIMPL-03, SEC-SIMPL-01

## 1. Goal and outcome
Replace the two per-engine governance repository families (`PostgreSqlGovernanceRepository*`, `SqlServerGovernanceRepository*`) and the `*SqlCore` helpers with one `GovernanceRepository` (partial per aggregate) plus an `IGovernanceSqlDialect` fragment provider with two sealed implementations. Measurable: AD-1 LOC metric (`*GovernanceRepository*.cs` + `*SqlCore.cs` + `*GovernanceDialect.cs`) <= 7,000 at the end of 20d (program exit <= 6,000 after WP-SIMPL-33); duplicated-code ratio < 15%; 0 types prefixed `PostgreSql`/`SqlServer` that end in `GovernanceRepository`; the WP-SIMPL-19 contract suite green on both engines before and after every PR.

## 2. Preconditions and dependencies
- Merged first (SEC-SIMPL-05, binding order): WP-SIMPL-11 (SQLite governance family deleted), WP-SIMPL-12 (ITSM deleted, so `ItsmOutboxSqlCore` is gone), WP-SIMPL-19 (contract suite green on PostgreSQL and SQL Server against the current two families).
- Serial persistence lane: 20a -> 20b -> 20c -> 20d -> 20e, one PR each, each rebased on the previous merge. No other WP may edit `src/Autheris.Infrastructure/Persistence/*Governance*`, `*SqlCore.cs` or `GatewayGovernanceServiceExtensions.cs` while 20a-20d are open (from the 20a branch cut to the 20d merge).
- Ordering rule for the owner-mandated WPs that add governance schema or aggregates (they run under the freeze exception): WP-SIMPL-41 (catalog discovery step, `GovernanceRepository.DocLinks.cs`), WP-SIMPL-43 (SCIM identity correlation step, Identity aggregate), WP-SIMPL-44 (column collation step, PhysicalIndexes/Catalog), WP-SIMPL-45 (write grants step, Consent aggregate) and WP-SIMPL-47 (consent API step, Consent aggregate) either merge before 20a (their changes then exist in both PG and SS families and move with the matching partial in 20a, 20b or 20d) or are written against `GovernanceRepository` after 20d. WP-SIMPL-40 (`GovernanceRepository.Docs.cs`) and WP-SIMPL-42 (`GovernanceRepository.Usage.cs`) already require 20d. None of them edits the lane files during 20a-20d.
- Migration step numbers are never fixed in a spec: each WP takes the next free forward-only migration step at merge time (per the frozen v1.5.0 base policy). The orphan step of 20e takes the next free number when it merges.
- May run in parallel: WP-SIMPL-21, 22, 24 and Stage 3 module WPs that do not touch the files above.

## 3. Scope
### In scope
- New `IGovernanceSqlDialect`, `PostgreSqlGovernanceDialect`, `SqlServerGovernanceDialect` in `src/Autheris.Infrastructure/Persistence/Dialects/`.
- New `GovernanceRepository.cs` + `GovernanceRepository.<Aggregate>.cs` partials (the 26 partial names of the current families).
- Folding `ConsentSqlCore`, `ConsentGrantSqlCore`, `CatalogSqlCore`, `CdcStreamSqlCore`, `DataOwnerAssignmentSqlCore`, `WebhookOutboxSqlCore` into the partials; `SqlCoreBinding` becomes a private helper of `GovernanceRepository`.
- `AuditSqlCore` stays a separate narrow component (SEC-SIMPL-24), consumed by `GovernanceRepository.Audit.cs`.
- DI rewiring in `GatewayGovernanceServiceExtensions.cs` (currently lines 88 and 94, generic `RegisterGovernanceAggregateStores<TRepository,TMigrationRunner>` at 124).
- 20e: one forward-only migration step per engine (next free step number at merge time) dropping orphaned tables of deleted features; `GovernanceSchemaShapeVerifier` shape files updated.
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
- Migrations: steps 0002-0005 exist in `Migrations/Steps/{postgresql,sqlserver}` (`.sql` + `.shape.json`); `Shape/GovernanceSchemaShapeVerifier.cs` (43 lines); `tests/Autheris.Tests.Architecture/GovernanceMigrationFreezeTests.cs` exists. WP-SIMPL-41/42/43/44/45/47 add further steps; their numbers are assigned at merge time.
- Orphan candidates: among deleted features only `ITSM_OUTBOX` (WP-12) has a governance table (verified by grep of `Persistence/Migrations`); Casbin, ReBAC, FinOps, SchemaRegistry, traffic shadowing and DP noise have none. `CONSUMER_APP_ACTOR_DAILY` is removed by WP-SIMPL-42 in its own step, not here.

## 5. Implementation steps (one PR per step; contract suite green before and after)
1. **20a dialect + Consent.** Create `IGovernanceSqlDialect` (internal, in `Persistence/Dialects/IGovernanceSqlDialect.cs`) with exactly: `string Upsert(GovernanceTable table, ReadOnlySpan<string> keyColumns, ReadOnlySpan<string> valueColumns)`, `string InsertReturning(string column)`, `string LockRowsForUpdate`, `string UtcNow`, `string JsonValue(string column, string path)`, `string StringAggregate(string expr, string separator)`, `string LimitWithoutOrder(int n)`, `bool IsUniqueViolation(DbException ex)`, `bool IsTransient(DbException ex)`, `IParameterBudgetProvider ParameterBudget`, `DbConnection CreateConnection()`. Identifiers are `static readonly` names from a closed `GovernanceTable`/column allowlist (SEC-SIMPL-21). `SqlServerGovernanceDialect.Upsert` emits `MERGE ... WITH (HOLDLOCK)`; `CreateConnection` asserts `QUOTED_IDENTIFIER ON` (run `SET QUOTED_IDENTIFIER ON` on open and throw `InvalidOperationException` if `SESSIONPROPERTY('QUOTED_IDENTIFIER')` is not 1). Create `GovernanceRepository.cs` (ctor takes `IGovernanceSqlDialect`, `ILogger`, options; shared executor logs fragment id + duration only, SEC-SIMPL-25) and the `TenantScope` builder: every tenant-scoped statement requires a non-null `TenantId` argument, throws `ArgumentException` when absent; global tables listed in `GovernanceGlobalTables` with a comment (SEC-SIMPL-22). Move Consent, ConsentGrants, ConsentPage: fold `ConsentSqlCore`/`ConsentGrantSqlCore` into `GovernanceRepository.Consent.cs`, `.ConsentGrants.cs`, `.ConsentPage.cs`; SQL written once (double-quoted identifiers, `OFFSET/FETCH`, booleans and timestamps as parameters via `DialectValueCodec`). Delete the PG/SS Consent* partials and both cores. Page-size caps and batch maxima copied unchanged (SEC-SIMPL-23). During 20a-20c the PG/SS family classes stay registered and delegate the moved aggregates to an internal `GovernanceRepository` instance (constructed with the matching dialect) so DI does not change until 20d.
2. **20b Catalog + Schema.** Fold `CatalogSqlCore` into `GovernanceRepository.Catalog.cs`; move `.Schema.cs`, `.PhysicalIndexes.cs`. Delete PG/SS Catalog/Schema/PhysicalIndexes partials and `CatalogSqlCore.cs`.
3. **20c Audit (+ anchor stores).** `GovernanceRepository.Audit.cs` delegates writes to `AuditSqlCore` (kept, renamed nothing, made dialect-neutral by taking `IGovernanceSqlDialect`; batch insert path unchanged). Anchor store in `AuditChainAnchorStores.cs`: one `GovernanceAuditChainAnchorStore` using the dialect; startup fails if no `IAuditChainAnchorStore` registration (SEC-SIMPL-12 stays). Hash input fields untouched.
4. **20d remaining partials + DI switch.** Move AccessProfiles, CallerAttributes, CdcStreamKeys, CdcStreams (fold `CdcStreamSqlCore`), CdcWatermarks, CloudEventSubscriptions, ConsumerAggregation, DataOwnerAssignment (fold core), Identity, Outbox, Recertification, RightsAndPolicies, SemanticManifests, TenantRegistry, TenantTables, TrustedDocuments, VirtualFilters, WebhookOutbox (fold core), plus every aggregate or column set that WP-SIMPL-41/43/44/45/47 added to both families before 20a (e.g. doc links, SCIM correlation columns and group nesting, column collation, write rules on grant records, consent API fields); the 20d PR lists them. Aggregates created after 20d (WP-SIMPL-40 `Docs`, WP-SIMPL-42 `Usage`, and any later one) are written directly as `GovernanceRepository.<Aggregate>.cs` partials. Delete all `PostgreSqlGovernanceRepository*.cs`, `SqlServerGovernanceRepository*.cs`, `SqlCoreBinding.cs` (logic moves into `GovernanceRepository.cs`). DI: `RegisterGovernanceAggregateStores<GovernanceRepository, TMigrationRunner>` with the dialect chosen by provider (`PostgreSql` -> `PostgreSqlGovernanceDialect`, `SqlServer` -> `SqlServerGovernanceDialect`); unknown provider keeps failing via `GovernanceProviderGuard` (WP-11).
5. **20e orphan-table step.** Add `Migrations/Steps/postgresql/<NNNN>_drop_orphaned_feature_tables.{sql,shape.json}` and `Migrations/Steps/sqlserver/<NNNN>_drop_orphaned_feature_tables.{sql,shape.json}`, where `<NNNN>` is the next free forward-only migration step at merge time (per the frozen v1.5.0 base policy), identical in both trees. It drops `ITSM_OUTBOX` (WP-SIMPL-12); Casbin, FinOps and ReBAC have no governance tables (verified); the developer re-verifies at merge time and lists each dropped table with the deleting WP in the PR. `DROP TABLE IF EXISTS` on PG; `IF OBJECT_ID(N'...', N'U') IS NOT NULL DROP TABLE` on SQL Server. Do not modify existing steps or the baselines (SEC-SIMPL-16).

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
  - `GovernanceMigrationOrphanTableTests.Orphan_step_drops_only_listed_tables` on both engines (20e).
  - `GovernanceCoreSecurityTests` (sec plan section 4.1, mandatory, 20a-20d): `Missing_tenant_id_throws_for_every_repository_method` (every repository method rejects a missing tenant id); `Cross_tenant_read_and_write_return_nothing_both_engines` (Testcontainers, both engines); `Identifier_allowlist_rejects_unknown_column_and_order_by`; `Injection_payloads_in_filters_are_treated_as_data` (both engines); `Page_size_and_batch_caps_enforced`; `Executor_logs_no_parameter_values`.
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
- [ ] Existing migration steps and baselines byte-identical; `GovernanceMigrationFreezeTests` unchanged and green; the orphan step present with the same number for both engines.
- [ ] Aggregates added by WP-SIMPL-41/43/44/45/47 before 20a are moved in 20a-20d; no family-specific partial remains.

## 9. PR slicing (`size-exception` not granted; src additions <= 400 net per PR, deletions unlimited)
1. PR 20a: dialect interface + 2 implementations, `GovernanceRepository.cs` executor/tenant scope, Consent/ConsentGrants/ConsentPage, SEC-SIMPL-21..26 tests. If > 400 added, split into 20a-1 (dialect + executor + tests) and 20a-2 (Consent aggregates).
2. PR 20b: Catalog, Schema, PhysicalIndexes.
3. PR 20c: Audit via `AuditSqlCore`, anchor store, chain-compatibility test.
4. PR 20d: remaining 18 partials, DI switch, delete both families, duplication + LOC tests. Split 20d-1 (CDC/Webhook/Outbox/CloudEvents) and 20d-2 (rest + deletion + DI) if > 400 added.
5. PR 20e: orphan step (next free number) per engine + shape files + orphan test.

## 10. Risks, rollback, and things the developer must NOT do
- Risks: SQL Server semantic drift (contract suite catches; fix SQL, never the test); MERGE concurrency (HOLDLOCK + unique-violation retry via `IsUniqueViolation`); `QUOTED_IDENTIFIER` off in pooled connections (assert in `CreateConnection`).
- Rollback: each PR is independently revertible because the family classes keep delegating until 20d; 20d is reverted as a whole.
- Do NOT: concatenate request input into SQL; fold `AuditSqlCore` into the generic executor; change audit hash fields; edit migration steps 0002-0005 or baselines; weaken or skip a contract test; add EF Core/Dapper; reintroduce any SQLite branch; log parameter values; mention advisory details (SEC-SIMPL-01).

## 11. Estimate
Src: about +4,300 / -11,800 (net about -7,500); tests about +900. 11 person-days (20a 3, 20b 3, 20c 3, 20d 1.5, 20e 0.5).

## Decisions taken in this spec
1. SEC-SIMPL-24 overrides AD-1 item 3 for audit: `AuditSqlCore` keeps its name and stays a separate component; all other `*SqlCore` names disappear.
2. Transitional delegation (family classes call into `GovernanceRepository`) keeps DI unchanged until 20d so every PR is green and revertible alone.
3. Orphan-table step takes the next free step number at merge time (same number in both trees; no number is reserved in a spec) and uses idempotent drops; it lists each dropped table with the deleting WP in the PR body.
4. LOC gate test at 7,000 lands in 20d; the 6,000 program target is enforced by WP-SIMPL-33. Partials added after 20d by the owner-mandated WPs 38-47 may raise the budget only by updating it in the same PR with label `freeze-exception` (WP-SIMPL-07 ratchet rule).
5. Owner-mandated schema work (WP-SIMPL-40/41/42/43/44/45/47) merges either before 20a or after 20d, never during 20a-20d.

## Implementation log
- 20a (branch `feat/simpl-20-pr-a`): `IGovernanceSqlDialect` (11 members) with sealed `PostgreSqlGovernanceDialect` and `SqlServerGovernanceDialect`, `GovernanceRepository` executor and tenant scope, Consent/ConsentGrants/ConsentPage moved; the PostgreSQL and SQL Server families delegate (DI unchanged). Src +2,852 / -2,590, net +262 (limit 400).
- Phase 3 re-audit conditions SEC-A20-01..11 closed. Global-table allowlist reduced to tables without `tenant_id` in either baseline; dialect identifier, path and separator inputs validated; executor failure log carries only fragment id, duration, exception type and error code; the SEC-SIMPL-26 scan is a Roslyn syntax scan with a per-file/member allowlist and negative control.
- Recorded decisions (default taken): the tenant-less `GetActiveConsentsForSubjectsAsync` / `GetAllActiveConsentsForSubjectsAsync` overloads stay as the explicit cross-tenant path, with the caller set pinned by `Tenantless_consent_overloads_used_only_from_allowlisted_files`; tenant-typed overloads throw on a missing tenant. Raw `Execute*Async` calls are not rerouted through the executor in 20a; `Governance_repository_logs_only_through_executor` forbids logging outside the executor instead.
- Coverage map: `DialectSqlGoldenArchitectureTests.EveryManifestMethod_HasFragments` skips the ConsentGrants area because its SQL is single-source; the replacement assertion is `SEC_A20_11_ConsentGrantSql_members_and_both_revoke_target_forms_are_in_the_golden` (both dialects). ConsentGrants goldens are the union of both engines.
- Test names: `Every_step_exists_for_both_engines`, `Tenant_scoped_statement_without_tenant_throws`, `Global_table_allowlist_is_exact`, `Logs_contain_no_parameter_values`, `Page_size_and_batch_caps_unchanged` exist as planned.
- Open for 20b-20d: engine-sniffing branches in `GovernanceRepository.Consent.cs` and the `ConsentSqlCore` remnant (marked transitional); `GovernanceRepository` stays public until the DI switch in 20d; consent timestamps use `TimeProvider.System` (no injected clock yet).
- 20b (branch `feat/simpl-20-pr-b`, 2026-11-08): Catalog, Schema and PhysicalIndexes moved into `GovernanceRepository.Catalog.cs`, `.CatalogSql.cs`, `.CatalogRows.cs` (former `CatalogSqlCore`, folded as static members), `.Schema.cs` (two engine-specific demo seeds behind one `SeedInitialCatalog`) and `.PhysicalIndexes.cs`; the three PG and three SS partials and `CatalogSqlCore.cs` are deleted, the family classes forward through `*.CatalogDelegation.cs` (transitional, removed in 20d). The metadata cache, key-table probe, catalog-changed event bus and the metadata test seam now live in `GovernanceRepository`.
- 20b drift and decisions (default taken): the 1000-line source ceiling forced three Catalog partials instead of one. Engine-different statements are kept as `...Pg`/`...Ss` constant pairs selected by the dialect type (no new dialect member; interface stays at 11). Catalog tables are all in `GovernanceGlobalTables`, so no tenant predicate applies (pinned by `Catalog_statements_touch_only_global_tables`). Logging in the moved code goes through one allowlisted member `LogCatalogNotice` that carries constant text only (no exception object, no table identifier); the previous warnings that included the exception or the table name lose that detail. The family logger is now handed to the unified repository.
- 20c (branch `feat/simpl-20c`, 2026-11-09): Audit and anchor store unification. Added `GovernanceRepository.Audit.cs` and dialect-neutral `GovernanceAuditChainAnchorStore` consuming `IGovernanceSqlDialect`. Dialect-neutral write and batch methods added to `AuditSqlCore` (`AcquireAuditChainLockAsync`, `InsertAuditRowsAsync`, `WriteToDeadLetterAsync`, `ExecutePurgeBatchAsync`). Transitional delegation in `PostgreSqlGovernanceRepository.Audit.cs` and `SqlServerGovernanceRepository.Audit.cs` with query constants aliased to preserve `AuditSqlCoreGoldenTests`. All logger calls routed through `AuditSqlCore` static methods per `Governance_repository_logs_only_through_executor`. Added `AuditChainCompatibilityTests.V1_5_0_chain_verifies_after_unification` tagged `Security`. Net src line budget: negative (substantial reduction by replacing duplicated family audit implementations).

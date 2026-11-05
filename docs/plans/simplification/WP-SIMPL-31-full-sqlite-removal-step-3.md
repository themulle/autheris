# WP-SIMPL-31 Full SQLite removal step 3
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-31-full-sqlite-removal-step-3-od-2-owner-may-stop-before) | Issues: #515 | Owner decisions: OD-2 (default taken, owner may stop before), OD-12 (core dialects PG/SS/Oracle/Databricks), OD-1 | Security conditions: SEC-SIMPL-53, SEC-SIMPL-55..57, SEC-SIMPL-17, SEC-SIMPL-02, SEC-SIMPL-03, SEC-SIMPL-50

## 1. Goal and outcome
Remove SQLite as a data-source dialect: generator, type affinity, zone table, column reader, enum value, provider, package and parity lane. Measurable: `git grep -il sqlite -- src` returns 0 files; `Microsoft.Data.Sqlite` referenced by 0 csproj files (today 5); `TargetSqlDialect` has no `Sqlite` member; `parity-sqlite` job gone; about -1,000 src lines.

## 2. Preconditions and dependencies
- Merged and green: WP-SIMPL-11 (governance SQLite gone), WP-SIMPL-21 (PG + SS parity cells executed and green for every case that has a SQLite cell; SEC-SIMPL-51 matrix complete), WP-SIMPL-22 (no real-SQLite test left in Unit, SqlCompilerGate, Simulation, Sim.ApiHost, TrinoSqlEngine.Tests).
- SEC-SIMPL-53 gate: mutation and fuzz floors of SEC-SIMPL-55 hold on the merge commit; if the SQL Server container lane is unavailable, this WP stays blocked (no skips).
- Owner stop point: the owner may stop before this WP (AD-5); the developer confirms the go in the PR.
- Parallel: any WP not touching `src/TrinoSqlEngine/**`, `Persistence/Catalog/**`, `Persistence/Dialects/**`, `.github/workflows/parity-nightly.yml`.

## 3. Scope
### In scope
- `src/TrinoSqlEngine/Ast/Generators/SqliteDialectGenerator.cs`, `src/TrinoSqlEngine/Ast/Security/SqliteTypeAffinity.cs`, the SQLite branch of `src/TrinoSqlEngine/Ast/Security/CatalogTypeMap.cs` (line 234 dispatch, `TryResolveSqlite` 277-302), `TargetSqlDialect.Sqlite` (`src/TrinoSqlEngine/TargetSqlDialect.cs`) and its 28 uses in src, the SQLite dialect function allowlist.
- `src/Autheris.Application/Data/Services/SqliteZoneOffsetTable.cs`.
- `src/Autheris.Infrastructure/Persistence/Catalog/SqliteColumnMetadataReader.cs`, `.../Catalog/SqliteTypeAffinity.cs` (used by `GovernedCatalogProvider.cs:337`), registration at `src/Autheris.Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs:130`.
- `SqliteDialectProvider` (`Persistence/Dialects/SqlDialectProviders.cs:95`) and its registry entry.
- `Microsoft.Data.Sqlite` PackageReference in `Autheris.Infrastructure.csproj:11`, `Autheris.Sim.ApiHost.csproj:17`, `Autheris.Tests.Simulation.csproj:14`, `Autheris.Tests.SqlCompilerGate.csproj:12`, `TrinoSqlEngine.Tests.csproj:13`.
- SQLite parity cells/baselines in `Autheris.Tests.Parity` and the `parity-sqlite` job (`.github/workflows/parity-nightly.yml:23`) `[WF]`.
- Config: `Provider=Sqlite` for a data source fails startup like any unknown dialect (SEC-SIMPL-02).
### Out of scope
- Governance store (done in WP-11); Ansi/DuckDb generator decisions (WP-13/26); Oracle/Databricks work.

## 4. Current state (verified on simpl-refine @ 63cbb893)
- 156 src files mention `sqlite` (case-insensitive) today; after WP-11 the remaining set is the data-source path above plus call sites (developer prints the list in the PR).
- `TargetSqlDialect` = Ansi, PostgreSql, SqlServer, Sqlite, DuckDb, Snowflake, Oracle, Databricks; `TargetSqlDialect.Sqlite` occurs 28 times in src.
- Test files using `SqliteConnection`/`Microsoft.Data.Sqlite` today: Unit 84, Integration 26, TrinoSqlEngine.Tests 18, Parity 8, Simulation 6, Sim.ApiHost 4, SqlCompilerGate 3, Architecture 2 (most leave with WP-11/22).

## 5. Implementation steps (TDD order)
1. Red: add `PackageBoundaryArchitectureTests.No_Microsoft_Data_Sqlite_reference` (scans all csproj under `src/` and `tests/`), `TargetSqlDialectTests.Only_supported_values_exist`, `NoSqliteArchitectureTests.No_sqlite_types_in_src` (no type name containing `Sqlite` in any src assembly).
2. Remove `TargetSqlDialect.Sqlite`; compile and delete each failing use: generator registration, `CatalogTypeMap` branch and `TryResolveSqlite`, `SqliteTypeAffinity` (AST), dialect function allowlist entry, Emit/Application/Sql/middleware switch arms. A `switch` over the enum keeps a default arm that throws `NotSupportedException` (fail closed).
3. SEC-SIMPL-17 proof: before deleting `SqliteZoneOffsetTable`, both `SqliteTypeAffinity` classes and `SqliteColumnMetadataReader`, record in the PR the `git grep` output proving no masking/governance path uses them; if a masking path did, replace it with the dialect-neutral type map first and add a test.
4. Delete `SqliteColumnMetadataReader`, Infrastructure `SqliteTypeAffinity`, the `GovernedCatalogProvider.cs:337` use, line 130 registration; register no fallback `IColumnMetadataReader` (resolution of the configured dialect's reader only; missing reader fails startup).
5. Delete `SqliteDialectProvider` and registry entry; data-source config with `Sqlite` dialect fails startup with a message naming OD-2 and the migration guide, no connection string value.
6. Remove the five PackageReferences; delete remaining SQLite parity cells/baselines (only after PG and SS cells for the same case are green); remove the `parity-sqlite` job `[WF]`.
7. Update `docs/operations/migration-simplification.md` and CHANGELOG entry ("SQLite data-source dialect removed; configured SQLite data sources fail startup").

## 6. Tests
- `Autheris.Tests.Architecture`: `PackageBoundaryArchitectureTests.No_Microsoft_Data_Sqlite_reference`; `NoSqliteArchitectureTests.No_sqlite_types_in_src`.
- `TrinoSqlEngine.Tests`: `TargetSqlDialectTests.Only_supported_values_exist` (expected set after WP-13: PostgreSql, SqlServer, Oracle, Databricks plus generator-only values still retained by WP-13/26; assert exact set).
- `Autheris.Tests.Integration` [Security]: `DataSourceDialectAdmissionTests.Sqlite_data_source_fails_startup` and `.Unknown_dialect_fails_startup` (no fallback, message has no connection string).
- `TrinoSqlEngine.Tests` [Security]: `CatalogTypeMapTests.Unsupported_dialect_resolution_fails_closed`.
- Gate (existing from WP-21): `DialectParityGateTests.Every_generator_has_a_parity_baseline_file` stays green with SQLite baselines deleted.
- Delete: SQLite generator/affinity/zone-table/column-reader tests and SQLite parity cells; each Security test removed is mapped with reason "feature deleted, WP-SIMPL-31" (SEC-SIMPL-03).

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture`; `dotnet test tests/TrinoSqlEngine.Tests`; `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj`; `dotnet test tests/Autheris.Tests.Parity --filter "Dialect=PostgreSql|Dialect=SqlServer"`
- `git grep -il sqlite -- src` -> empty; `git grep -n 'Microsoft.Data.Sqlite' -- '*.csproj'` -> empty; `git grep -n 'parity-sqlite' -- .github` -> empty.

## 8. Acceptance criteria
- [ ] SQLite references in src 0; real-SQLite test files 0; package refs 0.
- [ ] `TargetSqlDialect` has no `Sqlite`; enum switch defaults throw.
- [ ] SEC-SIMPL-17 grep proof and SEC-SIMPL-53/55 floors recorded in the PR.
- [ ] Configured SQLite data source fails startup (test green).
- [ ] Security test count not reduced without coverage-map rows.

## 9. PR slicing
1. PR 31-1: tests (red->green), enum removal, TrinoSqlEngine generator/affinity/type-map deletion, provider deletion, admission tests.
2. PR 31-2: Application/Infrastructure deletions (zone table, column reader, DI), package refs, parity cells, `parity-sqlite` job `[WF]`, migration guide + CHANGELOG. Both are deletion-heavy; added src lines well under 400.

## 10. Risks, rollback, and things the developer must NOT do
- Risk: hidden behaviour in `SqliteZoneOffsetTable` or affinity used by masking (SEC-SIMPL-17 proof). Risk: Parity gate counts drop (expected; baselines removed with the generator).
- Rollback: revert PR 31-2 then 31-1.
- Do NOT: delete a SQLite parity cell whose PG/SS twin is not green; add a fallback reader or dialect; skip tests to get green; leave `[Fact(Skip=...)]` for SQLite.

## 11. Estimate
Src about -1,000 / +40; tests about -1,500 / +120. 3 person-days.

## Decisions taken in this spec
1. Two PRs (engine-side first, host-side second) instead of one, so the enum break is reviewed separately from workflow changes.
2. `TargetSqlDialectTests` asserts the exact set existing at merge time (WP-13/26 may still retain Ansi/DuckDb as generator-only), not a hard-coded PG/SS pair.

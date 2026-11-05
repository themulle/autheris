# WP-SIMPL-31 Full SQLite removal step 3
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-31-full-sqlite-removal-step-3-od-2-owner-may-stop-before) | Issues: #515 | Owner decisions: OD-2 (default taken, owner may stop before), OD-12 (core data-source dialects PG/SS/Oracle/Databricks), OD-14 (Ansi generator-only, Snowflake deleted), OD-15 (DuckDb generator core), OD-1 | Security conditions: SEC-SIMPL-53, SEC-SIMPL-55..57, SEC-SIMPL-38, SEC-SIMPL-17, SEC-SIMPL-02, SEC-SIMPL-03, SEC-SIMPL-50

## 1. Goal and outcome
Remove SQLite as a data-source dialect: generator, type affinity, zone table, column reader, both enum values (`TargetSqlDialect.Sqlite`, `DatabaseDialect.Sqlite`), provider aliases, provider, package and parity lane. Measurable: no SQLite type, package or dialect member in `src` (the only allowed `sqlite` literals are the guards listed in `tests/Autheris.Tests.Architecture/Baselines/sqlite-literals.txt`); `Microsoft.Data.Sqlite` referenced by 0 csproj files (today 5); `TargetSqlDialect` = {Ansi, PostgreSql, SqlServer, DuckDb, Oracle, Databricks}; `DatabaseDialect` has no `Sqlite`; `parity-sqlite` job gone; about -1,300 src lines.

## 2. Preconditions and dependencies
- Merged and green: WP-SIMPL-11 (governance SQLite gone; ships `GovernanceProviderGuard`), WP-SIMPL-13 (Snowflake deleted, dialect admission), WP-SIMPL-21 (PG + SS parity cells executed and green for every case that has a SQLite cell; SEC-SIMPL-51 matrix complete), WP-SIMPL-22 (no real-SQLite test left in Unit, SqlCompilerGate, Simulation, Sim.ApiHost, TrinoSqlEngine.Tests).
- SEC-SIMPL-53 gate: mutation and fuzz floors of SEC-SIMPL-55 hold on the merge commit; if the SQL Server container lane is unavailable, this WP stays blocked (no skips).
- Owner stop point: the owner may stop before this WP (AD-5); the developer confirms the go in the PR.
- Parallel: any WP not touching `src/TrinoSqlEngine/**`, `src/Autheris.Domain/Common/DatabaseDialect.cs`, `Persistence/Catalog/**`, `Persistence/Dialects/**`, `.github/workflows/parity-nightly.yml`.

## 3. Scope
### In scope
- `src/TrinoSqlEngine/Ast/Generators/SqliteDialectGenerator.cs`, `src/TrinoSqlEngine/Ast/Security/SqliteTypeAffinity.cs`, the SQLite branch of `src/TrinoSqlEngine/Ast/Security/CatalogTypeMap.cs` (line 234 dispatch, `TryResolveSqlite` 277-302), `TargetSqlDialect.Sqlite` (`src/TrinoSqlEngine/TargetSqlDialect.cs`) and its 28 uses in src, the SQLite dialect function allowlist.
- `DatabaseDialect.Sqlite` (`src/Autheris.Domain/Common/DatabaseDialect.cs:9`) and its 49 uses in 31 src files; the `"sqlite" or "sqlite3"` aliases (`:103`); the empty-provider-means-SQLite default of `DataSourceProvider.TryResolveDialect` (`:212`): an empty provider fails resolution (no default dialect).
- `src/Autheris.Application/Data/Services/SqliteZoneOffsetTable.cs`.
- `src/Autheris.Infrastructure/Persistence/Catalog/SqliteColumnMetadataReader.cs`, `.../Catalog/SqliteTypeAffinity.cs` (used by `GovernedCatalogProvider.cs:337`), registration at `src/Autheris.Api/Extensions/DependencyInjection/GatewayStorageServiceExtensions.cs:130`.
- `SqliteDialectProvider` (`Persistence/Dialects/SqlDialectProviders.cs:95`) and its registry entry.
- `Microsoft.Data.Sqlite` PackageReference in `Autheris.Infrastructure.csproj:11`, `Autheris.Sim.ApiHost.csproj:17`, `Autheris.Tests.Simulation.csproj:14`, `Autheris.Tests.SqlCompilerGate.csproj:12`, `TrinoSqlEngine.Tests.csproj:13`.
- SQLite parity cells/baselines in `Autheris.Tests.Parity` and the `parity-sqlite` job (`.github/workflows/parity-nightly.yml:23`) `[WF]`.
- Config: a data source with `Provider=Sqlite`/`sqlite3` fails startup with the typed unsupported-dialect error (SEC-SIMPL-38, SEC-SIMPL-02).
### Out of scope
- Governance store (done in WP-SIMPL-11, incl. `GovernanceProviderGuard`). Ansi (OD-14, generator-only) and DuckDb (OD-15) stay; Snowflake is deleted by WP-SIMPL-13. Oracle/Databricks work.

## 4. Current state (verified on simpl-refine @ 63cbb893; enum uses re-checked on 88e5ab74)
- 156 src files mention `sqlite` (case-insensitive) today; after WP-SIMPL-11 the remaining set is the data-source path above plus call sites (developer prints the list in the PR).
- `TargetSqlDialect` = Ansi, PostgreSql, SqlServer, Sqlite, DuckDb, Snowflake, Oracle, Databricks; `TargetSqlDialect.Sqlite` occurs 28 times in src. Snowflake leaves with WP-SIMPL-13.
- `DatabaseDialect` = PostgreSql, SqlServer, Sqlite, Databricks, Oracle; `DatabaseDialect.Sqlite` occurs 49 times in 31 src files; `DataSourceProvider.TryResolveDialect` maps an empty provider to `"sqlite"` (`DatabaseDialect.cs:212`).
- Test files using `SqliteConnection`/`Microsoft.Data.Sqlite` today: Unit 84, Integration 26, TrinoSqlEngine.Tests 18, Parity 8, Simulation 6, Sim.ApiHost 4, SqlCompilerGate 3, Architecture 2 (most leave with WP-SIMPL-11/22).

## 5. Implementation steps (TDD order)
1. Red: add `PackageBoundaryArchitectureTests.No_Microsoft_Data_Sqlite_reference` (scans all csproj under `src/` and `tests/`), `TargetSqlDialectTests.Only_supported_values_exist`, `DatabaseDialectTests.Only_supported_values_exist`, `NoSqliteArchitectureTests.No_sqlite_types_in_src` (no type or member name containing `Sqlite` in any src assembly), `NoSqliteArchitectureTests.Sqlite_literals_only_in_allowlisted_guards` (case-insensitive `sqlite` in `src/**/*.cs` only in files listed in `Baselines/sqlite-literals.txt`: `GovernanceProviderGuard` (WP-SIMPL-11) and the removed-dialect rejection of step 5).
2. Remove `TargetSqlDialect.Sqlite` and `DatabaseDialect.Sqlite`, the `sqlite`/`sqlite3` aliases and the empty-provider default (empty provider fails resolution); compile and delete each failing use: generator registration, `CatalogTypeMap` branch and `TryResolveSqlite`, `SqliteTypeAffinity` (AST), dialect function allowlist entry, Emit/Application/Sql/middleware/connection-factory/TLS-policy switch arms. A `switch` over either enum keeps a default arm that throws `NotSupportedException` (fail closed).
3. SEC-SIMPL-17 proof: before deleting `SqliteZoneOffsetTable`, both `SqliteTypeAffinity` classes and `SqliteColumnMetadataReader`, record in the PR the `git grep` output proving no masking/governance path uses them; if a masking path did, replace it with the dialect-neutral type map first and add a test.
4. Delete `SqliteColumnMetadataReader`, Infrastructure `SqliteTypeAffinity`, the `GovernedCatalogProvider.cs:337` use, line 130 registration; register no fallback `IColumnMetadataReader` (resolution of the configured dialect's reader only; missing reader fails startup).
5. Delete `SqliteDialectProvider` and registry entry. A data-source config with provider `sqlite`/`sqlite3` fails startup with the typed unsupported-dialect error (SEC-SIMPL-38); the message names OD-2 and the migration guide and contains no connection string value. This rejection is the second allowlisted `sqlite` literal.
6. Remove the five PackageReferences; delete remaining SQLite parity cells/baselines (only after PG and SS cells for the same case are green); remove the `parity-sqlite` job `[WF]`.
7. Update `docs/operations/migration-simplification.md` and CHANGELOG entry ("SQLite data-source dialect removed; configured SQLite data sources and data sources without a provider fail startup").

## 6. Tests
- `Autheris.Tests.Architecture`: `PackageBoundaryArchitectureTests.No_Microsoft_Data_Sqlite_reference`; `NoSqliteArchitectureTests.No_sqlite_types_in_src`; `NoSqliteArchitectureTests.Sqlite_literals_only_in_allowlisted_guards`.
- `TrinoSqlEngine.Tests`: `TargetSqlDialectTests.Only_supported_values_exist` (exact set {Ansi, PostgreSql, SqlServer, DuckDb, Oracle, Databricks}; Ansi generator-only per OD-14, DuckDb per OD-15).
- `Autheris.Tests.Unit`: `DatabaseDialectTests.Only_supported_values_exist` (exact set {PostgreSql, SqlServer, Oracle, Databricks}), `DataSourceProviderTests.Empty_provider_fails_resolution` [Security], `.Sqlite_aliases_are_not_resolved` [Security].
- `Autheris.Tests.Integration` [Security]: `DataSourceDialectAdmissionTests.Sqlite_data_source_fails_startup` (typed unsupported-dialect error, message names OD-2) and `.Unknown_dialect_fails_startup` (no fallback, message has no connection string).
- `TrinoSqlEngine.Tests` [Security]: `CatalogTypeMapTests.Unsupported_dialect_resolution_fails_closed`.
- Gate (existing from WP-SIMPL-21): `DialectParityGateTests.Every_generator_has_a_parity_baseline_file` stays green with SQLite baselines deleted.
- Delete: SQLite generator/affinity/zone-table/column-reader tests and SQLite parity cells; each Security test removed is mapped with reason "feature deleted, WP-SIMPL-31" (SEC-SIMPL-03). Guard tests that mention "sqlite" (e.g. the admission tests above, `GovernanceProviderGuard` tests) stay.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Architecture`; `dotnet test tests/TrinoSqlEngine.Tests`; `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj`; `dotnet test tests/Autheris.Tests.Parity --filter "Dialect=PostgreSql|Dialect=SqlServer"`
- `git grep -il sqlite -- src` -> only the files in `tests/Autheris.Tests.Architecture/Baselines/sqlite-literals.txt`; `git grep -n 'Microsoft.Data.Sqlite' -- '*.csproj'` -> empty; `git grep -n 'parity-sqlite' -- .github` -> empty; `git grep -n 'Dialect.Sqlite' -- src` -> empty.

## 8. Acceptance criteria
- [ ] No SQLite type, package or dialect member in `src`; the only `sqlite` literal hits are the allowlisted guards (`GovernanceProviderGuard`, removed-dialect rejection); real-SQLite test files 0; package refs 0.
- [ ] `TargetSqlDialect` = {Ansi, PostgreSql, SqlServer, DuckDb, Oracle, Databricks}; `DatabaseDialect` has no `Sqlite`; enum switch defaults throw.
- [ ] Empty data-source provider fails resolution (no SQLite default).
- [ ] SEC-SIMPL-17 grep proof and SEC-SIMPL-53/55 floors recorded in the PR.
- [ ] Configured SQLite data source fails startup with the typed unsupported-dialect error (test green).
- [ ] Security test count not reduced without coverage-map rows.

## 9. PR slicing
1. PR 31-1: tests (red->green), `TargetSqlDialect.Sqlite` removal, TrinoSqlEngine generator/affinity/type-map deletion, provider deletion, admission tests.
2. PR 31-2: `DatabaseDialect.Sqlite`, aliases and empty-provider default; Application/Infrastructure deletions (zone table, column reader, DI), package refs, parity cells, `parity-sqlite` job `[WF]`, migration guide + CHANGELOG. Both are deletion-heavy; added src lines well under 400.

## 10. Risks, rollback, and things the developer must NOT do
- Risk: hidden behaviour in `SqliteZoneOffsetTable` or affinity used by masking (SEC-SIMPL-17 proof). Risk: Parity gate counts drop (expected; baselines removed with the generator). Risk: a code path relied on the empty-provider default; it now fails startup, which is the intended fail-closed behaviour.
- Rollback: revert PR 31-2 then 31-1.
- Do NOT: delete a SQLite parity cell whose PG/SS twin is not green; add a fallback reader, dialect or default provider; skip tests to get green; leave `[Fact(Skip=...)]` for SQLite; add a `sqlite` literal outside the allowlisted guards.

## 11. Estimate
Src about -1,300 / +60; tests about -1,500 / +160. 3.5 person-days.

## Decisions taken in this spec
1. Two PRs (engine-side first, host-side second) instead of one, so the enum break is reviewed separately from workflow changes.
2. `sqlite` literals in `src` are allowed only in the two guards and pinned by an allowlist file, so the acceptance check is mechanical.

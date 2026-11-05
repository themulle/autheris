# WP-SIMPL-22 Real-database tests out of Unit
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-22-real-database-tests-out-of-unit-od-2-step-2b-req-simpl-082) | Issues: #520, #519 (shard weights), #523 | Owner decisions: OD-2 (step 2b), OD-6, OD-15 | Security conditions: SEC-SIMPL-50, 52, 54, 60, SEC-SIMPL-03

## 1. Goal and outcome
`Autheris.Tests.Unit` opens no database and no host. Tests that only used SQLite as a convenient store move to in-memory fakes; tests that need DB semantics move to `Autheris.Tests.Integration` on the shared PostgreSQL fixture; DuckDB/Olap tests become core DuckDB integration tests (OD-15). Measurable: `TestLayeringArchitectureTests.Unit_tests_open_no_database_or_host` green; Security test count unchanged or mapped; Unit wall-clock lower than baseline.

## 2. Preconditions and dependencies
- Merged first: WP-SIMPL-11 (shared PostgreSQL fixture, template DB clone), #520 (host reuse), WP-SIMPL-21 (`RequiresContainerRuntimeFactAttribute`), WP-SIMPL-23 PR 23-1 (coverage map tooling; if not yet merged, this WP adds the map rows by hand in the same format).
- L-ENG lane after 21; per domain folder slices may run in parallel with L-TST (WP-SIMPL-23) only if they touch different folders.

## 3. Scope
### In scope
- 72 Unit files referencing `SqliteConnection`, 5 referencing `DuckDBConnection`, 11 using `TestServer`/`WebApplication.Create` (counts verified; plan states 83 incl. other SQLite helpers).
- Same layering for `Autheris.Tests.SqlCompilerGate` (1 file), `Autheris.Tests.Simulation` (4), `Autheris.Sim.ApiHost` (2), `TrinoSqlEngine.Tests` (16) per plan.
- Architecture test forbidding DB/host types in Unit.
### Out of scope
- Removing SQLite as data-source dialect (WP-SIMPL-31); restructuring folders/names (WP-SIMPL-23); CI sharding design (#516-#522).

## 4. Current state (verified on 63cbb893)
- `git grep -l SqliteConnection tests/Autheris.Tests.Unit` = 72 files; `DuckDBConnection` = 5; `TestServer|WebApplication.Create` = 11.
- Shared fixtures: `tests/Autheris.Tests.Parity/Dialects/ParityPostgresHostFixture.cs`; governance PG fixture added by WP-SIMPL-11 in `Autheris.Tests.Common`.

## 5. Implementation steps (TDD order, one domain folder per PR)
1. Red: add `tests/Autheris.Tests.Architecture/TestLayeringArchitectureTests.cs` scanning `tests/Autheris.Tests.Unit/**/*.cs` for `SqliteConnection`, `DuckDBConnection`, `NpgsqlConnection`, `SqlConnection`, `TestServer`, `WebApplication.Create`. Baseline file `tests/Autheris.Tests.Architecture/Baselines/unit-db-host-files.txt` with the current 88 paths; test fails on any file not in baseline and on any baseline entry that no longer matches (ratchet down only).
2. Per file, classify (write the class into the PR description table): (i) store convenience -> replace with existing in-memory store or a new `InMemory<Store>` fake in `Autheris.Tests.Common/Fakes`; (ii) DB semantics (SQL text, transactions, constraints, tenant predicates) -> move to `tests/Autheris.Tests.Integration/<Domain>/` using the shared PG fixture and `[RequiresContainerRuntimeFact]`; (iii) DuckDB/Olap -> `tests/Autheris.Tests.Integration/Olap/` with in-process DuckDB (no container); the PR that creates the folder makes the `Olap` entry of the WP-SIMPL-21 DuckDB lane gate strict; (iv) TestServer/WebApplication -> `Autheris.Tests.Integration` using the reused host from #520.
3. Security property preservation (SEC-SIMPL-52): a moved Security test keeps class and method name (namespace may change); a rewritten one asserts the same property (tenant isolation, audit append-only, masking) and gets a coverage-map row old id -> new id.
4. Remove the file from `unit-db-host-files.txt` in the same commit; when empty, delete the baseline and make the test absolute.
5. Repeat step 1-4 for SqlCompilerGate, Simulation, Sim.ApiHost, TrinoSqlEngine.Tests (engine semantics via PG fixture or in-memory row source).
6. Remove `Microsoft.Data.Sqlite`/`DuckDB.NET` package references from `Autheris.Tests.Unit.csproj` once unused (lock file update with `--locked-mode` restore check).
7. Rerun the #519 shard-weight regeneration script after the last slice.

## 6. Tests
- `Autheris.Tests.Architecture`: `TestLayeringArchitectureTests.Unit_tests_open_no_database_or_host` [Security], `.Unit_project_references_no_database_provider_package` (after step 6).
- `TestInfrastructureTests.Missing_runtime_fails_in_ci_and_skips_locally` [Security] (from WP-SIMPL-21, reused).
- `TestCoverageMapTests.No_security_test_lost_without_mapping` [Security] (WP-SIMPL-23) must stay green on every slice.
- Moved tests keep names; no new behaviour tests beyond rewrites.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Unit -c Release`
- `dotnet test tests/Autheris.Tests.Integration -c Release --filter "FullyQualifiedName~<Domain>"`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --filter "FullyQualifiedName~TestLayeringArchitectureTests|FullyQualifiedName~TestCoverageMapTests"`
- `git grep -lE 'SqliteConnection|DuckDBConnection|NpgsqlConnection|SqlConnection|TestServer|WebApplication\.Create' tests/Autheris.Tests.Unit | wc -l` = 0 at the end.
- Security count: `dotnet test ... --filter Category=Security --list-tests | wc -l` across all projects >= base count (or mapped).

## 8. Acceptance criteria
- [ ] Grep above = 0; `unit-db-host-files.txt` deleted; layering test absolute.
- [ ] Integration project in required checks with the same categories (SEC-SIMPL-52); Security skipped = 0 in CI.
- [ ] Coverage map has a row for every moved/renamed Security test.
- [ ] Unit project has no SQLite/DuckDB package reference.
- [ ] Median PR wall-clock (including queue) not worse than the WP-SIMPL-10 baseline (OD-6 target < 30 min, enforced by WP-SIMPL-35); one container per test assembly.
- [ ] The `Integration/Olap` suite is part of the WP-SIMPL-21 DuckDB parity lane (`DialectParityGateTests.DuckDb_parity_lane_runs_on_pr` strict for `Olap`).

## 9. PR slicing (tests only; target <= 400 net added lines per PR, deletions unlimited)
22-1 layering test + baseline; then one PR per domain folder group (Governance/Audit, Catalog/Dbt, Sql/Data, Olap/DuckDB, Api host tests, Streaming/Cdc, rest); last PR: secondary projects + package removal + shard weights.

## 10. Risks, rollback, must NOT
- Risk: Integration time grows; mitigated by fakes first, template DB clone, host reuse (#520).
- Rollback: per-domain PR revert; baseline re-admits files only through revert, never by editing the baseline upward.
- Must NOT: delete a Security test without a map row; convert a DB-semantics test to a fake that no longer exercises the semantics; add `Skip=` to moved tests; start a container per test class.

## 11. Estimate
Tests about -4,000 net; src 0. 7 person-days (splittable per domain).

## Decisions taken in this spec
- Ratchet via `unit-db-host-files.txt` baseline (existing Baselines pattern) instead of one big-bang PR.
- Category (iii) DuckDB uses in-process DuckDB in Integration, no container.

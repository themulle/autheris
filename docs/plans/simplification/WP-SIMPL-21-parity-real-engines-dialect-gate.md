# WP-SIMPL-21 Parity on real engines and dialect gate
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-21-parity-on-real-engines-and-dialect-gate-adr-019-od-2-step-2a-wf) | Issues: #519, #521, #522 (umbrella #523) | Owner decisions: OD-2 (step 2a), OD-6, OD-12, OD-14, OD-15, OD-16, OD-17(3), OD-36 (Databricks tier flip in WP-SIMPL-37 after this WP's lanes) | Security conditions: SEC-SIMPL-50, 51, 54, 55, 57, 92, 93, 94

## 1. Goal and outcome
The parity suite runs on real engines: PostgreSQL (reference) and SQL Server fully on every PR, an Oracle security smoke subset on PR plus the full Oracle suite nightly, Databricks contract tests against a fake statement-execution API on PR plus a real-workspace (or Spark-container fallback) suite nightly, and DuckDB federation parity in core (OD-15). Measurable: 0 `Pending` cells for PG/SS/Oracle/Databricks in `parity-baseline-dialects.json`; a dialect gate test fails when a generator has no baseline or no PR lane; the PR CI wall-clock median does not regress against the WP-SIMPL-10 baseline (the OD-6 target, PR median < 30 min including queue, is enforced by WP-SIMPL-35; this WP must not move the median away from it).

## 2. Preconditions and dependencies
- Merged first: WP-SIMPL-13 PR 2 and PR 3 (steps 5-6: Snowflake deleted, dialect startup check), #516/#517 (CI hang/timeouts), #519 + #521 workflow bundle (shard weights, image pre-pull) or bundled into PR 21-4 below with owner approval `[WF]`.
- Runs in L-ENG lane; parallel with L-PER (WP-SIMPL-19/20). WP-SIMPL-22 and the Oracle fixes (#479, the remaining Oracle part of #483; WP-SIMPL-13 only preserves the TCPS check) start after this WP.
- Databricks auth modes (WP-SIMPL-37) may land in parallel; the contract tests here cover OBO only and add per-mode cells when WP-SIMPL-37 merges. The Databricks tier flip (X1-DBX: WebSQL, Trino and MCP) is owned by WP-SIMPL-37 (OD-36) and starts only after the Databricks contract and nightly lanes of this WP are green.

## 3. Scope
### In scope
- PostgreSQL as parity reference engine; SQL Server cells executed (no `PendingCellException` for `sqlserver_host`).
- Oracle parity lane on `Testcontainers.Oracle` (smoke on PR, full nightly).
- `DatabricksParitySnapshotTests`, `DatabricksStatementExecutionContractTests` (fake API) on PR; real workspace or Spark container nightly.
- `FederationParityTests` (PG x SS, Databricks-contract x SS, declarative HTTP x PG) with in-process DuckDB staging in `Autheris.Tests.Integration`.
- `DialectParityGateTests`; Stryker config merge (3 -> 2); workflow changes in `ci.yml` and `parity-nightly.yml` (#521, #522 Oracle/Spark parts).
### Out of scope
- Deleting SQLite parity cells or the `parity-sqlite` job (WP-SIMPL-31).
- Moving Unit DB tests (WP-SIMPL-22); shard-weight script itself (#519).
- Fixing Oracle dialect semantics #479 (follows this WP, but a generator bug revealed by a new cell is fixed here, never left Pending).

## 4. Current state (verified on 63cbb893)
- `tests/Autheris.Tests.Parity/Dialects/` contains `DialectTests.cs`, `DialectStructureTests.cs`, `DialectTimeBucketTests.cs`, `ParityPostgresHostFixture.cs`, `ParitySqlServerHostFixture.cs`.
- `DialectTests.cs:12` documents "SQL Server cells are registered Pending (no host yet)"; `DialectTests.cs:34` throws `PendingCellException(channel, capability, SqliteOnlyReason)`; `DialectTests.cs:76` throws `PendingCellException(channel, "sqlserver_host")`.
- `.github/workflows/parity-nightly.yml`: jobs `parity-sqlite` (line 23) and `parity-dialects` (line 86); report via `.github/scripts/parity-report.py` against `tests/Autheris.Tests.Parity/parity-baseline-dialects.json`.
- `.github/workflows/ci.yml`: jobs `build-and-test` (17), `integration-tests` (65), `spark-integration-tests` (104, `--filter "Category=Spark"` at 128), `helm-render`, `sql-compiler-gate`. PR filter at line 44 excludes `Spark`, `NightlyGate`, `Metamorphic`, `Fuzz`, `Mutant`.
- Category traits: `Spark` in 2 test files; `Oracle` and `Stress` category traits: 0 files (Oracle tests are not separable by trait today).
- Databricks fakes exist in Unit: `tests/Autheris.Tests.Unit/Databricks/DatabricksTestSupport.cs`, `DatabricksStatementExecutionClientTests.cs`, `DatabricksFacadeTests.cs`.
- Stryker configs at repo root: `stryker-config.json`, `stryker-config.security-guards.json`, `stryker-config.governance.json`.

## 5. Implementation steps (TDD order)
1. Gate first (Red): create `tests/Autheris.Tests.Architecture/DialectParityGateTests.cs` with the tests of section 6; closed dialect list = `TargetSqlDialect` (`src/TrinoSqlEngine/TargetSqlDialect.cs`) minus Ansi and DuckDb (generator-only), minus Sqlite (own job until WP-SIMPL-31). Do not use the governance-engine enum `SqlDialect` (`Infrastructure/Persistence/DialectValueCodec.cs:8`), which does not list the data-source dialects. DuckDB parity lane (OD-15): `FederationParityTests` (step 6) plus `FederationSecurityTests` and `DuckDbCoreSecurityTests` (section 6) and, once WP-SIMPL-22 has moved them, the `tests/Autheris.Tests.Integration/Olap/` suite; the gate checks these by name in the PR filter (the `Olap` entry becomes strict in the WP-SIMPL-22 PR that creates the folder). Red because SS cells are Pending and no Oracle/Databricks lane exists.
2. SQL Server: in `DialectTests.cs` replace the `PendingCellException(..., "sqlserver_host")` branch with execution against `ParitySqlServerHostFixture` (collection fixture, one container per assembly). Update `parity-baseline-dialects.json` SS cells from `pending` to the observed result. Any mismatch = generator bug, fix in `src/Autheris.Application/Sql/...` generator in the same PR.
3. PostgreSQL reference: make `ParityPostgresHostFixture` the reference engine in the cell runner (expected values computed against PG, SQLite becomes a compared engine, cells kept).
4. Oracle: add `tests/Autheris.Tests.Parity/Dialects/ParityOracleHostFixture.cs` (`Testcontainers.Oracle`, random per-run password, localhost bind, SEC-SIMPL-54). Tag all Oracle cells `[Trait("Dialect","Oracle")]`; security cells (RLS, masking, consent, injection rejection, bind-by-name) additionally `[Trait("Category","Security")]`. Add `[Trait("Category","Oracle")]` to existing Oracle integration tests so #522 can route them.
5. Databricks: add `tests/Autheris.Tests.Parity/Dialects/DatabricksParitySnapshotTests.cs` (generated SQL per parity case vs checked-in snapshot `Dialects/Snapshots/databricks/*.sql`) and `tests/Autheris.Tests.Integration/Databricks/DatabricksStatementExecutionContractTests.cs` reusing a fake statement-execution handler (move/share `DatabricksTestSupport` into `Autheris.Tests.Common`).
6. Federation: add `tests/Autheris.Tests.Integration/Federation/FederationParityTests.cs` using PG and SS fixtures plus the Databricks fake and an in-process declarative HTTP stub; executes through `FederatedDuckDbExecutionService` / `CrossSourcePlanner`.
7. Container attribute: add `RequiresContainerRuntimeFactAttribute` in `Autheris.Tests.Common` (fail in CI when `CI`/`GITHUB_ACTIONS` set; skip locally with `Skipped: container runtime unavailable (local only)`); use it on all fixtures of this WP.
8. Workflows `[WF]` (owner approval): `ci.yml` add `parity-pr` job: `dotnet test tests/Autheris.Tests.Parity --filter "Dialect=PostgreSql|Dialect=SqlServer|(Dialect=Oracle&Category=Security)|Category=DatabricksContract"`; the PR `integration-tests` job keeps the DuckDB lane (`FederationParityTests`, `FederationSecurityTests`, `DuckDbCoreSecurityTests`, later `Olap`) in its filter; pre-pull PG, SS, Oracle images (#521); remove `spark-integration-tests` from `ci.yml`. `parity-nightly.yml` add jobs `parity-sqlserver`, `parity-oracle` (full), `parity-databricks` (real workspace if `DATABRICKS_HOST` secret exists, else `Category=Spark` container suite, results file records `engine=spark`). Secrets only in nightly (SEC-SIMPL-93). `release.yml`: fail when the last `parity-nightly` run on main failed (SEC-SIMPL-92).
9. Stryker: fold `stryker-config.security-guards.json` mutate globs into `stryker-config.json`, delete the file, update workflow references; floor for the AST security namespace unchanged (SEC-SIMPL-55).

## 6. Tests
- `Autheris.Tests.Architecture`: `DialectParityGateTests.Every_generator_has_a_parity_baseline_file`, `.Every_core_dialect_has_a_pr_parity_lane` (parses `ci.yml` for the four lane filters), `.DuckDb_parity_lane_runs_on_pr` (PR filter contains `FederationParityTests`, `FederationSecurityTests`, `DuckDbCoreSecurityTests` and, after WP-SIMPL-22, the `Olap` integration namespace), `.No_pending_cell_for_core_dialects` (reads baseline JSON), `DialectGateTests.Dialect_set_is_closed` [Security] (SEC-SIMPL-57; expected set from `TargetSqlDialect`).
- `Autheris.Tests.Parity`: `DialectParityTests.Oracle_and_Databricks_security_cells_deny_ddl_dml_system_access_and_unknown_functions` [Security] (SEC-SIMPL-92); `AstDifferentialTests.Checked_tree_equals_executed_sql_semantics` [Security] (the checked AST and the SQL actually executed have the same semantics per dialect, sec plan section 5.1); `AstSecurityVisitorTests.Unsupported_construct_is_denied_not_passed_through` per dialect theory [Security]; `AstFuzzTests.Corpus_retained_and_runs_for_postgres_and_sqlserver` [Security]; `DatabricksParitySnapshotTests.Generated_sql_matches_snapshot(caseId)`.
- `Autheris.Tests.Integration`: `DatabricksStatementExecutionContractTests.Uses_obo_token_per_caller`, `.Binds_parameters_not_literals`, `.Pages_until_completion`, `.Maps_errors_to_typed_errors`, `.Fails_closed_on_401_403_and_timeout` [Security]; `FederationParityTests.Pg_x_SqlServer_join_applies_rls_and_masks_per_subtree` [Security], `.Databricks_x_SqlServer_join_applies_consent_in_staging` [Security], `.Http_x_Pg_join_matches_reference`; `FederationSecurityTests.Cross_source_join_applies_per_source_decision_masking_and_tenant_isolation_in_staging` [Security] (SEC-SIMPL-94, exact name); `DuckDbCoreSecurityTests.Olap_and_table_cache_are_tenant_scoped_and_parameterized` [Security] (SEC-SIMPL-94, exact name; in-process DuckDB, no container).
- `Autheris.Tests.Unit` or Common: `TestInfrastructureTests.Missing_runtime_fails_in_ci_and_skips_locally` [Security], `.No_databricks_spark_or_oracle_credentials_in_repo_and_secrets_only_in_nightly_workflow` [Security].

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Parity -c Release --filter "Dialect=PostgreSql|Dialect=SqlServer|(Dialect=Oracle&Category=Security)"`
- `dotnet test tests/Autheris.Tests.Integration -c Release --filter "FullyQualifiedName~FederationParityTests|FullyQualifiedName~FederationSecurityTests|FullyQualifiedName~DuckDbCoreSecurityTests|FullyQualifiedName~DatabricksStatementExecutionContractTests"`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --filter "FullyQualifiedName~DialectParityGateTests|FullyQualifiedName~DialectGateTests"`
- `grep -c '"pending"' tests/Autheris.Tests.Parity/parity-baseline-dialects.json` limited to sqlite-only reasons; `git grep -n 'sqlserver_host' tests/` returns nothing; `ls stryker-config*.json | wc -l` = 2.

## 8. Acceptance criteria
- [ ] No Pending cell for PostgreSQL, SQL Server, Oracle, Databricks in the baseline JSON.
- [ ] PR run executes PG + SS full, Oracle security smoke, Databricks contract, DuckDB lane (federation parity, `FederationSecurityTests`, `DuckDbCoreSecurityTests`); Security skipped count = 0 in trx.
- [ ] The dialect gate derives its set from `TargetSqlDialect` minus Ansi, DuckDb (and Sqlite until WP-SIMPL-31).
- [ ] Nightly has SS, Oracle full and Databricks (real or Spark, engine recorded) jobs; release workflow checks last nightly.
- [ ] No credentials in repo/workflows (test green); Stryker configs = 2; AST security mutation floor unchanged.
- [ ] Median PR wall-clock (including queue) not worse than the WP-SIMPL-10 baseline; OD-6 target (< 30 min) enforced by WP-SIMPL-35.

## 9. PR slicing (tests-only PRs, <= 400 net src lines each)
1. 21-1: `RequiresContainerRuntimeFactAttribute`, gate tests (gate marked as expected-fail list), SS cells executed + generator fixes.
2. 21-2: PG reference switch; Oracle fixture, traits, smoke cells.
3. 21-3: Databricks snapshot + contract tests; FederationParityTests, FederationSecurityTests, DuckDbCoreSecurityTests.
4. 21-4 `[WF]`: ci.yml/parity-nightly.yml/release.yml, image pre-pull, Spark job to nightly, Stryker merge; gate becomes fully strict.

## 10. Risks, rollback, must NOT
- Risk: SS/Oracle cells reveal generator bugs; Oracle container start (~2-3 min) eats CI budget, mitigated by pre-pull and smoke subset only.
- Rollback: revert 21-4 restores old workflows; tests stay.
- Must NOT: mark a cell Pending or skipped to get green; delete SQLite cells; put Databricks/Oracle credentials in any file; lower a Stryker floor; edit workflows without owner approval.

## 11. Estimate
Tests +2,300 / -2,000; src only generator fixes (< 200). 7 person-days.

## Decisions taken in this spec
- PR Oracle subset = `Dialect=Oracle&Category=Security`; Databricks contract tests carry `Category=DatabricksContract`.
- `RequiresContainerRuntimeFactAttribute` is introduced here (needed by SEC-SIMPL-50) and reused by WP-SIMPL-22.
- Federation and Databricks contract tests live in `Autheris.Tests.Integration` (plan text), snapshot tests in `Autheris.Tests.Parity`.

# Plan: bounded retry for SQL Server shape-verify catalog reads (Issue #849)

Phase 2 (solution architect), 2026-11-19. Code facts verified on `origin/main` `e3e32b401`. Implementation starts after the v1.9.0 tag.

## 1. Problem and evidence

- Issue: [#849](https://github.com/themulle/autheris/issues/849). Integration runs on SQL Server fail now and then with error 102 ("incorrect syntax") during governance migrations. Each time a different step fails (0013, 0018), and re-runs pass.
- The #913 diagnostics caught the failure on PR #917 (job 135765371660): error 102, line 1, phase `shape-verify`. The statement was the primary-key read in `src/Autheris.Infrastructure/Persistence/Migrations/Shape/SqlServerSchemaCatalogReader.cs:49-56`, a static, read-only `SELECT` against `sys.indexes`/`sys.index_columns`/`sys.columns` with one bound parameter `@o`. It passes on every other run.
- Conclusion: the SQL is not at fault. The cause is transport or driver corruption, or command text altered on the way. The failures started about two days after the bump of `Microsoft.Data.SqlClient` from 7.1.1 to 7.1.2 (`0b78a73fc`, the net11 upgrade). That is a hypothesis, not proven.
- Context:
  - Shape verification runs inside the step transaction (`SqlServerGovernanceMigrationRunner.cs:134-139` sets `XACT_ABORT ON` and `LOCK_TIMEOUT`).
  - The phase is set in `GovernanceMigrationRunnerBase.cs:590`.
  - The reader goes through `SchemaCatalogReaderSupport.RowsAsync` (`src/Autheris.Infrastructure/Persistence/Migrations/Shape/IGovernanceSchemaCatalogReader.cs:46-52`), which is shared with `ListTablesAsync` and the PostgreSQL reader.
- The SqlClient version is pinned per project, not centrally: `src/Autheris.Infrastructure/Autheris.Infrastructure.csproj:10` and the Integration, Parity and SqlCompilerGate test projects. There is no `Directory.Packages.props`.

## 2. Options

| Option | Description | Verdict |
|---|---|---|
| A | Bounded retry (at most 3 attempts) on `SqlException.Number == 102`, only for the read-only shape-verify catalog reads. | **Recommended** as the product fix (owner direction). |
| B | Pin SqlClient back to 7.1.1. | Run as an experiment (WP-849-2), not as the fix: it reverts a security-maintained dependency, and the root cause is unproven. |
| C | Retry any failing migration statement. | Rejected: DDL steps are not idempotent within a doomed transaction. |

## 3. Decisions

1. D-849-1: The retry lives in a new `ShapeReadRetryPolicy`. It is applied only around `SchemaCatalogReaderSupport.RowsAsync` and the scalar read when they are called from a schema catalog reader. DDL batches, post-condition queries, `SET LOCK_TIMEOUT` and the history insert never retry. A ratchet test enforces this.
2. D-849-2: Retry only when all of these hold:
   - `SqlException.Number == 102`;
   - the attempt is below 3 (1 initial attempt plus at most 2 retries);
   - the step transaction is still usable: `transaction is null`, or `SELECT XACT_STATE()` on the same connection and transaction returns `1`;
   - `ct` is not cancelled.
   Otherwise the original exception is rethrown unchanged, so the #913 diagnostics stay the same.
3. D-849-3: Each retry logs a warning (`LoggerMessage` source-generated) with:
   - the full static statement text (catalog SQL only, no data and no parameters);
   - error number and line;
   - attempt number;
   - the `Microsoft.Data.SqlClient` assembly informational version.
   No server message is logged.
4. D-849-4: The backoff is fixed: 50 ms, then 200 ms. There is no jitter, because it runs inside one transaction holding schema locks.
5. D-849-5: PostgreSQL is unchanged. The policy is engine-scoped (`SqlServerSchemaCatalogReader` only).

## 4. Work packages

**WP-849-1 (S, about 140 LOC including tests): retry policy**
- New `src/Autheris.Infrastructure/Persistence/Migrations/Shape/ShapeReadRetryPolicy.cs`: `internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> read, Func<CancellationToken, Task<bool>> transactionUsable, ILogger logger, string statement, CancellationToken ct)`.
- `SqlServerSchemaCatalogReader`: wrap each `RowsAsync`/scalar call in `ListTablesAsync` and `ReadAsync` (lines 9-89). `transactionUsable` runs `SELECT XACT_STATE()` through the same `CatalogSession`. The logger comes from the verifier's caller. If none exists, add an optional `ILogger?` to `GovernanceSchemaShapeVerifier` and default to `NullLogger`.
- Unit tests: new `tests/Autheris.Tests.Unit/Governance/ShapeReadRetryPolicyTests.cs`:
  - `Error102_RetriesThenSucceeds`
  - `Error102_StopsAfterThreeAttempts_RethrowsOriginal`
  - `OtherSqlError_NoRetry`
  - `NonSqlException_NoRetry`
  - `TransactionNotUsable_NoRetry`
  - `Cancelled_NoRetry`
  - `Retry_LogsStatementAndSqlClientVersion`
  Build the `SqlException` with the WP-L-2 helper (see [`2026-11-19-plan-issue-group-pr899-904-913-review-lows.md`](2026-11-19-plan-issue-group-pr899-904-913-review-lows.md)), or put the predicate behind `SqlFailureInfo`.
- Architecture test: in `tests/Autheris.Tests.Architecture/`, add `ShapeReadRetry_IsUsedOnlyByTheSqlServerSchemaCatalogReader`. `ShapeReadRetryPolicy` may be referenced only from `SqlServerSchemaCatalogReader.cs`, and never from `SqlMigrationStep.cs`, `GovernanceMigrationRunnerBase.cs` or `SqlServerGovernanceMigrationRunner.cs`. This pins "no retry on DDL".

**WP-849-2 (XS, experiment, not merged to main): SqlClient 7.1.1 pin**
- On a throwaway branch, set `Microsoft.Data.SqlClient` to `7.1.1` in the four project files listed in section 1. Run the SQL Server integration shards 10 times through `workflow_dispatch`, and compare the error 102 rate with 10 runs on `main`.
- Record the result in the issue. If 7.1.1 shows 0 out of 10 and `main` fails at least once, file an upstream SqlClient report with the captured statement. The retry stays either way.

Order: WP-849-1, then WP-849-2 (which measures with and without the retry log). No DDL. No dependencies, except the optional WP-L-2 helper.

## 5. Integration test impact

- None functionally. Every migration contract suite on SQL Server runs through the reader. GitHub Actions shows retries as warnings in the log, and the logged statement serves as further evidence for #849.
- WP-849-2 touches `tests/Autheris.Tests.Integration/Autheris.Tests.Integration.csproj:14`, `tests/Autheris.Tests.Parity/Autheris.Tests.Parity.csproj:13` and `tests/Autheris.Tests.SqlCompilerGate/Autheris.Tests.SqlCompilerGate.csproj:11`, on the experiment branch only.

## 6. Phase 3

**Not required.** The retry covers read-only, static catalog SQL inside the migration runner. The log carries static SQL only. If Phase 3 wants to confirm the log content, one look at D-849-3 is enough.

## 7. Owner questions (recommended defaults)

1. Q1: Close #849 after WP-849-1, or keep it open until WP-849-2 reports? Default: **keep it open until the experiment reports**, then close.
2. Q2: Keep the retry if 7.1.1 is clean? Default: **yes**. It is cheap, bounded and scoped. Revisit it on the next SqlClient release.

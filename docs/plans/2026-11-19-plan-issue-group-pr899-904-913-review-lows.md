# Plan: Low findings from the 1.9.0 reviews of PR #899, #904 and #913

Phase 2 (solution architect), 2026-11-19. These findings were left open when the PRs were merged. No GitHub issues exist. Code facts verified on `origin/main` `e3e32b401`. Implementation starts after the v1.9.0 tag. The findings from PR #887 have their own plan: [`2026-11-19-plan-issue-group-pr887-trino-metadata-followups.md`](2026-11-19-plan-issue-group-pr887-trino-metadata-followups.md).

## 1. Findings, evidence and status

| ID | Source | Evidence on main | Status |
|---|---|---|---|
| L-899-1 | [PR #899](https://github.com/themulle/autheris/pull/899) review finding 1 | `src/Autheris.Infrastructure/Persistence/GovernanceRepository.CdcStreams.cs:23`: `new CdcStreamSql(_dialect is not PostgreSqlGovernanceDialect)`. An unknown dialect becomes SQL Server; the old code threw `NotSupportedException`. | **Fixed on local branch** `refactor/simpl-20-fold-consent-cores` (WP-SIMPL-20 PR A, `50dece1ee`): a switch on `PostgreSqlGovernanceDialect`/`SqlServerGovernanceDialect` that throws `NotSupportedException` otherwise, plus `tests/Autheris.Tests.Unit/Cdc/CdcStreamSqlDialectTests.cs`. Not pushed. |
| L-899-2 | PR #899 review finding 2 | `docs/plans/simplification/WP-SIMPL-20-addendum-2026-11-18.md:42` says `MaxAllowedLoc` 16,024 -> 16,010. The code (`GovernanceRepositoryLocBudgetTests.cs:14`) has 15,842. | **Fixed on the same local branch** (`f9c467f1b`). Not pushed. |
| L-899-3/4/5 | PR #899 Info items 3-5 (`Resolve`/`Cmd` string replaces on the poll path, lazy `_cdcCore ??=` race, test name of `Only_AuditSqlCore_keeps_the_SqlCore_suffix`) | `GovernanceRepository.CdcStreams.cs:23-24` and the `Resolve`/`Cmd` helpers | PR A keeps the lazy `_cdcCore ??=`, so item 4 stays open. The race is benign; WP-L-3 makes the initialization eager. Item 5 is tracked for PR A. Item 3 is optional (WP-L-3). |
| L-904-1 | [PR #904](https://github.com/themulle/autheris/pull/904) review finding 1 | `tests/Autheris.Tests.Integration/Persistence/TableObjectKindContractTests.cs:117-118`: the CHECK-violation insert uses the id `'t896b'` and only asserts `ThrowAsync<DbException>`, so a key or type error would also pass. | open |
| L-904-2 | PR #904 review finding 3 | `docs/plans/2026-11-19-plan-issues-894-896-898-catalog-paging-kind-facets.md:34` still says the step is `0029`. It was renumbered to `0030_table_object_kind`. | open (docs only; fixed by this plan commit, see section 3) |
| L-913-1 | [PR #913](https://github.com/themulle/autheris/pull/913) review finding 1 | `tests/Autheris.Tests.Unit/Governance/MigrationFailureDiagnosticsTests.cs:84-95` `CreateSqlException` uses the non-public `SqlError` constructor, `SqlErrorCollection.Add` and `SqlException.CreateException`. A `Microsoft.Data.SqlClient` upgrade (currently 7.1.2) breaks it with an opaque reflection error. | open |

## 2. Decisions

1. D-L-1: L-899-1 and L-899-2 are delivered by WP-SIMPL-20 PR A. This plan only checks them during the PR A review. No new work package.
2. D-L-2: L-904-1: assert the constraint name. PostgreSQL: `PostgresException.ConstraintName == "ck_tables_object_kind"`. SQL Server: the message contains `CK_TABLES_object_kind`. Check the exact names in `src/Autheris.Infrastructure/Persistence/Migrations/Steps/{postgresql,sqlserver}/0030_table_object_kind.sql`. Use a GUID id.
3. D-L-3: L-913-1: Options are (a) keep reflection but fail with a clear message that names the driver version; (b) move the `DescribeFailure` input to a small seam (`SqlFailureInfo(int Number, int LineNumber, string? Procedure)`) built from `SqlException` in one place and tested without a real `SqlException`. **Decision: (b)**: the production mapper stays a one-line adapter, and the tests no longer depend on driver internals. Keep one reflection-based smoke test, guarded by a clear `Skip` message that names the SqlClient version when the reflection lookup fails.

## 3. Work packages

**WP-L-1 (XS): contract test assertion (L-904-1)**
- `TableObjectKindContractTests.cs:113-118`: replace `Should.ThrowAsync<DbException>` with `var ex = await Should.ThrowAsync<DbException>(act); ConstraintNameOf(ex).ShouldBe(...)`. Use a per-engine abstract `ExpectedObjectKindConstraint` on `TableObjectKindContractBase`, and a `Guid.NewGuid().ToString("N")` id.
- Integration project: compile check locally, run on GitHub Actions.

**WP-L-2 (S, about 60 LOC): SqlClient-independent diagnostics tests (L-913-1)**
- `GovernanceMigrationRunnerBase.DescribeFailure(Exception)` (`src/Autheris.Infrastructure/Persistence/Migrations/GovernanceMigrationRunnerBase.cs:539`): add `internal static string DescribeFailure(SqlFailureInfo info)`. The `SqlException` arm of the existing switch maps to it.
- `MigrationFailureDiagnosticsTests`: move all cases to `SqlFailureInfo`. Keep `RealSqlException_MapsNumberAndLine` with the reflection helper, which skips with "SqlClient <version> changed its internal SqlError API" when the lookup fails.
- Run the filtered test `MigrationFailureDiagnosticsTests`.

**WP-L-3 (XS, optional): CDC SQL text cache and eager core (L-899-3, L-899-4)**
- After PR A: initialize `_cdcCore` eagerly in the constructor path, or use `Lazy<CdcStreamSql>`. Cache the resolved statement text per constant in a `static readonly` `FrozenDictionary` per dialect, instead of chained `string.Replace` on each `ReadAsync` poll.
- Tests: `CdcStreamSqlDialectTests` (`Resolve_IsCachedPerDialect`). Do this only if PR A has not already done it.

**Docs (done in this plan commit): L-904-2.** The changelog of the 894-896-898 plan records the renumber from 0029 to 0030.

Order: independent, so WP-L-1 and WP-L-2 can be one small PR. WP-L-3 follows PR A. No DDL.

## 4. Integration test impact

- `tests/Autheris.Tests.Integration/Persistence/TableObjectKindContractTests.cs` (WP-L-1) runs on GitHub Actions for both engines.
- WP-L-2 and WP-L-3 have no integration impact.

## 5. Phase 3

**Not required.** These are test and diagnostics hygiene only. L-899-1 is a fail-closed improvement that is already reviewed in PR A.

## 6. Owner questions

None.

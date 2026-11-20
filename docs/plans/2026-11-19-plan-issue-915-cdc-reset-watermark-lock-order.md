# Plan: SQL Server CDC watermark reset takes the stream lock first (Issue #915)

Phase 2 (solution architect), 2026-11-19. This is a short plan because the fix already exists on a local branch and was approved in a local review. Code facts verified on `origin/main` `e3e32b401`.

## 1. Problem and evidence

- Issue: [#915](https://github.com/themulle/autheris/issues/915). Found in the Phase 5 review of #905.
- `src/Autheris.Infrastructure/Persistence/GovernanceRepository.CdcStreams.cs:415-428` (`CdcStreamSql.ResetWatermarkAsync`): on SQL Server, the reset is a single `UPDATE w ... FROM %watermarks% w JOIN %streams% s`. Depending on the plan, it can lock the watermark before it reads the stream row. That is the reverse of the append order, so a deadlock (1205) with a concurrent append is possible.
- Severity: Low. It is an admin operation, and the deadlock victim rolls back cleanly.

## 2. Delivered fix (local, not pushed)

- Branch `fix/915-watermark-lock-order`, head `6109807d6` (`5710ebf08` fix, `5b202773f` review fixes, `6109807d6` comment tidy).
- **The branch is stacked on `refactor/simpl-20-fold-consent-cores` (WP-SIMPL-20 PR A, `f9c467f1b`).** It merges after PR A, or is rebased onto `main` if PR A slips.
- Content of the fix:
  - The SQL Server reset no longer holds a stream lock while it locks the watermark.
  - The contract case `tests/Autheris.Tests.Integration/Governance/CdcStreamsContract.ResetConcurrency.cs` runs a concurrent reset and append on PostgreSQL and SQL Server.
  - The unit test `tests/Autheris.Tests.Unit/Cdc/CdcStreamResetWatermarkSqlTests.cs` covers the SQL text. `CdcStreamSqlDialectTests` belongs to PR A (the #899 dialect fix).
  - The contract coverage scanner accepts a partial contract class.
  - The `GovernanceRepositoryLocBudgetTests` budget is recorded.
  - A `CHANGELOG.md` line is added.

## 3. Work package

**WP-915-1 (XS): land the branch**
1. After the v1.9.0 tag and after WP-SIMPL-20 PR A merges, rebase `fix/915-watermark-lock-order` onto `main`.
2. Recompute `GovernanceRepositoryLocBudgetTests.MaxAllowedLoc`. This is a shrink-only ratchet, so record the measured value.
3. Resolve `CHANGELOG.md`.
4. Run the filtered unit test `CdcStreamResetWatermarkSqlTests` and the architecture tests `GovernanceContractCoverageTests` and `GovernanceRepositoryLocBudgetTests`.
5. Push, open the PR and let GitHub Actions run the contract suites.

No DDL.

## 4. Integration test impact

`tests/Autheris.Tests.Integration/Governance/Contract/CdcStreamsContract.cs` becomes `partial` and gains `tests/Autheris.Tests.Integration/Governance/CdcStreamsContract.ResetConcurrency.cs`. Both are already on the branch and run on GitHub Actions for PostgreSQL and SQL Server.

## 5. Phase 3

**Not required.** It changes lock order only. Tenancy, authorization and data exposure do not change.

## 6. Owner questions

None.

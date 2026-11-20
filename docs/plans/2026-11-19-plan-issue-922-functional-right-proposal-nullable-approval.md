# Plan: functional right proposals fail on PostgreSQL and SQL Server (Issue #922)

- Issue: [#922](https://github.com/themulle/autheris/issues/922). Phase 2 plan, 2026-11-19. Code facts verified on `origin/main` after the v1.9.0 tag.
- Type: production bug (governance write path), forward-only governance DDL step.
- Phase 3: **required (light)**. The change touches the four-eyes approval data of functional rights. The approval gate itself does not change.

## 1. Problem and evidence

1. `POST /api/v1/tenants/{tenant}/governance/rights/{realm}` (proposal) returns HTTP 500 on PostgreSQL. The following `approve` call throws `KeyNotFoundException` because no row was stored.
2. Both baselines declare the approval columns `NOT NULL`:
   - `src/Autheris.Infrastructure/Persistence/Migrations/PostgreSqlBaselineSchema.cs:700-701` (`approved_by VARCHAR(128) NOT NULL`, `approved_at TIMESTAMP WITH TIME ZONE NOT NULL`).
   - `src/Autheris.Infrastructure/Persistence/Migrations/SqlServerBaselineSchema.cs:815-816` (`approved_by NVARCHAR(128) NOT NULL`, `approved_at NVARCHAR(64) NOT NULL`).
3. The domain model writes NULL on two paths:
   - `src/Autheris.Application/Governance/Rights/FunctionalRightMapper.cs:222-233`: `ProposeMappingAsync` creates the mapping with `ApprovedBy: null, ApprovedAt: null`.
   - `FunctionalRightMapper.cs:138-145`: a privileged self-approval in the creation payload is cleared to `ApprovedBy = null, ApprovedAt = null`.
4. `src/Autheris.Infrastructure/Persistence/RightsAndPoliciesStore.cs:119-150` (`CreateMappingAsync`) binds `DBNull` for a missing approver (line 150) in the PostgreSQL `ON CONFLICT` and SQL Server `MERGE` upserts.
5. The read side already expects NULL: `RightsAndPoliciesStore.cs:87` filters `approved_by IS NOT NULL AND approved_at IS NOT NULL`, and `ReadMapping` (line 193) maps `IsDBNull`.
6. No step in `Migrations/Steps/{postgresql,sqlserver}/0002..0030` relaxes these columns. SQL Server is affected in the same way; the issue only observed PostgreSQL.
7. The contract suite `tests/Autheris.Tests.Integration/Governance/Contract/FunctionalRightContract.cs` only creates approved mappings (helper at line 28 takes a non-null `approvedBy`), so CI never inserted a pending proposal.

## 2. Options

| Option | Description | Verdict |
|---|---|---|
| A | Forward-only governance step: `approved_by` and `approved_at` become nullable on both engines | **Recommended.** Matches the model and the read filter. Additive relaxation, no data rewrite. |
| B | Store a sentinel approver (for example `''`) for pending proposals | Rejected. A sentinel that is non-null passes `approved_by IS NOT NULL`. That fails open unless every reader is changed. |
| C | Separate `FUNCTIONAL_RIGHT_PROPOSALS` table | Rejected. Larger scope with no benefit; the model already carries pending state in the same row. |

## 3. Step number and merge order

- `main` ends at step `0030`. Step `0031` is taken by WP-SIMPL-52b (`0031_access_profile_items` on `feat/simpl-52-profile-items`). Step `0032` is reserved for #916 (`0032_metric_view_kind_and_column_role`); that work has not started.
- The step runner needs a gap-free catalogue. A branch that merges later renumbers its step pair, both checksum goldens, `StepSqlLintTests` and its step test.
- **Recommendation (default taken, Q-922-1):** #922 is a bug that blocks the four-eyes workflow on both server engines, so it goes ahead of #916. It takes **`0032_functional_right_mapping_pending_approval`**, and #916 moves to `0033`. #916 is not started, so the move only changes text in its plan.
- If the owner keeps the #916 reservation, #922 takes `0033` and merges after #916. The work packages below write the number as `00NN`.
- Hard dependency: WP-SIMPL-52b (`0031`) merges first. If 52b is still open when #922 is ready, #922 waits; it does not take `0031`.

## 4. Work packages (one PR, DDL queue)

### WP-922-1 (S): governance step `00NN_functional_right_mapping_pending_approval`
- Files:
  - `src/Autheris.Infrastructure/Persistence/Migrations/Steps/postgresql/00NN_functional_right_mapping_pending_approval.sql`: `ALTER TABLE FUNCTIONAL_RIGHT_MAPPINGS ALTER COLUMN approved_by DROP NOT NULL, ALTER COLUMN approved_at DROP NOT NULL;`. Idempotent. Follow the header-comment style of `0030_table_object_kind.sql`.
  - `.../Steps/sqlserver/00NN_functional_right_mapping_pending_approval.sql`: `ALTER TABLE dbo.FUNCTIONAL_RIGHT_MAPPINGS ALTER COLUMN approved_by NVARCHAR(128) NULL;` and `... ALTER COLUMN approved_at NVARCHAR(64) NULL;`. Restate the exact baseline types. Keep the column collation: read it from the baseline or step `0008`, and add an explicit `COLLATE` if the column has a non-default collation (an `ALTER COLUMN` without `COLLATE` resets it). Model the step on `sqlserver/0019_pattern_grants.sql:7`.
  - `00NN_functional_right_mapping_pending_approval.shape.json` for both engines: two `alterColumn` ops with `"nullable": true`. Copy the format from `postgresql/0019_pattern_grants.shape.json` (lowercase table, `CHARACTER VARYING(128)` / `TIMESTAMP WITH TIME ZONE`) and `sqlserver/0019_pattern_grants.shape.json` (uppercase table, `NVARCHAR`).
  - `tests/Autheris.Tests.Architecture/Golden/GovernanceSteps/checksums.postgresql.txt` and `checksums.sqlserver.txt`: append the `00NN` line (regenerate as documented in the golden test).
  - `tests/Autheris.Tests.Unit/Governance/StepSqlLintTests.cs`: add the new step to the linted set if the test lists steps explicitly; the step must pass `StepSqlLint` (`src/Autheris.Infrastructure/Persistence/Migrations/StepSqlLint.cs`).
  - Do not edit the baselines. v1.5.0 is the frozen base, and the baselines carry only the frozen schema plus forward steps.
- Unit tests: new `tests/Autheris.Tests.Unit/Persistence/Migrations/FunctionalRightPendingApprovalMigration00NNTests.cs`, modelled on `TableObjectKindMigration0030Tests.cs`:
  - `Step00NN_PostgreSql_DropsNotNullOnApprovalColumns`
  - `Step00NN_SqlServer_RestatesTypesAsNullable`
  - `Step00NN_ShapeJson_MatchesSql`
- Acceptance: both step files lint clean; the golden checksums match; the shape verify passes on both engines in CI.

### WP-922-2 (S): contract tests for pending proposals (both engines)
- File: `tests/Autheris.Tests.Integration/Governance/Contract/FunctionalRightContract.cs` (runs as `PostgreSqlFunctionalRightContractTests` and `SqlServerFunctionalRightContractTests`, lines 246 and 253).
- New tests:
  - `CreateMapping_PendingProposal_PersistsNullApproval`: insert with `ApprovedBy = null, ApprovedAt = null`; read back with `GetMappingAsync`; both are null.
  - `ApprovedMappings_PendingProposal_Excluded`: a pending row is never returned by the approved-mappings query (line 87 filter). Mark it `[Trait("Category", "Security")]`.
  - `ApproveMapping_PendingProposal_BecomesEffective`: propose, approve as a distinct user, and the row appears in approved mappings.
  - `CreateMapping_ReproposalOverApproved_ResetsApproval`: an upsert of a pending proposal over an approved row clears the approval. This pins the fail-closed upsert behaviour of lines 125-143. Mark it `[Trait("Category", "Security")]`.
- Unit tests (local TDD): `tests/Autheris.Tests.Unit/**/FunctionalRightMapperTests` (find the class with `git grep -l ProposeMappingAsync tests/Autheris.Tests.Unit`). Add `ProposeMapping_PassesNullApprovalToRepository`.
- Acceptance: the integration suite passes on PostgreSQL and SQL Server in GitHub Actions. Locally, compile-check only.

### Order
WP-922-1, then WP-922-2, in one PR. Start after WP-SIMPL-52b merges.

## 5. Integration test impact
- `tests/Autheris.Tests.Integration/Governance/Contract/FunctionalRightContract.cs` (extended).
- Migration runner, shape-verify and fresh-install suites under `tests/Autheris.Tests.Integration/Persistence/` pick up the new step automatically. Check that no test pins the last step number to `0030` or `0031` (`git grep -n "0031\|LatestStep" tests/Autheris.Tests.Integration`).
- `tests/Autheris.Tests.Integration/Governance/FunctionalRight*` endpoint tests, if any, gain a proposal-then-approve path.

## 6. Phase 3 focus (no conditions invented here)
- The read filter at `RightsAndPoliciesStore.cs:87` stays the only gate. Nullable columns must not make a pending row effective.
- Re-proposal over an approved row resets approval (more restrictive). Phase 3 decides whether that is acceptable or should be rejected with 409.

## 7. Owner questions
- Q-922-1: does #922 take step `0032` ahead of #916, with #916 moving to `0033`? **Default taken: yes.**
- Q-922-2: should a re-proposal over an approved mapping be rejected (409) instead of resetting the approval? **Default taken: keep the reset (fail-closed); Phase 3 may decide otherwise.**

# WP-SIMPL-19 Governance repository contract suite
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-19-governance-repository-contract-suite-persistence-lane-first) | Issues: #515 (stage 2) | Owner decisions: OD-1, OD-17(9) | Security conditions: SEC-SIMPL-05, SEC-SIMPL-13, SEC-SIMPL-20, SEC-SIMPL-23

## 1. Goal and outcome
One engine-neutral contract suite pins the behaviour of the existing PostgreSQL and SQL Server governance repository families (26 partials each) before WP-SIMPL-20 merges them. Outcome: one abstract `*Contract` class per aggregate, run by exactly two sealed subclasses (PostgreSQL, SQL Server) with identical expectations; the 40+ per-engine `PostgreSql*ContractTests`/`SqlServer*ContractTests` copies are replaced. Suite green on both engines; no `Skip`.

## 2. Preconditions and dependencies
- Merged: WP-SIMPL-11 (SQLite family gone, `GovernancePostgresFixture` exists), WP-SIMPL-12 (ITSM/Backstage/external catalog repositories gone, so they are not pinned).
- Blocks WP-SIMPL-20 (SEC-SIMPL-05/20). Persistence lane serial; parallel only with non-Persistence WPs.

## 3. Scope
### In scope
- `tests/Autheris.Tests.Integration/Governance/Contract/` abstract contracts + 2 subclasses each; `GovernanceSqlServerFixture` (mirror of the PG fixture).
- Consolidating the existing `*ContractTestsBase` classes into this folder.
- Fixing any divergence found (bug fix in the failing family, TDD, own commit).
### Out of scope
- Any production refactoring toward the unified repository (WP-SIMPL-20); `GovernanceCoreSecurityTests` (WP-SIMPL-20).
- CDC watermark / semantic-manifest / key-index reader contracts (not governance repositories).

## 4. Current state (verified @ 63cbb893, before WP-SIMPL-11/12)
- Existing reusable bases in `tests/`: `CatalogKeyDefinitionContractTestsBase`, `ConsentQueryContractTestsBase`, `ConsentRevokeUnificationContractTestsBase`, `CatalogSettingsRepositoryContractTestsBase`, `DataOwnerAssignmentRepositoryContractTestsBase`, `RelationRepositoryContractTestsBase`, `RowFilterEvaluationRepositoryContractTestsBase`, `CloudEventSubscriptionStoreContractTests`, `WebhookOutboxContractTests`, `CdcStreamStoreContractTests`.
- Per-engine pairs (PostgreSql/SqlServer): AccessProfileRepository, CallerAttribute, CatalogKeyDefinition, CatalogSettingsRepository, CloudEventSubscriptionStore, Consent, ConsentDecisionStatusGuard, ConsentDescriptionRepository, ConsentQuery, ConsentRevokeUnification, DataOwnerAssignmentRepository, Governance, GovernanceStores, MetadataCacheReplica, RelationRepository, RowFilterEvaluationRepository, TenantRegistry, VirtualFilter, WebhookOutbox; PG-only: RowScope.
- `Testcontainers.MsSql` 4.16.0 in `Autheris.Tests.Integration.csproj:21`; `tests/Autheris.Tests.Parity/Dialects/ParitySqlServerHostFixture.cs` = container reference.

## 5. Implementation steps
1. `tests/Autheris.Tests.Common/Governance/GovernanceSqlServerFixture.cs`: one `MsSqlContainer` (pinned image) per assembly, migrate `autheris_template` via `SqlServerGovernanceMigrationRunner`; per-class DB via `BACKUP`-less approach: create DB + run runner (SQL Server has no TEMPLATE; cache by applying migrations once per class, acceptable at < 3 s), collection `GovernanceSqlServerCollection`.
2. `Contract/GovernanceContractBase.cs`: `public abstract class GovernanceContractBase : IAsyncLifetime { protected abstract Task<GovernanceTestStore> CreateStoreAsync(); }` where `GovernanceTestStore` exposes the concrete repository interfaces (`IConsentRepository` etc., resolved from the real DI registration `RegisterGovernanceAggregateStores<,>` with the fixture connection string, so DI wiring is pinned too) and two tenant ids.
3. Per aggregate, one abstract contract (move and merge the existing base + per-engine assertions; keep the strictest): `ConsentContract`, `ConsentGrantsContract`, `ConsentPageContract`, `CatalogContract` (+ key definitions, settings), `SchemaContract`, `AuditContract`, `CdcStreamsContract`, `WebhookOutboxContract`, `CloudEventSubscriptionsContract`, `DataOwnerAssignmentContract`, `AccessProfilesContract`, `CallerAttributesContract`, `RelationContract`, `RowFilterEvaluationContract`, `TenantRegistryContract`, `VirtualFilterContract`, `MetadataCacheReplicaContract`, `RowScopeContract` (add SQL Server leg). Subclasses: `PostgreSql<X>ContractTests : <X>Contract` `[Collection(GovernancePostgresCollection.Name)]`, `SqlServer<X>ContractTests` likewise.
4. Every contract includes the cross-cutting cases from section 6. Run on both engines; on divergence write a failing test, fix the family, separate commit `fix(persistence): ...`.
5. Delete the replaced per-engine files and old bases after their assertions are moved (PR table maps old method -> new method).

## 6. Tests (all in `Autheris.Tests.Integration`, namespace `Autheris.Tests.Integration.Governance.Contract`)
Cross-cutting, inherited by every aggregate contract:
- `Reads_never_return_other_tenant_rows` [Security], `Writes_cannot_modify_other_tenant_rows` [Security], `Missing_or_unparseable_tenant_id_throws` [Security] (DEF-02 fail-closed parse), `Upsert_is_idempotent`, `Unique_violation_maps_to_conflict_exception`, `Paging_is_stable_and_ordered`, `Page_size_above_cap_is_clamped_or_rejected` [Security] (SEC-SIMPL-23; asserts current cap value per repository), `Soft_deleted_rows_are_invisible_to_reads` [Security] (where soft delete exists).
Aggregate-specific:
- `AuditContract.Audit_rows_are_append_only` [Security], `.Chain_order_matches_insert_order`, `.Batch_insert_respects_max_batch_size` [Security].
- `ConsentContract.Revoke_unifies_grants` (from `ConsentRevokeUnificationContractTestsBase`), `.Decision_status_guard_rejects_invalid_transition`.
- Arch (`Autheris.Tests.Architecture`): `GovernanceContractCoverageTests.Every_governance_repository_interface_has_contract_on_both_engines` (reflection: each interface registered by `RegisterGovernanceAggregateStores` has an abstract contract with exactly one PostgreSql and one SqlServer subclass), `.No_skipped_contract_tests`.
- Delete: per-engine `PostgreSql*/SqlServer*ContractTests.cs` listed in section 4 and the old `*ContractTestsBase.cs` once merged.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror`
- `dotnet test tests/Autheris.Tests.Integration --no-build --filter "FullyQualifiedName~Governance.Contract"` (both engines; podman override).
- `dotnet test tests/Autheris.Tests.Architecture --no-build --filter "FullyQualifiedName~GovernanceContractCoverage"`
- `git grep -n 'Skip *=' -- tests/Autheris.Tests.Integration/Governance/Contract` empty.

## 8. Acceptance criteria
- [ ] Each aggregate in section 5.3 has one abstract contract and exactly 2 engine subclasses; coverage arch test green.
- [ ] Identical expectations on both engines; 0 skipped; every divergence fixed with a regression test.
- [ ] Every security-tagged assertion of deleted per-engine tests is present in the suite (PR mapping table).
- [ ] Test LOC: about +3,000 / -4,000.

## 9. PR slicing (tests only; src changes only for divergence fixes)
1. PR-A: SQL Server fixture + `GovernanceContractBase` + Consent/ConsentGrants/ConsentPage + coverage arch test (allowlist of not-yet-covered interfaces that may only shrink).
2. PR-B: Catalog, Schema, Audit.
3. PR-C: remaining aggregates; allowlist empty; old per-engine files deleted.

## 10. Risks, rollback, must NOT
- Risks: real divergences surface (expected; fix, do not normalise expectations); SQL Server container startup time (one per assembly).
- Must NOT: weaken an assertion to make both engines pass; use `Skip` or engine-specific `if`; refactor production repositories beyond the divergence fix; mock the database.

## 11. Estimate
Tests +3,000 / -4,000; src only divergence fixes (< 150); 4 person-days.

## Decisions taken in this spec
1. Contracts resolve repositories through the real DI registration, so WP-SIMPL-20 DI changes are covered.
2. Coverage arch test uses a shrink-only allowlist across PR-A..C, empty at the end.
3. SQL Server per-class database is migrated directly (no template support); accepted cost.

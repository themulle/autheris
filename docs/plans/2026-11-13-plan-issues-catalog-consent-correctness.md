# Plan: catalog, dbt and consent correctness (open issues cluster)

- **Date:** 2026-11-13 · **Base:** `origin/main` @ `10400ec4` · **Status:** PLANNED (Phase 3 pending)
- **Parent:** [`00-master-plan-overview.md`](00-master-plan-overview.md) · WP specs: [`simplification/README.md`](simplification/README.md)
- **Binding:** owner decisions OD-1..OD-45 in [PRD section 2.0](2026-11-05-req-project-simplification.md) (a later OD wins). Feature freeze per PRD 5.1: bug fixes and security fixes need no approval; anything else needs an owner `freeze-exception` (WP-SIMPL-38..48 are pre-approved). PR limit: 400 net added `src/` lines.
- **Migrations:** forward-only steps per engine (v1.5.0 frozen base; applied steps never edited). The next free step at the time of writing is `0010` (after `0009_write_grants`); every DDL PR takes the next free number at merge time and merges serially with other DDL PRs (WP-SIMPL-47 PR 1, WP-SIMPL-52).
- **Issues:** #459 (addendum only), #666, #667, #670, #671, #680, #610, #612. WP ids `WP-OI-CAT-<n>`, owner questions `Q-CAT-<n>`.

## Summary

| Issue | Verdict | WP / spec | Open on main | Size | Phase 3 |
|---|---|---|---|---|---|
| #459 declared keys/indexes only | COVERED | [WP-SIMPL-44](simplification/WP-SIMPL-44-no-source-metadata-reads.md) PR 4 (steps 11, 12) | physical key/index chain and dbt `meta.indexes` (PR 1, 2 merged; PR 3, 4 not started) | per spec (+~200/-~1,500 src) | per spec (sec plan 11.2 row 44) |
| #666 dbt identifier/alias | NEW PLAN | WP-OI-CAT-1 (before or inside WP-SIMPL-28 PR 3) | all | S (1 PR) | yes (short) |
| #667 apply role spellings | NEW PLAN | WP-OI-CAT-2 | all | S (1 PR) | yes |
| #670 revoke/replace ignores token admin roles | NEW PLAN | WP-OI-CAT-3 (coordinated with WP-SIMPL-47 PR 2/3) | all | M (1 PR) | yes |
| #671 apply prune, REST revoke | PARTLY COVERED | REST revoke: WP-SIMPL-47 PR 5 (not started); prune: WP-OI-CAT-4 as WP-47 addendum | all | M (1 PR) | yes |
| #680 AutoSyncFromDbt vs SqlEndpoints.Enabled | NEW PLAN | WP-OI-CAT-5 | all | S (1 PR) | no |
| #610 TABLE_COLUMNS ordinal | NEW PLAN (WP-SIMPL-11 follow-up) | WP-OI-CAT-6 | all; SQL Server also unordered | S (1 PR + steps) | no |
| #612 federation explain vs eligibility | NEW PLAN (WP-SIMPL-36 sp1b OQ-2) | WP-OI-CAT-7 | all | S (1 PR) | yes (C-6 check) |

Recommended order: WP-OI-CAT-2 -> WP-OI-CAT-3 (same caller path, same tests) -> WP-OI-CAT-4 (after WP-SIMPL-47 PR 1 and PR 2); WP-OI-CAT-1, -5, -6, -7 independent and parallel. WP-OI-CAT-1 and -5 land before WP-SIMPL-28 PR 3 (dbt module extraction) or are carried by it. WP-OI-CAT-6 lands before #673 (repository batch upsert, catalog-scale cluster) because both touch the `TABLE_COLUMNS` insert path; WP-OI-CAT-1 lands before #674 (skip unchanged tables) because it changes the table identity the comparison keys on.

## #666 dbt sync: use identifier (sources) and alias (models) as physical table name

- **Verdict:** NEW PLAN (bug, no freeze approval needed). Not covered by WP-SIMPL-44 (declared catalog) or WP-SIMPL-28 (module move only).
- **Problem:** sources register under `name` instead of `identifier`, models under `name` instead of `alias`; catalog entries, relations and generated SQL endpoints point to non-existent tables.
- **Verified state:** `src/Autheris.Extensions/Dbt/DbtArtifactStreamingParser.cs:120-122` (models) and `:316-318` (sources) read only `name`, `database`, `schema`. `DbtModelDefinition` (`src/Autheris.Domain/Model/DbtMetadataModels.cs:26`) has no physical-name field; `ToTableIdentifier()` (`:46`) builds `(Database, Schema, Name)` and is the single identity used by ingestion (`DbtMetadataIngestionService.cs:332, 370, 384, 412, 942`). Relations resolve by unique id first, by name as fallback (`DbtRelationModelLookup.cs:38-39, 54`). The SQL endpoint auto-sync passes `model.Name` as table name into `SELECT ... FROM schema.name` (`DbtMetadataIngestionService.cs:752-757`, `SqlEndpointLoader.SyncDbtModelDefinitionToFile` `:350-378`).
- **Root cause:** the parser conflates dbt's logical name and the relation name.
- **Design:**
  1. Add `string PhysicalName` to `DbtModelDefinition` (init, defaults to `Name`); parser sets it from `identifier` (sources) or `alias` (models, seeds, snapshots), falling back to `name` when absent, null or empty. `relation_name` is not parsed (quoting differs per adapter).
  2. `ToTableIdentifier()` uses `PhysicalName`; every caller is fixed by this one change. `ByName` relation fallback accepts `Name` and `PhysicalName` (unique-id path unchanged).
  3. `PhysicalName` passes the same identifier validation as `name` today (`DbtIdentifierSanitizer`, reason `INVALID_IDENTIFIER`); an invalid alias or identifier rejects the node, never falls back silently to `name`.
  4. SQL endpoint auto-sync: endpoint file name stays `model.Name`, the `FROM` target uses `PhysicalName`.
  5. Display name (Q-CAT-1): when `PhysicalName != Name` and the table has no display name, ingestion sets `DisplayName = Name` so the logical code stays visible.
  6. Existing entries under the logical name (Q-CAT-2): no automatic re-key; the sync result adds warning `PHYSICAL_NAME_CHANGED` per affected unique id, naming only sanitized identifiers.
- **Work packages:** WP-OI-CAT-1, one PR, ~70 src lines. Lands before WP-SIMPL-28 PR 3 or is carried by it.
- **Tests (TDD):** `tests/Autheris.Extensions.Tests/DbtPhysicalNameTests.cs`: `Source_WithIdentifier_RegistersIdentifierAsTable`, `Source_WithoutIdentifier_FallsBackToName`, `Model_WithAlias_RegistersAliasAsTable`, `Seed_And_Snapshot_WithAlias_UseAlias`, `Relationship_ByUniqueId_ResolvesToPhysicalNames`, `Relationship_ByNameFallback_MatchesLogicalOrPhysicalName`, `ForeignKeyConstraint_ResolvesToPhysicalName`, `DisplayName_SetToLogicalName_WhenDifferent`, `Sync_ReportsPhysicalNameChanged_ForPreviouslyRegisteredLogicalName`, `SqlEndpointAutoSync_SelectsFromPhysicalName`. Security (`[Trait("Category","Security")]`): `Alias_WithInvalidIdentifier_RejectsNode_NoFallbackToName`, `Identifier_WithQuoteOrSemicolon_NeverReachesGeneratedSql`, `PhysicalName_DoesNotCollapseTwoSourcesOntoOneCatalogEntry_ReportsCollision`.
- **Dependencies:** before WP-SIMPL-28 PR 3, before #674 (catalog-scale cluster); independent of #459 (WP-SIMPL-44 PR 4) but both touch the parser (rebase).
- **Risks:** two logical sources mapping to one physical table collide (test above; second one rejected with a reason code). Existing grants on old logical entries stay attached to the old entry (Q-CAT-2).
- **Owner questions:** Q-CAT-1, Q-CAT-2.
- **Size:** S, ~70 src / ~250 test lines, 1 PR.
- **Delivery:** UNDER REVIEW (draft PR #748), see [`2026-11-14-plan-issue-666-dbt-physical-name.md`](2026-11-14-plan-issue-666-dbt-physical-name.md). Deviations: the catalog entry records the dbt unique id (governance step 0012, internal column) for SEC-OI-666-1; an entry without a recorded id is taken over only by a node without alias or identifier (owner decision 2026-11-14, re-approval requested); 134 net src lines instead of ~70.

## #667 consents/apply: endpoint requires GovernanceAdmin, service requires GOVERNANCE_ADMIN

- **Verdict:** NEW PLAN (authorization bug; security fix, no freeze approval needed).
- **Verified state:** endpoint `src/Autheris.Api/Endpoints/GovernanceEndpoints.Catalog.cs:72` checks `GatewayPolicies.HasAnyRole(GovernanceAdmin, ClusterAdmin)` (canonical, hierarchy-aware). Service `src/Autheris.Application/Governance/Services/DeclarativeConsentApplyService.cs:51-57` checks raw `IsInRole("GOVERNANCE_ADMIN"|"CLUSTER_ADMIN"|"DATA_GOVERNANCE_REVIEWER")` or a `scope` claim that merely *contains* `governance:admin` (substring match, `:54`), and throws `SecurityException`, which the exception pipeline maps to `SQL_POLICY_VIOLATION`. `DATA_GOVERNANCE_REVIEWER` is a classification-workflow label (`GovernanceClassificationEndpoints.cs:299`), never a token role. The only caller is the endpoint (two routes, `GovernanceEndpoints.cs:39, 42`).
- **Root cause:** pre-canonical role strings left in the service after WP-SIMPL-43 canonicalised roles.
- **Design:** the service takes `IGatewayRoleEvaluator` (`src/Autheris.Application/Common/Interfaces/IGatewayRoleEvaluator.cs`) and requires `HasAnyRole(caller, [GovernanceAdmin, ClusterAdmin])` - same predicate as the endpoint (defense in depth, one spelling). Raw strings and the `scope` substring branch are deleted (Q-CAT-3). The service throws a typed `ConsentApplyForbiddenException`; the endpoint maps it to 403 `ACCESS_DENIED` via `ApiProblems.Forbidden`. Tenant: a tenant-bound GovernanceAdmin (OD-9) applies only in its own tenant (`caller.GetTenantId()`, unchanged); only canonical ClusterAdmin is global.
- **Work packages:** WP-OI-CAT-2, one PR, ~30 src lines.
- **Tests:** `tests/Autheris.Tests.Unit/Governance/DeclarativeConsentApplyAuthorizationTests.cs` `[Trait("Category","Security")]`: `Apply_CanonicalGovernanceAdmin_IsAccepted`, `Apply_CanonicalClusterAdmin_IsAccepted`, `Apply_LegacyUpperCaseRoleOnly_IsRejected`, `Apply_DataGovernanceReviewerString_IsRejected`, `Apply_ScopeContainingGovernanceAdminSubstring_IsRejected`, `Apply_ConsumerRole_IsRejected_WithAccessDenied_NotSqlPolicyViolation`, `Apply_TenantBoundGovernanceAdmin_WritesOnlyCallerTenant`. Integration: `DeclarativeConsentAndAccessIntrospectionTests.Apply_GovernanceAdminOnly_Returns200`.
- **Dependencies:** none; first in the consent chain.
- **Risks:** callers relying on `governance:admin` scope lose access (no production; Q-CAT-3).
- **Size:** S, ~30 src / ~150 test lines, 1 PR.

## #670 Consent replace/revoke ignores admin roles from the token; ROLE_MEMBERS has no API

- **Verdict:** NEW PLAN (authorization bug, no freeze approval needed). WP-SIMPL-47 PR 2 reroutes `RevokeConsentInTransactionAsync` through `IGrantWriter` but does not change who may revoke; this fix is a precondition, not a duplicate.
- **Verified state:** `src/Autheris.Infrastructure/Persistence/GovernanceRepository.ConsentGrants.cs:261-273` re-checks inside the transaction: grantee self, else `IsAuthorizedApproverForTableInternalAsync` (`:306`), whose last branch reads `ROLES JOIN ROLE_MEMBERS` for `GovernanceAdmin`/`ClusterAdmin` (`:347-357`); failure throws `UnauthorizedAccessException` (500 on apply). Callers: GraphQL `Mutation.Consent.cs:933` (already checks token admin at `:905-930`, then the repository rejects), apply service `DeclarativeConsentApplyService.cs:424/430` via `ReplaceConsentsAsync` (`:90`, revoke at `:127`), decorator `AttributeValidatingConsentRepository.cs:60/76/106`. `ROLE_MEMBERS` writers: only seeds in `GovernanceRepository.Schema.cs:413, 616`; `GetTransitiveRolesAsync` (`IGovernanceRepository.cs:348`, `CatalogRows.cs:33, 557`) has no production caller. The approver check still carries the ITSM parameter `@itsmAccount` (OD-5 leftover).
- **Root cause:** the repository is the last authority but only knows database roles; token roles are canonical since WP-SIMPL-43 and roles are control-plane only (OD-30).
- **Design:**
  1. New `ConsentRevocationAuthority` (Application, record): `GranteeOrApproverOnly`, `TenantGovernanceAdmin(TenantId)`, `ClusterAdmin`; computed once by the caller from canonical token roles via `IGatewayRoleEvaluator` (OD-9: GovernanceAdmin tenant-bound, ClusterAdmin global).
  2. `RevokeConsentAsync` and `ReplaceConsentsAsync` take the authority; inside the transaction the repository allows revoke when grantee self, or authority covers the consent's tenant as re-read in the transaction (`ReadRevokeTargetAsync`), or the owner/delegate check passes. The `ROLES`/`ROLE_MEMBERS` branch and dead `GetTransitiveRolesAsync` are deleted (Q-CAT-4); no role-member API.
  3. Denial throws `ConsentRevocationForbiddenException` -> 403 `ACCESS_DENIED` (REST) / `FORBIDDEN` (GraphQL); never 500. Cross-tenant consent ids keep the existing uniform not-found.
  4. The `@itsmAccount` leftover is removed here or by WP-SIMPL-47 PR 3, whichever merges first.
- **Work packages:** WP-OI-CAT-3, one PR, ~150 src lines (interface change through the decorator). After WP-OI-CAT-2; before WP-SIMPL-47 PR 2.
- **Tests:** `tests/Autheris.Tests.Integration/Governance/ConsentRevocationAuthorityTests.cs` (PostgreSQL and SQL Server contract) `[Trait("Category","Security")]`: `Revoke_TokenGovernanceAdmin_SameTenant_Succeeds`, `Revoke_TokenGovernanceAdmin_OtherTenantConsent_ReturnsNotFound_AndKeepsConsent`, `Revoke_TokenClusterAdmin_AnyTenant_Succeeds`, `Revoke_RoleMembersRowWithoutTokenRole_IsDenied`, `Revoke_ConsumerWithoutOwnership_IsDenied_403`, `Revoke_OwnerOrDelegate_Succeeds`, `Revoke_GranteeSelf_Succeeds`, `Replace_ViaApply_TokenGovernanceAdmin_Succeeds` (the #670 repro), `Replace_PartialDenial_RollsBackWholeManifest`. Unit: `ConsentRevocationAuthorityTests.FromPrincipal_TenantPrefixedGovernanceAdmin_IsTenantBound`. Existing `ConsentReplaceRepositoryTests` updated.
- **Dependencies:** WP-OI-CAT-2; WP-SIMPL-47 PR 2/3 rebase on it; ROLES/ROLE_MEMBERS table drop belongs to the WP-SIMPL-52 drop step once seeds are removed.
- **Risks:** an authority computed by the caller widens the trust boundary of the repository; mitigated because the tenant is re-read inside the transaction and the authority type cannot be built from a request body (constructed only by `ConsentRevocationAuthority.FromPrincipal`).
- **Size:** M, ~150 src / ~350 test lines, 1 PR.

## #671 consents/apply cannot prune grants of tables no longer listed; no REST revoke

- **Verdict:** PARTLY COVERED. REST revoke: WP-SIMPL-47 step 10 / PR 5 (`POST /api/v1/consent/grants/{id}/revoke`, owner/delegate/GovernanceAdmin or deciding external system, `Idempotency-Key`), not started; no separate `DELETE /api/v1/governance/consents/{id}` (Q-CAT-5). Prune: gap, planned as a WP-SIMPL-47 addendum (WP-OI-CAT-4).
- **Verified state:** `DeclarativeConsentApplyService.cs:218-222` loads existing consents per (table, grantee) in the manifest only; `revoked` (`:276`) counts only those. Consents carry no origin marker (`ConsentModels.cs`); WP-SIMPL-47 PR 1 adds `grant_source` with value `DECLARATIVE` (spec `:62-63`).
- **Design:** `POST /api/v1/governance/consents/apply?prune=true` (with `dryRun=true` support): for each grantee named in the manifest, active consents in the caller tenant with `grant_source = DECLARATIVE` on tables not listed for that grantee are revoked in the same transaction as the replace (`ReplaceConsentsAsync` revoke list, reason `PRUNED_BY_MANIFEST`), audited per revoked consent. Never touches grants from consent requests, profiles, owners, bulk or access plans, nor other grantees or tenants. Dry run lists `prunedConsentIds` with table ids only. Authorization as WP-OI-CAT-2/3. Prune count above `Gateway:Governance:DeclarativeApply:MaxPrunePerRun` is not added; instead a manifest whose prune set exceeds 50 % of the grantee's declarative grants requires `prune=force` (Q-CAT-6).
- **Work packages:** WP-OI-CAT-4, one PR, ~180 src lines; after WP-SIMPL-47 PR 1 (grant_source) and PR 2 (revocations through `IGrantWriter`); freeze: inside WP-SIMPL-47 pre-approved scope if the owner confirms Q-CAT-5.
- **Tests:** `tests/Autheris.Tests.Unit/Governance/DeclarativeConsentPruneTests.cs`: `Prune_RevokesDeclarativeGrantOfUnlistedTable`, `Prune_DryRun_ListsWithoutRevoking`, `Prune_False_KeepsUnlistedGrants` (default unchanged), `Prune_ThresholdExceeded_RequiresForce`. `[Trait("Category","Security")]`: `Prune_NeverRevokesConsentRequestOrOwnerOrProfileGrants`, `Prune_NeverRevokesGrantsOfUnnamedGrantees`, `Prune_TenantBoundAdmin_NeverRevokesOtherTenantGrants`, `Prune_FailureMidway_RollsBackAll_NoPartialRevocation`, `Prune_EachRevocationAudited_WithGrantRecord`.
- **Dependencies:** WP-OI-CAT-2, WP-OI-CAT-3, WP-SIMPL-47 PR 1-2 (and PR 5 for REST revoke).
- **Risks:** a truncated manifest revokes broadly (threshold and dry run mitigate).
- **Size:** M, ~180 src / ~300 test lines, 1 PR.

## #680 SqlEndpoints.AutoSyncFromDbt should follow SqlEndpoints.Enabled; report a failure once per run

- **Verdict:** NEW PLAN (bug, no freeze approval needed; no option added).
- **Verified state:** `AutoSyncFromDbt` defaults to `true` (`src/Autheris.Domain/Options/Protocols/RestApiOptions.cs:253`); `DbtMetadataIngestionService.cs:744` checks only `AutoSyncFromDbt`, catches per model and logs a warning with exception plus one sync warning per model (`:758-762`); `SqlEndpointLoader.SyncDbtModelToFile` creates the directory per call (`:412`). `SqlEndpoints.Enabled` is honoured only at mapping time (`GatewayApplicationBuilderExtensions.cs:316`).
- **Design:** effective auto-sync = `SqlEndpoints.Enabled && SqlEndpoints.AutoSyncFromDbt`, evaluated once per run. The directory is prepared once per run; the first `IOException`/`UnauthorizedAccessException` stops auto-sync for the rest of the run and yields one warning `SQL_ENDPOINT_AUTOSYNC_FAILED` with the number of skipped models and one log entry (no stack trace per model). Per-model validation rejections (identifier rules, SEC H-20) stay per model but are aggregated into one counted warning.
- **Work packages:** WP-OI-CAT-5, one PR, ~40 src lines; before or inside WP-SIMPL-28 PR 3.
- **Tests:** `tests/Autheris.Extensions.Tests/DbtSqlEndpointAutoSyncTests.cs`: `AutoSync_SqlEndpointsDisabled_WritesNoFile`, `AutoSync_ReadOnlyDirectory_ReportsOneWarningPerRun`, `AutoSync_ReadOnlyDirectory_LogsOnce`, `AutoSync_Enabled_WritesFilePerModel`, `AutoSync_DryRun_WritesNothing`.
- **Dependencies:** none; touches the same method as WP-OI-CAT-1 step 4 (merge either order, rebase).
- **Risks:** none material.
- **Owner questions:** Q-CAT-7 (default of `AutoSyncFromDbt`).
- **Size:** S, ~40 src / ~150 test lines, 1 PR.

## #610 Governance: add ordinal column to TABLE_COLUMNS (WP-SIMPL-11 follow-up)

- **Verdict:** NEW PLAN (bug: unstable column order; no freeze approval needed).
- **Verified state:** PostgreSQL reads order by `c.ctid` (`src/Autheris.Infrastructure/Persistence/GovernanceRepository.CatalogSql.cs:63, 81`, comment names this follow-up); the SQL Server variants have no `ORDER BY` at all (`SelectTableColumnsSqlSs`, `:65-71`, and the batch variant), so SQL Server order is undefined too (gap beyond the issue). Inserts: `GovernanceRepository.Schema.cs:235, 323, 558` and `CatalogSql.cs:160`. `TABLE_COLUMNS.id` is a GUID/NVARCHAR key (`SqlServerBaselineSchema.cs:44-47`).
- **Design:** forward-only step `00NN_table_column_ordinal` per engine: `ALTER TABLE TABLE_COLUMNS ADD ordinal INT NULL`; backfill PostgreSQL by `ROW_NUMBER() OVER (PARTITION BY table_id ORDER BY ctid)`, SQL Server by `ORDER BY column_name` (no better order exists); then `NOT NULL` with default 0. Every insert path writes `ordinal = max(ordinal)+1` per table inside the same transaction, or the declared position from dbt/ingestion when supplied. Reads on both engines `ORDER BY c.ordinal, c.column_name`. Shape files and step-parity test updated.
- **Work packages:** WP-OI-CAT-6, one PR, ~60 src lines + 2 step files; serial with other DDL PRs; before #673.
- **Tests:** `tests/Autheris.Tests.Integration/Persistence/TableColumnOrdinalContractTests.cs` (PostgreSQL and SQL Server): `Columns_ReturnedInRegistrationOrder_AfterUpdateAndVacuumFull`, `Columns_ReturnedInDeclaredOrder_FromDbtSync`, `NewColumn_AppendedAfterExisting`, `Migration_BackfillsOrdinal_ForExistingRows`, `BatchRead_OrdersByTableThenOrdinal`; existing `MigrationShapeTests`/step-parity tests extended.
- **Dependencies:** WP-SIMPL-11/20 merged (done); serial with WP-SIMPL-47 PR 1 and WP-SIMPL-52 DDL; before #673.
- **Risks:** concurrent column inserts computing the same `max+1` (both rows kept; order tie broken by name; no uniqueness constraint).
- **Size:** S, ~60 src / ~150 test lines, 1 PR.

## #612 Federation: align explain output with member eligibility pushdown rule (WP-SIMPL-36 SP1b OQ-2)

- **Verdict:** NEW PLAN (correctness of a diagnostic; no freeze approval needed). Not covered by WP-SIMPL-46 (its explain is access explain, not query-plan explain).
- **Verified state:** execution partitions with the eligibility predicate and the configured maximum (`src/Autheris.Application/Sql/Services/CrossSourcePlanner.cs:266-268`, `SubtreeCompiler.IsPushdownEligible` `SubtreeCompiler.cs:83`). Explain calls `SubtreeGraphPartitioner.PartitionSubtrees(parsedStatement, resolvedTables)` with neither predicate nor `MaxTablesPerSubtree` (`src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Sql.cs:357`), so it can show subtrees that execution stages table by table. Explain already computes per-column access levels for the analysed principal (`QueryAnalysisService.Evaluation.cs:105-137`) and builds a `TableAccessDecision` per SQL table (`QueryAnalysisService.Sql.cs:225-235`, Mask/Clear only).
- **Design:** explain passes `options.MaxTablesPerSubtree` and the predicate `t => SubtreeCompiler.IsPushdownEligible(t, decisionFor(t))`, where `decisionFor` is the per-table decision explain already built, extended so that Deny and missing decisions map to "not eligible" (C-1 defaults). Tables that fail the predicate move to the existing single-table staging list; `AnalyzedSubtree` keeps its current fields, no new field and no reason text (C-6). The co-location section (`:341`) is unchanged.
- **Work packages:** WP-OI-CAT-7, one PR, ~40 src lines.
- **Tests:** `tests/Autheris.Tests.Unit/Federation/ExplainSubtreeEligibilityTests.cs`: `Explain_MaskedMember_NotShownInSubtree`, `Explain_AllClearMembers_ShownAsSubtree`, `Explain_UsesConfiguredMaxTablesPerSubtree`, `Explain_SubtreeMembers_EqualCrossSourcePlannerPartition_ForSameDecisions` (parity). `[Trait("Category","Security")]`: `Explain_IneligibleMember_ExposesNoColumnOrReasonDetail` (C-6: serialized explain contains no field beyond today's shape), `Explain_MissingDecision_TreatedAsIneligible`.
- **Dependencies:** WP-SIMPL-36 sp1b merged; if WP-SIMPL-46 later moves analysis onto `IAccessDecisionPoint`, the predicate input switches with it (no plan change).
- **Risks:** explain decisions are computed by the analysis path, not the decision point; the parity test pins equality for identical decisions only.
- **Size:** S, ~40 src / ~150 test lines, 1 PR.

## #459 Declared keys and indexes only: remove physical key/index reads, read meta.indexes from dbt

- **Verdict:** COVERED by [WP-SIMPL-44](simplification/WP-SIMPL-44-no-source-metadata-reads.md) PR 4 (step 11 physical key/index chain removal, step 12 dbt `meta.indexes`; "Closes #459"). No new plan.
- **Verified state on main:** PR 1 (guard, #619) and PR 2 (declared catalog, column readers deleted, #645) are merged; PR 3 (procedures, `EnsureUniqueKeyAsync` still in `src`) and PR 4 are not started. The chain the issue lists is still present: `IPhysicalKeyCatalog` (8 src files), `SqlServerKeyIndexMetadataReader`, `PostgreSqlKeyIndexMetadataReader`, `PhysicalKeyDirectionReconciler`, `PhysicalKeyRefreshService`, `PhysicalIndexSync`, `CatalogPhysicalIndexStore`, `IPhysicalIndexRepository`; the WP-44 architecture test allowlist still lists them.
- **Gap:** none in scope. Ordering notes only: PR 4 must merge before WP-SIMPL-28 moves dbt into a module, and before #673/#674 (catalog-scale cluster) rework the dbt write path, so the batch upsert never carries the physical-index branch; #666 (WP-OI-CAT-1) and PR 4 both edit `DbtMetadataIngestionService` (second to merge rebases).
- **Size / Phase 3:** per spec (+~200 / -~1,500 src, 1 PR); Phase 3 conditions of the spec (sec plan 11.2, row 44) apply unchanged.

## Open owner questions

| Id | Question | Recommended default |
|---|---|---|
| Q-CAT-1 | #666: keep dbt `name` visible when it differs from the physical name? | Yes: set `DisplayName = name` only when no display name is declared |
| Q-CAT-2 | #666: re-key existing catalog entries registered under the logical name? | No automatic re-key (not in production); sync warns `PHYSICAL_NAME_CHANGED`; operator deactivates old entries; grants are not moved |
| Q-CAT-3 | #667: keep apply rights for `DATA_GOVERNANCE_REVIEWER`, compliance roles or a `governance:admin` scope? | No: only canonical GovernanceAdmin (tenant-bound, OD-9) and ClusterAdmin |
| Q-CAT-4 | #670: add an API for `ROLE_MEMBERS` or drop the database-role branch? | Drop the branch and dead `GetTransitiveRolesAsync`; roles come from the token/SCIM only (OD-30, WP-SIMPL-43); tables dropped in the WP-SIMPL-52 drop step |
| Q-CAT-5 | #671: build prune under the freeze as part of the pre-approved WP-SIMPL-47 scope; separate `DELETE` revoke route? | Yes as WP-47 addendum after PR 2; no separate route: REST revoke is WP-47 PR 5 `POST /api/v1/consent/grants/{id}/revoke` |
| Q-CAT-6 | #671: guard against accidental mass prune? | Prune limited to `grant_source = DECLARATIVE`; > 50 % of a grantee's declarative grants needs `prune=force`; no new option |
| Q-CAT-7 | #680: change the default of `SqlEndpoints.AutoSyncFromDbt`? | Keep `true`, effective only when `SqlEndpoints.Enabled` |

## Phase 3 review scope

- WP-OI-CAT-2 and WP-OI-CAT-3 (one review): canonical role check, tenant re-read inside the transaction, authority construction not reachable from request input, 403 instead of 500, ROLE_MEMBERS branch removal.
- WP-OI-CAT-4: prune blast radius (grant source, grantee and tenant bounds, atomicity, audit, dry-run disclosure).
- WP-OI-CAT-1 (short): untrusted `identifier`/`alias` into catalog identity and generated SQL; collision handling.
- WP-OI-CAT-7 (short): condition C-6 of the sp1b review.
- Not needed: WP-OI-CAT-5, WP-OI-CAT-6.

## Owner decisions (2026-11-13)

The owner confirmed the recommended default of every open question (`Q-*`) in this file. The defaults in the question tables are binding for the implementation; a later owner decision wins. Phase 3 (security review) still applies before implementation starts.

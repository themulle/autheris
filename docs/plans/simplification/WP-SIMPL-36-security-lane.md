# WP-SIMPL-36 Security lane (freeze exception, parallel to Stage 0)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-36-security-lane-freeze-exception-parallel-to-stage-0) | Issues: #469, #475, #504, #505, #506, #507, #508 | Owner decisions: OD-3, OD-4, OD-5, OD-7, OD-8, OD-9, OD-10, OD-11, OD-12, OD-15 | Security conditions: SEC-SIMPL-01, 70, 80, 81, 84, 85, 86, 87, 88, 89, 92, 94

Disclosure rule (SEC-SIMPL-01): advisories are named only by GHSA id and a neutral component title. Details are in the private advisory. Commits, PR bodies, test names and code comments carry no exploit description. Developer-only root-cause notes are in a separate local file that is never committed.

## 1. Goal and outcome
Close all FIX-IN-CORE issues and advisories of the assessment in ordered, independently mergeable sub-packages, starting with the already pushed `fix/security-batch-5`, aligned to OD-9 before merge. Outcome: every sub-package has its Security-tagged tests green, #505-#508, #469, #475 and #504 are closed (or re-classified OBSOLETE-BY-REMOVAL with the deleting WP named), and no open FIX-IN-CORE advisory remains for KEEP code.

## 2. Preconditions and dependencies
- None for sub-package 0 (security exception of the freeze, SEC-SIMPL-70). The lane has merge priority in shared files over WP-SIMPL-12/18/20/28/29/30; those WPs rebase onto it, never the reverse (SEC-SIMPL-84, rule R3).
- Order is binding: 0, 1, 2, 3, 4, 5, 6, 7, 8. Sub-packages 3-8 touch disjoint areas and may run in parallel lanes after 1 and 2 are merged, but never two builds at once on one disk.
- Fixes in code that moves to a module land before or inside the extraction WP (R6): item 5 (dbt, OpenMetadata sync) before WP-SIMPL-28; item 6 (CDC, webhook residual) before WP-SIMPL-29; item 7 (auth cache) coordinates with WP-SIMPL-15 (Garnet removal).

## 3. Scope
### In scope
- Sub-package 0: align and merge `fix/security-batch-5` (origin head `504dd24b`, 39 files, +2367/-538 against main at plan time). It carries GHSA-335j-6vwc-7p4c, GHSA-qx32-576x-5cq2, GHSA-78j7-3gh6-7cqx (part 1), GHSA-xpw9-4m63-69pf, GHSA-95f9-ffc7-vw2c (partial), GHSA-jfrx-2mgc-pfh5, GHSA-4qx8-fv2p-q8r2 (anchor part) plus the earlier batch-5 items named in the plan assessment (GHSA-mgv5-4r69-mwww, GHSA-wvwx-q6jg-44wv, GHSA-cj7m-c75j-6p6h, GHSA-jcpx-6m27-2m97, GHSA-hw85-rqjh-cwxx, GHSA-wg8p-wfw5-qvgj, GHSA-547h-29x4-m4ch). The local working tree may hold further uncommitted batch-5 changes; the owner of that tree commits them to the branch first (this spec does not read them).
- Sub-package 1 (P0): #469 plus the P0 advisories GHSA-9229-wx6j-wvm9, GHSA-v5c2-29pv-cc86 (both on the OD-9 role model), GHSA-7ccv-hfgq-5qwm, GHSA-7pjw-hcrc-hvff (federation, OD-15).
- Sub-package 2: audit fail-closed bundle (#505 audit part, #507 audit part) with `RequiredAuditArchitectureTests`.
- Sub-package 3: DML bundle without Casbin (OD-3, SEC-SIMPL-80/81): GHSA-jcf8-hchm-55h3, GHSA-3prq-qxg7-92qf, GHSA-83g2-c2jr-c8vq, GHSA-v75c-28mx-7wm7, remainder of #505.
- Sub-package 4: #506, #507 (remainder), #508.
- Sub-package 5: GHSA-jw88-9j35-3c96 (dbt semantic import), GHSA-jfrx-2mgc-pfh5 OpenMetadata part, GHSA-95f9-ffc7-vw2c residual (OD-4), #475.
- Sub-package 6: GHSA-92vp-663x-pp52 (transport gate), GHSA-78j7-3gh6-7cqx part 2 (OD-7), GHSA-4qx8-fv2p-q8r2 export deletion (OD-8), GHSA-wvwx-q6jg-44wv residual.
- Sub-package 7: GHSA-x8g9-cm3h-p6jq (auth cache), GHSA-v77c-jh96-7rgr (persisted enum), GHSA-rxr4-mf28-p2hv (aggregation, OD-11), GHSA-66xj-4459-x8gx (row-filter evaluation, OD-12), GHSA-p234-g3ph-gqh2 (MCP document store).
- Sub-package 8: #504 security trait sweep.
### Out of scope
- Anything in code that a deletion WP removes (Backstage export part of GHSA-9229, vendor catalog sync, Flight-only part of GHSA-v75c: closed by WP-SIMPL-12/16). Re-verify at the start of each sub-package with `git grep`; if the code is gone, record "closed by WP-SIMPL-NN" in the PR and skip.
- New features, refactors, renames, Casbin (deleted in WP-SIMPL-14; never touched here).
- Changing the audit hash input fields (needs an ADR addendum, SEC-SIMPL-24).

## 4. Current state (verified at main 63cbb893)
- `EndpointSecurity.IsGlobalGovernanceAdmin` exists (`src/Autheris.Api/Endpoints/EndpointSecurity.cs:70`, 24 call sites on the batch-5 branch). Batch-5 treats a GovernanceAdmin without tenant claim as global (`ClaimTenantKind.Absent`); OD-9 forbids that. `IsCanonicalClusterAdmin` wraps `Autheris.Domain.Security.ClusterAdminPolicy` (`EndpointSecurity.cs:64`).
- `IAuditWriter.RecordRequiredAsync` exists (`src/Autheris.Application/Audit/AuditWriter.cs:39`); already used by `AuditRetentionPurgeWorker.cs:235`. Callers in the endpoint layer: `CatalogApiEndpoints.cs`, `GovernanceEndpoints.Access.cs`, `GovernanceEndpoints.Audit.cs`, `CallerAttributeAdminEndpoints.cs`, `SqlEndpointRoutes.cs`.
- Existing architecture test project: `tests/Autheris.Tests.Architecture` (Cecil via `CecilModuleCache.cs`, `AuditArchitectureTests.cs`, `AuditUsageRulesTests.cs`). 2024 occurrences of `Category","Security"` exist in tests.
- Files named by the sub-packages exist at the paths listed below (verified by `git ls-files`); `CdcSubscriptionGovernor.cs` is NOT under `Application/Events/Services/`: locate with `git grep -l "class CdcSubscriptionGovernor"` first.

## 5. Implementation steps
General rule for every sub-package: failing test first (names in section 6), minimal fix, tag all new tests `[Trait("Category","Security")]`, one `dotnet build`/test run at a time (batch builds, not per micro-step). Commit message form: `fix(security): resolve GHSA-xxxx-xxxx-xxxx (<neutral title>)`.

### Sub-package 0: batch 5 alignment and merge
1. In a worktree, check out `origin/fix/security-batch-5`, rebase onto current main (resolve conflicts in favour of the deletions already merged; the branch has merge priority over later WPs).
2. OD-9 alignment in `EndpointSecurity.cs`: change `IsGlobalGovernanceAdmin` to return true only for `IsCanonicalClusterAdmin`; add `IsTenantBoundGovernanceAdmin(principal)`. Review all 24 call sites: each that granted cross-tenant reach to a tenant-less GovernanceAdmin now (a) requires `IsCanonicalClusterAdmin` for global operations, or (b) scopes GovernanceAdmin to the caller tenant. A token without tenant claim resolves to the default tenant (via `TenantStamping`/`TenantClaimReader`) and is never global.
3. Keep endpoint-level role policies unchanged; only the scope derivation changes. Do not add a new role.
4. Run the batch-5 Security-trait suite plus the new OD-9 tests; run the Phase 5 review (`csharp-code-reviewer`) and fix findings by severity.
5. Merge. Record the merge commit in the tracker.

### Sub-package 1: #469 and P0 advisories
1. #469 `Sid.GetUserSid`/`GetAllUserSids` (`src/Autheris.Domain/Common/Sid.cs`): remove the app-identifier fallback (deny: return the anonymous/unresolved SID that every matcher rejects), keep app ids out of grant/consent matching. Replace the public static mutable `Sid.RealmLookup` by an injected lookup (constructor/DI), remove the test-only static setters.
2. GHSA-v5c2-29pv-cc86 (`CatalogApiEndpoints.RegisterDatasourceHandler`, `CatalogDiscoveryService`, `OpenApiIngestionService`): domain must equal caller tenant unless canonical ClusterAdmin; secret refs carry an owner tenant; registration never overwrites an existing ref.
3. GHSA-9229-wx6j-wvm9 (`CatalogApiEndpoints` datasource test and dataset state routes, `DatasourceTestingService`, `AccessPlanningService`): scope candidate tables to the caller tenant (same `restrictToTenant` as sibling handlers); dataset state change requires the same authorization and confirmation as the MCP tool, tenant check before write, 404 for a missing table.
4. GHSA-7pjw-hcrc-hvff (`CatalogFederatedMaskingCatalog.cs`): per-tenant load, key by table and column, strictest rule wins, invalidate on masking-rule change.
5. GHSA-7ccv-hfgq-5qwm (`SubtreeGraphPartitioner.cs`, `SubtreeCompiler.cs`, `CrossSourcePlanner.cs`, `FederatedStagingService.cs`, `GovernedConnectorReader.cs`): each member of a same-source subtree is authorized, denied columns excluded and masked against its own decision (SEC-SIMPL-94).

### Sub-package 2: audit fail-closed bundle
1. Replace best-effort audit writes on the security-decision and admin paths with `RecordRequiredAsync` (datasource test probe, datasource registration, dataset state change, DML, access-profile admin). The operation is refused when the audit write fails.
2. Add `tests/Autheris.Tests.Architecture/RequiredAuditArchitectureTests.cs`: Cecil scan of the affected endpoint and service types; baseline starts at the fixed state (no fire-and-forget audit calls on these types).

### Sub-package 3: DML bundle (no Casbin)
1. `GovernedSqlRewriter.cs`, `AstSecurityVisitor.TypedDml.cs`, `TableAccessPolicy.CanWriteTableAsync`: explicit per-table write grant on the core grant path for every DML target (also for writer roles); reject DML on tables that are row-policy dependencies; refuse DML on tables without tenant column unless explicitly exempted; with `AllowDml`, `RequireTenantColumn=true` and `MaxAffectedRows>0` are startup requirements (fail fast, SEC-SIMPL-31 style).
2. Write scope: DML requires an explicit write scope on every token type; absent scope means read-only.
3. Write-ahead audit (`RecordRequiredAsync`, statement fingerprint and table, no values, SEC-SIMPL-81); catalog write-protected column flag enforced in `SecureAssignments` and `ResolveInsertColumns`; per-principal write budget; read connections read-only when DML is disabled.
4. DML statement replay protection: single-use tickets and `Idempotency-Key` on the HTTP DML routes.

### Sub-package 4: #506, #507 remainder, #508
Each issue as listed in its GitHub body (verify line numbers before editing): global `Cache-Control: no-store` + `Vary` default with explicit opt-out, epoch in metadata cache keys, `DatasourceSecretRefPolicy` validation, generic DNS error, break-glass flow fail-closed, MCP `PersistedToolValidator` required and schema-recursive, prompt-argument validation, real catalog owner or none, empty visibility list without principal, multi-valued claims.

### Sub-package 5: dbt, OpenMetadata, search residual, #475
Merge semantic imports through the existing ratchets; skip external sync rows owned by another tenant; per-tier score handling for restricted tables; parameterised, typed cache mutations with failure invalidation.

### Sub-package 6: transport gate, ReBAC switch, WORM, webhooks
OD-7: one switch, fail-closed startup check, old keys fail fast; OD-8: delete `AuditWormExportService` and fail startup when `Audit:Worm:Enabled` is set; transport gate mandatory with `DenyAllTransportGate` fallback; webhook residual per advisory.

### Sub-package 7: cache, enums, aggregation, row filter, doc store
Per advisory fix lists (see GitHub issue bodies / private advisories); OD-11 floor for suppression threshold, OD-12 per-dialect proof (PostgreSQL, SQL Server, Oracle, Databricks).

### Sub-package 8: #504
Add `[Trait("Category","Security")]` to the tests listed in #504 and add the missing ones; script in section 7 proves no security test is untagged.

## 6. Tests (all `[Trait("Category","Security")]`, project `tests/Autheris.Tests.Unit` unless noted)
- Sub-package 0: `Governance/Batch5OD9ScopeTests`: `GovernanceAdmin_without_tenant_claim_is_not_global`, `GovernanceAdmin_with_tenant_is_tenant_bound`, `Canonical_ClusterAdmin_is_global`, `Token_without_tenant_claim_uses_default_tenant`; existing batch-5 tests stay green.
- Sub-package 1: `Identity/SidFallbackTests` (`GetUserSid_without_sub_or_oid_does_not_use_client_id`, `GetAllUserSids_excludes_app_identifiers`, `RealmLookup_is_injected_not_static`); `Catalog/DatasourceRegistrationTenantTests` (`Register_other_tenant_domain_is_forbidden`, `Register_existing_secret_ref_is_not_overwritten`, `Register_other_tenant_domain_allowed_for_ClusterAdmin`); `Catalog/CrossTenantAdminOperationsTests` (`Datasource_test_other_tenant_domain_is_not_found`, `Dataset_state_change_other_tenant_is_forbidden`, `Dataset_state_change_missing_table_returns_404`); `Federation/FederatedMaskingTenantIsolationTests` (`Masking_catalog_loads_only_caller_tenant`, `Conflicting_rules_resolve_to_strictest`); `Sql/SubtreeMemberAccessTests` (`Same_source_subtree_member_denied_column_is_excluded`, `Same_source_subtree_member_masked_column_is_masked`, `Same_name_columns_use_own_member_decision`).
- Sub-package 2: `tests/Autheris.Tests.Architecture/RequiredAuditArchitectureTests.Security_decision_paths_use_RecordRequiredAsync`; `Catalog/DatasourceTestAuditTests.Probe_is_refused_when_audit_write_fails`.
- Sub-package 3: `Sql/DmlAuthorizationTests`: `Dml_WriterRole_WithoutExplicitWriteGrant_IsDenied`, `Dml_OnPolicyDependencyTable_IsRejected`, `Dml_TableWithoutTenantColumn_IsRefused`, `Dml_TokenWithoutWriteScope_IsRejected`, `Dml_AuditFailureBeforeExecute_DoesNotExecute`, `Dml_AssignToWriteProtectedColumn_IsRejected`, `Select_RunsReadOnly_WhenDmlDisabled`, `Dml_MaxAffectedRowsZero_FailsStartup`, `Dml_FlightTicket_IsSingleUse`, `Dml_IdempotencyKey_ReplayDoesNotReexecute`; Casbin-independence: `Dml_WriteGrant_Check_Does_Not_Reference_Casbin` (Architecture).
- Sub-package 4-7: one test class per issue/advisory named `<Component>SecurityTests`, with at least one fail-closed test per bullet of the issue/advisory fix list; mandatory names: `DeleteProfile_Retry_After_EpochBump_Failure_Still_Invalidates`, `Federation_Masker_Missing_Fails_Startup_When_Masking_Enabled`, `Unknown_AssignmentKind_Throws`, `Epoch_Regression_Flushes_L1`, `Dbt_Semantic_Import_Preserves_Governance_Settings`, `Cdc_Admission_Without_TransportGate_Denies`, `Worm_Enabled_Fails_Startup`, `Rebac_Tuples_Present_Enforced_On_All_Paths`, `Aggregation_Where_On_Non_Group_Column_Is_Treated_As_Isolating`, `RowFilter_Evaluated_Before_User_Predicate_PerDialect` (Parity project, tagged), `Cache_Mutation_Uses_Typed_Parameters`, `Cache_Mutation_Failure_Invalidates_Table`.
- Sub-package 8: `SecurityTraitCoverageTests.Listed_regression_tests_carry_security_trait` (Architecture, reflection over the #504 list).
- Delete/move: none; test files grow only.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror` (one build at a time).
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --filter "Category=Security"`; `dotnet test tests/Autheris.Tests.Architecture -c Release`; `dotnet test tests/TrinoSqlEngine.Tests -c Release --filter "Category=Security"` for sub-package 3 and 7.
- `git grep -n "IsGlobalGovernanceAdmin" -- src` after sub-package 0: every remaining call site justified in the PR description.
- `git grep -n "GHSA-" -- src tests docs | grep -iv "GHSA-[a-z0-9-]*:" ` is reviewed manually: only ids and neutral titles, no exploit text.
- `df -h /work` before each build; remove `bin/obj` after push.

## 8. Acceptance criteria
- [ ] Batch 5 merged, OD-9 tests green, no tenant-less GovernanceAdmin is global.
- [ ] #469, #475, #504, #505, #506, #507, #508 closed or re-classified with the deleting WP named.
- [ ] Every advisory in section 3 has a Security-tagged regression test in the merged PR.
- [ ] `RequiredAuditArchitectureTests` green with a non-empty scanned set.
- [ ] DML path contains no Casbin reference and denies without explicit write grant (OD-3).
- [ ] No exploit detail in any commit, PR body, test name, comment or doc (SEC-SIMPL-01).
- [ ] Phase 5 review clean per sub-package; build warning-free.

## 9. PR slicing (<= 400 net added src lines per PR unless `size-exception` is granted)
- PR 0: batch 5 as is plus OD-9 alignment (`size-exception` expected, +2367 existing; the alignment commit is separate and small).
- PR 1a #469; 1b GHSA-v5c2 + GHSA-9229; 1c GHSA-7pjw; 1d GHSA-7ccv.
- PR 2 audit bundle. PR 3a DML authorization and scope; 3b DML audit, protected columns, budget, read-only reads; 3c replay protection.
- PR 4a #506; 4b #507; 4c #508. PR 5a dbt; 5b external sync; 5c search; 5d #475.
- PR 6a transport gate; 6b ReBAC switch; 6c WORM deletion; 6d webhooks. PR 7a-7e one per advisory. PR 8 trait sweep.

## 10. Risks, rollback, do-nots
- Risk: overlap with deletion WPs; mitigate by rebasing them onto the lane. Rollback: revert the single PR; each is independent from 2 onward.
- Do NOT write advisory details anywhere public; do not weaken any existing control; do not delegate a check to Casbin; do not skip the failing-test-first step; do not use `--no-verify`.

## 11. Estimate
About +2500 src and +3500 tests LOC across all PRs excluding batch 5; 8-12 person-days (batch 5 review and merge 1-2 pd, P0 items 2-3 pd, DML 2 pd, rest 3-5 pd).

## Decisions taken in this spec
- Sub-packages 4-7 refer to the issue bodies and private advisories for per-bullet fix lists instead of repeating them publicly.
- Idempotency for DML: single-use ticket plus `Idempotency-Key` header (advisory proposal), not removal of DML over Flight (Flight-only part may be closed by WP-SIMPL-16).

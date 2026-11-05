# WP-SIMPL-36 Security lane (freeze exception, parallel to Stage 0)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-36-security-lane-freeze-exception-parallel-to-stage-0) | Issues: #469, #475, #504, #506, #507, #508 (#505: audit part here, remainder in [WP-SIMPL-45](WP-SIMPL-45-governed-write-access.md)) | Owner decisions: OD-3, OD-4, OD-5, OD-7 (replaced by OD-30), OD-8, OD-9, OD-10, OD-11, OD-12, OD-15, OD-26, OD-30 | Security conditions: SEC-SIMPL-01, 70, 80, 81, 82, 84, 86, 87, 88, 89, 92, 94

> **Changed by OD-30 and OD-26 (2026-11-05):** the former sub-package 6b (ReBAC enforcement switch) is dropped; ReBAC is removed by [WP-SIMPL-46](WP-SIMPL-46-unified-authorization-model.md) PR 5 and GHSA-78j7-3gh6-7cqx needs no further work here. The write-grant design of the DML bundle belongs to WP-SIMPL-45 (D-45-8); sub-package 3 below only implements the DML advisories. Sections 3-11 already reflect both changes.

Disclosure rule (SEC-SIMPL-01): advisories are named only by GHSA id and the neutral title of [advisories-overview.md](advisories-overview.md). Commits, PR bodies, test names and code comments carry no exploit description. Root-cause and fix notes are in each private advisory, section "Implementation notes (WP-SIMPL-36)".

## 1. Goal and outcome
Close all FIX-IN-CORE issues and advisories of the assessment in ordered, independently mergeable sub-packages, starting with the already pushed `fix/security-batch-5`, aligned to OD-9 before merge. Outcome: every sub-package has its Security-tagged tests green, #469, #475, #504, #506, #507, #508 and the audit part of #505 are closed (or re-classified OBSOLETE-BY-REMOVAL with the deleting WP named), and no open FIX-IN-CORE advisory remains for KEEP code.

## 2. Preconditions and dependencies
- None for sub-package 0 (security exception of the freeze, SEC-SIMPL-70). The lane has merge priority in shared files over WP-SIMPL-12/18/20/28/29/30; those WPs rebase onto it, never the reverse (SEC-SIMPL-84, rule R3).
- Order is binding: 0, 1, 2, 3, 4, 5, 6, 7, 8. Sub-packages 3-8 touch disjoint areas and may run in parallel lanes after 1 and 2 are merged, but never two builds at once on one disk.
- Fixes in code that moves to a module land before or inside the extraction WP (R6): item 5 before WP-SIMPL-28; item 6 (code that WP-SIMPL-29 moves) before WP-SIMPL-29; item 7 (authorization caches) coordinates with WP-SIMPL-15 (Garnet removal).
- Sub-package 3 and WP-SIMPL-45: WP-SIMPL-45 owns the write-grant model (OD-26, OD-33, OD-35). If WP-SIMPL-45 PR 1 starts before sub-package 3 is merged, it carries the same advisory fixes under the test names of section 6 (no duplicate implementation).
- Single owner of advisories shared with other WPs: GHSA-p234-g3ph-gqh2 (docs ingestion store) and GHSA-x8g9-cm3h-p6jq (authorization caches) are owned here (sub-package 7). [WP-SIMPL-40](WP-SIMPL-40-docs-ingestion-clean.md) and [WP-SIMPL-43](WP-SIMPL-43-authentication-authorization-core.md) only reference them and keep their regression tests green. GHSA-92vp-663x-pp52 (transport gate) is split: sub-package 6 here, procedures part in [WP-SIMPL-39](WP-SIMPL-39-procedures-clean.md) PR 1; both add cases to the same regression test.

## 3. Scope
### In scope
- Sub-package 0: align and merge `fix/security-batch-5` (origin head `504dd24b`, 39 files, +2367/-538 against main at plan time). It carries GHSA-335j-6vwc-7p4c, GHSA-qx32-576x-5cq2, GHSA-78j7-3gh6-7cqx, GHSA-xpw9-4m63-69pf, GHSA-95f9-ffc7-vw2c, GHSA-jfrx-2mgc-pfh5, GHSA-4qx8-fv2p-q8r2 plus the earlier batch-5 items named in the plan assessment (GHSA-mgv5-4r69-mwww, GHSA-wvwx-q6jg-44wv, GHSA-cj7m-c75j-6p6h, GHSA-jcpx-6m27-2m97, GHSA-hw85-rqjh-cwxx, GHSA-wg8p-wfw5-qvgj, GHSA-547h-29x4-m4ch). The local working tree may hold further uncommitted batch-5 changes; the owner of that tree commits them to the branch first (this spec does not read them).
- Sub-package 1 (P0): #469 plus GHSA-9229-wx6j-wvm9 (admin catalog operations) and GHSA-v5c2-29pv-cc86 (data source registration), both on the OD-9 role model, and GHSA-7ccv-hfgq-5qwm (federation column access checks) and GHSA-7pjw-hcrc-hvff (federation masking catalog), both under OD-15.
- Sub-package 2: audit fail-closed bundle (#505 audit part, #507 audit part) with `RequiredAuditArchitectureTests`.
- Sub-package 3: DML advisories without Casbin (OD-3, SEC-SIMPL-80/81): GHSA-jcf8-hchm-55h3 (WebSQL DML authorization), GHSA-3prq-qxg7-92qf (WebSQL DML scoping), GHSA-83g2-c2jr-c8vq (WebSQL DML token scopes), GHSA-v75c-28mx-7wm7 (WebSQL DML request handling).
- Sub-package 4: #506, #507 (remainder), #508.
- Sub-package 5: GHSA-jw88-9j35-3c96 (dbt semantic manifest import), catalog search security tests (OD-4, SEC-SIMPL-82), #475.
- Sub-package 6: GHSA-92vp-663x-pp52 (transport gate), OD-8 audit archive export deletion (SEC-SIMPL-86).
- Sub-package 7: GHSA-x8g9-cm3h-p6jq (authorization caches), GHSA-v77c-jh96-7rgr (access profile persistence), GHSA-rxr4-mf28-p2hv (governed aggregation, OD-11), GHSA-66xj-4459-x8gx (WebSQL row-filter evaluation, OD-12 per-dialect proof), GHSA-p234-g3ph-gqh2 (docs ingestion store).
- Sub-package 8: #504 security trait sweep.
- Closed on `main` per [advisories-overview.md](advisories-overview.md) (re-verify at the start of the sub-package, add the regression test of section 6 if it is missing, otherwise no work): GHSA-95f9-ffc7-vw2c (catalog search) and GHSA-jfrx-2mgc-pfh5 (external catalog sync) in sub-package 5; GHSA-4qx8-fv2p-q8r2 (audit archive) and GHSA-wvwx-q6jg-44wv (CDC webhooks) in sub-package 6.
### Out of scope
- Advisory parts in code that a deletion WP removes (WP-SIMPL-12/16): re-verify at the start of each sub-package with `git grep`; if the code is gone, record "closed by WP-SIMPL-NN" in the PR and skip.
- The write-grant model, write-protected columns, write budget and write connection (WP-SIMPL-45, D-45-8). ReBAC (removed by WP-SIMPL-46 PR 5, OD-30).
- New features, refactors, renames, Casbin (deleted in WP-SIMPL-14 after WP-SIMPL-46; never touched here).
- Changing the audit hash input fields (needs an ADR addendum, SEC-SIMPL-24).

## 4. Current state (verified at main 63cbb893)
- `EndpointSecurity.IsGlobalGovernanceAdmin` exists (`src/Autheris.Api/Endpoints/EndpointSecurity.cs:70`, 24 call sites on the batch-5 branch). Its scope derivation on the batch-5 branch does not yet follow OD-9 (global reach only for the canonical `ClusterAdmin`). `IsCanonicalClusterAdmin` wraps `Autheris.Domain.Security.ClusterAdminPolicy` (`EndpointSecurity.cs:64`).
- `IAuditWriter.RecordRequiredAsync` exists (`src/Autheris.Application/Audit/AuditWriter.cs:39`); already used by `AuditRetentionPurgeWorker.cs:235`. Callers in the endpoint layer: `CatalogApiEndpoints.cs`, `GovernanceEndpoints.Access.cs`, `GovernanceEndpoints.Audit.cs`, `CallerAttributeAdminEndpoints.cs`, `SqlEndpointRoutes.cs`.
- Existing architecture test project: `tests/Autheris.Tests.Architecture` (Cecil via `CecilModuleCache.cs`, `AuditArchitectureTests.cs`, `AuditUsageRulesTests.cs`). 2024 occurrences of `Category","Security"` exist in tests.
- Files touched per advisory are listed in the private advisory; verify them with `git ls-files` before editing.

## 5. Implementation steps
General rule for every sub-package: failing test first (names in section 6), minimal fix, tag all new tests `[Trait("Category","Security")]`, one `dotnet build`/test run at a time (batch builds, not per micro-step). Commit message form: `fix(security): resolve GHSA-xxxx-xxxx-xxxx (<neutral title>)`. For every advisory step below the fix list is the private advisory, section "Implementation notes (WP-SIMPL-36)"; the spec text names only the id and the neutral title.

### Sub-package 0: batch 5 alignment and merge
1. In a worktree, check out `origin/fix/security-batch-5`, rebase onto current main (resolve conflicts in favour of the deletions already merged; the branch has merge priority over later WPs).
2. OD-9 alignment in `EndpointSecurity.cs`: change `IsGlobalGovernanceAdmin` to return true only for `IsCanonicalClusterAdmin`; add `IsTenantBoundGovernanceAdmin(principal)`. Review all 24 call sites: each that granted cross-tenant reach to a tenant-less GovernanceAdmin now (a) requires `IsCanonicalClusterAdmin` for global operations, or (b) scopes GovernanceAdmin to the caller tenant. A token without tenant claim resolves to the default tenant (via `TenantStamping`/`TenantClaimReader`) and is never global.
3. Keep endpoint-level role policies unchanged; only the scope derivation changes. Do not add a new role.
4. SEC-SIMPL-01 on the branch: rename every batch-5 test method whose name describes a defect to the neutral form `AdvisoryRegressionTests.Ghsa_<first4>_<last4>_regression` (section 6) and remove advisory detail from comments and commit messages of the branch before merge.
5. Run the batch-5 Security-trait suite plus the new OD-9 tests; run the Phase 5 review (`csharp-code-reviewer`) and fix findings by severity.
6. Merge. Record the merge commit in the tracker.

### Sub-package 1: #469 and P0 advisories
1. #469 `Sid.GetUserSid`/`GetAllUserSids` (`src/Autheris.Domain/Common/Sid.cs`): remove the app-identifier fallback (deny: return the anonymous/unresolved SID that every matcher rejects), keep app ids out of grant/consent matching. Replace the public static mutable `Sid.RealmLookup` by an injected lookup (constructor/DI), remove the test-only static setters.
2. GHSA-v5c2-29pv-cc86 (data source registration): implement the private advisory's fix list on the OD-9 role model.
3. GHSA-9229-wx6j-wvm9 (admin catalog operations): implement the private advisory's fix list on the OD-9 role model.
4. GHSA-7pjw-hcrc-hvff (federation masking catalog): implement the private advisory's fix list.
5. GHSA-7ccv-hfgq-5qwm (federation column access checks): implement the private advisory's fix list; the result must satisfy SEC-SIMPL-94 (OD-15).

### Sub-package 2: audit fail-closed bundle
1. Replace best-effort audit writes on the security-decision and admin paths with `RecordRequiredAsync` (datasource test probe, datasource registration, dataset state change, DML, access-profile admin). The operation is refused when the audit write fails.
2. Add `tests/Autheris.Tests.Architecture/RequiredAuditArchitectureTests.cs`: Cecil scan of the affected endpoint and service types; baseline starts at the fixed state (no fire-and-forget audit calls on these types).

### Sub-package 3: DML advisories (no Casbin)
1. One PR per advisory: GHSA-jcf8-hchm-55h3 (WebSQL DML authorization), GHSA-3prq-qxg7-92qf (WebSQL DML scoping), GHSA-83g2-c2jr-c8vq (WebSQL DML token scopes), GHSA-v75c-28mx-7wm7 (WebSQL DML request handling). Each implements only its private advisory's fix list.
2. Add no write-grant model, no write-protected column flag, no write budget and no write connection: these are WP-SIMPL-45 product requirements (OD-26, D-45-8). Do not add a role-based write check and do not delegate any check to Casbin.

### Sub-package 4: #506, #507 remainder, #508
Each issue as listed in its GitHub body (verify line numbers before editing): global `Cache-Control: no-store` + `Vary` default with explicit opt-out, epoch in metadata cache keys, `DatasourceSecretRefPolicy` validation, generic DNS error, break-glass flow fail-closed, MCP `PersistedToolValidator` required and schema-recursive, prompt-argument validation, real catalog owner or none, empty visibility list without principal, multi-valued claims.

### Sub-package 5: dbt, catalog search, #475
1. GHSA-jw88-9j35-3c96 (dbt semantic manifest import): implement the private advisory's fix list (before WP-SIMPL-28 extracts the dbt code).
2. Catalog search (OD-4, SEC-SIMPL-82): add the two `CatalogSearchSecurityTests` of section 6; fix any red case so that authorization filtering happens before ranking and counting and embedding egress follows the allowlist.
3. #475: parameterised, typed cache mutations with failure invalidation.
4. GHSA-95f9-ffc7-vw2c and GHSA-jfrx-2mgc-pfh5: closed on `main`; re-verify and add the regression tests if missing.

### Sub-package 6: transport gate, audit archive, webhooks
1. GHSA-92vp-663x-pp52 (transport gate): implement the private advisory's fix list except the procedures part (WP-SIMPL-39 PR 1).
2. OD-8: delete `AuditWormExportService` and fail startup when `Audit:Worm:Enabled` (or any other archive export key) is set (SEC-SIMPL-86, SEC-SIMPL-31). Retention purge keeps signed purge checkpoints and anchors.
3. GHSA-4qx8-fv2p-q8r2 and GHSA-wvwx-q6jg-44wv: closed on `main`; re-verify and add the regression tests if missing.

### Sub-package 7: remaining advisories
1. One PR per advisory (GHSA-x8g9-cm3h-p6jq, GHSA-v77c-jh96-7rgr, GHSA-rxr4-mf28-p2hv, GHSA-66xj-4459-x8gx, GHSA-p234-g3ph-gqh2), each implementing its private advisory's fix list.
2. OD-11: the suppression threshold has a secure non-zero default and a floor (SEC-SIMPL-89). OD-12: the GHSA-66xj regression test runs per dialect (PostgreSQL, SQL Server, Oracle, Databricks) in the Parity project.
3. GHSA-p234-g3ph-gqh2 lands on the current docs store. If WP-SIMPL-40 PR 40c (database store) has merged first, re-verify on the new store and only add the regression test.
4. GHSA-x8g9-cm3h-p6jq: WP-SIMPL-15 (Garnet removal) and WP-SIMPL-43 step 17 (cluster-wide deprovisioning) touch the same caches; the lane has merge priority, the other WP rebases.

### Sub-package 8: #504
Add `[Trait("Category","Security")]` to the tests listed in #504 and add the missing ones; script in section 7 proves no security test is untagged.

## 6. Tests (all `[Trait("Category","Security")]`, project `tests/Autheris.Tests.Unit` unless noted)
Advisory regression tests use one class `Security/AdvisoryRegressionTests` and the neutral name `Ghsa_<first4>_<last4>_regression` (one theory per advisory, one case per bullet of the private advisory's fix list; details: private advisory).
- Sub-package 0: `Governance/Batch5OD9ScopeTests`: `GovernanceAdmin_without_tenant_claim_is_not_global`, `GovernanceAdmin_with_tenant_is_tenant_bound`, `Canonical_ClusterAdmin_is_global`, `Token_without_tenant_claim_uses_default_tenant` (OD-9, SEC-SIMPL-87); `AdvisoryRegressionTests.Ghsa_mgv5_mwww_regression`, `Ghsa_qx32_5cq2_regression` (SEC-SIMPL-88), `Ghsa_335j_7p4c_regression`, `Ghsa_xpw9_69pf_regression` (renamed batch-5 tests, step 4); existing batch-5 tests stay green.
- Sub-package 1: `Identity/SidFallbackTests` (`GetUserSid_without_sub_or_oid_does_not_use_client_id`, `GetAllUserSids_excludes_app_identifiers`, `RealmLookup_is_injected_not_static`); `AdvisoryRegressionTests.Ghsa_v5c2_cc86_regression`, `Ghsa_9229_wvm9_regression`, `Ghsa_7pjw_hvff_regression`, `Ghsa_7ccv_5qwm_regression`. (`FederationSecurityTests` and `DuckDbCoreSecurityTests`, SEC-SIMPL-94, are owned by WP-SIMPL-21, not by this WP.)
- Sub-package 2: `tests/Autheris.Tests.Architecture/RequiredAuditArchitectureTests.Security_decision_paths_use_RecordRequiredAsync`; `Catalog/DatasourceTestAuditTests.Probe_is_refused_when_audit_write_fails`.
- Sub-package 3: `AdvisoryRegressionTests.Ghsa_jcf8_55h3_regression`, `Ghsa_3prq_92qf_regression`, `Ghsa_83g2_c8vq_regression`, `Ghsa_v75c_7wm7_regression` (WP-SIMPL-45 keeps them green). The write-path tests (`WriteGrantTests`, `WriteTransportTests`, `DmlSecurityTests`) belong to WP-SIMPL-45.
- Sub-package 4: one test class per issue named `<Component>SecurityTests`, with at least one fail-closed test per bullet of the issue.
- Sub-package 5: `AdvisoryRegressionTests.Ghsa_jw88_3c96_regression`; `CatalogSearchSecurityTests.Restricted_tables_absent_from_results_scores_counts_snippets`, `CatalogSearchSecurityTests.Embedding_egress_refused_for_host_not_on_allowlist` (OD-4, SEC-SIMPL-82); `Cache_Mutation_Uses_Typed_Parameters`, `Cache_Mutation_Failure_Invalidates_Table` (#475); `Ghsa_95f9_vw2c_regression`, `Ghsa_jfrx_pfh5_regression` only if missing on `main`.
- Sub-package 6: `AdvisoryRegressionTests.Ghsa_92vp_pp52_regression` (shared with WP-SIMPL-39: whichever lands first creates the theory, the other adds its cases); `Worm_Enabled_Fails_Startup`; `AuditRetentionSecurityTests.Archive_key_fails_startup_and_purge_keeps_checkpoints_and_chain_valid` (SEC-SIMPL-86); `Ghsa_4qx8_q8r2_regression`, `Ghsa_wvwx_44wv_regression` only if missing on `main`.
- Sub-package 7: `AdvisoryRegressionTests.Ghsa_x8g9_p6jq_regression`, `Ghsa_v77c_7rgr_regression`, `Ghsa_rxr4_p2hv_regression`, `Ghsa_p234_gqh2_regression`; `Ghsa_66xj_x8gx_regression` (Parity project `tests/Autheris.Tests.SqlCompilerGate`, one case per dialect, tagged); `AggregationPrivacyTests.Threshold_floor_and_failure_denies` (SEC-SIMPL-89).
- Sub-package 8: `SecurityTraitCoverageTests.Listed_regression_tests_carry_security_trait` (Architecture, reflection over the #504 list).
- Delete/move: none; test files grow only (renames in sub-package 0 keep the test bodies).

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror` (one build at a time).
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --filter "Category=Security"`; `dotnet test tests/Autheris.Tests.Architecture -c Release`; `dotnet test tests/TrinoSqlEngine.Tests -c Release --filter "Category=Security"` for sub-package 3 and 7; `dotnet test tests/Autheris.Tests.SqlCompilerGate -c Release --filter "Category=Security"` for sub-package 7.
- `git grep -n "IsGlobalGovernanceAdmin" -- src` after sub-package 0: every remaining call site justified in the PR description.
- `git grep -n "GHSA-" -- src tests docs | grep -iv "GHSA-[a-z0-9-]*:" ` is reviewed manually: only ids and neutral titles, no advisory detail.
- `git grep -n "Ghsa_" -- tests` lists one `AdvisoryRegressionTests` theory per advisory of section 3.
- `df -h /work` before each build; remove `bin/obj` after push.

## 8. Acceptance criteria
- [ ] Batch 5 merged, OD-9 tests green, no tenant-less GovernanceAdmin is global; batch-5 test names neutral (SEC-SIMPL-01).
- [ ] #469, #475, #504, #506, #507, #508 and the audit part of #505 closed or re-classified with the deleting WP named.
- [ ] Every open advisory of section 3 has its `AdvisoryRegressionTests` theory green in the merged PR; every advisory listed as closed has its regression test on `main`.
- [ ] `RequiredAuditArchitectureTests` green with a non-empty scanned set.
- [ ] Sub-package 3 adds no write-grant design, no role-based write check and no Casbin reference (WP-SIMPL-45 owns the write model).
- [ ] `CatalogSearchSecurityTests` (2), `AuditRetentionSecurityTests`, `AggregationPrivacyTests` and `AdvisoryRegressionTests.Ghsa_p234_gqh2_regression` of section 6 green (sec plan 11.6, row 36).
- [ ] No exploit detail in any commit, PR body, test name, comment or doc (SEC-SIMPL-01).
- [ ] Phase 5 review clean per sub-package; build warning-free.

## 9. PR slicing (<= 400 net added src lines per PR unless `size-exception` is granted)
- PR 0: batch 5 as is plus OD-9 alignment and test renames (`size-exception` expected, +2367 existing; the alignment commit is separate and small).
- PR 1a #469; 1b GHSA-v5c2 + GHSA-9229; 1c GHSA-7pjw; 1d GHSA-7ccv.
- PR 2 audit bundle. PR 3a-3d one per DML advisory.
- PR 4a #506; 4b #507; 4c #508. PR 5a GHSA-jw88; 5b catalog search tests; 5c #475.
- PR 6a transport gate; 6b audit archive export deletion (OD-8); 6c closed-advisory re-verification. PR 7a-7e one per advisory. PR 8 trait sweep.

## 10. Risks, rollback, do-nots
- Risk: overlap with deletion WPs; mitigate by rebasing them onto the lane. Rollback: revert the single PR; each is independent from 2 onward.
- Risk: overlap with WP-SIMPL-45 on the DML path; WP-SIMPL-45 rebases onto sub-package 3 and keeps its regression tests green.
- Do NOT write advisory details anywhere public; do not weaken any existing control; do not delegate a check to Casbin; do not add a ReBAC switch or ReBAC test; do not add write-grant design here; do not skip the failing-test-first step; do not use `--no-verify`.

## 11. Estimate
About +2,200 src and +3,300 tests LOC across all PRs excluding batch 5; 8-11 person-days (batch 5 review, alignment and test renames 1.5-2 pd, P0 items 2-3 pd, DML advisories 1 pd, rest 3.5-5 pd).

## Decisions taken in this spec
- Every advisory step refers to the private advisory for its fix list instead of repeating it publicly; the spec names only id and neutral title.
- DML: sub-package 3 implements only the four DML advisories; the write-grant model, write-protected columns and write budget are WP-SIMPL-45 (D-45-8, OD-26). The ReBAC switch is dropped (OD-30, WP-SIMPL-46 PR 5).
- Advisory ownership: this WP is the only owner of GHSA-p234-g3ph-gqh2 and GHSA-x8g9-cm3h-p6jq; GHSA-92vp-663x-pp52 is shared with WP-SIMPL-39 (procedures part) through one regression theory.

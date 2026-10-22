# Plan: Security Findings Area EP (Endpoints / Policy)

Phase 2 implementation plan for the HIGH/MEDIUM `EP-` findings of `docs/plans/security.txt`:
EP-01, EP-02, EP-03, EP-04, EP-08, EP-09, EP-11. EP-06 is withdrawn (duplicate of MCP-01) and is out of scope; LOW findings
(EP-05, 07, 10, 12, 13, 14) are out of scope. All root causes were re-verified on branch `fix/security-review-open-findings`
(HEAD `55dc7e27`). No production deployment exists: breaking API and behaviour changes are accepted, no upgrade migrations.

## 0. Conflict resolution: EP-11 vs PERS-10 / PERS-11

Verified: `IScopedAccessPolicyEngine.EvaluateAsync` (`ScopedAccessPolicyEngine.cs:32`) has **no caller** in `src/`. The only
references to the engine are `AccessPolicyEndpoints.cs` (create/update/delete) and the DI registration
(`GatewayGovernanceServiceExtensions.cs:327`). `IAccessPolicyRepository` is otherwise used only by
`RecertificationService.cs:270-280` (revoke deactivates a policy) and the three `*GovernanceRepository.RightsAndPolicies.cs`
stores. **EP-11 is right**: scoped policies are not enforced on any query path. PERS-10 (non-atomic store, empty selectors match
all) and PERS-11 (realm ignored in grantee match) describe real store/engine defects, but they are latent: they would only
become exploitable if `EvaluateAsync` were wired in. Their severity is overstated as "enforced".

**Decision D-0 (remove the API, do not wire enforcement).** Wiring a second policy decision source into the unified PDP
(TableAccessPolicy, CatalogVisibilityService, GraphQL, SQL, MCP, Iceberg, stream RLS) would first require PERS-10, PERS-11,
four-eyes activation and Deny-precedence semantics, i.e. a new feature with its own PRD. Consent, access profiles, ReBAC and
Casbin already cover the use cases. Removing the API ends the fail-open compliance claim (201 + SECURITY_POLICY_MUTATED audit
for a control that does nothing) immediately and makes PERS-10/PERS-11 obsolete. A future scoped-policy feature goes through
Phase 1 again.

## 1. Findings

### EP-11 Scoped access policy API manages unenforced policies (MEDIUM) - WP-EP-1
- Root cause: confirmed (see section 0). In addition POST stores `IsActive` from the body with `ApprovedBy = null`
  (`AccessPolicyEndpoints.cs:142-147`); PUT passes the editor as approver (`:107`).
- Fix: delete `src/Autheris.Api/Endpoints/AccessPolicyEndpoints.cs` and its `Map...` registration; delete
  `IScopedAccessPolicyEngine`, `ScopedAccessPolicyEngine`, `ScopedPolicyEvaluationResult` and the DI line 327; delete
  `IAccessPolicyRepository` (`Domain/Interfaces`) and the policy methods of the three `RightsAndPolicies` partials
  (keep `IFunctionalRightRepository`); remove the access-policy asset type from `RecertificationService` (constructor
  parameter `accessPolicyRepository`, branch at `:270-280`); delete `ScopedAccessPolicyEngineTests.cs` and the policy parts of
  `RecertificationCampaignTests.cs` / `PolicyMetamorphicSecurityTests.cs`. Schema tables `ACCESS_POLICIES*` stay untouched in
  DDL ownership of the caller (no migrations); the code no longer reads or writes them.
- Fail-closed: `/api/v1/tenants/{t}/governance/policies` returns 404 (route not mapped); recertification campaigns
  requesting the policy asset type are rejected with 400 `UNSUPPORTED_ASSET_TYPE` instead of silently succeeding.
- Red tests: `AccessPolicyApiRemovedTests.PolicyRoutes_AreNotMapped` (GET/POST/PUT/DELETE -> 404),
  `RecertificationCampaignTests.CreateCampaign_WithAccessPolicyAssetType_IsRejected`, architecture test
  `NoScopedAccessPolicyEngineTests.NoTypeNamed_ScopedAccessPolicy_Exists` (reflection over Application/Api assemblies).
- Breaking: REST endpoint and recertification asset type removed.
- Dependencies: closes PERS-10 and PERS-11 as obsolete (PERS area plan must drop them); no other area references the engine.

### EP-04 REST plan apply omits caller tenant (MEDIUM) - WP-EP-2
- Root cause: confirmed. `GovernancePlanEndpoints.cs:114` calls `ApplyAccessAsync(effectiveRequest, adminSid, ct: ct)`;
  `ConsumeConfirmedPlanAsync` (`AccessPlanningService.cs:578-583`) skips the tenant check when `callerTenant == null`;
  `ApplyAccessAsync` falls back to `tableId.Domain ?? "default"` (`:292`).
- Fix: `IAccessPlanningService.ApplyAccessAsync(AdminApplyAccessRequest request, string adminSid, TenantId tenantId,
  CancellationToken ct = default)` and the same for `ApplyRowFilterEvaluationAsync` (tenant becomes required, typed).
  The REST endpoint passes `EndpointSecurity.GetRequestTenant(context)`; MCP passes `sessionContext.TenantId`. Delete the
  `Domain`/`"default"` fallback at `:292`. In `ConsumeConfirmedPlanAsync` require `stored.TenantId != null` and ordinal
  equality with the caller tenant, otherwise `SecurityException("Plan belongs to a different tenant.")`.
- Fail-closed: a plan without stored tenant, or a caller tenant mismatch, is never applied; no tuples, reload or audit.
- Red tests (`AccessPlanningTenantBindingTests`): `RestApply_WritesTuplesUnderRequestTenant_NotDatasetDomain`,
  `Apply_StoredPlanWithoutTenant_Throws`, `Apply_CallerTenantDiffersFromPlanTenant_Throws`,
  `ApplyRowFilterEvaluation_CallerTenantDiffers_Throws`; endpoint test
  `GovernancePlanEndpoints_Apply_PassesRequestTenant` (fake `IAccessPlanningService` captures the tenant).
- Breaking: interface signature change (compile-time); plans stored before the change cannot be applied (in-memory/TTL only).
- Dependencies: same files as EP-08; implement in the same WP.

### EP-08 ReBAC individual-grant rule only for `user:` subjects (MEDIUM) - WP-EP-2
- Root cause: confirmed. `RebacEndpoints.IsIndividualUserTuple` (`:158-159`) classifies only `user:` subjects; enforcement
  (`RebacTableGate.cs:70`, `CatalogVisibilityService.cs:336`) checks the bare canonical SID and the evaluator matches
  `t.User` exactly (`ZanzibarRebacEvaluator.cs:362`). `AccessPlanningService.cs:297,301` writes `user:{Principal}` tuples that
  never match the gate. Not ReBAC writers (no change): `McpSessionStore.GetOwnerKey` (session quota key) and
  `ClientTierResolver` (cache-tier subject).
- Decision D-1: the canonical ReBAC subject for an individual principal is the **bare canonical SID** (the value of
  `Sid.GetUserSid()` / `SecurityPrincipalContext.UserSid`), because every enforcing path already evaluates that form.
- Fix: new `Autheris.Domain.Model.RebacSubject` (static) with
  `static RebacSubjectParseResult Parse(string raw)` returning `Kind` (`Individual`, `Userset`, `Entity`, `Invalid`) and
  `Canonical`. Rules: trim; `user:<id>` and `sp:<id>` -> `Individual`, canonical `<id>`; a value containing `#` with a known
  entity prefix -> `Userset`; a known entity prefix (`table:`, `schema:`, `column:`, `group:`, `role:`, `catalog:`) ->
  `Entity`; a colon-free id matching the SID/GUID/realm-qualified character set -> `Individual`; anything else ->
  `Invalid`. Use it in: `RebacEndpoints.AddTuplesAsync` and `RemoveTupleAsync` (reject `Invalid` with 400
  `INVALID_REBAC_SUBJECT`, rewrite `User` to `Canonical`, apply the WP-SC-16 `GrantPolicy` to every `Individual`);
  `AccessPlanningService.ApplyAccessAsync` (validate `grant.Principal` via `Parse`, write the canonical bare id; `Invalid`
  -> `SecurityException` before consuming the plan); `ZanzibarRebacEvaluator.CheckAsync` (canonicalize `request.User`
  at entry; `Invalid` -> deny); `InMemoryRebacStore.AddTupleAsync` / `RedisRebacStore.AddTupleAsync` guard: a tuple whose
  `User` is not already canonical throws `ArgumentException` (defence against other writers). Update
  `RebacSeedHostedService` seeds to bare ids.
- Fail-closed: unknown subject forms are rejected on write and denied on check; individual grants without valid `ExpiresAt`
  are rejected regardless of prefix.
- Red tests (`RebacSubjectCanonicalizationTests`): `Parse_BareSid_IsIndividual`, `Parse_UserPrefix_CanonicalizesToBareSid`,
  `Parse_UnknownForm_IsInvalid`; `RebacEndpoints_AddBareSidWithoutExpiry_Returns400`,
  `RebacEndpoints_AddUserPrefixedSid_StoredAsBareSid`, `RebacEndpoints_InvalidSubject_Returns400`,
  `PlanApply_GrantedPrincipal_PassesRebacTableGate` (end-to-end: planned grant becomes effective for that SID),
  `Store_AddNonCanonicalSubject_Throws`, `Evaluator_InvalidSubject_Denies`.
- Breaking: `user:`/`sp:` prefixes are accepted on input but never stored; bare-SID tuples without expiry are rejected.
- Dependencies: if another area's plan (REBAC/MCP) also edits `RebacEndpoints` or the evaluator, rebase onto it; WP-EP-2
  must land before any plan that relies on planned grants being effective.

### EP-09 WebSQL/Trino statement owner `PrimarySid ?? sub ?? anonymous` (MEDIUM) - WP-EP-3
- Root cause: confirmed. `WebSqlStatementManager.ResolveUserSid` (`:341-342`) is used for owner, quota (`:83-91`) and
  `ValidateSessionOwnership` (`:332-338`); the statement id carries only 24 random bits (`:150`).
- Fix: change `SubmitOrWaitAsync`, `GetStatusOrWaitAsync`, `CancelStatementAsync` to take an explicit `Sid owner` in addition
  to the `ClaimsPrincipal`; `WebSqlEndpoints.Execution.cs:140,420,466` resolve it via `EndpointSecurity.GetCallerIdentity(context)`
  (SecurityPrincipalContext.UserSid) and return 401 when it is null. Delete `ResolveUserSid`. Statement id becomes
  `{yyyyMMdd_HHmmss}_{counter:00000}_{RandomNumberGenerator.GetHexString(32, lowercase: true)}` (128 random bits, Trino-style
  prefix kept for client compatibility). Ownership comparison stays ordinal, tenant check unchanged.
- Fail-closed: no canonical SID -> 401, never a shared "anonymous" owner; `Sid` with empty value -> `ArgumentException`.
- Red tests (`WebSqlStatementManagerTests`): `TwoOidcUsersWithoutPrimarySid_CannotReadEachOthersStatement`,
  `TwoOidcUsersWithoutPrimarySid_HaveSeparateQuotas`, `Cancel_ByOtherCanonicalSid_Throws`, `StatementId_Has128BitRandomSuffix`;
  endpoint test `TrinoStatement_WithoutCanonicalIdentity_Returns401`.
- Breaking: manager signatures; statement id length.
- Dependencies: none.

### EP-02 DP budget self-reset bypass with tenant-qualified client id (MEDIUM) - WP-EP-4
- Root cause: confirmed. `GovernanceEndpoints.Audit.cs:395-399` compares raw `clientId` with `callerId`; the key is derived
  afterwards by `ResolveTenantBoundClientId` (`GovernanceEndpoints.Access.cs:628-647`). The check is also skipped when
  `callerId` is null.
- Fix: compute `targetKey` first; build the set of caller keys = `ResolveTenantBoundClientId` applied to every caller
  identity form (`client_id`, `azp`, `sub`, `NameIdentifier`, `Identity.Name`, `EndpointSecurity.GetCallerIdentity`); deny
  if `targetKey` is in the set (OrdinalIgnoreCase). Extract as `internal static bool IsSelfBudgetReset(HttpContext, string targetKey)`.
- Fail-closed: caller without any resolvable identity -> 403; forbidden tenant -> 403 (unchanged). Four-eyes on budget resets
  is not added (default taken: the PRD hint marks it optional; distinct-principal rule is sufficient).
- Red tests (`DifferentialPrivacyBudgetResetTests`): `Reset_TenantQualifiedOwnClientId_Returns403`,
  `Reset_OwnClientIdDifferentCase_Returns403`, `Reset_CallerWithoutIdentity_Returns403`, `Reset_OtherClientSameTenant_Returns200`.
- Breaking: none beyond stricter denial. Dependencies: none.

### EP-03 OpenLineage sync uses raw `tenant_id` claim (MEDIUM) - WP-EP-4
- Root cause: confirmed (`GovernanceEndpoints.Audit.cs:572`).
- Fix: `var tenantId = EndpointSecurity.GetRequestTenant(context);` and restrict the endpoint to `GovernanceAdmin`
  (default taken: DataOwner is not tenant-bound; owners keep read access to lineage elsewhere). Map
  `UnauthorizedAccessException` / invalid tenant from `GetRequestTenant` to 401/403 through the existing pattern.
- Fail-closed: no `"default"` fallback; unresolved tenant -> 403, never a 500.
- Red tests (`OpenLineageSyncTenantTests`): `Sync_UsesRealmBoundTenant_NotTenantIdClaim`, `Sync_EntraTidCaller_PushesTidTenant`,
  `Sync_DataOwnerOnly_Returns403`, `Sync_MalformedTenantClaim_DoesNotReturn500`.
- Breaking: DataOwner loses the sync trigger. Dependencies: none.

### EP-01 Unmasked approval with zero matched tables (MEDIUM) - WP-EP-5
- Root cause: confirmed (`GovernanceEndpoints.Access.cs:465-486`): the owner loop iterates `GetAllTablesAsync` matches and
  passes when there are none. `ITableMetadataRepository.GetAllTablesAsync` has no tenant parameter (catalog metadata is
  global), so tenant scoping of the list is not possible at this layer.
- Decision D-2: an individual (`User`, `Standard`) Unmasked assignment may only be approved for a profile whose patterns
  contain no wildcard (concrete tables), and at least one table must match. This removes the "tables registered later" gap
  without a re-approval mechanism; wildcard Unmasked access remains possible only through group/role mappings, which stay on
  route (a) four-eyes.
- Fix: before the loop, `if (profile.HasWildcardPattern) return 403 DATA_OWNER_APPROVAL_REQUIRED` (new
  `AccessProfile.HasWildcardPattern` property, `*` or `?` in any pattern) and `if (matched.Count == 0) return 403
  DATA_OWNER_APPROVAL_REQUIRED`; additionally require every concrete pattern to be matched by a catalogued table.
- Fail-closed: unknown or uncatalogued tables -> 403; missing services -> 503 (unchanged).
- Red tests (`ProfileAssignmentApprovalOwnerTests`): `ApproveIndividualUnmasked_PatternMatchesNoTable_Returns403`,
  `ApproveIndividualUnmasked_WildcardPattern_Returns403`, `ApproveIndividualUnmasked_OneConcretePatternUncatalogued_Returns403`,
  `ApproveIndividualUnmasked_OwnerOfAllConcreteTables_Returns200`, `ApproveGroupUnmasked_Wildcard_StillAllowedViaFourEyes`.
- Breaking: individual Unmasked grants on wildcard profiles are no longer approvable. Dependencies: AccessProfile area
  (VF-05 approval hash binding) touches the same handler; rebase if it lands first.

## 2. Work packages

| WP | Findings | Order | Size (rough) | Main files |
|----|----------|-------|--------------|------------|
| WP-EP-1 | EP-11 | 1 | M (~-900/+80 LOC, mostly deletion) | AccessPolicyEndpoints, ScopedAccessPolicyEngine, RightsAndPolicies x3, RecertificationService |
| WP-EP-2 | EP-04, EP-08 | 2 | M (~+350 LOC) | GovernancePlanEndpoints, AccessPlanningService, IAccessPlanningService, McpAdminTools, RebacEndpoints, RebacSubject, ZanzibarRebacEvaluator, Rebac stores |
| WP-EP-3 | EP-09 | 3 (parallel to 2) | S (~+150 LOC) | WebSqlStatementManager, WebSqlEndpoints.Execution |
| WP-EP-4 | EP-02, EP-03 | 4 (parallel) | S (~+120 LOC) | GovernanceEndpoints.Audit, GovernanceEndpoints.Access |
| WP-EP-5 | EP-01 | 5 (parallel) | S (~+100 LOC) | GovernanceEndpoints.Access, AccessProfile |

WP-EP-1 first so later WPs do not maintain dead code; WP-EP-3/4/5 touch disjoint files and may run in parallel lanes.
WP-EP-4 and WP-EP-5 both edit `GovernanceEndpoints.Access.cs` (different methods): sequential if in one lane.

## 3. Security conditions for Phase 3

1. No scoped-policy decision path may remain: architecture test proves no `ScopedAccessPolicy*` type and no policy route.
2. Plan apply (access and row-filter) is impossible without a stored plan tenant equal to the typed caller tenant; no
   `Domain`/`"default"` tenant fallback exists anywhere in `AccessPlanningService`.
3. Exactly one ReBAC subject canonicalizer (`RebacSubject`) is used by every tuple writer and by the evaluator entry; stores
   reject non-canonical subjects; every individual subject is subject to WP-SC-16 expiry.
4. A planned grant must be shown effective through `RebacTableGate` in a test (no silently ineffective grants).
5. WebSQL owner and quota keys come only from the canonical `SecurityPrincipalContext.UserSid`; no literal `"anonymous"`
   owner; statement ids carry at least 128 random bits from a CSPRNG.
6. DP self-reset check runs on normalized keys and denies when the caller identity is unresolvable.
7. OpenLineage tenant comes only from `EndpointSecurity.GetRequestTenant`; no raw `tenant_id` claim read in endpoints
   (grep-based architecture test `NoRawTenantIdClaimInEndpointsTests`).
8. Individual Unmasked approval denies on zero matches, wildcard patterns and uncatalogued concrete patterns.
9. All denials return the existing error shapes without exception text (NoRawExceptionMessageInEndpointResponsesTests stays green).

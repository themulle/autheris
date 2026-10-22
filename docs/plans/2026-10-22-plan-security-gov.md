# Plan: Security findings, area GOV (HIGH/MEDIUM)

- Source: `docs/plans/security.txt` (GOV-01, 02, 04, 06, 07, 08, 09, 10, 12; GOV-03/05/11/13/14/15 are LOW and out of scope).
- Code verified on `fix/security-review-open-findings` @ `7e0df9de` (G-PLAN-REBASE). Commits after the audit that touch GOV:
  `5bbd74e3` (GOV-01), `55dc7e27` (bulk four-eyes removal), `c65345fe` (GOV-06/07), `12071ea6` (GOV-10). The last two say
  "Not built or tested yet", so their findings move to verification work, not new design.
- No production yet: breaking changes to repository interfaces, API error codes and stored state are allowed (no migrations of old data).

## Findings

### GOV-01 Recert auto-revoke order (MEDIUM) - FIXED in `5bbd74e3`
Verified: `RecertificationService.AutoRevokeItemAsync` deactivates before `UpdateItemDecisionAsync`; unknown types/keys/repos throw;
per-item try/catch in the cycle. Remaining work: regression tests only (WP-GOV-A), plus the CAS interplay defined in GOV-02.

### GOV-02 Certify ignores reviewer, state and campaign (MEDIUM) - OPEN
Root cause (verified): `RecertificationService.CertifyItemAsync` (l.122-146) and `RevokeItemAsync` only compare `reviewerId` with
`AssignedTo`; no check of `ReviewerId`, `Decision == Pending`, campaign `Status`/`Deadline`. `RecertificationEndpoints` l.67/91 fall
back to `"REVIEWER"` when `GetUserSid()` is null. `RecertificationStore.UpdateItemDecisionAsync` (l.185-212) is an unconditional UPDATE (PERS-12).
Fix:
1. `IRecertificationRepository.UpdateItemDecisionAsync` becomes `Task<bool> TryUpdateItemDecisionAsync(TenantId, string campaignId, string itemId, RecertDecision expected, RecertDecision decision, string decisionBy, string? comments, CancellationToken)`; SQL adds `AND decision = @expected`, returns `affected == 1`. Old method removed (all three repo partials + store).
2. Certify/Revoke: load campaign (`GetCampaignByIdAsync`); require campaign `Active`, `now <= Deadline` (inject `TimeProvider`), `item.Decision == Pending`, `reviewerId` equals `item.ReviewerId` (trimmed, OrdinalIgnoreCase) and not `AssignedTo`. Violations throw `SecurityException` (403) or `InvalidOperationException` (409 for state/deadline); CAS false -> 409 `RECERT_ITEM_ALREADY_DECIDED`.
3. Endpoints: no SID -> 403 before calling the service; delete the `"REVIEWER"` fallback.
4. Auto-revoke: CAS `expected: Pending`; on false re-read the item; if it is `Certified` the asset is already gone, so write `Revoked` with CAS `expected: Certified` and audit `SECURITY_RECERT_DECISION_CONFLICT` (evidence must match reality).
Fail closed: any missing campaign/item/SID or CAS conflict leaves the record unchanged and returns an error.
Red tests (`tests/Autheris.Tests.Unit/Governance/Recertification/RecertificationCertifyGuardTests.cs`): `Certify_ByNonDesignatedReviewer_Throws`,
`Certify_RevokedItem_Throws`, `Certify_AfterDeadline_Throws`, `Certify_CompletedCampaign_Throws`, `Revoke_ByNonDesignatedReviewer_Throws`,
`AutoRevoke_ConcurrentCertify_EndsRevokedAndAudited`; endpoint test `Certify_WithoutSid_Returns403`; store test (Sqlite)
`TryUpdateItemDecision_ExpectedStateMismatch_ReturnsFalse`.
Dependency: PERS-12 (same store change) - implement here, mark PERS-12 as covered in the PERS plan.

### GOV-04 Access-plan apply writes unmatched ReBAC tuples, self-grant (MEDIUM) - OPEN
Root cause (verified): `AccessPlanningService.ApplyAccessAsync` l.297 writes `user:{Principal}` / `table:{raw DatasetId}`; `RebacTableGate.IsAllowedAsync` checks raw SID and `ObjectId(TableIdentifier)` = `table:{Domain}.{Schema}.{Table}`. Column tuples (l.301) have no consumer. `EnsureGrantsAllowed` (l.62-82) never compares `grant.Principal` with the admin; tuple tenant falls back to the dataset domain / `"default"` while the gate uses the caller tenant.
Fix:
1. Tuple = `new RebacTuple(tenant, grant.Principal /* SID, no prefix */, RebacTableGate.Relation, RebacTableGate.ObjectId(tableId))`; delete the column tuples and report column levels as "not applied" in the plan result (default taken: drop, do not implement).
2. Tenant: require a non-empty caller tenant; no domain/"default" fallback (throw `SecurityException`).
3. Self-grant: extract `DeclarativeConsentApplyService.CollectActorIdentifiers` into a shared `internal static ActorIdentifiers.Collect(ClaimsPrincipal, Sid, SecurityPrincipalContext?)` (Application/Governance/Services); reject at plan time and again at apply when `grant.Principal` matches (OrdinalIgnoreCase). `PlanAccessAsync`/`ApplyAccessAsync` gain a `ClaimsPrincipal caller` parameter.
4. Four-eyes: if `GrantPolicy` decision `RequiresFourEyes` for any grant, apply is rejected with `GrantErrorCodes.FourEyesRequired` (single-actor MCP path never grants sensitive access; use the consent workflow).
Fail closed: unparsable dataset id, missing tenant, self-grant or four-eyes requirement -> no tuple written, audit DENY.
Red tests (`tests/Autheris.Tests.Unit/Governance/AccessPlanApplyTupleTests.cs`): `Apply_WritesTupleMatchedByRebacTableGate` (apply, then `RebacTableGate.IsAllowedAsync` with an in-memory `ZanzibarRebacEvaluator` returns true), `Apply_TwoPartDatasetId_UsesNormalisedObjectId`, `Apply_WritesNoColumnTuples`, `Plan_GrantToSelf_Throws`, `Apply_GrantToSelfAlias_Throws`, `Apply_SensitiveGrant_RequiresFourEyes_Rejected`, `Apply_WithoutTenant_Throws`.
Dependency: MCP area (admin_apply_access caller passes the principal).

### GOV-06 Only one access profile applied (MEDIUM) - FIXED in `c65345fe`, untested
Verified: `TableAccessPolicy.ApplyAccessProfiles` uses the most restrictive mode of `profiles[0]` and ANDs the row filters of all matched profiles; any unparsable predicate denies. Work: tests only (WP-GOV-A):
`TwoProfiles_StrictWithoutFilter_AndDefaultWithFilter_BothRestrictionsApply`, `TwoStrictProfiles_BothRowFiltersApplied`, `OneInvalidPredicate_DeniesTable`.

### GOV-07 Undefined numeric maskingMode (MEDIUM) - FIXED in `c65345fe`; `55dc7e27` verified
Verified: `GovernanceEndpoints.Access.cs` l.137 rejects `!Enum.IsDefined(mode)`; `AccessProfileStore` l.440 reads undefined as Strict;
`AccessProfileAssignmentRules.EnforcedMode`/`Rank` treat undefined as Strict; `ApplyProfileMasking` default arm denies sensitive columns.
`55dc7e27` (removal of the bulk `RequiresFourEyes` 400) is acceptable now: Unmasked is rejected with 403 before, undefined modes are rejected with 400, and Default/Strict only reduce access. Condition: keep the IsDefined check covered by a test, otherwise the removal reopens the hole.
Red tests (WP-GOV-A): `BulkConsent_NumericUndefinedMode_Returns400`, `BulkConsent_NumericDefinedMode_Unmasked_Returns403`, `Store_UndefinedMode_ReadsAsStrict`, `UndefinedModeProfile_DoesNotOutrankOrNeutraliseStrict`.

### GOV-08 Role provider outage cached as empty groups (MEDIUM) - OPEN
Root cause (verified): Graph/Scim/Ldaps providers return `ProviderRoleResult.Empty` on any exception; `RoleResolver.ResolveAsync` l.117 swallows exceptions and caches `"resolved"` for 15/5 min; `EnterpriseClaimsTransformation` l.66-69 falls back to `string.Empty` as user key.
Fix:
1. `ProviderRoleResult` gains `bool Failed` and `static ProviderRoleResult FailedResult`; providers return it instead of `Empty` on exceptions/throttling/open breaker (a genuine "no groups" stays `Empty`).
2. `RoleResolution` gains `RoleResolutionStatus Status { Resolved, Degraded }`. Any failed provider or caught exception -> `Degraded`; degraded results are never written to `IRoleResolverCache` (or with a TTL of at most 30 s, config `Identity:DegradedRoleCacheSeconds`, default 0).
3. `EnterpriseClaimsTransformation`: empty user key -> no resolver call, add claim `autheris:role_resolution=degraded`; degraded resolution adds the same claim; metric `autheris_role_resolution_degraded_total` + warning log.
4. Enforcement: `TableAccessPolicy` (single PDP entry) denies when the query claims carry `autheris:role_resolution=degraded` ("Group resolution unavailable (fail-closed)"); Casbin/consent paths read through the same PDP. Default taken: deny the data request rather than "match all group-scoped deny rules" (simpler, strictly fail closed).
Red tests (`tests/Autheris.Tests.Unit/Identity/RoleResolverDegradedTests.cs`): `ProviderThrows_ResolutionDegraded_NotCached`, `GraphThrottled_ReturnsFailedResult`, `MixedProviders_OneFails_Degraded`, `NoGroups_Success_ResolvedAndCached`; `EnterpriseClaimsTransformationTests.EmptyUserKey_MarksDegraded_NoSharedCacheEntry`; `TableAccessPolicyTests.DegradedRoleClaim_DeniesTable`.
Breaking: callers relying on empty groups during an outage now get denials.

### GOV-09 TOTP brute force (MEDIUM) - OPEN
Root cause (verified): `TotpVerificationService.VerifyAndConsumeTotpAsync` l.99-112 reads the counter (`IncrementAsync(key, 0)`) then increments after failure; `TryMatchStep` l.278-302 accepts SHA-1 or SHA-256 over +/-1 step (6 codes); cluster outage degrades to local counts. Enrollment URI advertises SHA1 only (l.78).
Fix: reserve-then-check: `var count = await _clusterState.IncrementAsync(failureKey, 1, _lockoutWindow, ct)`; reject when `count > _maxFailedAttempts`; on success `ResetFailuresAsync`. `ClusterStateUnavailableException` or `null` from the store when a cluster store is configured -> return false (fail closed); local counter only when no cluster store is registered, and it uses the same increment-first semantics under `_failureLock`. Remove the SHA-256 candidate (SHA-1 only, matches enrollment). Default `_maxFailedAttempts` unchanged.
Red tests (`tests/Autheris.Tests.Unit/Security/Totp/TotpLockoutConcurrencyTests.cs`): `ParallelWrongGuesses_EvaluatedAtMostMaxAttempts` (50 parallel calls, counting fake verifies <= limit), `Sha256Code_Rejected`, `ClusterStoreUnavailable_RejectsValidCode`, `SuccessAfterFailures_ResetsCounter`.

### GOV-10 Query analysis self-simulation as admin (MEDIUM) - MOSTLY FIXED in `12071ea6`
Verified: any target needs `HasSimulatePrivilege(caller)` (l.141); `isAdmin` only for the real caller without target (l.166); the endpoint strips admin/simulate roles via `CreateSimulatedTargetPrincipal`. Residual (open):
1. `HasSimulatePrivilege` still uses `Contains(...)` and the generic `"admin"` value (l.684-692): replace with exact ordinal-ignore-case matches on `governance:simulate`, `governance:admin`, `GovernanceAdmin` (role claims and space-split `scope` tokens); drop `"admin"`.
2. The simulated target has no tenant claim, so `LoadActiveConsentsAsync` queries with `tenantId: null`: the target identity must carry the caller's tenant claim (`CreateSimulatedTargetPrincipal(subject, roles, callerTenant)`); caller without tenant in multi-tenant mode -> 403.
Red tests (`tests/Autheris.Tests.Unit/Governance/QueryAnalysisSimulationTests.cs`): `OwnSid_WithTargetRoles_WithoutPrivilege_Throws`, `Target_WithGovernanceAdminRole_GetsNoAdminView`, `TenantPrefixedAdminRole_IsNotSimulatePrivilege`, `GenericAdminRole_IsNotSimulatePrivilege`, `SimulatedTarget_ConsentsScopedToCallerTenant`.

### GOV-12 A-10 bypass through approval workflow (MEDIUM) - OPEN
Root cause (verified): only `AttributeValidatingConsentRepository.ValidateAsync` (IConsentRepository decorator) calls `ConsentRowFilterAttributeValidator.IsAttributeSource`; `IConsentApprovalRepository` (registered directly on the concrete repository, `GatewayGovernanceServiceExtensions` l.379) materialises consents via `ActivateConsentAsync`, `ActivateConsentForAutoApproveAsync`, `ExecuteAtomicApprovalWithConsentAsync` (GraphQL `MutationTypes` l.740, ITSM `ItsmWebhookHandler` l.415) with direct `INSERT INTO CONSENTS`; `IsAttributeSource` filters `d.Tenant == consent.TenantId`.
Fix (no decorator on IConsentApprovalRepository, because callers cast it to `ITableMetadataRepository`, e.g. `MutationTypes` l.489):
1. New `IConsentMaterializationGuard` (Application/Identity/Attributes): `ValueTask EnsureMayMaterializeAsync(TenantId tenant, TableIdentifier table, IReadOnlyList<ConsentRowFilter> filters, CancellationToken ct)`; impl `CallerAttributeConsentGuard` wraps the A-10 check plus `ValidateAgainstRegistry`, throws `InvalidOperationException` (409 `CONSENT_ON_ATTRIBUTE_SOURCE`).
2. `IsAttributeSource` evaluates attribute-table definitions of ALL tenants pointing at the same datasource/schema/table (tenant filter removed).
3. The three governance repositories take the guard as a required ctor dependency and call it inside the materialising transaction before every `INSERT INTO CONSENTS`; `AttributeValidatingConsentRepository` delegates to the same guard (single implementation). Registry unavailable -> guard throws (fail closed).
4. `ConsentRequestService.CreateConsentRequestAsync` and `BundledConsentRequestService` call the guard at request creation (early 409, no request stored).
Red tests: `tests/Autheris.Tests.Unit/Identity/Attributes/ConsentMaterializationGuardTests.cs` - `AttributeSourceOfOtherTenant_Refused`, `AllowListedSource_Allowed`, `RegistryUnavailable_Throws`; `ConsentRequestServiceTests.CreateRequest_OnAttributeSource_Refused`; Sqlite repository tests `ActivateConsent_OnAttributeSource_Throws_NoConsentRow`, `AtomicApproval_OnAttributeSource_Throws_NoConsentRow`, `AutoApprove_OnAttributeSource_Throws`; architecture test `EveryConsentInsertSiteCallsMaterializationGuard` (scans the three `*GovernanceRepository.Consent.cs` for `INSERT INTO CONSENTS` per method with a guard call).
Dependency: PERS area (repository ctor changes, Postgres/SqlServer integration tests); EXT (ITSM path covered transitively).

## Work packages (order)
| WP | Findings | Content | Size |
|---|---|---|---|
| WP-GOV-A | 01, 06, 07, 10 (verify) | Regression tests for landed fixes; fix anything the tests expose | S (~250 LOC tests) |
| WP-GOV-B | 09 | TOTP increment-first, SHA-1 only, fail closed | S (~120) |
| WP-GOV-C | 02 (+PERS-12) | Recert guards, CAS store method, endpoint SID | M (~300) |
| WP-GOV-D | 10 residual, 04 | Exact privilege match, tenant-bound target; access-plan tuples, self-grant, four-eyes | M (~350) |
| WP-GOV-E | 12 | Materialization guard in repos, request creation, cross-tenant A-10 | M (~400) |
| WP-GOV-F | 08 | Degraded role resolution, no caching, PDP deny | M-L (~450) |
A first (it validates unbuilt commits); B-E independent after A; F last (widest blast radius, touches the claims pipeline).

## Security conditions for Phase 3
1. Every new check fails closed: missing SID/tenant/campaign, store or registry unavailability, CAS conflict -> deny/no write, never a fallback constant.
2. `Enum.IsDefined` must guard every `MaskingPolicyMode` input; the removal in `55dc7e27` is only acceptable while `BulkConsent_NumericUndefinedMode_Returns400` passes.
3. No consent row may be inserted without passing `IConsentMaterializationGuard`; enforced by the architecture test.
4. ReBAC tuples are built only via `RebacTableGate.ObjectId` and the raw SID; a test round-trips apply -> gate.
5. TOTP attempts are reserved before evaluation; parallel test must show at most `MaxFailedAttempts` evaluations per window.
6. Degraded role resolution is never cached for longer than the configured cap (default 0) and always denies data access.
7. Simulation never yields admin semantics or cross-tenant consents; privilege role matching is exact.

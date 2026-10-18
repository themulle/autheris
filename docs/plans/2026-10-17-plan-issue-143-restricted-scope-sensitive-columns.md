# PLAN-GOV-143: Sensitive Columns for Restricted Scopes Only with a Scope-Specific Clear

**Document ID:** `PLAN-GOV-143`
**Date:** 2026-10-17
**Phase:** 2 (solution architect); next: Phase 3 security review (mandatory, access-control semantics)
**PRD:** [`REQ-GOV-143`](2026-10-17-req-issue-143-restricted-scope-sensitive-columns.md) · **Parent:** [00-master-plan-overview.md](00-master-plan-overview.md) rank 2.7 · **Intake:** [`INTAKE-2026-10-17`](2026-10-17-issue-intake-assessment.md) §3.2
**Verified against:** `origin/main` `f0071be6`; open branches `release/chunk1-consolidated` (contains `fix/procedure-inc1-security` = 1.6 and `fix/issue-158-inc0-explain-access`), `feat/issue-129-caller-attribute-row-filters` (I-25, local worktree head), `feat/issue-107-wp-c` (I-3, no Phase 4 code yet), lane B worktree `feat/issue-115-read-path-switches`, lane C worktree `feat/issue-151-composite-key-relations`.

## 1. Inputs and defaults

- Owner decisions: OQ-2 default list `["Agent.Read"]`; OQ-3 access profile `Unmasked` does not override; OQ-10 `cleared_columns` lifting a scope `Mask` is a bug (fixed by INC-1). D-1..D-12 do not touch this item. No production, no migrations, no schema change.
- Defaults taken (PRD recommendation): Q-143-2 an explicit scope `Clear` is needed for **every** restricted scope held; Q-143-4 procedure `cleared_columns` follow R-5; Q-143-5 `describe_my_access` exposes a restricted flag (section 3, D-9).

## 2. Verified code facts beyond the PRD

| # | Fact | Consequence |
|---|---|---|
| V-1 | `ConsentResolutionService` is a DI singleton (`GatewayGovernanceServiceExtensions.cs:144`) but also created with `new ConsentResolutionService()` as fallback in `McpDatasetCatalog.cs:87,120` (lane B edits these ctors for #138 INC-1). | Parameterless construction must default to the secure list (`Agent.Read`); no edit of `McpDatasetCatalog.cs`. |
| V-2 | The decision cache has an L2 MemoryPack envelope `CachedConsentEnvelope` (`Domain/Model/CachedConsentEnvelope.cs:13-27`, `FromDecision` `:29`, `ToDecision` `:55`; read at `ConsentCacheService.cs:165,302`). Init-only decision properties not in the envelope are lost on an L2 hit. | A new decision field must round-trip, and a legacy or foreign entry must be a miss (fail closed). |
| V-3 | `with { ... }` copies keep init properties: `TableAccessPolicy.Consent.cs:157` (access profiles, `Unmasked` at `:147`), `TableAccessPolicy.Rls.cs:65,83` (ABAC), `GovernedProcedureExecutionService.cs:406`. Rebuilds that drop them: `GovernedAggregationService.ValidationAndExecution.cs:105-135` (`BuildMetricScopedFallbackDecision` copies `existing` levels into a new `Allowed`), `SemanticMetricQueryService.ConsentAndEvaluation.cs:174-196` (fallback without resolver re-resolves rules itself). All other `TableAccessDecision.Allowed(...)` sites (`GovernedSqlRewriter.cs:490`, `FederatedDuckDbExecutionService.cs:446`, `TableAccessPolicy.cs:268`, `GovernedProcedureExecutionService.cs:367`, `CasbinEnforcementService.Columns.cs:568`, `StreamRlsPolicyEnforcer.cs:316`) use an empty column map, so sensitive columns are already `Mask`. | Two rebuild sites must carry or compute the restriction. |
| V-4 | Path-local copy of the sensitive rule: `GovernedConnectorReader.cs:232-236` uses `HasExplicitClear` directly. | Switch to `GetEffectiveColumnAccess`. |
| V-5 | `ComputeSubjectContextHash` (`IConsentCacheService.cs:46-60`) already hashes the scope set; the list is bound once (no reload). L2 is shared across instances, which may run different lists during a rolling deploy. | Fingerprint check on L2 read (D-6), no key change. |
| V-6 | On `release/chunk1-consolidated`, INC-1 replaced the `cleared_columns` override by `ComputeSensitivityOnlyMask` (`GovernedProcedureExecutionService.cs:614-639` there; use at `:836-841`): only a `Mask` from catalog sensitivity alone (column absent from `ColumnAccess`, `HasUnconstrainedColumnAllow`) is lifted. Such a column has no rule, so it can never carry a scope `Clear`. | R-11 reduces to "empty lift set for a restricted decision". |
| V-7 | `TokenAccessScope.GetScopes(principal)` reads `scp` claim types only (`TokenAccessScope.cs:18-22,37-41`); `GetScopeSet(IEnumerable<Claim>)` also reads `scope` (`:50-65`). Every `ResolveAccess` caller uses the claims overload (`TableAccessPolicy.Consent.cs:36`, aggregation `:66`, metrics `:58,155`, explanation `:71`, analysis `:133`, `McpDatasetCatalog.cs:500`, `CatalogVisibility.cs:290`, `QueryTypes.cs:449`). `GetScopes` feeds `EntraTokenPolicy.cs:28` and `ConsentDecisionGuard.cs:92` (delegated-token test of D-7). | Do not widen `GetScopes`: a `scope` claim would make an app-only token look delegated in `ConsentDecisionGuard` (fail open against D-7). Pin the claims overload by test instead (S-5). |
| V-8 | Config lives under `Gateway:`; lists bind `[]` to `null` (see `JwtOptions.ReadOnlyScopes` `:182-195`). | A plain list cannot express "off" next to a non-empty default; use an explicit `Enabled` switch. |
| V-9 | Impersonated analysis evaluates the target with an extra worst-case scope set (`QueryAnalysisService.Evaluation.cs:306-325`, minimum across evaluations). | R-14 = one more evaluation with the restricted list. |

## 3. Decisions

1. **D-1 Options.** New `src/Autheris.Domain/Options/Security/RestrictedScopeOptions.cs`, `SectionName = "Gateway:Governance:RestrictedScopes"` (constant in the class; `GatewaySections.cs` untouched because I-25 edits it): `bool Enabled = true`, `List<string>? Scopes` (`null` -> `["Agent.Read"]`). Deviation from the PRD key `Governance:RestrictedScopes`: repository convention plus V-8. Registered `AddOptions<>().BindConfiguration().ValidateOnStart()` in `GatewayGovernanceServiceExtensions`, read via `IOptions` (not reloadable, R-10).
2. **D-2 Validator.** `RestrictedScopeOptionsValidator : IValidateOptions<RestrictedScopeOptions>`: an entry that is null, blank, contains whitespace, ends with `/` or exceeds 256 chars fails startup; duplicates after normalisation collapse. An empty array (binds `null`) keeps the default and logs a warning; `Enabled=false` logs one information line "restricted scopes disabled". The effective list (names only) is logged once at information level.
3. **D-3 Policy.** New `src/Autheris.Domain/Security/IRestrictedScopePolicy.cs` + `RestrictedScopePolicy`: `IReadOnlySet<string> Scopes` (bare names, `OrdinalIgnoreCase`), `string Fingerprint` (SHA-256 of the sorted list, `"off"` when disabled), `IReadOnlySet<string> HeldBy(IReadOnlySet<string>? callerScopes)`; statics `Default` (`Agent.Read`) and `Disabled`. Normalisation moves to `ScopeNames.ToBareName` (Domain); `EntraTokenPolicy.IsSameScope` (`:93-103`) delegates to it. Singleton built from options.
4. **D-4 Decision field and choke point.** `TableAccessDecision` gets `public RestrictedScopeClearance? ScopeClearance { get; init; }` (`null` = unrestricted caller); new record `RestrictedScopeClearance(IReadOnlySet<string> RestrictedScopesHeld, IReadOnlySet<string> ClearedColumns)` with `Permits(column)` (case-insensitive). Extract `public static bool IsCatalogSensitive(string column, TableMetadata? metadata)` from `GetEffectiveColumnAccess` (`TableAccessDecision.cs:420-427`, R-12). `GetEffectiveColumnAccess` masks when `IsCatalogSensitive && (!HasExplicitClear || ScopeClearance is { } c && !c.Permits(column))`. New `IsMaskedByScopeRestriction(column, metadata)` (sensitive, explicit `Clear`, not permitted) for D-9. `HasExplicitClear` unchanged. `Unmasked` (V-3) is covered because the copy keeps `ScopeClearance` (AC-9, OQ-3).
5. **D-5 Resolver.** `ConsentResolutionService(IRowFilterSqlBuilder? sqlBuilder = null, IRestrictedScopePolicy? restrictedScopes = null)`, `null` -> `RestrictedScopePolicy.Default` (V-1). In `ResolveAccess` after rule 3 (`:98-111`): `held = policy.HeldBy(callerScopes)`; empty -> no clearance. Otherwise `ClearedColumns` = referenced columns for which, for every `s` in `held`, the grouping of rule 3 supplies a rule with `AccessLevel == Clear` whose `ApplicableScopes` contains `s` (unconstrained consents: any; row-constrained: every group, any consent inside; consents without column rules and Deny-effect consents never supply; R-6). Scope comparison in rules stays as `RuleAppliesToScopes` (`:238-240`: raw, case-insensitive), so a rule written `api://x/Agent.Read` matches nowhere (safe, S-7). On any exception inside this step the clearance is `ClearedColumns = {}` (S-1). Interface `IConsentResolutionService` unchanged.
6. **D-6 Cache.** `CachedConsentEnvelope` appends (MemoryPack layout, last): `int RestrictionLayout`, `string? RestrictionFingerprint`, `List<string>? RestrictedScopesHeld`, `List<string>? ScopeClearedColumns`. `ToDecision(string expectedFingerprint)` returns `null` (cache miss) when `RestrictionLayout != 1` or the fingerprint differs (V-5). `ConsentCacheService` ctor takes optional `IRestrictedScopePolicy` (`null` -> `Default`). L1 holds the record itself; no context-hash change.
7. **D-7 Rebuild sites.** `BuildMetricScopedFallbackDecision` sets `ScopeClearance = existing?.ScopeClearance`. The metrics fallback without resolver (`:174-196`) is replaced by a static `new ConsentResolutionService()` (secure default), removing the duplicated rule logic. `GovernedConnectorReader.cs:232-236` uses `decision.GetEffectiveColumnAccess`.
8. **D-8 Procedures (R-11).** In `ComputeSensitivityOnlyMask` (INC-1 code, V-6): return an empty set when `decision.ScopeClearance is not null`. No other procedure change.
9. **D-9 Explanation (Q-143-5, R-13).** `EffectiveTableAccessDto` appends `bool RestrictedScope = false` and `IReadOnlyList<string>? ScopeClearRequiredColumns = null` (columns with `IsMaskedByScopeRestriction`); no scope names are listed. `EffectiveAccessExplanationService` fills them in the column loop (`:119-135`). Deviation: `QueryAnalysisService` reports the level only, no reason text (its levels come from `CatalogColumnAccessResolver` without per-column reasons); recorded for Phase 3.
10. **D-10 Analysis (R-14).** `QueryAnalysisService` gets optional `IRestrictedScopePolicy` (`null` -> `Default`); for an impersonated target `ResolveTableColumnLevels` adds the evaluation `policy.Scopes` when non-empty (V-9).
11. **D-11 Unchanged on purpose.** `TokenAccessScope.GetScopes` (V-7), CDC and Iceberg scope-blind resolution (G-5), `McpDatasetCatalog.cs`, `CatalogColumnAccessResolver.cs` (consumes `GetEffectiveColumnAccess`), value-profile and sample services (consume resolver levels), `ConsentColumnSnapshot` (consent authoring defaults).

## 4. Work packages (one PR, sequential)

**WP-143-1 Core (M).** D-1..D-6. Files: options, validator, policy, `ScopeNames`, `EntraTokenPolicy.cs`, `TableAccessDecision.cs`, new `RestrictedScopeClearance.cs`, `ConsentResolutionService.cs`, `CachedConsentEnvelope.cs`, `ConsentCacheService.cs`, DI. Tests (new `tests/Autheris.Tests.Unit/Governance/Issue143RestrictedScopeTests.cs` unless noted):
- `BaseClearOnly_RestrictedToken_Masks_UnrestrictedAndScopeless_Clear` (AC-1); `BaseAndScopeClear_BothTokensClear` (AC-2)
- `MultiScope_RestrictedWins` with cases of AC-3; `MonotonicHardening_Unchanged` (AC-4 incl. scope-only `Deny` for others); `NonSensitiveColumn_Unchanged` (AC-5)
- `RowGroups_EveryGroupMustSupplyScopeClear` (AC-6); `ConsentWithoutColumnRules_DoesNotSupplyScopeClear`; `DenyEffectConsent_DoesNotSupply`
- `RuleWithPrefixedScope_DoesNotClear`; `Restriction_NeverRaises_NorTouchesDeny` (S-3); `ClearanceFailure_MasksAllSensitive` (S-1, fault-injected)
- `UnmaskedProfile_RestrictedTokenStillMasked` (AC-9, `TableAccessPolicy` level); `DecisionCopies_KeepClearance` (ABAC merge, `WithAdditionalRowPolicy`, procedure `with`)
- `tests/.../OptionsBinding/RestrictedScopeOptionsBindingTests.cs`: default list, `api://app/Agent.Read` matches `Agent.Read`, blank/space entry fails startup, `[]` keeps default with warning, `Enabled=false` disables (AC-11); `ParameterlessResolver_UsesDefaultList`
- `CachedConsentEnvelopeTests`: `RoundTrip_KeepsClearance`, `LegacyLayout_IsMiss`, `FingerprintMismatch_IsMiss`; `DecisionCache_RestrictedAndUnrestrictedTokens_NeverShare` (AC-12)
- Existing suites (`ConsentResolutionTests`, `Issue116...`, `Issue127ScopeAwareLevelTests`) stay green unchanged except fixtures that use `Agent.Read` tokens with base-only `Clear` on sensitive columns: those assertions change to `Mask` (intended behaviour change; list them in the PR).

**WP-143-2 Consumers and parity (S-M).** D-7..D-10. Files: `GovernedAggregationService.ValidationAndExecution.cs`, `SemanticMetricQueryService.ConsentAndEvaluation.cs`, `GovernedConnectorReader.cs`, `GovernedProcedureExecutionService.cs`, `EffectiveTableAccessDto.cs`, `EffectiveAccessExplanationService.cs`, `QueryAnalysisService(.Evaluation).cs`. Tests:
- `ProcedureClearedColumns_RestrictedToken_StaysMasked` / `_UnrestrictedToken_ContractLift` (AC-10)
- `AggregationFallback_KeepsClearance`; `MetricWithoutResolver_RestrictedMasked`; `ConnectorReader_RestrictedMasked`
- `ExplainMyAccess_ReportsRestrictedScopeAndColumns`; `ImpersonatedAnalysis_PredictsMask` (AC-13)
- `FilterOrGroupOnMaskedColumn_RejectedForRestrictedOnly` (AC-8)
- Parity (AC-7), one parameterised integration test `tests/Autheris.Tests.Integration/Governance/Issue143RestrictedScopeParityTests.cs` over GraphQL list and aggregate, WebSQL, REST, OData, export, MCP `query_*`, `describe_dataset` (profile suppressed, no sample values), OLAP, governed aggregation, semantic metric, `describe_my_access`, analysis.
- Architecture test `ResolveAccessCallers_UseClaimsScopeOverload` (Cecil, style of `ConsentQueryTenantArchitectureTests`): no caller of `IConsentResolutionService.ResolveAccess` references `TokenAccessScope.GetScopes`/`GetScopeSet(ClaimsPrincipal)` (S-5).

**Total: M.** Fail closed throughout: unknown clearance = mask; config errors stop startup; foreign or legacy cache entries are misses.

## 5. Sequencing and parallelisation

**(a) Shared files.**
- Chunk 1 / 1.6 (`fix/procedure-inc1-security`, inside `release/chunk1-consolidated`): `GovernedProcedureExecutionService.cs` (+468 lines; D-8 edits its new `ComputeSensitivityOnlyMask`).
- #158 INC-0 (in `release/chunk1-consolidated`): `ConsentResolutionService.cs` (new `IsApplicable`, `:31-33`, `:313-326`; D-5 edits `:98-131`, textual neighbours) and `EffectiveAccessExplanationService.cs` (D-9).
- I-25 #129 (lane A, `i25-caller-attrs`): `GatewayGovernanceServiceExtensions.cs` (+38 lines; D-1 adds two registrations), `ConsentResolutionService.cs` (via the stack); per `PLAN-GOV-129` it will rewrite `WithTypedConsentPolicy` in `TableAccessPolicy.Consent.cs:105-125` - this plan does not edit that file (AC-9 is a test only).
- I-3 #107 WP-C (chunk 1): epochs and `RlsOptions.cs` only; no overlap found.
- Lane B (I-4 now, then I-7 #111 and #138 INC-1): `McpDatasetCatalog.cs` and catalog visibility; **no shared file** (V-1 avoids the MCP ctors). I-15 later edits `QueryAnalysisService.Sql.cs` (different partial from D-10).
- Lane C (#151): no shared file (no DDL here).
- Added by section 8 (A-1): `StreamRlsPolicyEnforcer.cs` is also edited by I-25 #129 WP-5 (`:134,245-247,305-342`), a second hand-merge point with I-25; `IcebergRestCatalogFederationService.cs` (Extensions) is touched by no open branch.
- Procedure INC-2 (`PLAN-PROC-152-INC2`): `GovernedProcedureExecutionService.cs` - must not run concurrently.

**(b) Hard dependencies.** 1.6 (procedure INC-1) and #158 INC-0, both in `release/chunk1-consolidated` (D-8 and D-9 edit their code). Phase 3 of this plan.

**(c) Recommended position.** Chunk 1 tail: branch `feat/issue-143-restricted-scopes` from the chunk-1 head that contains 1.6 and #158 (`release/chunk1-consolidated` or later), after I-25 is merged into that head or in parallel with it (hand-merge the DI registrations), and serialised with procedure INC-2 (either order; the second rebases `GovernedProcedureExecutionService.cs`). It can run in parallel with lanes B and C and with every chunk 2-6 step. WP-143-1 alone has no procedure or explanation edit and could start earlier from `main` if needed; WP-143-2 then waits for the chunk-1 head.

## 6. Owner questions

None blocking. For confirmation at PR (defaults taken): config key `Gateway:Governance:RestrictedScopes:{Enabled,Scopes}` instead of `Governance:RestrictedScopes` (D-1); analysis shows the level without a reason text (D-9).

## 7. Phase 3 security review

**Reviewer:** `csharp-security-expert` · **Date:** 2026-10-17 · **Verified against:** `origin/main`, `origin/release/chunk1-consolidated` (ConsentResolutionService), working tree of lane D.
**Verdict: APPROVED WITH CONDITIONS.** The single-choke-point design (`GetEffectiveColumnAccess` plus a decision-carried `ScopeClearance`) is sound: consumers already route through it (planner, SQL rewriter, tree compiler, mask policy factory, `MaskedColumnSet`, lakehouse executors, CDC cursors), `with` copies keep init properties, profile `Unmasked` writes `Clear` into `ColumnAccess` but the D-4 check still masks. Gaps below are binding.

**Verified facts**
- F-1 `IcebergRestCatalogFederationService.cs:300` and `StreamRlsPolicyEnforcer.cs:337` call `ResolveAccess` without `callerScopes` (stream cache key built without scopes at `:319`). A restricted token is therefore resolved as scopeless = unrestricted there. G-5 ("scope-blind stays") would leave a read path that releases base-`Clear` sensitive columns to `Agent.Read`.
- F-2 `RuleAppliesToScopes` (`ConsentResolutionService.cs:238`) compares raw caller scopes; D-5 computes `held` as bare names. Mixing both can produce a `Clear` for a rule that the existing resolver never applied.
- F-3 `CachedConsentEnvelope.ToDecision()` has two production callers (`ConsentCacheService.cs:165,302`) and five test callers; a forgotten call site would drop the restriction on an L2 hit.
- F-4 `TableAccessQuery.Claims` is optional; `UnifiedPolicyDecisionPoint` (`:76`) takes claims from `SecurityContext`. A path that rebuilds the query without `scp` silently becomes unrestricted.
- F-5 `GetScopes` reads `scp` only; the claims overload also reads `scope`. D-7/S-5 reasoning confirmed; do not widen `GetScopes`.

**Binding conditions**
- SEC-GOV143-01 Fail closed on every error: an exception anywhere in the restriction step, including `HeldBy` and scope extraction, must yield a decision that masks every catalog-sensitive column (clearance with a non-empty held marker and empty `ClearedColumns`), or a deny. Never `ScopeClearance = null` on error. Log the type only.
- SEC-GOV143-02 No scope-blind read path may release sensitive columns to a restricted holder. Pass `GetScopeSet(claims)` to `ResolveAccess` in the Iceberg service (`:300`) and the stream enforcer (`:337`, include scopes in `contextHash` there) and in any CDC path that resolves without claims. Where claims are unavailable, treat the caller as restricted (empty clearance) rather than unrestricted. This amends G-5 for restricted scopes only; other scope-blind semantics stay unchanged.
- SEC-GOV143-03 `ClearedColumns` may only contain columns whose base level in the same decision is `Clear` (never raises, never touches `Deny`/`Mask`), and a rule supplies scope `Clear` for restricted `s` only if it also passes `RuleAppliesToScopes` against the raw caller scopes and its `ApplicableScopes` entry (raw, ordinal-ignore-case) is held by the caller and normalises to `s`. A bare rule against a prefixed token therefore does not clear (documented, fail closed).
- SEC-GOV143-04 Rolling-deploy cache safety: every envelope (restricted or not) carries `RestrictionLayout=1` and the fingerprint; `ToDecision()` without fingerprint is removed (compile error for stale callers) and both L2 read paths treat a null result as a miss with `RemoveKeyFromTableIndex`. L1 promotion happens only after the check. A restricted decision must never be stored under, or read from, a key lacking the scope set.
- SEC-GOV143-05 Normalisation: `ToBareName` is applied identically to config entries and token scopes, ordinal-ignore-case, trims nothing implicitly, and an empty bare name never matches. Prefixed foreign-resource scopes matching a restricted bare name is accepted (restrictive direction).
- SEC-GOV143-06 Config: outside Development `Enabled=false` must fail startup (no silent global off); a non-empty `Scopes` that omits `Agent.Read` logs a warning with the effective names; empty/`null` list keeps the default. `ValidateOnStart` mandatory; no `IOptionsMonitor`.
- SEC-GOV143-07 Rebuild sites: `BuildMetricScopedFallbackDecision` carries `existing?.ScopeClearance`; when `existing` is null the sensitive columns stay `Mask` (verified) and the test must pin it. The metrics fallback replacement (D-7) must pass the claims scope set into the static resolver and keep row filters at least as strict as before; add a test that it does not allow a table the old branch denied.
- SEC-GOV143-08 Procedures: D-8 lift set is empty when `ScopeClearance` is non-null; the rule must hold for all procedure paths including nested/async result masking. `cleared_columns` never turns a scope-restricted `Mask` into `Clear`.
- SEC-GOV143-09 Explain/describe: `RestrictedScope` and `ScopeClearRequiredColumns` list only non-`Deny` columns of the subject's own decision, no scope names, no rule or consent ids; they are served only under the #158 INC-0 gate (subject self or authorised admin). `describe_dataset` for a restricted caller shows no value profile or samples for a scope-masked column.
- SEC-GOV143-10 Scope extraction: `GetScopes` unchanged (D-7 app-only tokens); app-only tokens (no `scp`, roles only) are unrestricted by design but must be pinned by a test so a later widening is caught. `SecurityContext.Claims` for async/export/UnifiedPolicyDecisionPoint must retain `scp` (test).
- SEC-GOV143-11 DoS bound: the held set is intersected with the configured list (max list size 64 enforced in the validator), so envelope and decision size do not grow with attacker-supplied scope counts; `ClearedColumns` is bounded by table columns.
- SEC-GOV143-12 Analysis/impersonation (D-10): the extra evaluation uses the restricted list only when `Enabled`; result is the minimum across evaluations; no change to impersonation authorisation.

**Mandatory security tests** (all `[Trait("Category","Security")]`)
- T-GOV143-01 `ResolutionFault_MasksAllSensitive_NeverNullClearance` (SEC-01)
- T-GOV143-02 `Iceberg_RestrictedToken_NoRawVending_ForSensitiveColumns` (SEC-02)
- T-GOV143-03 `StreamEnforcer_RestrictedSubscriber_MasksSensitive_CacheKeyHasScopes` (SEC-02)
- T-GOV143-04 `ClearedColumns_NeverRaisesDenyOrMask` (SEC-03)
- T-GOV143-05 `PrefixedTokenScope_BareRule_DoesNotClear_PrefixedRule_NotApplied` (SEC-03/05)
- T-GOV143-06 `MultiScope_OneScopeWithoutClear_Masks` (AND semantics, two restricted scopes)
- T-GOV143-07 `Envelope_LegacyLayoutAndForeignFingerprint_AreMiss_L1AndL2Paths` (SEC-04, both read paths)
- T-GOV143-08 `Envelope_RestrictedKeyNeverServedToScopelessCaller` (SEC-04, AC-12)
- T-GOV143-09 `Normalisation_CaseAndPrefix_MatchesRestricted_EmptyBareNeverMatches` (SEC-05)
- T-GOV143-10 `Options_DisabledOutsideDevelopment_FailsStartup_ListWithoutAgentRead_Warns_OversizedListFails` (SEC-06/11)
- T-GOV143-11 `AggregationFallback_ExistingNull_SensitiveMasked_ExistingClearance_Kept` (SEC-07)
- T-GOV143-12 `MetricFallback_DoesNotWiden_VsLegacyBranch` (SEC-07)
- T-GOV143-13 `Procedure_ClearedColumns_RestrictedToken_NoLift_AllPaths` (SEC-08)
- T-GOV143-14 `Explain_RestrictedFields_NoDenyColumns_NoScopeNames_NonSubjectForbidden` (SEC-09)
- T-GOV143-15 `AppOnlyToken_Unrestricted_GetScopesUnchanged` and `SecurityContextClaims_KeepScp_ForAsyncExport` (SEC-10)
- T-GOV143-16 `ImpersonatedAnalysis_EnabledFalse_NoExtraEvaluation_EnabledTrue_Minimum` (SEC-12)
- T-GOV143-17 `ProfileUnmasked_And_AbacMerge_RestrictedStillMasked_Sql_GraphQl_WebSql_Mcp_Olap_Export` (AC-9, parity)

Test ID range: T-GOV143-01..17 (17 tests; plan tests T-names of WP-143-1/2 remain additional).

**Owner questions (recommended default)**
- OQ-A `Enabled=false` outside Development: reject at startup (default) vs allow with warning.
- OQ-B Extend the restriction to Iceberg and stream paths (default: yes, SEC-02) vs keep G-5 scope-blind and document the residual risk.
- OQ-C A restricted `Scopes` list that drops `Agent.Read`: warning only (default) vs reject.

## 8. Architect addendum (post Phase 3)

Folds in the structural conditions SEC-GOV143-02 and SEC-GOV143-03. Section 7 still binds; where this section and D-5 or D-11 disagree, this section wins.

1. **A-1 Scope-blind paths (SEC-GOV143-02; amends G-5 and D-11).** Two edits are added to WP-143-2:
   - `IcebergRestCatalogFederationService.cs:300` (`src/Autheris.Extensions/Lakehouse/Services/`): the principal is already in scope (`:286-288`), so pass `TokenAccessScope.GetScopeSet(principal.Claims)` (`null` when empty) as `callerScopes`.
   - `StreamRlsPolicyEnforcer.cs`: thread the subscriber's scope set (from the `ClaimsPrincipal subscriber` at `:74`, captured at subscribe time) through the call at `:134` into `ResolveConsentDecisionAsync` (`:305-342`). Include it in `ComputeSubjectContextHash(groupSids, roles, scopes)` at `:319` and pass it to `ResolveAccess` at `:337`.
   - Any caller that resolves without claims while the policy is enabled sets `decision with { ScopeClearance = RestrictedScopeClearance.Unknown(policy) }` (held = the whole list, `ClearedColumns = {}`). The caller is then treated as restricted, so every sensitive column is masked. This is a post-step on the decision, so the `IConsentResolutionService` signature stays the same.
   - G-5 is amended for restricted scopes only; the other scope-blind semantics of the CDC and Iceberg paths stay as they are.
   - Tests: `IcebergTable_RestrictedToken_SensitiveMasked`, `StreamEvent_RestrictedSubscriber_SensitiveMasked`, `StreamDecisionCache_RestrictedAndUnrestrictedSubscribers_NeverShare`, `ResolveWithoutClaims_PolicyEnabled_TreatedAsRestricted`. The architecture test of WP-143-2 is extended: every production `ResolveAccess` call either passes a scope set from `GetScopeSet(IEnumerable<Claim>)` or applies `RestrictedScopeClearance.Unknown`.
2. **A-2 Rule matching (SEC-GOV143-03; replaces the matching sentence of D-5; resolves F-2).**
   - `held = policy.HeldBy(callerScopes)`: each raw token scope is normalised with `ScopeNames.ToBareName` and compared with the bare list. This only decides who is restricted.
   - A rule `r` supplies a scope `Clear` for a held `s` only when all of these hold:
     - (i) `r.AccessLevel == Clear`;
     - (ii) `RuleAppliesToScopes(r, callerScopes)` is true against the raw caller scopes (`ConsentResolutionService.cs:238-240`);
     - (iii) some entry `a` in `r.ApplicableScopes` is in the raw caller set (ordinal-ignore-case) and `ToBareName(a)` equals `s`.
   - Then `ClearedColumns` is intersected with the columns whose level in the same decision is `Clear`. The step can never raise a level and never touches `Deny` or `Mask`.
   - Effect: a rule only clears a column for a scope written exactly as the token carries it. A bare rule `Agent.Read` against a prefixed token scope `api://x/Agent.Read` makes the caller restricted but does not clear the column. This is fail closed and documented as residual risk S-7.
   - Tests: `PrefixedTokenScope_BareRule_DoesNotClear`, `ScopeClear_OnBaseMaskColumn_NotInClearedColumns`, `ClearedColumns_SubsetOfClearLevels` (property test over random rule sets).
3. **A-3 Size and position.** WP-143-2 stays S-M and the total stays M. The dependencies in 5(b) are unchanged. In 5(c), coordinate with I-25 on two files (DI registrations and `StreamRlsPolicyEnforcer.cs`). Preferred order: #143 after I-25 is in the chunk-1 head, so that only #143 rebases.

### 8.1 Phase 3 re-check of addendum

**Final verdict: APPROVED WITH CONDITIONS** (section 7 conditions SEC-GOV143-01..12 stay binding). A-1 satisfies SEC-02 and A-2 satisfies SEC-03 (clauses i-iii plus the intersection with `Clear` levels match the condition); G-5 is amended for restricted scopes only, as required. New conditions:
- SEC-GOV143-13 `RestrictedScopeClearance.Unknown` applies only when the claims argument is null (absent), never when claims exist without `scp` (scopeless and app-only callers stay unrestricted, SEC-10). Scopes captured at stream subscribe time must not outlive the token: the stream drops or re-resolves when the subscription outlives token expiry, or the cached scope set is re-read on token refresh.
- SEC-GOV143-14 The `Unknown` post-step is applied after cache read and after every `with` rebuild, never stored under a scope-bearing key, and is part of the architecture test (every production `ResolveAccess` caller passes a claims scope set or applies `Unknown`; a new caller fails the build).
- SEC-GOV143-15 Iceberg `callerScopes` uses the claims overload only (`GetScopeSet(IEnumerable<Claim>)`), never `GetScopes`; empty maps to `null` via `ForResolver`.

Mandatory tests (`[Trait("Category","Security")]`), range T-GOV143-18..21:
- T-GOV143-18 `Unknown_OnlyWhenClaimsNull_ClaimsWithoutScp_StayUnrestricted`
- T-GOV143-19 `StreamSubscription_ScopeSetNotUsedAfterTokenExpiry`
- T-GOV143-20 `UnknownClearance_SurvivesCacheHitAndDecisionCopies`
- T-GOV143-21 `ArchitectureTest_EveryResolveAccessCaller_PassesClaimsScopesOrUnknown`
(The addendum's own tests `IcebergTable_RestrictedToken_SensitiveMasked`, `StreamEvent_RestrictedSubscriber_SensitiveMasked`, `StreamDecisionCache_...NeverShare`, `ResolveWithoutClaims_PolicyEnabled_TreatedAsRestricted`, `PrefixedTokenScope_BareRule_DoesNotClear`, `ScopeClear_OnBaseMaskColumn_NotInClearedColumns`, `ClearedColumns_SubsetOfClearLevels` are accepted as covering T-GOV143-02/03/05/04.) Total security test range: T-GOV143-01..21.

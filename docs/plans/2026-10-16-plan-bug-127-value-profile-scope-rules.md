# Implementation Plan: REQ-BUG-127 - Scope-Aware Column Levels for Value Profiles, Synthetic Samples, Query Analysis and Catalog Visibility

**Document ID:** `PLAN-BUG-127` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-BUG-127](2026-10-16-req-bug-127-value-profile-scope-rules.md) (FR-127-1..6, AC-127-1..10, Q-127-1, Q-127-2)
**Branch:** `fix/issue-127-profile-scope-levels` (suggested by the PRD; base `main`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Status:** PROPOSED (Phase 2 complete; next: Phase 3 security review, then Phase 4 TDD)

**Binding project rules applied:** fail closed; no query text or row values in logs or audit; no schema change, no migration, no new configuration, no new endpoint; the data path (`ConsentResolutionService`, `TableAccessDecision`) is **not** modified; English only.

---

## 1. Verification of the PRD Against the Code

All references are to the worktree at the time of writing.

| PRD claim | Result |
|---|---|
| `ComputeTableColumnAccessAndFilters` is scope-blind | **Confirmed.** `src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs:399-463`: signature `(TableMetadata, IReadOnlyList<Consent>, bool isAdmin)`; callers `:198` (describe) and `:539` (synthetic sample). It is `private static`. |
| Base `Clear` + scope-A `Mask` yields `Clear` in the catalog | **Confirmed.** `:437-444` `hasExplicitClear` is true for any non-scope-specific `Clear` rule (it does not look at scope hardening); `:446-449` `hasExplicitMask` is true but only decides when `!hasExplicitClear`; `:451-459` returns `Clear`. |
| The data path hardens via scopes | **Confirmed.** `GetConsentColumnLevel` (`src/Autheris.Application/Services/ConsentResolutionService.cs:241-300`): base rules give the level (max over base rules, `:268-274`), then every scope rule whose scopes the caller holds can only lower it (`:275-285`, monotonic hardening); without a base rule the column is `Deny` unless a matching scope rule exists (`:287-299`). Entry points: `ResolveAccess(..., callerScopes)` (`:19-25`) and the internal static `ResolveConsentColumnLevel` (`:234`). |
| Profile leak path | **Confirmed.** `McpDatasetCatalog.cs:214-221` passes the level to `ColumnValueProfileService.GetColumnProfile` (`src/Autheris.Application/Catalog/Profiling/ColumnValueProfileService.cs:203-260`): `Deny` returns `null` (`:210-213`), `IsColumnSensitiveOrMasked` strips min, max, allowed values, sample values, distinct bucket (`:240-252`). |
| Synthetic rows use the same level | **Confirmed.** `McpDatasetCatalog.cs:539-541` passes `columnAccessLevels` to `SyntheticSampleRowGenerator.GenerateRows` (`src/Autheris.Application/Catalog/Profiling/SyntheticSampleRowGenerator.cs:19-35`), which feeds them to `GetDatasetProfiles` and reads the level per column (`:47-49`). No change needed inside the generator or the profile service. |
| Real sample rows unaffected | **Confirmed**, they run through `IGatewayExecutionService` (scope-aware). |
| `QueryAnalysisService` has its own scope-blind copy | **Confirmed.** `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Evaluation.cs:255-310` (`private static ResolveEffectiveColumnAccess`), used at `:119` (output columns, `READ_CLEARTEXT` suggestion at `:130-137`) and `:166` (filter/join columns, `MaskedColumnInFilter` at `:172-187`). |
| `CatalogVisibility.FilterForSubject` is scope-blind but fail closed | **Confirmed.** `src/Autheris.Application/Services/CatalogVisibility.cs:59-150`: `explicitlyGrantedColumns` drops scope-specific rules (`:112-117`), `deniedColumns` counts every `Deny` of any scope (`:119-123`). Callers: `CatalogVisibility.VisibleTablesAsync` (`:259`, reached by `McpCatalogVisibility.VisibleTablesAsync`, `src/Autheris.Application/Mcp/Services/McpCatalogVisibility.cs:36-37`, with the principal in hand) and `src/Autheris.GraphQL/Types/QueryTypes.cs:444` (variable `principal` in scope, `:395-444`). |

### 1.1 Findings that refine the PRD

1. **The right oracle is `TableAccessDecision.GetEffectiveColumnAccess`, not `ResolveAccess(...).ColumnAccess` alone.** The consent resolver only fills `ColumnAccess` for columns that a rule mentions (`ConsentResolutionService.cs:99-117`); an unmentioned column is answered by `GetColumnAccess` through `HasUnconstrainedColumnAllow` (`src/Autheris.Domain/Interfaces/TableAccessDecision.cs:396-403`), and catalog-sensitive columns (`IsSensitive` or a `ColumnMaskingRules` entry) are masked unless the **resolved** level is an explicit `Clear` (`GetEffectiveColumnAccess`, `:413-436`; `HasExplicitClear` `:405-406` reads the resolved level, so a base `Clear` hardened to `Mask` is not an explicit clear). That method is what every data-path caller uses (`GovernedAggregationService.ValidationAndExecution.cs:154,202,250,288`, `ConnectorSecurityPolicyEvaluator.cs:62`). The catalog must call it, otherwise a "fifth" combination of resolver output and sensitivity would appear.
2. **`ResolveAccess` returns a denied decision with an empty `ColumnAccess` when the caller has no applicable Allow consent** (`ConsentResolutionService.cs:46-56`). Today the catalog shows a baseline profile (Clear, or Mask for sensitive columns) to such a caller when the owner set `ReleaseProfileToCatalogViewers` (`McpDatasetCatalog.cs:192-195`, `ComputeAccessStatus` returns `NONE`). Naively replacing the computation would turn that into `Deny` for every column and silently remove a documented feature. The design must keep the baseline for the not-granted case (section 3, step 3).
3. **`ResolveAccess` re-filters consents by grantee** (`IsSubjectMatch` -> `GranteeMatcher.Matches`, `ConsentResolutionService.cs:309-316`) and by `IsActive(now)` (`:32`). The caller must therefore pass the principal's own SID, group SIDs and roles (the same values that loaded the consents, `McpDatasetCatalog.cs:~455-462`, `QueryAnalysisService.cs:460-471`). A consent matched through a role or `allUserSids` still resolves, because the same matcher decides.
4. **The catalog `DENY` masking rule (`ColumnMaskingRules[col].RuleType == "DENY"`) is not part of the resolver.** The catalog and the analysis treat it as `Deny` today (`McpDatasetCatalog.cs:429-433`, `QueryAnalysisService.Evaluation.cs:278-283`, `CatalogVisibility.cs:125-132`). That rule is stricter than the data-path decision object and is kept as an additive layer (it can only lower a level). The parity test excludes it by construction (fixtures without a `DENY` catalog rule) and asserts the layer separately.
5. **Access profiles can relax the data path** (`TableAccessPolicy.Consent.cs:127-160`, `MaskingPolicyMode.Unmasked` turns `Mask` into `Clear`). The catalog does not model access profiles today and will not: it can only be stricter than the data path (acceptable, documented limit; no new leak).
6. `McpCatalogVisibility.cs` needs **no change**: scopes are derived inside `CatalogVisibility.VisibleTablesAsync` from the principal. The PRD file list shrinks by one file.

**Root cause: confirmed.** `ComputeTableColumnAccessAndFilters` and `QueryAnalysisService.ResolveEffectiveColumnAccess` are independent, scope-blind re-implementations of the column level rules (copy 3 and 4 beside `ConsentResolutionService` and `CatalogVisibility`). They were not updated when scope-specific rules (#26) and the monotonic hardening (#116) were added to the resolver. Only the data path tightens by scope.

---

## 2. Design Decisions

### D-1 Delegate to the shared resolver, delete the private level logic
`McpDatasetCatalog` and `QueryAnalysisService` stop computing levels themselves. They call `IConsentResolutionService.ResolveAccess(userSid, groupSids, roles, table.Identifier, activeConsents, table.Dialect, allUserSids: null, callerScopes)` and read the level per column through `decision.GetEffectiveColumnAccess(column, table)`. No rule (base, scope, row-group, Deny-effect `Mask`) is re-expressed outside `ConsentResolutionService` and `TableAccessDecision`. The `hasExplicitClear` / `hasExplicitMask` / `isExplicitlyDenied` blocks are removed.

### D-2 Scopes are threaded from the principal at the public entry point
`IReadOnlySet<string> callerScopes = TokenAccessScope.GetScopeSet(principal)` (`src/Autheris.Domain/Security/TokenAccessScope.cs:44-48`, reference implementation: `EffectiveAccessExplanationService.cs:65`). Passed to the resolver as `scopes.Count > 0 ? scopes : null`, exactly as the data path does (`TableAccessPolicy.Consent.cs:36,72-80`), so a token without scope claim behaves as today (AC-127-10) and a scope-only rule stays `Deny` (#116).

### D-3 Injection: optional `IConsentResolutionService`, defaulted to a real instance (deviation from PRD section 6 step 2)
The PRD proposed "fall back to all `Mask` when the service is absent". That would break the many unit constructions of `McpDatasetCatalog` and `QueryAnalysisService` that do not pass the service. Better: an optional constructor parameter `IConsentResolutionService? consentResolution = null`, resolved as `consentResolution ?? new ConsentResolutionService()`. `ConsentResolutionService` is a stateless pure class (only a default `RowFilterSqlBuilder`, `ConsentResolutionService.cs:10-15`), so the default is the production behaviour, never a weaker one. Production DI already registers it as singleton (`GatewayGovernanceServiceExtensions.cs:139`); both services are built through `ActivatorUtilities`, so the new optional parameter is picked up without registration changes. The parameter is added to both `McpDatasetCatalog` constructors (`:52-65` with `[ActivatorUtilitiesConstructor]`, `:82-93`) and both `QueryAnalysisService` constructors (`:35-44`, `:60-67`), appended last to keep positional callers compiling.

### D-4 Not-granted callers keep the baseline (feature `ReleaseProfileToCatalogViewers`)
`describe_dataset` computes levels only when `decision.IsAllowed`. When the decision is denied (no applicable Allow consent, hard table Deny, MCP auth bypass without SID), the level per column is the **catalog baseline**: `TableAccessDecision.Allowed(table.Identifier, empty, null, hasUnconstrainedColumnAllow: true).GetEffectiveColumnAccess(...)`, i.e. `Clear`, or `Mask` for `IsSensitive` / `ColumnMaskingRules` columns, which is today's result for a caller without consents. Reuse of `GetEffectiveColumnAccess` keeps even this baseline out of private logic. A profile for the not-granted case is only built when `canViewProfile` (`:187-188`), unchanged. The catalog `DENY` layer (finding 4) still applies in both branches.

### D-5 Fail closed on any resolution error
The level computation is wrapped: an exception, a `null` decision (the interface allows it, see `TableAccessPolicy.Consent.cs:82-85`), or an undefined enum value yields `Mask` for every column of the table (never `Clear`), one `LogWarning` without values (table identifier only, no column values, no scope values). Admins (`GovernanceAdmin`, `ClusterAdmin`) keep `Clear` as before (no consents are loaded for them, `McpDatasetCatalog.cs:462-466`).

### D-6 `QueryAnalysisService`: effective-principal scope rule (resolves Q-127-1 as recommended)
`ResolveEffectiveColumnAccess` becomes an instance method. Scope set: if `targetPrincipal == null` or the target SID equals the caller SID, `GetScopeSet(callerPrincipal)`; otherwise the **empty set** (the target's token is unknown). Owner and admin short-circuit (`Clear`) stays. The decision is resolved **once per table** (not per column) in `EvaluateTableAndColumnsAsync` and handed to the two column loops (`:119`, `:166`); the grant test `hasTableGrant` (`ComputeAccessStatus`) stays for the table-level missing-consent suggestion. When the impersonated-target case is taken, one diagnostic is added per analyzed query that states the limit (reusing the existing `QueryValidationDiagnostic` with a new info-level code in `QueryDiagnosticCodes`, message without values). Plumbing: the scope set is computed once in `AnalyzeQueriesAsync` (`QueryAnalysisService.cs:~98-113`) and passed down as an additional parameter of `EvaluateTableAndColumnsAsync` (call sites `QueryAnalysisService.Sql.cs:149`, `QueryAnalysisService.GraphQl.cs:73,152`).

### D-7 `CatalogVisibility`: scope-aware, still without a SID, still no fourth copy
`FilterForSubject` has no SID or groups, so it cannot call `ResolveAccess`. It keeps its table-level rules and replaces only the two column sets (`explicitlyGrantedColumns`, `deniedColumns`, `:109-134`) by a per-column predicate built from the shared single-consent function `ConsentResolutionService.ResolveConsentColumnLevel(consent, column, scopes)` (`:234`, same assembly):

- hidden when any Deny-effect consent has an **applicable** (scope-matching or unscoped) `Deny` rule for the column, or the catalog `DENY` masking rule applies (unchanged);
- otherwise visible when some Allow consent yields a level other than `Deny` for the caller scopes (this also covers "Allow consent without column rules" = `Clear`, so `hasUnconstrainedAllow` disappears).

New optional trailing parameter `IReadOnlySet<string>? callerScopes = null` (`null` = base rules only, which equals today's behaviour for base-rule data and keeps the fail-closed #116 behaviour for scope-only rules). `VisibleTablesAsync` (`:259`) passes `TokenAccessScope.GetScopeSet(principal)`; `QueryTypes.cs:444` passes `TokenAccessScope.GetScopeSet(principal)`. Known, accepted gap: the resolver takes the minimum over row-constrained groups, this predicate takes the maximum over Allow consents, so for the row-group case the catalog may **list** a column that the data path denies (names only, no values, existing behaviour). The property asserted is therefore "listed whenever the data path does not deny it" plus equality for single-consent and unconstrained cases (T-12). Making the listing exact needs a SID-free resolver entry point and is out of scope (open question O-3).

### D-8 Unchanged on purpose
`ColumnValueProfileService`, `SyntheticSampleRowGenerator`, `hasActiveRowFilters` computation (`McpDatasetCatalog.cs:410-415`, moved verbatim into a small helper), `ConsentResolutionService`, `TableAccessDecision`, `EffectiveAccessExplanationService`, the real `sample_rows` path. Q-127-2 (format/empty share on a `Mask` profile) stays out of scope.

---

## 3. Algorithm (catalog)

`McpDatasetCatalog.ResolveColumnLevels(ClaimsPrincipal principal, TableMetadata table, IReadOnlyList<Consent> activeConsents, bool isAdmin)` returns `IReadOnlyDictionary<string, ColumnAccessLevel>` and is the only producer of levels for describe and synthetic sample. Its output is complete (one entry per catalog column), so the fallback branch at `:214-219` is no longer reached (it stays as a defensive `Mask`-for-sensitive fallback, never `Clear` for a missing entry of a sensitive column).

1. `isAdmin` -> all `Clear`.
2. `scopes = TokenAccessScope.GetScopeSet(principal)`; `userSid = principal.GetUserSid()`; groups and roles from the principal.
3. `userSid == null` -> baseline decision (D-4). Else `decision = consentResolution.ResolveAccess(userSid, groups, roles, table.Identifier, activeConsents, table.Dialect, null, scopes.Count > 0 ? scopes : null)`; if `!decision.IsAllowed` -> baseline decision (D-4).
4. Per column: `level = decision.GetEffectiveColumnAccess(col.ColumnName, table)`; if the catalog `DENY` masking rule applies -> `Deny`; undefined enum -> `Deny` (for the profile: no profile, the conservative outcome).
5. Any exception -> all `Mask` + warning (D-5).

`hasActiveRowFilters` stays a separate pure helper (moved out of the old method, behaviour identical) so that the level producer carries no row-filter logic.

---

## 4. Changed Files and Ownership

One developer, one work package (section 7). Estimated production change about 130 LOC changed/added, about 120 LOC removed (the three private blocks).

| File | Change | Owner |
|---|---|---|
| `src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs` | New optional ctor parameter `IConsentResolutionService? consentResolution` (both ctors); `ComputeTableColumnAccessAndFilters` split into `ResolveColumnLevels` (D-1..D-5) and `HasActiveRowFilters` (verbatim logic); both call sites (`:198`, `:539`) updated; private level blocks deleted | Phase 4 developer |
| `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.cs` | Optional ctor parameter in both ctors; scope set computed once and passed down (D-6) | Phase 4 developer |
| `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Evaluation.cs` | `ResolveEffectiveColumnAccess` becomes instance, uses a per-table decision; new parameter on `EvaluateTableAndColumnsAsync`; impersonation diagnostic | Phase 4 developer |
| `src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Sql.cs`, `QueryAnalysisService.GraphQl.cs` | Pass the new scope parameter at three call sites | Phase 4 developer |
| `src/Autheris.Application/Governance/Analysis/QueryDiagnosticCodes` (file to be located by the developer; holds `MaskedColumnInFilter`) | One new informational code for "target scopes unknown" | Phase 4 developer |
| `src/Autheris.Application/Services/CatalogVisibility.cs` | `FilterForSubject` gets `callerScopes`; column predicate per D-7; `VisibleTablesAsync` passes scopes | Phase 4 developer |
| `src/Autheris.GraphQL/Types/QueryTypes.cs` | One argument at `:444` | Phase 4 developer |
| `tests/Autheris.Tests.Unit/Mcp/` (new `McpDatasetCatalogScopeLevelTests.cs`, new `ColumnLevelParityTests.cs`), `tests/Autheris.Tests.Unit/Governance/QueryAnalysisAndValueProfilingTests.cs` (extend), `CatalogVisibility` tests (existing file or new `CatalogVisibilityScopeTests.cs`) | Tests per section 6 | Phase 4 developer |

**Not touched:** `McpCatalogVisibility.cs`, `ConsentResolutionService.cs`, `TableAccessDecision.cs`, `ColumnValueProfileService.cs`, `SyntheticSampleRowGenerator.cs`, `EffectiveAccessExplanationService.cs`, DI registration files, docs under `docs/features/` (Phase 6 adds one sentence to the value-profile and catalog-transparency feature docs; no master-plan edit in this phase).

---

## 5. Interface Changes

All additive and source compatible (optional trailing parameters), no wire/API change.

```csharp
// McpDatasetCatalog (both constructors), appended last
IConsentResolutionService? consentResolution = null   // null => new ConsentResolutionService()

// QueryAnalysisService (both constructors), appended last
IConsentResolutionService? consentResolution = null

// CatalogVisibility
public static IReadOnlyList<TableMetadata> FilterForSubject(
    IEnumerable<TableMetadata> allTables, IEnumerable<Consent> activeConsents, TenantId tenantId,
    IReadOnlySet<string>? callerScopes = null);
```

Internal only: `McpDatasetCatalog.ResolveColumnLevels` (private), `QueryAnalysisService.ResolveEffectiveColumnAccess` (private instance), extra parameter on private `EvaluateTableAndColumnsAsync`. `FilterForSubject` keeps its three-argument call shape for existing tests.

---

## 6. Fail-Closed Behaviour

| Situation | Result |
|---|---|
| No scope claim | `callerScopes = null`: base rules only; scope-only rules `Deny` (#116) |
| Scope claim unreadable/malformed | Parsed set empty or partial, never a superset; only fewer scopes = base rules |
| Resolver throws or returns `null` | every column `Mask`, one warning, no values; the profile service strips all values for `Mask` |
| Undefined `ColumnAccessLevel` value | treated as `Deny` (the resolver already maps it, `ConsentResolutionService.cs:111,214,219`; the catalog adds the same guard on read) |
| No applicable Allow consent / hard Deny | baseline (D-4): `Clear`/`Mask` as today, only reachable with `ReleaseProfileToCatalogViewers` (guarded by `canViewProfile`) |
| Impersonated analysis target | no scopes; stricter for scope-only grants, documented limit for base+scope rules (diagnostic) |
| Admin | `Clear`, unchanged; MCP auth bypass unchanged |
| Catalog `DENY` masking rule | `Deny` in every branch (additive, never lowered) |

---

## 7. TDD Test List (red first)

Notation: base rule `B` (no scopes), scope rule `S(x)`. Profile fixtures carry sentinel strings (e.g. `SENTINEL_MIN_7f3a`) for min, max, allowed and sample values; assertions on the **serialized** MCP response (`JsonSerializer.Serialize(description)`), not only on the object.

**Red tests (must fail on current `main`):**

| # | Test | Covers |
|---|---|---|
| T-1 | `DescribeDataset_BaseClear_ScopeAMask_ScopeAToken_ProfileHasNoValues` | AC-127-1; sentinels absent from serialized JSON |
| T-2 | `DescribeDataset_BaseClear_ScopeAMask_ScopeBToken_And_NoScope_FullProfile` | AC-127-2 (guards against over-masking) |
| T-3 | `DescribeDataset_BaseClear_ScopeADeny_ScopeAToken_NullProfile_ScopeBToken_FullProfile` | AC-127-3 (today hidden for both) |
| T-4 | `DescribeDataset_ScopeOnlyClear_ScopeHolderFullProfile_OthersNoProfile` | AC-127-4 |
| T-5 | `SampleRows_Synthetic_BaseClear_ScopeAMask_ScopeAToken_NoProfileDerivedValues` | AC-127-6 |
| T-6 | `QueryAnalysis_BaseClear_ScopeAMask_ScopeAToken_ReportsMask_AndReadCleartextSuggestion`, and scope B reports `Clear` without suggestion | AC-127-7 |
| T-7 | `QueryAnalysis_ImpersonatedTarget_UsesNoScopes_AndEmitsDiagnostic` | D-6, Q-127-1 |
| T-8 | `CatalogVisibility_ScopeADeny_HidesColumnForScopeAOnly` and `ScopeOnlyAllow_ShowsColumnForScopeHolderOnly` | AC-127-8 |
| T-9 | `ColumnLevelParity_Catalog_Equals_DataPath` (theory, below) | FR-127-6, AC-127-5 |

**Green-first guard tests (must pass before and after; written first to freeze behaviour):**

| # | Test | Covers |
|---|---|---|
| T-10 | `DescribeDataset_NotGranted_ReleaseProfileToCatalogViewers_StillShowsBaselineProfile` (and sensitive column stays `Mask`) | D-4, finding 2 |
| T-11 | `DescribeDataset_Admin_And_AuthBypass_Unchanged_Clear`; tokens without scope claim unchanged | AC-127-9, AC-127-10 |
| T-12 | `CatalogVisibility_ListedWheneverDataPathDoesNotDeny` and equality for single-consent/unconstrained cases | D-7 |
| T-13 | `ResolutionException_YieldsMaskForAllColumns_NeverClear` (substitute `IConsentResolutionService` that throws / returns `null`) and logs no values | D-5 |
| T-14 | `CatalogDenyMaskingRule_StillDenies_InBothBranches` | finding 4 |
| T-15 | `McpDatasetCatalog_WithoutInjectedResolver_UsesRealResolver` (guards D-3: the default must not be weaker) | D-3 |

### Parity test (T-9)

Location `tests/Autheris.Tests.Unit/Mcp/ColumnLevelParityTests.cs`. Enumerates the cross product:

- base rule: none / `Clear` / `Mask` / `Deny`;
- scope rule: none / `S(A) Clear` / `S(A) Mask` / `S(A) Deny` (as a second rule of the same consent);
- caller scopes: `{A}` / `{B}` / `{}` / `{A,B}`;
- consents: one; two unfiltered; one row-filtered `Clear` plus one unfiltered `Mask`; two with different row filters;
- effect: Allow-only, and one Deny-effect consent carrying a `Mask` or `Deny` column rule;
- column: sensitive-in-catalog or not.

Oracle (the data path, real `ConsentResolutionService`, not a mock): `ResolveAccess(..., callerScopes).GetEffectiveColumnAccess(col, table)`. Assertions: (a) `McpDatasetCatalog` levels (observed through `describe_dataset` profile presence/absence on a sentinel profile, plus the extracted `ResolveColumnLevels` via `InternalsVisibleTo` if available) equal the oracle for every combination; (b) `QueryAnalysisService` `EffectiveAccessLevel` equals the oracle for the caller-is-target case; (c) `CatalogVisibility` obeys T-12. A second theory asserts the monotone safety property "catalog level is never more permissive than the oracle" over randomized combinations (FsCheck if the repo already uses it in `ConsentPropertyBasedTests.cs`, otherwise a seeded `Random`). Fixtures exclude the catalog `DENY` rule and access profiles (findings 4 and 5). The test is registered in the security regression catalog (PRD section 6).

---

## 8. Work Packages

| WP | Content | Size | Dependency |
|---|---|---|---|
| WP-127-A (single package, one developer) | Red tests T-1..T-9 and guard tests T-10..T-15; implementation D-1..D-7; remove the private level blocks; extend `docs` feature sentences are Phase 6, not here | S: about 130 LOC production added, about 120 removed, about 450 LOC tests | none |

Order inside the package: guard tests first (green), then red tests, then `McpDatasetCatalog`, then `QueryAnalysisService`, then `CatalogVisibility` + `QueryTypes.cs`. Per project rule, the lead compiles and runs tests once at the end (Release build with warnings as errors, `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj`); no integration test needed (no new data path).

---

## 9. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | Behaviour change for `ReleaseProfileToCatalogViewers` (not-granted callers) | D-4 baseline branch; guard test T-10 written first |
| R-2 | Unmentioned columns: resolver leaves them out of `ColumnAccess` | Always read through `GetEffectiveColumnAccess` (finding 1), parity matrix has a "no rule for the column" row |
| R-3 | Subject match differs from the repository's prior filtering (role/group grantee) | Pass the principal's SID/groups/roles; matrix includes a role-based grantee case; a mismatch can only result in `Deny`, never in `Clear` |
| R-4 | `ResolveAccess` cost per describe | One call per table and per request (not per column); the catalog already loads all consents of the subject; no caching introduced |
| R-5 | `CatalogVisibility` lists a column the data path denies for row-group consents | Names only, existing behaviour, documented (D-7, O-3) |
| R-6 | Existing tests that pin the old (wrong) levels | Expected: update only tests whose fixtures combine a base rule with a scope rule; any other failing test is a regression to investigate |
| R-7 | A second divergence later | Parity test T-9 is the regression gate; the three private copies are deleted so the surface cannot drift again |

---

## 10. Security Notes for Phase 3

The reviewer must check:

1. **No residual private level logic**: `grep` for `hasExplicitClear`, `hasExplicitMask`, `isExplicitlyDenied`, `IsScopeSpecific(` outside `ConsentResolutionService` and `CatalogVisibility` returns nothing in `McpDatasetCatalog.cs` and `QueryAnalysisService.Evaluation.cs`.
2. **Scope source**: scopes come only from `TokenAccessScope.GetScopeSet(principal)` (claims `scp`, mapped scope, `scope`); never from a request parameter, tool argument or the analyzed query. Confirm the `scope` claim handling difference between the two `GetScopeSet` overloads (`GetScopes` knows `scp` and the MS claim only, the claims overload also `scope`) does not make the catalog stricter-or-equal vs. `TableAccessPolicy.Consent.cs:36` (which uses the claims overload with `query.Claims`): the catalog should use the same overload as the data path (claims overload) or the difference must be shown harmless. This is the one place where a looser catalog result could still arise.
3. **Exception path**: a throwing or `null` resolver yields `Mask`, never `Clear`; no column values, consent content or scope values are logged.
4. **Baseline branch (D-4)** is reachable only with `canViewProfile` and cannot produce `Clear` for a column that is `Deny` for a granted caller (granted callers never take the baseline branch; `IsAllowed` decides).
5. **Deny-effect `Mask` path** (`maskedOnlyColumns`, `ConsentResolutionService.cs:60-75`) is covered by the parity matrix; verify a scope-bound Deny-effect `Mask` rule applies only to scope holders, as in the data path.
6. **Admin and bypass** short-circuits unchanged; impersonation in `QueryAnalysisService` cannot widen levels (no scopes means fewer hardening rules for base+scope rules, a documented, disclosed limit; the output is a prediction, not an access decision).
7. **Serialized output**: sentinel assertions on the MCP JSON for `describe_dataset` and synthetic sample rows; `formatPattern`, `emptyShare`, `typicalLength` on a `Mask` profile remain (Q-127-2, owner decision).
8. **Catalog `DENY` rule layer** and the access-profile limit (findings 4 and 5) only make the catalog stricter than the data path.
9. **No new data flow, endpoint, configuration or persistence**; the change is read-only and monotone (levels can only get tighter or equal for scope holders; scope-B tokens get the data-path level, which is more permissive than today for scope-A `Deny`, by design and matching the data path).

---

## 11. Open Questions

| ID | Question | Recommendation |
|---|---|---|
| O-1 | D-3 default instance instead of PRD "all `Mask` when absent": acceptable? | Yes (stateless, identical to production) |
| O-2 | Informational diagnostic code for the impersonation limit: new `QueryDiagnosticCodes` entry vs. text in an existing field | New code (machine readable) |
| O-3 | Make `CatalogVisibility` exact for row-group consents (needs a SID-free resolver entry point on `ConsentResolutionService`) | Defer; names-only, fail-safe side |
| O-4 | `GetScopeSet` overload choice (security note 2) | Use the claims overload (`principal.Claims`) as the data path does |

---

## 12. Changelog

- 2026-10-16: Revision 1 (Phase 2), verified against the worktree code; refines the PRD (oracle is `GetEffectiveColumnAccess`, baseline branch for not-granted callers, default resolver instance, `McpCatalogVisibility.cs` untouched).

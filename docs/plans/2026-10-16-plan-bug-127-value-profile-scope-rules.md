# Implementation Plan: REQ-BUG-127 - Scope-Aware Column Levels for Value Profiles, Synthetic Samples, Query Analysis and Catalog Visibility

**Document ID:** `PLAN-BUG-127` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-BUG-127](2026-10-16-req-bug-127-value-profile-scope-rules.md) (FR-127-1..6, AC-127-1..10, Q-127-1, Q-127-2)
**Branch:** `fix/issue-127-profile-scope-levels` (suggested by the PRD; base `main`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Status:** PROPOSED (Phase 2 complete; Phase 3 security review complete 2026-10-16, verdict APPROVED WITH CONDITIONS: binding conditions BC-1..BC-12 in the Phase 3 section supersede D-2 and D-4 and refine D-5, D-6; next: Phase 4 TDD)

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
- 2026-10-16: Phase 3 security review appended (APPROVED WITH CONDITIONS; 13 findings SEC-B127-01..13, binding conditions BC-1..BC-12, mandatory tests ST-1..ST-16).

---

## Phase 3 Security Review (2026-10-16)

**Reviewer:** `csharp-security-expert` (Phase 3). **Scope:** PLAN-BUG-127 rev. 1 against PRD REQ-BUG-127 and the worktree code. Every claim below was re-read in the code (file:line); read-only review, no source change.

### 1. Verdict

**APPROVED WITH CONDITIONS.** The structural decision (delete the three private level copies, delegate to `ConsentResolutionService` and `TableAccessDecision.GetEffectiveColumnAccess`) is correct and closes the reported leak. Two design decisions as written would re-open or create a leak and must be replaced before coding: **D-2** (wrong `GetScopeSet` overload, SEC-B127-01) and **D-4** (baseline keyed on `!decision.IsAllowed`, SEC-B127-02). D-5 (fail-closed value) and D-6 (impersonation) need tightening. The binding conditions BC-1..BC-12 below supersede the conflicting parts of the plan; with them applied no further Phase 2 revision is required.

### 2. Threat model summary

| Item | Assessment |
|---|---|
| Asset | Column values and value-derived statistics of columns whose effective level for the caller token is `Mask` or `Deny`: min, max, allowed values, sample values, distinct bucket/count, null/empty share, typical length, format pattern; synthetic rows derived from them |
| Adversary | A caller with a valid token, deliberately narrowed by scope (delegated agent / LLM client) or by a Deny-effect consent, who reads metadata channels (`describe_dataset`, `sample_rows synthetic=true`, REST `/api/v1/catalog/datasets/{dataset}/sample-rows`, query analysis, catalog listing) to learn what the data path would mask. Secondary: a `governance:simulate` holder analysing on behalf of another principal. Tenant-crossing caller. |
| Trust boundary | Token claims (scopes, roles, groups, SID, tenant) -> consent resolution -> level -> profile/sample/prediction/visibility. The level is the only gate; `ColumnValueProfileService` and `SyntheticSampleRowGenerator` are pure functions of it (`ColumnValueProfileService.cs:211-252`, `SyntheticSampleRowGenerator.cs:47-49,100-104`). |
| Value-bearing sinks (complete) | Only two call sites feed the profile service: `McpDatasetCatalog.cs:221` and `:541` (grep of `GetColumnProfile`, `GetDatasetProfiles`, `GenerateRows` outside `Profiling/` finds nothing else). REST sample-rows (`QueryAnalysisAndProfilingEndpoints.cs:157-166`) and MCP tools (`McpDatasetTools.cs:430`) both go through `SampleRowsAsync`; GraphQL `QueryTypes.cs:444` and MCP list/describe emit names/metadata only. No profile or level is cached across callers (profile snapshots are per table/column and the level is applied at read time, `ColumnValueProfileService.cs:32-41,203-252`; the only caches found are `AccessProfileCache` and the consent decision cache, neither used by the catalog). |
| Fail-closed anchor | Data path: no scope claim => base rules only, scope-only rule `Deny` (#116); null decision => `Denied` (`TableAccessPolicy.Consent.cs:82-85`). |

#### Leak-channel closure matrix

| Channel | Closed by the plan? | Note |
|---|---|---|
| `valueProfile` min / max | Yes (level `Mask`/`Deny` strips, `ColumnValueProfileService.cs:240-252`) | |
| allowed values, sample values | Yes | same |
| distinct bucket / approximate distinct count | Yes (`null` on `Mask`) | |
| null/empty share, typical length, format pattern | **Not stripped on `Mask`** (`:242-251`) | metadata only, no values; owner decision Q-127-2, see SEC-B127-11 |
| synthetic sample rows | Yes (`Mask` -> placeholder, `Deny` -> column omitted, `SyntheticSampleRowGenerator.cs:60-62,100-104`); primary-key branch comes after the masked check | |
| real `sample_rows` | Unaffected (data path) | |
| query-analysis `EffectiveAccessLevel`, `READ_CLEARTEXT` suggestion, `MaskedColumnInFilter` | Yes for caller==target; **prediction too permissive for an impersonated target** (SEC-B127-04) | not a value leak |
| `CatalogVisibility` column names | Fail closed today; D-7 is an accuracy change; row-group maximum can list a name the data path denies | names only |
| MCP JSON / GraphQL `QueryTypes.cs` | Same objects; GraphQL emits column names only (`QueryTypes.cs:~453-466`) | |
| Static column text (`Description`, `Semantics`, golden query `QueryText`/`VariablesJson`, `McpDatasetCatalog.cs:~233-241`) | Not scope-gated, not part of this bug | out-of-scope observation: owner-authored text could contain literal values; no change requested here |

### 3. Findings

| ID | Sev. | Finding | Evidence | Required change |
|---|---|---|---|---|
| SEC-B127-01 | **High** | **D-2 uses the wrong `GetScopeSet` overload and would leave the bug open for IdPs that emit a plain `scope` claim.** `GetScopeSet(ClaimsPrincipal)` delegates to `GetScopes`, which only reads `scp` and the Microsoft mapped `.../claims/scope`. The data path uses `GetScopeSet(IEnumerable<Claim>)`, which additionally reads `scope`. A token whose scopes arrive as `scope` (Keycloak, Okta, generic OIDC) is scope-A for the data path (masked) but scope-less for a catalog that uses the principal overload (full profile). The plan's O-4 identifies the difference but the reference it cites (`EffectiveAccessExplanationService.cs:65`) has the same defect. | `TokenAccessScope.cs:44-48` (principal overload), `:50-67` (claims overload incl. `scope`, `:58`); data path `TableAccessPolicy.Consent.cs:36` (`GetScopeSet(query.Claims)`), `query.Claims = principal.Claims` (`SecurityPrincipalContext.cs:179`); other data-path users `GovernedAggregationService.ValidationAndExecution.cs:66`, `SemanticMetricQueryService.ConsentAndEvaluation.cs:58,155` all use the claims overload | BC-1 |
| SEC-B127-02 | **High** | **D-4 baseline is keyed on `decision.IsAllowed` and re-opens leaks.** (a) `ResolveAccess` returns `Denied` with an empty `ColumnAccess` when the caller has no Allow consent, before Deny-effect column rules are read; a caller who holds only a Deny-effect consent that `Deny`s column X (or a scope-bound `Deny`) falls into the baseline, which yields `Clear` for X, so with `ReleaseProfileToCatalogViewers` the explicitly denied column gets a full profile. Today X is `Deny` because `isExplicitlyDenied` looks at every consent of the table regardless of effect. The plan's "catalog `DENY` layer" only covers `ColumnMaskingRules`, not Deny-effect consents. (b) A caller whose status is `GRANTED` (`ComputeAccessStatus` is a coarse any-Allow/no-unconditional-Deny test, not scope- or grantee-aware) but whose decision is denied (subject/`IsActive` re-filter, clock edge, tenant mismatch) would take the baseline and get `Clear`; the plan's security note 4 ("granted callers never take the baseline branch") is not guaranteed by `IsAllowed`. (c) An explicit unconditional table-level Deny consent also reaches the baseline and shows a profile to a caller the owner explicitly denied (existing behaviour, `ComputeAccessStatus` returns `NONE`). | `ConsentResolutionService.cs:41-56` (hard deny and zero-trust return before `:58-75`), `McpDatasetCatalog.cs:429-433` (today's any-effect Deny), `:187-196` (`canViewProfile`), `CatalogVisibility.cs:21-47` | BC-2 |
| SEC-B127-03 | Medium | **D-5 fails closed to `Mask` instead of `Deny`, and may drop the catalog `DENY` layer.** The data path's own precedent for a failed/absent decision is `Denied`. A `Mask` profile still emits format pattern, empty share and typical length; `Mask` for a column that the catalog `DENY` masking rule denies is looser than `Deny`. A broad `catch` would also swallow cancellation. | `TableAccessPolicy.Consent.cs:82-85`; `ColumnValueProfileService.cs:240-252`; `McpDatasetCatalog.cs:429-433` | BC-3 |
| SEC-B127-04 | Medium | **Impersonated analysis target: "no scopes" is looser than reality, and the plan's fail-closed claim is wrong for base+scope rules.** The REST endpoint builds a `SimulatedTarget` principal with SID and roles only, never scopes. With an empty set a base `Clear` + scope-A `Mask` is predicted `Clear` for a target whose real token may hold scope A (the exact bug class, in a prediction channel). A single "no scopes" evaluation is stricter for scope-only grants but looser for hardening rules; the right fail-closed answer for an unknown token is the minimum of both worst cases. | `QueryAnalysisAndProfilingEndpoints.cs:78-97`; `QueryAnalysisService.cs:98-113`; resolver hardening `ConsentResolutionService.cs:275-285` | BC-4 |
| SEC-B127-05 | Medium | **Tenant isolation not carried into the new resolver call.** The data path re-filters consents by tenant before `ResolveAccess`; the resolver itself never checks `TenantId`. The old catalog code computed levels from whatever the repository returned; the plan now feeds the same list to a resolver that can answer `Allow`/`Clear`, so a repository or mock returning a foreign-tenant consent would grant. `FilterForSubject` filters by tenant, the level producer must too. | `TableAccessPolicy.Consent.cs:~60-63` (`.Where(c => c.TenantId == query.Tenant)`), `CatalogVisibility.cs:66`, `ConsentResolutionService.cs:30-33` (no tenant predicate), `McpDatasetCatalog.cs:~479-483` | BC-5 |
| SEC-B127-06 | Medium | **Admin short-circuit is looser than the data path and uses a weaker predicate than the rest of the catalog.** The data path has no admin read bypass (admin is only referenced for DML, `TableAccessPolicy.cs:348-354`), so a `GovernanceAdmin`/`ClusterAdmin` with a scope-narrowed token or with only a `Mask` consent gets full profiles while the data path masks. The catalog also tests the raw role string `ClusterAdmin` (`McpDatasetCatalog.cs:468-470`) whereas `CatalogVisibility.VisibleTablesAsync` and GraphQL use `ClusterAdminPolicy.IsCanonicalClusterAdmin` (ForwardAuth identities and tenant-prefixed roles never qualify, `ClusterAdminPolicy.cs:20-37`). `QueryAnalysisService.IsAdminPrincipal` has its own copy. Pre-existing, not introduced by the plan, but the plan claims "unchanged" and relies on it (D-5, security note 6). | as cited; `QueryAnalysisService.cs:425-` | BC-6 |
| SEC-B127-07 | Low | **"The catalog is stricter than the data path in every non-modelled case" is not true.** Access profile `Strict` turns sensitive columns into `Deny` on the data path (`TableAccessPolicy.Consent.cs:149`), the catalog gives `Mask` (a profile object with format/empty share/typical length and a synthetic placeholder column instead of omission). The baseline branch, the admin branch and the `CatalogVisibility` row-group maximum are also looser. For **values** the claim holds: `Unmasked` only relaxes the data path (catalog stricter), `Default`/`Strict` only lower, and no unmodelled layer turns a catalog `Mask`/`Deny` into value disclosure. Findings 5 of section 1.1 and security note 8 must be reworded. | `TableAccessPolicy.Consent.cs:127-160` | BC-7 |
| SEC-B127-08 | Low | **Other consumers of `CatalogVisibility.VisibleTablesAsync` change behaviour silently.** Scopes are now derived from the passed principal inside `VisibleTablesAsync`, so Arrow Flight, Trino metadata, Iceberg REST federation, `SemanticMcpCompiler`, `AiDataGuardrailService` and `EffectiveAccessExplanationService` inherit it. If any of them hands over a synthetic principal without the token's scope claims, a scope-bound `Deny` no longer hides the column name there while the data path (with the real claims) would deny it. Names only, but the plan lists only two callers. | callers: `ArrowFlightSqlServer.cs:131`, `TrinoMetadataQueryHandler.cs:88`, `IcebergRestCatalogFederationService.cs:113`, `SemanticMcpCompiler.cs:116`, `AiDataGuardrailService.cs:290,354`, `EffectiveAccessExplanationService.cs:59,92` | BC-8 |
| SEC-B127-09 | Low | **Logging in D-5.** Passing the exception object to `LogWarning` can put consent filter text or SQL fragments into the log; scope values and column values must not appear. | project rule (no values in logs/audit) | BC-9 |
| SEC-B127-10 | Low | **Default resolver (O-1) is safe but unwired production is undetectable.** The default `new ConsentResolutionService()` equals the registered singleton (`GatewayGovernanceServiceExtensions.cs:138-139`, same default `RowFilterSqlBuilder`, `ConsentResolutionService.cs:10-15`), so it is never weaker. But an optional parameter hides a DI wiring regression and the row-group key (`BuildRowFilterGroups`, `:~160-175`) calls `_sqlBuilder`, which can throw on a malformed row filter (must land in the D-5 path). | as cited | BC-10 |
| SEC-B127-11 | Low (owner decision) | **`Mask` profile still discloses `formatPattern`, `emptyShare`, `typicalLength`.** Not value disclosure, but for a column that the owner masked per scope it is a residual metadata channel (e.g. a `format` meta that is a regex of real shapes, an empty share of a masked column). PRD Q-127-2 defers it. | `ColumnValueProfileService.cs:230-251` | Keep out of scope; track as follow-up (BC-12) |
| SEC-B127-12 | Info | **Existence/timing oracles.** `Deny` (null profile) vs `Mask` (profile without values) vs `Clear` distinguishes the three levels to the caller; the same information is already available through `describe_my_access`. Column visibility is filtered before describe (`FindVisibleAsync`, `CatalogVisibility.cs:107-134`), so a scope-denied column is not even named. One `ResolveAccess` per table and request is constant work per consent; no per-column or value-dependent branch. No change required. | `McpDatasetCatalog.cs:717-` | none |
| SEC-B127-13 | Info | **Linked identities and role derivation.** The data path passes `GetAllUserSids()` (linked SIDs) and uses the security context roles (`McpEndpoints.cs:211-213`); the catalog loads and matches consents with the principal's own SID/groups/roles only. Consents granted to a linked SID are therefore not seen: the catalog can only be stricter (granted -> not-granted -> baseline, see SEC-B127-02) in that case. Plan finding 3 is right; no change beyond BC-2. | `TableAccessPolicy.Consent.cs:~55-70` | none |

Confirmed correct in the plan (no finding): enum order `Deny < Mask < Clear` (`ColumnAccessLevel.cs:3-8`) makes "scope rules only tighten" monotone (`ConsentResolutionService.cs:275-285`); `GetEffectiveColumnAccess` as the oracle (`TableAccessDecision.cs:413-436`); passing `scopes.Count > 0 ? scopes : null` exactly like the data path (`TableAccessPolicy.Consent.cs:80`); scope-bound Deny-effect `Mask`/`Deny` rules apply only to scope holders (`ConsentResolutionService.cs:60-75`); `CatalogVisibility` callers pass a principal whose claims are available; no new endpoint, persistence or configuration.

### 4. Binding conditions for Phase 4

| ID | Condition |
|---|---|
| BC-1 (SEC-01, supersedes D-2 and O-4) | Scopes are taken as `TokenAccessScope.GetScopeSet(principal.Claims)` (the **claims overload**, same as `TableAccessPolicy.Consent.cs:36`) in `McpDatasetCatalog`, `QueryAnalysisService` and `CatalogVisibility.VisibleTablesAsync` / `QueryTypes.cs:444`. Never from a tool argument, query text or request parameter. Pass `scopes.Count > 0 ? scopes : null` to the resolver. File a follow-up issue to make `GetScopeSet(ClaimsPrincipal)` delegate to the claims overload (root-cause fix; also repairs `EffectiveAccessExplanationService.cs:65`); not part of this change unless the lead decides otherwise. |
| BC-2 (SEC-02, supersedes D-4 and security note 4) | The not-granted baseline is selected by the **access status** (`accessStatus != GRANTED` and `!isAdmin`), not by `decision.IsAllowed`. `GRANTED` (or bypass) with a denied or null decision yields `Deny` for every column, never the baseline. In the baseline branch the level per column is the baseline from `GetEffectiveColumnAccess` **and** is lowered to `Deny` by (i) the catalog `DENY` masking rule, (ii) every subject-matching, active, tenant-matching **Deny-effect consent** with an applicable (unscoped, or scope-matching with the same `callerScopes` semantics as the resolver) `Deny` column rule (reuse `ConsentResolutionService.ResolveConsentColumnLevel`; no new rule logic), and (iii) an unconditional table-level Deny consent (no column rules, no row filters) for the subject: all columns `Deny` (profile `null`). Item (iii) is stricter than today and is the recommended reading of the owner flag; if the owner wants the old behaviour it must be an explicit decision and a test, not an accident. The baseline is reachable only with `canViewProfile` and only through `ReleaseProfileToCatalogViewers`. |
| BC-3 (SEC-03, refines D-5) | On any exception from resolution (excluding `OperationCanceledException`, which propagates), a `null` decision, an undefined enum value, or a failed subject/tenant derivation: every column is `Deny` (profile `null`, synthetic column omitted), matching `TableAccessPolicy.Consent.cs:82-85`. The catalog `DENY` rule and admin short-circuit keep their results. No `Mask`-for-all fallback and no `Clear` for a missing entry of any column: the defensive fallback at `McpDatasetCatalog.cs:214-219` becomes `Deny` for a missing entry (or is deleted because `ResolveColumnLevels` is complete). |
| BC-4 (SEC-04, refines D-6 and Q-127-1) | `QueryAnalysisService` scope set: target absent or same principal as the caller => claims-overload scopes of the caller. Impersonated target (`targetPrincipal != null` and a different SID) => evaluate the decision **twice**, with no scopes and with the union of all scopes named by the table's consent column rules (worst-case hardening), and use the **minimum** level per column. Emit the informational diagnostic (new code, no values, no scope names) stating that the prediction assumes unknown token scopes. The prediction must never be more permissive than either evaluation. |
| BC-5 (SEC-05) | Before `ResolveAccess`, `ResolveColumnLevels` filters `activeConsents` to `c.TenantId == principal.GetTenantId()` (same defensive filter as the data path). A tenant claim that cannot be resolved throws before any level is produced (fail closed, existing behaviour of `GetTenantId`). Same filter in `QueryAnalysisService` for the decision it resolves. |
| BC-6 (SEC-06) | Replace the raw `roles.Contains("ClusterAdmin")` in `McpDatasetCatalog.GetCallerConsentsAndRequestsAsync` by `ClusterAdminPolicy.IsCanonicalClusterAdmin(principal)` (keep the `GovernanceAdmin` role check); no other change to the admin semantics (admin keeps `Clear`, documented as a catalog-only privilege that is not an access decision). Do the same alignment for `QueryAnalysisService.IsAdminPrincipal` only if it can be done without changing the `simulate` privilege rules; otherwise a follow-up issue. State the admin looseness in the Phase 6 docs. |
| BC-7 (SEC-07) | Reword plan finding 5 and security notes 6 and 8: "no new value leak: for values the catalog is never more permissive than the data path in the modelled and unmodelled cases; for metadata-only fields it can be looser (access profile `Strict`, baseline, admin)". Add the access-profile `Strict` case to the documented limits. |
| BC-8 (SEC-08) | Before merging, Phase 4 lists every `CatalogVisibility.VisibleTablesAsync`/`FilterForSubject` caller (eight listed in SEC-B127-08) and confirms in a test or a code comment that the principal it passes carries the token claims used by that protocol's data path. Callers that build a synthetic principal without claims must keep today's fail-closed behaviour: when the principal has no scope claim at all, a column hidden by a scope-bound Deny today stays hidden only if the data path for that principal would also deny it; if that cannot be shown for a caller, that caller passes `callerScopes` explicitly or the finding is documented as names-only. |
| BC-9 (SEC-09) | The D-5 warning logs only the table identifier and the exception **type name** (`ex.GetType().Name`), never the exception message/object, scope values, consent content, column values or profile values. Add a log-capture assertion (ST-12). |
| BC-10 (SEC-10) | Keep the optional parameter and the real-instance default (O-1 approved). Additionally: an integration-style DI test (existing `WebApplicationFactory`/service-provider test pattern, unit-level if one exists) resolves `IMcpDatasetCatalog` and `IQueryAnalysisService` from the real container and asserts that the resolver actually used is the registered `IConsentResolutionService` singleton (expose an `internal` property for the test via the existing `InternalsVisibleTo`, or assert behaviour: a scope-A Mask fixture through the container). Exceptions thrown by the row-filter builder inside `ResolveAccess` must reach the BC-3 path. |
| BC-11 | **Caching:** no caching of levels or `ColumnValueProfile` across callers is introduced. If a cache is ever added to the catalog or the analysis, its key MUST include tenant, user SID and the context hash of groups, roles and scopes (`IConsentCacheService.ComputeSubjectContextHash(groupSids, roles, scopes)`, as `TableAccessPolicy.Consent.cs:38`); a key without scopes is a defect. Add a code comment at the level producer and a regression test (ST-14). |
| BC-12 | Follow-up issues (not blocking): (a) root-cause fix for `GetScopeSet(ClaimsPrincipal)`; (b) Q-127-2, suppress or coarsen `formatPattern`/`emptyShare`/`typicalLength` for scope-masked columns (links to #117); (c) a SID-free resolver entry point so `CatalogVisibility` can be exact for row-group consents (O-3). |

### 5. Mandatory security tests (in addition to T-1..T-15)

Assertions on profile content use sentinel strings and the **serialized** MCP/REST JSON, as the plan requires. Each must fail on current `main` unless marked guard.

| # | Test | Covers |
|---|---|---|
| ST-1 | `Describe_ScopeClaimTypes_ParityWithDataPath` (theory over `scp`, `http://schemas.microsoft.com/identity/claims/scope`, plain `scope`, space-separated multi-scope, upper/lower case values): base `Clear` + scope-A `Mask`; the catalog level equals `ResolveAccess(..., GetScopeSet(principal.Claims))`; plain `scope` token has no sentinel in the JSON. Same for synthetic rows and `QueryAnalysisService` | SEC-01, BC-1 |
| ST-2 | `Describe_NotGranted_DenyEffectConsentColumnDeny_StaysDeny_EvenWithReleaseProfileToCatalogViewers` (Deny-effect consent denying column X, no Allow; also scope-bound variant: Deny for scope A holder only) | SEC-02, BC-2 (ii) |
| ST-3 | `Describe_NotGranted_UnconditionalTableDeny_NoProfileForAnyColumn` | SEC-02, BC-2 (iii) |
| ST-4 | `Describe_StatusGranted_ButDecisionDenied_AllColumnsDeny` (substitute resolver returning `Denied`, status `GRANTED`) and `...ThrowingResolver_AllColumnsDeny_NeverMask_NeverClear`, null decision, undefined enum value, catalog `DENY` column stays `Deny`, `OperationCanceledException` propagates | SEC-02, SEC-03, BC-2, BC-3 |
| ST-5 | `Describe_ScopeBoundDenyEffectMask_AppliesOnlyToScopeHolders` (Deny-effect consent with `Mask` for scope A: scope A -> no values; scope B and no scope -> data-path level) | security note 5 |
| ST-6 | `Describe_ForeignTenantAllowConsent_DoesNotGrant` (repository substitute returns an Allow `Clear` consent of another tenant: level must not be `Clear`; for the synthetic path too) | SEC-05, BC-5 |
| ST-7 | `QueryAnalysis_ImpersonatedTarget_BaseClearScopeAMask_PredictsMask` and scope-only grant predicts `Deny`/no grant; diagnostic present and free of scope names and values | SEC-04, BC-4 |
| ST-8 | `QueryAnalysis_CallerIsTarget_UsesCallerClaimsScopes_IncludingPlainScopeClaim` | BC-1, BC-4 |
| ST-9 | `Describe_Admin_ForwardAuthClusterAdminRole_IsNotAdmin` and canonical `ClusterAdmin` / `GovernanceAdmin` remain `Clear` (guard) | SEC-06, BC-6 |
| ST-10 | `ColumnLevelParity` extended (T-9): add the "scope claim type" dimension, the "no rule for column" row, a role-based grantee, and the property "catalog value-level never more permissive than the oracle" with the catalog `DENY` rule and access profiles excluded by construction | FR-127-6 |
| ST-11 | `Describe_AccessProfileStrict_SensitiveColumn_NoValues` (documents that the catalog is `Mask`, not `Deny`; asserts no min/max/allowed/sample/distinct in JSON) | SEC-07 |
| ST-12 | `Describe_ResolutionFailure_LogsNoValues` (capturing logger: contains table identifier and exception type only; no sentinel, no scope name, no exception message) | SEC-09, BC-9 |
| ST-13 | `Container_ResolvesCatalogAndAnalysis_WithRegisteredResolver` (scope-A Mask fixture through the real service provider) | SEC-10, BC-10 |
| ST-14 | `Describe_TwoCallersDifferentScopes_NoCrossContamination` (same `McpDatasetCatalog` instance, same table, caller 1 scope A then caller 2 scope B then caller 1 again; interleaved and concurrent via `Task.WhenAll`; profile service shared; assert levels follow each caller) | BC-11 |
| ST-15 | `CatalogVisibility_ScopeFromClaimsOverload` and `FilterForSubject_CallerScopesNull_BehavesAsDataPathWithoutScopes` (null set: scope-only grant hidden, scope-bound Deny not applied, equals data path for a token without scope); `QueryTypes` catalog: scope-A Deny hides the column for scope A and shows it for B | SEC-01, SEC-08, AC-127-8 |
| ST-16 | `Describe_ExplicitDenyColumn_NotListedAndNoProfile_ForScopeHolder` and "denied column absent from serialized JSON" (name and sentinel) for MCP `describe_dataset`, REST sample-rows (`synthetic=true`) and `list`/GraphQL `catalog` | channel closure, SEC-12 |

Also required: update every existing test whose expectation pins the old scope-blind level (plan R-6) only after confirming, case by case, that the new expectation equals the data-path oracle.

### 6. Answers to the plan's open questions (security view)

| ID | Recommendation |
|---|---|
| O-1 (default `new ConsentResolutionService()`) | **Safe, approved.** The class is stateless and its default `RowFilterSqlBuilder` equals the registered one, so the default is the production behaviour, never weaker than the old all-`Clear` shortcut. Conditions: BC-10 (DI wiring test; exceptions from the builder fail closed per BC-3). Do not use the PRD's "all `Mask` when absent" fallback (it breaks construction and weakens nothing, but is untestable against the oracle). |
| O-2 (new `QueryDiagnosticCodes` entry) | **Approved.** Machine-readable, informational severity, message without values or scope names (BC-4). |
| O-3 (exact `CatalogVisibility` for row-group consents) | **Defer is acceptable.** The residual is a column **name** listed that the data path denies for one row group; the data path and the profile (via the resolver) still deny it. Keep T-12 and document. Track as BC-12(c). |
| O-4 (`GetScopeSet` overload) | **Use the claims overload (`principal.Claims`) everywhere in this change** (BC-1). The principal overload is looser than the data path for the plain `scope` claim. Fix the overload at the root as a separate issue. |
| Q-127-1 (impersonated target) | **Approved with change:** not "no scopes" alone but the minimum of no-scope and all-rule-scopes evaluation (BC-4), plus the informational diagnostic. |
| Q-127-2 (`formatPattern`/`emptyShare`/`typicalLength` on `Mask`) | **Out of scope here**, owner decision; recommend tracking with #117 because the channel exists for scope-masked columns (SEC-B127-11, BC-12(b)). |

### 7. Review checklist result (plan section 10)

1. No residual private level logic: acceptable once `hasExplicitClear`, `hasExplicitMask`, `isExplicitlyDenied` are deleted from `McpDatasetCatalog.cs:429-459` and `QueryAnalysisService.Evaluation.cs:277-307`; the Phase 4 grep gate stays mandatory. The baseline layer (BC-2) must reuse `ResolveConsentColumnLevel`, not re-express rules.
2. Scope source: **fails as written** (SEC-B127-01), fixed by BC-1.
3. Exception path: **fails as written** (Mask, message logging), fixed by BC-3 and BC-9.
4. Baseline branch: **fails as written** (SEC-B127-02), fixed by BC-2.
5. Deny-effect `Mask` path: sound in the resolver; covered by ST-5 and ST-10.
6. Admin and bypass: unchanged but looser than the data path; BC-6 aligns the predicate, the rest is documented.
7. Serialized output: covered (sentinels on serialized JSON, ST-1, ST-16).
8. Looser-than-data-path layers: restated in BC-7.
9. No new data flow, endpoint, configuration, persistence: confirmed; the change is read-only. Monotonicity claim holds except for the deliberate scope-B relaxation for scope-A `Deny` (matches the data path) and the BC-2 baseline.

### 8. Phase 4 hand-over

Apply BC-1..BC-11 as part of WP-127-A; BC-12 as follow-up issues. The lead compiles once at the end (Release build, warnings as errors) and runs the unit tests; no integration test is needed beyond ST-13 (which runs in the unit project if the existing fixtures allow it, otherwise as a DI smoke test).

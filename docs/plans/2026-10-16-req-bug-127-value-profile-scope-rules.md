# REQ-BUG-127: Value Profiles in `describe_dataset` Ignore Scope-Specific Column Rules

**Document ID:** `REQ-BUG-127`
**Date:** 2026-10-16
**Status:** PRD READY (Phase 1 complete); SECURITY REVIEW pending (Phase 3 is short for a bug; the architect step is folded into section 6); next: Phase 4 TDD fix
**Priority:** P1, security-relevant, **implement next** (before any feature work)
**Author:** Enterprise Product Manager (Phase 1)
**Source:** GitHub issue #127 (owner, v1.1.15), follow-up to #26 (token scopes), #80 (value profiles) and #116 (scope rules fail closed)
**Branch:** `fix/sqlserver-ntlm-catalog-diagnostics` (docs only; the fix gets its own branch, suggested `fix/issue-127-profile-scope-levels`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Related feature docs (read-only references):** [F-GOV-15 declarative consents & scopes](../features/f-gov-15-declarative-consents-scopes.md), [F-GOV-16 catalog transparency](../features/f-gov-16-catalog-ownership-transparency.md), `docs/features/README.md` (value profiling, issue #80)

**Binding project constraints:** fail closed; no query text or row values in logs or audit; English only; no upgrade or repair migrations (this fix has no schema change).

---

## 1. Problem and Verified Behaviour

`describe_dataset` coarsens the `valueProfile` of each column by the caller's access level, but the level is computed without the caller's token scopes. For a column with a base rule `Clear` plus a `Mask` rule for scope A, a token with scope A is masked by the data path (consent resolution, `callerScopes`) but treated as `Clear` by the catalog, so the full profile (minimum, maximum, allowed values, sample values) is shown.

### 1.1 Claim verification (code at branch head `5ae3c046`)

| Claim in #127 | Verified | Evidence |
|---|---|---|
| The level comes from `ComputeTableColumnAccessAndFilters` and does not know scopes | **Confirmed** | `src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs:399-463`: signature `(TableMetadata, IReadOnlyList<Consent>, bool isAdmin)`, no scopes; called at `:198` (describe) and `:539` (synthetic sample) |
| `hasExplicitClear` wins for base `Clear` + scope-A `Mask` | **Confirmed** | `:437-444` `hasExplicitClear` is true for any non-scope-specific `Clear` rule; `:446-449` `hasExplicitMask` also matches the scope-A `Mask` rule but only decides when `!hasExplicitClear`; `:451-459` then yields `Clear`. Compare the data path: `ConsentResolutionService.GetConsentColumnLevel` (`src/Autheris.Application/Services/ConsentResolutionService.cs:241-300`) takes the base level and tightens it by every scope rule whose scopes the caller holds (monotonic hardening), which gives `Mask` for scope A |
| The leak reaches the profile | **Confirmed** | `McpDatasetCatalog.cs:214-221`: `effectiveLevel` from the scope-blind dictionary goes to `ColumnValueProfileService.GetColumnProfile` (`src/Autheris.Application/Catalog/Profiling/ColumnValueProfileService.cs:203-260`), which suppresses min, max, allowed and sample values only for `Mask` or `Deny` (`IsColumnSensitiveOrMasked`) |
| The same level feeds other value-derived output (line 198, line 539) | **Confirmed** | `:198` (profiles), `:539-541` (`SyntheticSampleRowGenerator.GenerateRows(table, rowCount, columnAccessLevels, hasActiveRowFilters)` builds synthetic rows from `GetDatasetProfiles`, `SyntheticSampleRowGenerator.cs:35`) |
| Not only the `Mask` case: other scope-blind differences exist | **New finding** | (a) `:429-433` `isExplicitlyDenied` treats a `Deny` rule of any scope as a deny for every caller (conservative, but wrong for a scope-B token: the profile is hidden although the data path would show it). (b) `hasExplicitMask` (`:446-449`) and the combination of several consents use "any consent grants `Clear`" (maximum), while the data path takes, for consents bound to row filters, the **minimum over row-constrained groups** (`ConsentResolutionService.ResolveColumnLevel`, `:200-232`). A column that is `Clear` through one row-filtered consent and `Mask` through another can show a full profile although the person gets `Mask` for the rows of the other group. (c) Catalog `Mask` rules from `ColumnMaskingRules` of type other than `DENY` and `IsSensitive` are handled only as a fallback (`:216-219`), not as the data path does (`maskedOnlyColumns` of Deny-effect consents, `ConsentResolutionService.cs:60-75`) |

Real sample rows (`sample_rows` with `synthetic=false`) are **not** affected: they go through `IGatewayExecutionService.ExecuteTableQueryAsync` (`McpDatasetCatalog.cs:~575`), i.e. the scope-aware data path.

### 1.2 Severity

Information disclosure of masked values (range, allowed values, sample values of the profile) to a token that the owner deliberately narrowed by scope, in the metadata channel that MCP clients (LLM agents) read first. No write access, no bulk data; bounded to profile metadata. P1 because it defeats a masking rule that the owner configured for exactly this token class, and the fix is small.

## 2. Inventory: Value-Derived Output and Scope-Blind Level Computations

| # | Place | Output | Level source today | Scope-aware? | Decision |
|---|---|---|---|---|---|
| 1 | `McpDatasetCatalog.DescribeDatasetAsync` `valueProfile` per column (`:198`, `:214-221`) | min, max, allowed values, sample values, distinct bucket, format | `ComputeTableColumnAccessAndFilters` | **No** | **Fix (in scope)** |
| 2 | `McpDatasetCatalog.SampleRowsAsync(synthetic: true)` (`:526-560`) | synthetic rows derived from value profiles | same function (`:539`) | **No** | **Fix (in scope)** |
| 3 | `McpDatasetCatalog.SampleRowsAsync(synthetic: false)` | real rows | data path | Yes | none |
| 4 | `ColumnValueProfileService.GetDatasetProfiles` (`:326-350`) | per-dataset profile dictionary (used by #2) | level argument from the caller | depends on the caller | fixed through the callers |
| 5 | `QueryAnalysisService.Evaluation.ResolveEffectiveColumnAccess` (`src/Autheris.Application/Governance/Analysis/QueryAnalysisService.Evaluation.cs:255-310`) | per-column `EffectiveAccessLevel`, `Mask` diagnostics, `READ_CLEARTEXT` missing-consent suggestions | own copy of the same logic, scope-blind (`:290-299`) | **No** | **Fix (in scope), see 2.1** |
| 6 | `CatalogVisibility.FilterForSubject` (`src/Autheris.Application/Services/CatalogVisibility.cs:57-150`) used by GraphQL `catalog` (`src/Autheris.GraphQL/Types/QueryTypes.cs:444`) and `McpCatalogVisibility` | which **columns** (names, types, descriptions) are listed | `explicitlyGrantedColumns` ignores scope-specific rules (`:112-116`), `deniedColumns` counts every `Deny` regardless of scope (`:118-121`) | **No**, but fail closed (hides, never reveals) | **Align (in scope, accuracy), see 2.2** |
| 7 | `CatalogVisibility.ComputeAccessStatus` (`:21-55`) | `GRANTED` / `PENDING` / `NONE` | table-level only | not column related | none |
| 8 | `describe_my_access` / `EffectiveAccessExplanationService` | explanation of levels | `TokenAccessScope.GetScopeSet(principal)` (`src/Autheris.Application/Governance/Services/EffectiveAccessExplanationService.cs:65`) | Yes | none (reference implementation of scope extraction) |
| 9 | `SemanticMetricQueryService.ConsentAndEvaluation.cs:~193` | metric access decision | `ResolveConsentColumnLevel(c, col, scopes)` | Yes (#116) | none |
| 10 | GraphQL aggregate, OData, export, WebSQL, MCP `query_*` | data | `ConsentResolutionService.ResolveAccess(..., callerScopes)` | Yes | none |

### 2.1 `QueryAnalysisService` needs scopes too (decision: yes)

It does not disclose values, but it tells a caller "this column is `Clear` for you" and suppresses the `READ_CLEARTEXT` access request suggestion, when the data path will mask it. After #116 it is fail closed only for scope-only grants; the base `Clear` + scope `Mask` case predicts `Clear`. Result: a wrong analysis, no leak. It must use the same resolution as the data path so that the analysis never promises more than the execution delivers. For the "analyse on behalf of another principal" case (`effectivePrincipal`), the scopes of the **target** are unknown; the analysis uses the scopes of the calling token if the target is the caller, and **no scopes** (base levels, scope-only grants denied) for an impersonated target, with the result flagged `scopesAssumed: none` in the diagnostics (fail closed for predicted grants; a prediction that is stricter than reality is acceptable, a looser one is not). Note: with "no scopes" a base `Clear` + scope-A `Mask` predicts `Clear` for the target; since the target's token scope is unknown this is the documented limit, stated in the analysis output.

### 2.2 `CatalogVisibility` (decision: align, lower priority)

Fail closed and thus **not a leak**: it can only hide a column from a scope-B caller that the data path would allow (a scope-A `Deny` hides the column for everyone) or hide a scope-only-granted column from the scope holder. It is an accuracy and usability defect, not a security one. Include it in the same change set because it shares the helper and the test fixtures; it gets caller scopes through `FilterForSubject(allTables, activeConsents, tenantId, callerScopes)` (additive optional parameter, `null` = today's behaviour for tests and admin paths). Callers: `QueryTypes.cs:444`, `McpCatalogVisibility.VisibleTablesAsync`.

## 3. Requirements

- **FR-127-1 One resolution.** The access level of a column used for `valueProfile`, synthetic samples, `QueryAnalysisService` and (accuracy) `CatalogVisibility` is the level that the data path resolves for the same caller and the same token scopes: the caller scopes come from `TokenAccessScope.GetScopeSet(principal)`, the level from `ConsentResolutionService` (`ResolveAccess(..., callerScopes)` for the table decision, or `ResolveConsentColumnLevel(consent, column, scopes)` per consent combined exactly as `ResolveColumnLevel` does). No fourth copy of the rules.
- **FR-127-2 Profile follows level.** `Mask` and `Deny` produce a profile without min, max, allowed values, sample values and distinct bucket (existing `ColumnValueProfileService` behaviour, unchanged); `Deny` produces no profile.
- **FR-127-3 Synthetic samples.** Synthetic rows use the same scope-aware levels; a `Mask` column contains no values derived from the profile.
- **FR-127-4 Combination semantics.** Several consents, row-constrained groups, Deny-effect `Mask` rules and catalog masking rules combine as in `ConsentResolutionService.ResolveAccess`; the catalog never reports a more permissive level than the data path for the same inputs (parity property test, FR-127-6).
- **FR-127-5 Fail closed.** Missing or unreadable scope claim (empty set) means "no scopes": base rules only, scope-only grants denied (the existing fail-closed rule of #116). An exception while resolving yields `Mask` for every column (never `Clear`) and a warning without values.
- **FR-127-6 Parity test.** A parameterised test enumerates combinations (base `Clear`/`Mask`/`Deny`/none x scope rule `Clear`/`Mask`/`Deny` x caller scope A/B/none x one or two consents x row-filtered or not) and asserts `catalog level == data-path level` for the same input, for `ComputeTableColumnAccessAndFilters` (replacement), `QueryAnalysisService` and `CatalogVisibility`.

## 4. Acceptance Criteria (from "Done when" plus findings)

| ID | Criterion | Test |
|---|---|---|
| AC-127-1 | Column with base `Clear` and a `Mask` rule for scope A: `describe_dataset` with a scope-A token returns the profile of a masked column (no min, max, allowed values, sample values, distinct bucket) | unit test on `McpDatasetCatalog` with fake consent repository and profile snapshot containing min/max/allowed values |
| AC-127-2 | Same column with a scope-B token and with no scope: full profile | same fixture |
| AC-127-3 | Base `Clear` + `Deny` for scope A: scope-A token gets no profile (`null`); scope-B token the full profile (today: hidden for both) | same fixture |
| AC-127-4 | Scope-only `Clear` rule (no base rule): a token holding that scope gets the full profile, a token without it gets no profile (`Deny`, #116); the result equals the `ConsentResolutionService` level | same fixture; asserts equality with the data path |
| AC-127-5 | Two consents, one row-filtered `Clear`, one unfiltered `Mask`: catalog level equals the data-path level | parity test |
| AC-127-6 | `sample_rows` with `synthetic=true` and a scope-A token on the AC-127-1 column contains no profile-derived values for that column | unit test |
| AC-127-7 | `QueryAnalysisService` on a query selecting the AC-127-1 column with a scope-A token reports `Mask` and the `READ_CLEARTEXT` suggestion; with scope B reports `Clear` | `QueryAnalysisAndValueProfilingTests` extension |
| AC-127-8 | `CatalogVisibility.FilterForSubject`: scope-A `Deny` on a base-`Allow` column hides it for scope A only; scope-only `Allow` shows it for the scope holder only | `CatalogVisibility` tests |
| AC-127-9 | Admin and `GovernanceAdmin`/`ClusterAdmin` unchanged (`Clear`), `IsMcpAuthBypassed` unchanged | existing tests stay green |
| AC-127-10 | Tokens without scope claim behave as today for base rules | existing tests stay green |

## 5. Gatekeeper Notes

- No new data flow, no new endpoint, no schema change, no configuration. The change only tightens or equalises levels.
- The danger is a **second** divergence: do not fix only the `Mask` case in the catalog copy. The structural fix is to delete the private level computation and call the shared resolver (FR-127-1).
- The `sample_rows` real path and `describe_my_access` already use scopes; they are the oracle in the parity test.
- Tests must include the profile values as sentinel strings so that "no values" is asserted on the serialized MCP response, not only on the profile object.

## 6. Fix Approach (architecture, brief)

1. Add an optional `IReadOnlySet<string>? callerScopes` parameter to `ComputeTableColumnAccessAndFilters`, `QueryAnalysisService.ResolveEffectiveColumnAccess` and `CatalogVisibility.FilterForSubject`; callers pass `TokenAccessScope.GetScopeSet(principal)` (for the analysis: the effective principal rule of 2.1).
2. Replace the private `hasExplicitClear` / `hasExplicitMask` / `isExplicitlyDenied` logic by the shared resolution. Preferred: inject `IConsentResolutionService` (optional constructor parameter, as `profileService`) into `McpDatasetCatalog` and `QueryAnalysisService` and use `ResolveAccess(userSid, groups, roles, table, activeConsents, dialect, allUserSids: null, callerScopes).ColumnAccess` per table, so row-constrained group semantics and Deny-effect masks come for free; fall back to the existing conservative computation (all columns `Mask` unless admin) when the service is absent (unit construction), never to the old `Clear` shortcut. The `ResolveConsentColumnLevel` helper (`ConsentResolutionService.cs:234`, internal) stays the single-consent entry for `SemanticMetricQueryService`.
3. Keep `hasActiveRowFilters` as is (not level related).
4. Tests per section 4; the parity test (FR-127-6) is the regression gate and is registered in the security regression catalog.

**Size:** S. About 150 LOC production (three call sites, one DI parameter), about 350 LOC tests. No dependency. Files: `McpDatasetCatalog.cs`, `QueryAnalysisService.Evaluation.cs` (+ constructor wiring), `CatalogVisibility.cs`, `McpCatalogVisibility.cs`, `QueryTypes.cs` (one argument), tests under `tests/Autheris.Tests.Unit/Mcp/` and `Governance/`.

## 7. Open Owner Questions

| ID | Question | PM recommendation |
|---|---|---|
| Q-127-1 | For "analyse on behalf of another principal", accept "no scopes" (documented, flagged in the output) because the target's token scope is unknown? | Yes (stricter prediction for scope-only grants, documented limit for base+scope rules) |
| Q-127-2 | Should a `Mask` column still disclose `formatPattern`, `emptyShare` and `typicalLength` in the profile (today yes)? | Out of scope here; keep as is (no values), revisit with #117 disclosure rules if wanted |

## 8. Security Review Status

Phase 3 pending (small): confirm that the parity test covers the Deny-effect mask path and that an exception in resolution cannot produce `Clear`. Verdict expected: approved.

## 9. Changelog

- 2026-10-16: Initial PRD (Phase 1), verified against `5ae3c046`.

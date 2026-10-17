# REQ-GOV-143: Sensitive Columns for Restricted Scopes Only with an Explicit Scope-Specific Clear Rule

**Document ID:** `REQ-GOV-143`
**Date:** 2026-10-17
**Status:** PRD READY (Phase 1 complete); next: Phase 2 plan (short, one work package), Phase 3 security review (mandatory: access-control semantics)
**Priority:** P2 security hardening (defence in depth for AI/agent tokens); Wave 2
**Author:** Enterprise Product Manager (Phase 1)
**Source:** GitHub issue #143 (owner, v1.1.15), follow-up to #26 (token scopes) and #116 (scope rules fail closed)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Related:** [REQ-BUG-127](2026-10-16-req-bug-127-value-profile-scope-rules.md) (catalog scope awareness, merged), issue #152 (procedure `cleared_columns`), [PLAN-SCALE-5000](2026-10-16-plan-scale-5000-users.md) `WP-SC-06` (tag-based column policy), [F-GOV-15](../features/f-gov-15-declarative-consents-scopes.md) (monotonic scope hardening)

**Binding project constraints:** fail closed; no query text or row values in logs or audit; English only; no upgrade or repair migrations (no schema change here); no production yet, so behaviour changes are acceptable.

---

## 1. Problem

A person holds two read-only delegated scopes: one for applications, one for an assistant that forwards data to a language model. A data owner clears a sensitive column with a base consent rule `Clear` (no `applicable_scopes`) for the applications. The same rule also clears the column for the assistant scope, unless the owner remembers a `Mask` rule for that scope. The owner wants: for restricted (assistant) scopes a sensitive column is clear only when the owner decided so explicitly for that scope.

## 2. Verified Current State

| # | Fact | Evidence |
|---|---|---|
| 1 | Sensitive-column masking has one rule: a catalog-sensitive column (`IsSensitive` or a `ColumnMaskingRule`) is `Mask` unless the decision holds an explicit `Clear` for it. | `src/Autheris.Domain/Interfaces/TableAccessDecision.cs:413-434` (`GetEffectiveColumnAccess`), `HasExplicitClear` at `:403-404` |
| 2 | `HasExplicitClear` only checks that the resolved level is `Clear`. It does not know which rule (base or scope-specific) produced it, and the decision does not know the caller's scopes. | `TableAccessDecision.cs:403-404`; `ColumnAccess` is filled by `ConsentResolutionService.ResolveAccess` (`src/Autheris.Application/Services/ConsentResolutionService.cs:19-135`) |
| 3 | Scope-specific rules are resolved per consent in `GetConsentColumnLevel`: with a base rule, scope rules can only tighten (monotonic hardening); without a base rule, a scope-only rule defines the level for holders of that scope and the column is `Deny` for everybody else (#116). | `ConsentResolutionService.cs:241-300` |
| 4 | Row-filter groups: level is the maximum inside a group, the minimum across row-constrained groups. | `ConsentResolutionService.cs:200-232` (`ResolveColumnLevel`) |
| 5 | The caller scopes come from the token claims through one helper and reach `ResolveAccess` on every scope-aware path. | `TokenAccessScope.GetScopeSet` (`src/Autheris.Domain/Security/TokenAccessScope.cs:44-65`); callers: `TableAccessPolicy.Consent.cs:36` and `:64-72` (data path, decision cache context hash includes scopes at `:40`), `GovernedAggregationService.ValidationAndExecution.cs:66-67`, `SemanticMetricQueryService.ConsentAndEvaluation.cs:58-59, 155-158`, `EffectiveAccessExplanationService.cs:71-91`, `CatalogColumnAccessResolver.ResolveGranted` (`:40-52`) for `describe_dataset`, value profiles and synthetic samples (#127), `QueryAnalysisService.cs:133` |
| 6 | Every read path consumes the decision through `GetEffectiveColumnAccess` (about 40 call sites): `GovernedTableReadPlanner`, `SqlTableReadExecutor`, `GatewayExecutionService`, `GovernedAggregationService`, OLAP (`MaskedColumnSet.cs:25`), masking policy (`ColumnMaskPolicyFactory.cs:38`), change detection, cross-source relations, connectors, catalog levels (`CatalogColumnAccessResolver.LevelsFromDecision`, `:130-137`). `GetEffectiveColumnAccess` is therefore the single choke point for the sensitive-column rule. | grep of `GetEffectiveColumnAccess` in `src/` |
| 7 | Access profiles can bypass masking: `MaskingPolicyMode.Unmasked` turns `Mask` into `Clear` on a copy of the decision. | `TableAccessPolicy.Consent.cs:145-153` |
| 8 | Stored procedures can bypass it: `ClearedResultColumns` of the procedure contract overrides `Mask` to `Clear` per result column (any non-`Deny` level), independent of scope. This is the subject of issue #152. | `GovernedProcedureExecutionService.cs:603-609` |
| 9 | Two callers resolve access without scopes (CDC stream channel and Iceberg federation). They have no token, so base levels apply; this is an existing limit and not changed here. | `StreamRlsPolicyEnforcer.cs:337`, `IcebergRestCatalogFederationService.cs:300` |
| 10 | No scope registry or scope metadata exists. The only scope configuration is `Authentication:EntraId:ReadOnlyScopes` (default `Agent.Read`, `App.Read`), which classifies tokens as read-only and is not the same concept. | `src/Autheris.Domain/Options/Security/JwtOptions.cs:190-195`, `EntraTokenPolicy.cs:82-87`; grep for `RestrictedScope`, `ScopeRegistry`: no hit |
| 11 | Scope names in config may be written as `api://app/Agent.Read`; the token carries the bare name. `EntraTokenPolicy.IsSameScope` strips the prefix. Rule `applicable_scopes` are compared as listed, case-insensitively, against the bare token scopes. | `EntraTokenPolicy.cs:93-103`; `ConsentResolutionService.cs:261-264` |
| 12 | Tokens without a delegated `scp`/`scope` claim (app-only tokens with `roles`) have an empty scope set, so no scope-based rule can restrict them. | `TokenAccessScope.GetScopes` reads `scp` claims only; `GetScopeSet(IEnumerable<Claim>)` also reads `scope` |

Prior work: #116 (scope-only rules fail closed), #127 (catalog and value profiles use the same scope-aware resolution; merged as `fbeb43b3`), F-GOV-15 monotonic hardening. Nothing in `docs/plans` or `docs/features` implements "explicit Clear per scope".

## 3. Assessment of the Issue

**Verdict: sensible with changes.** The use case is real and the gap is a genuine privilege-bleed path (F-GOV-15 purpose statement 4 names exactly this). The issue's behaviour request is right; four changes to its design are needed.

1. **Enforce at the resolver and decision, not per path.** Because of facts 5 and 6 one change in `ResolveAccess` plus `GetEffectiveColumnAccess` covers GraphQL, WebSQL, REST, OData, export, MCP queries, `describe_dataset` (levels, value profiles, synthetic samples), OLAP, aggregations, metrics, analysis and explanation. No per-path code.
2. **A token with several scopes is restricted when any one of its scopes is restricted** (the restrictive scope wins), and then a sensitive column needs an explicit scope-specific `Clear` for **every** restricted scope the token holds. The issue is silent on this.
3. **Close the two bypasses that sit beside the choke point**: access profile `Unmasked` (fact 7) is covered automatically if the decision carries the restriction (R-9); procedure `cleared_columns` (fact 8) must follow the same rule and is coordinated with #152 (R-11).
4. **Config must be validated and normalised** like the read-only scope config (fact 11), and an empty or malformed list must not silently disable the feature in a way that looks active (R-2, R-3).

**Alternatives considered**

| Alternative | Judgement |
|---|---|
| Global config `RestrictedScopes` (issue) | **Chosen for v1.** Smallest change, matches the existing deploy-level scope config (`ReadOnlyScopes`), no schema change. Limit: the data owner cannot toggle it per scope; the platform admin decides which scopes are restricted. That matches the use case (the class "assistant scope" is a platform property). |
| Scope registry or metadata in the consent model (per-scope flag in governance storage) | Rejected for now. Needs schema and baselines in three repositories, an admin API, audit and four-eyes. No scope registry exists today; building one only for this flag is disproportionate. Revisit with `WP-SC-06`/`WP-SC-13` (realm registry) if more per-scope properties appear. The config list is the stable seam: a registry can later feed the same `IRestrictedScopePolicy`. |
| Per-column or per-tag policy "requires scope-explicit Clear" | Orthogonal, not a replacement. It answers "which columns", the issue answers "which callers". The sensitive predicate stays catalog-sensitivity (`IsSensitive`, masking rule); a later tag-based Access Policy (`WP-SC-06`, `#111` tags) can widen that predicate without changing this mechanism (R-12). |
| Default-deny for every read-only or agent scope | Rejected as written (would also block `App.Read`, which is the application scope in the issue). Achievable by configuration: list the agent scope. |
| Require a `Mask` rule per scope (today's workaround) | Fail-open by omission; this is the defect. |

**Size: M** (one resolver change, one decision field, options and validator, about 10 path tests, decision-cache key, procedure coordination). **Dependencies:** none blocking (#116, #127 merged); coordinate with #152 and `WP-SC-06`. **Wave: 2.**

## 4. Requirements

**Configuration**

- **R-1** New option `Governance:RestrictedScopes` (list of strings, default see Q-143-1). Scope entries are normalised exactly like `EntraTokenPolicy.IsSameScope` (trim, bare name after the last `/`, case-insensitive).
- **R-2** Startup validation (fail closed): entries that are empty, whitespace or contain a space reject startup. Duplicates after normalisation are collapsed. An unparsable value rejects startup. `null` or missing means "no restricted scopes" (feature off) and is logged once at information level with the effective list (names only).
- **R-3** A configured restricted scope that appears in no consent rule is allowed (the feature is deny-by-construction). When the list is empty, startup logs one information line "no restricted scopes configured" so operators can see the feature is off (Q-143-1).

**Semantics**

- **R-4** A caller is *restricted* when its scope set (`TokenAccessScope.GetScopeSet`) intersects the normalised restricted list. Callers without scopes are not restricted (fact 12, non-goal G-3).
- **R-5** For a restricted caller, a catalog-sensitive column (same predicate as today: `IsSensitive` or `ColumnMaskingRule`) resolves to `Clear` only when (a) the existing resolution yields `Clear` and (b) for **every restricted scope the caller holds** at least one applicable consent rule for that column with `AccessLevel = Clear` lists that scope in `applicable_scopes`. A base rule `Clear` never satisfies (b). Otherwise the effective level is `Mask`.
- **R-6** Rule (b) is evaluated with the same consent grouping as the level: inside a row-filter group any consent can supply the scope rule; across row-constrained groups every group must supply it (minimum semantics). A consent without column rules (implicit "all Clear") does not supply a scope rule.
- **R-7** The restriction can only lower a level (`Clear` to `Mask`). It never changes `Deny`, never raises a level, and does not touch non-sensitive columns or unrestricted callers. Existing monotonic hardening stays: a scope rule `Mask` or `Deny` for any held scope still tightens, and a scope `Clear` cannot lift a base `Mask` or `Deny`.
- **R-8** The decision carries the restriction. `ConsentResolutionService.ResolveAccess` computes the set of columns that have a scope-explicit `Clear` for the caller's restricted scopes and stores it on `TableAccessDecision` together with a flag "restricted caller". `GetEffectiveColumnAccess` applies R-5. `HasExplicitClear` keeps its meaning for unrestricted callers. The restricted-scope list is read from configuration inside the resolution service (not passed by 20 callers), so no caller can forget it.
- **R-9** Decision copies (`with { ... }`, access-profile application, ABAC explicit levels) keep the restriction. An access profile `Unmasked` does not release a sensitive column for a restricted caller unless R-5(b) holds (Q-143-3).
- **R-10** The decision cache distinguishes restricted from unrestricted callers. The cache context hash already contains the scopes; the restricted list is fixed at startup, so no extra key part is needed as long as a list change requires a restart. If the options become reloadable, the list fingerprint joins the key and the policy epoch (verify in Phase 2).
- **R-11** Stored procedures: the contract `cleared_columns` override must not release a catalog-sensitive result column for a restricted caller unless R-5(b) holds for the mapped source column (`GovernedProcedureExecutionService.cs:603-609`). The rule is stated here; the code change is delivered with #152 if that lands first, otherwise with this issue, so the two do not conflict (Q-143-4).
- **R-12** The "sensitive column" predicate is one internal function so a later tag-based policy can extend it without touching the scope logic.
- **R-13** Catalog and describe paths show the same level as the data path (already true via `CatalogColumnAccessResolver.LevelsFromDecision`); value profiles, min/max, allowed values and synthetic samples are suppressed for `Mask` (existing behaviour, `ColumnValueProfileService`). `describe_my_access` and `QueryAnalysisService` report `Mask` and the missing-rule reason (a clear hint: "needs an explicit Clear rule for scope X").
- **R-14** `QueryAnalysisService` for an impersonated target (scopes unknown, `QueryAnalysisService.cs:133`) must assume the target may be restricted: predicted level for sensitive columns is `Mask` unless a scope-explicit `Clear` exists for every configured restricted scope (a prediction stricter than reality is acceptable).

## 5. Acceptance Criteria

Fixture: table with sensitive column `ssn` (catalog `IsSensitive`), non-sensitive `name`; restricted list `["Agent.Read"]`.

1. **AC-1 (issue core)** Only base rule `Clear` on `ssn`: token `Agent.Read` gets `Mask`, token `App.Read` gets `Clear`, token without scopes gets `Clear`.
2. **AC-2** Base `Clear` plus rule `Clear` with `applicable_scopes: [Agent.Read]`: both tokens get `Clear`.
3. **AC-3** Multi-scope token `Agent.Read App.Read` with only base `Clear`: `Mask`. With a scope `Clear` for `Agent.Read` only: `Clear`. With restricted list `[Agent.Read, Other.Read]` and token holding both, a rule listing only `Agent.Read`: `Mask`; a rule listing both: `Clear`.
4. **AC-4** Base `Clear` plus scope rule `Mask` for `Agent.Read`: `Mask` (unchanged). Base `Mask` plus scope `Clear` for `Agent.Read`: `Mask`. Scope-only `Clear` for `Agent.Read` without base: `Clear` for `Agent.Read`, `Deny` for others (unchanged #116).
5. **AC-5** Non-sensitive `name`: unchanged for every token.
6. **AC-6** Row-filtered consents: group A has scope `Clear`, group B only base `Clear`: restricted token gets `Mask`.
7. **AC-7 (parity across paths)** The same fixture yields the same masking for the restricted token on: GraphQL list and aggregate, WebSQL, REST/OData read, export, MCP `query_*`, `describe_dataset` value profile and synthetic samples (profile suppressed, no sample values), OLAP query, governed aggregation, semantic metric, `describe_my_access`, query analysis. One parameterised test per path; no path may read the base-only decision.
8. **AC-8** Filtering or grouping on a masked `ssn` stays rejected for the restricted token (existing `Clear`-only rule), allowed for the unrestricted token.
9. **AC-9** Access profile `Unmasked` assigned to the person: the restricted token still gets `Mask` for `ssn` (without scope rule); the unrestricted token gets `Clear`.
10. **AC-10** Procedure with `cleared_columns` containing a sensitive column: restricted token gets `Mask` without scope rule; unrestricted gets the contract behaviour (R-11).
11. **AC-11** Configuration: empty list means no behaviour change (all existing tests pass); an entry `api://app/Agent.Read` matches token scope `Agent.Read`; a blank entry rejects startup.
12. **AC-12** Decision-cache test: restricted and unrestricted token of the same person never share a cached decision.
13. **AC-13** Analysis for an impersonated target predicts `Mask` for a base-only `Clear`.

## 6. Security Requirements

- **S-1** Fail closed: any error while computing the scope-explicit set yields `Mask` for the sensitive columns of a restricted caller (never `Clear`).
- **S-2** Restrictive scope wins in every multi-scope combination (R-4, R-5, AC-3). A token cannot unrestrict itself by adding an unrestricted scope.
- **S-3** The mechanism is monotonic: it can only lower levels. No path may use it to raise access.
- **S-4** One resolver, one decision field: no path-local re-implementation (the #127 failure mode). Phase 3 reviews the `with { ColumnAccess = ... }` copies for loss of the restriction.
- **S-5** Scope matching is case-insensitive on bare names and uses the same extraction helper everywhere (`GetScopeSet`); the Phase 2 plan must note the existing difference between `GetScopes(principal)` (claims `scp` only) and `GetScopeSet(claims)` (also `scope`) and use one for all callers.
- **S-6** Audit: no new row values; the masking reason for restricted callers may name the scope and column, never values. Consent changes keep the existing Tier-A audit.
- **S-7** Documented residual risks: app-only tokens have no scope (G-3); CDC and Iceberg callers resolve without scopes (fact 9); `applicable_scopes` lists are free text and a typo means "not cleared" (safe direction).

## 7. Non-Goals

- **G-1** No scope registry, no per-consent "restricted" flag, no schema change, no admin API.
- **G-2** No tag-based or per-column policy (belongs to `WP-SC-06`; R-12 keeps the seam).
- **G-3** No restriction of app-only tokens or roles-based callers.
- **G-4** No change for unrestricted scopes, non-sensitive columns, write paths.
- **G-5** No change to the CDC and Iceberg scope-blind resolution.

## 8. Deviations from the Issue

| # | Issue | This PRD | Reason |
|---|---|---|---|
| D-1 | Silent on multi-scope tokens | Restricted wins; explicit rule needed for every restricted scope held | Fail closed |
| D-2 | Config key `RestrictedScopes` (flat) | `Governance:RestrictedScopes` with startup validation and normalisation of `api://` forms | Consistency with `ReadOnlyScopes`, fail-closed config |
| D-3 | Only consent rules mentioned | Also access profile `Unmasked` and procedure `cleared_columns` | Otherwise the restriction is bypassable |
| D-4 | Implicit per-path | One resolver and decision change, all paths via the choke point | #127 lesson |

## 9. Open Owner Questions (recommended defaults)

- **Q-143-1** Default value of `RestrictedScopes`. Recommended: `["Agent.Read"]` (secure by default, no production yet, F-GOV-15 names it as the assistant scope; operators clear it explicitly). Alternative: empty (strictly opt-in as the issue implies).
- **Q-143-2** Multi-scope rule: explicit Clear for every restricted scope held (recommended) versus any one of them.
- **Q-143-3** Should an access profile `Unmasked` override the restriction for a restricted token? Recommended: no (the point is an explicit decision for that scope); owner may allow it by also adding the scope rule.
- **Q-143-4** Procedure `cleared_columns` for restricted callers: recommended to apply R-5 (needs the scope rule), delivered together with #152.
- **Q-143-5** Should the restricted scope list also be exposed (read-only) in `describe_my_access` so an agent can tell why a column is masked? Recommended: yes, as `restrictedScope: true` without listing other scopes.

## 10. Proposed Wave

Wave 2 (urgent small/medium feature, security hardening). Order: after the #152 PRD decision so the procedure semantics are settled once; independent of #107, #111, #129. One Phase 4 work package.

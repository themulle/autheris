# PLAN-OI-708: never return the row filter definition to read-only tokens

- Date: 2026-11-13 · Base: `origin/main` @ `1ad6e342` · Issue: [#708](https://github.com/themulle/autheris/issues/708)
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) (rank 24).
- Freeze (PRD 5.1): security fix; it removes a disclosure and adds no parameter, field, endpoint, option or switch. No
  `freeze-exception` needed.
- Size: 4 net lines in `src/`.

## Phase 1: Requirement

- **Problem:** MCP `describe_my_access` and GraphQL `myAccess` (both via `IEffectiveAccessExplanationService`) return the
  combined row filter SQL, with literal values, in `activeRowFilters` to data owners, delegates and governance admins.
  The decision depends on the person only, not on the token. An agent that acts for a data owner with a read-only token
  forwards the filter definition to its model provider.
- **Requirement REQ-708-1:** a read-only token (`TokenAccessScope.IsReadOnly`, the gateway-issued marker for delegated
  agent scopes, app-only tokens and read-only MCP sessions) or a restricted scope (decision carries a
  `RestrictedScopeClearance`, default `Agent.Read`) always receives the constant
  `"Row filter applies (defined by data owner)."`, whoever the user is.
- **Requirement REQ-708-2 (counter-check):** the same owner or admin with an ordinary token still sees the definition.
- **Unchanged:** filters that depend on a caller attribute keep their own constant for everyone (V-129-10).

## Phase 2: Design

- In `EffectiveAccessExplanationService.ExplainMyAccessAsync`, `canSeeDefinition` becomes
  `!principal.IsReadOnly() && !restrictedScope && (isAdmin || IsOwnerOrDelegate)`. The token checks come first, so a
  read-only call also skips the ownership lookup.
- `restrictedScope` is the existing local (`decision.ScopeClearance is not null`); no new state.

## Phase 3: Security review

| Check | Result |
|---|---|
| Fail-closed | Narrows disclosure only; every new branch yields the constant. The ownership lookup failure still yields the constant. |
| Forgery | `IsReadOnly` accepts the marker only with the gateway issuer `Autheris:TokenAccessScope`; a token-supplied look-alike claim is ignored (test pins that it neither grants nor removes anything). |
| Other paths | GraphQL `myAccess` (`Query.Consent.cs`) and MCP `describe_my_access` (`McpDatasetCatalog`) are the only consumers of this service, so both are covered. The Envoy ext-authz path already never returns the SQL (SEC C-1). |
| Tests | `Issue117AccessExplanationDisclosureTests` (`[Trait("Category","Security")]` on the new cases): owner with read-only token, admin with read-only token, owner with restricted scope receive the constant; forged marker leaves the owner view unchanged; the existing owner and admin cases (ordinary token) still see the definition. |

## Phase 4 and 5

- Branch `fix/708-row-filter-readonly-tokens`; red test first (3 of the 4 new cases failed), then the condition.

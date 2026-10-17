# PLAN-GOV-158-INC0: Access Explanation Uses Only Grants the Caller Holds

**Plan ID:** PLAN-GOV-158-INC0
**Source:** [`REQ-GOV-158`](2026-10-17-req-issue-158-consent-description.md) section 5.1 (R-0.1 to R-0.4);
[`INTAKE-2026-10-17B`](2026-10-17-issue-intake-assessment-2.md) rank 1.10 (owner accepted OQ-16..25).
**Scope:** INC-0 only. INC-1 (stored description, `consentDescriptions`, write paths) is wave 2 and out of scope.
**Size:** S. **Code base verified:** `origin/main` `fa7e29c0`.

## 1. Context

`ExplainMyAccessAsync` backs both the MCP tool `describe_my_access` and the GraphQL field `myAccess`. On the `ALLOWED`
path it derives `consentDescription` and `validTo` from every active allow consent the repository returned, including
role grants for roles the caller does not hold. Result: role names of foreign grants are disclosed, and `validTo` can be
too early (minimum over foreign grants) or too late (raw `ValidTo` instead of the clamped `EffectiveValidTo`).

## 2. Verified Code Facts (`origin/main`)

| # | Fact |
|---|---|
| F1 | `src/Autheris.Application/Governance/Services/EffectiveAccessExplanationService.cs:34-167` `ExplainMyAccessAsync`. Inputs already computed: `userSid` (`:41`, null -> early `DENIED` `:42-54`), `roles` (`:58`), `groupSids` (`:68`), `allUserSids` (`:69`), `now` (`:78`), `activeConsents` filtered by tenant (`:79-81`). |
| F2 | Bug: `applicableConsents` filters only `Effect == Allow && IsActive(now)` (`:146-148`); `validTo = Min(c.ValidTo)` (`:150-152`); description = first consent with non-empty `RoleName`, else `$"Active grant for subject {userSid}"` (`:154-155`). |
| F3 | Decision side matches grantees: `ConsentResolutionService.ResolveAccess` `:31-33` filters `IsActive(now) && TableIdentifier == table && IsSubjectMatch(...)`; `IsSubjectMatch` `:313-320` calls `GranteeMatcher.Matches(GranteeType, GranteeSid, RoleName, RoleId, userSid, groupSids, roles, allUserSids)`. |
| F4 | `src/Autheris.Application/Policy/GranteeMatcher.cs` `Matches`: User/ServicePrincipal by SID or `allUserSids`; Group by `groupSids`; Role by `RoleName` or `RoleId.ToString()` in `roles`; unknown type -> `false`; throws on null `groupSids`/`roles`. |
| F5 | `Consent.EffectiveValidTo` (`src/Autheris.Domain/Model/ConsentModels.cs:103`, via `IndividualGrantLimits.EffectiveConsentValidTo`); `IsActive` (`:105-111`) already uses it. |
| F6 | `GetGroupSids`, `GetUserRoles`, `GetAllUserSids` (`src/Autheris.Domain/Common/Sid.cs:114,139,153`) return non-null `HashSet<>` (empty when claims are missing). |
| F7 | Both surfaces delegate unchanged to the service: MCP `McpDatasetCatalog.DescribeMyAccessAsync` (`src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs:575`), GraphQL `QueryTypes` (`src/Autheris.GraphQL/Types/QueryTypes.cs:98`). DTO `EffectiveTableAccessDto` (`src/Autheris.Domain/Model/EffectiveTableAccessDto.cs`) unchanged. |
| F8 | Existing tests on this service: `tests/Autheris.Tests.Unit/Governance/Issue117AccessExplanationDisclosureTests.cs`, `Issue127ScopeAwareLevelTests.cs` (`AccessExplanation_PlainScopeClaim_AppliesScopeRule`, real `ConsentResolutionService`), `Wave2GovernanceTests.cs` (`Issue35_*`, empty consent list with mocked allow decision). |

## 3. Design Decisions

1. **Same matching as the decision (R-0.1).** Replace `:146-148` with a private static helper
   `MatchingAllowConsents(activeConsents, table, now, userSid, groupSids, roles, allUserSids)` returning consents with
   `Effect == Allow && IsActive(now) && TableIdentifier == table && GranteeMatcher.Matches(c.GranteeType, c.GranteeSid,
   c.RoleName, c.RoleId, userSid, groupSids, roles, allUserSids)`. This is the predicate of F3 restricted to allows, so
   the explanation never reads a consent the decision ignored. Role grants match by name or role id; group grants only
   for groups in the token; user grants by primary SID or linked SIDs.
2. **Time validity.** Expired (`now >= EffectiveValidTo`), not-yet-valid (`ValidFrom > now`) and revoked consents are
   excluded via `IsActive(now)` even if the repository returned them. The single `now` of `:78` is reused.
3. **`validTo` (R-0.2).** `Min(EffectiveValidTo)` over the matching allow consents: the earliest moment at which the
   caller's current effective access changes. Default taken from the PRD (minimum, not maximum); a later grant may keep
   partial access, which INC-1's `consentDescriptions` list can explain. Clamped legacy individual grants are reflected.
4. **Description (R-0.4, interim).** `RoleName` of a matching consent with `GranteeType == Role` and non-empty
   `RoleName`, chosen deterministically (ordinal minimum of `RoleName`, so repository order cannot change the output);
   otherwise the generic text with unchanged wording. A non-role consent's stale `RoleName` is never shown.
5. **Empty match on the `ALLOWED` path.** Cannot occur with the real resolver (it requires at least one matching
   allow), but can with clock skew or a substituted resolver: return `validTo = null` and the generic text. The decision
   stays authoritative; no extra data is shown. Columns and row-filter output are unchanged.
6. **Fail-closed inputs.** Missing `userSid` keeps the existing early `DENIED`. Missing group/role claims yield empty
   sets (F6), so fewer grants match, never more. `GranteeType` values outside the switch never match (F4). No new
   null paths: the helper receives the non-null sets already in scope.
7. **#117 parity unchanged.** Only lines `:146-155` change; denied, unknown and hidden-table paths, the DTO and both
   surface call sites are untouched. MCP and GraphQL stay identical because both delegate to the same method (F7).
8. **No new logging, options, DDL or public API.**

## 4. Work Package WP-158-0 (one TDD step)

**Production:** `src/Autheris.Application/Governance/Services/EffectiveAccessExplanationService.cs` only
(add `using Autheris.Application.Policy;`, helper, replace `:146-155`).
**Tests:** new `tests/Autheris.Tests.Unit/Governance/Issue158ExplainAccessMatchingTests.cs` (real
`ConsentResolutionService`, substituted repositories as in F8 `Issue127` pattern). Existing tests must stay green
unchanged.

| Test | Expectation |
|---|---|
| `RoleGrantOfOtherRole_IsNotDisclosedAsDescription` (regression, Red today) | caller role `A`; allow to role `B` (+1 d, listed first) and role `A` (+30 d): description `A`, never `B` |
| `RoleGrantOfOtherRole_DoesNotShortenValidTo` (Red today) | same fixture: `validTo` = `A`'s `EffectiveValidTo` (+30 d) |
| `OnlyForeignRoleGrantPlusOwnUserGrant_UsesGenericDescription` (Red today) | user grant for caller + role `B` grant: generic text, `validTo` from user grant |
| `LegacyIndividualGrant_ValidTo_IsEffectiveValidTo` (Red today) | user grant with `ValidTo` beyond the ceiling: `validTo == EffectiveValidTo < ValidTo` |
| `MultipleMatchingGrants_ValidTo_IsEarliestEffectiveValidTo` | user +10 d, group +20 d, role +30 d all matching: +10 d |
| `GroupGrant_CallerNotMember_IsIgnored` / `GroupGrant_Member_Counts` | group SID absent/present in token |
| `UserGrant_OtherUser_IsIgnored` / `UserGrant_LinkedUserSid_Counts` | other SID ignored; SID in `allUserSids` counts |
| `RoleGrant_MatchedByRoleId_Counts` | `RoleId` in roles claim matches; name shown |
| `ExpiredAndNotYetValidMatchingGrants_AreIgnored` | repository returns them; `validTo` from the active grant only |
| `DenyAndForeignTableConsents_AreIgnoredForValidTo` | deny consent and other-table allow do not affect `validTo`/description |
| `NonRoleConsentWithRoleName_IsNotUsedAsDescription` | user grant carrying `RoleName`: generic text |
| `DescriptionChoice_IsIndependentOfRepositoryOrder` | two matching role grants, both orders: same description |
| `CallerWithoutRoleClaims_RoleGrantsNeverMatch` | role grants only, no role claims: `DENIED` (decision) and no role name anywhere in the DTO |
| `AllowedDecision_NoMatchingConsents_ReturnsNullValidToAndGenericText` | substituted allow decision, only foreign consents |
| `McpDescribeMyAccess_EqualsServiceOutput_ForRegressionFixture` | `McpDatasetCatalog.DescribeMyAccessAsync` result equivalent to direct service call |

## 5. Estimate and Files Touched

| File | Kind | LOC |
|---|---|---|
| `src/Autheris.Application/Governance/Services/EffectiveAccessExplanationService.cs` | production, modify | ~25 |
| `tests/Autheris.Tests.Unit/Governance/Issue158ExplainAccessMatchingTests.cs` | test, new | ~280 |

Overlap: INC-1 (wave 2) will touch the same lines `:146-155`; no other wave-1 plan touches this file per the intake.

## 6. Risks

- **R1 Clock skew between decision and explanation** (two `UtcNow` reads): at most a grant expiring between the calls
  is dropped from `validTo`; covered by decision 5. Low.
- **R2 Behaviour change for clients reading `validTo`:** values become later or earlier but correct; no production
  yet (owner rule 2026-10-13). Low.
- **R3 Drift from `ConsentResolutionService` predicate:** the helper duplicates F3's predicate. Mitigation: the
  `CallerWithoutRoleClaims_*` and group/user tests run against the real resolver. Exposing a shared predicate is
  deferred to INC-1 (default taken, keeps INC-0 to one file).

## 7. Open Questions

None blocking. Defaults taken: minimum `EffectiveValidTo` (R-0.2); interim role-name description restricted to
matching `Role` grants with ordinal tie-break (R-0.4); generic text wording unchanged.

## Phase 3 Security Review

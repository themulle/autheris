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
| F3 | Decision side matches grantees: `src/Autheris.Application/Services/ConsentResolutionService.cs` `ResolveAccess` `:31-33` filters `IsActive(now) && TableIdentifier == table && IsSubjectMatch(...)`; `IsSubjectMatch` `:313-320` calls `GranteeMatcher.Matches(GranteeType, GranteeSid, RoleName, RoleId, userSid, groupSids, roles, allUserSids)`. |
| F4 | `src/Autheris.Application/Policy/GranteeMatcher.cs` `Matches`: User/ServicePrincipal by SID or `allUserSids`; Group by `groupSids`; Role by `RoleName` or `RoleId.ToString()` in `roles`; unknown type -> `false`; throws on null `groupSids`/`roles`. |
| F5 | `Consent.EffectiveValidTo` (`src/Autheris.Domain/Model/ConsentModels.cs:103`, via `IndividualGrantLimits.EffectiveConsentValidTo`); `IsActive` (`:105-111`) already uses it. |
| F6 | `GetGroupSids`, `GetUserRoles`, `GetAllUserSids` (`src/Autheris.Domain/Common/Sid.cs:114,139,153`) return non-null `HashSet<>` (empty when claims are missing). |
| F7 | Both surfaces delegate unchanged to the service: MCP `McpDatasetCatalog.DescribeMyAccessAsync` (`src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs:575`), GraphQL `QueryTypes` (`src/Autheris.GraphQL/Types/QueryTypes.cs:98`). DTO `EffectiveTableAccessDto` (`src/Autheris.Domain/Model/EffectiveTableAccessDto.cs`) unchanged. |
| F8 | Existing tests on this service: `tests/Autheris.Tests.Unit/Governance/Issue117AccessExplanationDisclosureTests.cs`, `Issue127ScopeAwareLevelTests.cs` (`AccessExplanation_PlainScopeClaim_AppliesScopeRule`, real `ConsentResolutionService`), `Wave2GovernanceTests.cs` (`Issue35_*`, empty consent list with mocked allow decision). |

## 3. Design Decisions

1. **One shared applicability predicate (R-0.1, SEC-GOV158-01).** In `ConsentResolutionService` extract
   `internal static bool IsApplicable(Consent c, TableIdentifier table, DateTimeOffset now, Sid userSid,
   IReadOnlySet<Sid> groupSids, IReadOnlySet<string> roles, IReadOnlySet<Sid>? allUserSids)` =
   `c.IsActive(now) && c.TableIdentifier == table && IsSubjectMatch(...)`; `ResolveAccess` `:31-33` calls it (behaviour
   unchanged). The explanation's private static helper `MatchingAllowConsents(...)` replaces `:146-148` and is
   `Effect == Allow && ConsentResolutionService.IsApplicable(...)`, applied to the tenant-filtered `activeConsents`
   (`:79-81` stays first; SEC-GOV158-02). No path, including the empty match, falls back to unfiltered consents. The
   explanation therefore never reads a consent the decision ignored, by construction. Role grants match by name or role id; group grants only
   for groups in the token; user grants by primary SID or linked SIDs.
2. **Time validity.** Expired (`now >= EffectiveValidTo`), not-yet-valid (`ValidFrom > now`) and revoked consents are
   excluded via `IsActive(now)` even if the repository returned them. The single `now` of `:78` is reused.
3. **`validTo` (R-0.2).** `Min(EffectiveValidTo)` over the matching allow consents: the earliest moment at which the
   caller's current effective access changes. Default taken from the PRD (minimum, not maximum); a later grant may keep
   partial access, which INC-1's `consentDescriptions` list can explain. Clamped legacy individual grants are reflected.
4. **Description (R-0.4, interim).** `RoleName` of a matching consent with `GranteeType == Role` and non-empty
   `RoleName` (non-whitespace), chosen deterministically (minimum by `StringComparer.Ordinal`, so repository order
   cannot change the output; SEC-GOV158-03); a `RoleId`-matched grant shows its stored `RoleName` (S-2 accepted);
   otherwise the generic text with unchanged wording. A non-role consent's stale `RoleName` is never shown.
5. **Empty match on the `ALLOWED` path.** Cannot occur with the real resolver (it requires at least one matching
   allow), but can with clock skew or a substituted resolver: return `validTo = null` and the generic text. The decision
   stays authoritative; no extra data is shown. Columns and row-filter output are unchanged.
6. **Fail-closed inputs.** Missing `userSid` keeps the existing early `DENIED`. Missing group/role claims yield empty
   sets (F6), so fewer grants match, never more. `GranteeType` values outside the switch never match (F4). No new
   null paths: the helper receives the non-null sets already in scope.
7. **#117 parity unchanged.** Only lines `:146-155` change; denied, unknown and hidden-table paths, the DTO and both
   surface call sites are untouched. MCP and GraphQL stay identical because both delegate to the same method (F7).
8. **No new logging, audit event, exception text, options, DDL or public API** (SEC-GOV158-06). `IsApplicable` is
   `internal`; `Autheris.Tests.Unit` already has `InternalsVisibleTo` (`Autheris.Application.csproj:38`).

## 4. Work Package WP-158-0 (one TDD step)

**Production:** `src/Autheris.Application/Services/ConsentResolutionService.cs` (extract `IsApplicable`, call it
from `ResolveAccess`) and `src/Autheris.Application/Governance/Services/EffectiveAccessExplanationService.cs`
(helper on top of `IsApplicable`, replace `:146-155`).
**Tests:** new `tests/Autheris.Tests.Unit/Governance/Issue158ExplainAccessMatchingTests.cs` (real
`ConsentResolutionService`, substituted repositories as in F8 `Issue127` pattern). Existing tests, including all
resolver and `Issue117*` tests, must stay green unchanged. Security tests carry `[Trait("Category","Security")]`.

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
| `AllowedDecision_NoMatchingConsents_ReturnsNullValidToAndGenericText` | substituted allow decision, only foreign consents |
| `McpDescribeMyAccess_EqualsServiceOutput_ForRegressionFixture` | `McpDatasetCatalog.DescribeMyAccessAsync` result equivalent to direct service call |
| T-GOV158-S01 `ForeignRoleGrant_NameAbsentFromEntireDto` | caller role A, allows to A and B: no `B` in any field of the serialized DTO |
| T-GOV158-S02 `ForeignRoleGrant_ExpiryDoesNotInfluenceValidTo` | B expires earlier; `validTo` == A's `EffectiveValidTo` |
| T-GOV158-S03 `NoRoleClaims_OwnUserGrantPlusForeignRoleGrant_AllowedWithoutRoleName` | replaces the former no-role-claims test (S-3): real resolver, `ALLOWED`, generic text, `validTo` from the user grant |
| T-GOV158-S04 `UnknownGranteeType_NeverMatchesNorDiscloses` | `(GranteeType)99` consent with `RoleName` ignored by explanation and decision |
| T-GOV158-S05 `ExplanationApplicability_ParityWithResolver_Theory` | grantee type x match/non-match x active/expired/not-yet-valid/revoked x table: helper set == resolver allow set |
| T-GOV158-S06 `ValidTo_BoundaryExactlyAtEffectiveValidTo_IsExcluded` | consent expiring at `now` does not count |
| T-GOV158-S07 `GroupGrant_NotInToken_NotDisclosed` | foreign group grant with `RoleName` contributes nothing |
| T-GOV158-S08 `OtherTenantConsent_NeverUsed` | other-tenant consent from substituted repository ignored |
| T-GOV158-S09 `DeniedAndUnknownTablePaths_UnchangedByFix_NoExistenceOracle` | hidden and unknown table give identical DTOs (#117) |
| T-GOV158-S10 `RoleGrantMatchedByRoleId_ShowsOnlyCallersHeldRoleGrant` | id-matched grant shown, other-id grant not |
| T-GOV158-S11 `DescriptionAndValidTo_IndependentOfRepositoryOrder` | both orders yield equal DTOs |
| T-GOV158-S12 `McpAndGraphQlMyAccess_ShareServiceOutput_NoForeignRole` | MCP result for the regression fixture has no foreign role name |

Rows S01, S02 and S10 may be implemented as the security-tagged form of the overlapping functional rows above
(no duplicate tests required).

## 5. Estimate and Files Touched

| File | Kind | LOC |
|---|---|---|
| `src/Autheris.Application/Services/ConsentResolutionService.cs` | production, modify | ~10 |
| `src/Autheris.Application/Governance/Services/EffectiveAccessExplanationService.cs` | production, modify | ~25 |
| `tests/Autheris.Tests.Unit/Governance/Issue158ExplainAccessMatchingTests.cs` | test, new | ~400 |

Total about 435 LOC (35 production, 400 test); size stays S. Overlap: INC-1 (wave 2) will touch the explanation
lines `:146-155`. Per the intake, no other wave-1 plan touches either production file; the PM should still check
`ConsentResolutionService.cs` against the other wave-1 plans.

## 6. Risks

- **R1 Clock skew between decision and explanation** (two `UtcNow` reads): at most a grant expiring between the calls
  is dropped from `validTo`; covered by decision 5. Low.
- **R2 Behaviour change for clients reading `validTo`:** values become later or earlier but correct; no production
  yet (owner rule 2026-10-13). Low.
- **R3 Drift from the resolver predicate:** removed by SEC-GOV158-01 (one shared `IsApplicable`); T-GOV158-S05
  guards the parity. Residual risk: a resolver refactor changing `ResolveAccess` behaviour; covered by existing
  resolver tests staying unchanged. Low.

## 7. Open Questions

None blocking. Defaults taken: minimum `EffectiveValidTo` (R-0.2); interim role-name description restricted to
matching `Role` grants with ordinal tie-break (R-0.4); generic text wording unchanged.

## Phase 3 Security Review

**Reviewer:** `csharp-security-expert`, 2026-10-17. **Verified against:** `origin/main`.
**Verdict: APPROVED WITH CONDITIONS.** The fix removes a real disclosure (foreign role names, foreign expiries) and only
narrows what is shown. No control is weakened.

### Verified code facts
- F1, F2, F4, F5, F6 confirmed. F3 path is wrong: the resolver is `src/Autheris.Application/Services/ConsentResolutionService.cs`
  (`ResolveAccess` `:31-33`, `IsSubjectMatch` `:313`), not `Governance/Services/`. Fix the reference.
- The decision applies `IsActive(now) && TableIdentifier == table && IsSubjectMatch` with its own `UtcNow`. Tenant filtering
  is done only by the explanation service (`:79-81`), before the decision. Scope claims affect column/rule evaluation, not
  which consents are applicable, so the plan's predicate is equivalent for allows.

### Findings
| Id | Sev | Finding |
|---|---|---|
| S-1 | Medium | R3 (drift): the helper re-states the decision predicate. It is equivalent today, but a future change to `IsSubjectMatch`/`IsActive` use would re-open the disclosure silently. The reuse is cheap now (one extra private-to-internal change), so deferring to INC-1 is not justified. |
| S-2 | Low | A `Role` grant matched by `RoleId` shows the consent's stored `RoleName`, which may differ from the caller's claim. It is a role the caller holds by id, so not a foreign disclosure; the label may be stale. Accepted. |
| S-3 | Low | `CallerWithoutRoleClaims_RoleGrantsNeverMatch` ends in `DENIED`, so it does not exercise the new helper on the `ALLOWED` path. A mixed fixture (own user grant + foreign role grant, no role claims) is needed. |
| S-4 | Low | `validTo` = minimum over matching allows may be earlier than later grants; shows only the caller's own grants. No disclosure. Accepted. |
| S-5 | Info | Missing claims give empty non-null sets (F6), unknown `GranteeType` never matches (F4), missing `userSid` keeps early `DENIED`. Fail-closed confirmed. #117 paths (unknown, hidden, denied) are untouched. No new logging. |

### Binding conditions
- **SEC-GOV158-01** Do not duplicate the predicate. Extract one `internal static` method in `ConsentResolutionService`
  (for example `IsApplicable(Consent, TableIdentifier, DateTimeOffset, Sid, IReadOnlySet<Sid>, IReadOnlySet<string>, IReadOnlySet<Sid>?)` =
  `IsActive(now) && TableIdentifier == table && IsSubjectMatch(...)`), call it from `ResolveAccess` and from the explanation
  helper. Production scope grows by this one file (~10 LOC); update plan sections 3.1, 4, 5 and R3 accordingly. The explanation
  adds only `Effect == Allow`. Existing resolver tests must stay green unchanged.
- **SEC-GOV158-02** `description` and `validTo` are computed only from consents that pass SEC-GOV158-01 and the tenant filter
  (`:79-81` stays before the helper). No fallback to the unfiltered `activeConsents` anywhere, including the empty-match case.
- **SEC-GOV158-03** The role-name description is taken only from a matching `GranteeType.Role` consent with non-whitespace
  `RoleName`, chosen with `StringComparer.Ordinal`. Never from `User`/`Group`/`ServicePrincipal` or unknown types.
- **SEC-GOV158-04** `validTo` uses `EffectiveValidTo` of matching consents only, one `now` for the whole method, inclusive
  boundary via `IsActive` (`now == EffectiveValidTo` excluded).
- **SEC-GOV158-05** Empty match on `ALLOWED` returns `validTo = null` and the unchanged generic text. No exception, no
  extra data, no change to `DENIED`/`NotFound`/hidden-table output or to the DTO shape.
- **SEC-GOV158-06** No new logging, audit event or exception message containing role names, SIDs or consent data.
- **SEC-GOV158-07** Fix the F3 path reference in the plan.

### Mandatory security tests (`[Trait("Category","Security")]`, file `Issue158ExplainAccessMatchingTests.cs`)
- **T-GOV158-S01** `ForeignRoleGrant_NameAbsentFromEntireDto` : caller role A; allow to B and A; no `B` string anywhere in the serialized DTO (all fields).
- **T-GOV158-S02** `ForeignRoleGrant_ExpiryDoesNotInfluenceValidTo` : B expires earlier, `validTo` equals A's `EffectiveValidTo`.
- **T-GOV158-S03** `NoRoleClaims_OwnUserGrantPlusForeignRoleGrant_AllowedWithoutRoleName` : mixed fixture, real resolver, `ALLOWED`, generic text, `validTo` from the user grant (covers S-3).
- **T-GOV158-S04** `UnknownGranteeType_NeverMatchesNorDiscloses` : `(GranteeType)99` consent with `RoleName`; ignored by explanation and decision.
- **T-GOV158-S05** `ExplanationApplicability_ParityWithResolver_Theory` : theory over grantee type x match/non-match x active/expired/not-yet-valid/revoked x table; the helper's applicable set equals the resolver's allow set (guards drift).
- **T-GOV158-S06** `ValidTo_BoundaryExactlyAtEffectiveValidTo_IsExcluded` : consent expiring at `now` does not count.
- **T-GOV158-S07** `GroupGrant_NotInToken_NotDisclosed` : foreign group grant with `RoleName` set contributes nothing.
- **T-GOV158-S08** `OtherTenantConsent_NeverUsed` : consent of another tenant returned by a substituted repository is ignored.
- **T-GOV158-S09** `DeniedAndUnknownTablePaths_UnchangedByFix_NoExistenceOracle` : hidden table and unknown table give identical DTOs (#117 parity); existing `Issue117AccessExplanationDisclosureTests` stay green.
- **T-GOV158-S10** `RoleGrantMatchedByRoleId_ShowsOnlyCallersHeldRoleGrant` : id-matched grant shown, other-id grant not.
- **T-GOV158-S11** `DescriptionAndValidTo_IndependentOfRepositoryOrder` : both orders yield equal DTOs.
- **T-GOV158-S12** `McpAndGraphQlMyAccess_ShareServiceOutput_NoForeignRole` : MCP result for the regression fixture carries no foreign role name.

## Changelog

- 2026-10-17 (Phase 4): G-PLAN-REBASE check against the branch: F1-F8 confirmed; F3 path corrected to
  `src/Autheris.Application/Services/ConsentResolutionService.cs` (SEC-GOV158-07). Implemented as planned:
  `ConsentResolutionService.IsApplicable` (internal static) is used by `ResolveAccess` and by the explanation helper
  `EffectiveAccessExplanationService.MatchingAllowConsents` (internal static, so the parity theory T-GOV158-S05 can call it).
  Deviation: the helper is `internal` (not private) for testability; `validTo` and the description use
  `EffectiveValidTo`/ordinal minimum as in sections 3.3 and 3.4. Tests S01, S02, S10 are the security-tagged functional rows.

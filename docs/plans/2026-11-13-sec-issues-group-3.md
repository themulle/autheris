# Phase 3 security review: open-issue plans, priority group 3

- **Date:** 2026-11-14 · **Phase:** 3 (`csharp-security-expert`) · **Base verified:** `origin/main` @ `7a4df959`
- **Reviewed:** [`2026-11-13-plan-issues-catalog-consent-correctness.md`](2026-11-13-plan-issues-catalog-consent-correctness.md) sections #667, #670, #671, #666, #612. Owner defaults `Q-CAT-1..7` are binding (2026-11-13). `Q-SEC1-*` and `Q-SEC2-*` are not confirmed; nothing here depends on them.
- **Ids:** `SEC-OI-<issue>-<n>`. Every test below carries `[Trait("Category","Security")]`. A condition is binding for Phase 4 and Phase 5. SEC-SIMPL-01 applies: advisories by GHSA id only.

| Issue | WP | Verdict | Developer-ready |
|---|---|---|:---:|
| #667 | WP-OI-CAT-2 | APPROVED WITH CONDITIONS (a substring scope match is a live over-grant on the service) | yes |
| #670 | WP-OI-CAT-3 | APPROVED WITH CONDITIONS (authority object must be unforgeable, tenant re-read, no default) | after WP-OI-CAT-2 |
| #671 | WP-OI-CAT-4 | APPROVED WITH CONDITIONS (prune blast radius, legacy rows, force parsing) | after WP-SIMPL-47 PR 1-2 |
| #666 | WP-OI-CAT-1 | APPROVED WITH CONDITIONS (identity rebinding can inherit another entry's grants) | yes |
| #612 | WP-OI-CAT-7 | APPROVED (light, 2 conditions) | yes |

Order unchanged: CAT-2, CAT-3, CAT-4 (SEC-OI-671-1 additionally requires the `grant_source` backfill rule before CAT-4).

## #667 consents/apply authorization

**Verified.** `DeclarativeConsentApplyService.cs:51-57`: `IsInRole("GOVERNANCE_ADMIN"|"CLUSTER_ADMIN"|"DATA_GOVERNANCE_REVIEWER")` or any `scope` claim whose value `Contains("governance:admin")`. The substring branch admits `x-not-governance:admin-y`, and the service is reachable only through the endpoint (`HasAnyRole`), so today the endpoint is the effective control and the service is a weaker second gate (it is stricter on role spelling, looser on scope).
- SEC-OI-667-1: one predicate. The service requires `IGatewayRoleEvaluator.HasAnyRole(caller, GovernanceAdmin, ClusterAdmin)`; no raw role string, no `scope` claim branch, no `DATA_GOVERNANCE_REVIEWER`. Unauthenticated or no-SID callers are denied before any repository call (existing SID rule stays).
- SEC-OI-667-2: the target tenant is derived from the caller (`GetTenantId()`) only. A tenant-bound GovernanceAdmin whose tenant claim is missing or empty has **no** authority (never global); a manifest field naming another tenant is rejected, not honoured. Only canonical ClusterAdmin is global.
- SEC-OI-667-3: `ConsentApplyForbiddenException` maps to 403 `ACCESS_DENIED` with a fixed message that names no role, scope or tenant; the old `SecurityException` text is gone. Dry run uses the same gate.
- Tests: plan names plus `Apply_ScopeSubstring_VariantsRejected` (`xgovernance:admin`, `governance:administrator`), `Apply_TenantBoundAdminWithoutTenantClaim_IsRejected`, `Apply_ManifestNamingOtherTenant_IsRejected`, `Apply_DenialBody_ContainsNoRoleOrTenantName`, `Apply_DryRun_RequiresSameAuthority`.

## #670 consent revoke authority

**Verified.** `GovernanceRepository.ConsentGrants.cs:261-273` throws `UnauthorizedAccessException` (500) for non-owner, non-grantee; the last approver branch (`:347-357`) trusts `ROLES`/`ROLE_MEMBERS` rows, seeded only by `Schema.cs:413,616`. The plan removes that branch (a narrowing) and adds a caller-computed `ConsentRevocationAuthority`. `IsAuthorizedApproverForTableInternalAsync` is shared by approval flows, so removing the role branch also changes who may approve a consent request (narrowing; must be intended and tested).
- SEC-OI-670-1: `ConsentRevocationAuthority` has a private constructor and exactly one factory `FromPrincipal(ClaimsPrincipal)`; it is not a DTO (no `JsonConstructor`, no public setters, never bound from a request or deserialized). An architecture test fails if any type outside `FromPrincipal` constructs it.
- SEC-OI-670-2: no default parameter anywhere (interface, decorator `AttributeValidatingConsentRepository`, tests excepted): the omitted authority must be a compile error, never `ClusterAdmin` or `null` meaning "allowed". `null`/default value = `GranteeOrApproverOnly`.
- SEC-OI-670-3: inside the transaction the consent's tenant is re-read (`ReadRevokeTargetAsync`) and compared with the authority's tenant; a tenant-bound authority with a null tenant never matches. A consent of another tenant answers the existing uniform not-found, before any authorization branch is evaluated (no 403-versus-404 oracle for foreign ids).
- SEC-OI-670-4: `ConsentRevocationForbiddenException` carries a fixed message (no actor SID, no table id; today's message names both). The denial is audited with consent id and actor.
- SEC-OI-670-5: removing the `ROLES`/`ROLE_MEMBERS` branch is tested on the approval path too (`Approve_RoleMembersRowWithoutOwnership_IsDenied`); the seeded `ROLE_MEMBERS` rows become inert and are verified to grant nothing.
- SEC-OI-670-6: `ReplaceConsentsAsync` computes all revocations and the authority check **before** the first write; any denial rolls back the whole manifest (plan test kept).
- Tests: plan names plus `Authority_NotConstructibleOutsideFactory` (architecture), `Authority_TenantBoundWithoutTenantClaim_MatchesNothing`, `Revoke_ForeignTenantConsent_ForbiddenAndUnknownLookLikeEachOther` (byte-equal), `Revoke_DenialMessage_ContainsNoSidOrTable`, `Approve_RoleMembersRowWithoutOwnership_IsDenied`.

## #671 declarative prune

**Verified.** No `grant_source` on main (WP-SIMPL-47 PR 1 adds it). Prune is a mass-revoke path driven by a request body.
- SEC-OI-671-1: only rows with `grant_source = 'DECLARATIVE'` exactly are prunable. Rows with a null, empty or unknown value are never pruned. The PR 1 backfill must not label existing rows `DECLARATIVE` wholesale (only rows provably written by apply, else a legacy or consent-request grant is deleted by the first prune); CAT-4 does not start before this is verified in the PR 1 migration.
- SEC-OI-671-2: the prune set is computed inside the revoke transaction from rows re-read there (not from the dry-run result), scoped by `tenant = caller tenant` **and** `grantee in manifest`, using the same grantee identity resolution as the replace. A grantee entry with an empty grant list is allowed but its prune set is 100 % of that grantee, so it always needs `prune=force`.
- SEC-OI-671-3: `prune` parses strictly: absent or `false` = off, `true`, `force`; any other value is 400, never coerced to true. `dryRun=true` applies the same threshold and reports `force required` without revoking. The >50 % rule uses the grantee's declarative grants counted in the transaction.
- SEC-OI-671-4: bounded: prune set at most 10,000 consents per run (above: 400), manifest size limits unchanged. Concurrent applies for one grantee are serialized (row lock or advisory lock per tenant+grantee) so two manifests cannot each see a stale set.
- SEC-OI-671-5: each revoked consent is audited in the same transaction (required audit write, failure rolls back everything), reason constant `PRUNED_BY_MANIFEST`, fields are consent id, grantee SID hash or id and table id; no manifest content. Dry-run output carries `prunedConsentIds` and table ids only for the caller's tenant, behind the same authority as apply.
- SEC-OI-671-6: the authority is the one from SEC-OI-670-1/3; the revoke reason cannot be set by the request.
- Tests: plan names plus `Prune_LegacyNullGrantSource_IsNeverRevoked`, `Prune_UnknownGrantSourceValue_IsNeverRevoked`, `Prune_InvalidValue_Returns400_NotTrue` (`prune=yes`, `1`, `FORCE ` variants), `Prune_EmptyGrantList_RequiresForce`, `Prune_SetRecomputedInTransaction_IgnoresDryRunSnapshot`, `Prune_ConcurrentApplies_DoNotOverRevoke`, `Prune_AboveCap_Rejected`, `Prune_DryRun_ForeignTenantRowsNotListed`.

## #666 dbt physical name

**Verified** `DbtArtifactStreamingParser.cs:120-122,316-318`: only `name`/`database`/`schema` read; the table identity `(Database, Schema, Name)` is the key of catalog entries and so of every grant, consent, policy and classification. Moving it to `alias`/`identifier` lets a dbt artifact (operator-supplied, but possibly from an automated pipeline) re-point an identity.
- SEC-OI-666-1: an artifact never overwrites a catalog entry that belongs to another origin. Upsert onto an existing identity is allowed only when the entry carries the same dbt unique id; otherwise the node is rejected with `PHYSICAL_NAME_COLLISION` (verify while implementing whether the entry records the unique id; if not, record it in this PR). Otherwise an alias equal to a hand-registered or other-model table inherits its grants.
- SEC-OI-666-2: collision between two nodes in one artifact is deterministic and order independent: all colliding nodes are rejected (none registered), each reported with its sanitized unique id; comparison is case-insensitive where the source dialect folds case.
- SEC-OI-666-3: `alias`/`identifier` pass `DbtIdentifierSanitizer` exactly like `name`; empty or whitespace falls back to `name` only when the field is **absent or null/empty**, never when invalid. Length bound as for `name`. Generated SQL (`SqlEndpointLoader`) renders the identifier through the dialect's identifier quoting, never string concatenation.
- SEC-OI-666-4: the `ByName` relation fallback matches `Name` or `PhysicalName`; more than one match is unresolved (relation skipped, warning), never first-wins. The unique-id path stays authoritative.
- SEC-OI-666-5: `PHYSICAL_NAME_CHANGED` warning and collision reasons contain sanitized identifiers only (Q-CAT-2: no re-key, grants stay on the old entry, which is never reactivated by the sync).
- Tests: plan names plus `Alias_EqualToExistingEntryOfOtherOrigin_IsRejected`, `Alias_CollisionBetweenTwoNodes_RejectsBoth_OrderIndependent`, `Alias_NullOrEmpty_FallsBackButInvalid_Rejects`, `Relation_ByNameFallback_Ambiguous_IsSkipped`, `Warning_ContainsOnlySanitizedIdentifiers`.

## #612 federation explain eligibility

**Verified** `QueryAnalysisService.Sql.cs:357` calls the partitioner without predicate; execution (`CrossSourcePlanner.cs:266-268`) uses it. The change aligns a diagnostic with an existing control and adds no data path.
- SEC-OI-612-1: the decision-for-table lookup treats Deny, missing and exceptions as ineligible; `MaxTablesPerSubtree` is the same option value as execution (non-positive value = execution's own handling, no unbounded subtree). C-6 stays: serialized explain equals today's shape (test pins the property set by reflection).
- SEC-OI-612-2: explain must not evaluate decisions for tables the analysed principal cannot see differently from today (no new decision call per table beyond the existing per-table decision).
- Tests: plan names (`Explain_IneligibleMember_ExposesNoColumnOrReasonDetail`, `Explain_MissingDecision_TreatedAsIneligible`) plus `Explain_DecisionThrows_TableIneligible_NotSubtree`.

## Anything the plans missed

1. #667 scope substring is a real over-grant and a tenant-less tenant-admin must not become global (SEC-OI-667-1/2).
2. #670 authority must be unforgeable and non-defaulted; removing the role branch also narrows approvals (SEC-OI-670-1/2/5).
3. #671 legacy rows and backfill, strict `prune` parsing, in-transaction prune set (SEC-OI-671-1/2/3).
4. #666 identity rebinding can inherit another entry's grants (SEC-OI-666-1/2).

## New owner questions (NOT confirmed)

| Id | Question | Recommended default |
|---|---|---|
| Q-SEC3-1 | #670: removing the `ROLE_MEMBERS` branch also stops DB-role admins from approving consent requests (shared helper). Accept? | Yes (roles are control-plane only, OD-30); approvals go through owner, delegate or token admin |
| Q-SEC3-2 | #666: reject colliding nodes entirely, or keep the first by unique id? | Reject all colliding nodes (order independent; operator fixes the alias) |
| Q-SEC3-3 | #671: cap on prune set per run | 10,000 consents, above = 400 (constant, not an option) |

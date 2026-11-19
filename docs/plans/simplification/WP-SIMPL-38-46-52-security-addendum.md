# WP-SIMPL-38 / 46 / 52 - Phase 3 security addendum (2026-11-18)

Parent plans: [WP-SIMPL-38](WP-SIMPL-38-trino-protocol-clean.md), [WP-SIMPL-46](WP-SIMPL-46-unified-authorization-model.md), [WP-SIMPL-52](WP-SIMPL-52-governance-data-model.md). Reviewer: Phase 3 security expert. Owner instruction: recommended defaults for every open decision.

**Verdict: APPROVED WITH CONDITIONS.** The conditions below are binding. Test names are the minimum set; all carry `[Trait("Category","Security")]`. "Unit" runs locally; "CI" needs a live database or container and runs on GitHub Actions.
Reminder for Phase 4: never write `WP-`, `SEC-` or `OD-` identifiers into comments under `src/Autheris.Api` or `src/Autheris.Application`.

## Verified facts
- `UnifiedPolicyDecisionPoint` builds a new `AccessDecisionPoint` per call with `memoryCache: null`, no audit writer and `AccessTransport.Rest`, then logs denials and stamps `EvaluatedByPdp = true`. The DI `IAccessDecisionPoint` is the same class with cache and audit wired.
- In `AccessDecisionPoint`, the transport changes only: write gating (`WebSql`/`Discovery` only, else `dml_transport_not_wired`) and the `Procedure` object kind. Read decisions are identical for every other transport; the transport is otherwise provenance only (explain/audit).
- `GranteeMatcher.MatchesGrant` never matches a role; hard row policies with a role grantee already deny inside their scope; `ConsentModels.Validate` rejects non-Deny role consents; step 0024 aborts when any unrevoked role-based consent exists.

## A. WP-SIMPL-46 final step (delete the adapter)
- **SEC-46-A1 (transport per consumer):** Use exactly: `TableStreamEndpoints` -> `Rest`; `GovernedDataQueryService`, `GovernedQueryStages`, `CrossSourceRelationResolver`, `GovernedChangeDetectionService` -> `Rest` (they serve REST reads; no new gate depends on it); `GovernedAggregationService`, `SemanticMetricQueryService` -> `Analytics`. Never `Internal` for a caller-facing read. Every call is `AccessOperation.Read`.
- **SEC-46-A2 (parity):** Columns and `SupplementalConsents` pass through unchanged into `AccessRequest`; a consumer must not drop either. `requestedColumns: null` stays null (not an empty list).
- **SEC-46-A3 (no write path via transport):** No migrated consumer may build a request with an Insert/Update/Delete operation; a test pins that the migrated read call sites use `Read`.
- **SEC-46-A4 (provenance):** `EvaluatedByPdp` is set only by `AccessDecisionPoint` itself. Remove any code that stamps it outside `Policy/Pdp`; consumers that accept `PreEvaluatedDecision` keep the `EvaluatedByPdp: true` check unchanged.
- **SEC-46-A5 (required dependency, fail closed):** The decision point is a required constructor argument in all 8 consumers (no `= null`). `GovernedAggregationService` and `SemanticMetricQueryService` must not continue when the decision is absent; an absent decision is a denial, never an allow.
- **SEC-46-A6 (exceptions):** An exception from `DecideAsync` propagates or maps to a denial; it must never be caught and treated as allowed or as "no restrictions". Cancellation is not converted to a denial.
- **SEC-46-A7 (denial logging parity):** The removed denial warning moves into each consumer (or a shared helper) with the same content: table, subject SID, reason codes; no column values, no filter literals, no claims. If the DI decision point already audits denials, no second audit row is written for the same decision.
- **SEC-46-A8 (decision cache):** The now-active cache must key on tenant, subject (SID plus group set), table, operation, requested columns and supplemental consents; it must invalidate on the policy epoch. A decision computed with supplemental consents must never be reused for a request without them (and vice versa).
- **SEC-46-A9 (deletion complete):** `git grep -n "IUnifiedPolicyDecisionPoint\|UnifiedPolicyDecisionPoint" -- src tests` returns nothing; an architecture ratchet asserts the types are gone.

Mandatory tests (Unit unless noted):
- `Security_MigratedConsumers_UseAccessDecisionPointWithExpectedTransport`
- `Security_AggregationAndMetricServices_RequireDecisionPoint_FailClosed`
- `Security_DecisionPointThrows_ConsumerDeniesAndNeverReturnsData`
- `Security_SupplementalConsents_ForwardedToAccessRequest`
- `Security_DecisionCache_DoesNotReuseDecisionAcrossSupplementalConsents`
- `Security_DecisionCache_InvalidatedByPolicyEpochBump`
- `Security_Denial_IsLoggedWithoutValues_AndAuditedOnce`
- `Security_PreEvaluatedDecision_WithoutPdpProvenance_IsReevaluated`
- `Architecture_NoUnifiedPolicyDecisionPointReferences` (architecture project)
- CI: `Security_RestAndAnalyticsReads_DecisionParityAfterAdapterRemoval` (consent allow, deny, row filter and mask give the same result as before on a real database).

## B. WP-SIMPL-38 PR 4 (Trino metadata)
- **SEC-38-B1 (source):** `SHOW`, `information_schema` and `system.jdbc.*` rows come only from `IDiscoveryService` for the caller's tenant (OD-19 visible set); hidden objects and hidden columns appear only for the tenant admin and the data owner. No call to a source connector, catalog sync or `DbConnection` happens in the handler (WP-44 guard stays green).
- **SEC-38-B2 (access status):** Table and column status uses a single `DecideManyAsync(..., AccessOperation.Discover, AccessTransport.Trino)` call per statement over the visible set; a missing, throwing or partial result marks the object not accessible and never accessible. Denied columns are listed with the not-accessible status only if the table is visible; columns the principal has no consent to see are omitted from `system.jdbc.columns` (default: omit) and never appear with type information or masks.
- **SEC-38-B3 (tenant):** The tenant is taken from the authenticated principal bound to the statement; a `table_cat`/schema predicate naming another tenant's catalog yields an empty result, identical to a nonexistent one.
- **SEC-38-B4 (patterns):** `LIKE` patterns are matched in memory with a hand-written matcher (`%`, `_`, the declared `ESCAPE` char), never translated to SQL or regex from user input; pattern length is capped at 256 chars, the `ESCAPE` clause must be exactly one char, more than 8 conjuncts or an `OR`/subquery/function/join/`UNION` -> `NotSupportedException`. The statement regex keeps its timeout.
- **SEC-38-B5 (oracles):** `ORDER BY` only on listed output columns with a fixed ordinal tiebreak; `LIMIT`/`OFFSET`, `COUNT(*)` and aggregates are `NotSupported` (default), so no count reflects hidden objects. Result rows are capped by the existing WebSQL row limit; filtering happens before ordering and paging. A hidden object and a nonexistent object produce byte-identical responses.
- **SEC-38-B6 (unsupported):** Any shape outside the handler grammar, any other `system.*` schema, and any mixed metadata plus user-table statement is `NotSupported` with a fixed message and is never forwarded to a source; the response contains no table name that the caller cannot see.

Mandatory tests (Unit unless noted):
- `Security_SystemJdbcTables_OmitsHiddenAndOtherTenantObjects`
- `Security_SystemJdbcColumns_OmitsDeniedAndHiddenColumns`
- `Security_MetadataListing_DecideManyThrows_MarksNotAccessible`
- `Security_SystemJdbc_UnsupportedShapes_NotSupportedAndNotForwarded` (OR, subquery, join, UNION, COUNT, LIMIT, unknown view, mixed statement)
- `Security_SystemJdbc_LikeEscape_NoRegexOrSqlInjection_AndLengthCapped`
- `Security_SystemJdbc_HiddenVersusMissing_IdenticalResponse`
- `Security_MetadataHandler_PerformsNoSourceMetadataRead` (spy connector, zero calls)
- CI: `Security_TrinoJdbcMetadata_ParityWithDiscoveryEndpoint` (same visible set as the discovery API for the same principal).

## C. WP-SIMPL-52 follow-up (a): remove the Role grantee
Defaults: step `0029_drop_role_grantee` (PostgreSQL and SQL Server, paired `.sql` and `.shape.json`; if 0029 is taken at merge, use the next free number). No conversion and no automatic retirement: converting a role to a group or user would change who is covered, and retiring a Deny would remove a restriction (fail open).
- **SEC-52-C1 (pre-check):** The step aborts with the row id and changes nothing when any active (unrevoked) row has a role grantee in: `consents`, access profile / virtual filter profile (hard row policy) tables, or `access_profile_assignments`. Same pre-check pattern as step 0024. Remediation is an explicit operator action (revoke, or recreate as group/user Deny) before the upgrade.
- **SEC-52-C2 (history):** After the pre-check passes, the step deletes already-revoked role rows (children first, respecting foreign keys) and then drops `consents.role_id` and `consents.role_name`; it is idempotent (`IF EXISTS`) and re-runnable. The step header states that all nodes must run the new build first.
- **SEC-52-C3 (readers fail closed):** Before the step runs, a reader that meets a role grantee in an active row must not drop it silently. Consent Deny rows and hard row policies with a role grantee keep denying inside their scope (existing behaviour); any other active role row makes the decision for that table a denial with a fixed reason code. After removal, an unknown `grantee_type` value in any active row throws on load (the decision then denies); it is never skipped, mapped to another grantee type or treated as "no match".
- **SEC-52-C4 (input refusal):** `GranteeType.Role`/`"role"` is refused with a fixed 400 / `GraphQL` validation error (no echo of the supplied name) in `ConsentApiEndpoints`, the GraphQL consent types, `GovernanceEndpoints.Access` bulk grants, virtual filter endpoints and the consent request pipeline. `DeclarativeConsentApplyService` rejects the entire document before persisting anything (no partial apply, dry-run included). dbt and OpenMetadata ingestion skip that entry, report it in the run summary, never map it to a group or user, and never write it.
- **SEC-52-C5 (model):** Remove the enum member only after all writers and readers are gone; persisted enum text is not renumbered. `GranteeMatcher`, `RowScopeCompiler.HardPolicies`, `EffectiveAccessAnalyzer`, `AuthzMigrationReport` and `ConsentGrants` contain no role branch afterwards; a ratchet asserts `GranteeType` has no `Role` member and `git grep "role_id\|role_name"` outside historic migration steps is empty.
- **SEC-52-C6 (no leakage):** The abort message contains only the step id and the consent id, not names or tenant data.

Mandatory tests:
- Unit: `Security_RoleGranteeInput_Refused_AllEntryPoints` (REST, GraphQL, bulk, virtual filter), `Security_DeclarativeApply_RoleGrantee_RejectsWholeDocumentWithoutWrites`, `Security_DbtAndOpenMetadata_RoleGrantee_SkippedNeverMapped`, `Security_UnknownGranteeType_OnActiveRow_ThrowsAndDecisionDenies`, `Security_ActiveRoleDenyRow_BeforeStep_DeniesTable`, `Architecture_GranteeType_HasNoRoleMember`.
- CI (PostgreSQL and SQL Server containers): `Security_Step0029_ActiveRoleConsent_AbortsWithoutChange`, `Security_Step0029_ActiveRoleProfile_AbortsWithoutChange`, `Security_Step0029_RevokedRoleRows_RemovedAndColumnsDropped_IdempotentRerun`, `Security_Step0029_ShapeJson_MatchesSqlOnBothProviders`.

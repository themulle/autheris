# Plan: Security review fixes, area PERS (persistence)

Source: `docs/plans/security.txt`, findings `PERS-*` with severity HIGH or MEDIUM (PERS-08, PERS-12, PERS-15 are LOW and out of
scope; PERS-12 is covered by the GOV area plan WP-GOV-C (merged in PR #287, history in git)). Verified against branch
`fix/security-review-open-findings` at `55dc7e27`. Breaking changes are allowed (no production); no upgrade migrations: baseline
schema files are changed in place and the caller supplies the newest schema.

## 0. Status of the findings

| ID | Sev | Verdict on HEAD | WP |
|---|---|---|---|
| PERS-01 | M | Confirmed (`@since`/`@from`/`@to` not UTC-normalized; write path also formats `OccurredAt` unnormalized) | F |
| PERS-02 | M | Confirmed (`SqliteGovernanceRepository.Consent.cs:1363` calls `GetConsentRequestAsync` inside the write scope) | A |
| PERS-03 | H | **Fixed by 5bbd74e3** (SoD, `SameApprover`, `StatusAfterApproval == APPROVED`, `state.NextStepNumber` at `:1414-1446`). Regression tests only | A |
| PERS-04 | M | Confirmed (PG `:848`, MSSQL `:883` lack `valid_to > @now`, no limit) | A |
| PERS-05 | M | Confirmed (`:1741-1824`: no already-revoked guard, no transaction, audit without `tx`) | A |
| PERS-06 | M | Confirmed (PK and `DELETE` on raw `subject`; `subject_norm` nullable) | C |
| PERS-07 | H | Confirmed (`IdentityStore.cs:146-156`, `:268-278`, `:342-358`; no cache invalidation in `ScimService.DeleteGroupAsync`) | B |
| PERS-09 | H | Confirmed in the reduced form of the correction (self-approved privileged and customer-realm mappings granted as claims) | B |
| PERS-10 | H | **Superseded by EP D-0** (EP area plan, merged in PR #287, history in git): scoped policy API, engine, repository methods removed | - |
| PERS-11 | M | **Superseded by EP D-0** (same reason) | - |
| PERS-13 | M | Confirmed (`VirtualFilterStore.ApplyAsync` has no expected generation/hash) | E |
| PERS-14 | M | Confirmed (PG `ON CONFLICT (id) DO NOTHING`, MSSQL `WHERE NOT EXISTS`) | D |
| PERS-16 | H | Confirmed for Identity, FunctionalRight, Recertification stores; the `ACCESS_POLICIES` part is superseded by EP D-0 | B |
| PERS-17 | M | Confirmed (TTL-only cache hit, local `TryRemove` only, negative entries cached) | D |
| PERS-18 | M | Confirmed (SQLite `SaveAnchorMonotonic` returns silently; PG/MSSQL write paths skip; PG/MSSQL verify returns false without latch) | F |

## 1. Decisions per finding

**PERS-02.** Add `private static Task<ConsentRequest?> ReadConsentRequestAsync(SqliteConnection conn, SqliteTransaction tx, Guid id, CancellationToken ct)` (same projection as `GetConsentRequestAsync`, incl. steps) and use it in `WithdrawConsentRequestAsync` inside the transaction before the guarded UPDATE. No method may call `ReadScopeAsync`/`WriteScopeAsync` while holding `_writeGate`. Fail-closed: the request is not found inside the tx -> rollback, return false. Red tests (`SqliteInMemoryWithdrawTests`): `Withdraw_OnInMemoryDatabase_CompletesWithinTimeout` (5 s token), `Withdraw_AuditRowReflectsTransactionalState`, `Withdraw_ThenOtherWrite_DoesNotBlock`.

**PERS-03 (verify only).** No code change. Add to `AtomicApprovalFourEyesTests` (SQLite) and the PG/MSSQL integration subclasses: `Atomic_SameApproverSecondCall_Throws`, `Atomic_FirstStepOnFourEyesTable_ThrowsFourEyesNotSatisfied`, `Atomic_CallerStepNumberIgnored_UsesNextStepNumber`. If any already exists, keep it; GQL plan references this verification.

**PERS-04.** PG/MSSQL `GetExpiringConsentsAsync`: add `AND c.valid_to > @now`, `ORDER BY c.valid_to`, and a hard cap. Extend `IGovernanceRepository.GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default)` (all three providers and `AttributeValidatingConsentRepository`; SQLite gets the same cap). `ExtendConsentExpiryAsync` (all providers) rejects consents with `valid_to <= now` with `InvalidOperationException` ("expired consents must be re-requested") - default taken, fail-closed. Red tests in a new `ExpiringConsentsContractBase` (SQLite unit, PG/MSSQL integration): `Expiring_ExcludesAlreadyExpired`, `Expiring_RespectsMaxResults`, `Extend_ExpiredConsent_Throws`.

**PERS-05.** SQLite `RevokeConsentAsync`: one `BeginTransaction` covering authorization read, `SELECT is_revoked` (return early when 1, no audit, no epoch bump), `UPDATE ... WHERE id = @id AND is_revoked = 0` (affected must be 1, else rollback and return), `IncrementTableEpochInternalAsync(conn, table, tx, ct)`, `RecordAuditEventInternalAsync(conn, entry, ct, tx)`; epoch cache invalidation and `OnTransactionCommitted` after commit. Fail-closed: audit or epoch failure rolls the revocation back and rethrows. Red tests (`ConsentRevocationContractBase`, all providers): `Revoke_Twice_KeepsOriginalRevoker_AndReason`, `Revoke_Twice_WritesSingleAuditEvent`, `Revoke_AuditWriteFails_RevocationRolledBack` (SQLite, latched chain violation).

**PERS-06.** Key assignments by normalized subject. Baselines (SQLite `:378`, PG `:388`, MSSQL `:450`): `subject_norm NOT NULL`, PK `(tenant_id, profile_id, subject_type, subject_norm)`. `AccessProfileStore`: upsert conflict target / MERGE `ON` use `subject_norm`; `DeleteAssignmentAsync` deletes `WHERE ... AND subject_norm = @subjectNorm`; on update keep the stored `subject` spelling (do not set `subject` in `DO UPDATE`); remove the `a.subject_norm IS NULL OR` branch (`:87`). Breaking: schema PK change. Red tests in `AccessProfileRepositoryContractBase`: `Assign_DifferentCasing_UpdatesSingleRow`, `Remove_DifferentCasing_RemovesEffectiveAccess`, `UpsertProfile_DifferentCasing_NoDuplicate`.

**PERS-16 (first in WP-B).** New `internal enum SqlDialect { Sqlite, PostgreSql, SqlServer }` and `internal static class DialectValueCodec` in `Persistence/`: `object Bool(SqlDialect d, bool v)` (SQLite `1/0`, PG/MSSQL `bool`), `object Ts(SqlDialect d, DateTimeOffset? v)` (PG UTC `DateTimeOffset`, others `ToUniversalTime().ToString("O")`, null -> `DBNull`), `bool ReadBool(SqlDialect d, DbDataReader r, int i)` (SQLite `GetInt64 != 0`, others `GetBoolean`), `DateTimeOffset? ReadTs(SqlDialect d, DbDataReader r, int i)` (PG `GetFieldValue<DateTimeOffset>`, others `Parse(..., InvariantCulture, RoundtripKind)`). `IdentityStore`, the functional-right part of `RightsAndPoliciesStore`, and `RecertificationStore` map their dialect enum to `SqlDialect` and use the codec for every bool/timestamp parameter and read (`IdentityStore.cs:115-141,262-263,309,410,446,471-485`; `RightsAndPoliciesStore.cs:136-154,412-415`; `RecertificationStore.cs:34-50,197-246`). `AccessProfileStore.Ts/ReadTs` delegate to the codec. Red tests: `IdentityStoreContractBase`, `FunctionalRightStoreContractBase`, `RecertificationStoreContractBase` (round-trip of every column incl. null timestamps and `active=false`) with SQLite subclasses in Unit and `PostgreSql...`/`SqlServer...` subclasses in Integration (`SharedDatabaseServers`); `ScimDeleteUser_OnSqlServer_LocksSubject`.

**PERS-07.** `IdentityStore.DeleteUserAsync`: one transaction deleting `IDENTITY_GROUP_MEMBERS WHERE tenant_id, realm_id, user_key` then the user (locks in `IDENTITY_LOCKS` stay). `DeleteGroupAsync` becomes `Task<IReadOnlyList<string>> DeleteGroupAsync(...)`: in one transaction select member `user_key`s, delete memberships, delete the group, return the keys. `GetGroupMembershipsForUserAsync` inner-joins `IDENTITY_GROUPS` on `(tenant_id, realm_id, group_id)`. `ScimService.DeleteGroupAsync` calls `_roleCache?.Invalidate(realmId, key)` per returned key; `DeleteUserAsync` invalidates the user. No FK (SCIM may add members before the user exists) - default taken. Red tests (`IdentityStoreContractBase`): `DeleteGroup_RemovesMemberships`, `DeleteGroup_RecreatedSameId_HasNoStaleMembers`, `DeleteUser_RemovesMemberships_KeepsLock`, `Memberships_OrphanRow_NotReturned`; unit `ScimService_DeleteGroup_InvalidatesRoleCacheForMembers`.

**PERS-09.** `EnterpriseClaimsTransformation` depends on `IFunctionalRightMapper` instead of `IFunctionalRightRepository` and emits claims only from `GetRightsForGroupsAndRolesAsync` (single authority: four-eyes for privileged, customer-realm ceiling). Defense in depth in `RightsAndPoliciesStore.GetApprovedMappingsForGroupsAndRolesAsync`: `AND approved_by IS NOT NULL AND approved_at IS NOT NULL AND (functional_right NOT IN (@priv0..n) OR UPPER(TRIM(approved_by)) <> UPPER(TRIM(created_by)))` with the privileged list from `FunctionalRight.IsPrivileged`, and case-insensitive group match (`UPPER(group_or_role) IN (...)` with upper-cased parameters). Fail-closed: mapper failure -> no functional-right claims. Red tests: `ClaimsTransformation_SelfApprovedPrivilegedMapping_NoClaim`, `ClaimsTransformation_CustomerRealm_NoAdminClaim`, `ApprovedMappings_SelfApprovedPrivileged_Excluded` (contract, all providers), `ApprovedMappings_GroupCaseDiffers_Matches`.

**PERS-13.** `VirtualFilterChangeSet` gets a required `long ExpectedGeneration`. `VirtualFilterStore.ApplyAsync` first runs `UPDATE VIRTUAL_FILTER_GENERATION SET generation = generation + 1 WHERE id = 1 AND generation = @expected` inside its transaction; 0 rows -> rollback and throw `VirtualFilterConflictException` (HTTP 409 via existing mapping). The trailing unconditional increment (`:156`) is removed. All callers in `VirtualFilterAdministrationService` and the GitOps sync pass `snapshot.Generation` of the snapshot they mutated; no automatic retry for approval operations (the user re-reads). Red tests: `Apply_StaleGeneration_ThrowsConflict`, `ConcurrentApproveAndSave_SecondFails_ApprovedDefinitionKept`, `ConcurrentDeleteAndSave_NoResurrection` (contract, all providers).

**PERS-14.** PG/MSSQL catalog upsert: always insert masking rules with a server-generated `Guid.NewGuid()` id (client id ignored), plain `INSERT` without `ON CONFLICT`/`NOT EXISTS`, assert affected rows == 1 else throw and roll back. Baselines: unique index on `COLUMN_MASKING_RULES(table_column_id)`. SQLite aligns to the same rule (new id, no throw on reused client id). Breaking: rule ids change on every upsert (no references found to `masking_rule_id`). Red tests (`CatalogMaskingContractBase`): `Upsert_ReusedRuleIdAcrossColumns_BothColumnsMasked`, `Upsert_RuleIdOfOtherTable_ColumnMasked`.

**PERS-17.** PG/MSSQL metadata cache entries store the table epoch (`POLICY_EPOCHS`) read in the same query as the metadata. On a hit the repository reads the current epoch (single-row query; the batch path at `PostgreSqlGovernanceRepository.Catalog.cs:219` reads all epochs in one query) and reloads on mismatch; TTL stays as an upper bound. Negative results (table missing) are not cached. Every write that changes cached fields (columns, masking, sensitivity, four-eyes, is_active, row-filter strategy) bumps the table epoch in its transaction - verify and add where missing. Fail-closed: epoch read failure -> bypass cache and load from DB; load failure propagates. Red tests (integration, two repository instances on one DB): `MetadataCache_WriteOnReplicaA_ReplicaBSeesMaskingRuleImmediately`, `MetadataCache_DeactivateOnA_BSeesInactive`, `MetadataCache_MissingTableCreatedOnA_BFindsIt`.

**PERS-01.** New `internal static class AuditTimestamp { public static string ToStorage(DateTimeOffset v) => v.ToUniversalTime().ToString("O"); }` used for every `@since`, `@from`, `@to` parameter (SQLite `:717,785`; PG `:874,949,1155-1156`; MSSQL `:933,1007,1212-1213`) and at the single point where `AuditInsertRow`/SQLite `@occ` strings are produced, so hash input and stored value stay identical. Red tests (`AuditQueryContractBase`): `AuditPage_SinceWithPositiveOffset_IncludesLaterUtcEntries`, `AuditPage_SinceWithNegativeOffset_ExcludesEarlierEntries`, `ChainRange_NonUtcWindow_ReturnsSameRowsAsUtc`, `Write_NonUtcOccurredAt_ChainVerifies`.

**PERS-18.** Write paths (SQLite `SaveAnchorMonotonic`, PG `:539-541` and transaction path, MSSQL `:553-555,850-852`): an invalid stored anchor calls `FlagAuditChainViolation("stored audit anchor invalid")` and throws, so `anchorFailure` is set and Tier-A writes fail with `AuditUnavailableException`. PG/MSSQL `VerifyAuditHashChainAsync` flags the violation on an invalid anchor (as SQLite `:980-983`). `CompositeAuditChainAnchorStore` keeps `Load` returning Invalid when any member is invalid (fail-closed, operator re-seals) and `Save` writes every member whose own anchor is valid. Red tests: `TierAWrite_InvalidStoredAnchor_ThrowsAuditUnavailable` (all providers), `Verify_InvalidAnchor_LatchesViolation_PgMssql`, `Composite_OneCorruptMember_TierAFails_HealthyMembersStillSaved`.

## 2. Work packages (order)

| WP | Findings | Scope | Size |
|---|---|---|---|
| WP-PERS-A | 02, 03 (tests), 04, 05 | Consent store, three providers | M (~350 LOC) |
| WP-PERS-B | 16, then 07, 09 | Codec, Identity/FunctionalRight/Recert stores, ScimService, claims transformation | L (~700) |
| WP-PERS-C | 06 | AccessProfileStore + baselines | S (~150) |
| WP-PERS-D | 14, 17 | Catalog upsert and metadata cache (PG/MSSQL) | M (~400) |
| WP-PERS-E | 13 | VirtualFilterStore + service callers | S (~200) |
| WP-PERS-F | 01, 18 | Audit timestamps and anchor fail-closed | M (~300) |

Order: B (HIGH, broken PG/MSSQL deprovisioning) -> A -> D -> F -> C -> E. A, C, E touch disjoint files and may run in parallel lanes.

Dependencies: WP-B lands after EP WP-EP-1 (removes the policy methods of `RightsAndPoliciesStore`) or rebases onto it; WP-B's codec must exist before GOV WP-GOV-C changes `RecertificationStore.UpdateItemDecisionAsync` (GOV-C uses the codec). PERS-03 verification is shared with GQL-04 (GQL plan). Audit anchor changes in WP-F must not conflict with any audit-area plan touching `SaveAnchorMonotonic`.

## 3. Security conditions for Phase 3

1. No store write that changes security state (revocation, membership, masking rule, filter definition, anchor) may succeed partially: one transaction, affected-row checks, rollback on any failure.
2. Claims for functional rights come only from `FunctionalRightMapper`; the SQL filter is defense in depth, never the only gate.
3. Deleting a SCIM group or user removes its memberships in the same transaction and invalidates the role cache; orphan memberships never resolve to a group.
4. Cache hits for security metadata are validated against the authoritative table epoch; negative results are not cached.
5. An invalid stored audit anchor faults the pipeline and blocks Tier-A acknowledgement on every provider.
6. Every fix ships a provider-parity contract test (SQLite in Unit, PostgreSQL and SQL Server in Integration via Testcontainers).
7. Time comparisons on string-stored timestamps use only UTC-normalized values produced by one helper.

## Changelog

- 2026-10-22 (G-PLAN-REBASE, implementation on `fix/sec-impl-b-gov`): PERS-10/PERS-11 are obsolete (EP D-0 removed the scoped policy API; the dead store methods and `AccessPolicy` model are removed as well). PERS-03 regression tests already exist (`AtomicApprovalFourEyesTests`, `ConsentDecisionStatusGuardContractBase`). PostgreSQL consent revocation (PERS-05) was already transactional with the already-revoked guard; SQLite was brought in line.
- 2026-10-22: PERS-04 `ExtendConsentExpiryAsync` still accepts expired consents: rejecting them contradicts the SEC-SC-20 contract (`SEC_SC_20_Renewal_RestartsTheClampPeriod_AtDay150_AndAfterExpiry`) and the OpenMetadata sync refresh, so only the scan fix (filter, ordering, `maxResults`) is applied. Open decision for the owner.
- 2026-10-22: PERS-13 `VirtualFilterChangeSet.ExpectedGeneration` is nullable (`null` = unconditional, used by direct repository tests); the administration service always sets it.
- 2026-10-22: PERS-14 the unique index on `COLUMN_MASKING_RULES(table_column_id)` is not added to the baselines (shape snapshots would need regeneration); the insert fix does not depend on it. PERS-06 changes the assignment primary key in all three baselines and shape snapshots.
- 2026-10-22: PERS-17 epoch validation applies to the single and the batch read path on PostgreSQL and SQL Server; the writers that change cached fields were not re-audited for an epoch bump.

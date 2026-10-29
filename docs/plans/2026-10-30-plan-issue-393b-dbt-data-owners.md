# Plan: issue #393 part b, data owners from the dbt governance import

- Phase: 2 (architecture). Size: M (about 650 LOC including tests). Phase 3 required (owner = consent approver).
- Source: issue #393 (item 1), triage `2026-10-29-triage-issues-388-393.md` section #393. Part a (mask aliases) merged as #395.
- Branch: `feat/issue-393-dbt-data-owners` from `origin/main` (0e5b62cf).

## Verified code facts (current branch)

- `IDataOwnershipRepository` (`IGovernanceRepository.cs:337`) is read-only plus `DelegateDataOwnershipAsync`; no owner write path.
- `DATA_OWNERS` / `TABLE_OWNERS` rows are only inserted by the demo seed (`*GovernanceRepository.Schema.cs`, `SeedInitialCatalog`). In a non-demo deployment both tables are empty, so the import must be able to create `DATA_OWNERS` rows.
- `DATA_OWNERS(id, ad_sid, ad_account, display_name, email, is_active)`, no tenant column; `id` is parsed as `Guid` on read. `TABLE_OWNERS(id, table_id -> TABLES.id, data_owner_id, owner_role 'PRIMARY'|'DELEGATE')`; `TABLE_OWNERS.id` is never read (TEXT / NVARCHAR(128)).
- Approver resolution (`ResolveDataOwnerIdsInternalAsync`, `IsAuthorizedApproverForTableAsync`) matches `ad_sid`, `ad_account` or `email`, so every one of these three values is approval-relevant.
- Consent approval (`MutationTypes.cs` ~220, ~565): `GovernanceAdmin` / cross-tenant admin approve without an owner check; others need `IsAuthorizedApproverForTableAsync`; consent tenant must equal the caller tenant. `ConsentApprovalPolicy.IsSelfApproval` already blocks requester = approver.
- `POST /api/extensions/dbt/governance` requires `EndpointSecurity.IsGlobalGovernanceAdmin`; manifest sync also allows `DbtAdmin`. The governance import is collect-then-write (`pendingUpserts`, failure result before any write) and checks `IsTenantAllowed(tableId, caller)` per item.
- `describe_dataset` already shows `dataOwner` from `GetDataOwnersForTableAsync` (`McpDatasetCatalog.LoadOwnersAsync`, full view `DisplayName <email>`, summary view display name only).

## Decisions

1. **Write path only via the governance import.** Owners are written only by `IngestGovernanceStreamAsync` (`POST /api/extensions/dbt/governance`, global governance admin). The manifest sync keeps `meta.owner` as `SuggestedOwnerTeam`/`OwnerTeam` and writes no owners (default taken: dbt `meta.owner` is usually a team label, not a principal, and the manifest endpoint admits the lower-privileged `DbtAdmin`). The issue's "configurable meta key in the dbt sync" variant is rejected.
2. **Explicit opt-in.** New option `Gateway:Dbt:ImportDataOwners` (bool, default `false`) on `DbtOptions`; registered in `GatewayConfigurationSchema` so `UnknownGatewayKeyGuard` accepts it. If a payload carries `owner`/`owners` while the flag is off, the import is rejected as a whole (`Success=false`, error `dbt_owner_import_disabled`), no silent skip. Same rejection when the flag is on but the write repository or `IAuditLogRepository` is not registered.
3. **Payload.** Per table item `owner` (object) or `owners` (array of objects, max 10 per table). Object: `sid` (required), `account`, `display_name`, `email`, `role` (`primary` default, or `delegate` -> `'DELEGATE'`). A bare string is accepted only as a lookup key that must resolve to exactly one existing `DATA_OWNERS` row (sid, account or email); otherwise reject. Groups are not supported (`group` key -> reject, `dbt_owner_group_unsupported`); default taken, approver resolution is principal-based.
4. **Format validation (parse phase, pure, `DbtOwnerSpecValidator`).** `sid` 1..256 chars, no whitespace/control characters, not a reserved prefix (`system:`, the forwarded-group prefix from `JwtOptions`, `S-1-5-21-FORWARD-GRP-`); `account` <= 256, same character rules; `email` <= 320 and `MailAddress`-valid; `display_name` <= 200, no control or bidi characters (it reaches LLM callers via MCP). Any violation rejects the whole import with the table and field named.
5. **No self-assignment.** An owner whose `sid`, `account` or `email` matches `caller.Actor.Sid` or any of `caller.Actor.Identifiers` (case-insensitive, via `ConsentApprovalPolicy.IdentifierCandidates`) rejects the import (`dbt_owner_self_assignment`). The importer cannot hand itself, or an alias of itself, owner authority.
6. **Existing owners are immutable through the import; identity collisions reject.** New write interface `IDataOwnerAssignmentRepository` (ISP, implemented by the SQLite, PostgreSQL and SQL Server governance repositories, registered next to `IDataOwnershipRepository` in `GatewayGovernanceServiceExtensions`): `Task<DataOwnerAssignmentResult> ApplyTableOwnerAssignmentsAsync(DataOwnerAssignmentRequest request, CancellationToken ct)`. Inside one transaction it: resolves `table_id` per `TableIdentifier` (missing table -> reject); resolves each owner by `ad_sid`. Existing row: linked as is; if the payload's `account`/`email`/`display_name` differ from the stored row -> reject (`dbt_owner_conflict`), never update (an `email`/`account` change would move approval authority, and rows are shared across tenants). No row: create one with a deterministic id (`Guid` v5-style from `ad_sid`) only if neither `sid`, `account` nor `email` matches any identifier of another row (`dbt_owner_identifier_collision`); `ad_account` defaults to `sid`, `display_name` is required for creation. Inactive existing row (`is_active=0`) -> reject.
7. **Provenance without schema change.** Rows the import creates in `TABLE_OWNERS` get `id = "dbt:" + 32-hex SHA-256(table_id|data_owner_id|owner_role)`. Insert only when no row for (`table_id`, `data_owner_id`) exists, whatever its provenance (idempotent, no duplicates next to manually seeded rows). `DATA_OWNERS` needs no provenance because the import never updates or deletes those rows. No DDL change, so no dependency on #402 (governance migrations); a provenance column was rejected because it would require migration step 0002/0003 and gate this PR on #402.
8. **Merge vs replace.** Default (merge): only adds missing assignments. Replace (`replace=true` / `mode=replace`, existing flag): for each table listed in the payload, `dbt:`-prefixed `TABLE_OWNERS` rows not in the payload's owner set are deleted; non-`dbt:` rows are never touched; tables absent from the payload are untouched. A listed table with `owners: []` in replace mode removes all its `dbt:` rows.
9. **Ordering and atomicity.** Owner specs are parsed and format-validated with the other items; a read-only pre-check (`request.DryRun = true`, same method, transaction rolled back) runs before any metadata write, so a bad owner rejects before `pendingUpserts`. The real apply runs after metadata, filters and profiles and re-validates inside its own transaction (TOCTOU); a failure there returns `Success=false` with `dbt_owner_apply_failed` and writes no owner rows.
10. **Dry run.** `dryRun=true` runs only the pre-check and reports the counts. `DbtGovernanceSyncResult` gains `CreatedDataOwnersCount`, `AssignedTableOwnersCount`, `RemovedTableOwnersCount` (default 0).
11. **Audit.** After a successful apply, one `AuditLogEntry` per change via `IAuditLogRepository.RecordAuditEventsAsync`: `DATA_OWNER_CREATED` (target `data_owner:<id>`), `TABLE_OWNER_ASSIGNED`, `TABLE_OWNER_REMOVED` (target canonical table), `ActorSid = caller.Actor.Sid`, tenant `caller.Tenant`, details `{ table, data_owner_id, role, source: "dbt-governance-import", replace }`. No e-mail in the details (owner id only). Audit write failure: import result `Success=false` + error log (see Phase 3 Q2).
12. **Tenant scoping.** Owner assignments are only accepted for tables that pass `IsTenantAllowed(tableId, caller)` (same check as the rest of the item; a foreign-tenant table rejects). Cross-tenant approval stays blocked by the existing consent-tenant check in the approval mutations.
13. **Visibility.** No change to `describe_dataset`: full view keeps `DisplayName <email>`, summary keeps display names only. Test proves an imported owner appears.

## Work packages

- **WP1 (S) validator and options.** `DbtOwnerSpec` record, `DbtOwnerSpecValidator` (decisions 3-5), `DbtOptions.ImportDataOwners`, schema key. Tests (`tests/Autheris.Tests.Unit/Dbt/DbtOwnerSpecValidatorTests.cs`): `Validate_MissingSid_Rejects`, `Validate_ReservedSidPrefix_Rejects`, `Validate_ControlCharInDisplayName_Rejects`, `Validate_InvalidEmail_Rejects`, `Validate_GroupKey_Rejects`, `Validate_SelfSid_Rejects`, `Validate_SelfIdentifierAlias_Rejects`, `Validate_TooManyOwners_Rejects`; config: `GatewayConfigurationSchema_AcceptsDbtImportDataOwners`.
- **WP2 (M) repository write path, three dialects.** `IDataOwnerAssignmentRepository`, request/result records, SQLite/PostgreSQL/SQL Server implementations (decisions 6-8), DI registration. Tests: SQLite unit `ApplyOwners_CreatesOwnerAndAssignment`, `ApplyOwners_SecondRun_IsIdempotent`, `ApplyOwners_ExistingOwnerDifferentEmail_RejectsAndWritesNothing`, `ApplyOwners_IdentifierCollision_Rejects`, `ApplyOwners_InactiveOwner_Rejects`, `ApplyOwners_MissingTable_Rejects`, `ApplyOwners_Replace_RemovesOnlyDbtRows`, `ApplyOwners_ExistingManualRow_NoDuplicate`, `ApplyOwners_DryRun_RollsBack`; PostgreSQL and SQL Server Testcontainers integration with the same scenarios plus a golden of the issued SQL (shared core pattern from ARCH-MNT-22 where it fits).
- **WP3 (M) import integration and audit.** Parse `owner`/`owners` in `IngestGovernanceStreamAsync`, pre-check, final apply, result counts, audit (decisions 2, 9-12). Tests (`tests/Autheris.Extensions.Tests/DbtGovernanceOwnerImportTests.cs`): `Import_FlagOff_WithOwner_RejectsWholeImport`, `Import_FlagOn_AssignsOwner_DescribeDatasetShowsDataOwner` (issue acceptance via `McpDatasetCatalog`), `Import_InvalidOwner_WritesNoMetadata`, `Import_SelfAssignment_Rejects`, `Import_ForeignTenantTable_Rejects`, `Import_DryRun_ReportsCountsWritesNothing`, `Import_Replace_RemovesStaleDbtOwner_AuditsRemoval`, `Import_AuditWriteFails_ReportsFailure`, `Import_ImportedOwnerCanApproveConsent_NonOwnerCannot` (four-eyes path through `IsAuthorizedApproverForTableAsync`).
- Order: WP1 -> WP2 -> WP3 (sequential, one developer). Manifest sync untouched; test `ManifestSync_MetaOwner_WritesNoTableOwners` in WP3 pins decision 1.

## Phase 3 questions

1. Is direct apply acceptable for owner additions (opt-in flag + global governance admin + audit), or must added owners go through a four-eyes pending state like `VIRTUAL_FILTER_PENDING_APPROVAL`?
2. Audit after commit (failure reported, owner rows stay) vs audit inside the owner transaction (requires the audit store in the same database) vs audit-intent before apply.
3. Should owner creation require the principal to exist in the caller tenant's SCIM identity store (`IIdentityStoreRepository`) when a realm is configured, or is the format check plus collision check enough?
4. `DATA_OWNERS` has no tenant column: is linking an existing owner row created by another tenant's import acceptable (read-only reuse), or must cross-tenant reuse be rejected?
5. Provenance encoded in the `TABLE_OWNERS.id` prefix instead of a column: acceptable, or require a migration step after #402?
6. Should `delegate` role assignments be allowed through the import, or primary only?

## Phase 3 binding conditions (csharp-security-expert, 2026-10-30)

Verdict: APPROVED WITH CONDITIONS. Facts checked: `IsGlobalGovernanceAdmin` guards the governance endpoint (`DbtEndpoints.cs:37`); `GovernanceAdmin` already approves consents without an owner check, so an owner write grants authority to a non-admin principal (delegation, not escalation of the admin). Approver matching on sid/account/email makes every imported identifier approval-relevant.

Rulings: Q1 direct apply accepted (opt-in + global admin + audit + no self-assignment; a pending state would need a new approver of the approver and is out of proportion). Q2 audit-intent before apply, outcome after (not post-commit only). Q3 SCIM existence required when a realm is configured. Q4 read-only reuse across tenants accepted, tenant gate stays on the table. Q5 prefix provenance accepted, no migration. Q6 `primary` and `delegate` both allowed (equal approval authority; role is recorded in audit).

Product fit: explicit owner objects via the governance import satisfy the issue acceptance. Manifest `meta.owner` stays suggestion-only: it is a free-text team label and the manifest endpoint admits the lower-privileged `DbtAdmin`; importing it would let a DbtAdmin mint approvers. Optional later follow-up, not in this PR: under the same flag and only for a global governance admin caller, a lookup-only mapping of `meta.owner` to an existing active `DATA_OWNERS` row (never creation).

Binding conditions
- I393B-SC-01 Fail closed: flag off, missing write repository, missing audit repository, unparsable owner, or any validation or apply error rejects the whole import before any owner row; no partial owner writes. Error messages name table and field, never the identifier or e-mail value.
- I393B-SC-02 Owner writes require `IsGlobalGovernanceAdmin` re-checked in the import service (not only at the endpoint); `DbtAdmin` alone never reaches the owner path, even with the flag on.
- I393B-SC-03 Audit-intent: the dry-run change set is written via `RecordAuditEventsAsync` (awaited, synchronous, not the group-commit buffer) before the apply; audit failure aborts with no owner write. After apply a single outcome event (success or `dbt_owner_apply_failed`) is recorded. Details carry owner id, role, table, source; no e-mail, no account, no display name.
- I393B-SC-04 When an identity realm is configured for the caller tenant, every owner to be created or newly linked must exist and be active in `IIdentityStoreRepository`; lookup failure or unavailability rejects (fail closed). Without a realm: format and collision checks only, and the audit event records `identity_verified=false`.
- I393B-SC-05 Identifier comparison (self-assignment, collision, existing-row lookup) is trimmed, Unicode-normalized (FormC) and case-insensitive on all three dialects (SQLite default collation is case-sensitive); leading/trailing whitespace in identifiers is rejected. Collision check covers sid, account and email against all three columns of every row.
- I393B-SC-06 Bounds: at most 10 owners per table (as planned) and at most 5,000 owner assignments and 1,000 new `DATA_OWNERS` rows per import; exceeding rejects. Dry run and apply use the same set; DryRun never commits.
- I393B-SC-07 `dbt:` TABLE_OWNERS ids are created only by the import; replace deletes only rows with the `dbt:` prefix and only for tables passing `IsTenantAllowed`; the demo seed and any manual path must not emit the prefix (test pins it).
- I393B-SC-08 Replace/removal events are audited per row; removing all owners of a table in replace mode is allowed but flagged `removed_all=true` in the audit details.
- I393B-SC-09 Bare-string owner lookup resolves only to exactly one active row; zero or several matches reject; it never creates a row.
- I393B-SC-10 `display_name` sanitization (control and bidi characters rejected, 200 cap) is applied before persistence; logs never contain owner identifiers or e-mail.
- I393B-SC-11 Linking an existing owner to a table requires the table to pass `IsTenantAllowed` for the caller; the existing consent-tenant check in approval stays unchanged (no change to `IsAuthorizedApproverForTableAsync`, no weakening of `IsSelfApproval`).

Mandatory tests (all `[Trait("Category","Security")]`)
- I393B-T-01 `Import_FlagOff_WithOwner_RejectsWholeImport_NoWrites`
- I393B-T-02 `Import_DbtAdminOnly_WithFlagOn_RejectsOwnerPath`
- I393B-T-03 `Import_AuditIntentFails_NoOwnerRowsWritten`
- I393B-T-04 `Import_AuditDetails_ContainNoEmailOrDisplayName`
- I393B-T-05 `Import_RealmConfigured_UnknownOrInactivePrincipal_Rejects`; `Import_RealmConfigured_IdentityStoreUnavailable_Rejects`
- I393B-T-06 `Import_SelfAssignment_ByCaseOrWhitespaceVariant_Rejects` (all three identifiers, sid/account/email alias)
- I393B-T-07 `Apply_IdentifierCollision_CaseVariantAcrossColumns_Rejects` on SQLite, PostgreSQL, SQL Server
- I393B-T-08 `Import_ExceedsOwnerCaps_Rejects`
- I393B-T-09 `Replace_DeletesOnlyDbtPrefixedRows_AndOnlyForTenantAllowedTables`
- I393B-T-10 `ManualOrSeedPath_NeverEmitsDbtPrefix`
- I393B-T-11 `Import_BareStringOwner_AmbiguousOrMissing_Rejects_NeverCreates`
- I393B-T-12 `Import_ForeignTenantTable_Rejects_NoOwnerLinked`
- I393B-T-13 `ImportedOwner_CannotApproveConsentOfOtherTenant` (existing tenant check holds)
- I393B-T-14 `ManifestSync_MetaOwner_WritesNoTableOwners` (as planned)
- I393B-T-15 `Import_ErrorMessages_DoNotEchoIdentifiers`

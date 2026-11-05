# WP-SIMPL-45 Governed write access (consent-based write grants)
Status: Ready for implementation
Parent: [plan](../2026-11-05-plan-project-simplification.md) (supersedes the design of WP-SIMPL-36 sub-package 3 step 1 and the `IDmlWriteGrant` step 8 of WP-SIMPL-14) | Issues: #505 | Owner decisions: OD-26 (binding), OD-3, OD-22, OD-25, OD-19, OD-27 (groups via SCIM) | Advisories: GHSA-jcf8-hchm-55h3 (DML writer role writes any readable table), GHSA-3prq-qxg7-92qf (DML on tables without a tenant column), GHSA-83g2-c2jr-c8vq (DML has no write scope), GHSA-v75c-28mx-7wm7 (DML replay) - see private advisory | Security conditions: SEC-SIMPL-01, 30, 31, 34, 70, 80, 81; new SEC-SIMPL-120..131 proposed in section 12

## 1. Goal and outcome
Replace the global writer role (`WebSql.DmlWriterRoles`) plus read consent with explicit, data-owner-approved write grants: operation (INSERT, UPDATE, DELETE) x column set x row scope (`VisibleRows` or `AllRows`), held on consents and access profiles, assignable to users and AD groups, never inferred from read access and independent of Casbin. Outcome, objectively checkable: zero `DmlWriterRoles` matches in `src` (except the fail-fast guard entry); every DML statement is authorized by `IWriteGrantEvaluator` and enforced column by column in the AST visitor; new rows and updated rows must satisfy the subject's row scope (check option); write-protected columns are never writable; DML runs only on the WebSQL transport with a write scope, an `Idempotency-Key`, a write-ahead audit and a write budget; discovery shows the caller's write state and offers a write-access request; all mandatory tests of section 6 are green.

## 2. Preconditions and dependencies
- Must be merged first: WP-SIMPL-36 sub-package 0 (batch 5) and sub-package 3 urgent items as re-scoped in section 3 (write scope, tenant-column refusal, interim policy-dependency deny, replay protection, write-ahead audit, `MaxAffectedRows>0`). If WP-SIMPL-36 SP3 is not merged when PR 1 starts, PR 1 implements those items itself under the WP-SIMPL-36 test names (no duplicate implementation).
- WP-SIMPL-43 PR 3 (canonical group SIDs, `GroupResolutionStatus`): write grants to groups are evaluated only on canonical group SIDs; PRs 1-3 here can merge before it (users and current group matching via `GranteeMatcher`), PR 4 (groups in access profiles) after it.
- WP-SIMPL-14 (Casbin deletion): independent. WP-SIMPL-14 step 8 is reduced to "`CanWriteTableAsync` no longer references Casbin" and its four `DmlSecurityTests`; it must not build an `IDmlWriteGrant` role check. Whichever merges second rebases.
- WP-SIMPL-41 (discovery): PR 5 here extends its DTOs; requires WP-SIMPL-41 PR with `DiscoveryObject`/`DiscoveryColumn` merged.
- WP-SIMPL-16 deletes Flight SQL; until then Flight stays read-only (section 5 step 8).
- Parallel: everything else; shared files with WP-SIMPL-36 (`GovernedSqlRewriter.cs`, `GovernedSqlExecutor.cs`, `AstSecurityVisitor.TypedDml.cs`) - WP-SIMPL-36 has merge priority (SEC-SIMPL-84).

## 3. Scope
### In scope
- Domain model `WriteRule` (operation, columns, row scope, optional per-statement row cap) on `Consent` and on `AccessProfile`; governance schema step `0006_write_grants` (PostgreSQL, SQL Server, and SQLite while it still exists).
- Consent workflow: write rules in consent requests, data-owner approval, revocation, recertification, expiry (same lifecycle as read consent).
- `IWriteGrantEvaluator` (core, no Casbin) replacing both `CanWriteTableAsync` overloads and the writer-role block in `GovernedSqlRewriter`.
- Engine: `DmlWritePermit` in `GovernancePolicy`; column-level checks in `SecureAssignments` / `ResolveInsertColumns`; masked, denied and PII columns writable only when named by a write rule; write-protected columns; INSERT check option for consent IN-list row filters; DELETE behind a disabled-by-default switch; MERGE rejected.
- Catalog column flag `WriteProtected` (#505) with catalog API support; primary-key columns implicitly write-protected for UPDATE.
- Durable audit: write-ahead intent record and pre-commit outcome record (#505).
- Write budgets per subject (statements per minute, rows per hour) on top of `MaxAffectedRows`.
- Transport matrix: DML only on `SqlTransports.WebSql`; all other transports read-only (typed deny).
- DB-side least privilege: per-data-source write connection, required-privileges report (OD-25).
- Discovery write state and write-access request (WP-SIMPL-41 integration).
- Migration from `DmlWriterRoles` (fail-fast key, migration report endpoint).
- Re-scope note for WP-SIMPL-36 SP3 and WP-SIMPL-14 step 8 (spec text only, applied by the lead in `docs/plans/`).
### Out of scope
- DDL of any kind, adding columns, `CREATE`/`ALTER`/`GRANT` issued by Autheris (Autheris never changes source privileges itself).
- DML on Trino protocol, MCP (`query_sql`), Flight SQL, async jobs, SQL endpoints, REST data API, OData, GraphQL, Arrow export, Olap and federated cross-source statements (stay read-only; see decisions D-45-3, D-45-4).
- MERGE and `INSERT ... SELECT` into row-filtered tables (stay rejected).
- Write grants on stored procedures (procedures follow WP-SIMPL-39).
- Bulk load / file upload endpoints.
- Returning before/after row values in audit (audit holds no values, SEC-SIMPL-81).

## 4. Current state (verified at `docs/simplification-plans` worktree `/work/wt/simpl-od19`)
- Global switch `WebSqlOptions.AllowDml` (`src/Autheris.Domain/Options/Protocols/RestApiOptions.cs:41`, default false), `DmlWriterRoles` (`:95`), `MaxAffectedRows` (`:101`, default 1000, 0 = unlimited). `GatewayOptions.IsWebSqlDmlAllowed` (`src/Autheris.Domain/Options/GatewayOptions.cs:121`). Startup check `GraphQlAndSwitchStartupChecks.cs:151-154` requires a writer role when `AllowDml`.
- Writer-role gate in `GovernedSqlRewriter.cs:247-271` (`user.IsReadOnly()` then role overlap); per-table check `GovernedSqlRewriter.cs:538-546` calls `TableAccessPolicy.CanWriteTableAsync` (`src/Autheris.Application/Policy/TableAccessPolicy.cs:476-519`, second overload `:522-533`), which grants on cluster admin or writer role and otherwise depends on `_policyEnforcementService.HasPolicies` (Casbin) and a `"write"` action attribute. Read consent and write are not separated per column.
- Tenant column: `TableMetadata.RequireTenantColumnOrThrow(... RequireTenantColumn, TenantColumnExemptTables)` at `GovernedSqlRewriter.cs:548-551`.
- Engine DML (`src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.TypedDml.cs`, 496 lines): MERGE `:20-77`, DELETE `:79-92`, UPDATE `:94-108`, INSERT `:113-128`; `RequiresInsertCheckOption` `:136-157` rejects INSERT into consent-row-filtered tables and `INSERT ... SELECT` into policy tables; `ResolveInsertColumns` `:333-374` and `SecureAssignments` `:442-494` reject masked columns (`RejectMaskedColumnsInDml`), policy-column assignment (SEC-ADG-06) and tenant-column changes. No write-protected column concept (0 matches of `WriteProtected` in `src/*.cs`).
- `DmlGuardOptions` and `GovernancePolicy` in `src/TrinoSqlEngine/CompileRequest.cs:24-49`; `TableCatalogEntry` in `src/TrinoSqlEngine/Governance/TableCatalog.cs:26`; `CatalogColumn(Name, DataType, Collation)` `:25`.
- Execution: `GovernedSqlExecutor.cs:466-505`: DML transaction, `CheckedDmlLimits(commandTimeout, MaxAffectedRows)`, `ICheckedDmlExecutor.ExecuteInTransactionAsync` (`src/TrinoSqlEngine/Ast/Emit/CheckedDmlExecutor.cs:36`, owns commit/rollback, check option `:138`, row limit `:146-147`); ALLOW audit `RecordCommittedDmlAuditAsync` is written after commit (`:500`), failure audit swallowed (`:486-497`). Audit types `WEBSQL_DML_EXECUTED/REJECTED/FAILED` (`src/Autheris.Domain/Audit/AuditEventTypes.cs:35-37`), FailClosed in `AuditPolicyTable.cs`.
- Transports: `SqlTransports` constants `WebSql, Trino, Rest, Mcp, ArrowExport, FlightSql, SqlEndpoints, AsyncJob, Olap` (`src/Autheris.Application/Sql/Interfaces/IGovernedSqlExecutionService.cs:16-32`), carried in `GovernedSqlQueryRequest.Transport` (`:56-63`). DML gating today is transport-agnostic (only `AllowDml`). Federated execution rejects DML (`FederatedDuckDbExecutionService.cs:269`).
- Token scope: `TokenAccessScope.IsReadOnly` / `HasScope` (`src/Autheris.Domain/Security/TokenAccessScope.cs:24`); no write scope exists.
- Consent model: `Consent` (`src/Autheris.Domain/Model/ConsentModels.cs:76`, `GranteeType`, `GranteeSid`, `RoleName`, `ColumnRules`, `RowFilters`, validity, revocation), `ConsentColumnRule.AccessLevel` (`ColumnAccessLevel { Deny, Mask, Clear }`), `ConsentRequest` (`src/Autheris.Domain/Model/GovernanceModels.cs:33`), `IConsentRequestService.SubmitAsync` (`src/Autheris.Application/ConsentRequests/IConsentRequestService.cs`). Grantee matching `src/Autheris.Application/Policy/GranteeMatcher.cs`. Access profiles: `AccessProfile` (`src/Autheris.Domain/Model/AccessProfile.cs:7`, wildcard `TargetTables`), `AccessProfileAssignment` (`AccessProfileSubjectType { User, Group, Role }`, `ApprovedBy`).
- Idempotency: `IIdempotencyStore.GetAsync/SetIfNotExistsAsync` (`src/Autheris.Application/Interfaces/IIdempotencyStore.cs`), Redis and in-memory implementations in `src/Autheris.Infrastructure/Idempotency/`.
- Governance migrations: forward-only steps `src/Autheris.Infrastructure/Persistence/Migrations/Steps/{postgresql,sqlserver,sqlite}/`, last step `0005_catalog_min_group_size`.
- `DmlWriterRoles` referenced in 13 test files and in `docs/configuration-guide.md`, `docs/endpoints-and-testing.md`, `docs/features/f-data-02-governed-websql.md`, `docs/features/f-dialect-02-ast-sql-compiler.md`.

## 5. Implementation steps (TDD: write the section-6 tests of each step first)
1. **Domain model** (`src/Autheris.Domain/Model/WriteGrantModels.cs`, new):
   ```csharp
   public enum WriteOperation { Insert = 1, Update = 2, Delete = 3 }
   public enum WriteRowScope { VisibleRows = 0, AllRows = 1 }
   public sealed record WriteRule(Guid Id, WriteOperation Operation, IReadOnlyList<string> Columns,
       WriteRowScope RowScope, long? MaxRowsPerStatement = null)
   { public void Validate(); } // Columns non-empty for Insert/Update, empty for Delete; distinct, case-insensitive; MaxRows > 0 when set
   ```
   `Consent.WriteRules` (`IReadOnlyList<WriteRule>`, default empty) and `AccessProfile.WriteRules` (`IReadOnlyList<AccessProfileWriteRule>` with an exact `TableIdentifier`; `Validate` rejects wildcard tables and `Role` subjects for profiles with write rules). `Consent.Validate` rejects write rules on `GranteeType.Role` grantees (OD-22 subjects are users and groups) and on `ConsentEffect.Deny`. `TableColumn.WriteProtected` (bool, `TableModels.cs:213`).
2. **Persistence**: step `0006_write_grants.sql` + `.shape.json` per governance dialect: `CONSENT_WRITE_RULES(id, tenant_id, consent_id FK CONSENTS, operation, row_scope, max_rows_per_statement NULL, columns_json)`, `ACCESS_PROFILE_WRITE_RULES(id, tenant_id, profile_id, table_identifier, operation, row_scope, max_rows_per_statement NULL, columns_json)`, `ACCESS_PROFILE_WRITE_APPROVALS(tenant_id, profile_id, table_identifier, rules_hash, approved_by_sid, approved_at)`, `CONSENT_REQUESTS.requested_write_rules_json NULL`, `TABLE_COLUMNS.write_protected BIT/BOOLEAN NOT NULL DEFAULT 0`. Load/save in `*GovernanceRepository.Consent*.cs`, `*.AccessProfiles.cs`, `AccessProfileStore.cs`, `InMemoryAccessProfileRepository.cs`; contract tests in the governance repository contract suite (WP-SIMPL-19) if merged, otherwise per-repository tests.
3. **Consent workflow**: `ConsentRequestSubmission` gains `IReadOnlyList<WriteRule>? RequestedWriteRules`; `ConsentRequestService.SubmitAsync` validates columns against the catalog, rejects write-protected columns, policy-dependency tables (step 6) and `Delete` while `WebSql:Dml:AllowDeleteGrants=false`; approval copies the approved rules (the owner may narrow, never widen) onto the created `Consent`. Approver must be a data owner of the table (existing approver check). Access-profile write rules become effective per table only when an `ACCESS_PROFILE_WRITE_APPROVALS` row exists whose `rules_hash` (SHA-256 of canonical JSON of that table's rules) equals the current hash; any rule edit voids the approval. Admin endpoints: `POST /api/v1/access-profiles/{id}/write-approvals/{table}` (data owner of `table` only, audited `RecordRequiredAsync`). Grant, approval, revocation write `WRITE_GRANT_CHANGED` audit events (required).
4. **Evaluator** (`src/Autheris.Application/Policy/WriteGrants/IWriteGrantEvaluator.cs`, `WriteGrantEvaluator.cs`, new):
   ```csharp
   public sealed record WriteGrantQuery(ClaimsPrincipal User, TenantId Tenant, TableMetadata Table,
       WriteOperation Operation, IReadOnlySet<string> WrittenColumns);
   public sealed record WriteGrantDecision(bool IsAllowed, WriteRowScope RowScope, IReadOnlySet<string> WritableColumns,
       long? MaxRowsPerStatement, string? DenyCode);
   Task<WriteGrantDecision> EvaluateAsync(WriteGrantQuery query, CancellationToken ct);
   ```
   Sources: active, non-revoked consents of the table matched by `GranteeMatcher` (user SID, canonical group SIDs) plus approved access-profile write rules of active assignments (User/Group). Per written column, the widest scope among rules of the requested operation covering it; statement scope = narrowest over all written columns (`VisibleRows` < `AllRows`). Deny when any written column is uncovered, when operation is `Delete` and `AllowDeleteGrants=false`, when the table is a policy dependency, when the column is write-protected, when group resolution is `Degraded` (WP-SIMPL-43), or on any exception (fail closed, `DenyCode` set, never thrown to the caller as allow). Read access is never consulted. Cluster admin gets no implicit write grant. Delete `TableAccessPolicy.CanWriteTableAsync` (both overloads) and the `"write"` action attribute path.
5. **Rewriter wiring** (`GovernedSqlRewriter.cs`): replace `:259-271` (writer roles) with: transport check (step 8), `user.HasWriteScope()` (WP-SIMPL-36), then per DML target `EvaluateAsync` with the written columns (INSERT column list; UPDATE assignment targets; DELETE empty set) replacing `:538-546`. Build `DmlWritePermit` for the compile request. For `RowScope=AllRows` the target's row-filter set is reduced to tenant predicate plus mandatory/virtual filters (consent and access-profile row filters removed); for `VisibleRows` the full read row filter set applies. MERGE: throw `WebSqlPolicyException` ("MERGE is not supported for governed writes").
6. **Policy-dependency index** (GHSA-jcf8-hchm-55h3): `IRowPolicyDependencyIndex` (`src/Autheris.Application/Policy/WriteGrants/RowPolicyDependencyIndex.cs`, new) returns, per tenant, every table referenced by consent row-filter subqueries (`SubqueryJoinHop` tables), virtual filter definitions and access-profile row predicates; invalidated on the governance epoch. Writes to such tables are denied in the evaluator and refused at grant creation (`409 write_grant_policy_dependency`). Replaces the WP-SIMPL-36 interim deny with the same test name.
7. **Engine permit** (`src/TrinoSqlEngine/CompileRequest.cs`, `AstSecurityVisitorOptions.cs`, `AstSecurityVisitor.TypedDml.cs`, `TableCatalog.cs`):
   ```csharp
   public sealed record DmlWritePermit(DmlWriteOperation Operation, IReadOnlySet<string> WritableColumns,
       IReadOnlySet<string> WriteProtectedColumns);
   // GovernancePolicy: public DmlWritePermit? Write { get; init; }  // null => any DML is a SecurityException
   ```
   `ResolveInsertColumns` and `SecureAssignments`: a column not in `WritableColumns` -> `SecurityException("A column is not covered by a write grant.")`; in `WriteProtectedColumns` -> `SecurityException("A write-protected column must not be written.")`; the masked check becomes `masked.Contains(c) && !WritableColumns.Contains(c)`. `RejectMaskedReads` on WHERE and assignment values stays unchanged (a write grant never grants a read). Tenant column handling, SEC-ADG-06 policy-column rejection and `EnsureFilteredDml` stay. DELETE requires `Operation == Delete`. INSERT check option: tables whose consent row filters are all IN-list/literal shapes are passed as plain predicates and removed from `TablesWithConsentRowFilter`, so `WrapInCheckOption` enforces them; subquery-shaped consent filters keep the typed rejection. Application side maps `TableColumn.WriteProtected` plus primary-key columns (UPDATE only) into `WriteProtectedColumns`.
8. **Transport matrix**: `WebSqlOptions.Dml.AllowedTransports` is not configurable; constant `DmlTransports.Allowed = { SqlTransports.WebSql }`. In `GovernedSqlRewriter` any DML with another `Transport` (including null) -> `WebSqlPolicyException` code `dml_transport_read_only`, audited as `WEBSQL_DML_REJECTED`. Trino route (`WebSqlEndpoints.Execution.cs:119`) passes `SqlTransports.Trino` and is therefore read-only.
9. **Audit and replay** (`GovernedSqlExecutor.cs`, `CheckedDmlExecutor.cs`): add `Func<int, CancellationToken, Task>? beforeCommit` to `ICheckedDmlExecutor.ExecuteInTransactionAsync`, invoked after check option and row limit, before commit; an exception rolls back. Sequence: (a) intent `WEBSQL_DML_INTENT` via `RecordRequiredAsync` (fingerprint, table, operation, written column names, grant ids, row scope, idempotency key hash; no values) before execution, failure -> not executed; (b) outcome `WEBSQL_DML_EXECUTED` with affected rows in `beforeCommit` (required, failure -> rollback); (c) commit failure -> `WEBSQL_DML_FAILED` referencing the intent id. Remove the swallow at `:493-497` for the intent path. `Idempotency-Key` is mandatory on WebSQL DML (`400 idempotency_key_required`); key scope `(tenant, subject SID, key)`, value = fingerprint + outcome, TTL 24 h, same key + same fingerprint returns the stored outcome without execution, different fingerprint -> `409`. Reuse WP-SIMPL-36's implementation if merged.
10. **Write budgets**: `WebSqlOptions.Dml.Budget { StatementsPerMinute = 60, RowsPerHour = 10_000 }` per (tenant, subject SID); `IWriteBudgetStore` with Redis and in-memory implementations (same pattern as `IIdempotencyStore`; in-memory only in the documented single-node mode, SEC-SIMPL-34). Effective `CheckedDmlLimits.MaxAffectedRows = min(MaxAffectedRows, rule.MaxRowsPerStatement, remaining hourly rows)`; exhausted budget -> `429 write_budget_exhausted` before execution. Startup: `AllowDml` with `MaxAffectedRows <= 0` or any budget value `<= 0` fails.
11. **DB-side least privilege (OD-25)**: data-source option `WriteConnection` (secret reference, same secret policy as WP-SIMPL-13). DML on a source without `WriteConnection` -> deny `dml_write_connection_missing`; SELECT never uses the write connection; with `AllowDml=false` the write connection must be absent (startup fail). Admin endpoint `GET /api/v1/admin/datasources/{name}/required-write-privileges` returns per table the operations and column lists of active write grants (PostgreSQL, SQL Server and Oracle as column-level `GRANT INSERT (cols)` / `GRANT UPDATE (cols)` text; Databricks table-level `MODIFY`) for operators to apply; Autheris never executes it and never reads source catalogs to verify it.
12. **Discovery (WP-SIMPL-41)**: `DiscoveryObject` gains `DiscoveryWriteState Write` = `{ bool CanInsert, bool CanUpdate, bool CanDelete, WriteRowScope? UpdateRowScope, IReadOnlyList<string> InsertColumns, IReadOnlyList<string> UpdateColumns, bool Requestable }` computed by `IWriteGrantEvaluator` (no data read); `DiscoveryColumn` gains `bool WriteProtected`. The access request from discovery accepts `RequestedWriteRules` (step 3). GraphQL `TableMetadataDto` mirrors the fields.
13. **Migration from `DmlWriterRoles`**: delete `DmlWriterRoles` and the startup check `GraphQlAndSwitchStartupChecks.cs:151-154`; add `WebSql:DmlWriterRoles` to `UnknownGatewayKeyGuard` with message "removed in vX; replaced by consent write grants (WP-SIMPL-45)" (SEC-SIMPL-31). No automatic conversion (roles are not OD-22 subjects; implicit table-wide grants would violate OD-26). `GET /api/v1/admin/write-grants/migration-report` lists tables that had DML audit events in the last 90 days with their subjects, so owners can issue grants. Update the four docs listed in section 4 and the WP-SIMPL-36/14 re-scope lines in `docs/plans/`.

## 6. Tests (`[Trait("Category","Security")]` unless noted)
- `tests/Autheris.Tests.Unit/Policy/WriteGrants/WriteGrantEvaluatorTests.cs`: `Insert_Allowed_OnlyForGrantedColumns`, `Insert_UngrantedColumn_IsDenied`, `Update_ColumnGrant_DoesNotCoverOtherColumns`, `Update_MixedScopes_UsesNarrowestScope`, `Update_AllRowsGrant_YieldsAllRowsScope`, `Delete_Denied_WhenAllowDeleteGrantsFalse`, `Delete_Allowed_WithDeleteGrant_WhenSwitchOn`, `ReadConsentWithoutWriteRule_GrantsNoWrite`, `ClusterAdmin_WithoutWriteRule_IsDenied`, `WriterRoleClaim_GrantsNothing`, `GroupGrant_AppliesToMember_ByCanonicalGroupSid`, `GroupResolutionDegraded_DeniesWrite`, `RevokedOrExpiredConsent_GrantsNoWrite`, `AccessProfileWriteRule_WithoutOwnerApproval_IsIgnored`, `AccessProfileWriteRule_EditVoidsApproval`, `PolicyDependencyTable_IsDenied`, `EvaluatorException_FailsClosed`, `Evaluator_DoesNotReferenceCasbin` (Architecture project, `tests/Autheris.Tests.Architecture`).
- `tests/Autheris.Tests.Unit/Domain/WriteRuleValidationTests.cs` (not Security): `Insert_RequiresColumns`, `Delete_RejectsColumns`, `RoleGrantee_WithWriteRules_IsRejected`, `AccessProfileWildcard_WithWriteRules_IsRejected`.
- `tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorWritePermitTests.cs`: `Dml_WithoutPermit_IsRejected`, `Insert_ColumnOutsidePermit_IsRejected`, `Update_ColumnOutsidePermit_IsRejected`, `MaskedColumn_WritableOnlyWithPermit`, `MaskedColumn_InWhere_StillRejectedWithPermit`, `WriteProtectedColumn_RejectedEvenWithPermit`, `PrimaryKeyColumn_UpdateRejected_InsertAllowed`, `Insert_ConsentInListFilter_UsesCheckOption`, `Insert_ConsentSubqueryFilter_StaysRejected`, `Update_PolicyColumnAssignment_StillRejected`, `TenantColumn_Update_StillRejected`, `Merge_IsRejected`.
- `tests/Autheris.Tests.Unit/Sql/GovernedWriteTests.cs` (rewriter + executor with fakes): `Update_VisibleRows_AppliesReadRowFilter`, `Update_AllRows_KeepsTenantAndMandatoryFilters`, `Insert_OutsideRowScope_IsRolledBack` (check-option violation), `Dml_TableWithoutTenantColumn_IsRefused`, `Dml_TokenWithoutWriteScope_IsRejected`, `Dml_IntentAuditFailure_DoesNotExecute`, `Dml_OutcomeAuditFailure_RollsBack`, `Dml_IdempotencyKeyMissing_IsRejected`, `Dml_IdempotencyKey_ReplayDoesNotReexecute`, `Dml_IdempotencyKey_DifferentStatement_Conflicts`, `Dml_BudgetExhausted_IsRejectedBeforeExecution`, `Dml_EffectiveRowLimit_IsMinimumOfCaps`, `Dml_WithoutWriteConnection_IsDenied`, `Select_NeverUsesWriteConnection`.
- `tests/Autheris.Tests.Unit/Sql/DmlTransportMatrixTests.cs`: Theory `Dml_IsRejected_OnReadOnlyTransport` over `Trino, Rest, Mcp, ArrowExport, FlightSql, SqlEndpoints, AsyncJob, Olap, null`; `Dml_IsAccepted_OnWebSqlWithGrant`; `FederatedDml_IsRejected`.
- `tests/Autheris.Tests.Unit/Configuration/WriteGrantStartupTests.cs`: `DmlWriterRolesKey_FailsStartup`, `AllowDml_MaxAffectedRowsZero_FailsStartup`, `AllowDml_ZeroBudget_FailsStartup`, `WriteConnection_WithDmlDisabled_FailsStartup`.
- `tests/Autheris.Tests.Unit/ConsentRequests/WriteConsentWorkflowTests.cs`: `Owner_MayNarrowButNotWiden_RequestedWriteRules`, `NonOwner_CannotApproveWriteRules`, `WriteRule_OnWriteProtectedColumn_IsRefused`, `WriteRule_OnPolicyDependencyTable_IsRefused`, `GrantChange_IsAuditedRequired`.
- `tests/Autheris.Tests.Unit/Discovery/DiscoveryWriteStateTests.cs`: `WriteState_EqualsEvaluatorDecision`, `WriteState_ReadsNoData`, `WriteAccessRequest_RoutesToDataOwner`.
- `tests/Autheris.Tests.Unit/Admin/RequiredWritePrivilegesTests.cs` (not Security): `Report_ListsColumnLevelGrants_PerDialect`, `Report_ExcludesRevokedGrants`.
- Integration (`tests/Autheris.Tests.Integration`, PostgreSQL and SQL Server containers, Category=Security): `GovernedWriteIntegrationTests.Insert_Update_WithGrants_EndToEnd`, `GovernedWriteIntegrationTests.Update_OutsideVisibleRows_AffectsZeroRows`, `GovernedWriteIntegrationTests.WriteIdentity_HasNoPrivilegeOutsideGrants` (fixture DB user with column-level grants only).
- Migrated/deleted: the 13 test files referencing `DmlWriterRoles` are rewritten to seed write rules; WP-SIMPL-36 test `Dml_WriterRole_WithoutExplicitWriteGrant_IsDenied` is replaced by `WriterRoleClaim_GrantsNothing`; WP-SIMPL-14 `Dml_denied_without_write_grant_after_casbin_removal` stays and must pass against the evaluator.

## 7. Verification commands
- `dotnet build Autheris.sln -c Release -warnaserror` (one build at a time; check `df -h /work` first).
- `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj -c Release --no-build --filter "FullyQualifiedName~WriteGrant|FullyQualifiedName~GovernedWrite|FullyQualifiedName~DmlTransport|FullyQualifiedName~WriteConsent|FullyQualifiedName~DiscoveryWriteState"`
- `dotnet test tests/TrinoSqlEngine.Tests -c Release --no-build --filter "FullyQualifiedName~WritePermit|FullyQualifiedName~Dml"`
- `dotnet test tests/Autheris.Tests.Architecture -c Release --no-build`; then `--filter Category=Security` for Unit; Integration with Testcontainers (podman host override).
- `git grep -n DmlWriterRoles -- src` -> only `UnknownGatewayKeyGuard`; `git grep -n -E 'CanWriteTableAsync' -- src` -> 0; `git grep -n -i casbin -- src/Autheris.Application/Policy/WriteGrants` -> 0.

## 8. Acceptance criteria
- [ ] All section-6 tests green; full Unit, Architecture, TrinoSqlEngine and Security filters green.
- [ ] No DML executes without: `AllowDml`, WebSQL transport, write scope, idempotency key, evaluator allow for every written column, intent audit written, budget available, write connection configured.
- [ ] A read consent alone never permits a write (test `ReadConsentWithoutWriteRule_GrantsNoWrite`).
- [ ] INSERT/UPDATE results outside the subject's row scope are rejected or affect zero rows; tenant predicate applies under `AllRows`.
- [ ] DELETE grants cannot be created and DELETE is denied while `AllowDeleteGrants=false` (default).
- [ ] Write-protected and primary-key columns cannot be updated, with or without grants.
- [ ] `DmlWriterRoles` present in config fails startup with the replacement message.
- [ ] Discovery shows write state and accepts write-access requests.
- [ ] Docs listed in section 4 no longer mention `DmlWriterRoles`.

## 9. PR slicing (<= 400 net added src lines each)
1. PR 1 Model + persistence + validation (steps 1-2), ~350 src.
2. PR 2 Evaluator + policy-dependency index + rewriter wiring + transport matrix + `CanWriteTableAsync` deletion (steps 4-6, 8), ~400 src.
3. PR 3 Engine permit, write-protected/PK columns, check option for IN-list consent filters (step 7), ~300 src.
4. PR 4 Consent workflow + access-profile approvals + migration (steps 3, 13), ~380 src.
5. PR 5 Audit sequence, idempotency, budgets, write connection, privileges report (steps 9-11), ~400 src.
6. PR 6 Discovery integration and docs (step 12, doc updates), ~200 src.
Feature stays dark (`AllowDml=false`) until PR 5 is merged; PRs 2-4 must keep DML fully denied when no write rule exists.

## 10. Risks, rollback, and things the developer must NOT do
- Must NOT derive any write permission from read consent, `ColumnAccessLevel.Clear`, roles, cluster admin or Casbin.
- Must NOT issue `GRANT`, DDL or catalog/system-view reads against sources (OD-25).
- Must NOT log or audit parameter values or written values.
- Must NOT enable DML on any transport other than WebSQL; must NOT accept MERGE.
- Must NOT widen `RejectMaskedReads`: a write grant never allows masked columns in WHERE or assignment expressions.
- Risk: row-scope narrowing changes behaviour for existing writers -> mitigated by the migration report and `AllowDml=false` default (no production yet).
- Risk: IN-list check option on dialects without insert row count -> stays typed-rejected (existing `ReportsInsertRowCount`).
- Rollback: revert PRs in reverse order; with `AllowDml=false` the system is read-only regardless.

## 11. Estimate
About +2,000 / -250 src lines, +2,200 test lines; 9-11 person-days.

## 12. Phase 3 conditions proposed (to add to the sec plan)
- SEC-SIMPL-120: write permission only from explicit approved write rules (consent or owner-approved profile); never from read access, roles, admin or Casbin.
- SEC-SIMPL-121: every written column must be covered; masked/denied/PII columns writable only when named; write grant never grants read.
- SEC-SIMPL-122: `VisibleRows` writes are bounded by the full read row filter on target and new values (check option); `AllRows` still enforces tenant and mandatory filters.
- SEC-SIMPL-123: write-protected and primary-key columns are never updatable.
- SEC-SIMPL-124: DELETE grants disabled by default; MERGE rejected.
- SEC-SIMPL-125: DML only on WebSQL; every other transport and federated execution rejects DML.
- SEC-SIMPL-126: write scope required on every token type (GHSA-83g2-c2jr-c8vq).
- SEC-SIMPL-127: tables without tenant column refused unless exempt (GHSA-3prq-qxg7-92qf); policy-dependency tables never writable (GHSA-jcf8-hchm-55h3).
- SEC-SIMPL-128: mandatory idempotency key, replay returns stored outcome (GHSA-v75c-28mx-7wm7).
- SEC-SIMPL-129: intent audit before execution and outcome audit before commit, both required (#505).
- SEC-SIMPL-130: budgets and `MaxAffectedRows > 0` enforced; zero values fail startup.
- SEC-SIMPL-131: dedicated write connection; SELECT never uses it; privileges report only, no source privilege changes.

## Decisions taken in this spec
- D-45-1: DELETE is a separate grant (`WriteOperation.Delete`), off by default via `WebSql:Dml:AllowDeleteGrants=false` (owner did not request DELETE).
- D-45-2: Row scope per statement is the narrowest scope over the written columns; `AllRows` lifts only consent/profile row filters, never tenant or mandatory filters.
- D-45-3: DML only on WebSQL. Trino stays read-only (client protocol retries and no idempotency channel); MCP, async jobs, SQL endpoints, Flight (until deleted) read-only.
- D-45-4: MERGE rejected; INSERT into consent-filtered tables allowed only for IN-list/literal filters via check option.
- D-45-5: Write grants on roles are rejected; `DmlWriterRoles` is removed without automatic conversion (migration report instead).
- D-45-6: Access-profile write rules require exact tables and a per-table data-owner approval bound to a rules hash.
- D-45-7: Primary-key columns are implicitly write-protected for UPDATE.
- D-45-8: WP-SIMPL-36 SP3 keeps write scope, tenant-column refusal, interim dependency deny, replay protection, write-ahead audit and `MaxAffectedRows>0`; its step-1 grant design, write-protected columns and write budget move here. WP-SIMPL-14 step 8 shrinks to removing Casbin from `CanWriteTableAsync`.
- D-45-9: Least privilege is achieved by a separate write connection and an operator-applied privileges report; Autheris never alters or inspects source privileges.

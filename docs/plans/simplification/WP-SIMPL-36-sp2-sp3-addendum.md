# WP-SIMPL-36 addendum: sub-packages 2 and 3 (Phase 2)

Parent spec: [WP-SIMPL-36](WP-SIMPL-36-security-lane.md) sections 5 (SP2, SP3), 6, 9, 10. Verified on `origin/main` `cd723039`.
Advisories are named only by GHSA id and the neutral title of [advisories-overview.md](advisories-overview.md) (SEC-SIMPL-01); fix details stay in the private advisories.

## 1. Preconditions

1. Rebase `feat/simpl-36-sp2` onto `main` after PR #565 (WP-SIMPL-50 PR A, merged 2026-11-08, not in `cd723039`) before Phase 4.
2. PR #565 overlaps: none with SP2 source files. With SP3: `src/Autheris.Application/Policy/TableAccessPolicy.cs` (3 hunks, near `DecideAsync` and `CanWriteTableAsync`) and `tests/Autheris.Tests.Unit/Policy/TableAccessPolicySecurityTests.cs` (3a). `tests/Autheris.Tests.Architecture` gains `AuditDetailsArchitectureTests.cs` (new file, no conflict with `RequiredAuditArchitectureTests.cs`).

## 2. SP2 (PR 2): audit fail-closed bundle

D-1. Call sites switching to `IAuditWriter.RecordRequiredAsync` (7, in 5 types). Each record is written before the state change or outbound action (write-ahead), not after it:
| # | Path | File:method | Today |
|---|---|---|---|
| 1 | datasource test probe | `Catalog/Services/DatasourceTestingService.cs:AppendAuditAsync` (8 callers in `TestDatasourceAsync` flow) | repository write, failure logged; no row without tenant claim |
| 2 | datasource registration (MCP/admin plan) | `Governance/Services/AccessPlanningService.cs:RegisterDatasourceAsync` | repository write after the change |
| 3 | datasource registration (catalog API) | `Catalog/Services/CatalogDiscoveryService.cs:RegisterDatasourceAsync` | repository write after ingestion |
| 4 | dataset state change | `Governance/Services/AccessPlanningService.cs:SetDatasetStateAsync` | repository write after `UpsertTableMetadataAsync` |
| 5 | access-profile admin (apply access plan) | `Governance/Services/AccessPlanningService.cs:ApplyAccessAsync` | repository write after the tuple write |
| 6 | DML commit record | `Sql/Services/GovernedSqlExecutor.Audit.cs:RecordCommittedDmlAuditAsync` | `RecordAsync`, failure logged Critical |
| 7 | DML failure record | `Sql/Services/GovernedSqlExecutor.cs` DML `catch (Exception dmlEx)` block | `RecordAsync` inside a swallowing catch |
(All paths under `src/Autheris.Application/`.) The access-profile endpoints in `GovernanceEndpoints.Access.cs` already use `RecordRequiredAsync` and stay unchanged.

D-2. Event types: the string literals `ADMIN_APPLY_ACCESS`, `ADMIN_REGISTER_DATASOURCE`, `ADMIN_SET_DATASET_STATE`, `DATASOURCE_REGISTERED` become `AuditEventTypes` constants (same values), plus new `DatasourceTestRequested` and `WebSqlDmlIntent`; all are `FailClosed` in `AuditPolicyTable`.

D-3. Datasource probe: `TestDatasourceAsync` writes `DatasourceTestRequested` before any outbound connection and `DatasourceTested` with the outcome before returning. A caller without a valid tenant claim is audited under the resolved default tenant (OD-9), never skipped. Either write failing refuses the probe or withholds its result.

D-4. DML (SEC-SIMPL-81): `GovernedSqlExecutor` writes `WebSqlDmlIntent` (statement fingerprint, target table, no values) with `RecordRequiredAsync` before `ExecuteInTransactionAsync`; on failure the statement is not executed. The post-commit `WebSqlDmlExecuted` record is required too; its failure after commit is logged Critical and counted (`audit_dml_completion_missing`), the response stays the statement result (a 503 after commit would invite a duplicate write). Default taken.

D-5. Refusal behaviour: `AuditUnavailableException` propagates. HTTP: 503, `code = SERVICE_UNAVAILABLE`, `Retry-After` (existing `GatewayExceptionHandler` mapping, no new code). MCP admin tools: the generic tool error of `McpAdminTools` (no state change). GraphQL unchanged. Default taken: no dedicated `AUDIT_UNAVAILABLE` code.

D-6. `tests/Autheris.Tests.Architecture/RequiredAuditArchitectureTests.cs`, test `Security_decision_paths_use_RecordRequiredAsync` (Cecil via `CecilModuleCache.For(typeof(X).Assembly)`):
- Types: `DatasourceTestingService`, `AccessPlanningService`, `CatalogDiscoveryService`, `GovernedSqlExecutor` (all partial files, including nested compiler-generated state machines and lambdas).
- Rule A: no call to `IAuditLogRepository.RecordAuditEventAsync`, and no `IAuditWriter.RecordBestEffortAsync`/`RecordAsync` except the baseline allowlist.
- Rule B: no fire-and-forget: a `call`/`callvirt` to any `IAuditWriter`/`IAuditLogRepository` method whose `Task` result is followed by `pop` fails.
- Baseline = fixed state: allowlist of `(type, method, callee)` tuples for the read path of `GovernedSqlExecutor` only (`QueryExecutionError` best effort, `WebSqlQuery`/`WebSqlQueryDenied` via `RecordAsync`). The list may only shrink; a sanity test asserts every allowlisted tuple still exists.

D-7. Unit tests (`[Trait("Category","Security")]`): `Catalog/DatasourceTestAuditTests.Probe_is_refused_when_audit_write_fails` (no outbound connection attempted), `Probe_without_tenant_claim_is_audited_under_default_tenant`; `Governance/AccessPlanningAuditTests.{Register_datasource,Set_dataset_state,Apply_access}_is_refused_when_audit_write_fails` (repository mutation not called); `Catalog/CatalogDiscoveryAuditTests.Register_datasource_is_refused_when_audit_write_fails`; `Sql/DmlWriteAheadAuditTests.Dml_is_not_executed_when_intent_audit_fails`, `Dml_audit_failure_after_commit_is_logged_critical`.

## 3. SP3 (PRs 3a-3d): DML advisories, no Casbin

Constraint (spec SP3 step 2): no write-grant model, no write-protected column flag, no write budget, no write connection, no role-based write check, nothing delegated to Casbin. Where an advisory fix list names one of these, the fail-closed variant below is taken (deny instead of grant). WP-SIMPL-45 later replaces the interim deny with its grant evaluator and keeps the regression tests green.

D-8. PR 3a, GHSA-jcf8-hchm-55h3 (WebSQL DML authorization). Default taken: interim deny-all.
- `Policy/TableAccessPolicy.cs:CanWriteTableAsync`: returns false for every target (no role, consent or policy evaluation); `DecideAsync` is no longer consulted for writes.
- `Sql/Services/GovernedSqlRewriter.cs`: remove the `DmlWriterRoles` check (DML block near `user.IsReadOnly()`); add a check before the write decision that refuses DML whose target is referenced by any row-filter predicate of the caller tenant (dependency set from `GovernancePolicyFactory`), error `POLICY_VIOLATION`.
- `src/Autheris.Api/Configuration/InertOptionWarnings.cs`: `WebSql:DmlWriterRoles` reported inert (removal and startup failure stay with WP-SIMPL-45 `WriteGrantStartupTests`).
- Tests: `AdvisoryRegressionTests.Ghsa_jcf8_55h3_regression`; `Sql/WebSqlDmlGuardTests.Dml_is_refused_for_every_role_while_no_write_decision_exists`, `Dml_on_row_filter_dependency_table_is_refused`; `Policy/TableAccessPolicySecurityTests.CanWriteTable_is_false_without_write_decision`; architecture assertion that `TableAccessPolicy.CanWriteTableAsync` references no `Casbin*` type.

D-9. PR 3b, GHSA-3prq-qxg7-92qf (WebSQL DML scoping). DML on a table without a tenant column is refused unless the table is listed in `DataSources:TenantColumnExemptTables`, independent of `RequireTenantColumn`.
- `GovernedSqlRewriter.cs` (tenant-column block after the write decision), `src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.TypedDml.cs` (`ForceTenantIn*` refuse instead of passing the row through when no tenant column is set).
- New `src/Autheris.Api/Configuration/Validation/WebSqlDmlOptionsValidator.cs`: `WebSql:AllowDml=true` fails startup unless `RequireTenantColumn=true` and `MaxAffectedRows>0` (D-45-8; default taken: carried here).
- Tests: `AdvisoryRegressionTests.Ghsa_3prq_92qf_regression`; `Sql/WebSqlDmlGuardTests.Dml_on_table_without_tenant_column_is_refused`, `Dml_on_exempt_table_is_admitted_to_next_check`; `Configuration/WebSqlDmlOptionsValidatorTests.AllowDml_requires_tenant_column`, `AllowDml_requires_positive_MaxAffectedRows`; `TrinoSqlEngine.Tests/Ast/AstSecurityVisitorDmlTests.ForceTenant_without_tenant_column_throws`.

D-10. PR 3c, GHSA-83g2-c2jr-c8vq (WebSQL DML token scopes). DML requires the scope `Sql.Write` on every token type; a token without it is read-only (name per private notes, default taken).
- `src/Autheris.Domain/Security/TokenAccessScope.cs`: `WriteScope = "Sql.Write"`, `CanWrite(principal)` (= has `Sql.Write` and not marked read-only).
- `GovernedSqlRewriter.cs`: DML block checks `CanWrite`; refusal `READ_ONLY_TOKEN` (existing `ApiErrorCodes.ReadOnlyToken`).
- `src/Autheris.Api/Security/EntraTokenPolicy.cs`: read-only marking no longer decides write capability (kept for read scoping only).
- Tests: `AdvisoryRegressionTests.Ghsa_83g2_c8vq_regression`; `Security/TokenWriteScopeTests.Dml_without_write_scope_is_refused` (theory over Entra, generic JWT, API key, MCP and WebSocket principals), `Write_scope_on_read_only_marked_token_is_refused`.

D-11. PR 3d, GHSA-v75c-28mx-7wm7 (WebSQL DML request handling). The Flight SQL server named in the advisory no longer exists on `main`; the remaining items apply to the HTTP routes. Defaults taken:
- `src/Autheris.Api/Endpoints/WebSqlEndpoints.Execution.cs`: DML on `/v1/statement` (Trino protocol) is refused (OD-35: only WebSQL is wired); DML on `/api/v1/sql` requires an `Idempotency-Key` header (missing: 400 `BAD_REQUEST`); the key is reserved per (tenant, subject SID, key) through `IIdempotencyStore.SetIfNotExistsAsync` (cluster-wide with Redis, TTL 24 h) before execution; a repeated key is refused with 409 and does not execute. Store unavailable: 503.
- Tests: `AdvisoryRegressionTests.Ghsa_v75c_7wm7_regression` (second request with the same key: 409, affected rows counted once); `Api/WebSqlDmlIdempotencyTests.Dml_without_idempotency_key_is_refused`, `Dml_on_trino_route_is_refused`, `Idempotency_store_failure_refuses_dml`.

D-12. Because D-8 denies every DML target, the regression tests of 3b-3d assert their own refusal code, which is raised before the write decision (order in the DML block: token scope, route/idempotency, tenant column, dependency set, write decision). Each guard is also unit-tested in isolation.

D-13. File overlap and order: 3a, 3b and 3c all edit the DML block of `GovernedSqlRewriter.cs` (3a and 3c the same lines): stack 3a -> 3c -> 3b. 3d touches only `WebSqlEndpoints.Execution.cs` and runs in parallel after SP2. To avoid conflicts in `AdvisoryRegressionTests.cs`, 3a makes the type `sealed partial`; each PR adds its test in `Security/AdvisoryRegressionTests.<first4>.cs`. All of SP3 rebases onto SP2 (`GovernedSqlExecutor` write-ahead record).

## 4. Open owner questions (defaults taken unless the owner decides otherwise)

- Q1: Interim deny-all DML until WP-SIMPL-45/46 (D-8). Recommended: yes (SEC-SIMPL-80: "DML stays denied until WP-46 PR 2"; nothing is in production).
- Q2: Required `Idempotency-Key` for DML instead of optional (D-11). Recommended: required.
- Q3: Refuse DML on the Trino route now instead of in WP-SIMPL-45 (D-11). Recommended: now.

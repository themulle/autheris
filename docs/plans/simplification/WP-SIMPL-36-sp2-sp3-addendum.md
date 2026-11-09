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

## 5. Phase 3 binding conditions (security review)

Verdict: APPROVED WITH CONDITIONS. Owner defaults Q1 (deny-all DML until WP-45/46), Q2 (Idempotency-Key required) and Q3 (refuse DML on the Trino route) are confirmed. Verified at head 63e776e7: `GovernedSqlRewriter` rejects non-SELECT unless `IsWebSqlDmlAllowed` (default false), so DML is already off by default; the deny-all (write decision at `CanWriteTableAsync` call site) sits inside the rewriter and therefore covers every caller of `RewriteCoreAsync`, including the `dmlContext: null` overloads. GraphQL `Mutation` types only cover consent and catalog (no SQL). Procedure calls and Merge/Truncate are not `Insert|Update|Delete` and fall to the `StatementNotPermitted` branch.

SP2 (audit fail-closed)
- SEC-36-SP2-01: The probe audit must not use a default-tenant bucket for a caller without a valid tenant claim: that reverses the verified control DT-SC-03 (`AppendAuditAsync` today skips on `ClaimTenantKind != Valid`). Resolve the tenant through the OD-9 resolver only when it returns a deterministic tenant (single-tenant mode); in multi-tenant mode a caller without a valid tenant claim is refused before any outbound connection (fail-closed, no unaudited and no mis-attributed probe). Never take the tenant from request input.
- SEC-36-SP2-02: `DatasourceTestRequested` is written with `RecordRequiredAsync` before DNS resolution, before the SSRF/allowlist-bound connect and before any pool or credential lookup; the existing GHSA-9229 tenant confinement check and input validation stay before it (no audit row for an unauthorized datasource id of another tenant that leaks existence). The outcome record is written before the result is returned; if it fails the result (error text, latency, server version) is withheld and 503 returned.
- SEC-36-SP2-03: Audit entries carry no connection string, credential, SQL literal, row value or raw driver message: probe outcome uses the already redacted message; `WebSqlDmlIntent` holds fingerprint, statement type, target table, token id, tenant only.
- SEC-36-SP2-04: Post-commit DML completion record: Critical log plus counter is accepted ONLY because the `WebSqlDmlIntent` write-ahead row exists and is the authoritative evidence of the attempt. Condition: intent row must be written and flushed before `ExecuteInTransactionAsync` begins, a completion failure must never roll back or alter the response, and the counter `audit_dml_completion_missing` is exposed and alertable. A 503 after commit stays rejected. Under deny-all (3a) the path is unreachable in production; keep it covered by the test below so WP-SIMPL-45 inherits it.
- SEC-36-SP2-05: The failure record in the DML `catch` block switches to a non-swallowing required write only for the audit exception: an `AuditUnavailableException` raised there is logged Critical and counted but must not mask the original statement error, and the statement outcome (rollback) is unchanged.
- SEC-36-SP2-06: Architecture test hardening: the allowlist is a literal fixed set (no wildcard, no `Contains` match on type or callee name), a sanity test fails when an entry no longer exists (shrink-only enforced by count constant that may only be lowered), and Rule B (fire-and-forget `pop`) also covers `Task.Run`/`_ =` discard patterns and `ConfigureAwait`-wrapped awaiters without `await`. The scan covers every `IAuditWriter` callee, including `RecordBestEffortAsync`, and compiler-generated closures.
- SEC-36-SP2-07: Coverage check for #505/#507: each of the 7 sites in D-1 needs a refusal test; the read-path allowlist entries (`QueryExecutionError`, `WebSqlQuery`, `WebSqlQueryDenied`) are explicitly out of scope and must be listed as a tracked remainder in the lane plan, not silently accepted.

SP3 (DML advisories)
- SEC-36-SP3a-01: Deny-all is implemented as a constant false in `CanWriteTableAsync` (both overloads, lines 477 and 523) and the call site at the rewriter write decision stays in place; remove `DmlWriterRoles` as a grant only after the deny exists in the same PR. The config validator at `GraphQlAndSwitchStartupChecks` (`AllowDml` needs `DmlWriterRoles`) must not be loosened: `AllowDml=true` with no role still fails startup.
- SEC-36-SP3a-02: A deny never depends on options: no setting (`AllowDml`, `DmlWriterRoles`, `IsConsentBypassed`, read-only flag absence) can turn `CanWriteTableAsync` true; consent-bypass mode must not bypass the write deny.
- SEC-36-SP3b-01: The tenant-column refusal is evaluated for every referenced DML table (multi-table UPDATE/DELETE with USING or subquery), and an exempt-table list entry is matched by fully qualified name case-insensitively, never by suffix.
- SEC-36-SP3c-01: `Sql.Write` is read from the validated token claims only (never from a request header or body), and `user.IsReadOnly()` wins over the scope: a read-only-marked principal with `Sql.Write` is refused. Same on every token type (Entra, API key, delegated/impersonation).
- SEC-36-SP3d-01: Route/idempotency checks run in the endpoint before the executor; the key is bound to (tenant, subject SID, key hash), has a length cap (<= 128 chars, ASCII, else 400) and is stored hashed; the key is reserved only after authentication and the scope check, so unauthenticated or unscoped callers cannot occupy keys; the reservation is not released on failure (a retried statement requires a new key). Store failure: 503, never fall back to executing.
- SEC-36-SP3d-02: DML detection for the Trino route must classify by parsed statement type (the same classifier as the rewriter), not by text prefix; a `/v1/statement` request containing DML is refused with the route error before the rewriter, and no query id is created.
- SEC-36-ORDER-01: The check order in D-12 is binding and each refusal must be observable: token scope -> route/idempotency (endpoint, earlier) -> tenant column -> dependency set -> write decision. The deny-all write decision is the LAST check; a test per refusal asserts the specific code, so a later deny cannot mask a broken earlier guard. The `AllowDml` gate at the rewriter stays first, so tests of 3b-3d must set `AllowDml=true` explicitly.
- SEC-36-ORDER-02: Because deny-all hides 3b-3d in production, each guard also gets an isolated unit test that runs against a stubbed permissive write decision (test-only seam, not a production option), so WP-SIMPL-45 cannot regress them silently.

Mandatory tests (all `[Trait("Category","Security")]`, neutral names; no root cause or exploit text, SEC-SIMPL-01)
- `Catalog/DatasourceTestAuditTests.Probe_without_valid_tenant_claim_is_refused_in_multi_tenant_mode`, `Probe_intent_audit_precedes_outbound_connection`, `Probe_result_is_withheld_when_outcome_audit_fails`, `Probe_unauthorized_datasource_writes_no_audit_row`.
- `Sql/DmlWriteAheadAuditTests.Intent_row_is_written_before_transaction_begins`, `Completion_audit_failure_does_not_alter_response_and_increments_counter`, `Intent_row_contains_no_literal_values`, `Audit_failure_in_failure_path_does_not_mask_statement_error`.
- `Architecture/RequiredAuditArchitectureTests.Allowlist_is_exact_and_not_larger_than_baseline`, `Fire_and_forget_audit_calls_are_rejected`.
- `Policy/TableAccessPolicySecurityTests.CanWriteTable_is_false_with_consent_bypass_and_writer_role`, `Startup_fails_when_dml_enabled_without_writer_role`.
- `Sql/WebSqlDmlGuardTests.Multi_table_dml_with_tenantless_table_is_refused`, `Read_only_principal_with_write_scope_is_refused`, `Each_guard_refuses_with_its_own_code_under_permissive_write_stub`.
- `Api/WebSqlDmlIdempotencyTests.Overlong_or_nonascii_key_is_rejected`, `Key_is_not_reserved_for_unauthorized_or_unscoped_caller`, `Key_is_scoped_per_tenant_and_subject`, `Trino_route_dml_is_refused_without_query_id`.
- `Mcp/McpQuerySqlDmlTests.Mcp_query_sql_refuses_dml_for_every_caller`.

## 6. Phase 4 changelog

- 2026-11-08 (SP2): D-1 sites 2-5 now take `IAuditWriter` instead of `IAuditLogRepository` in their constructors (DatasourceTestingService, AccessPlanningService, CatalogDiscoveryService); unit test construction sites wrap a repository double with `AuditWriterTestServices.Over`.
- 2026-11-08 (SP2): the probe resolves the audit tenant with `TenantResolver.Decide` over the claim and `TenantResolver.EffectiveDefaultOf(Gateway:Tenancy)` (no new `ITenantResolver` dependency); an absent claim without a default tenant throws `TenantUnresolvedException`, an invalid claim `TenantClaimInvalidException`. Both map to 403 through the existing handler.
- 2026-11-08 (SP2): the existing counter `autheris_dml_audit_failed_total` keeps its name (referenced by `docs/features/f-dialect-02-ast-sql-compiler.md`); it is the alertable `audit_dml_completion_missing` signal of SEC-36-SP2-04 and now also counts a lost DML failure record (SEC-36-SP2-05).
- 2026-11-08 (SP2): `CatalogDiscoveryService.RegisterDatasourceAsync` writes its record before ingestion, so the record no longer carries `CreatedDatasetsCount`.
- 2026-11-08 (SP2): tracked remainder (SEC-36-SP2-07): the read-path audit calls of `GovernedSqlExecutor` stay on the baseline allowlist of `RequiredAuditArchitectureTests` (`QueryExecutionError` best effort, `WebSqlQuery` and `WebSqlQueryDenied` through `RecordAsync`); the allowlist may only shrink.
- 2026-11-08 (SP3a): the write decision moved behind the tenant-column check in the rewriter (it was before it), so the D-12 order holds; `CanWriteTableAsync` returns a constant false in both overloads. The only permissive path is the internal, per-async-context `DmlWriteDecisionTestSeam` (SEC-36-ORDER-02), enabled by `CreateDmlService(permissiveWrite: true)` for the existing DML unit tests; no option or setting reaches it.
- 2026-11-08 (SP3a): the dependency set is computed from the caller's decisions over `GetAllTablesAsync` (the row policies that apply to the caller), not from `GovernancePolicyFactory` (it assembles per statement and has no tenant-wide view); a non-typed row policy is treated as a dependency (fail-closed). Error code `POLICY_VIOLATION`, observable before the deny-all `TABLE_DENIED`.
- 2026-11-08 (SP3a): `WebSql:DmlWriterRoles` is reported inert at startup; the validator (`AllowDml` needs a role) is unchanged (`SEM_AllowDml_WithoutWriterRoles_AbortsStartup_InEveryEnvironment`); the rewriter no longer evaluates the role. Casbin-based `CanWriteTableAsync` tests in `TableWritePermissionTests` now assert the deny.
- 2026-11-08 (SP3a review): the dependency scan now runs once per statement (`DmlDependencyScan`) and stops at the first hit; a table without columns is decided like any other (only inactive tables and denied decisions are skipped). Follow-up: cache the tenant-wide dependency set per (tenant, principal, catalog version) so a DML statement does not decide every catalog table; not needed while DML is denied, to be revisited with the write grant evaluator.
- 2026-11-09 (SP3a): `Autheris.Tests.Integration` is added to the internals of `Autheris.Application` (test assembly) so the two in-process executor DML integration cases (`Dml_IsTenantScoped_ThroughTheCheckedExecutor`, `Dml_AboveMaxAffectedRows_IsRejectedAndRolledBack`) enable the permissive seam and keep proving tenant scoping and the row cap against real databases; `Dml_IsRefused_WithoutAWriteDecision` asserts the refusal without the seam. The Sim ApiHost `write` profile runs out of process, so its DML is denied until the write decision exists (no unit/integration test depends on it).

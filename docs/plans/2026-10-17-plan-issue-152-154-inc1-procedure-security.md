# Implementation Plan: Governed Procedures INC-1 - Security Slice (#152, #153, #154)

**Document ID:** `PLAN-PROC-152-INC1` · **Date:** 2026-10-17 · **Phase:** 2 (solution architect); next: Phase 3
**Source PRD:** [`REQ-PROC-152..154`](2026-10-17-req-issues-152-154-governed-procedures.md) sections 5.1, 6, 7 (INC-1 only) · **Intake:** [rank 1.6](2026-10-17-issue-intake-assessment.md) (owner accepted OQ-1..15) · **Tracker:** [waves 2/3](2026-10-16-plan-waves-2-3-delivery.md)
**Code base verified:** `origin/main` `b5c1b5dd` (worktree code identical). Size **M**. Not in production; breaking changes allowed; no new options, no DDL.

## 1. Verified current behaviour (file:line)

All paths under `src/`. `GPES` = `Autheris.Application/Procedures/Services/GovernedProcedureExecutionService.cs`, `PER` = `Autheris.Api/Endpoints/ProcedureEndpointRoutes.cs`.

| # | Fact | Where |
|---|---|---|
| F-1 | Registry lookup first; unknown name throws `KeyNotFoundException` **without audit** | `GPES:110-113` |
| F-2 | Missing SID (no bypass) throws `SecurityException` -> 403 for a registered name, while an unknown name gets 404 (**new oracle, not in the PRD**) | `GPES:121-125`, `PER:353-356` |
| F-3 | Role gate: audited `reason=role`, 404 (already correct) | `GPES:129-133` |
| F-4 | Non-Active -> 503 **before** consent (existence + health oracle) | `GPES:135-138`, `PER:345-348` |
| F-5 | Parameter validation (400, message names the parameter) **before** consent | `GPES:141`, `:241-252`, `PER:349-352` |
| F-6 | Declared mode with zero tables -> 403 `no-result-table`; consent loop -> 403 `consent`; body "Access to the procedure is denied." differs from 404 body | `GPES:151-171`, `PER:341-356` |
| F-7 | Catalog mode with zero referenced tables and no `ResultTable` runs **without any consent check** (gap, not in the PRD) | `GPES:144-171` |
| F-8 | Exceptions thrown inside the consent stage (`GatewaySecurityException` from incomplete virtual filters, `InvalidOperationException` without policy enforcement, `TableAccessDecision.cs:390-391`, `GPES:373`) fall into the generic catch -> 500 for a registered name (oracle) | `PER:365-369` |
| F-9 | Row-scope key unusable / lookup failure -> 403 | `GPES:469-473`, `:483-488` |
| F-10 | Business error: invoker carries mechanically sanitised free text (control chars, 500 chars) in `ProcedureBusinessException.Message`, returned as 422 | `MssqlProcedureInvoker.cs:191-194`, `:323-360`; `IProcedureExecutionService.cs:27-33`; `PER:357-360` |
| F-11 | Non-business DB failures: `LogError(ex, ...)` logs driver text (may contain values); audit stores type name only | `GPES:189-194` |
| F-12 | No concurrency lease on the procedure path; gate `ITableReadConcurrencyGate.TryEnter(key, max)` (singleton `GatewaySecurityServiceExtensions.cs:111`), key shape `{tenant}|{sid}|{table}` lower-case, 429 via `GatewayThrottledException(2)` | `GovernedSqlExecutor.cs:403-429`; option `DatabaseOptions.cs:21` (default 4, `[Range(0,1000)]`); `PER` has no 429 mapping (would be 500) |
| F-13 | `cleared_columns` lifts any non-`Deny` level to `Clear` | `GPES:603-608` (set `:529`) |
| F-14 | Explicit consent column rules (base and applicable scope-specific) land in `TableAccessDecision.ColumnAccess`; scope rules only harden | `ConsentResolutionService.cs:238-243`, `:266-290`; `TableAccessDecision.cs:396-436` |
| F-15 | With ABAC policies the decision is rewritten so every catalog column carries its **effective** level (sensitivity mask becomes indistinguishable from an explicit `Mask`) | `GPES:402-407` |
| F-16 | REST list and OpenAPI filter by role only; open-schema mode documents **every** Active procedure for anonymous callers | `PER:63-76`, `:78-88`, `:41-56`, `IsVisibleTo :241-250` |
| F-17 | Audit rows are readable only by audit/governance roles (no caller self-view) | `GovernanceEndpoints.Audit.cs:222-245` |
| F-18 | Write-mode declarations are rejected at load | `ProcedureDefinitionLoader.cs:211-213` |
| F-19 | No procedure integration tests exist; MSSQL fixture available | `tests/Autheris.Tests.Integration/SharedDatabaseServers.cs` |

## 2. Design decisions

**D-1 Visibility stage (R-153-1).** New internal record `ProcedureVisibility(bool Visible, string Reason, IReadOnlyDictionary<string,(TableAccessDecision Decision, TableMetadata Meta)> Decisions)` and `GPES.EvaluateVisibilityAsync(RegisteredProcedure, ClaimsPrincipal, TenantId, ct)` (async, because consent is async; replaces the PRD's static `IsVisible`). Visible only if: SID present or consent bypass; role gate passes; table set non-empty; every table passes `EvaluateTableAsync` with the existing row-filter rule. Table set: Active = `Validation.ReferencedTables` + `ResultTable`; non-Active = `ResultTable` only (none -> not visible). **Zero tables in any mode = not visible** (closes F-7; default taken, fail closed). **Any exception except `OperationCanceledException` inside the stage = not visible**, logged at Error with procedure name, exception type and trace id, audited `reason=visibility-error` (closes F-8). Public seam for list, OpenAPI and later INC-4/MCP: `IProcedureExecutionService.IsVisibleAsync(RegisteredProcedure, ClaimsPrincipal, TenantId, ct)` returning `Visible && State == Active`.

**D-2 Fixed order in `ExecuteAsync` (R-153-3).** trace id -> registry lookup -> visibility (404) -> availability (503, visible callers only) -> lease (429) -> `ValidateInputs` (400) -> invoke -> row scope -> `GovernResult` -> audit `PROCEDURE_EXECUTE` -> release lease (`finally`). The visibility `Decisions` are reused by row scope and `GovernResult` (no second consent evaluation). Consent is not re-evaluated during a long call (S-6, accepted risk, same as the SQL path; the visibility `Decisions` are computed once before invocation).

**D-3 One not-found exception.** Every pre-visibility failure (unknown, missing SID, role, consent, zero tables, visibility error, unavailable-but-invisible) throws `KeyNotFoundException` with the constant message `"Procedure endpoint not found."`; the route keeps the single 404 writer. `SecurityException` is no longer thrown by `GPES`; the `PER` 403 branch is removed (no remaining producer). Row-scope key unusable and row-scope lookup failure (F-9) throw `ProcedureUnavailableException` -> 503 (R-153-2a, default taken for the lookup failure as well: both are operator errors after visibility).

**D-4 Audit of denials (Q-7 default yes).** Unknown names are audited `PROCEDURE_DENIED` with `reason=unknown` through a new `AuditAsync(string target, ...)` overload, only for authenticated callers (anonymous bypass callers are not audited) and rate-limited per tenant and SID (existing limiter or a small in-memory token bucket, default 30 per minute; excess dropped silently and counted in one metric/log line, response unchanged; SEC-PROC152-05); target = name if it matches `^[A-Za-z0-9_.\-]{1,128}$`, else the literal `<invalid>` (no other caller-supplied text is stored or logged). Reasons kept server-side: `unknown`, `identity`, `role`, `consent`, `no-result-table`, `visibility-error`, `unavailable`. All denial audits use the same writer and tier, so the unknown path performs exactly one audit write, awaited at the same point as the denied paths, with no early return before it (timing, see R-3). One trace id per call (`Activity.Current?.TraceId` or a new GUID `N`) is passed to every audit row of the call.

**D-5 Lease (R-154-5).** New optional ctor parameter `ITableReadConcurrencyGate? concurrencyGate` (resolved by the existing `AddScoped` at `GatewaySqlEngineServiceExtensions.cs:107`, no DI change). For each consent-checked table, in ordinal order of `Meta.Identifier.ToQualifiedName()`: key `$"{tenant}|{userSid}|{qualifiedName}".ToLowerInvariant()` (same key as `GovernedSqlExecutor`, so SQL and procedure reads of the same table share one budget); limit `DataSources.MaxConcurrentReadsPerUserAndTable` (0 = disabled). A null lease releases the already taken ones and throws `GatewayThrottledException(2)`, audited `PROCEDURE_THROTTLED`. Leases are disposed in one `finally` that encloses validation through the final audit (covers exceptions and cancellation). Fail closed: limit > 0 and gate null -> `InvalidOperationException` (500, after visibility, so no oracle).

**D-6 Generic business error (R-154-4).** `ProcedureBusinessException` becomes `(int? ErrorNumber, string? SqlState)` with the fixed `Message` "The procedure rejected the request."; `TryGetBusinessError` returns number/SQLSTATE only; `SanitizeMessage` is deleted. `GPES` logs at Warning `procedure, number, sqlState, traceId` and audits `reason=business-error, number|sqlState`, never text (PRD constraint "no procedure message text in logs or audit" is stricter than the intake wording "detail in server log" and wins). Non-business `DbException`: log type, number/SQLSTATE and trace id **without** the exception object; other exception types keep `LogError(ex, ...)`.

**D-7 `cleared_columns` precedence (R-152-1).** Lift allowed only when the column's `Mask` comes from catalog sensitivity: `liftable = !decision.ColumnAccess.ContainsKey(col) && decision.HasUnconstrainedColumnAllow && effective == Mask` on the decision **before** the ABAC rewrite of F-15. `EvaluateTableAsync` therefore returns, next to the decision, the set `SensitivityOnlyMasked` (computed before `GPES:405`). Explicit `Mask`/`Deny` (base or scope-specific) is never lifted. Sourceless (computed) columns keep today's behaviour in INC-1 (`GPES:561-571`, INC-3 replaces it with `cleared_for_scopes`). Compatible with #143 R-11: #143 only adds a further condition to `liftable`.

**D-8 List and OpenAPI (R-153-4, OQ-12).** `HandleList` and `HandleOpenApi` become async, inject `IProcedureExecutionService` and keep only `IsVisibleAsync == true`; `IsVisibleTo` is deleted. Anonymous callers in open-schema mode get an OpenAPI document with empty `paths`. The list keeps its shape.

## 3. Contracts and error shapes (REST, both GET and POST)

| Case | Status | Body (exact) | Headers |
|---|---|---|---|
| unknown, no SID, role, consent, zero tables, visibility error, unavailable+invisible | 404 | `{"error":"Procedure endpoint not found."}` | `Cache-Control: no-store`, JSON content type; nothing else name-dependent |
| unavailable, visible; row-scope key/lookup failure | 503 | `{"error":"Procedure endpoint is currently unavailable."}` | as above |
| lease rejected (visible) | 429 | `{"error":"Too many concurrent requests. Retry later.","code":"TOO_MANY_REQUESTS"}` | + `Retry-After: 2` |
| invalid parameters (visible) | 400 | unchanged (`ArgumentException` message) | as above |
| business error | 422 | `{"error":"The procedure rejected the request.","code":"PROCEDURE_REJECTED","traceId":"<id>"}` | as above |

`traceId` equals the audit rows' trace id (default taken; random per request, no information). Name-independent pre-service answers stay (invalid JSON 400, body too large 413): they do not depend on the name, so they are no oracle. INC-2 extends 422 with `error_codes`; INC-1 fixes the contract shape it extends.

## 4. Configuration

No new option. Uses `DataSources.MaxConcurrentReadsPerUserAndTable` (default 4, 0 disables, 0..1000). Startup validation: confirm that `GatewayStorageOptionsValidator` (or data annotations) rejects a value outside 0..1000; if nested annotations are not evaluated, add the check there (test `GatewayStorageOptionsValidator_RejectsOutOfRangeProcedureConcurrency`).

## 5. Work packages (one PR, TDD order)

| WP | Content | Size |
|---|---|:---:|
| WP-1 | D-1, D-3, D-4: visibility stage, unified not-found, audit reasons, row-scope 503, remove 403 branch | M |
| WP-2 | D-2, D-5: reorder, lease with `finally`, 429 mapping in `PER` | S |
| WP-3 | D-6: exception contract, invoker, logging, 422 shape | S |
| WP-4 | D-7: sensitivity-only lift set, `GovernResult` change | S |
| WP-5 | D-8: list/OpenAPI filter, anonymous open-schema omission | S |
| WP-6 | Integration tests (section 6.2) | M |

## 6. Test plan

**6.1 Unit** (`tests/Autheris.Tests.Unit/Procedures/ProcedureSecurityInc1Tests.cs`, plus updates of the 11 `SecurityException` assertions in `ProcedureEndpointTests.cs`, `ProcedureRowScopeTests.cs`, `ProcedureVirtualFilterTests.cs`, `PureYamlProcedureGovernanceTests.cs`):
- `Execute_UnknownRoleConsentNoTablesNoSid_AllThrowSameNotFound` (SR-1 service level; message constant)
- `Execute_InvalidParameters_WithoutVisibility_ThrowsNotFound_NotArgument` (SR-2)
- `Execute_Unavailable_Invisible404_Visible503` (AC-3)
- `Execute_CatalogModeWithoutTables_IsNotVisible` (F-7); `Execute_VisibilityStageThrows_IsNotFound_AndAuditsVisibilityError` (F-8)
- `Execute_UnknownName_AuditsReasonUnknown_WithInvalidMarkerForNonIdentifier`
- `Execute_RowScopeKeyUnusable_Throws503` (R-153-2a)
- `Execute_OverLimit_Visible_Throws429_Invisible_404` (AC-5); `Lease_TakenAfterVisibility_ReleasedOnSuccessExceptionAndCancel`; `Lease_KeyIsTenantUserTable_AndSharedWithSqlPath`; `Lease_LimitPositiveGateMissing_FailsClosed` (SR-7)
- `BusinessError_CarriesNumberOnly_NoTextInExceptionLogOrAudit` (SR-3, captured logger + audit fake, message `Asset 4711 of Mueller` / `dbo.Secret`)
- `ClearedColumns_ScopeSpecificMask_StaysMaskedForScopeA_ClearForScopeB` (AC-6); `ClearedColumns_BaseMask_NotLifted`; `ClearedColumns_Deny_NotLifted`; `ClearedColumns_SensitivityOnlyMask_Lifted`; `ClearedColumns_WithAbacRewrite_SensitivityOnlyMaskStillLifted` (F-15)
- `List_And_OpenApi_ShowOnlyVisibleActive` (AC-2); `OpenApi_OpenSchemaAnonymous_HasNoProcedurePaths` (OQ-12)
- `Routes_Map429WithRetryAfter_And422GenericShape`; DI test `ProcedureExecutionService_ResolvesConcurrencyGate`

**6.2 Integration** (`tests/Autheris.Tests.Integration/Procedures/ProcedureSecurityIntegrationTests.cs`, MSSQL fixture + web factory):
- `Differential_UnknownRoleConsentNoTableUnavailable_ByteIdenticalStatusBodyHeaders` - GET and POST, valid/invalid/missing parameters; headers compared without `Date` (SR-1, AC-1)
- `BusinessError_Throw50001_ReturnsGeneric422_NoTextInResponseLogAudit` (AC-4, SR-3)
- `ConcurrentCalls_BeyondLimit_429ForConsented_404ForOthers` (AC-5)
- `ClearedColumn_ScopeSpecificMask_EndToEnd` (AC-6)
- `List_And_OpenApi_ConsentDependent` (AC-2)

## 7. Files touched

Edit: `GPES`; `Autheris.Application/Procedures/Interfaces/IProcedureExecutionService.cs` (`IsVisibleAsync`, exception contract); `Autheris.Application/Procedures/Services/MssqlProcedureInvoker.cs` (`:191-194`, `:323-360`); `PER`; possibly `Autheris.Api/Configuration/Validation/GatewayStorageOptionsValidator.cs`. Tests as in section 6. Not touched: options classes, DDL, `TableAccessDecision.cs`, `ConsentResolutionService.cs`, DI extensions.

## 8. Overlap with waves 2/3 (I-1..I-30) and position

| Step | Shared file / concern | Collision |
|---|---|---|
| I-9 (`PLAN-FED-01` INC-1) | `PER:353` `SecurityException`->403 mapping listed in the FED delta error-contract work | Same catch block; INC-1 removes it. I-9 must rebase onto INC-1 and drop procedures from its list |
| I-23 (#114 WP-3) | `GPES` audit `:690` recorder seam | Textual conflict in `AuditAsync`; INC-1 first, I-23 adds the recorder to the new overload too |
| I-25 (#129) | `GPES:718` text-only virtual-filter fallback | Same file, different method; low risk, inside the visibility stage (D-1 error rule applies) |
| I-29 (BL-06 B) | `MssqlProcedureInvoker` client spans | Adjacent edits; spans must not record exception text (D-6) |
| I-10 | lease concept (per-tenant/global federation lease) | No shared file; I-10 should reuse the D-5 key shape |
| Intake 2.7 (#143 R-11) | `GPES:603-608` | Builds on D-7 (adds the restricted-scope condition) |
| INC-2 (2.9), INC-3/4 (3.9) | `GPES.ExecuteAsync`, `GovernResult`, `PER` | Strictly after INC-1 (PRD section 10) |

I-1/I-2 are merged; I-3, I-5, I-6 (chunk 1) and #138 INC-1 (1.7) share no file. **Recommended position:** standalone security step (branch `fix/procedure-inc1-security`) **directly after I-3**, before I-5, stacked on I-3 like the B-steps; in any case before I-9, I-23, I-25, #143 and procedure INC-2.

## 9. Risks

- **R-1** Visibility on list/OpenAPI evaluates consent per procedure and table per request; acceptable with the consent decision cache, measured in the integration test (no new limit).
- **R-2** Hiding visibility-stage misconfiguration as 404 slows operations diagnosis; mitigated by Error log + audit reason + trace id.
- **R-3** Timing (closed by decision SEC-PROC152-04, no latency floor): no latency floor. The unknown-name path performs exactly one audit write through the same writer as the denied paths; the remaining difference (consent lookup) is accepted.
- **R-6 (S-3, accepted)** Anonymous (consent-bypass / open-schema) callers share the SID `anonymous`, so the lease key is one shared budget per table and one anonymous client can cause 429 for all others. Accepted risk per SEC-PROC152-07 (lease SID computed as in `GovernedSqlExecutor`: PrimarySid, then `sub`, then `anonymous`).
- **R-4** Callers that parsed procedure text from 422 lose it (no production; INC-2 adds fixed codes).
- **R-5** Zero-table catalog procedures stop working (F-7); intended fail-closed change.

## 10. Open questions (defaults taken)

1. Business error text never in logs (D-6 follows PRD over intake wording) - default taken.
2. `traceId` in the 422 body - default taken (yes).
3. Row-scope lookup failure -> 503 like key misconfiguration - default taken.
4. Zero-table procedures not visible - default taken.

---

## Phase 3 Security Review (csharp-security-expert, 2026-10-17)

**Verdict: APPROVED WITH CONDITIONS.** No plan revision required; the conditions are binding for Phase 4. Code facts F-1..F-13 re-verified against `GPES`/`PER`/`GovernedSqlExecutor` (ordering, `SecurityException` 403 branch, `ex.Message` into 422, `LogError(ex)`, `level != Deny` lift, lease key shape all as stated).

### Findings

| Id | Sev | Finding |
|---|---|---|
| S-1 | Medium | D-4 audits every unknown name as `PROCEDURE_DENIED`. Any caller (incl. anonymous in open-schema mode, or any authenticated user) can flood the audit store and evict real evidence. Not bounded in the plan. |
| S-2 | Medium | R-3 timing: unknown names skip consent/registry-dependent work, so a known-but-denied name is slower (consent lookup, async I/O). A latency floor is **not** required (brittle, penalises all denials, DoS lever); instead equalise the structure: same awaits, same single audit write, and one constant-time-ish path. Residual bound accepted, see SEC-04. |
| S-3 | Medium | Anonymous (consent-bypass / open-schema) callers share SID `anonymous`, so the D-5 lease key is one global budget per table: a single anonymous client can exhaust it and 429 everyone else. |
| S-4 | Low | `GPES` still has `ArgumentException.ThrowIfNullOrWhiteSpace(name)` before lookup and the route maps `ArgumentException` to 400 with `ex.Message`; any `ArgumentException` raised inside the visibility stage must not reach the 400 mapping. |
| S-5 | Low | D-5 derives the SID via `GetUserSid()` while `GovernedSqlExecutor` uses `PrimarySid` then `sub`; the "shared budget" claim holds only if the key SID is identical. |
| S-6 | Low | Visibility `Decisions` are computed before invocation and reused after it (D-2); a consent change during a long call is not re-evaluated. Acceptable (same as the SQL path), document only. |
| S-7 | Info | D-7/D-1/D-3/D-6/D-8 and defaults 1-5 are sound and fail closed; no existing control is weakened. 400 for invalid parameters moves behind visibility, so it is no oracle. |

### Binding conditions

- **SEC-PROC152-01** Visibility, 503 and 429 are decided strictly in the D-2 order; no parameter text, procedure name or driver text appears in any response before visibility is established. Unavailable-and-invisible is always the 404.
- **SEC-PROC152-02** Inside the visibility stage every exception except `OperationCanceledException` (including `ArgumentException`, `GatewaySecurityException`, `InvalidOperationException`) maps to not visible / 404 and is never rethrown to the route. `OperationCanceledException` propagates and is not audited as a denial.
- **SEC-PROC152-03** The 404 body, headers and status are produced by one writer with a constant message; the route must not echo the name, the reason or the exception type; the constant `KeyNotFoundException` message must also not be logged with the name at a level visible to callers (server log only, Information or lower is fine).
- **SEC-PROC152-04** Timing: no latency floor. The unknown-name path must perform exactly one audit write through the same writer, awaiting at the same point as the denied paths, and must not return early before it. Residual timing difference (consent lookup) is accepted and recorded in R-3 as closed-by-decision.
- **SEC-PROC152-05** Audit flood bound (S-1): unknown-name audit is allowed only for authenticated callers and rate-limited per tenant and SID (reuse an existing limiter or a small in-memory token bucket, default 30 per minute; excess is dropped silently and counted in one metric/log line, response unchanged). Anonymous (bypass) unknown names are not audited. Target is the sanitised `^[A-Za-z0-9_.\-]{1,128}$` form or `<invalid>`; no other caller-supplied text is stored or logged.
- **SEC-PROC152-06** Lease: taken only after visibility and 503, released in one `finally` covering validation, invoke, row scope, govern, audit and cancellation; partial acquisition is rolled back; a lease must be released even if the final audit write throws (dispose before or independent of the audit await failure). Limit > 0 with gate null fails closed (500 after visibility).
- **SEC-PROC152-07** Lease key SID must be computed exactly as in `GovernedSqlExecutor` (PrimarySid, then `sub`, then `anonymous`); for the anonymous bypass SID the key additionally includes the remote IP-independent constant only if a per-client discriminator exists, otherwise anonymous callers get the documented shared budget and S-3 is recorded as accepted risk in section 9.
- **SEC-PROC152-08** 422 and its audit/log lines carry only the fixed message, number/SQLSTATE, procedure id and trace id. Non-business `DbException` is logged without the exception object (and without `ex.Message`/`InnerException` text) at every level; the invoker's client span/activity must not record exception text.
- **SEC-PROC152-09** `cleared_columns` lifts only the sensitivity-only `Mask` computed before the ABAC rewrite; explicit base or scope-specific `Mask` and any `Deny` are never lifted; if the sensitivity-only set cannot be computed (null/exception) nothing is lifted (fail closed).
- **SEC-PROC152-10** Zero referenced tables and no `ResultTable` is not visible in every mode; zero tables never reaches invoke. Open-schema anonymous OpenAPI has empty `paths` and the list returns only `IsVisibleAsync` entries; list/OpenAPI evaluation failure for one procedure omits that procedure and never fails the whole response with details.
- **SEC-PROC152-11** `GPES` no longer throws `SecurityException`; the route 403 branch is removed, and the generic 500 handler never logs request parameter values.

### Mandatory security tests (`[Trait("Category","Security")]`)

- **T-PROC152-01** `Differential_UnknownRoleConsentNoTableUnavailable_ByteIdenticalStatusBodyHeaders` (GET/POST, all parameter variants).
- **T-PROC152-02** `VisibilityStage_AnyExceptionKind_Returns404_NotThrownToRoute` (theory over Argument/GatewaySecurity/InvalidOperation/KeyNotFound; Cancellation propagates).
- **T-PROC152-03** `Order_Invisible_NeverSees503_429_400_Visible_SeesInOrder` (theory per stage).
- **T-PROC152-04** `UnknownName_Anonymous_NotAudited_Authenticated_AuditedAndRateLimited_OneWritePerCall`.
- **T-PROC152-05** `UnknownName_NonIdentifierAndOversize_StoredAsInvalidMarker_NoRawTextInAuditOrLog`.
- **T-PROC152-06** `Lease_ReleasedOn_Success_Exception_Cancellation_AuditThrows_PartialAcquire` (gate fake counts outstanding leases = 0).
- **T-PROC152-07** `Lease_KeyEqualsSqlPathKey_PrimarySidThenSubThenAnonymous`.
- **T-PROC152-08** `Lease_LimitPositive_GateNull_FailsClosed500AfterVisibility_404ForInvisible`.
- **T-PROC152-09** `BusinessAndDbFailure_SecretText_AbsentFromResponseLogsAuditSpans` (capturing logger + audit fake + ActivityListener; message `Asset 4711 of Mueller`, `dbo.Secret`, a value in a `DbException`).
- **T-PROC152-10** `ClearedColumns_ExplicitBaseMask_ScopeMask_Deny_NeverLifted_SensitivityOnlyLifted_AlsoUnderAbac`.
- **T-PROC152-11** `ClearedColumns_SensitivitySetUnavailable_LiftsNothing`.
- **T-PROC152-12** `ZeroTableProcedure_AllModes_InvisibleAndNeverInvoked`.
- **T-PROC152-13** `OpenApiAndList_OpenSchemaAnonymous_NoProcedures_OneProcedureFailing_OmittedOnly`.
- **T-PROC152-14** `Route_NoSecurityExceptionBranch_NoParameterValuesInErrorLogs`.

Plan edits to apply by the architect/developer without re-review: add S-3 and the closed R-3 decision to section 9, and note S-6 in D-2.

---

## Changelog (Phase 4 developer)

- 2026-10-17 G-PLAN-REBASE: code facts F-1..F-13 re-verified on branch `fix/procedure-inc1-security` (merge base `origin/main` + I-3). Drift: `GovernedSqlExecutor` lives in `Autheris.Application/Sql/Services/`; `GatewayThrottledException` in `Autheris.Domain/Exceptions`; `GovernedSqlExecutor.cs` is not touched (SID logic duplicated in `GPES.AcquireLeases`, three lines, instead of a shared helper, to stay out of the parallel #128 work).
- 2026-10-17 Deviation D-A: the unknown-name audit limiter needs process-wide state, so `ProcedureDenialAuditLimiter` is registered as a singleton in `GatewaySqlEngineServiceExtensions` (the plan said "no DI change"). Fixed one-minute window per tenant and SID, default 30, at most 10000 tracked keys (excess keys are dropped, fail closed for the audit write only).
- 2026-10-17 Deviation D-B: `GatewayStorageOptionsValidator` checks `DataSources.MaxConcurrentReadsPerUserAndTable` in 0..1000 (nested data annotations are not evaluated), test `GatewayStorageOptionsValidator_RejectsOutOfRangeProcedureConcurrency`.
- 2026-10-17 Deviation D-C: OpenAPI no longer takes `documentAllForAnonymous` (open-schema anonymous documents no procedure). The 403 entry of the OpenAPI responses is replaced by 404; 429 is added.
- 2026-10-17 Deviation D-D: the invoker has no client spans/activities, so SEC-PROC152-08 (spans without exception text) holds trivially; the route logs `DbException` by type only.
- 2026-10-17 Deviation D-E: default `MaxConcurrentReadsPerUserAndTable` is 4, so `GovernedProcedureExecutionService` without a gate fails closed by default; existing unit fixtures pass a `GrantAllReadConcurrencyGate` test double.
- 2026-10-17 Deviation D-F: container integration tests of section 6.2 are not part of this change (the differential check T-PROC152-01 is implemented at route level in the unit suite against the real service).

# Architectural Implementation Plan: Casbin & SQL Invariant Hardening

**Date:** 2026-10-07  
**Branch:** `feat/ast-target-dialect-generator`  
**Targets:** 
- Casbin Core & Startup: F-4, E-4, E-5, R-POL-5, F-7, R-POL-8
- SQL & Procedure Governance: D-3, D-6, D-9, D-10

---

## 1. Problem Statement & Threat Analysis

### 1.1 Casbin Core Concurrency & Startup (F-4, E-4, E-5, R-POL-5, F-7, R-POL-8)
1. **F-4 (Enforcer Thread Safety):**
   - In `CasbinEnforcementService.cs:576, 615, 706`, `enforcer.Enforce(...)` and `enforcer.HasRoleForUser(...)` are invoked concurrently by multiple request threads sharing the snapshot's tenant enforcers.
   - In Casbin.NET, internal evaluation states and role managers are not thread-safe for concurrent read/evaluation operations.
   - **Fix:** Synchronize access to `enforcer` via `lock (enforcer)` during role evaluation and policy enforcement.
2. **E-4 (Public GetOrCreateEnforcer Exposure):**
   - `CasbinEnforcementService.GetOrCreateEnforcer(TenantId tenant)` was declared `public` and returned internal enforcers or `_emptyFallbackEnforcer`.
   - **Fix:** Restrict visibility to `internal` as it has no public API consumers.
3. **E-5 (Stale Epoch Cache Race):**
   - Concurrent policy evaluations that begin before a policy reload (`_decisionCache.Clear()`) could write their results into `_decisionCache` after the epoch incremented.
   - **Fix:** Guard cache insertions with `if (Volatile.Read(ref _currentSnapshot).Epoch == snapshot.Epoch)`.
4. **F-7, R-POL-5, R-POL-8 (Startup Validation & Warnings):**
   - If `Casbin.Enabled == false`, a configured `ModelPath` was never validated at startup. When resolving DI lazily, an invalid path led to 500 on first access.
   - If `Casbin.Enabled == true`, `PolicyPath` was checked for existence and file length > 0, but never parsed. A file containing only comments or invalid syntax was accepted at startup, rendering authorization broken or fail-open at runtime.
   - No warning was logged when Casbin was disabled.
   - **Fix:** 
     - Validate `ModelPath` whenever configured, regardless of `Casbin.Enabled`.
     - When `Casbin.Enabled == true`, test-parse `PolicyPath` during startup validation and ensure at least one `p`-rule exists.
     - Log a warning if `Casbin.Enabled == false`.

### 1.2 SQL & Procedure Governance (D-3, D-6, D-9, D-10)
1. **D-6 (Information Disclosure in 403 Forbidden):**
   - `SqlDataSourceExecutor.cs:329` threw `GatewayForbiddenException($"Invalid tenant identity '{tenantVal}'.")`. Since `FORBIDDEN` error messages are allowlisted in `ErrorSanitizingFilter`, client-provided invalid tenant strings were echoed back.
   - **Fix:** Log `tenantVal` securely at Warning level, and throw `GatewayForbiddenException("Invalid tenant identity.")`.
2. **D-9 (Procedure Invoker Invalid Tenant ID Handling):**
   - `MssqlProcedureInvoker.cs:80, 90` directly called `new TenantId(security.TenantId)`. For malformed SIDs or tenant names, this threw `ArgumentException` yielding 500 Internal Server Error instead of 403 Forbidden.
   - **Fix:** Validate with `TenantId.TryParse(security.TenantId, out var validatedTenantId)` and throw `GatewayForbiddenException` on failure.
3. **D-10 (Committed Transaction Leak in WebSQL):**
   - In `GovernedSqlExecutionService.cs:911, 928`, `tx = null` was executed immediately after `CommitAsync`. Consequently, `finally { if (tx != null) await tx.DisposeAsync(); }` was bypassed for every successfully committed transaction.
   - **Fix:** Retain `tx` reference and track completion with `bool txCommitted = false;`. Dispose `tx` unconditionally in `finally`.
4. **D-3 (WebSQL DML Transaction Disposal & Rollback Guard):**
   - In `GovernedSqlExecutionService.ExecuteDmlInTransactionAsync`, when `existingTx == null`, a new transaction was started but never disposed if execution succeeded.
   - **Fix:** Track ownership `bool ownsTx = existingTx == null;` and dispose owned transaction in `try ... finally`. Guard rollback against secondary exceptions.

---

## 2. Implementation Steps

1. **Casbin Core:**
   - Update `CasbinEnforcementService.cs`:
     - Synchronize evaluations with `lock (enforcer)`.
     - Update `GetOrCreateEnforcer` to `internal Enforcer GetEnforcer(TenantId tenant)`.
     - Guard `_decisionCache.TryAdd` with snapshot epoch match.
2. **Casbin Startup Validation:**
   - Update `GatewayServiceCollectionExtensions.cs`:
     - Validate `options.Casbin.ModelPath` if specified.
     - If `options.Casbin.Enabled`: parse policy text using rule verification to ensure valid syntax and presence of `p`-rules.
     - Add warning log when `options.Casbin.Enabled` is false.
3. **SQL & Procedures:**
   - Update `SqlDataSourceExecutor.cs` (D-6).
   - Update `MssqlProcedureInvoker.cs` (D-9).
   - Update `GovernedSqlExecutionService.cs` (D-3, D-10).
4. **Tests:**
   - Add unit tests verifying thread safety under concurrent evaluations.
   - Add unit tests verifying startup validation with comments-only policy file (R-POL-5) and invalid model path with `Enabled=false` (F-7).
   - Add unit tests for generic 403 message on invalid tenant (D-6).
   - Add unit tests for `MssqlProcedureInvoker` 403 on invalid tenant (D-9).
   - Add unit tests for transaction disposal on commit (D-10) and DML transaction lifecycle (D-3).
5. **Status Update & Verification:**
   - Update `docs/plans/status-und-umsetzungsplan-2026-10-07.md`.
   - Run complete test suite and architecture tests.

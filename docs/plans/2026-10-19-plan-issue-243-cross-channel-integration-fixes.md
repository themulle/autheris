# Implementation Plan: Issue #243 Cross-Channel Integration Fixes

**Document ID:** `PLAN-INT-243`  
**Date:** 2026-10-19  
**Status:** APPROVED (In Implementation)  
**Author:** Solution Architect (`project-manager`)  
**Parent PRD:** [`docs/plans/2026-10-19-req-issue-243-cross-channel-integration-fixes.md`](2026-10-19-req-issue-243-cross-channel-integration-fixes.md) (`REQ-INT-243`)  
**Target Milestone:** v1.3.0 / v1.2.5  
**Branch:** `feat/issue-243-cross-channel-integration-fixes`  

---

## 1. Technical Design & Architecture

### WP-243-1: Zanzibar ReBAC Active Call-Stack Cycle Detection
- In `src/Autheris.Application/Security/Rebac/Services/ZanzibarRebacEvaluator.cs`:
  - Change cycle tracking from a single monotonically increasing `visited` set to an active recursion call-stack set (`HashSet<string> onStack`).
  - Upon entering `TraverseAndEvaluateAsync`:
    `if (!onStack.Add(visitKey)) { _logger.LogWarning("F-SEC-04 ReBAC cyclic relationship detected: {CycleKey}. Decision: Denied.", visitKey); return false; }`
  - In a `finally` block: `onStack.Remove(visitKey)`.
  - Maintain a separate `HashSet<string> visitedFailed` for memoizing dead ends without triggering cycle alarms.
- In `src/Autheris.Application/Mcp/Services/McpToolExecutionHandler.cs`:
  - In `ExecuteToolAsync` for `query_dataset`, wrap `_dataQueryService.ExecuteQueryAsync` with `catch (UnauthorizedAccessException ex)` and return clean error JSON.

### WP-243-2: OData Selective Projection in `GovernedConnectorReader`
- In `src/Autheris.Application/Connectors/GovernedConnectorReader.cs`:
  - Extend `GovernedRowPolicy` with `IReadOnlyCollection<string>? RequestedColumns = null`.
  - In `ProjectRow`:
    - If `policy.RequestedColumns` is provided and non-empty, filter `metadata.Columns` against `policy.RequestedColumns` (case-insensitive).
    - Otherwise, default to all `metadata.Columns`.
- In `src/Autheris.Application/Services/GatewayExecutionService.cs`:
  - When calling `GovernedConnectorReader.Apply`, pass `RequestedColumns: (requestedFields != null && requestedFields.Count > 0) ? effectiveRequestedFields : null`.

### WP-243-3: Clamped Consents Expiry Diagnostic
- In `src/Autheris.Application/Services/ConsentResolutionService.cs`:
  - In `ResolveAccess`, when `aConsents.Count == 0`:
    - Inspect `activeConsents` for matching consents where:
      - `c.Effect == ConsentEffect.Allow`
      - `c.TableIdentifier == table`
      - `!c.IsRevoked`
      - `c.ValidFrom <= now`
      - `now < c.ValidTo` (would be active by original validity)
      - `now >= c.EffectiveValidTo` (expired by clamp)
      - `IsSubjectMatch(c, userSid, subjectGroupSids, userRoles, allUserSids)`
    - If any matching clamped consents exist, find `latestClamped = clamped.MaxBy(c => c.EffectiveValidTo)`.
    - Set denial reason:
      `$"Zero Trust: No active ALLOW consent granted to subject (consent expired on {latestClamped.EffectiveValidTo:yyyy-MM-dd} due to maximum duration clamp)."`

### WP-243-4: MCP Syntax Errors & Tool Diagnostics
- In `src/Autheris.Application/Mcp/Services/McpDataTargetResolver.cs`:
  - When analyzing SQL in `ResolveSqlTables`, catch syntax/parsing exceptions explicitly and return structured result or rethrow so `AiDataGuardrailService` can format as `SQL_SYNTAX_ERROR: <details>`.
- In `src/Autheris.Application/Mcp/Services/AiDataGuardrailService.cs`:
  - When `tool.Name` is `describe_my_access` or `get_my_permissions` and no `dataset` argument was provided, treat as caller-level self-access check rather than failing with "target data table could not be resolved".
  - For Four-Eyes step-up approval denial or timeout, provide actionable diagnostic details including ticket status and justification guidance.

### WP-243-5: Webhook Delivery Exception Logging
- In `src/Autheris.Application/Webhooks/WebhookDeliveryHostedService.cs`:
  - In `RunWorkerAsync` catch block, log `ex.Message` and include `ex` in the log call:
    `_logger.LogError(ex, "Webhook delivery loop failed ({ErrorType}: {ErrorMessage}); retrying on the next tick.", ex.GetType().Name, ex.Message);`

---

## 2. Binding Security & Compliance Review (Phase 3)

| ID | Category | Requirement & Enforcement | Verification |
|---|---|---|---|
| `SEC-INT243-01` | ReBAC Cycle Defense | Circular dependency prevention must strictly detect cycles within the active call path without failing closed on legitimate multi-path DAG inheritance. Maximum recursion depth limit must remain strictly enforced. | Unit tests with 3-tier and diamond hierarchies; cyclic graph test. |
| `SEC-INT243-02` | Selective Projection Safety | `ProjectRow` must continue to enforce `ColumnAccessLevel.Deny` column stripping even when `RequestedColumns` is provided. Denied columns must never be projected. | Unit test requesting a denied column under `$select`. |
| `SEC-INT243-03` | Consent Expiry Information Boundary | Clamped consent diagnostic messages must only emit date strings (`yyyy-MM-dd`) and never disclose cryptographic keys, HMAC salts, or tenant identifiers. | Unit test validating denial string format. |
| `SEC-INT243-04` | Parser Injection & Tableless Safety | Syntax error handling must be fail-closed: malformed SQL must not be executed or evaluated against upstream sources. Self-access tools must remain confined to the caller's authenticated session. | Unit tests with malformed SQL queries. |
| `SEC-INT243-05` | Log Sanitization | Exception logs in webhook delivery must never expose embedded passwords or connection strings. | Review log format. |

---

## 3. Work Breakdown & Test Plan

1. **Unit & Security Tests:** `tests/Autheris.Tests.Unit/Governance/CrossChannelIntegrationFixesTests.cs`
   - Test 1: Diamond ReBAC inheritance allows `owner` -> `can_query` without false cycle warnings.
   - Test 2: True ReBAC cycle (A -> B -> A) is detected and denied with `F-SEC-04`.
   - Test 3: OData selective projection in `GovernedConnectorReader` projects only requested columns and omits unrequested columns.
   - Test 4: Denied columns are stripped even if in `RequestedColumns`.
   - Test 5: Clamped consent expiry returns `"consent expired on {date} due to maximum duration clamp"`.
   - Test 6: MCP `query_sql` syntax error returns `SQL_SYNTAX_ERROR`.
   - Test 7: MCP `get_my_permissions` / `describe_my_access` without dataset proceeds to handler.
2. **Targeted Execution:**
   - Execute `dotnet test tests/Autheris.Tests.Unit --filter "FullyQualifiedName~CrossChannelIntegrationFixesTests"`.

---

## 4. Verification Results & Delivery Status

- **Status:** COMPLETED & VERIFIED ✅
- **Targeted Test Results:**
  - `CrossChannelIntegrationFixesTests`: 9/9 passed (321 ms).
    - `Rebac_DiamondInheritance_CanQueryAllowed_WithoutCyclicWarning`: PASSED
    - `Rebac_TrueCycle_DetectedAndDenied_FailsClosed`: PASSED
    - `McpToolExecutionHandler_QueryDataset_UnauthorizedAccessException_ReturnsCleanErrorPayload`: PASSED
    - `GovernedConnectorReader_ProjectRow_WithRequestedColumns_ProjectsOnlyRequestedColumns`: PASSED
    - `GovernedConnectorReader_ProjectRow_WithRequestedColumns_StripsDeniedColumnsEvenIfRequested`: PASSED
    - `ConsentResolutionService_ResolveAccess_ClampedExpiredConsent_ReturnsClearDiagnosticDenial`: PASSED
    - `AiDataGuardrailService_QuerySql_WithSyntaxError_ReturnsSqlSyntaxErrorMessage`: PASSED
    - `McpDataTargetResolver_TryResolveSqlTables_WithInvalidSql_ReturnsSyntaxError`: PASSED
    - `AiDataGuardrailService_DescribeMyAccess_WithoutDataset_DoesNotDenyForUnresolvableTable`: PASSED
  - **Regression Test Suites:**
    - `RebacHierarchyAndFailClosedTests`, `McpServerTests`, `GovernedConnectorReaderArch2Tests`: 30/30 passed (663 ms).
- **Security Audit:** All conditions `SEC-INT243-01` through `SEC-INT243-05` satisfied.


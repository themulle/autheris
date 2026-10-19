# Product Requirements Document: Issue #243 Cross-Channel Integration Fixes

**Document ID:** `REQ-INT-243`  
**Date:** 2026-10-19  
**Status:** APPROVED  
**Author:** Solution Architect (`project-manager`)  
**Target Milestone:** v1.3.0 / v1.2.5  
**Branch:** `feat/issue-243-cross-channel-integration-fixes`  

---

## 1. Executive Summary & Problem Statement

Comprehensive cross-channel integration tests against Autheris v1.2.4 across REST Data API, Model Context Protocol (MCP), OData v4, and Governance subsystems identified 4 functional bugs and 4 operational/diagnostic gaps:

1. **WP-243-1: ReBAC False-Positive Cycle Detection in Diamond Inheritance:**
   - *Problem:* `ZanzibarRebacEvaluator.cs` used a non-backtracking `HashSet<string> visited`. In standard enterprise role hierarchies (e.g. `can_query` -> `viewer`, `editor`, `owner`, where `editor` and `owner` also inherit `viewer`), traversal of multiple incoming paths to the same permission resulted in `visited.Add()` returning false on second path traversal. This falsely triggered cyclic relationship detection, emitting thousands of warning logs and rejecting authorized callers (`Decision: Denied`).
   - *Requirement:* Replace cycle detection with active recursion call-stack tracking (`onStack`). Maintain explored negative results to avoid redundant evaluations without falsely reporting cycles.
   - *MCP Handling:* In `query_dataset`, catch `UnauthorizedAccessException` and return clean structured error result instead of unhandled 500 crash.

2. **WP-243-2: OData `$select` Column Projection Bug:**
   - *Problem:* When an OData query requested a subset of columns via `$select` (e.g. `$select=item_id`), `GovernedConnectorReader.ProjectRow` iterated over all `metadata.Columns`, populating missing fields as `null` and causing OData to serialize all columns (e.g. 95 columns with 94 null values).
   - *Requirement:* Pass `RequestedColumns` in `GovernedRowPolicy` and only project the requested columns when specified.

3. **WP-243-3: Clamped Consents Expiry Diagnostic:**
   - *Problem:* When a subject's consents on a table expired due to `IndividualGrantLimits` read-time duration clamping (`EffectiveValidTo` < `now` while original `ValidTo` was still in the future), the decision returned generic `"Zero Trust: No active ALLOW consent granted to subject."`, leaving users and operators unaware of the clamp expiry.
   - *Requirement:* Detect when consents were expired due to the read-time duration clamp and return actionable diagnostic message:
     `"Zero Trust: No active ALLOW consent granted to subject (consent expired on {EffectiveValidTo:yyyy-MM-dd} due to maximum duration clamp)."`

4. **WP-243-4: MCP Syntax Errors and Tool Diagnostics Alignment:**
   - *Problem:* When `query_sql` encountered a SQL syntax error (e.g. `SELEC * FRM ...`), `McpDataTargetResolver` caught the exception and returned `null`, which `AiDataGuardrailService` misreported as `"target data table could not be resolved (fail-closed)"`. Furthermore, `describe_my_access` and `get_my_permissions` without a `dataset` parameter failed with unresolvable table error. Four-Eyes timeout and denial lacked actionable error guidance.
   - *Requirement:* Surface `SQL_SYNTAX_ERROR` with parser diagnostic details. Support table-less invocation of self-access tools (`describe_my_access`, `get_my_permissions`). Provide actionable guidance for Four-Eyes step-up approval denial/timeout.

5. **WP-243-5: Webhook Delivery Logging & Exception Diagnostics:**
   - *Problem:* `WebhookDeliveryHostedService` swallowed exception messages (`ex.Message`), logging only exception type names (`SqliteException`), obscuring failure causes (e.g. database locks or disk I/O errors).
   - *Requirement:* Log `ex.Message` and include inner exception details in the webhook delivery loop, while preserving credential masking.

---

## 2. Functional Requirements & Acceptance Criteria

### AC-243-1: ReBAC Multi-Path Inheritance
- Given a ReBAC schema with diamond inheritance (`can_query` inherited by `viewer`, `editor`, `owner`; `viewer` inherited by `editor`, `owner`),
- When a caller assigned only `owner` checks `can_query`,
- Then access is granted (`Allowed = true`) without logging false-positive cyclic relationship warnings.
- Only true cycles (e.g. A -> B -> A on the active recursion path) are flagged as cyclic and denied.

### AC-243-2: OData Selective Projection
- Given a table with multiple columns in metadata,
- When a query specifies `$select` / `RequestedFields` (e.g. `["item_id"]`),
- Then `GovernedConnectorReader.ProjectRow` projects only the requested columns that are not denied. Unrequested columns are omitted, not emitted as `null`.

### AC-243-3: Clamped Consent Expiry Guidance
- Given an individual ALLOW consent whose `ValidTo` is 2027-01-01, but whose `EffectiveValidTo` is clamped to 2026-03-01 by `IndividualGrantLimits`,
- When access is evaluated after 2026-03-01 and no other active ALLOW consents exist,
- Then `TableAccessDecision.DeniedReasons` includes:
  `"Zero Trust: No active ALLOW consent granted to subject (consent expired on 2026-03-01 due to maximum duration clamp)."`

### AC-243-4: MCP Actionable Diagnostics & Tableless Self-Access
- Given an invalid SQL statement sent to MCP `query_sql`,
- When guardrail resolves target tables,
- Then the response error message indicates `SQL_SYNTAX_ERROR` along with parser details.
- Given an MCP call to `describe_my_access` or `get_my_permissions` with no `dataset` parameter,
- Then the guardrail allows execution to proceed to the handler instead of failing with "target data table could not be resolved".
- When Four-Eyes approval is denied or times out, an actionable status and guidance message is returned.

### AC-243-5: Webhook Delivery Exception Visibility
- When an exception occurs during outbox batch polling or delivery,
- Then the log entry includes `ex.Message` and the exception type.

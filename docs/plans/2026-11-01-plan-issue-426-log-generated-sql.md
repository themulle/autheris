# Implementation Plan: Governed Logging of Generated SQL Statements (#426)

**Document ID:** `PLAN-LOG-426`  
**Phase:** 2 (Architectural Implementation Plan)  
**Author:** Solution Architect (`csharp-architect`)  
**Date:** 2026-11-01  
**Status:** PROPOSED  
**Issue Reference:** [Issue #426](https://github.com/themulle/autheris/issues/426)  
**PRD Reference:** [`2026-11-01-req-issue-426-log-generated-sql.md`](2026-11-01-req-issue-426-log-generated-sql.md) (`REQ-LOG-426`)  
**Branch:** `fix/issue-426-log-generated-sql`

---

## 1. Problem & Scope Analysis

When `Gateway:Logging:LogGeneratedSql=true` (or data source profile `LogGeneratedSql=true`) is configured, generated SQL is logged at `Debug` level by query services. However, structured log records emitted to JSON stdout / OpenTelemetry log exporters replace the statement attribute with `"[redacted]"`:
```json
{"body":"GovernedTreeQueryService: Generated SQL: {Sql}","attributes":{"Sql":"[redacted]"}}
```

### Cause
In `src/Autheris.Infrastructure/Telemetry/LogRedactionProcessor.cs`:
- The log processor inspects all structured log record attributes in `IsSensitiveKey(string key)`.
- It marks any attribute sensitive if `key.Contains("sql", StringComparison.OrdinalIgnoreCase)` or `key.Contains("query", StringComparison.OrdinalIgnoreCase)`.
- Because query services used message templates with `{Sql}` and `{SecuredSql}`, the key names (`"Sql"`, `"SecuredSql"`) matched `Contains("sql")`, unconditionally triggering redaction to `"[redacted]"`.
- Furthermore, string attributes are truncated to 1024 characters by default (`LogRedactionProcessor.cs:54-57`), which would aggressively truncate complex enterprise SQL queries.

### Scope
- **In Scope:**
  1. Add approved statement and query hash attribute names (`StatementText`, `QueryHash`, `TelemetryAttributes.DbQueryText`) to the `LogRedactionProcessor` allowlist.
  2. Expand string length bounds for approved statement attributes from 1024 to 4096 characters in `LogRedactionProcessor`.
  3. Update `GovernedTreeQueryService`, `GovernedSqlRewriter`, and `SqlProcedureRowScopeResolver` to:
     - Scrub queries using `SqlTextScrubber.TryScrub` before logging (ensuring literal sanitization and query hash calculation).
     - Log using approved template placeholders: `[{QueryHash}]: {StatementText}`.
     - Strictly respect `Telemetry:Redaction:AllowStatementText` as a safety ceiling.
  4. Ensure raw parameter dictionaries (`cmd.Parameters` or `compiled.Parameters`) are NEVER logged.
  5. Add unit and security regression tests in `Autheris.Tests.Unit`.
- **Out of Scope:**
  - Logging unscrubbed or raw literal values.
  - Logging SQL statement text in permanent audit trails (`AUDIT_LOG_ENTRIES`).

---

## 2. Architectural Design Decisions

### D-1: Approved Statement Attribute Keys in `LogRedactionProcessor`
- Update `LogRedactionProcessor.IsSensitiveKey(string key)`:
  - Add explicit exemptions for `StatementText`, `QueryHash`, and `TelemetryAttributes.DbQueryText` (`"db.query.text"`).
  - Keys containing `"sql"` or `"query"` (e.g. `SqlQuery`, `UserSql`, `RawQuery`) continue to be strictly redacted to `"[redacted]"`. Only the vetted, scrubbed statement attributes are permitted.
- Update truncation logic:
  - For attributes identified as statement text (`StatementText` or `TelemetryAttributes.DbQueryText`), apply a bound of 4096 characters (`MaxStatementLength = 4096`).
  - All other string attributes retain their existing 1024-character truncation ceiling.

### D-2: Universal Query Sanitization via `SqlTextScrubber`
- Before logging generated SQL in `GovernedTreeQueryService`, `GovernedSqlRewriter`, and `SqlProcedureRowScopeResolver`:
  - Invoke `SqlTextScrubber.TryScrub(sql, dialect, out var scrubbedSql, out var queryHash)`.
  - If scrubbing fails (e.g., query exceeds `MaxSqlLengthBytes = 64 KiB`), log only a diagnostic message with the failure reason and omission marker, never raw SQL.
  - Log message template:
    `"{Service}: Generated SQL [{QueryHash}]: {StatementText}"`
  - Output attributes:
    `QueryHash`: SHA-256 hexadecimal hash (matches trace tag `autheris.db.query.hash`).
    `StatementText`: Parameterized SQL statement with comments and literals scrubbed.

### D-3: Fail-Closed Telemetry Redaction Ceiling
- Enforce the precedence established in SEC-DS05-06:
  - `bool allowStatementText = _options.Telemetry?.Redaction?.AllowStatementText ?? true;`
  - Logging is only performed when:
    1. Effective `LogGeneratedSql == true`.
    2. `allowStatementText == true`.
    3. `_logger.IsEnabled(LogLevel.Debug)` is `true`.
  - If `AllowStatementText == false`, no statement text is logged regardless of `LogGeneratedSql`.

---

## 3. Component & File Changes

| File | Project | Nature of Change |
|---|---|---|
| `src/Autheris.Infrastructure/Telemetry/LogRedactionProcessor.cs` | `Autheris.Infrastructure` | Add `StatementText`, `QueryHash`, `TelemetryAttributes.DbQueryText` to non-sensitive allowlist; allow 4096 chars for statement keys. |
| `src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs` | `Autheris.Application` | Check `AllowStatementText`, scrub with `SqlTextScrubber`, log `{QueryHash}` and `{StatementText}`. |
| `src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs` | `Autheris.Application` | Check `AllowStatementText`, scrub with `SqlTextScrubber`, log `{QueryHash}` and `{StatementText}`. |
| `src/Autheris.Application/Procedures/Services/SqlProcedureRowScopeResolver.cs` | `Autheris.Application` | Check `AllowStatementText`, scrub with `SqlTextScrubber`, log `{QueryHash}` and `{StatementText}`. |
| `tests/Autheris.Tests.Unit/Security/LoggingRedactionDep8Tests.cs` | `Autheris.Tests.Unit` | Add tests verifying `StatementText` and `QueryHash` are unredacted and bounded to 4096 chars, while generic `Sql` keys remain redacted. |
| `tests/Autheris.Tests.Unit/Sql/GovernedLoggingGeneratedSqlTests.cs` | `Autheris.Tests.Unit` | Add end-to-end tests for `GovernedTreeQueryService` and `GovernedSqlRewriter` verifying scrubbed statement logging and redaction ceilings. |

---

## 4. Work Packages (Single Stream / TDD)

### WP-1: `LogRedactionProcessor` Exemptions and Bounds
- **Files:** `src/Autheris.Infrastructure/Telemetry/LogRedactionProcessor.cs`, `tests/Autheris.Tests.Unit/Security/LoggingRedactionDep8Tests.cs`
- **TDD Steps:**
  1. 🔴 **Red:** Test `StatementText_And_QueryHash_AreNotRedacted()` asserting `StatementText` and `QueryHash` values survive `LogRedactionProcessor` untouched.
  2. 🔴 **Red:** Test `StatementText_LengthTruncation_AllowsUpTo4096()` asserting statement text up to 4096 chars is preserved, while 5000 chars is truncated to 4096.
  3. 🔴 **Red:** Test `GenericSqlAttributes_RemainRedacted()` asserting `{Sql}`, `{SecuredSql}`, `{SqlQuery}` remain `"[redacted]"`.
  4. 🟢 **Green:** Update `LogRedactionProcessor.cs` with allowlist and 4096-char ceiling.
  5. 🔵 **Refactor:** Clean static helper methods.

### WP-2: Query Execution Logging Call-Sites
- **Files:** `GovernedTreeQueryService.cs`, `GovernedSqlRewriter.cs`, `SqlProcedureRowScopeResolver.cs`
- **TDD Steps:**
  1. 🔴 **Red:** Unit test `GovernedTreeQueryService_LogsGeneratedSql_WhenEnabled()` asserting debug log contains scrubbed SQL and query hash.
  2. 🔴 **Red:** Unit test `GovernedTreeQueryService_DoesNotLogSql_WhenAllowStatementTextIsFalse()` asserting no SQL is logged when redaction ceiling is active.
  3. 🟢 **Green:** Integrate `SqlTextScrubber.TryScrub` and new log message templates.
  4. 🔵 **Refactor:** Ensure zero extra allocations when debug log level is disabled.

---

## 5. Security & Threat Modeling (Phase 3 Alignment)

- **Parameter Leakage Defense:** Parameter dictionaries (`DbParameterCollection`, `CompiledTreeQuery.Parameters`) are excluded from logging. `SqlTextScrubber` scrubs any unexpected raw literals into replacement tokens.
- **Log Forgery / Injection:** Line breaks in statements are preserved or normalized by `SqlTextScrubber`; JSON log exporter formats attributes safely via `Utf8JsonWriter`.
- **Log Flooding / DoS:** Statements capped at 4096 characters by `LogRedactionProcessor`, and `SqlTextScrubber` rejects input larger than 64 KiB.
- **Fail-Closed Default:** `LogGeneratedSql` defaults to `false`. Without explicit opt-in and `AllowStatementText=true`, zero SQL statements are emitted.

---

## 6. Phase 3 Security Rulings & Binding Conditions (csharp-security-expert, 2026-11-01)

**Verdict:** APPROVED WITH BINDING CONDITIONS.

### Rulings
1. **Parameter Isolation:** Confirmed. Parameter values must never be logged under any log level. Because `SqlTextScrubber.TryScrub` strips string literals to `'?'` and numbers to `?`, and query compilers emit parameterized SQL (`@p0`, `@p1`), statements logged contain zero caller inputs or PII.
2. **Redaction Allowlist Precision:** Confirmed. `IsSensitiveKey` must not use overly broad exemptions. Exemption is strictly limited to exact matches for `StatementText`, `QueryHash`, and `TelemetryAttributes.DbQueryText`. Arbitrary keys containing `"sql"` or `"query"` (e.g. `SqlQuery`, `SqlText`, `RawQuery`) must continue to be redacted to `"[redacted]"`.
3. **Safety Ceiling Precedence:** Confirmed. `Telemetry:Redaction:AllowStatementText` is a hard safety ceiling. If set to `false`, `StatementText` must not be emitted.
4. **Length Bounding:** Confirmed. 4096 characters is an appropriate ceiling for structured statement attributes, preventing memory amplification while accommodating complex multi-table AST pushes.

### Binding Conditions
- **LOG426-SC-01:** All generated SQL emitted to `_logger` must be sanitized through `SqlTextScrubber.TryScrub`. If `TryScrub` fails or returns `false`, statement text must be omitted from the log record.
- **LOG426-SC-02:** Parameter values (`DbParameter.Value` or parameter collections) must never be added to log attributes or message strings.
- **LOG426-SC-03:** `LogRedactionProcessor` allowlist must be exact-match: `key.Equals("StatementText", StringComparison.OrdinalIgnoreCase) || key.Equals("QueryHash", StringComparison.OrdinalIgnoreCase) || key.Equals(TelemetryAttributes.DbQueryText, StringComparison.OrdinalIgnoreCase)`.
- **LOG426-SC-04:** `LogRedactionProcessor` string truncation is capped at 4096 for statement attributes and 1024 for all other attributes.
- **LOG426-SC-05:** If `Telemetry:Redaction:AllowStatementText == false`, query services must suppress statement logging even if `LogGeneratedSql == true`.
- **LOG426-SC-06:** All new security test cases must be marked with `[Trait("Category", "Security")]`.

### Mandatory Security Tests
- `LOG426_T01_StatementText_And_QueryHash_AreNotRedacted` (Verifies vetted keys pass unredacted).
- `LOG426_T02_GenericSqlKeywords_RemainRedacted` (Verifies unapproved keys like `Sql`, `SecuredSql`, `UserQuery` remain `"[redacted]"`).
- `LOG426_T03_StatementText_Truncation_Enforces4096Bound` (Verifies 4096-char ceiling for statement keys).
- `LOG426_T04_AllowStatementText_Ceiling_SuppressesLogging` (Verifies `AllowStatementText=false` blocks SQL logging).
- `LOG426_T05_SqlTextScrubber_SanitizesLiterals_BeforeLogging` (Verifies literal values are replaced with placeholders).

---

## Phase 4: Test-Driven Development (TDD) Summary

### Red-Green-Refactor Cycle
- **WP-1 (`LogRedactionProcessor` Exemptions & Truncation Bounds):**
  - Red tests: Added `LOG426_T01_StatementText_And_QueryHash_AreNotRedacted`, `LOG426_T02_GenericSqlKeywords_RemainRedacted`, and `LOG426_T03_StatementText_Truncation_Enforces4096Bound` in `tests/Autheris.Tests.Unit/Security/LoggingRedactionDep8Tests.cs`.
  - Failure: Initial test run failed as expected (`QueryHash` was redacted to `"[redacted]"` and statement string length was truncated to 1024).
  - Implementation: Updated `LogRedactionProcessor.cs` with `IsStatementKey(key)` exact matching and 4096-char ceiling for statement keys.
  - Green verification: 9/9 tests passed in `LoggingRedactionDep8Tests`.
- **WP-2 (Query Execution Logging Call-Sites & Scrubbing):**
  - Updated `AuditLoggingOptions.cs` (`LoggingOptions`) with `AllowStatementText` defaulting to `true`.
  - Updated `HierarchicalDataSourceOptionsResolver.cs` with redaction and logging ceiling evaluation.
  - Updated call-sites in `GovernedTreeQueryService.cs`, `GovernedSqlRewriter.cs`, and `SqlProcedureRowScopeResolver.cs` to sanitize with `SqlTextScrubber.TryScrub` and emit `{QueryHash}` and `{StatementText}` attributes under `LogLevel.Debug`.
  - Added unit test fixtures in `tests/Autheris.Tests.Unit/Sql/GovernedLoggingGeneratedSqlTests.cs`.
  - Green verification: Full test suite passed (16,264 unit tests, 0 failed, 0 skipped).

---

## Phase 5: Code Review & Quality Gate (`csharp-code-reviewer`)

### Quality Gate Inspection
- **Modern C# 13 Idioms:** Primary constructors, pattern matching, collection expressions, and null-coalescing guards correctly used.
- **Fail-Closed & Zero-Trust Verification:**
  - `AllowStatementText` acts as an absolute ceiling across telemetry and logging configs.
  - Literal scrubbing via `SqlTextScrubber.TryScrub` ensures literal values and caller parameters are replaced before log emission.
  - Exact allowlist prevents inadvertent leakage of unsanitized SQL fields.
- **Performance & Allocation Efficiency:** Log statements are guarded by `_logger.IsEnabled(LogLevel.Debug)` avoiding unnecessary string allocation in production environments.
- **Verification Result:**
  - `dotnet build Autheris.sln`: 0 Error(s), 0 Warning(s).
  - `dotnet test tests/Autheris.Tests.Unit`: 16,264 Passed, 0 Failed, 0 Skipped.

**Verdict:** PASS / APPROVED.



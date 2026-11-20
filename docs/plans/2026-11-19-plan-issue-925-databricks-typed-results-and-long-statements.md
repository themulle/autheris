# Plan: Databricks results come back only as strings, and statements are cancelled after at most 50 s (Issue #925)

- Issue: [#925](https://github.com/themulle/autheris/issues/925). Phase 2 plan, 2026-11-19. Code facts verified on `origin/main` after the v1.9.0 tag.
- Type: provider correctness and robustness (Statement Execution API). No DDL.
- Phase 3: **not required**. No change to authentication, authorization, row filters or masking. The Phase 5 reviewer checks D-925-3 (no orphaned statements). The owner may still request Phase 3.
- Related: the #916 plan ([`2026-11-19-plan-issue-916-databricks-metric-views.md`](2026-11-19-plan-issue-916-databricks-metric-views.md)). `MEASURE(...)` results are numeric aggregates over large fact tables. Typed columns (WP-925-2) are needed for sorting and aggregating them in WebSQL and OData, and long-running aggregations on auto-stopped warehouses need WP-925-1. Land this row before WP-MV-6 of #916.

## 1. Problem and evidence

1. Strings only. `src/Autheris.Infrastructure/Databricks/DatabricksStatementExecutionClient.cs:493-517` reads `manifest.schema.columns[].type_name` into `DatabricksColumn(Name, TypeName)` (`DatabricksTypes.cs:19`). However, `src/Autheris.Infrastructure/Databricks/DatabricksDbCommand.cs:84` adds every column as `typeof(string)`, and lines 87-96 copy the raw strings. Every reader therefore reports `string`, which differs from the SQL Server and PostgreSQL providers.
2. The 50 s cap.
   - `BuildBody` (lines 313-350) clamps `wait_timeout` to 5-50 s (`MinWait`/`MaxWait`, lines 42-43; option `WaitTimeoutSeconds = 30`, `DatabaseOptions.cs:349`) and sends `on_wait_timeout = CANCEL` (line 347).
   - Databricks cancels the statement when the wait ends. The existing polling loop (lines 84-95, bounded by `CommandTimeoutSeconds`, `[Range(1, 300)]` at `DatabaseOptions.cs:45`) never gets a statement that is still running after 50 s.
   - The issue also notes that a warehouse in auto-stop takes about 10 s to start, and that time counts against the same wait.
3. The cancel path already exists: on the command deadline, `TryCancelAsync` cancels a known `statement_id` (lines 104-117).

## 2. Options

Long statements:

| Option | Description | Verdict |
|---|---|---|
| L-A | `on_wait_timeout = CONTINUE`, keep polling until the `CommandTimeoutSeconds` deadline, cancel on deadline | **Recommended.** Uses the existing loop and the existing cancel path. |
| L-B | Keep `CANCEL` and only document the 50 s limit | Rejected. `CommandTimeoutSeconds` up to 300 is then misleading. |

Types:

| Option | Description | Verdict |
|---|---|---|
| T-A | Map `type_name` (plus `type_precision`/`type_scale`) to CLR types in `DatabricksDbCommand`, parse with the invariant culture, throw a protocol error on a parse failure | **Recommended.** |
| T-B | Switch to `format = ARROW_STREAM` | Rejected now. Larger change and a new dependency; the byte limit and the inline disposition would change. |

## 3. Decisions
1. D-925-1: `BuildBody` sends `on_wait_timeout = CONTINUE`.
2. D-925-2: wait time = `min(configured WaitTimeout, CommandTimeoutSeconds − 2 s, 50 s)`. If that is below 5 s, send `wait_timeout = "0s"` (fully asynchronous; the API accepts 0 or 5-50 s). The first POST then always returns before the local deadline, so a `statement_id` is known before any cancellation.
3. D-925-3: no orphaned statements. Every exit path after a `statement_id` is known and before a terminal state calls `TryCancelAsync`: deadline, caller cancellation, protocol error and result-too-large. Extend the `catch` at lines 104-117 with a `catch (Exception) when (statementId is not null && !terminal)` that cancels and then rethrows.
4. D-925-4: type map in a new `internal static class DatabricksTypeMap` (`src/Autheris.Infrastructure/Databricks/DatabricksTypeMap.cs`):

   | Databricks `type_name` | CLR type |
   |---|---|
   | `BOOLEAN` | `bool` |
   | `BYTE` | `sbyte` |
   | `SHORT` | `short` |
   | `INT` | `int` |
   | `LONG` | `long` |
   | `FLOAT` | `float` |
   | `DOUBLE` | `double` |
   | `DECIMAL` with precision ≤ 28 | `decimal` |
   | `DECIMAL` with precision > 28 | `string` (whole column, decided from the manifest) |
   | `DATE` | `DateTime` (date part) |
   | `TIMESTAMP` | `DateTimeOffset` (UTC) |
   | `TIMESTAMP_NTZ` | `DateTime` (`Unspecified`) |
   | `STRING`, `CHAR`, `VARCHAR`, `BINARY`, `INTERVAL`, `ARRAY`, `MAP`, `STRUCT`, `VARIANT`, anything else | `string` |

   `FLOAT` and `DOUBLE` also accept `NaN`, `Infinity` and `-Infinity`. A value that does not parse as its column type throws `DatabricksProviderException(ProtocolViolation)`. It is never returned as a silent string.
5. D-925-5: `DatabricksColumn` gains `int? Precision, int? Scale`, read from `type_precision` and `type_scale` in `ReadColumns`. `ExecuteNonQuery` (`DatabricksDbCommand.cs:50-52`) and `ExecuteScalar` (line 63) keep their current behaviour; the scalar becomes typed.

## 4. Work packages (one PR)

### WP-925-1 (S): `CONTINUE`, deadline-bounded wait, cancel on every exit
- File: `src/Autheris.Infrastructure/Databricks/DatabricksStatementExecutionClient.cs` (`BuildBody` lines 313-350, `ExecuteAsync` lines 64-118).
- Unit tests in `tests/Autheris.Tests.Unit/Databricks/DatabricksStatementExecutionClientTests.cs` (filter `DatabricksStatementExecutionClientTests`):
  - `Body_UsesOnWaitTimeoutContinue`
  - `Body_WaitTimeout_BoundedByCommandTimeout`
  - `Body_ShortCommandTimeout_SendsZeroWait`
  - `RunningAfterFirstWait_IsPolledUntilSucceeded`
  - `Deadline_CancelsStatement_AndThrowsTimeout`
  - `ProtocolErrorWhileRunning_CancelsStatement`
- Acceptance: a statement that runs longer than 50 s but less than `CommandTimeoutSeconds` succeeds; at the deadline, exactly one cancel request is sent.

### WP-925-2 (M): typed results
- Files: new `src/Autheris.Infrastructure/Databricks/DatabricksTypeMap.cs`; `DatabricksTypes.cs:19` (`DatabricksColumn`); `DatabricksStatementExecutionClient.cs:493-517` (`ReadColumns`); `DatabricksDbCommand.cs:71-99` (column types and per-cell conversion).
- Unit tests:
  - New `tests/Autheris.Tests.Unit/Databricks/DatabricksTypeMapTests.cs`. Theory `Maps(typeName, precision, raw, expectedClr, expectedValue)` covers every row of D-925-4. Also `Decimal38_StaysString`, `UnparsableInt_ThrowsProtocol`, `Null_IsDbNull_ForEveryType` and `Timestamp_IsUtcOffset`.
  - `DatabricksStatementExecutionClientTests.ReadColumns_ReadsPrecisionAndScale`.
  - `DatabricksFacadeTests`: `Reader_ReportsTypedFieldTypes`.
- Acceptance: `GetFieldType` and the values match the table, and no culture-dependent parsing is used.

### Order
WP-925-1 and WP-925-2 are independent; ship them in one PR, with WP-925-1 first.

## 5. Integration test impact
- `tests/Autheris.Tests.Integration/Databricks/DatabricksStatementExecutionContractTests.cs`: the mock server must accept `CONTINUE` and return `PENDING`/`RUNNING` and then `SUCCEEDED`. Add a long-running case. Check its manifests for `type_precision`.
- `tests/Autheris.Tests.Integration/DatabricksTransportParityTests.cs`: parity assertions that compare Databricks values as strings with other providers must compare typed values. This is the main adaptation point.
- `tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs` and `AstCompilerDatabricksSparkDmlExecutionTests.cs`: check value assertions for string expectations.
- `tests/Autheris.Tests.SqlCompilerGate/Databricks/DatabricksLiveGateTests.cs` (live, nightly): result comparison may change from strings to typed values; adapt `DatabricksGateSupport.cs`.
- Consumers that treat Databricks cells as strings: `git grep -n "Databricks" src | grep -i "GetString\|ToString()"` in the WebSQL, OData and Trino result writers. Hydrogen inspects them and adds a test for each one changed.

## 6. Owner questions
- Q-925-1: should `DECIMAL` with precision > 28 stay `string`, or become `SqlDecimal` or `double`? **Default taken: `string` (lossless).**
- Q-925-2: should `CommandTimeoutSeconds` for Databricks allow more than 300 s (today `[Range(1, 300)]` for all sources)? **Default taken: no change.**
- Q-925-3: should `DATE` map to `DateOnly` instead of `DateTime`? **Default taken: `DateTime`, matching SqlClient and Npgsql defaults.**

# Implementation Plan: REQ-SQL-130 - Governed Time Zone Conversion `convert_timezone` in WebSQL

**Document ID:** `PLAN-SQL-130` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-SQL-130](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md) section 4 (V-130-1..9, FR-130-1..6, AC-130-1..8; owner decisions Q-130-1 and Q-130-2 in section 4.6)
**Branch:** to be created from the `main` that contains the previous delivery item (delivery step I-6)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md); requirement [REQ-SQL-130](2026-10-16-req-issues-128-129-130-websql-parameters-user-attributes-timezones.md); delivery tracker [PLAN-DELIVERY-W23](2026-10-16-plan-waves-2-3-delivery.md) (planning step P-3, implementation step I-6)
**Status:** PROPOSED, Phase 3 security review done (APPROVED WITH CONDITIONS, revision 1 plus binding conditions BC-130-1..14 in the review section); next: Phase 2 owner folds the conditions into revision 2 (or the Phase 4 developer applies them verbatim), then Phase 4 after gate G-PLAN

**Binding project rules applied:** values are bound, never spliced; fail closed with one stable code per cause and fixed messages; no zone text, timestamp or row value in logs, audit, spans or exception text; no schema change (no DDL, no migration); least privilege on sources (SELECT only, no extension installation, no session `SET TIME ZONE`); the SqlGate/AST security suites and the G8 allocation gate stay green; `AstSecurityVisitor.cs` is **not** modified; the infix `AT TIME ZONE` operator stays rejected; Phase-4 TDD developer is Sonnet, one developer, sequential (gate G-ONE).

**Fixed owner decisions (not re-opened here):** **Q-130-1** argument order `convert_timezone(from_zone, to_zone, ts)` (final, cannot change after the first release). **Q-130-2** the result is the **local wall-clock plain timestamp without offset in every dialect**; an offset-carrying variant may follow later as a separate function. Q-130-3..5 default to the PRD recommendation (section 10).

---

## 1. Verification of the PRD Against the Code (`1a45c7bf`, waves 1 merged)

| PRD item | Result |
|---|---|
| No time zone function in the allowlists | **Confirmed.** `src/TrinoSqlEngine/SqlFunctionAllowlists.cs:19-35` (`CommonFunctions`: only `extract`, `cast`, `current_date`, `current_timestamp` in the date group), dialect lists `:37-79` (no `at_timezone`, `with_timezone`, `convert_timezone`, `switchoffset`, `todatetimeoffset`). `DuckDb` is `Common + PostgreSql` (`:186`). |
| How a function is admitted | **Verified, three gates in sequence.** (1) WebSQL pre-check by name on the analyzer's `metadata.FunctionCalls` against `SqlFunctionAllowlists.Build(dialect, additional)` (`src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs:601-612`, `WebSqlPolicyException`, 403). (2) Builder: `SqlAstBuilder.EnsureFunctionAllowed` (`src/TrinoSqlEngine/Ast/Builder/SqlAstBuilder.cs:1155-1161`) uses `SqlFunctionPolicy.IsFunctionAllowed` (`SqlFunctionPolicy.cs:135`, denylist always wins) with the effective set `GovernedSqlCompiler.EffectiveFunctions` (`GovernedSqlCompiler.cs:223-238`) = the dialect function map `DialectFunctionMap.ForDialect` (`Ast/Capabilities/DialectCapabilities.cs:69-88`) = `SqlFunctionAllowlists.CompilerNames(dialect)` (`SqlFunctionAllowlists.cs:119-127`). (3) The dialect generator: SQL Server, PostgreSQL, Oracle and Databricks override `GenerateFunctionCall` for a few renames (`SqlServerDialectGenerator.cs:259-296`, `PostgreSqlDialectGenerator.cs:32-45`, `OracleDialectGenerator.cs:84-123`, `DatabricksDialectGenerator.cs:193-212`); **everything else passes through unchanged** (`SqlDialectGeneratorBase.Functions.cs:GenerateFunctionCall`). A name that is allowlisted but has no generator rule would therefore reach the database verbatim. The plan closes this in the base class (A-6). |
| Name must be in `CommonFunctions` to reach all dialects | **Finding.** Gate (1) uses `Build(dialect)` (default list + additional), gate (2) uses `CompilerNames`. Adding the name only to `TrinoSpecialForms`/`RewrittenFunctions` would pass gate (2) but fail gate (1) with a generic 403. To give SQLite, Oracle and Databricks the stable "unsupported dialect" code (AC-130-6) the name goes into `CommonFunctions` and the **validator** (A-3) decides per dialect. Side effect: `RowPolicyFactory.cs:102` and `PolicyParserSetup.cs:49` build row-policy predicate allowlists with `Build(...)` and therefore also admit the name (flagged for Phase 3, section 11 item 9). Virtual filters use their own closed list (`SqlFilterCompiler.cs:152-165`) and are not widened. |
| Where the dialect map lives | **Confirmed.** `DialectCapabilityTable.cs:37-224` (SqlServer `:37`, DuckDb `:67`, PostgreSql `:98`, Oracle `:131`, Databricks `:163`, Sqlite `:196`); `DialectCapabilities` is a record with optional trailing members (`DialectCapabilities.cs`), so a new optional member is non-breaking. |
| Pipeline order for a user statement | **Confirmed** (`GovernedSqlCompiler.cs:131-190`): parse, `SqlAstBuilder.BuildStatement`, `AstValidationVisitor`, `AstSimplificationVisitor` (user tree), `LiteralColumnTypeAnnotator`, `AstSecurityVisitor` (injection), coverage verification, `DialectCapabilityValidator`, emit. The new validator runs **after `AstValidationVisitor` and before simplification** (A-3). |
| Template cache and `CompileRequest` | **Confirmed.** `CompileCacheKey.Material` serializes every public property of `CompileRequest` (`Ast/Emit/CompiledSqlTemplate.cs:243-345`), so a new `CompileRequest` property is part of the key by construction. A cache hit skips validation (`GovernedSqlCompiler.cs:128-133`): everything the validator decides must therefore be a function of the raw SQL and the request (it is), and anything that depends on a value (a client parameter) must be re-checked at bind time (A-4). |
| Literals are always bound on the governed path | **Confirmed.** `SqlDialectGeneratorBase.FormatLiteral/FormatBoundLiteral` (`SqlDialectGeneratorBase.Functions.cs`), `BindLiterals` is true for PostgreSQL (`PostgreSqlDialectGenerator.cs:47`) and DuckDB (`AnalyticalDialectGenerators.cs:88`). `SqlEmitterContext.BindValue` de-duplicates equal values (`SqlEmitterContext.cs:102-111`), so the same zone literal in `SELECT` and `GROUP BY` yields the **same marker** (relevant for PostgreSQL and SQL Server `GROUP BY` matching, test T-30). |
| Client parameters | **Confirmed.** `__param_<name>` becomes `ParameterReference(name, IsSynthetic: true)` (PLAN-SQL-128 section 1); `SqlEmitterContext.BindClient` binds one slot per name with value `null`, the binder resolves the value per request and infers the type (`SqlEmitterContext.cs:158-172`, `ICompiledSqlBinder.cs:196-207`, `InferType` `:241-257`). The dedup key `(Client, name)` means a name used both as a zone and as an ordinary value would share one slot; a zone slot needs its own role and dedup kind (A-4). |
| Masks and functions | **Confirmed mechanism, PRD wording needs a clarification.** Masks are applied by the secured derived table before the user expression sees a column (`AstSecurityVisitor`, `SecurityCoverageVerifier`); only aggregate arguments and grouping expressions over a masked column are refused (`AstSecurityVisitor.MaskGuards.cs:395-436`, `AGGREGATION_ON_MASKED_COLUMN`). A scalar function over a masked column therefore receives the **masked projection**, never the raw value. V-130-6 "Clear columns only" is not enforced by any existing mechanism for scalar functions and is **not** introduced here; the plan proves the safe property instead (test T-36). Flagged as F-2. |
| OLAP cross-source allowlist (PR #140) | **Verified, decision: `convert_timezone` does NOT join `OlapSqlPolicy.AllowedFunctions`.** `OlapSqlPolicy.AllowedFunctions` (`src/Autheris.Application/Olap/OlapSqlPolicy.cs:15-34`, enforced by `EnforceCrossSource` `:75-114`) applies to **client-written DuckDB SQL** on the OLAP endpoint (`DuckDbOlapEngine.cs:159-172`, only `if (!trustedGeneratedSql)`). That text never passes the governed compiler, `convert_timezone` is not a DuckDB function, and the zone closed-set check (V-130-1) cannot run on it. Gateway-generated federated SQL goes through `ExecuteGeneratedAsync` with `trustedGeneratedSql: true` (`DuckDbOlapEngine.cs:80-89`, `FederatedDuckDbExecutionService.cs:340`) and is not inspected by that policy. A pinning test asserts that neither `convert_timezone` nor `timezone` is in the OLAP allowlist (T-29). |
| Federated path function checks | **Confirmed, two consequences.** (a) `CrossSourcePlanner.cs:107-118` checks the user's raw function names against `SqlFunctionAllowlists.DuckDb`; adding the name to `CommonFunctions` satisfies it. (b) The planner does **not** use the governed compiler for the DuckDB statement: it builds the AST with a default `new SqlAstBuilder()` (`:272-276`) and calls `generator.Generate(rewrittenAst, ParameterSource.Empty)` (`:278-281`). The compiler's validator stages therefore do not run there; the planner must call the same validator explicitly (A-8). |
| DuckDB ICU in the gateway image | **Static evidence only; runtime proof is the first red test.** The gateway uses `DuckDB.NET.Data.Full` 1.5.6 (`src/Autheris.Application/Autheris.Application.csproj:13`), whose native `runtimes/linux-x64/native/libduckdb.so` (70.5 MB) contains the ICU C++ symbols (`icu_66::...`) and the extension strings `icu_calendar_names` and `pg_timezone_names` (checked with `strings` on the restored NuGet package; the Docker images ship the same publish output, `Dockerfile:5`, `Dockerfile.production:6`, no separate DuckDB install, no `INSTALL`/`LOAD`). The sandbox sets `autoinstall_known_extensions=false`, `autoload_known_extensions=false`, `enable_external_access=false`, `lock_configuration=true` (`DuckDbOlapEngine.cs:283-287`) and blocks `INSTALL`/`LOAD` in user text (`:548`); whether a **statically linked** ICU stays callable under exactly these settings must be proven by test T-1 before any DuckDB code is written. If T-1 fails the DuckDB mapping is not delivered (Q-130-4 default). |
| `TimeZoneInfo.TryConvertIanaIdToWindowsId` for SQL Server | **Available in .NET, but depends on ICU and is node-dependent.** All three Dockerfiles set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0` (`Dockerfile:18`, `Dockerfile.staging:13`, `Dockerfile.production:13`) on `mcr.microsoft.com/dotnet/aspnet:10.0.13` (Debian based; the image's ICU and `tzdata` packages are expected but **not verified here**, developer checks `docker run --rm <image> ls /usr/share/zoneinfo` in WP-3). No `InvariantGlobalization` setting exists in any `*.csproj` or `Directory.Build.props` (grep). The conversion is still **not** used as the mapping source (A-2): developer machines, CI runners and other deployments may run invariant mode, and validation must be identical on every node (Q-130-3). |
| Accepted difference `AT TIME ZONE` | **Confirmed.** `tests/Autheris.Tests.SqlCompilerGate/Corpus/accepted-differences.json:93` lists `AT TIME ZONE` (entry for issue #91, approved). The operator stays rejected; the entry is **not edited**. The function is new surface, not a loosened difference. The corpus (`Corpus/dql.sql`, rows `-- @id <hash> class=select expected=accept|reject source=...`) gets new rows (section 7, WP-6). |
| Analysis function list (FR-130-5) | **Not found.** `QueryAnalysisService.Sql.cs` contains no function list and no API route returns one (grep over `src/Autheris.Api` and `src/Autheris.Application/Governance`). FR-130-5 reduces to documentation (Phase 6: `docs/features/f-data-02-governed-websql.md`) and the allowlist change; no new endpoint is planned. |

Two PRD statements are refined: (1) V-130-1 "bound parameter checked at bind time" needs a **zone slot role** because the dedup key and the SQL Server name mapping make a plain `ClientNamed` slot insufficient (A-4); (2) V-130-6 is replaced by the proven property "the function sees the masked projection" (F-2). No PRD veto is reverted.

---

## 2. Architecture Decisions

### A-1 One function, one AST shape, no new node, no operator
`convert_timezone(from_zone, to_zone, ts)` stays an ordinary `FunctionCallExpression` named `convert_timezone` (case-insensitive, unqualified). No new AST node type, no grammar change (the Trino grammar already parses the call). The infix `AT TIME ZONE` keeps failing in the builder exactly as today (`AstBuildException`, fixture `80d5942dea95`); the plan adds a test that it still does (T-15).

### A-2 Zone registry: embedded data, no runtime dependency (Q-130-3)
`TimeZoneRegistry` (engine, `src/TrinoSqlEngine/TimeZones/`) is immutable and built once from an **embedded resource** `iana-windows-zones.txt` (lines `iana;windows`). Source: CLDR `windowsZones.xml` (primary territory `001` plus all territory entries, inverted to IANA -> Windows), the CLDR/tzdata version and the SHA-256 of the resource stated in a header comment and in a constant. The **closed set is the IANA ids that have a Windows mapping** (intersection), so one list validates identically for SQL Server, PostgreSQL and DuckDB and on every node; ids without a Windows mapping are simply not offered (documented). Rules for every entry (checked at load and by the self-test): IANA id matches `^[A-Za-z0-9_+\-/]{1,64}$`, Windows id matches `^[A-Za-z0-9 .,()+\-]{1,64}$` (covers `W. Europe Standard Time`, `Central Standard Time (Mexico)`; no quote, no comment marker, no control character, no non-ASCII), ordinal case-sensitive lookup (`FrozenDictionary<string,string>` with `StringComparer.Ordinal`), no duplicates. API: `Contains`, `TryResolve(TargetSqlDialect, ianaId, out dialectValue)` (SQL Server returns the Windows id, PostgreSQL and DuckDB return the registry's own IANA string instance, any other dialect returns false). The value that reaches the database is always **registry-owned text** (V-130-2), never the caller's string instance. A CLDR update is a code change with the resource and the hash constant (never configuration). Licence: the Unicode licence terms for CLDR data go into the repository's third-party notice file (Phase 6 check).

### A-3 Validation in the engine: one internal visitor, runs for every dialect
`TimeZoneCallValidator` (`src/TrinoSqlEngine/Ast/Visitors/`, internal, built on the scope tracker extracted from `LiteralColumnTypeAnnotator`, A-9) walks the **user tree** after `AstValidationVisitor` and before simplification (so no constant folding can turn a non-literal into a literal) and checks every `convert_timezone` call in this fixed order, all with fixed messages and no echo of the input:
1. **Dialect and availability:** `!caps.SupportsTimeZoneConversion` -> `SQL_FUNCTION_UNSUPPORTED_DIALECT` (501; SQLite, Oracle, Databricks, any dialect without a rule); `!request.TimeZoneConversionAvailable` -> `SQL_TIMEZONE_UNAVAILABLE` (503; self-test failed or not yet evaluated, fail closed).
2. **Shape:** exactly three arguments, no `DISTINCT`, `FILTER`, `OVER`, `WITHIN GROUP` -> `SQL_TIMEZONE_ARGUMENT` (422).
3. **Zone arguments (V-130-1):** each of `from_zone`, `to_zone` is a `LiteralExpression` of type String (non-null, at most 64 characters, `TimeZoneRegistry.Contains`, else `SQL_TIMEZONE_INVALID`, 400) **or** a synthetic `ParameterReference` (named client parameter; value checked at bind time, A-4). Everything else (column, expression, concatenation, function, `CAST`, subquery, positional `?`, NULL literal, number) -> `SQL_TIMEZONE_ARGUMENT`.
4. **Timestamp argument (V-130-4):** must be one of: a resolvable column reference whose **catalog type is a plain timestamp for the compile dialect** (A-9 table); `CAST(x AS timestamp)` or `CAST(x AS timestamp(p))` without `with time zone` (and `TRY_CAST` likewise); a `TIMESTAMP '...'` typed literal; a nested `convert_timezone(...)` (its result is plain by Q-130-2). Anything else (offset-carrying column type, unresolved column of a CTE or derived table, a bare client parameter, `current_timestamp`, arithmetic, a `date` or `time` column) -> `SQL_TIMEZONE_ARGUMENT`. Unknown catalog type is rejected, never guessed.
No check depends on row data and no check can raise only for specific data (V-130-9): the rejections are static, the bind-time rejection depends only on the supplied parameter value.

### A-4 Bound-parameter zones: a zone slot role, mapped and checked by the binder
A named client parameter used as a zone argument is emitted through a new `SqlEmitterContext.BindZoneClient(ParameterReference)` with a new dedup kind `ClientZone` and `BoundParameter.ZoneArgument = true` (carried by `CompiledSqlTemplate.SqlSlot` through `ToSlots`/`Rebind`). A name used both as zone and as an ordinary value therefore yields **two slots** (the value slot is untouched). In `DbCommandCompiledSqlBinder.BindCore` (`ICompiledSqlBinder.cs:173-217`) a slot with `ZoneArgument` requires a non-null `string`, runs `TimeZoneRegistry.TryResolve(Dialect, value, out mapped)` and binds `mapped` (SQL Server: the Windows id; others: the registry IANA string); otherwise it throws `SqlTimeZoneException(SQL_TIMEZONE_INVALID)` **before** the command exists, so nothing reaches the database (AC-130-3, AC-130-4). The check happens in the binder because the template is cached value-free and because the binder is the one place all dialects, the checked-DML path (`BindForCheckedExecution`, `ICompiledSqlBinder.cs:155`) and, after #131, the federated DuckDB path share (INV-13: one rule set). A `null` zone value is rejected (never `AT TIME ZONE NULL`).

### A-5 Literal zones: validated at compile time, bound as the mapped value
A zone literal is validated by A-3, mapped with `TimeZoneRegistry.TryResolve(TargetDialect, ...)` and emitted as `context.BindValue(mapped, SqlParameterType.String, ParameterOrigin.QueryLiteral)`. **Decision: bind, do not inline** (even though the value is registry-owned): it keeps INV-4 literally true ("a literal value reaches the database only as a bound parameter"), avoids a new `ConstantFragment` that `EmittedSqlInvariantChecker` would skip (`EmittedSqlInvariantChecker.cs:76`, ranges are not inspected), and de-duplicates equal zones to one marker. Rejected alternative: inline `'Europe/Berlin'` as a constant fragment (planner sees a constant, no bind budget) - kept as the documented fallback if T-30 shows a dialect that cannot match `GROUP BY` expressions with parameter markers (R-2).

### A-6 Emission: one interception point in the base generator, three overrides, everything else throws
`SqlDialectGeneratorBase.GenerateFunctionCall` gets a first branch: a call named `convert_timezone` goes to `FormatConvertTimeZone(ref builder, fn, context)` (new partial file `SqlDialectGeneratorBase.TimeZone.cs`). The base implementation **throws** `SqlTimeZoneException(SQL_FUNCTION_UNSUPPORTED_DIALECT)` (defense in depth: SQLite, Oracle, Databricks, ANSI, Snowflake generators can never pass the name through), and in an unbound context (legacy string path, `!context.IsBound`) every override throws `NoBoundForm`. The zone arguments are emitted by one shared helper that accepts only literal (A-5) or synthetic parameter (A-4) and throws otherwise (a second, independent check next to A-3). Results (the user timestamp expression is always parenthesized):

| Dialect | Emitted form | Result type |
|---|---|---|
| SQL Server | `CAST(((<ts>) AT TIME ZONE <z_from>) AT TIME ZONE <z_to> AS datetime2)` | `datetime2`: local wall clock of `to_zone` (CAST of a `datetimeoffset` keeps the local date and time, drops the offset) |
| PostgreSQL | `(((<ts>) AT TIME ZONE <z_from>) AT TIME ZONE <z_to>)` | `timestamp without time zone`: local wall clock of `to_zone`; independent of the session `TimeZone` because the inner value is only an instant |
| DuckDB (only if T-1 passes) | `TIMEZONE(<z_to>, TIMEZONE(<z_from>, <ts>))` | `TIMESTAMP`: `timezone(zone, naive)` interprets the naive value in `zone` and returns `TIMESTAMPTZ`, `timezone(zone, tstz)` returns the local `TIMESTAMP` |
| SQLite, Oracle, Databricks | not emitted | rejected by A-3 step 1 and by the base guard |

`datetime2` contains a digit, so the type text goes through `AppendStructural` (`SqlDialectGeneratorBase.cs:124-129`); the surrounding keywords are plain text. Result precision follows the input; no rounding is introduced.

### A-7 Self-test and availability (FR-130-3, AC-130-8)
- **Engine, `TimeZoneRegistry.SelfTest()`** (pure, no I/O): resource loads and its SHA-256 equals the constant; entry count at least a floor constant; every entry passes the A-2 regexes; anchors exist with exact mappings (`UTC`, `Etc/UTC`, `Europe/Berlin` -> `W. Europe Standard Time`, `America/New_York` -> `Eastern Standard Time`, `Asia/Kolkata` -> `India Standard Time`, `Australia/Lord_Howe` -> `Lord Howe Standard Time`, `America/St_Johns` -> `Newfoundland Standard Time`). Failure returns a check id (constant text) only.
- **Optional cross-check against .NET (warning only):** when `TimeZoneInfo.FindSystemTimeZoneById` works (ICU and tzdata present), the offsets of Europe/Berlin and America/New_York at the four 2026 transition instants must match the expectations in the test list; a mismatch logs one Warning (check id only) and **does not** disable the function (the databases do not use the .NET data). Invariant mode or missing tzdata: skipped silently at Debug.
- **Application, `TimeZoneConversionState`** (`src/Autheris.Application/Sql/TimeZones/`, singleton `ITimeZoneConversionState.IsAvailable(TargetSqlDialect)`, `Lazy` evaluation, also warmed by a hosted service at startup so a failure is logged once): SQL Server and PostgreSQL available iff the registry self-test passed; DuckDB additionally requires a **live ICU probe** on an in-memory DuckDB connection with the same sandbox statements as `DuckDbOlapEngine.BuildSetupSql` (`SELECT timezone('Europe/Berlin', timezone('UTC', TIMESTAMP '2026-03-29 01:00:00'))` must equal `2026-03-29 03:00:00`; constants only, no user text). Any exception or mismatch -> DuckDB unavailable, one Warning without driver text. Before the state is evaluated the answer is `false` (fail closed).
- `CompileRequestFactory.Create(..., bool timeZoneConversionAvailable = false)` (optional last parameter, fail-closed default; after #128 it follows `clientLists`) sets `CompileRequest.TimeZoneConversionAvailable`. Only `GovernedSqlRewriter.RewriteCoreAsync` passes the dialect's value; every other `CompileRequest` construction (tests, engine-internal callers) keeps `false`.

### A-8 Federated queries (AC-130-5)
`CrossSourcePlanner` (after its AST build at `:272-276`, before `FederationAstRewriter`) calls the public facade `TimeZoneConversion.ValidateFederated(SqlStatement, bool duckDbAvailable)` over the same visitor with two stricter options: **bare column references are not accepted** as `ts` (catalog types in a federated statement belong to source dialects, the classifier is dialect specific; the user writes `CAST(col AS timestamp)`), and **parameter zones are not accepted** until #131 lands (the federated engine loop would bind a `ClientNamed` slot as `NULL`, PLAN-FED-131 section 1; #131 flips this single option in step I-26 and adds the case to its tests). Literal zones work: the DuckDB generator binds the mapped value as a `QueryLiteral` slot with its value (the engine loop at `DuckDbOlapEngine.cs:190-195` binds `BoundParameter.Value`). Availability comes from `ITimeZoneConversionState.IsAvailable(DuckDb)`. Same-source subtrees (#123, not implemented): when `WP-FED-13` pushes a subtree to the source it must compile it through the governed compiler (validator runs, source dialect emission); nothing is needed here, a pointer sentence goes into the #123 plan notes by the lead.

### A-9 Timestamp type classification and the extracted scope tracker
`LiteralColumnTypeAnnotator` (`Ast/Security/LiteralColumnTypeAnnotator.cs`) already resolves a column to a catalog entry through the alias and scope rules of a query block (CTE and derived-table columns stay unresolved). Its scope code is extracted unchanged into an internal `ColumnScopeTracker` (pure refactor, the annotator tests are the safety net) and reused by the validator. `PlainTimestampClassifier.IsPlain(TargetSqlDialect, catalogType)` (new, internal, lower-cased, `(p)` arguments stripped) is **dialect specific**:

| Dialect | Plain (accepted) | Rejected (examples) |
|---|---|---|
| SQL Server | `datetime`, `datetime2`, `smalldatetime` | `datetimeoffset`, `date`, `time`, and **`timestamp` (a `rowversion` binary type in T-SQL, never a time)** |
| PostgreSQL | `timestamp`, `timestamp without time zone` | `timestamptz`, `timestamp with time zone`, `date`, `time` |
| DuckDB | `timestamp`, `timestamp_s`, `timestamp_ms`, `timestamp_ns`, `timestamp without time zone` | `timestamptz`, `timestamp with time zone`, `timestamp_tz`, `date` |

Anything not listed, including a null or unparsable type, is rejected (`SQL_TIMEZONE_ARGUMENT`).

### A-10 Error contract
Four new stable codes in `GovernedSqlErrorCodes` with fixed, value-free messages (`FixedMessage`): `SQL_TIMEZONE_INVALID` ("The time zone is not supported."), `SQL_TIMEZONE_ARGUMENT` ("The time zone conversion arguments are not valid."), `SQL_FUNCTION_UNSUPPORTED_DIALECT` ("The function is not supported for this data source dialect."), `SQL_TIMEZONE_UNAVAILABLE` ("Time zone conversion is temporarily unavailable."). One exception type `SqlTimeZoneException(code)` (derives from `Exception`, **not** `SecurityException`, so it cannot be absorbed by the generic 403 row). `SqlCompilerErrorMapper` (`Sql/Errors/SqlCompilerErrorMapper.cs:100-135`) gets one row **before** the `SecurityException` row (`:123`): `SQL_TIMEZONE_INVALID` -> 400, `SQL_TIMEZONE_ARGUMENT` -> 422, `SQL_FUNCTION_UNSUPPORTED_DIALECT` -> 501, `SQL_TIMEZONE_UNAVAILABLE` -> 503. Messages never contain the zone, the timestamp, a counter or the dialect-specific zone id. The existing surfaces (WebSQL, Trino route, Flight SQL, MCP, statements, async jobs) map through the same mapper, so no per-surface work exists.

### A-11 No configuration, no persisted state
No new option: the function is available iff the self-test passes (A-7). A kill switch is deliberately not added (it would be a second fail-open/fail-closed matrix); disabling is a code change. No schema, no audit field (a call count would add nothing reviewable; zone values stay out of audit). The compile audit already lists `bind_count`.

### A-12 Documentation and DST semantics (written by Phase 6, specified here)
Documented behaviour: results follow the **database's** zone rules (SQL Server: operating system or tzdata of the host, PostgreSQL: its tzdata, DuckDB: ICU); engines can differ for historic dates, after rule updates and **for ambiguous and non-existent local times** (the repeated hour at the end of DST, the skipped hour at its start), where each engine applies its own, documented rule. The gateway guarantees identical results across engines **only** for unambiguous inputs (the tested transition days with a UTC source). Converting **from** a local zone on a transition night is therefore best-effort; users who need exactness convert from UTC.

---

## 3. Contracts (fixed up front, first commit "contracts")

### C-1 Engine (`src/TrinoSqlEngine`)
```csharp
// TimeZones/TimeZoneRegistry.cs (new, public static; data = embedded resource iana-windows-zones.txt)
public static class TimeZoneRegistry
{
    public static int Count { get; }
    public static bool Contains(string ianaId);                                              // ordinal, length <= 64
    public static bool TryResolve(TargetSqlDialect dialect, string ianaId, out string dialectValue);   // SqlServer: Windows id; PostgreSql, DuckDb: IANA id; else false
    public static TimeZoneSelfTestResult SelfTest();
}
public sealed record TimeZoneSelfTestResult(bool Passed, string? FailedCheck, bool CrossCheckSkipped);   // FailedCheck = constant id, never data

// TimeZones/SqlTimeZoneException.cs (new)
public sealed class SqlTimeZoneException(string code) : Exception(GovernedSqlErrorCodes.FixedMessage(code)) { public string Code { get; } = code; }

// TimeZones/TimeZoneConversion.cs (new, public facade for the federated planner)
public static class TimeZoneConversion
{
    public static void ValidateFederated(SqlStatement statement, bool duckDbAvailable);       // throws SqlTimeZoneException
}

// CompileRequest.cs
public bool TimeZoneConversionAvailable { get; init; }                                       // default false = fail closed; part of the cache key by reflection

// Ast/Capabilities/DialectCapabilities.cs (optional trailing member) and DialectCapabilityTable.cs
bool SupportsTimeZoneConversion = false;                                                     // true: SqlServer, PostgreSql, DuckDb

// Ast/Emit/CompiledSql.cs (BoundParameter) and CompiledSqlTemplate.cs (SqlSlot): append  bool ZoneArgument = false
// Ast/Buffer/SqlEmitterContext.cs:  string BindZoneClient(ParameterReference parameter);  DedupKind.ClientZone
// Ast/Emit/GovernedSqlException.cs: four codes + FixedMessage rows (A-10)
```
Names that are internal: `TimeZoneCallValidator`, `ColumnScopeTracker`, `PlainTimestampClassifier`, `SqlDialectGeneratorBase.TimeZone.cs` members.

### C-2 Application (`src/Autheris.Application`)
```csharp
// Sql/TimeZones/ITimeZoneConversionState.cs (new)
public interface ITimeZoneConversionState { bool IsAvailable(TargetSqlDialect dialect); }
// Sql/TimeZones/TimeZoneConversionState.cs (new): Lazy evaluation, registry self-test + DuckDB ICU probe, one Warning on failure
// Sql/TimeZones/TimeZoneConversionStartupCheck.cs (new): IHostedService that touches the state once at startup
// Sql/Policy/CompileRequestFactory.cs: Create(..., bool timeZoneConversionAvailable = false)
// Sql/Services/GovernedSqlRewriter.cs: constructor takes ITimeZoneConversionState? (null = unavailable); passes IsAvailable(targetSqlDialect)
// Sql/Services/CrossSourcePlanner.cs: calls TimeZoneConversion.ValidateFederated(statement, state.IsAvailable(DuckDb))
// Sql/Errors/SqlCompilerErrorMapper.cs: one row (A-10)
```
DI registration next to the other SQL services in `src/Autheris.Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs` (the `IDuckDbOlapEngine` registration is at `:109`).

---

## 4. Changed Files and Ownership (one developer; every file has exactly one work package)

### WP-1 - Engine: registry, data, classifier, codes
- New: `src/TrinoSqlEngine/TimeZones/TimeZoneRegistry.cs`, `SqlTimeZoneException.cs`, `TimeZoneSelfTestResult` (same file), embedded `src/TrinoSqlEngine/TimeZones/iana-windows-zones.txt` (+ `<EmbeddedResource>` in `TrinoSqlEngine.csproj`), `src/TrinoSqlEngine/Ast/Security/PlainTimestampClassifier.cs`.
- Edit: `src/TrinoSqlEngine/Ast/Emit/GovernedSqlException.cs` (codes and fixed messages), `src/TrinoSqlEngine/SqlFunctionAllowlists.cs` (`"convert_timezone"` in `CommonFunctions`, date group), `src/TrinoSqlEngine/CompileRequest.cs` (property), `src/TrinoSqlEngine/Ast/Capabilities/DialectCapabilities.cs` and `DialectCapabilityTable.cs` (flag).
- Tests: `tests/TrinoSqlEngine.Tests/TimeZones/TimeZoneRegistryTests.cs`, `PlainTimestampClassifierTests.cs`, `TimeZoneAllowlistTests.cs`.

### WP-2 - Engine: validator, scope-tracker extraction, pipeline wiring
- New: `src/TrinoSqlEngine/Ast/Visitors/TimeZoneCallValidator.cs`, `src/TrinoSqlEngine/Ast/Security/ColumnScopeTracker.cs`, `src/TrinoSqlEngine/TimeZones/TimeZoneConversion.cs`.
- Edit: `src/TrinoSqlEngine/Ast/Security/LiteralColumnTypeAnnotator.cs` (use the tracker, no behaviour change), `src/TrinoSqlEngine/GovernedSqlCompiler.cs` (validator pass between `validate` and `simplify`, around `:140-148`).
- Tests: `tests/TrinoSqlEngine.Tests/TimeZones/TimeZoneCallValidatorTests.cs`, existing `LiteralColumnTypeAnnotator` tests unchanged and green.

### WP-3 - Engine: emitter, zone slot, binder; Application state
- New: `src/TrinoSqlEngine/Ast/Generators/SqlDialectGeneratorBase.TimeZone.cs`, `src/Autheris.Application/Sql/TimeZones/{ITimeZoneConversionState,TimeZoneConversionState,TimeZoneConversionStartupCheck}.cs`.
- Edit: `SqlDialectGeneratorBase.Functions.cs` (interception branch in `GenerateFunctionCall`), `SqlServerDialectGenerator.cs`, `PostgreSqlDialectGenerator.cs`, `AnalyticalDialectGenerators.cs` (DuckDB override only), `src/TrinoSqlEngine/Ast/Buffer/SqlEmitterContext.cs`, `Ast/Emit/CompiledSql.cs`, `Ast/Emit/CompiledSqlTemplate.cs`, `Ast/Emit/ICompiledSqlBinder.cs` (zone branch in `BindCore`).
- Tests: `tests/TrinoSqlEngine.Tests/TimeZones/TimeZoneEmissionTests.cs`, `TimeZoneBinderTests.cs`, `TimeZoneTemplateCacheTests.cs`; `tests/Autheris.Tests.Unit/Sql/TimeZones/TimeZoneConversionStateTests.cs`.

### WP-4 - Application wiring, mapper, federation guard, OLAP pin
- Edit: `src/Autheris.Application/Sql/Policy/CompileRequestFactory.cs`, `Sql/Services/GovernedSqlRewriter.cs`, `Sql/Services/CrossSourcePlanner.cs`, `Sql/Errors/SqlCompilerErrorMapper.cs`, `src/Autheris.Api/Extensions/DependencyInjection/GatewayExecutionServiceExtensions.cs`.
- Tests: `tests/Autheris.Tests.Unit/Sql/ConvertTimeZoneWebSqlTests.cs` (through `GovernedTestSupport`), `Errors/SqlCompilerErrorMapperTests.cs` rows, `tests/Autheris.Tests.Unit/Sql/ConvertTimeZoneFederationTests.cs`, `OlapSqlPolicy` pin test.

### WP-5 - Integration and gate corpus
- Tests only: `tests/Autheris.Tests.Integration/ConvertTimeZoneExecutionTests.cs` (SQL Server and PostgreSQL via the shared Testcontainers fixtures of `AstCompilerSqlServerExecutionTests`/`AstCompilerPostgreSqlExecutionTests`; DuckDB in process like `AstCompilerDuckDbFunctionTests`), `tests/Autheris.Tests.SqlCompilerGate/Corpus/dql.sql` (new rows, see T-34), gate and G8 re-run (lead builds).
- No source edits. `accepted-differences.json` is **not** edited. Phase 6 hand-over: `docs/features/f-data-02-governed-websql.md` (function, argument order, result type, DST rules A-12, zone list, dialect support), `docs/features/f-dialect-02-ast-sql-compiler.md` (new capability flag), `docs/features/README.md` unchanged (no new feature id), master plan row, tracker row I-6. The lead owns those updates; this plan edits no other file.

Shared-file note for the lead (gate G-SHARED): this item touches `CompiledSql.cs`, `CompiledSqlTemplate.cs`, `SqlEmitterContext.cs`, `ICompiledSqlBinder.cs`, `CompileRequest.cs`, `GovernedSqlException.cs`, `GovernedSqlRewriter.cs`, `CompileRequestFactory.cs`, `SqlCompilerErrorMapper.cs`, `CrossSourcePlanner.cs` and `DialectCapabilities.cs`, all also touched by `PLAN-SQL-128` (I-5, merges first; appended record members keep the order `ListIndex, ListCount, ZoneArgument`) and partly by `PLAN-FED-131` (I-26) and the INC-6 plans. The tracker's shared-files list for I-6 should be extended accordingly.

---

## 5. Fail-Closed Behaviour

| Situation | Behaviour |
|---|---|
| Dialect SQLite, Oracle, Databricks (any dialect with `SupportsTimeZoneConversion == false`) | `SQL_FUNCTION_UNSUPPORTED_DIALECT` (501) from the validator; the base generator throws the same code if the validator were bypassed; nothing emitted |
| Self-test failed, DuckDB ICU probe failed, state not yet evaluated | `SQL_TIMEZONE_UNAVAILABLE` (503); `CompileRequest.TimeZoneConversionAvailable` defaults to `false` for every other caller |
| Unknown zone literal (wrong case, trailing space, over 64 characters, alias such as `EST`, quote or comment marker, NUL, lookalike Unicode) | `SQL_TIMEZONE_INVALID` (400) at compile time; nothing reaches the database |
| Zone parameter value not in the closed set, not a string, `null` | `SQL_TIMEZONE_INVALID` (400) at bind time, before the command is created |
| Zone is a column, expression, concatenation, function, cast, subquery, positional `?` | `SQL_TIMEZONE_ARGUMENT` (422) |
| Wrong arity, `DISTINCT`/`FILTER`/`OVER` on the call | `SQL_TIMEZONE_ARGUMENT` |
| `ts` offset-carrying, `date`/`time`, unresolved, bare parameter, `current_timestamp`, SQL Server `timestamp` (rowversion) | `SQL_TIMEZONE_ARGUMENT` |
| Infix `AT TIME ZONE` | still `AstBuildException` -> `SQL_CONSTRUCT_NOT_SUPPORTED`; accepted-difference entry unchanged |
| Federated statement with a bare column `ts` or a parameter zone | `SQL_TIMEZONE_ARGUMENT` until the column rule is relaxed (not planned) and #131 lands (parameter zone) |
| Ambiguous or non-existent local time, historic date | no error ever (V-130-9); database rule applies (A-12) |
| Source does not support `AT TIME ZONE` (SQL Server compatibility level below 130, very old PostgreSQL) | provider error mapped by the existing `SQL_PROVIDER_ERROR` path; documented prerequisite, not probed |
| Zone id unknown to the **remote** database's tz data (older tzdata than the embedded list) | provider error, data-independent (depends on the zone only); documented, risk R-4 |
| Any exception while building the registry or the state | function unavailable (A-7); the gateway keeps running |

---

## 6. Configuration

None (A-11). Operator-visible: one startup Warning when the self-test or the DuckDB probe fails (check id only), the function documented per dialect. `WebSql:AdditionalAllowedFunctions` cannot add `timezone`, `at_timezone`, `switchoffset` etc. beyond the existing rules: they have no generator rule (SQL Server/PostgreSQL pass unknown names through today, which is existing behaviour of the `AdditionalAllowedFunctions` option and is out of scope).

---

## 7. TDD Test List (write red first, in this order)

Expected values use Europe/Berlin (CET +01, CEST +02; 2026-03-29 and 2026-10-25 transitions at 01:00:00 UTC) and America/New_York (EST -05, EDT -04; 2026-03-08 at 07:00:00 UTC and 2026-11-01 at 06:00:00 UTC).

**Proof first (WP-1, DuckDB assumption)**
1. T-1 DuckDB ICU under the production sandbox: open `DataSource=:memory:`, run `DuckDbOlapEngine.BuildSetupSql` statements (autoload off, external access off, `lock_configuration`), then `SELECT timezone('Europe/Berlin', timezone('UTC', TIMESTAMP '2026-03-29 01:00:00'))` returns `2026-03-29 03:00:00`; also `SELECT count(*) FROM pg_timezone_names()` is greater than 400. **If this fails the plan degrades to SQL Server and PostgreSQL only (Q-130-4): WP-3 skips the DuckDB override, `SupportsTimeZoneConversion` stays false for DuckDb, the federated tests T-25 becomes a rejection test.**

**Registry and classifier (WP-1)**
2. T-2 every embedded entry matches the A-2 regexes; no duplicate IANA id; ordinal lookup (`europe/berlin`, `Europe/Berlin `, `Europe/Berlin\0` are not found); `UTC`, `Etc/UTC`, `Europe/Berlin`, `America/New_York`, `Asia/Kolkata`, `Australia/Lord_Howe`, `America/St_Johns`, `Pacific/Kiritimati` are present with the expected Windows ids.
3. T-3 `TryResolve`: SQL Server returns `W. Europe Standard Time` for `Europe/Berlin`, PostgreSQL and DuckDB return `Europe/Berlin`, SQLite/Oracle/Databricks return false; the SQL Server value is a registry string (reference-equal to the table entry, not the caller's instance).
4. T-4 hostile inputs are not members: `Europe/Berlin'; SELECT 1--`, `Europe/Berlin/*x*/`, 65-character id, empty, whitespace, `{{x}}`, Cyrillic lookalike of `Europe`, `%`, backslash.
5. T-5 `SelfTest()` passes; a test seam (mutated resource text through an internal constructor) makes it fail for a changed hash, a missing anchor and an illegal character, each with its own check id and no data in the result.
6. T-6 `PlainTimestampClassifier` table of A-9 per dialect, including SQL Server `timestamp` rejected, `datetimeoffset`, `timestamptz`, `date`, `null`, garbage.
7. T-7 `convert_timezone` is in `SqlFunctionAllowlists.Build(d, null)` and `CompilerNames(d)` for every dialect; `SqlFunctionPolicy.IsDeniedByDefault("convert_timezone")` is false; `timezone`, `at_timezone`, `with_timezone`, `switchoffset`, `todatetimeoffset` are in no allowlist.

**Validator (WP-2)**
8. T-8 order of checks: unsupported dialect wins over availability, availability over shape, shape over zone, zone over `ts`; each throws its own code.
9. T-9 literal zones: valid pair passes; unknown literal -> `SQL_TIMEZONE_INVALID`; the exception message equals the fixed message and contains neither zone nor SQL text.
10. T-10 non-literal zones: column, `'Europe/' || 'Berlin'`, `lower('X')`, `CAST('UTC' AS varchar)`, scalar subquery, `?`, `NULL`, number, `CASE` -> `SQL_TIMEZONE_ARGUMENT`.
11. T-11 synthetic parameter zones pass the validator; the same name also used as an ordinary value elsewhere compiles to two distinct slots.
12. T-12 arity 2 and 4, `DISTINCT`, `FILTER`, `OVER` -> `SQL_TIMEZONE_ARGUMENT`.
13. T-13 `ts` forms accepted: plain column (per dialect type), `CAST(x AS timestamp)`, `CAST(x AS timestamp(3))`, `TRY_CAST` on SQL Server and DuckDB, `TIMESTAMP '2026-03-29 01:00:00'`, nested `convert_timezone`.
14. T-14 `ts` forms rejected: `datetimeoffset`/`timestamptz` column, `date` column, unresolved CTE or derived-table column, bare `@p`, `current_timestamp`, `ts + INTERVAL '1' DAY`, `CAST(x AS timestamp with time zone)`.
15. T-15 `AT TIME ZONE` operator still raises `AstBuildException` (fixture `80d5942dea95` unchanged).
16. T-16 the validator runs on the user tree only: a policy-injected node or a mask expression named `convert_timezone` cannot exist (negative construction test of `GovernedSqlCompiler` order: validator before injection).

**Emission and binder (WP-3)**
17. T-17 golden SQL and parameter lists for SQL Server, PostgreSQL, DuckDB with literal zones (two `QueryLiteral` slots, mapped values) and with parameter zones (two `ClientNamed` zone slots); the user `ts` is parenthesized; no zone text appears in the SQL text of any dialect (`EmittedSqlInvariantChecker` green).
18. T-18 SQL Server mapping: literal `Europe/Berlin` binds `W. Europe Standard Time`; PostgreSQL and DuckDB bind `Europe/Berlin`.
19. T-19 binder, all dialects: a zone slot with a valid value binds the mapped value; with an unknown value, `null`, a non-string, `'x'' OR 1=1--`, a 65-character string -> `SqlTimeZoneException(SQL_TIMEZONE_INVALID)` and `command.Parameters` stays empty; the ordinary value slot of the same name is untouched.
20. T-20 template cache: second compile of the same raw SQL rebinds; changing `TimeZoneConversionAvailable` yields a different cache key (mutation: remove the property from the key and the test fails); the zone flag survives `ToSlots`/`Rebind`.
21. T-21 base generator guard: SQLite, Oracle, Databricks, ANSI, Snowflake generators throw `SQL_FUNCTION_UNSUPPORTED_DIALECT` when handed the AST directly (validator bypassed); legacy unbound emission throws `NoBoundForm`.
22. T-22 `TimeZoneConversionState`: self-test failure (seam) -> all dialects unavailable; DuckDB probe failure (seam) -> only DuckDB unavailable and exactly one Warning without driver text; unevaluated state answers `false`.
23. T-23 `CompileRequestFactory` default is `false`; only the rewriter path passes `true`; a request constructed elsewhere (virtual filters, table reads) rejects the function with `SQL_TIMEZONE_UNAVAILABLE`.

**Application and federation (WP-4)**
24. T-24 WebSQL end to end on SQLite governed test support: `SQL_FUNCTION_UNSUPPORTED_DIALECT` with HTTP 501 and the fixed body through the mapper; same through the Trino route.
25. T-25 federated: literal zones plus `CAST(col AS timestamp)` run on DuckDB with the staged rows (skipped to a rejection test if T-1 failed); bare column `ts` and parameter zone -> `SQL_TIMEZONE_ARGUMENT`; DuckDB unavailable -> `SQL_TIMEZONE_UNAVAILABLE`.
26. T-26 the federated planner function check (`CrossSourcePlanner.cs:107-118`) accepts `convert_timezone` and still rejects `timezone`, `at_timezone`.
27. T-27 mapper rows: four codes, statuses 400/422/501/503, fixed messages, no value in `Title`/`Detail`; the row precedes the `SecurityException` row (an `SqlTimeZoneException` is never 403).
28. T-28 audit and logs: with log capture and audit capture, a rejected zone (`Europe/Evil`) and an accepted one appear in no log line, audit field, span attribute or exception message; `bind_count` includes the two zone binds.
29. T-29 `OlapSqlPolicy.AllowedFunctions` contains neither `convert_timezone` nor `timezone`; an OLAP cross-source statement calling `timezone(...)` is still rejected with `ConstructNotAllowed`.

**Real engines (WP-5, SQL Server and PostgreSQL containers, DuckDB in process)**
30. T-30 `GROUP BY cast(convert_timezone('UTC','Europe/Berlin', ts) as date)` with the same call in `SELECT` and `GROUP BY` executes (the de-duplicated markers match on PostgreSQL and SQL Server) and assigns these UTC instants to local days: 2026-03-28 23:30:00 -> 2026-03-29; 2026-06-30 22:30:00 -> 2026-07-01; 2026-10-24 22:30:00 -> 2026-10-25; 2026-10-25 23:30:00 -> 2026-10-26 (AC-130-2). If a dialect rejects it, apply the inline-constant fallback of A-5 (R-2) and keep the test.
31. T-31 winter and summer, UTC to Berlin: `2026-01-15 12:00:00` -> `13:00:00`; `2026-07-15 12:00:00` -> `14:00:00`; and the reverse direction for unambiguous local values (`Europe/Berlin` `2026-07-15 14:00:00` to UTC -> `12:00:00`). Identical on SQL Server, PostgreSQL and DuckDB.
32. T-32 transition days with a UTC source, identical on all engines (AC-130-1, V-130-5): Berlin spring `2026-03-29 00:59:59` -> `01:59:59`, `01:00:00` -> `03:00:00`; Berlin autumn `2026-10-25 00:59:59` -> `02:59:59`, `01:00:00` -> `02:00:00`; New York spring `2026-03-08 06:59:59` -> `01:59:59`, `07:00:00` -> `03:00:00`; New York autumn `2026-11-01 05:59:59` -> `01:59:59`, `06:00:00` -> `01:00:00`. Plus half-hour zones: `Asia/Kolkata` `2026-07-15 12:00:00` -> `17:30:00`; `Australia/Lord_Howe` across its 30-minute shift (expected values recorded from the first run and cross-checked against `TimeZoneInfo` where ICU exists).
33. T-33 local-time inputs on transition nights (**characterization**, per dialect): ambiguous Berlin `2026-10-25 02:30:00` and non-existent Berlin `2026-03-29 02:30:00` converted to UTC must **not throw** on any engine (V-130-9) and the result must be one of the two valid instants for the ambiguous case; the observed per-dialect values are asserted and documented (A-12). If any engine throws, the dialect is removed from `SupportsTimeZoneConversion` and the owner is told (this would contradict V-130-9).
34. T-34 gate corpus: positive rows (winter, summer, both transition days, parameter zone, nested call, `CAST(.. AS date)` on the result) and negative rows (unknown zone, column zone, concatenated zone, `datetimeoffset`-typed input, wrong arity, operator form, SQLite dialect) added to `Corpus/dql.sql`. **Before adding rows the developer reads `CorpusTests.cs`, `DifferentialGateTests.cs` and `docs/plans/2026-10-15-plan-sqlgate-cutover-and-accepted-differences.md` to learn how row ids and frozen legacy results (`golden-results/legacy-*.json`) are derived. If a row needs regenerated golden files or a new accepted difference, the developer stops and reports to the lead; `accepted-differences.json` is never edited without owner sign-off (AC-130-7).**
35. T-35 result-type contract: PostgreSQL returns `timestamp` (no offset) regardless of the session `TimeZone` (set the connection to `Pacific/Auckland` and to `UTC`, same result); SQL Server returns `datetime2` (no `datetimeoffset` leaks); DuckDB returns `TIMESTAMP`.
36. T-36 masks: a table with a typed mask on a timestamp column: `convert_timezone('UTC','Europe/Berlin', t.masked_ts)` returns a value derived from the **masked** projection and never the raw value (secured derived table); grouping by it is refused only as today for masked columns.
37. T-37 `date_trunc('day', convert_timezone(...))` on PostgreSQL and DuckDB and `cast(... as date)` on all three give the local day (FR-130-4); for SQL Server `date_trunc` is asserted only if the dialect already supports it, otherwise the case is documented, not added.
38. T-38 G8 allocation gate and `CompilePathPerformanceTests` unchanged (no allocation on statements without `convert_timezone`: the interception is one name comparison).

---

## 8. Work Packages for One Sequential Developer

Order is fixed (gate G-ONE); each WP ends green on its own tests; the lead builds once per item (G-BUILD, Release, warnings as errors).
1. **WP-1** commit "contracts" first (C-1 signatures compile), then T-1 (decides the DuckDB scope), T-2..T-7 red, then registry, data file, classifier, codes, allowlist, flag.
2. **WP-2** T-8..T-16 red, then scope-tracker extraction (annotator tests stay green), validator, compiler wiring.
3. **WP-3** T-17..T-22 red, then generator interception and overrides, zone slot, binder, application state.
4. **WP-4** T-23..T-29 red, then factory, rewriter, planner guard, mapper, DI. Re-check every other construction site of `CompileRequest` (grep `new CompileRequest`) keeps the fail-closed default.
5. **WP-5** T-30..T-38 (containers, gate corpus); stop-and-report rule of T-34 and T-33.
Gate per WP: SqlGate and AST security suites, the existing WebSQL, binder and template-cache tests stay green. Hand-over note for Phase 6 in WP-5.

---

## 9. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | DuckDB ICU is not callable under the production sandbox despite the static symbols | T-1 first; documented degradation to SQL Server and PostgreSQL (Q-130-4); federated use blocked with `SQL_TIMEZONE_UNAVAILABLE` |
| R-2 | `GROUP BY` expression matching with parameter markers fails on a dialect | Marker de-duplication (`SqlEmitterContext.cs:102-111`) makes equal zones one marker; T-30 proves it; fallback to inline registry constants (A-5) is pre-designed |
| R-3 | Record changes (`BoundParameter`, `SqlSlot`, `DialectCapabilities`, `CompileRequest`) break positional constructions or golden tests; collision with `PLAN-SQL-128` | Members appended optional and in the order `ListIndex, ListCount, ZoneArgument`; I-5 merges first; architecture and compiler tests run in WP-1/WP-3 |
| R-4 | Remote database tz data older than the embedded list (unknown newer zone id) or SQL Server below compatibility level 130 | Provider error (`SQL_PROVIDER_ERROR`), data independent; documented prerequisite; the list is conservative (CLDR intersection) and versioned |
| R-5 | CLDR Windows mapping changes names between releases | Resource plus hash constant plus anchors in the self-test; a data update is a reviewed code change |
| R-6 | Engines differ on ambiguous and non-existent local times | Not a requirement outside tested days (V-130-5); T-33 pins and documents per dialect; guidance "convert from UTC" (A-12) |
| R-7 | Adding the name to `CommonFunctions` widens row-policy predicate allowlists (`RowPolicyFactory.cs:102`, `PolicyParserSetup.cs:49`) | Policy predicates are admin-authored and compiled by the same builder; Phase 3 decides whether to exclude the name there (section 11 item 9) |
| R-8 | `LiteralColumnTypeAnnotator` refactor changes behaviour | Pure extraction; the existing annotator and SEC-ADG-16 tests must stay green before the validator is built on it |
| R-9 | Two zone parameters used as `ts`-adjacent values could confuse callers (a name used as zone and value) | Two slots by design (A-4); documented; test T-11 |
| R-10 | Gate corpus rows require golden or accepted-difference changes | Stop-and-report rule (T-34); no edit of `accepted-differences.json` |
| R-11 | Federated bare-column restriction annoys users | Documented limitation with the `CAST` workaround; relaxing needs per-source dialect types and is a separate decision |

---

## 10. Owner Flags and Open Questions (defaults taken)

| ID | Question | Default used in this plan |
|---|---|---|
| Q-130-1 | Argument order | **Decided**: `(from_zone, to_zone, ts)`, final |
| Q-130-2 | Result type | **Decided**: local wall-clock plain timestamp in all dialects |
| Q-130-3 | Zone list source | Embedded IANA-to-Windows resource (closed set = ids with a Windows mapping) plus self-test; .NET `TimeZoneInfo` only as a warning-level cross-check |
| Q-130-4 | DuckDB without ICU | Ship SQL Server and PostgreSQL only if T-1 fails; static evidence says ICU is present (section 1) |
| Q-130-5 | Further dialects | None (Oracle, Databricks, SQLite stay `SQL_FUNCTION_UNSUPPORTED_DIALECT`) |
| F-1 | Security-relevant engine change: new validator pass in `GovernedSqlCompiler`, new binder branch, new generator interception; `AstSecurityVisitor.cs` is **not** modified | Owner review at Phase 5 |
| F-2 | PRD V-130-6 ("Clear columns only") is replaced by "the function sees the masked projection" (T-36) because no mechanism restricts scalar functions on masked columns today | Accept, or specify the stricter rule |
| F-3 | The closed set excludes IANA ids without a Windows mapping, so some valid PostgreSQL zones are not offered | Accept (portability over coverage) |
| F-4 | Federated bare-column `ts` and parameter zones are rejected initially (A-8) | Accept; parameter zones follow with #131 |

---

## 11. Security Notes for Phase 3

1. **Zone text never reaches SQL text:** confirm that both the literal path (A-5, bound mapped value) and the parameter path (A-4, binder-mapped value) bind registry-owned strings, that no `ConstantFragment` is registered for zones, and that the emitted-SQL checker sees no zone text. Review the SQL Server Windows-id regex (spaces, parentheses, periods) against the binding style (all values bound, so the regex is hygiene, not the barrier).
2. **Closed-set completeness:** review every route by which a zone value can enter: literal, named parameter, nested expression (rejected), federated literal, caller-attribute values of #129 (future: must call `TimeZoneRegistry` too, noted for the #129 review), MCP and Flight SQL parameters (all through the same binder). Confirm the binder check also covers `BindForCheckedExecution` and the federated path after #131.
3. **Cache interplay:** a cache hit skips the validator; confirm that every decision it makes is a function of the raw SQL, the dialect and `TimeZoneConversionAvailable` (key by reflection, T-20 mutation) and that the bind-time check cannot be skipped by a rebind.
4. **No oracle (V-130-9):** every rejection is static or depends on the zone value only; messages and codes carry no zone, no count, no catalog type name; ambiguous and non-existent local times never raise (T-33).
5. **Type confusion:** review the dialect-specific plain-timestamp table (SQL Server `timestamp` is `rowversion`; PostgreSQL `timestamptz` flips the semantics of `AT TIME ZONE`) and the "unknown is rejected" rule; review that the user `ts` expression cannot be an offset-carrying value through a `CAST` spelling or a typed literal (`TIMESTAMP '... UTC'` is rejected by the existing typed-literal regex `SqlAstBuilder.cs:TimestampValue`).
6. **Result type and session independence:** confirm PostgreSQL results are independent of the session `TimeZone` (T-35) and that the gateway never issues `SET TIME ZONE`/`SET LANGUAGE`/`SET DATEFIRST` (least privilege unchanged).
7. **Federated generator bypass:** `CrossSourcePlanner` generates DuckDB SQL without the governed compiler; confirm the explicit validator call is complete (before `FederationAstRewriter`, all statements incl. CTEs and subqueries) and that `ExecuteGeneratedAsync` remains the only trusted entry (`OlapEntryPointArchitectureTests`).
8. **OLAP allowlist:** confirm the decision that `convert_timezone` and `timezone` stay out of `OlapSqlPolicy.AllowedFunctions` (client-written DuckDB SQL cannot be zone-validated).
9. **Policy allowlist side effect (R-7):** decide whether `convert_timezone` should be removed from `Build(...)` results used by `RowPolicyFactory`/`PolicyParserSetup` (admin-authored policy predicates). The plan accepts it because the same builder and validator run, but the reviewer owns the call.
10. **Denial of service:** zone literals are length-capped before lookup; the registry is a frozen dictionary (O(1)); the DuckDB probe runs once per process on an in-memory database with the sandbox memory and thread limits; the unavailable-state path does no work per request.
11. **Self-test honesty:** the cross-check against `TimeZoneInfo` is warning-only by design; confirm that a node with different OS tz data than its database cannot cause a fail-open (it cannot: the databases decide the conversion, the gateway only decides admission).
12. **Supply chain:** the embedded CLDR data is code-reviewed text with a hash constant; review the licence note and that no runtime download exists.

---

## 12. Size Estimate

| Area | Production LOC | Test LOC |
|---|---:|---:|
| WP-1 registry, classifier, codes, allowlist, flag | 260 (+ about 650 lines of embedded data) | 330 |
| WP-2 validator, scope-tracker extraction, wiring | 230 | 330 |
| WP-3 emitter, zone slot, binder, application state | 270 | 340 |
| WP-4 wiring, mapper, federation guard, DI | 90 | 220 |
| WP-5 integration and corpus | 0 | 300 |
| **Total** | **about 850 (+ data)** | **about 1,500** |
PRD estimate S-M (650 / 900); the delta comes from the validator (type classification, scope tracker), the zone slot role in the binder and the federated guard. One sequential developer; no schema or DDL change.

---

## 13. Changelog

| Revision | Date | Change |
|---|---|---|
| 1 | 2026-10-16 | Initial Phase 2 plan (status PROPOSED). |
| 1+S | 2026-10-16 | Phase 3 security review appended (13 findings SEC-130-01..13, 14 binding conditions, 18 mandatory security tests, verdict APPROVED WITH CONDITIONS). No change to the revision-1 text above. |

---

## Phase 3 Security Review (2026-10-16)

**Reviewer:** `csharp-security-expert` (Phase 3, delivery step S-3). **Inputs:** this plan (revision 1), PRD section 4 with owner decisions Q-130-1/2, the code at the worktree head (waves 1 merged). **Scope:** review only; no source, corpus, golden or `accepted-differences.json` change. All file:line references were re-read for this review. Where the plan's own line references were correct they are not repeated.

### 1. Threat Model

| Asset | Threat | Actor | Existing control | Plan control | Verdict |
|---|---|---|---|---|---|
| Statement text sent to the source | Zone text spliced into SQL (injection through `from_zone`/`to_zone`) | WebSQL / MCP / Flight SQL user | All literals bound on the governed path (`PostgreSqlDialectGenerator.cs:47`, `SqlEmitterContext.BindValue` `:100-111`), `EmittedSqlInvariantChecker` | closed set, value mapped to registry-owned text, always bound (A-4, A-5) | Sound, plus BC-130-5/6/7 |
| Closed zone set | Poisoned or stale embedded mapping (wrong zone silently) | Supply chain, careless data update | SHA-256 constant, anchors | hash + 7 anchors | Insufficient against semantic swaps (SEC-130-06) |
| Function gates | New name opens a path for other unlisted functions, or the new call is emitted without validation | WebSQL user, operator config | allowlist gates, denylist, exact-name allowlist match (`SqlFunctionPolicy.cs:156-160`) | name in `CommonFunctions`, validator, base interception | Sound for the name itself; gaps on non-compiler emitters (SEC-130-02/03/04/09) |
| Masked values | Function used as oracle or to read raw value | Authenticated user | masks applied in the secured derived table before user expressions (`GovernedSqlCompiler.cs:178-186`), aggregate/grouping guard (`AstSecurityVisitor.MaskGuards.cs:395-436`) | "function sees masked projection" (F-2, T-36) | Accepted with BC-130-1 (reject masked `ts` columns) |
| Row policies | Admin-authored predicates gain an unvalidated function | Admin / policy author | policy allowlist is `Build(...)` (`src/Autheris.Application/Policy/RowPolicyFactory.cs:102`, `.../Policy/Lint/PolicyParserSetup.cs:49`, parser `PolicyExpressionParser.cs:285`, no validator) | accepted as side effect (R-7) | Exclude (SEC-130-02) |
| Federated DuckDB | Planner generates SQL without the compiler (`CrossSourcePlanner.cs:272-282`) | WebSQL user | planner function check (`:107-118`) | explicit validator call (A-8) | Sound if placed correctly (SEC-130-04) |
| DuckDB sandbox | Extension autoload / network to get ICU; wrong zone semantics | WebSQL user | `autoinstall/autoload=false`, `enable_external_access=false`, `lock_configuration` (`DuckDbOlapEngine.cs:283-287`) | T-1, startup probe | Needs stronger probe (SEC-130-07) |
| Availability | Expensive conversions (SQL Server `AT TIME ZONE` is non-sargable and costly), nested chains | Authenticated user | row limits, command timeout | none specific | Add caps (SEC-130-08) |
| Operator `AT TIME ZONE` | Accidentally re-admitted through generator or a second emit path | any | builder rejects (`accepted-differences.json:93`, fixture `80d5942dea95`) | A-1, T-15 | Sound, widen tests (SEC-130-11) |

Trust boundaries: (1) client text and client parameter values are untrusted; (2) the embedded registry is trusted only after CI verification; (3) the generator is trusted to emit only what the AST allows, hence the AST must be validated on every path that reaches it.

### 2. Answers to the Review Questions

1. **Three gates and generator pass-through.** Gate 1 is exact-name allowlist matching on the analyzer's names (`GovernedSqlRewriter.cs:601-612`); in allowlist mode a qualified spelling such as `dbo.convert_timezone` does **not** match the plain entry (`SqlFunctionPolicy.cs:156-160`), so it is rejected before any validator runs. Gate 2 is the builder (`SqlAstBuilder.cs:1155-1161`, name from `NormalizeQualifiedName`, `:1296`). Gate 3 is the generator. Adding one name to `CommonFunctions` does not widen the set for any other name, and the interception in `GenerateFunctionCall` is keyed to one name, so **the new function does not open a path for other unlisted functions**. The pre-existing pass-through of allowlisted-but-unmapped names (`SqlDialectGeneratorBase.Functions.cs:244-270`, `FormatFunctionName` `:446-455`) is not changed and stays out of scope, but two weak spots remain: the interception must match the **normalized** name regardless of quoting (SEC-130-03) and `AdditionalAllowedFunctions` (`RestApiOptions.cs:56`) lets an operator add `timezone`/`at_timezone`/`switchoffset`, which would then pass through verbatim without the closed set (SEC-130-09).
2. **Literals vs parameters, mapping integrity, fallback.** Both routes bind registry-owned text; the plan is sound in design. Gaps: zone slots must be validated in a pre-pass (the binder sets `CommandText` before its loop, `ICompiledSqlBinder.cs:187-193`), list parameters must never be zone slots (#128), and other consumers of `CompiledSql.Parameters` do not use the binder (SEC-130-05). The **inline-constant fallback is safe only under conditions**: `EmittedSqlInvariantChecker` skips constant ranges (`EmittedSqlInvariantChecker.cs:76`), so an unchecked `ConstantFragment` would be an unverified string in SQL text (SEC-130-10).
3. **Resource tampering and staleness:** the hash constant protects against accidental edits only (SEC-130-06).
4. **`AT TIME ZONE` operator** stays rejected in all user-controlled positions; the generated SQL contains the text only through the interception (SEC-130-11).
5. **Masks:** masks are applied before the user expression, so the function receives the masked projection in every dialect that the secured-derived-table mechanism covers (all compile dialects, one mechanism: `GovernedSqlCompiler.cs:178-186`). The function is a pure per-row transformation of an already-masked value and reveals nothing beyond that value; the aggregate/grouping guard refuses a masked column inside `GROUP BY` and aggregate arguments (`MaskGuards.cs:413-436`). Residual risk is **type, not leak**: the validator classifies the catalog type of the raw column, but the masked projection may have another type (HMAC and Redact yield text, Nullify yields NULL), producing a provider type error that discloses the mask kind (SEC-130-01).
6. **Row-policy allowlists:** exclude (SEC-130-02).
7. **Federated planner:** covered if the validator call is placed after the `statement ??=` block and on every `Plan` entry (SEC-130-04).
8. **DuckDB ICU:** static evidence only; require a probe that asserts the extension is statically linked and loaded under the exact production sandbox (SEC-130-07).
9. **DoS:** add per-statement call and nesting caps (SEC-130-08).
10. **Corpus, golden, accepted differences:** the plan's stop-and-report rule (T-34) is adequate and is made binding (BC-130-14).

### 3. Findings

Severity scale: High = exploitable or silent security-control bypass if shipped as planned; Medium = control gap that must be closed before merge; Low = hardening; Info = no change required.

**SEC-130-01 (Medium) - Masked column as `ts`: validator is mask-blind, type mismatch discloses mask kind.**
Evidence: the validator runs before injection (A-3, `GovernedSqlCompiler.cs:142-148` order: validate, simplify, then `AstSecurityVisitor` at `:178-186`), classifying the **catalog** type of the raw column (A-9); the mask changes the projected type (`MaskKind { Nullify, Redact, PartialMask, Hmac, GeoJitter, Constant }`, `PolicyProviders.cs:26`). `convert_timezone(.., masked_ts)` then reaches the database with a text/NULL operand: the provider error (`SQL_PROVIDER_ERROR`) or, for Nullify, a NULL result, differs by mask kind. No raw value leaks (the function sees the masked projection), so this is not a value oracle, but it is a mask-kind probe and an inconsistent behaviour per user.
Required change: BC-130-1.

**SEC-130-02 (Medium) - Row-policy predicate allowlists widened by `CommonFunctions`.**
Evidence: `src/Autheris.Application/Policy/RowPolicyFactory.cs:102` and `src/Autheris.Application/Policy/Lint/PolicyParserSetup.cs:49` use `SqlFunctionAllowlists.Build(target, ...)`; `PolicyExpressionParser.cs:285` builds the expression with that list and runs **no** `TimeZoneCallValidator`. The plan's R-7 / section 11 item 9 defers the call to this review. Decision: **exclude**. Policy predicates need no time zone conversion (policy parameters are values, not zones), the validator's availability, zone and `ts` type rules would be bypassed there, and an admin predicate with a nested conversion would create per-row cost in every query on the table. The files are not in WP-4's edit list.
Required change: BC-130-2.

**SEC-130-03 (Medium) - Name matching must be on the normalized name in validator and generator.**
Evidence: the dialect overrides only act on `Parts.Count == 1 && !Parts[0].IsQuoted` (`SqlServerDialectGenerator.cs:261`, `PostgreSqlDialectGenerator.cs:34`) and then call `base.GenerateFunctionCall` (`:293`, `:44`), so a quoted `"CONVERT_TIMEZONE"(...)` reaches the base. Builder admission uses `NormalizeQualifiedName` (`SqlAstBuilder.cs:1296`), which unquotes. If the validator or the interception compared `Parts[0].Value` with `IsQuoted == false` or `Parts.Count == 1`, a quoted spelling would skip validation (validator) or be emitted as an ordinary pass-through call `CONVERT_TIMEZONE(...)` with an unvalidated zone (generator) - an unvalidated, injection-relevant path to the database function of that name where one exists.
Required change: BC-130-3.

**SEC-130-04 (Medium) - Federated planner: validator placement and entry coverage.**
Evidence: `CrossSourcePlanner.Plan(..., SqlStatement? statement = null)` (`:99`) is called with a prebuilt statement (`FederatedDuckDbExecutionService.cs:312-314`, built by `ParseStatement` `:354-357` with a default `new SqlAstBuilder()`) and without one (`:230`, builds at `:272-276`). The plan says "after its AST build at `:272-276`", which is inside `if (statement == null)` and would skip the validator on the main path. Also, a default `SqlAstBuilder` has `AllowedFunctions = null` (`AstBuilderOptions.cs:14`, `SqlAstBuilder.cs:1155-1161`): the planner's name check on `metadata.FunctionCalls` (`CrossSourcePlanner.cs:107-118`) is the only allowlist on that path, which is existing behaviour and stays, but it means the validator must work on AST names, not on metadata.
Required change: BC-130-4.

**SEC-130-05 (Medium) - Zone slot binding: pre-pass, list parameters, bypassing consumers.**
Evidence: `BindCore` assigns `CommandText` and starts adding parameters before it can reject a later slot (`ICompiledSqlBinder.cs:187-215`), contradicting the plan's "nothing reaches the database / `command.Parameters` stays empty" unless the zone check is a pre-pass; `InferType(value)` (`:207`) would type a numeric parameter as numeric; #128 introduces list parameters expanding to several markers, which must not serve as a single zone; other code reads `CompiledSql.Parameters` without the binder: `DuckDbOlapEngine.cs:89` (via `GeneratedOlapQuery`), `SqlVirtualFilterKeySetProvider.cs:270`, `GovernedTreeQueryService.cs:447` (own compiler, no user functions), `MaskSpecRenderer.cs:66` (fails closed for non-`Mask` origins, good). A `ZoneArgument` slot reaching one of those would be bound as the raw unmapped value or `NULL`.
Required change: BC-130-5.

**SEC-130-06 (Medium) - Embedded resource integrity and staleness.**
Evidence: A-2/A-7: the SHA-256 constant and the anchors live in the same assembly as the data, so anyone who can change the resource can change the constant; 7 anchors cover about 2% of the entries; a swapped mapping (for example `Europe/London` to another Windows id) satisfies regex, uniqueness and hash if both are edited together, and silently yields wrong wall-clock results on SQL Server. Staleness fails closed for new IANA ids (not offered) but old removed or renamed ids would produce provider errors only. The runtime cross-check against `TimeZoneInfo` is warning-only (correct, see section 11 item 11) and therefore protects nothing in production.
Required change: BC-130-6.

**SEC-130-07 (Medium) - DuckDB ICU proof and probe must reflect the real sandbox.**
Evidence: the plan's own evidence is static (`strings` on the native library). The probe in A-7 uses "the same sandbox statements" but would silently diverge if copied; `BuildSetupSql` is `internal static` (`DuckDbOlapEngine.cs:283-287`). A passing `timezone(...)` call proves ICU works but not **why**: a dynamically autoloadable ICU (needs `autoinstall`/`autoload`, i.e. network or file access) would pass on a developer machine and fail on a locked-down node, or the reverse after a driver upgrade that makes ICU an autoload extension. DuckDB's `timezone(zone, ts)` takes an explicit zone, so the session `TimeZone` does not influence the result (good), but `to_timestamp` and `now` already on the DuckDB allowlist do depend on it (pre-existing, not this item).
Required change: BC-130-7.

**SEC-130-08 (Low) - DoS: expensive conversions.**
Evidence: SQL Server `AT TIME ZONE` is evaluated per row, twice per call, is non-sargable in `WHERE`/`JOIN`, and nested `convert_timezone` chains are allowed as `ts` (A-3 step 4); nothing bounds calls or nesting per statement. Federated rows are bounded by `MaxTotalStagedBytes`/row limits, governed rows by existing limits and command timeouts.
Required change: BC-130-8.

**SEC-130-09 (Low) - `AdditionalAllowedFunctions` can re-open unvalidated zone functions.**
Evidence: `RestApiOptions.cs:56`; `GovernedSqlRewriter.cs:601` merges it into the allowlist; plan section 6 acknowledges that `timezone`, `at_timezone`, `switchoffset` could be added by configuration and pass through verbatim. The closed zone set and the mapping would be bypassable by operator configuration (not by a user).
Required change: BC-130-9 (startup validation, owner-visible flag R-3 below).

**SEC-130-10 (Low) - Inline-constant fallback (A-5, R-2) needs a checker and excludes parameter zones.**
Evidence: `EmittedSqlInvariantChecker.cs:76` does not inspect constant ranges; the fallback therefore removes the one automated check that "no zone text is in the SQL". A parameter zone cannot be inlined (value unknown at compile time, and inlining caller text is forbidden).
Required change: BC-130-10.

**SEC-130-11 (Low) - `AT TIME ZONE` stays rejected: test breadth and emit-site uniqueness.**
Evidence: T-15 covers one fixture. The text `AT TIME ZONE` will now be produced by generators (A-6); the only safe producer is `FormatConvertTimeZone`.
Required change: BC-130-11 plus mandatory tests ST-130-12/13.

**SEC-130-12 (Low) - Validator coverage of all expression positions and statement kinds.**
Evidence: the validator is a new visitor over the user tree (A-3); a position it does not traverse (window frame, `ORDER BY` of an aggregate, `HAVING`, `MERGE`/`INSERT ... SELECT`, `UPDATE SET`, table-function arguments, `CASE`, lambda-like constructs) would emit the call without the zone/`ts` checks, relying only on the generator's zone-shape guard (second check, A-6), which does **not** check the `ts` type or availability.
Required change: BC-130-12.

**SEC-130-13 (Info) - No further action; recorded so it is not re-opened.**
(a) Error ordering `501` before `503` (A-3) discloses only gateway state, not tenant data. (b) A PostgreSQL provider error for a zone unknown to an old remote tzdata can depend on whether any row is evaluated (empty set vs non-empty), a row-existence signal on a table the user can already query and count; R-4 stays accepted. (c) `SqlTimeZoneException` deriving from `Exception` (A-10) is correct only if no generic catch in the pipeline converts it; BC-130-13 adds the audit/catch check. (d) SQL Server `CAST(... AS datetime2)` uses default precision 7, not "precision follows the input" as A-6 says; a documentation correction for Phase 6, no security impact.

### 4. Binding Conditions (the Phase 4 developer implements these verbatim; revision 2 of the plan folds them in)

- **BC-130-1 (SEC-130-01):** `TimeZoneCallValidator` receives `request.Policy.Masks` and rejects with `SQL_TIMEZONE_ARGUMENT` any `ts` column reference that resolves (alias and scope rules of `ColumnScopeTracker`) to a column with any mask, and any column it cannot resolve (already the rule). The check uses the same `HasMask` semantics as the mask guards (`MaskGuards.cs:53-58`, including the `TablesWithMaskedColumns` fallback) and fails closed on a failing lookup (as `MaskGuards.cs:385-395`). The plan's F-2 wording becomes: "the function never sees a raw masked value, and a masked column is not accepted as `ts`". The nested-`convert_timezone` and `CAST(masked AS timestamp)` forms are covered by the same rule (the reference inside is the masked column). Owner flag F-2 is replaced by this rule (recommendation R-1 below).
- **BC-130-2 (SEC-130-02):** `convert_timezone` is **not** admitted in row-policy predicates, policy lint and any other `Build(...)`-based policy allowlist. Implement as a single `SqlFunctionAllowlists.BuildForPolicy(dialect, additional)` (or equivalent filter) used by `RowPolicyFactory.cs:102` and `PolicyParserSetup.cs:49`; add both files to WP-4. Virtual filters keep their closed list (`SqlFilterCompiler.cs:152-165`, unchanged). Section 11 item 9 and R-7 are closed by this decision.
- **BC-130-3 (SEC-130-03):** the validator and the generator interception select the call by `fn.Name.NormalizedName`/simple name compared case-insensitively with `convert_timezone`, independent of quoting, and treat a qualified name ending in that name as the function too (it then fails validation, never passes through). Interception is the **first** statement of `SqlDialectGeneratorBase.GenerateFunctionCall`, before `FormatFunctionName`, and the dialect overrides keep calling `base` for it (no override may handle the name). Add an architecture test that no generator override matches the name itself.
- **BC-130-4 (SEC-130-04):** in `CrossSourcePlanner.Plan` the call `TimeZoneConversion.ValidateFederated(...)` is placed **after** the `if (statement == null) { ... }` block (`:271-275`) and before `FederationAstRewriter`, so it runs for a supplied and a built statement and for the dry-run entry at `FederatedDuckDbExecutionService.cs:230`. It traverses CTEs, subqueries and set operations. An architecture/pinning test lists every non-compiler call site of a dialect generator's `Generate` in `src` (today: `CrossSourcePlanner.cs:282`, `MaskSpecRenderer.cs:41`) and fails when a new one appears without review; every such site must either call the validator or be provably unable to carry a user expression.
- **BC-130-5 (SEC-130-05):** (a) the binder validates and maps **all** zone slots in a pre-pass before `PrepareCommand`/`CommandText`/any `Parameters.Add`; a rejected value leaves the command untouched; (b) a zone slot is always bound as `SqlParameterType.String`, never through `InferType`; a non-string (number, bool, array, list, `DBNull`, `null`) is rejected; (c) a client parameter that is declared as a list parameter (#128 `clientLists`) or that also appears with an element index is rejected at compile time when used as a zone (`SQL_TIMEZONE_ARGUMENT`); (d) `CompiledSql`/`BoundParameter` with `ZoneArgument == true` is consumable only through `DbCommandCompiledSqlBinder`: every other consumer of `CompiledSql.Parameters` (`DuckDbOlapEngine.cs:89` path, virtual-filter key set provider, tree service) throws on such a slot until #131 explicitly wires the federated binding (A-8 keeps parameter zones rejected in the planner until then); (e) the mapped value is looked up by exact ordinal match after a length cap of 64 and never trimmed or normalized.
- **BC-130-6 (SEC-130-06):** supply-chain hardening beyond the hash: (a) the CLDR `windowsZones.xml` source and the generator script are checked in under a test-only or tools folder with their own SHA-256, and a unit test regenerates `iana-windows-zones.txt` from it and compares byte for byte (reproducibility); (b) a CI-only test cross-checks **every** entry against `TimeZoneInfo` (Windows id resolvable, UTC offsets of the IANA id and the Windows id equal at the four 2026 transition instants and mid-winter/mid-summer) and fails the build on any mismatch (the runtime cross-check stays warning-only); (c) `iana-windows-zones.txt` and the hash constant are listed in CODEOWNERS (security owner) so a data change cannot be merged without review; (d) the hash constant and the resource change only in the same commit as a documented CLDR version bump; (e) the resource is embedded, loaded with `GetManifestResourceStream`, no runtime download, no file path configuration.
- **BC-130-7 (SEC-130-07):** the DuckDB probe (a) builds its connection from `DuckDbOlapEngine.BuildSetupSql` itself (make it reachable to the state class, for example `InternalsVisibleTo` or a shared internal factory), not a copy; (b) in addition to the conversion check asserts `SELECT loaded, install_mode FROM duckdb_extensions() WHERE extension_name = 'icu'` returns loaded and a **statically linked** mode (exact value fixed from the T-1 run and pinned in the test) - an autoload-dependent ICU makes DuckDB unavailable; (c) T-1 additionally asserts that `autoload_known_extensions`/`autoinstall_known_extensions` are still `false` and `enable_external_access` is `false` after the probe, and that `INSTALL icu`/`LOAD icu` still fail under the lock. The probe result is cached for the process lifetime; a failure keeps DuckDB unavailable (fail closed) until restart.
- **BC-130-8 (SEC-130-08):** the validator enforces at most 8 `convert_timezone` calls per statement and a nesting depth of at most 3 (a call inside the `ts` of another call), both `SQL_TIMEZONE_ARGUMENT`; both are constants in the engine (no configuration). The Phase 4 developer verifies that `CommandTimeout` and the result row limit apply to the governed binder path and the federated path unchanged (existing tests referenced in the test list below).
- **BC-130-9 (SEC-130-09):** options validation (startup, fail closed) rejects `WebSql:AdditionalAllowedFunctions` entries equal to `timezone`, `at_timezone`, `with_timezone`, `switchoffset`, `todatetimeoffset`, `timezone_hour`, `timezone_minute`, `convert_timezone` (case-insensitive, any qualification). This does not change `SqlFunctionPolicy` default denylists. Owner flag R-3.
- **BC-130-10 (SEC-130-10):** the inline-constant fallback of A-5 may be used only for **literal** zones, only for a registry-owned string, and only together with a new `EmittedRangeKind.ZoneConstant` that `EmittedSqlInvariantChecker` validates against `TimeZoneRegistry.Contains`-equivalent rules (regex plus membership); parameter zones are never inlined: on a dialect that needs the fallback and cannot match parameter markers, parameter zones are rejected (`SQL_TIMEZONE_ARGUMENT`) for that dialect. Using the fallback requires a Phase 3 re-review note in this plan.
- **BC-130-11 (SEC-130-11):** the text `AT TIME ZONE` (and the DuckDB `TIMEZONE(` call) is emitted only by `FormatConvertTimeZone`; a test greps the generator sources and fuzzes AST shapes to prove no other emitter produces them; `accepted-differences.json`, `Corpus/` golden files and `golden-results/*` stay untouched except for the new `dql.sql` rows allowed by T-34.
- **BC-130-12 (SEC-130-12):** the validator uses the common traversal base so every `FunctionCallExpression` in the statement is reached (all clauses, window specs, subqueries, CTEs, set operations, DML including `INSERT ... SELECT`, `UPDATE SET`, `MERGE`); mandatory test ST-130-9 enumerates positions. The generator guard additionally refuses emission when `!context.IsBound`, when an argument is not literal/zone-parameter, **and** when the statement was not validated (a `bool` on the emitter context set by the compiler/planner after the validator ran; default `false` = throw `SQL_TIMEZONE_ARGUMENT`), so a future emit site that skips the validator fails closed instead of emitting an unchecked `ts`.
- **BC-130-13 (SEC-130-13c):** `SqlTimeZoneException` is audited like the other compile denials (code only), and a test proves that no `catch (Exception)` between the validator/binder and the mapper converts it into another status or leaks its message with data; the audit record contains the code, no zone and no timestamp.
- **BC-130-14 (corpus):** T-34 stays as written and becomes binding: if a new corpus row requires regenerated golden files, a new accepted difference or an edit of `accepted-differences.json`, the developer stops and reports; no such edit is part of this item. The negative rows (operator form, unsupported dialect, unknown zone) must be added with `expected=reject`.

### 5. Mandatory Security Tests (in addition to T-1..T-38; they are release-blocking and are written red first)

| ID | Test | Expectation |
|---|---|---|
| ST-130-1 | Hostile zone literals through the full compile pipeline on SQL Server, PostgreSQL, DuckDB: `Europe/Berlin'; DROP TABLE t--`, `Europe/Berlin/*x*/`, `Europe/Berlin\0`, `' OR 1=1--`, `Europe/Berlin ` , `europe/berlin`, `EST`, Cyrillic lookalike, 65 characters, `{{x}}`, `%`, backslash | `SQL_TIMEZONE_INVALID` at compile time; nothing emitted; fixed message; no input echo |
| ST-130-2 | Hostile zone **parameter** values for the same list, plus `null`, number, bool, array, nested list, empty string | `SQL_TIMEZONE_INVALID`; `command.Parameters.Count == 0` and `CommandText` unchanged after the failed bind (pre-pass, BC-130-5a); the ordinary slot of the same name is untouched |
| ST-130-3 | Emitted SQL invariant: for every dialect and every zone spelling (literal/param/duplicate/nested) the SQL text contains no zone substring and no Windows id; every zone is a bound marker whose value is registry-owned (reference-equal to the registry string); `EmittedSqlInvariantChecker` is green | pass |
| ST-130-4 | Binder-only consumption: a `CompiledSql` with a `ZoneArgument` slot handed to the OLAP generated-query path, the virtual-filter key set provider and the tree service throws before any provider call | throws `InvalidOperationException`/fixed message |
| ST-130-5 | Zone parameter declared as a list (#128) or used with an element index | `SQL_TIMEZONE_ARGUMENT` at compile time (BC-130-5c) |
| ST-130-6 | Name spellings: `"CONVERT_TIMEZONE"(..)`, `Convert_TimeZone(..)`, `dbo.convert_timezone(..)`, `main.convert_timezone(..)`, `x."convert_timezone"(..)` with bad zones and bad `ts` | every spelling that is admitted by the allowlist reaches the validator; the others are rejected by gate 1/2; **none** reaches the generator as a pass-through (assert no emitted `CONVERT_TIMEZONE(` text) |
| ST-130-7 | Unlisted-name pass-through unchanged: with `convert_timezone` allowlisted, `timezone('x', ts)`, `at_timezone`, `switchoffset`, `todatetimeoffset`, `timezone_hour` are rejected by gate 1 and 2 in every dialect (also as `AdditionalAllowedFunctions` entries at startup, BC-130-9) | rejected |
| ST-130-8 | Row policies: `RowPolicyFactory`/`PolicyParserSetup`/`PolicyExpressionParser` reject a predicate containing `convert_timezone`; the policy lint reports it; virtual filter list unchanged | rejected (BC-130-2) |
| ST-130-9 | Validator coverage: one test per position (projection, `WHERE`, `HAVING`, `ORDER BY`, `GROUP BY`, window `PARTITION BY`/`ORDER BY`/frame, `CASE`, aggregate argument, aggregate `FILTER`, CTE, derived table, scalar/`IN`/`EXISTS` subquery, set operation arm, `INSERT ... SELECT`, `UPDATE SET`, `MERGE`) each with an **invalid** zone | rejected in all positions with `SQL_TIMEZONE_INVALID`/`ARGUMENT`; a reflection-driven test asserts that the validator overrides/handles every `SqlNode` type that hosts an expression |
| ST-130-10 | Generator bypass guard: hand-built AST with a column zone or an unvalidated statement flag given to each dialect generator and to the federated planner generator call | throws; nothing emitted (BC-130-12) |
| ST-130-11 | Masks: for each `MaskKind` (Nullify, Redact, PartialMask, Hmac, GeoJitter, Constant) x dialect (SQL Server, PostgreSQL, DuckDB federated staging) with a mask on a timestamp column: `convert_timezone(.., t.col)` as projection, `WHERE`, `ORDER BY`, `GROUP BY`, `HAVING` | masked column as `ts` rejected with `SQL_TIMEZONE_ARGUMENT` (BC-130-1); an unmasked column of the same table accepted; the same statement for a user without the mask succeeds; the response of the two failing cases is identical for every mask kind (no mask-kind oracle); the emitted SQL never references the raw column outside the secured derived table |
| ST-130-12 | `AT TIME ZONE` operator in projection, `WHERE`, `ORDER BY`, inside `CAST`, in a CTE, in a policy predicate, in a virtual filter, in a federated statement, in the WebSQL pre-analysis, with and without `convert_timezone` in the same statement | rejected everywhere with the existing code; fixture `80d5942dea95` unchanged |
| ST-130-13 | Emit-site uniqueness (BC-130-11): text `AT TIME ZONE` appears in generator sources only in `SqlDialectGeneratorBase.TimeZone.cs`/the three overrides; no AST built from a statement without `convert_timezone` emits it | pass |
| ST-130-14 | Federated entries: `Plan` with a supplied `statement`, without one, and the dry-run entry (`FederatedDuckDbExecutionService.cs:230`) each reject a column zone, a bare column `ts`, a parameter zone, a masked column, and the unavailable state; a CTE containing the call is checked | all rejected, identical codes (BC-130-4) |
| ST-130-15 | DuckDB sandbox: after `BuildSetupSql` the probe passes, ICU is reported loaded and statically linked, `autoload`/`autoinstall` stay `false`, `INSTALL icu` and `LOAD icu` fail; a simulated "ICU unavailable" probe makes DuckDB unavailable and federated calls return `SQL_TIMEZONE_UNAVAILABLE` (not an execution-time provider error) | BC-130-7 |
| ST-130-16 | Registry supply chain: regeneration from the checked-in CLDR source is byte-identical (BC-130-6a); CI cross-check of all entries against `TimeZoneInfo` (BC-130-6b); a deliberately swapped mapping fails the cross-check and the reproducibility test even when the hash constant is updated to match | red on tamper |
| ST-130-17 | DoS caps: 9 calls, nesting depth 4, and a 3000-call generated statement | rejected statically (BC-130-8), no allocation growth proportional to the input before rejection, G8 gate unchanged for statements without the function |
| ST-130-18 | Audit and exception path: a rejected conversion appears in the compile audit with the code only; no `catch` converts `SqlTimeZoneException` into a 403/500; log/span/audit capture contains neither zone nor timestamp (extends T-28) | pass (BC-130-13) |

### 6. Sufficiency of the Planned Tests

The planned list is strong on registry, validator shapes, binder and golden SQL, and correctly refuses to edit the gate artefacts. It is **not sufficient** for release in these respects: no test for quoted/qualified name spellings (ST-130-6), no mask-kind matrix and no clause matrix (T-36 covers one case; ST-130-11), no coverage of every expression position and DML (ST-130-9), no test that other `CompiledSql.Parameters` consumers cannot see zone slots (ST-130-4), no federated entry-point matrix and no check of the "statement supplied" path (ST-130-14), no policy-predicate rejection (ST-130-8), no proof of the ICU load mode (ST-130-15), no supply-chain reproducibility (ST-130-16), no DoS caps (ST-130-17), and the failed-bind "command untouched" claim of T-19 is not achievable without the pre-pass (ST-130-2). The 18 ST tests close these gaps.

### 7. Recommendations for Owner Flags

- **R-1 (replaces F-2):** accept "masked columns are not accepted as `ts`" (BC-130-1) instead of "the function sees the masked projection". It is stricter, deterministic across mask kinds and dialects, and removes the mask-kind error probe. If the owner wants masked timestamps convertible later, it needs a per-mask-kind type contract and is a separate item.
- **R-2 (Q-130-3, decision confirmed):** keep the embedded intersection list; additionally approve BC-130-6 (CODEOWNERS, reproducible generation, CI full cross-check).
- **R-3 (new):** approve startup rejection of time-zone-related names in `WebSql:AdditionalAllowedFunctions` (BC-130-9). Without it an operator can re-open unvalidated zone functions; with it, a configuration that used one of those names before fails at startup (no known use; check deployment configs before release).
- **R-4 (F-1, F-3, F-4):** accepted as written; F-1 owner review at Phase 5 should focus on the validator, the binder pre-pass and the generator guard.
- **R-5 (Q-130-4):** keep the degradation rule (no DuckDB if T-1 or the BC-130-7 probe fails); the owner is told in the Phase 4 report which outcome occurred.
- **R-6 (new):** the per-statement caps of BC-130-8 (8 calls, depth 3) are product limits; the owner may raise them, never remove them.

### 8. Verdict

**APPROVED WITH CONDITIONS.** The design is fundamentally safe: zone text never reaches SQL text, the mapped value is always bound, the closed set fails closed, the `AT TIME ZONE` operator stays rejected, the new name does not widen any other function, and nothing in the revision-1 text needs to be reverted. The plan may proceed to the owner decision on the flags and then to Phase 4 **only after** the 14 binding conditions are folded into revision 2 (or are stated as part of the Phase 4 brief) and the 18 mandatory security tests are part of the red-first list. Release is blocked until BC-130-1 (masked `ts`), BC-130-2 (policy exclusion), BC-130-3 (normalized name), BC-130-4 (planner placement), BC-130-5 (binder pre-pass and consumers), BC-130-6 (resource provenance) and BC-130-7 (ICU probe) are verified in the Phase 5 review; the remaining conditions are verified there as well but none is an architectural change. Additional note for the lead: add `src/Autheris.Application/Policy/RowPolicyFactory.cs`, `src/Autheris.Application/Policy/Lint/PolicyParserSetup.cs`, the options validator for `RestApiOptions` and `src/Autheris.Application/Sql/Services/FederatedDuckDbExecutionService.cs` (read-only check) to the shared-file list of delivery step I-6.

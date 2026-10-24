# Plan: PostgreSQL parity fixes (issues #333, #334, #335)

## 1. Requirements (Phase 1)

### Problems
- **#333** GraphQL `timeBuckets` return INTERNAL_SERVER_ERROR on PostgreSQL. `GovernedTableReadPlanner.BuildTimeBucketExpression` emits native `date_trunc('u', "c" AT TIME ZONE 'UTC')` (SQL Server branch: `AT TIME ZONE` with `[c]`). The planned SQL is compiled by the Trino AST compiler, and `SqlAstBuilder` has no `atTimeZone` visitor, so `AstBuildException` surfaces as an internal fault. A second defect follows: `BuildBucketsFromPlannedSqlResult` passes the raw driver `DateTime` bucket key into the GraphQL `String` key field (SQLite returns text, so it passes there).
- **#334** WebSQL writes PG `timestamp` as `2023-06-01T08:30:00.0000000` (no UTC designator). `WriteDbValue` uses `ToString("o")` and Npgsql returns Kind `Unspecified`. REST and GraphQL already treat `Unspecified` as UTC.
- **#335** WebSQL writes PG `date` (`DateOnly`) through the default `val.ToString()` arm, giving `06/01/2023`.

### Acceptance criteria
- REQ-PGP-01: Time-bucket planning for PostgreSQL and SQL Server produces SQL that the Trino AST compiler accepts and the dialect generator translates. No construct without an AST visitor reaches the compiler.
- REQ-PGP-02: GraphQL `timeBuckets` (year, month, day, plus all other supported units) return correct buckets on PostgreSQL and SQL Server, matching SQLite semantics (UTC bucketing).
- REQ-PGP-03: Bucket keys from the planned-SQL path are normalised to the same canonical ISO 8601 UTC string as the SQLite path, independent of driver type (`DateTime` Kind Unspecified/Utc/Local, `DateTimeOffset`, `DateOnly`, text).
- REQ-PGP-04: An unsupported unit or time zone yields a 4xx GraphQL/validation error with a stable code, never an internal fault. Unexpected AST build failures on planner output are not leaked as raw exception text.
- REQ-PGP-05: WebSQL writes `DateTime` as ISO 8601 UTC with `Z` (`2023-06-01T08:30:00Z`-style, InvariantCulture). `Unspecified` is treated as UTC, `Local` is converted to UTC, `Utc` is kept. Same for `DateTimeOffset` (converted to UTC).
- REQ-PGP-06: WebSQL writes `DateOnly` as `yyyy-MM-dd`, InvariantCulture.
- REQ-PGP-07: Every remaining culture-sensitive arm in `WriteDbValue` (`TimeOnly`, `TimeSpan`, other `IFormattable`) formats with InvariantCulture and a fixed ISO format. No `CurrentCulture` dependency.
- REQ-PGP-08: Tests: unit tests that compile the planned time-bucket SQL through the real AST compiler (not string compare only) for PG and SQL Server, culture-switching tests for WebSQL formatting (e.g. de-DE, tr-TR), and the nightly PostgreSQL parity cells for #333-#335 turn green.

### Non-goals
- No other parity follow-ups (PARITY-FU-003, test diagnostics). No new time zones or bucket units. No general rework of the Trino AST compiler beyond what REQ-PGP-01 needs. No change to REST/GraphQL timestamp handling that already works.

### Anti-patterns rejected
- Bypassing the AST compiler with raw/unparsed SQL for planner output (breaks governance and fail-closed).
- Catching all exceptions and returning an empty or partial bucket list.
- Adding a `Z` by string-append after `ToString()` or using the current culture/`ToString()` defaults.
- Fixing only the PostgreSQL branch while SQL Server keeps the same latent defect.

### Open questions
1. Fix via engine-canonical Trino SQL (e.g. `date_trunc('u', col)` for UTC, a supported zone conversion otherwise) or via an `atTimeZone` visitor in `SqlAstBuilder`? Default: canonical Trino SQL, because the rewriter already translates date functions. Add the visitor only if needed for non-UTC zones.
2. Fractional seconds in `Z` timestamps: keep the 7-digit `o` precision or trim? Default: keep `o` precision with `Z` (stable and lossless), unless existing cells expect otherwise.

## 2. Architecture (Phase 2, csharp-architect, 2026-10-24)

Verified against origin/main f065cb97. Q1 decided: canonical Trino SQL, no `atTimeZone` visitor. Q2 decided (PM): trim trailing zero fractions.

### 2.1 Root causes
1. **#333 compile failure.** `GovernedTableReadPlanner.BuildTimeBucketExpression` (src/Autheris.Application/Data/Services/GovernedTableReadPlanner.cs:573-647) emits `date_trunc('u', "c" AT TIME ZONE 'z')` for PG (:606) and `DATETRUNC(u, [c] AT TIME ZONE 'z')` for SQL Server (:616, with `dialect.QuoteIdentifier` at :589, so it uses brackets that Trino cannot parse). `GovernedSqlRewriter.CompileStatement` compiles the result with `translateDateFunctions: true` (GovernedSqlRewriter.cs:776). `SqlAstBuilder` has no `atTimeZone` visitor (SqlBase.g4:631), so it throws `AstBuildException`, which is logged at :785-791 and rethrown.
2. **#333 5xx mapping.** The GraphQL aggregate resolver (AggregateGraphQlTypes.cs:240) maps through `AggregateFailureMapper.ForAggregate` (AggregateErrors.cs:49-63). `AstBuildException`, `SqlTimeZoneException` and `SqlCompileNotSupportedException` are not mapped there, so they reach `ErrorSanitizingFilter` as INTERNAL_SERVER_ERROR. REST is unaffected because `GatewayExceptionHandler` uses `SqlCompilerErrorMapper` (422/400).
3. **#333 latent unit gaps.** `TimeBucketInterval.Quarter` is accepted by GraphQL (planner :584, SQLite #325), but `DateUnit` (Expressions.cs:278) has no `Quarter`, and `ParseDateUnit` (SqlAstBuilder.cs:1057) rejects it. `DateUnitName` (SqlDialectGeneratorBase.Functions.cs:408) has a `_ => "year"` default that would silently turn a new unit into `year`. `SqlServerDialectGenerator.FormatDateTrunc` (:222-246) supports only Day, Month and Year.
4. **#333 keys.** `BuildBucketsFromPlannedSqlResult` (GovernedAggregationService.Buckets.cs:182) stores the raw driver value. The GraphQL `ConvertKey` (AggregateResultMapper.cs:167-186) already normalises DateTime-kind keys (`date` maps to DateTime, ColumnValueKind.cs:69). The REST group key, however, is the raw `DateTime`/`DateTimeOffset`, while SQLite returns text.
5. **#334/#335.** `WebSqlEndpoints.WriteDbValue` (WebSqlEndpoints.Execution.cs:374-424; call sites :240, :250) has these arms. `DateTime` uses `dt.ToString("o")` (:409), so Npgsql's Unspecified value gets no `Z`. `DateTimeOffset` uses `"o"` (:412). `DateOnly`, `TimeOnly`, `TimeSpan` and other values fall to `val.ToString()` (:421), which uses the current culture.

### 2.2 Fix design
1. **Planner (PG and SQL Server branches only; SQLite and default unchanged).** Add a `string dataType` parameter to `BuildTimeBucketExpression` (call site :198 passes `metadata.GetColumn(gCol)!.DataType`). Quote with `DataApiFilterTranslator.QuoteIdentifier`. The source expression `src` is:
   - `CAST("c" AS timestamp)` when `ColumnValueKinds.IsDateOnly(dataType)`;
   - `"c"` when `PlainTimestampClassifier.IsPlain(SqlDialectMapper.ToTargetDialect(dialect), dataType)`;
   - otherwise (zoned or unknown types: timestamptz, datetimeoffset, text) it throws `ArgumentException` (400). These types previously failed with 5xx, and their results would depend on the session time zone.
   The expression is `date_trunc('<unit>', src)` for UTC, and `date_trunc('<unit>', convert_timezone('UTC', '<zone>', src))` otherwise. `<zone>` is emitted only after `TimeZoneRegistry.Contains` (an ordinal key match, max 64 characters) and with `'` doubled. `<unit>` comes only from the existing closed switch. The zone is bound by `AppendZoneArgument`. `TimeZoneCallValidator` (BC-130-1, masking) is applied unchanged. Do not relax any allow-list.
2. **Engine `Quarter` (only for `date_trunc`).** Add `DateUnit.Quarter`. `ParseDateUnit` accepts `"quarter"` only when `function == "date_trunc"`. `date_add` and INTERVAL stay rejected, which keeps DateFunctionTranslationTests.cs:103 green. `DateUnitName` gets an explicit `Quarter => "quarter"` arm, and its default arm becomes `throw UnsupportedDateFunction`. The PG, Analytical and Databricks generators need no change (they use `DateUnitName`). Oracle adds `Quarter => "Q"`. SQLite keeps the throw in its default arm (4xx). Every `DateUnit` switch in src/TrinoSqlEngine is reviewed (9 files: grep `DateUnit`).
3. **SQL Server `FormatDateTrunc`.** Add arms that complete the GraphQL unit set, all as `CAST(... AS datetimeoffset)` like the existing arms:
   - Hour: `DATEADD(hour, DATEPART(hour, x), CAST(CAST(x AS date) AS datetime2))`.
   - Week: the ISO Monday, independent of `@@DATEFIRST`/LANGUAGE, `DATEADD(day, -((DATEDIFF(day, CAST('19000101' AS date), CAST(x AS date)) % 7 + 7) % 7), CAST(x AS date))`.
   - Quarter: `DATEFROMPARTS(YEAR(x), (MONTH(x) - 1) / 3 * 3 + 1, 1)`.
   Fragments use `AppendStructural`/`AppendConstantFragment` like the Month arm.
4. **GraphQL mapping.** In `AggregateFailureMapper.ForAggregate`, before `ForDataAccess`, add these arms:
   - `SqlTimeZoneException { Code: TimeZoneUnavailable }` maps to `UNAVAILABLE`.
   - Any other `SqlTimeZoneException`, `AstBuildException` and `SqlCompileNotSupportedException` map to `AggregateErrors.ArgumentInvalid()` (`AGGREGATION_ARGUMENT_INVALID`, a fixed message that is already whitelisted).
   No exception text is passed through.
5. **Planned-path keys.** `BuildBucketsFromPlannedSqlResult` gets `query.TimeBuckets` (call site ValidationAndExecution.cs:390). For bucketed columns only, it writes `DateTime` (any Kind), `DateTimeOffset` (`.DateTime`, the wall clock) and `DateOnly` as invariant `yyyy-MM-dd'T'HH:mm:ss`. Null stays null; other types are unchanged. The value is the wall clock of the bucket zone, the same semantics as the SQLite branch (zone-local text). GraphQL output stays `...T00:00:00.0000000Z` through `ConvertKey`, which is identical to SQLite. Parity matching is by prefix (RobinClient.cs:359).
6. **WebSQL writer.** Add the new `internal static class WebSqlValueWriter` (src/Autheris.Api/Endpoints/WebSqlValueWriter.cs) with `Write(Utf8JsonWriter, object?)`. It moves all existing arms unchanged and replaces the temporal and default arms:
   - `DateTime`: Unspecified is treated as UTC (`SpecifyKind`), Local is converted with `ToUniversalTime` (which clamps and does not throw), Utc is kept.
   - `DateTime` and `DateTimeOffset` (`.UtcDateTime`) are written with `yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'` and InvariantCulture. This is the same format as GovernedTreeQueryService.cs:915; there is no shared helper, so the format string is a `const`.
   - `DateOnly`: `yyyy-MM-dd`. `TimeOnly`: `HH:mm:ss.FFFFFFF`. `TimeSpan`: `c`.
   - Other `IFormattable` values: `ToString(null, InvariantCulture)`. For non-`IFormattable` values the existing `ToString()` is kept.
   `WriteDbValue` is deleted, and :240 and :250 call the new writer. REST (`"O"`) is not changed (non-goal).

### 2.3 Tests (Red first)
- WP1 Unit (tests/Autheris.Tests.Unit):
  - Update TimeBucketingQueryPlanningTests.cs:40-45 and :85-86, and TimeBucketingAggregationSecurityTests.cs:209-210, to the canonical text.
  - New `Data/TimeBucketingPlannedSqlCompileTests.cs`. It compiles `planned.Sql` through the real `TrinoSqlEngine` using `CompileRequestFactory.Create(..., translateDateFunctions: true)`, with time zones available and an `InMemoryTableCatalog` that has typed columns (harness as in ConvertTimeZoneWebSqlTests.cs:93-104). Tests:
    - `PlannedBucket_Compiles(dialect ∈ {PG, SqlServer}, unit ∈ all 6, zone ∈ {UTC, Europe/Berlin})`: no exception, and PG contains `DATE_TRUNC('<unit>'`.
    - `PlannedBucket_DateColumn_CastsToTimestamp`.
    - `PlannedBucket_ZonedColumn_Throws400`.
    - `PlannedBucket_UnknownZone_ThrowsArgument`.
    - `PlannedBucket_ZoneLiteral_IsRegistryKeyOnly`.
  - `PlannedTimeBucketKey_IsInvariantIsoText`: de-DE, with `DateTime` Unspecified, `DateTimeOffset` and `DateOnly` keys giving `2023-01-01T00:00:00`, and null staying null.
  - `AggregateFailureMapper_CompilerErrors_Are4xx`: `AstBuildException`, `SqlTimeZoneException` (each code) and `SqlCompileNotSupportedException` map to `AGGREGATION_ARGUMENT_INVALID`/`UNAVAILABLE`, and the message never contains the exception text.
- WP1 Engine (tests/TrinoSqlEngine.Tests): `DateTrunc_Quarter_PostgreSql`, `DateTrunc_Quarter_SqlServer`, `DateTrunc_HourWeek_SqlServer` (exact text, no `@@DATEFIRST`), `DateTrunc_Quarter_Sqlite_ThrowsAstBuild`, `DateAdd_Quarter_StillRejected`, `DateUnitName_CoversEveryEnumValue` (reflection over `Enum.GetValues<DateUnit>()`).
- WP2 (new tests/Autheris.Tests.Unit/Api/WebSqlValueWriterTests.cs): a theory under de-DE and tr-TR.
  - `DateTime` Unspecified 2023-06-01 08:30 gives `2023-06-01T08:30:00Z`.
  - Local gives the UTC instant with `Z`. Ticks give `...:00.1234567Z`.
  - `DateTimeOffset` +02:00 gives UTC `Z`.
  - `DateOnly` gives `2023-06-01`; `TimeOnly` gives `08:30:00`; `TimeSpan` gives `1.02:03:04`.
  - `decimal`/`double` stay invariant.
  - `DateTime.MinValue`/`MaxValue` (all Kinds) and `DateTimeOffset.MinValue`/`MaxValue` do not throw.
- E2E: the nightly PostgreSQL parity cells for #333-#335 (GraphQL year/month/day, Matrix `group_date_*`, WebSQL timestamp and date) turn green in CI. The SQLite parity cells stay green.

### 2.4 Work packages
| WP | Files (disjoint) | Parallel |
|---|---|---|
| WP1 (#333) | GovernedTableReadPlanner.cs, GovernedAggregationService.Buckets.cs, GovernedAggregationService.ValidationAndExecution.cs, Autheris.GraphQL/Catalog/AggregateErrors.cs, TrinoSqlEngine: Ast/Nodes/Expressions.cs, Ast/Builder/SqlAstBuilder.cs, Ast/Generators/{SqlDialectGeneratorBase.Functions, SqlServer, Oracle}DialectGenerator.cs (and other `DateUnit` switches found by grep); tests listed in WP1 | yes, with WP2 |
| WP2 (#334/#335) | WebSqlEndpoints.Execution.cs, new WebSqlValueWriter.cs, new WebSqlValueWriterTests.cs | yes, with WP1 |

### 2.5 Behaviour changes (release note)
- GraphQL and REST time buckets work on PostgreSQL and SQL Server; they were 5xx before. Planned-path REST bucket keys are `yyyy-MM-ddTHH:mm:ss` text.
- On PostgreSQL and SQL Server, a time bucket on a zoned or non-temporal column is a 400. On GraphQL, a compiler or time-zone refusal on the aggregate path is `AGGREGATION_ARGUMENT_INVALID` (or `UNAVAILABLE`) instead of INTERNAL_SERVER_ERROR.
- `date_trunc('quarter', …)` is now accepted in governed WebSQL on dialects that support it. Elsewhere it gives a 4xx (SQL_CONSTRUCT_NOT_SUPPORTED, 422).
- WebSQL writes timestamps as UTC ISO 8601 with `Z` and no trailing zero fraction. Dates are `yyyy-MM-dd`. Time, interval and number values are culture-invariant.

## 3. Phase 3 security review (csharp-security-expert, 2026-10-24)

**Verdict: APPROVED WITH CONDITIONS.** Design is fail-closed and shrinks the attack surface (no raw AST fault leaks, closed unit enum, registry-only zone literal). PM defaults: (1) zoned/non-temporal column returns 400 is KEPT (session-zone dependence would be silently wrong, fail closed is correct); (2) exact-text-only tests for SQL Server hour/week/quarter is OVERRIDDEN, see SEC-PGP-05.

### Binding conditions
- SEC-PGP-01 (WP1, unit/zone injection): `<unit>` is emitted only from a `switch` over `TimeBucketInterval` with an explicit arm per value and a throwing default; never from request text or `ToString()`. `<zone>` is emitted only as the registry-resolved canonical value (not the caller string, no case-folding differences), after `Contains`, length <= 64, `'` doubled; the column identifier goes only through `QuoteIdentifier`. Both literals are built from planner state, never concatenated from `dataType`.
- SEC-PGP-02 (WP1, fail closed): `metadata.GetColumn(gCol)` null, empty/unknown `dataType`, or a type neither date-only nor `PlainTimestampClassifier.IsPlain` throws `ArgumentException` with a fixed message that contains no column name, type text, zone or provider text. No fallback to the unwrapped column or to session time zone.
- SEC-PGP-03 (WP1, mapper): `AggregateFailureMapper` returns only fixed codes/messages for `AstBuildException`, `SqlTimeZoneException`, `SqlCompileNotSupportedException`; the exception message, inner exception and SQL text go to neither the GraphQL error, `extensions`, nor logs at >= Information (log type name and code only). `TimeZoneUnavailable` is the only `UNAVAILABLE` mapping; every other path stays 4xx and is never an unmapped throw.
- SEC-PGP-04 (WP1, engine): `date_trunc('quarter')` is accepted only for the `date_trunc` function; `DateUnitName` has no default arm falling through to unit text (explicit arm per enum value, throw on unknown). In governed WebSQL a dialect without quarter support must return 4xx, never pass the unit text through to the provider. The tenant/principal table allow-list and row-filter wrapping must still apply unchanged to bucketed queries (no new bypass path).
- SEC-PGP-05 (WP1, SQL Server, overrides default 2): exact-text tests are insufficient for week/hour/quarter; add a semantic assertion that the generated text contains no `@@DATEFIRST`, `SET LANGUAGE`, `FORMAT(` or culture-dependent conversion (`CONVERT` with style-less string to date), and that the date literal is the culture-neutral `'19000101'`. A SQL Server Testcontainers cell for the three units (Monday week, Dec/Jan boundary, quarter start) is recommended in the nightly parity set (non-blocking for merge, blocking for the issue close).
- SEC-PGP-06 (WP1, keys): planned-path keys are formatted with `CultureInfo.InvariantCulture` and a fixed format; `DateTimeOffset` uses the wall clock only because zoned columns are rejected, so the key stays the bucket-zone local time like SQLite; non-temporal values in a bucketed column fall to a fixed error, never `ToString()` with current culture. No value is written to logs.
- SEC-PGP-07 (WP2, writer): all temporal arms use `CultureInfo.InvariantCulture` and `const` formats (`yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'`, `yyyy-MM-dd`, `HH:mm:ss.FFFFFFF`, `c`); `Local` converts via `ToUniversalTime`; Min/Max values must not throw (`DateTimeOffset.UtcDateTime` is safe, `DateTime.MinValue` Local conversion clamps). The non-`IFormattable` `ToString()` fallback must be replaced by a type-name-free fixed handling (string/bytes/Guid/bool arms stay) or `Convert.ToString(v, InvariantCulture)`; no exception text from the writer reaches the stream (writer exceptions are caught upstream and mapped to the existing sanitized error). `Write` must be total, side-effect free and allocate no more than O(value size) (no unbounded string build for large byte[]; keep the existing byte[] arm limits unchanged).

### Mandatory security tests (`[Trait("Category","Security")]`)
- WP1: `PlannedBucket_UnitText_ComesOnlyFromEnum`; `PlannedBucket_ZoneLiteral_IsRegistryCanonicalKey_QuotesEscaped` (includes `Europe/Berlin'); DROP TABLE--`, expects 400, no SQL); `PlannedBucket_UnknownOrNullColumnMetadata_Throws400_FixedMessage`; `PlannedBucket_ZonedColumn_Throws400_NoTypeLeak`; `AggregateFailureMapper_CompilerErrors_NoExceptionTextInResponse` (sentinel string in exception message must be absent from error and extensions); `DateUnitName_NoDefaultFallthrough_ThrowsOnUnknownEnum`; `DateTrunc_Quarter_UnsupportedDialect_Is4xx_NoUnitTextToProvider`; `SqlServerDateTrunc_WeekHourQuarter_NoSessionDependentConstructs`; `BucketedAggregation_TenantRowFilter_StillApplied_PgSqlServer`; `PlannedTimeBucketKey_InvariantUnderTrTrAndDeDe_NoLogLeak`.
- WP2: `WebSqlValueWriter_TemporalArms_CultureInvariant_TrTrDeDe`; `WebSqlValueWriter_MinMaxValues_AllKinds_NoThrow`; `WebSqlValueWriter_UnknownType_NoTypeNameOrExceptionLeak`; `WebSqlValueWriter_LocalKind_ConvertsToUtcZ`.

## Changelog (Phase 4)
- 2026-10-24 (WP1): drift vs plan: existing conformance fixtures (SqlServer `date-translated-004/-005`) and tests that asserted SQL Server `date_trunc` hour/week rejection were updated to the new supported output (`DateFunctionTranslationTests`, `SqlFilterDefinitionTests` now use `second`). `GovernedSqlRewriter` already logs through `SafeLog` (type name only), so no change was needed for SEC-PGP-03 logging. Non-temporal planned-path bucket values (for example SQLite text) pass through unchanged, as in plan 2.2 step 5.

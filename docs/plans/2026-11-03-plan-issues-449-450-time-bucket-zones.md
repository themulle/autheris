# Plan: Time bucket key offsets and zoned columns (#449, #450)

Date: 2026-11-03. Phase 2 (architect). PRD: [`2026-11-03-req-issues-449-450-time-bucket-zones.md`](2026-11-03-req-issues-449-450-time-bucket-zones.md) (REQ-TBZ-01..14). Builds on [`2026-10-24-plan-issues-333-335-postgres-parity.md`](2026-10-24-plan-issues-333-335-postgres-parity.md) (SEC-PGP-01..06). Verified against `main` @ `50d89ec9`.

Constraints: Autheris is not in production, so output changes are allowed. No schema change: REQ-TBZ-14 is satisfied because the type classification uses the existing catalog `DataType`. Any later schema need is forward-only on the frozen v1.5.0 base. Delivery: WP1 (#449) first, then WP2 (#450) rebased on WP1, in the same lane.

## Verified code facts

1. `GovernedAggregationService.Buckets.cs:182,207-213`: `NormalizePlannedBucketKey` formats `yyyy-MM-dd'T'HH:mm:ss` without an offset, and SQLite string keys pass through unchanged (`'2026-10-05'`).
2. `AggregateResultMapper.ConvertKey` (`AggregateResultMapper.cs:167`, `CatalogFieldType.DateTime`) calls `TryDateTime`. That method treats `Unspecified` as UTC (`:303-315`) and emits `UtcDateTime.ToString("O")`. This is the `Z` reported in #449.
3. In-memory path `TruncateValueToTimeBucket` (`Buckets.cs:769`) has four defects:
   - (a) It uses the row's offset, not the offset at the bucket start, so a DAY bucket on a DST-change day splits into two keys.
   - (b) `DateTimeOffset.TryParse(..., DateTimeStyles.None)` reads offset-less strings as server-local time.
   - (c) `catch { tzi = Utc }` fails open.
   - (d) Non-temporal values pass through silently.
4. `GovernedTableReadPlanner.BuildTimeBucketExpression` (`:579-668`) has different behavior per dialect:
   - PG/MSSQL: Trino canonical `date_trunc(unit, convert_timezone('UTC', zone, src))`. A zoned, unknown or non-temporal type throws `ArgumentException`, which surfaces as `AGGREGATION_ARGUMENT_INVALID` (`AggregateErrors.cs:65`). A `date` column is cast to timestamp and still zone-shifted, which can move it to the previous day in western zones.
   - SQLite: `SqliteZoneOffsetTable` arms. WEEK emits `strftime('%Y-%W')`, which is not a date (no parity with ISO Monday).
   - `default` (Databricks, Oracle): `date_trunc(unit, col)` silently ignores the zone.
5. The engine's `convert_timezone` is 3-argument only:
   - Validator `TimeZoneCallValidator.CheckTimestamp` requires `PlainTimestampClassifier.IsPlain` (`:155-187`).
   - Generator base `SqlDialectGeneratorBase.TimeZone.cs` requires `Arguments.Count == 3`.
   - Emitter binds zone arguments with deduplication (`SqlEmitterContext` dedup), so the SELECT and GROUP BY copies share one parameter.
   - Statement cap: `MaxCallsPerStatement = 8`.
6. `TimeBucketSpecification` is created only in `AggregateArgumentParser.cs:198` (GraphQL). REST and WebSQL have no `timeBuckets` surface. `AggregationQuery.TimeBuckets` exists, but no API DTO maps it.

## Key contract (REQ-TBZ-01..06)

A new `src/Autheris.Application/Analytics/TimeBucketKeys.cs` (internal static) is the only producer of bucket keys. Both paths call it.

- Timestamp/zoned column: `yyyy-MM-dd'T'HH:mm:sszzz`, `CultureInfo.InvariantCulture` (`2026-10-05T00:00:00+02:00`). With no zone the key ends in `+00:00`, not `Z` (one format; D1).
- `date` column: `yyyy-MM-dd`. The zone is ignored, because a date has no instant (D3). HOUR on a date column yields the date key.
- `FormatInstant(DateTimeOffset start, TimeZoneInfo zone)`: `TimeZoneInfo.ConvertTime`, then format. Used for HOUR keys and the in-memory path.
- `FormatWall(DateTime wallStart, TimeZoneInfo zone)` resolves the offset for DAY+ on the SQL path:
  - Ambiguous wall time: the offset of the first occurrence, which is the earlier instant (largest offset).
  - Invalid wall time (gap): the offset in effect just before the transition, so the key denotes the transition instant.
- `ResolveZone(string id)`: `TimeZoneRegistry.Contains`, then `FindSystemTimeZoneById`. On failure it throws the same `ArgumentException` as `SqliteZoneOffsetTable.Get`. There is no UTC fallback (fail closed).
- HOUR buckets are grouped by the zone-aligned UTC instant, not the wall hour. Grouping by wall hour merges the two DST-fold hours in SQL (D2).
  - The bucket instant is `date_add('minute', -m, date_trunc('hour', date_add('minute', m, U)))`. `U` is the UTC plain timestamp and `m` is the zone's offset minute part (`offset mod 60`, 0..59), constant over the `SqliteZoneOffsetTable` year range.
  - For `m = 0` (almost all zones) it is plain `date_trunc('hour', U)`, with no `convert_timezone` call.
  - When the minute part varies (for example `Australia/Lord_Howe`), HOUR falls back to wall-hour grouping with `FormatWall` (documented limit).
  - New helper: `ZoneHourAlignment.TryGetMinutePart(zoneId, out int m)` in `Application/Data/Services`. It reuses the `SqliteZoneOffsetTable.Get` arms.
- `PlannedTableAggregate` (`IGovernedTableReadPlanner.cs:58`) gets `IReadOnlyDictionary<string, TimeBucketKeyShape> BucketKeyShapes`, with values `UtcInstant | ZoneWall | Date`.
  - `NormalizePlannedBucketKey(object? value, TimeBucketSpecification spec, TimeBucketKeyShape shape)` replaces the single-argument version.
  - Driver `DateTime`/`DateTimeOffset`/`DateOnly`/SQLite text is parsed invariantly (text: `AssumeUniversal` for `UtcInstant`, wall otherwise). It is then formatted through `TimeBucketKeys`.
- Ordering (REQ-TBZ-04):
  - The in-memory order selector (`Buckets.cs:575-596`) maps a bucket-column key to `DateTimeOffset.UtcTicks` through `ParseExact` of the fixed formats, so fold hours sort chronologically.
  - SQL `ORDER BY` uses the bucket expression, which is chronological for both HOUR (instant) and DAY+ (wall time).
- GraphQL (REQ-TBZ-04): `AggregateResultMapper.Map` gets `IReadOnlySet<string> bucketColumns` (from `parsed.TimeBuckets`, caller `AggregateGraphQlTypes.cs:238`).
  - For those columns, `ConvertKey` returns the key string verbatim. A non-string value throws `ResultTypeMismatch`.
  - Non-bucket `DateTime` keys are unchanged.
  - REST has no time buckets (fact 6), so there is no REST key change.

## Zoned columns and unsupported code (REQ-TBZ-07..11)

- Engine: `PlainTimestampClassifier.IsZoned(dialect, type)`.
  - SqlServer: `datetimeoffset`.
  - PostgreSql and DuckDb: `timestamptz` and `timestamp with time zone`, precision stripped.
  - Anything else returns false.
- Engine: a two-argument `convert_timezone(to_zone, zoned_ts)` (Snowflake semantics) alongside the three-argument form.
  - The validator accepts 2 arguments only when the timestamp argument is a bare, unmasked column with `IsZoned`. The 3-argument form keeps `IsPlain`. Zone-argument rules and caps are unchanged.
  - The generator base accepts 2 or 3 arguments. A new virtual `FormatConvertTimeZoneZoned` emits:
    - PG: `((ts) AT TIME ZONE $to)`.
    - MSSQL: `CAST((ts) AT TIME ZONE $to AS datetime2)`.
    - Both forms are independent of the session time zone because the input is an instant.
  - DuckDB generator and `TimeZoneConversion.ValidateFederated` refuse the 2-argument form (`FunctionUnsupportedDialect` / `TimeZoneArgument`) (D4).
- Planner on PG/MSSQL classifies the column as date, plain or zoned; anything else throws the new exception.
  - Zoned source: the local value is `convert_timezone('<zone>', col)` and `U` is `convert_timezone('UTC', col)`.
  - Plain source: unchanged.
  - Databricks/Oracle: a non-UTC zone is unsupported; UTC is unchanged (D6).
  - SQLite: no type gate, behavior unchanged (D7).
- New code `AggregationErrorCodes.TimeBucketUnsupported = "AGGREGATION_TIME_BUCKET_UNSUPPORTED"` with exception `AggregationTimeBucketUnsupportedException` in `AggregationModels.cs`.
  - The exception does not derive from `ArgumentException`, so the `ArgumentException` arm cannot swallow it.
  - Fixed message: "Time bucketing is not supported for this column."
  - It carries the column name internally and never serializes it.
- Raised at three points:
  - (a) Parser: the bucket field's `FieldType != CatalogFieldType.DateTime` (D8).
  - (b) Planner classification.
  - (c) In-memory path: a non-null value that is neither temporal nor parseable.
- Mapping:
  - `AggregateFailureMapper.ForAggregate` maps it to the new code. Extensions carry `field` (the GraphQL field name, resolved by the GraphQL layer) and `bucket` (enum name) (D5).
  - `ErrorSanitizingFilter` gets an allowlist entry and a fixed message.
  - `AnalyticsEndpoints` maps it to 400 (defensive).
  - WebSQL: none (no producer).
  - Malformed unit or zone stays `AGGREGATION_ARGUMENT_INVALID`.

## WP1 (#449): offset-bearing keys, all paths

Files:
- `Application/Analytics/TimeBucketKeys.cs` (new).
- `Application/Data/Services/ZoneHourAlignment.cs` (new).
- `GovernedAggregationService.Buckets.cs`: normalize, in-memory truncate, order selector.
- `GovernedAggregationService.ValidationAndExecution.cs:449`: pass the shapes.
- `GovernedTableReadPlanner.cs`: HOUR instant alignment; date columns without zone; SQLite WEEK as Monday `date(local,'-6 days','weekday 1')`; shapes.
- `IGovernedTableReadPlanner.cs`.
- `GraphQL/Catalog/AggregateResultMapper.cs`.
- `GraphQL/Catalog/AggregateGraphQlTypes.cs`.

Tests (Red first):
- `Unit/Analytics/TimeBucketKeysTests.cs` (new):
  - `FormatWall_BerlinSummerDay_CarriesPlus0200`
  - `FormatInstant_Utc_UsesPlus0000`
  - `FormatWall_BerlinAutumnFold_UsesFirstOccurrenceOffset`
  - `FormatWall_SantiagoSpringGapMidnight_UsesPreTransitionOffset`
  - `FormatDate_IsWallDateWithoutOffset`
  - `Format_InvariantUnderTrTrAndArSa`
  - `ResolveZone_Unresolvable_FailsClosed_NoUtcFallback`
- `Unit/Data/TimeBucketingPlannedSqlCompileTests.cs`: rewrite the `PlannedTimeBucketKey_*` tests to the three-argument normalize, and add:
  - `PlannedKey_ZoneWallDayBerlin_Issue449_CarriesOffset`
  - `PlannedKey_UtcInstantHour_ConvertsToZoneOffset`
  - `PlannedKey_DateColumn_IsYyyyMmDd`
  - `PlannedBucket_Hour_WholeHourZone_GroupsByUtcInstant_NoConvertCall`
  - `PlannedBucket_Hour_Kolkata_ShiftsByThirtyMinutes`
  - `PlannedBucket_Hour_LordHowe_FallsBackToWallHour`
  - `PlannedBucket_DateColumn_IgnoresZone`
- `Unit/DataApi/GovernedTableReadPlannerSqliteBucketTests.cs`:
  - `SqliteBucket_Week_ReturnsMondayDate`
  - `SqliteBucket_Hour_ReturnsZoneAlignedUtcInstant`
  - `SqliteBucket_DateColumn_IgnoresZone`
- `Unit/Analytics/InMemoryTimeBucketKeyTests.cs` (new):
  - `InMemory_DayBucket_BerlinDstChangeDay_OneKeyWithStartOffset`
  - `InMemory_HourFold_TwoDistinctKeys`
  - `InMemory_OffsetlessString_IsUtcNotServerLocal`
  - `InMemory_UnknownZone_FailsClosed`
  - `Buckets_OrderByBucketKey_FoldHours_Chronological`
- `Unit/GraphQL/AggregateResultMapperTests.cs`:
  - `Map_BucketColumn_PassesOffsetKeyVerbatim`
  - `Map_BucketColumn_NonStringKey_ResultTypeMismatch`
  - `Map_NonBucketDateTimeColumn_Unchanged`
- Parity: `Tests.Parity/Dialects/DialectTimeBucketTests.cs` (PG and MSSQL containers):
  - Update the expected keys to the offset form.
  - Add cell `tb_day_berlin_issue449` (`2026-10-06T22:30Z` maps to `2026-10-07T00:00:00+02:00`).
  - Add cell `tb_hour_berlin_autumn_fold` (`02:00+02:00` and `02:00+01:00` are distinct).
- Integration: `Tests.Integration/TypedAggregationParityEndToEndTests.cs`: `TimeBucketKeys_SqliteAndInMemory_ByteEqual_Issue449`.

## WP2 (#450): zoned columns and `AGGREGATION_TIME_BUCKET_UNSUPPORTED`

Files:
- `TrinoSqlEngine/Ast/Security/PlainTimestampClassifier.cs`.
- `Ast/Visitors/TimeZoneCallValidator.cs`.
- `Ast/Generators/SqlDialectGeneratorBase.TimeZone.cs`, `PostgreSqlDialectGenerator.cs`, `SqlServerDialectGenerator.cs`.
- `TimeZones/TimeZoneConversion.cs` (federated refusal).
- `GovernedTableReadPlanner.cs` (classification).
- `Application/Analytics/AggregationErrorCodes.cs`, `AggregationModels.cs`.
- `GovernedAggregationService.Buckets.cs` (in-memory refusal).
- `GraphQL/Catalog/AggregateErrors.cs`, `AggregateArgumentParser.cs`.
- `Api/Middleware/ErrorSanitizingFilter.cs`, `Api/Endpoints/AnalyticsEndpoints.cs`.

Tests (Red first):
- `TrinoSqlEngine.Tests/TimeZones/PlainTimestampClassifierTests.cs`:
  - `IsZoned_SqlServerDatetimeoffset_True`
  - `IsZoned_PostgresTimestamptzSpellings_True`
  - `IsZoned_PlainAndRowversion_False`
- `TimeZoneCallValidatorTests.cs`:
  - `TwoArg_ZonedColumn_Accepted`
  - `TwoArg_PlainColumn_Rejected`
  - `ThreeArg_ZonedColumn_StillRejected`
  - `TwoArg_MaskedColumn_Rejected`
  - `TwoArg_ExpressionArgument_Rejected`
  - `TwoArg_CountsTowardCallCap`
- `TimeZoneEmissionTests.cs`:
  - `TwoArg_Postgres_SingleAtTimeZone_ZoneBound`
  - `TwoArg_SqlServer_CastDatetime2_WindowsIdBound`
  - `TwoArg_DuckDb_Refused`
  - `TwoArg_Federated_Refused`
- `Unit/Data/TimeBucketingPlannedSqlCompileTests.cs`:
  - Rewrite `PlannedBucket_ZonedColumn_Throws400_NoTypeLeak` as `PlannedBucket_ZonedColumn_CompilesWithTwoArgConvert` (REQ-TBZ-13).
  - `PlannedBucket_NonTemporalColumn_TimeBucketUnsupported_NoTypeLeak`
  - `PlannedBucket_SqlServerRowversion_TimeBucketUnsupported`
  - `PlannedBucket_UnknownOrNullMetadata_TimeBucketUnsupported`
  - `PlannedBucket_Databricks_NonUtcZone_TimeBucketUnsupported`
  - `PlannedBucket_UnknownZone_StillArgumentInvalid`
  - `AggregateFailureMapper_TimeBucketUnsupported_OwnCode_FixedMessage_NoColumnName`
- `Unit/GraphQL/TimeBucketingAggregateParserTests.cs`: `TimeBucket_NonDateTimeField_TimeBucketUnsupported`.
- `Unit/Analytics/InMemoryTimeBucketKeyTests.cs`: `InMemory_NonTemporalValue_TimeBucketUnsupported`.
- `Unit/Api/AnalyticsAggregationErrorCodesEndpointTests.cs`:
  - `TimeBucketUnsupported_Rest_400_FixedMessage`
  - `TimeBucketUnsupported_SanitizerPassesOutsideDevelopment`
- Integration (Testcontainers, new `Tests.Integration/TimeBucketZonedColumnTests.cs`, PG and MSSQL):
  - `ZonedColumn_DayBerlin_SameInstantDifferentStoredOffsets_OneBucket`
  - `ZonedColumn_Issue450Query_WhereOffsetLiteral_AndDayBucket`
  - `ZonedColumn_PgSessionTimeZoneNewYork_SameResult` (connection `Options=-c TimeZone=America/New_York`; SQL Server has no session time zone, so not applicable)
  - `ZonedColumn_HourFold_TwoKeys`
- Parity cell `tb_day_berlin_zoned` (PG and MSSQL keys equal).

## Shared-file notes

- WP1 and WP2 both touch `GovernedTableReadPlanner.cs`, `Buckets.cs` and `TimeBucketingPlannedSqlCompileTests.cs`, so WP2 starts after WP1 is merged.
- The #448 lane edits `Buckets.cs` (minimum group size) and the #447 lane likely edits `ErrorSanitizingFilter.cs`. Whichever lane merges later rebases. The edit regions are disjoint (keys and ordering vs. suppression; the allowlist is append-only).
- `TrinoSqlEngine` changes are confined to WP2.

## Risks

1. Under the 2-argument form, WebSQL users also gain zoned conversion on the governed path (D4). The zones are still registry-bound and the caps unchanged. Phase 3 to confirm.
2. The HOUR alignment assumes a constant offset minute part. Zones without one fall back and merge their fold hour (limit documented in tests).
3. SQL Server uses Windows time zone rules and keys use .NET/IANA rules. Historical divergence can shift an offset label; this is pre-existing for plain columns. SQLite uses the base offset outside the year range of `SqliteZoneOffsetTable`.
4. Clients parsing `...Z` keys break. Accepted (not in production).
5. Out of scope, flagged for Phase 3: `AggregateArgumentParser.cs:195` echoes the caller's zone text (`Time zone '{timeZone}' is not recognized.`) with code `INVALID_QUERY`.

## Open decisions (default taken)

- D1: UTC keys use `+00:00`.
- D2: HOUR grouped by the zone-aligned instant, with wall-hour fallback.
- D3: `date` columns ignore the zone.
- D4: 2-argument `convert_timezone` on governed PG/MSSQL; refused on DuckDB and federated.
- D5: Fixed message; extensions `field` and `bucket` only; no DB column name.
- D6: Databricks/Oracle non-UTC zone is unsupported.
- D7: SQLite has no type gate.
- D8: Parser gate on `FieldType`.

## Requirement traceability

| REQ-TBZ | WP | Tests (representative) |
|---|---|---|
| 01, 03 | WP1 | `PlannedKey_ZoneWallDayBerlin_Issue449_CarriesOffset`, `FormatInstant_Utc_UsesPlus0000`, `FormatDate_IsWallDateWithoutOffset` |
| 02, 06 | WP1 | `FormatWall_BerlinAutumnFold_*`, `FormatWall_SantiagoSpringGap*`, `tb_hour_berlin_autumn_fold` |
| 04 | WP1 | `Map_BucketColumn_PassesOffsetKeyVerbatim`, `Buckets_OrderByBucketKey_FoldHours_Chronological` |
| 05 | WP1 | `Format_InvariantUnderTrTrAndArSa` |
| 07, 08 | WP2 | `TwoArg_*`, `ZonedColumn_DayBerlin_SameInstantDifferentStoredOffsets_OneBucket`, `ZonedColumn_PgSessionTimeZoneNewYork_SameResult` |
| 09, 10 | WP2 | `*_TimeBucketUnsupported*`, `TimeBucketUnsupported_Rest_400_FixedMessage` |
| 11 | WP2 | `ZonedColumn_Issue450Query_WhereOffsetLiteral_AndDayBucket` |
| 12 | WP1+2 | Parity cells, `TimeBucketKeys_SqliteAndInMemory_ByteEqual_Issue449` |
| 13 | WP2 | Rewritten `PlannedBucket_ZonedColumn_CompilesWithTwoArgConvert` |
| 14 | both | No migration; catalog `DataType` only |

## Phase 3 security review

**Verdict: APPROVED WITH CONDITIONS.** Date: 2026-11-03. Verified: `AggregateArgumentParser.cs:195` echo; group-by on a `Mask` column throws `MaskedAggregationException` (`ValidationAndExecution.cs:149,196`); `TimeZoneRegistry.Contains` is ordinal (so the accepted zone text is already the canonical key); suppression runs in `Buckets.cs` after bucket formation.

### Threat model (summary)
- Zone/unit input: already registry-bound (ordinal) and enum-switched; the plan keeps this. Risk is regression, so it is pinned by tests. The new key formatting emits only offsets computed from `TimeZoneInfo`, never request text.
- Two-argument `convert_timezone` (D4): no new injection path. The zone is a bound parameter validated by the existing zone-argument rules; the timestamp argument is restricted to a bare, unmasked, zoned column. Surface added: WebSQL users on governed PG/MSSQL can read a zoned column in any registry zone. This reveals no more than the column value itself (same column access and row filter apply). DuckDB and federated refuse it.
- Session-zone independence: `AT TIME ZONE` on a `timestamptz`/`datetimeoffset` input is an instant operation; correct. The `U = convert_timezone('UTC', col)` form must not be emitted for plain columns with an implicit session cast.
- Masking: group-by on a masked column is refused before planning, so bucketing cannot bypass a mask. Row filters are applied in SQL or in memory before bucketing. Bucketing only coarsens a visible column, so it is not a bypass.
- Suppression: grouping HOUR by the zone-aligned instant and DAY+ by wall time changes group membership only (fold hours become two groups, never merged). Suppression counts per final group, so it remains correct; the wall-hour fallback merges two hours into one larger group (safe direction).
- DoS: bucket cardinality is bounded by the existing max-groups limit and the statement caps (`MaxCallsPerStatement = 8`); the zone table lookup is O(1). HOUR over a long range may hit the group limit, which fails closed with `LimitExceeded`.

### Binding conditions
- SEC-TBZ-01 (WP1, zone/unit): `<unit>` comes only from an explicit-arm `switch` over `TimeBucketInterval` with a throwing default; the zone reaches SQL only as a bound parameter or a literal resolved through `TimeZoneRegistry` (ordinal match), never as the raw caller string. `TimeBucketKeys.ResolveZone` has no UTC fallback. Retains SEC-PGP-01.
- SEC-TBZ-02 (WP1, fail closed): the in-memory `catch { tzi = Utc }` is removed; an unresolvable zone throws the fixed `ArgumentException`. Offset-less strings parse as UTC (`AssumeUniversal`), never server-local.
- SEC-TBZ-03 (WP1, keys): keys use `CultureInfo.InvariantCulture` and `const` formats only; a non-temporal value in a bucketed column raises the fixed `AGGREGATION_TIME_BUCKET_UNSUPPORTED`, never `ToString()` passthrough. Retains SEC-PGP-06 (its "zoned columns rejected" premise is superseded).
- SEC-TBZ-04 (WP1, mapper): for bucket columns `ConvertKey` returns the string verbatim and a non-string throws `ResultTypeMismatch`; no `DateTime` conversion may touch a bucket key.
- SEC-TBZ-05 (WP2, validator): the two-argument form is accepted only when the timestamp argument is a bare column reference, not masked, and `IsZoned` for the dialect; `IsZoned` is an allowlist (unknown type returns false). The three-argument form keeps `IsPlain`. Zone-argument rules, deduplication and the call cap are unchanged.
- SEC-TBZ-06 (WP2, engine scope): the DuckDB generator and `TimeZoneConversion.ValidateFederated` refuse the two-argument form with fixed errors; a federated or cross-source statement containing it fails closed before execution.
- SEC-TBZ-07 (WP2, classification): in the planner, a null column metadata, empty/unknown `DataType`, or a type that is neither date nor plain nor zoned throws `AggregationTimeBucketUnsupportedException`. No fallback to the unwrapped column or to session time. Supersedes SEC-PGP-02 only in the error type and the zoned case; the no-leak rule is retained.
- SEC-TBZ-08 (WP2, D7): SQLite has no type gate, so the parser gate (D8, `FieldType == DateTime`) is mandatory and applies to all dialects; the SQLite SQL stays a fixed per-unit template with the zone from the offset table only. Residual risk accepted in R1.
- SEC-TBZ-09 (WP2, messages/D5): the fixed message "Time bucketing is not supported for this column." is the only text; the exception message, column name, DB type, zone text and provider text are absent from the response, `extensions` (only `field` as the GraphQL field name and `bucket` enum name) and logs at Information or above (log type name and code only). `field` is the caller's own requested field, already authorized. `AggregationTimeBucketUnsupportedException` must not derive from `ArgumentException`. Retains SEC-PGP-03.
- SEC-TBZ-10 (WP2, sanitizer): the `ErrorSanitizingFilter` allowlist entry is append-only with a fixed message; outside Development the original exception message must not pass.
- SEC-TBZ-11 (WP1, scope): the `AggregateArgumentParser.cs:195` echo is in scope as a one-line fix: use `AggregateErrors.ArgumentInvalid()` (or a fixed-message `INVALID_QUERY`) with no zone text. It is the same file as the WP2 parser gate, and caller text in an error is a reflected-content weakness.
- SEC-TBZ-12 (WP1, HOUR): `ZoneHourAlignment` minute part `m` is an `int` computed in C# and emitted as an integer literal (0..59) from the table, never from request text; the wall-hour fallback must never use the two-argument form on a plain column.
- SEC-TBZ-13 (WP1/2, masking): bucketing a `Mask` column is refused before planning (existing behaviour must not be bypassed by the new planner path); no planner or in-memory change may reorder the mask check after bucket resolution.
- SEC-TBZ-14 (WP1, suppression): the minimum group size is applied to final groups after the new key grouping, with no merge of fold hours; a test pins this.

Retained unchanged: SEC-PGP-04 (units), SEC-PGP-05 (SQL Server culture independence), SEC-PGP-07 (writer, `Z` format in the writer only). Superseded: SEC-PGP-06's "zoned rejected" premise and the 400-for-zoned default (PM default 1 of that review); SEC-PGP-02's exception type.

### Mandatory security tests (`[Trait("Category","Security")]`)
- WP1: `ResolveZone_Unresolvable_FailsClosed_NoUtcFallback`; `InMemory_UnknownZone_FailsClosed`; `InMemory_OffsetlessString_IsUtcNotServerLocal`; `Format_InvariantUnderTrTrAndArSa`; `Map_BucketColumn_NonStringKey_ResultTypeMismatch`; `TimeBucketParser_UnknownZone_FixedMessage_NoZoneEcho` (SEC-TBZ-11); `BucketOnMaskedColumn_Rejected_BeforePlanning` (SEC-TBZ-13); `Suppression_FoldHours_NotMerged_PerFinalGroup` (SEC-TBZ-14); `PlannedBucket_Hour_MinutePartIsIntegerLiteral` (SEC-TBZ-12); `PlannedBucket_UnitSwitch_ThrowingDefault`.
- WP2: `TwoArg_PlainColumn_Rejected`; `TwoArg_MaskedColumn_Rejected`; `TwoArg_ExpressionArgument_Rejected`; `TwoArg_CountsTowardCallCap`; `TwoArg_DuckDb_Refused`; `TwoArg_Federated_Refused`; `IsZoned_UnknownType_False`; `PlannedBucket_UnknownOrNullMetadata_TimeBucketUnsupported`; `PlannedBucket_NonTemporalColumn_TimeBucketUnsupported_NoTypeLeak`; `TimeBucket_NonDateTimeField_TimeBucketUnsupported` (SQLite included); `AggregateFailureMapper_TimeBucketUnsupported_OwnCode_FixedMessage_NoColumnName`; `TimeBucketUnsupported_SanitizerPassesOutsideDevelopment`; `TimeBucketUnsupported_NotLoggedWithColumnOrZone`; `ZonedColumn_PgSessionTimeZoneNewYork_SameResult`.

### Accepted residual risks
- R1: on SQLite a non-temporal column that passes `FieldType` (catalog mislabel) is bucketed per its stored text; limited to the same table's visible column, no cross-tenant effect.
- R2: Windows-vs-IANA rule divergence can shift an offset label on SQL Server (pre-existing).
- R3: Lord_Howe-style zones merge their DST fold hour in HOUR buckets; merging increases group size, so it is privacy-safe.
- R4: clients parsing `Z` keys break (not in production).

### Owner questions (defaults)
- Q1: fix the `AggregateArgumentParser.cs:195` echo in WP1? Default: yes (SEC-TBZ-11).
- Q2: keep D4 on governed WebSQL (zoned columns readable in any registry zone)? Default: yes, with SEC-TBZ-05/06.

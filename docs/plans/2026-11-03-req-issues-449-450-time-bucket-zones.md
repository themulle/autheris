# Requirements: Time bucket key offsets and zoned columns (#449, #450)

Date: 2026-11-03. Phase 1 (PM). Issues: themulle/autheris #449 (bucket key labelled `Z` but is zone wall time) and #450 (`timeBuckets` refuses `datetimeoffset`/`timestamptz`, only generic error). Builds on `2026-10-24-plan-issues-333-335-postgres-parity.md` (REQ-PGP-03, SEC-PGP-06, which rejected zoned columns on purpose).

## Context
1. SQL path (`GovernedAggregationService.Buckets.cs:182,207-211` `NormalizePlannedBucketKey`) emits `yyyy-MM-ddTHH:mm:ss` wall time of the bucket zone; the GraphQL `DateTime` scalar then appends `Z`. In-memory path (`TruncateValueToTimeBucket`) returns `zzz` offset. Paths disagree; clients mis-parse instants (#449).
2. `GovernedTableReadPlanner.BuildTimeBucketExpression` (`:608-624`) throws `ArgumentException` for zoned columns on PostgreSQL/SQL Server; surfaces as `AGGREGATION_ARGUMENT_INVALID` (#450).
3. Not in production: breaking output changes are allowed.

## Sequencing
WP1 (#449, key labelling) first and independent; WP2 (#450, zoned columns + error codes) after WP1, because zoned support yields instants whose key correctness depends on WP1.

## Requirements
- REQ-TBZ-01 (#449): Every time bucket key denotes the real bucket start instant. Default: key = bucket start as an ISO 8601 string with the offset valid at that instant in the requested zone (`2026-10-05T00:00:00+02:00`), identical on SQL and in-memory paths.
- REQ-TBZ-02 (#449): Offset is derived from the requested IANA zone (registry-validated, existing `timeZone` handling) at the bucket start; DST fold hours (`02:00+02:00` vs `02:00+01:00`) yield distinct keys.
- REQ-TBZ-03 (#449, D1, D3): A key with no `timeZone` ends in `+00:00`, never `Z`. `date` columns ignore the zone and stay `yyyy-MM-dd`; HOUR on a `date` column yields the date key.
- REQ-TBZ-15 (#449, D2): HOUR buckets group by the zone-aligned UTC instant, so the two autumn-fold hours stay distinct. Zones with odd minute offsets fall back to wall-clock grouping.
- REQ-TBZ-16 (D6): Databricks and Oracle refuse any non-UTC zone (fixed refusal, fail closed).
- REQ-TBZ-17 (D8): The GraphQL parser rejects `timeBuckets` fields that are not `DateTime`.
- REQ-TBZ-18 (WP1 side fixes, in scope): (a) SQLite WEEK key becomes the Monday date; (b) in-memory DAY split on DST days fixed; (c) offset-less strings are read as local time of the bucket zone; (d) an unknown zone no longer silently falls back to UTC (refused); (e) `date` columns are no longer shifted on PostgreSQL and SQL Server.
- REQ-TBZ-04 (#449): GraphQL output of the bucket key must not re-label: the mapper (`AggregateResultMapper.ConvertKey`) must pass offset-bearing keys through without forcing `Z`; the REST group key uses the same string. Order-by on the key remains chronological.
- REQ-TBZ-05 (#449): Fixed format, `CultureInfo.InvariantCulture`, never `ToString()` with current culture; extends SEC-PGP-06. No key values in logs.
- REQ-TBZ-06 (#449): Offset resolution for ambiguous/nonexistent local times (DST gaps) is deterministic (first occurrence rule documented in tests); the SQL wall time is resolved against the zone, not the session.
- REQ-TBZ-07 (#450): `datetimeoffset` (SQL Server) and `timestamptz` (PostgreSQL) columns accept `timeBuckets`. The stored absolute instant is converted to the requested zone (`AT TIME ZONE` form per dialect, no dependence on session time zone), then truncated, as for plain timestamps. SQLite behaviour unchanged.
- REQ-TBZ-08 (#450): Result must be independent of the session time zone and of the stored offset; the same instant stored with different offsets lands in the same bucket.
- REQ-TBZ-09 (#450): Unsupported columns (non-temporal or unknown type) return a distinct code `AGGREGATION_TIME_BUCKET_UNSUPPORTED` (default name) with a fixed message that does not contain the column name; its `extensions` carry only the GraphQL field name and the bucket (D5). No SQL and no driver text. `AGGREGATION_ARGUMENT_INVALID` stays for malformed arguments (bad unit, bad zone).
- REQ-TBZ-10 (#450): The new code is added to the GraphQL error-code catalogue/docs reference, mapped for REST (400) and WebSQL, and is never a 5xx. Fail-closed: any column whose type classification is unknown is refused with this code.
- REQ-TBZ-11 (#450): Bucket zone and `where` filters on zoned columns interoperate (offset-bearing literals as in the issue example); no change to filter semantics.
- REQ-TBZ-12: Parity tests (SQLite, PostgreSQL, SQL Server via Testcontainers, in-memory) assert identical keys for the same data incl. DST autumn change and an offset-stored column.
- REQ-TBZ-13: Supersedes the 2026-10-24 decision "zoned column returns 400" (PGP, `PlannedBucket_ZonedColumn_Throws400`); that test is rewritten, SEC-PGP-06 wall-clock-only note is replaced by REQ-TBZ-01/05.
- REQ-TBZ-14: No schema change expected. If the architect needs one (e.g. catalog type classification), it follows the forward-only migration policy (v1.5.0 frozen base, auto-migrate to newest).

## Acceptance criteria
1. Issue #449 query on SQLite returns `2026-10-05T00:00:00+02:00` (or equal instant) for the 2026-10-06T22:30:00Z row's neighbour bucket; SQL and in-memory keys are byte-equal.
2. Hourly buckets across the Europe/Berlin autumn change give distinct keys for the two 02:00 hours.
3. Issue #450 query on a `datetimeoffset` column returns daily groups; same on `timestamptz`; same instants stored with +02:00 and +00:00 share a bucket.
4. Bucketing a text/bool/unknown column returns `AGGREGATION_TIME_BUCKET_UNSUPPORTED`, HTTP 400 on REST, never 5xx.
5. Changing PostgreSQL/SQL Server session time zone does not change results.
6. All existing bucket tests pass or are updated per REQ-TBZ-13.

## Non-goals
- Per-row source-offset preservation (key uses the requested zone offset, not stored offsets).
- New bucket units, new zone registry, or changing privacy/small-group suppression.
- Bucketing by non-temporal expressions.

## Rejected anti-patterns
- Keeping wall time and just documenting it (client cannot distinguish DST fold, issue #449 note).
- Relabelling wall time as UTC by shifting values silently (breaks ordering and parity).
- Relying on the DB session time zone for zoned columns.
- Catching and swallowing the planner `ArgumentException` into a generic code; matching on message text for code selection (use a typed exception).
- Emitting driver/SQL text in the new error.

## Open questions (default taken)
1. Key form: offset (`+02:00`) vs UTC instant (`Z`)? Default taken: offset of the requested zone (matches in-memory path, readable wall time, both accepted by the issue).
2. Error code name? Default taken: `AGGREGATION_TIME_BUCKET_UNSUPPORTED` (suggested in #450).
3. `date` columns: add offset? Decided (D3): no, plain `yyyy-MM-dd`, zone ignored.
4. Fix #449 in the in-memory path instead (switch to UTC)? Default taken: no, SQL path adapts to the in-memory format.
5. Single PR or two? Default taken: two sequential WPs, WP1 (#449) merged first, same lane.

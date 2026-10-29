# Plan: distinct governed codes for aggregation failures (issue #389)

Phase 2 (csharp-architect), 2026-10-29. Branch `fix/issue-389-aggregation-error-codes` (base `b50b1680`). Size: S (about 60 production LOC, 150 test LOC, 1 work package).

## Problem (verified on the branch)

- `AggregateFailureMapper.ForAggregate` (`src/Autheris.GraphQL/Catalog/AggregateErrors.cs:61,64`) maps `GatewayInvalidQueryException`, `ArgumentException`, `AstBuildException`, `SqlCompileNotSupportedException` and every non-`UNAVAILABLE` `SqlTimeZoneException` to `AGGREGATION_ARGUMENT_INVALID`.
- `GovernedAggregationService.ValidateAggregatesForNoise` (`GovernedAggregationService.cs:403,409`) throws `GatewayInvalidQueryException(..., "AGGREGATION_ARGUMENT_INVALID")`; `ResolveMeasureBounds` (`:450`) throws with the default code `INVALID_QUERY_OPTION` and the column name in the message. REST passes `ex.ErrorCode` through (`AnalyticsEndpoints.cs:151-154`, `:239-242`); GraphQL drops it.
- Additional finding: `AggregationFilterRejectedException` (`AGG_NEGATED_FILTER_REJECTED`, thrown from `GovernedAggregationService.cs:180`) has no arm in `ForAggregate`, so GraphQL answers `INTERNAL_SERVER_ERROR` for a privacy-policy refusal that REST answers with 400.
- REST compile failures from aggregation are not caught in `AnalyticsEndpoints`; they reach `GatewayExceptionHandler` and surface as `SQL_*` codes (`SqlCompilerErrorMapper`), so the two channels disagree.
- Reachability: aggregation is reachable from GraphQL `<table>_aggregate` and metrics (`ForMetrics` falls through to `ForAggregate`) and REST `/api/v1/analytics/aggregate` and metrics. Not reachable from MCP or OData (no caller of `IGovernedAggregationService`); no change there.

## Decisions

1. **Codes.** Keep `AGGREGATION_ARGUMENT_INVALID` for malformed or invalid caller arguments. Add `AggregationErrorCodes.PrivacyRestricted = "AGGREGATION_PRIVACY_RESTRICTED"` and `AggregationErrorCodes.NotSupported = "AGGREGATION_NOT_SUPPORTED"` (`src/Autheris.Application/Analytics/AggregationErrorCodes.cs`). Naming follows the existing `AGGREGATION_<CAUSE>` family. `ParityErrorCodes` reflects that class, so the parity suite accepts the codes with no test change. Messages are NOT public consts on that class (the reflection would read them as codes).
2. **Fixed messages** (SEC-112-11; no column, alias, function, epsilon, bounds, dialect or SQL text): `PrivacyRestricted` = "The requested aggregate is not available under the privacy policy."; `NotSupported` = "The requested aggregation is not supported for this dataset." Single source: `FixedMessage` const on the exception (decision 3) and `AggregateErrors.NotSupportedMessage`.
3. **Typed exception for the DP restriction.** New `public sealed class AggregationPrivacyRestrictedException : GatewayInvalidQueryException` in `AggregationModels.cs` with `ErrorCodeConst`, `FixedMessage`, parameterless constructor `: base(FixedMessage, ErrorCodeConst)`. Deriving from `GatewayInvalidQueryException` keeps it a 400 client error and makes every existing `catch (GatewayInvalidQueryException)` emit the new code. Throw sites: `ValidateAggregatesForNoise` (MIN/MAX and repeated pair) and `ResolveMeasureBounds` (missing or invalid bounds) all throw it; the column name leaves the message. The bounds case gets the column name only in a `Debug`-level server log if Phase 4 wants operator diagnosis (default: no log, as today).
4. **Mapper** (`AggregateFailureMapper.ForAggregate`), arms in this order before the generic `GatewayInvalidQueryException` arm: `AggregationPrivacyRestrictedException` -> `PrivacyRestricted`; `AggregationFilterRejectedException` -> its `ErrorCodeConst`/`FixedMessage`. Compiler arms: `SqlTimeZoneException { Code: TimeZoneUnavailable }` -> `UNAVAILABLE` (unchanged); `SqlTimeZoneException { Code: TimeZoneInvalid or TimeZoneArgument }` -> `ArgumentInvalid` (caller supplied the zone); any other `SqlTimeZoneException` (for example `SQL_FUNCTION_UNSUPPORTED_DIALECT`), `AstBuildException`, `SqlCompileNotSupportedException` (any `Reason`) -> `NotSupported`. Plain `GatewayInvalidQueryException` and `ArgumentException` (parser, time-bucket planner on zoned/rowversion columns) stay `ArgumentInvalid`. Type pattern only; no message matching.
5. **REST** (`AnalyticsEndpoints.HandleAggregateAsync` and the metrics handler): add `catch (AggregationPrivacyRestrictedException)` -> 400 with code and `FixedMessage` before the `GatewayInvalidQueryException` catch; add `catch (Exception ex) when (ex is AstBuildException or SqlCompileNotSupportedException or SqlTimeZoneException { Code: not (TimeZoneUnavailable or TimeZoneInvalid or TimeZoneArgument) })` -> 422 `AGGREGATION_NOT_SUPPORTED`. Time-zone invalid/argument and unavailable keep reaching `GatewayExceptionHandler` (unchanged `SQL_TIMEZONE_*` codes on REST; default taken: do not widen scope). Log via `SafeErrorResults.LogFailure` with the code.
6. **GraphQL sanitizer** (`src/Autheris.Api/Middleware/ErrorSanitizingFilter.cs`): add both codes to `WhitelistedSafeCodes` (block at line 67) and to `FixedClientMessages` with the decision 2 texts. Not in `ExpectedOutcomeCodes`.
7. **Reference doc**: `docs/features/f-data-08-governed-aggregations-and-k-anonymity.md` line 151 names `AGGREGATION_PRIVACY_RESTRICTED` for the noise rules and the missing-bounds case; section 5 gains one bullet for `AGGREGATION_NOT_SUPPORTED` (translation limits, 422 on REST) and states that `AGG_NEGATED_FILTER_REJECTED` is returned on GraphQL too. No other reference doc lists aggregation codes.
8. **Anti-oracle (input for Phase 3).** `Privacy.Noise.Enabled` is deployment-wide (`GovernedAggregationService.cs:182`), not per table or caller, and every successful result already returns `noiseApplied`. A MIN/MAX refusal therefore discloses nothing a caller cannot learn from one successful call. The missing-bounds refusal discloses that a column has no configured `MeasureBounds`; the column is one the caller may already aggregate (access checks run first at `:151-167`), and the message carries no column. Recommendation: accept; same code and message for all three DP causes.
9. **Ordering guarantee**: all DP refusals stay before any data read and before the budget charge (unchanged, `executor.Calls == 0`). The run-time `ResolveMeasureBounds` calls at `:566/:576` throw the same typed exception; Phase 4 verifies they are covered by the pre-check at `:430` (no budget charged before the throw), otherwise moves the check earlier.

## Tests (TDD, Phase 4)

- Unit `TypedAggregateResolverTests.EngineFailures`: add rows `AggregationPrivacyRestrictedException` -> `AGGREGATION_PRIVACY_RESTRICTED`, `AggregationFilterRejectedException` -> `AGG_NEGATED_FILTER_REJECTED`, `AstBuildException(secret)` and `SqlCompileNotSupportedException` (each `Reason`) -> `AGGREGATION_NOT_SUPPORTED`, `SqlTimeZoneException(FunctionUnsupportedDialect)` -> `AGGREGATION_NOT_SUPPORTED`; existing `ArgumentInvalid` rows unchanged. Same rows in `TypedMetricQueryGraphQlTests`.
- Unit `TimeBucketingPlannedSqlCompileTests` (~line 285): split the loop; `TimeZoneInvalid`/`TimeZoneArgument` stay `AGGREGATION_ARGUMENT_INVALID`, `AstBuildException` and `SqlCompileNotSupportedException(Construct)` become `AGGREGATION_NOT_SUPPORTED`; sentinel non-echo assertion kept. Lines 141/151 unchanged.
- Unit `AggregationPrivacyHardeningTests`: `ExecuteAggregationAsync_NoiseOn_RejectsMinAndMax` and the repeated-pair test assert `Should.ThrowAsync<AggregationPrivacyRestrictedException>`, code `AGGREGATION_PRIVACY_RESTRICTED`, `executor.Calls == 0`; new `ExecuteAggregationAsync_NoiseOn_MissingMeasureBounds_PrivacyRestricted_NoColumnInMessage`.
- Unit `ErrorSanitizingFilterCodeTests.TypedFixedMessageCodes_PassThroughInEveryEnvironment`: add `Production` rows for both codes; new `NewAggregationCodes_FixedMessagesContainNoThrowerText`.
- Unit (REST) `AnalyticsEndpoints` handler tests: `Aggregate_PrivacyRestricted_Returns400WithCode`, `Aggregate_CompileNotSupported_Returns422NotSupported`, body without exception text; same for metrics.
- Integration (GraphQL over HTTP, noise enabled, Production env): `Aggregate_MinUnderNoise_ReturnsPrivacyRestrictedCode` asserts code, fixed message, no column or alias in the response.

## Phase 3 questions

1. Accept the anti-oracle analysis in decision 8 (one code for MIN/MAX, repeated pair, missing bounds)?
2. 400 for `AGGREGATION_PRIVACY_RESTRICTED` (client can change the request) vs 403 (policy denial)?
3. Is `AGGREGATION_NOT_SUPPORTED` (one code for all translation limits, no dialect hint) sufficient, or must dialect-level refusals stay indistinguishable from construct refusals (current proposal: yes, indistinguishable)?
4. Fixing the GraphQL `AGG_NEGATED_FILTER_REJECTED` gap in this issue: in scope (default taken: yes)?

## Phase 3 binding conditions (csharp-security-expert, 2026-10-29)

Verdict: APPROVED WITH CONDITIONS.

Rulings: (1) Accepted: noise is deployment-wide and `noiseApplied` is already public, so one distinct code for all three DP causes leaks nothing new; the same code and message must apply to all three (no per-cause variant). (2) 400 on REST (client can change the request; the policy is not an authorization decision; consistent with `AGG_NEGATED_FILTER_REJECTED`); GraphQL carries the code only. (3) Single `AGGREGATION_NOT_SUPPORTED`, dialect-level and construct-level refusals indistinguishable, no dialect hint. (4) The `AGG_NEGATED_FILTER_REJECTED` GraphQL mapping is in scope. (5) The fixed message is required and the column name must leave the message.

Binding conditions
- I389-SC-01: Messages for `AGGREGATION_PRIVACY_RESTRICTED`, `AGGREGATION_NOT_SUPPORTED` and `AGG_NEGATED_FILTER_REJECTED` are compile-time constants; no column, alias, function, epsilon, bounds, dialect, SQL or exception text in any channel (REST body, GraphQL error and extensions, logs, audit, metrics tags).
- I389-SC-02: Decision 3 Debug-log option is withdrawn: no log or audit entry for these refusals contains the column, alias or inner exception message. Logs carry the code and exception type name only; operator diagnosis of missing bounds belongs to startup/config validation, not the request path.
- I389-SC-03: Mapping is by exception type or typed `Code`/`Reason` property only; never by message text. The arm order (specific types before `GatewayInvalidQueryException`) is asserted by tests. Unknown exceptions keep falling to the existing sanitized generic path (fail closed, no new pass-through).
- I389-SC-04: Both new codes are added to the sanitizer allow-list and `FixedClientMessages`, never to `ExpectedOutcomeCodes`; any code not on the list stays sanitized.
- I389-SC-05: All DP refusals (MIN/MAX, repeated pair, missing/invalid bounds, including the run-time `ResolveMeasureBounds` sites) occur before executor invocation and budget charge; Phase 4 must prove the run-time sites are pre-covered or move the check earlier.
- I389-SC-06: REST and GraphQL parity: for each cause the same code, a status mapping of 400 (privacy restricted, argument invalid) or 422 (not supported), and the same fixed message; metrics paths included. The REST catch filters must not widen to catch time-zone invalid/argument/unavailable (those keep their current handling).
- I389-SC-07: No new behavior for MCP/OData; the typed exception must not carry data properties (no column/alias fields).

Mandatory tests (all `[Trait("Category","Security")]`)
- I389-T-01 `PrivacyRestricted_MinMaxRepeatedPairMissingBounds_SameCodeAndMessage_NoColumnOrAlias` (sentinel column and alias absent from message, ToString of response, log output).
- I389-T-02 `PrivacyRestricted_ExecutorAndBudgetNotCalled` (covers pre-check and run-time bounds sites; `executor.Calls == 0`).
- I389-T-03 `AggregateFailureMapper_MapsByType_NotMessage` (message containing another code's text or a sentinel does not change the code; arm order).
- I389-T-04 `NotSupported_AstBuild_CompileNotSupported_DialectUnsupported_FixedMessage_NoDialectOrSecretEcho` (all `Reason` values and `FunctionUnsupportedDialect` yield identical code and message).
- I389-T-05 `Sanitizer_NewCodes_AllowListed_FixedMessage_InProduction` and `Sanitizer_UnknownCode_StillSanitized`.
- I389-T-06 `Rest_Graphql_Parity_PerCause` (code, status class, message equality, for aggregate and metrics) including `AGG_NEGATED_FILTER_REJECTED` on GraphQL (no `INTERNAL_SERVER_ERROR`).
- I389-T-07 `Rest_TimeZoneInvalidAndUnavailable_NotCaughtByNotSupportedFilter` (regression guard for SC-06).
- I389-T-08 `RefusalLogsAndAudit_ContainNoColumnAliasEpsilonOrInnerMessage` (log capture with sentinels).

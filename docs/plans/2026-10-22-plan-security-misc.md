# Plan: Security review fixes, area MISC (OLAP, analytics, lineage/GDPR, HTTP sources, shadowing, schema registry, CI)

Phase 2 (csharp-architect), 2026-10-22. Source: `docs/plans/security.txt`, all `MISC-*` findings rated HIGH or MEDIUM.
Branch base verified: `fix/security-review-open-findings` @ 55dc7e27. No production yet: breaking changes are allowed.

**Scope.** In: MISC-01, 02, 03, 04, 05, 06, 07, 08, 09, 11, 12, 13, 14, 16, 17 (15 findings).
Out: MISC-10 (LOW) and MISC-15 (LOW). Per the MISC-15 amendment, `CursorPaginationHelper` has no production caller and
`ArgumentOutOfRangeException` already maps to 400, so MISC-15 is a latent defect in a shared helper: fix before its first use, not here.
All findings below were re-verified against the current code; none is already fixed. Two findings are broader than reported (noted inline).

---

## WP-M1 Required filter columns: one shared rule, OLAP enforcement (MISC-03 HIGH, MISC-04 MEDIUM). Size M (~300 LOC)

**MISC-03, root cause (verified).** `OlapStagingService.ResolveAsync` (`src/Autheris.Application/Olap/OlapStagingService.cs:588-662`) and the
staging loop (`:359-392`) never read `RequiredFilterColumns`/`RequiredFilterColumnGroups`; staging pushes only `Decision.CombinedRowFilterSql`
(`:368`) and reads up to `MaxStagedRowsPerTable` rows. Single-source OLAP therefore bypasses the rule that GraphQL/WebSQL/stream enforce.

**MISC-04, root cause (verified, broader).** The substring test `CombinedRowFilterSql.Contains(reqCol, OrdinalIgnoreCase)` exists in four
places, not one: `FederatedStagingService.cs:111`, `GovernedTableReadPlanner.cs:533`, `GovernedTableReadPlanner.Keyset.cs:130`,
`CrossSourcePlanner.cs:170`.

**Fix.**
1. New `src/Autheris.Application/Data/Services/RequiredFilterColumnRule.cs`:
   `internal static class RequiredFilterColumnRule { static void Enforce(TableMetadata metadata, TableAccessDecision decision, IReadOnlyCollection<string>? clientFilterColumns, bool tenantColumnCounts); static bool IsSatisfied(...); }`
   Satisfied only by (a) the tenant column when `tenantColumnCounts`, (b) `decision.RowPolicy is PolicyPredicateRowPolicy` with
   `Predicate.ReferencedColumns`, (c) whole-identifier columns of `decision.CombinedRowFilterSql` via
   `DataApiFilterTranslator.ExtractReferencedColumns(sql, metadata)` (identifier tokens, no literals/comments), (d) `clientFilterColumns`.
   If (c) is needed and the row filter cannot be analysed (parser throws or returns no columns for non-empty SQL), treat as not satisfied.
   Throws `RequiredFilterColumnMissingException(GovernedSqlErrorCodes.RequiredFilterColumnMissing)`. Groups: any non-empty group fully satisfied.
2. Replace the four local `IsSatisfied` copies with calls to the rule (keep each call site's existing `isAggregate`/tenant semantics).
3. `OlapStagingService.ResolveAsync`: after the decision is allowed, call `RequiredFilterColumnRule.Enforce(meta, decision, clientFilterColumns: null, tenantColumnCounts: true)`
   (client SQL runs after staging, so it never counts). Map the exception to `OlapStagingOutcome(400, REQUIRED_FILTER_COLUMN_MISSING)` via `MapException`.
   Cross-source OLAP goes through the same `ResolveAsync`, so both modes are covered.

**Fail-closed.** Unparseable row filter, empty group list entries, or a missing decision all count as "not satisfied".

**Red tests.** `tests/Autheris.Tests.Unit/Olap/OlapStagingServiceTests.cs`: `Olap_RequiredFilterColumn_NotInRls_Returns400RequiredFilterMissing`,
`Olap_CrossSource_RequiredFilterColumn_NotInRls_Returns400`, `Olap_RequiredFilterColumn_SatisfiedByTypedRls_Stages`.
New `tests/Autheris.Tests.Unit/Data/RequiredFilterColumnRuleTests.cs`: `LongerIdentifier_region_code_DoesNotSatisfy_region`,
`StringLiteralContainingColumn_DoesNotSatisfy`, `TenantIdColumn_DoesNotSatisfy_id`, `CommentContainingColumn_DoesNotSatisfy`,
`UnparseableRowFilter_FailsClosed`, `WholeIdentifier_Satisfies`, `GroupSatisfied_WhenAllMembersSatisfied`.
Parity: one test per read path (`FederatedStaging_...`, `CrossSourcePlanner_...`, `KeysetPlanner_...`) using the `region_code` repro.

**Breaking.** Callers whose RLS text only mentioned the column in a literal/longer name now get 400. Intended.
**Dependencies.** SQLE plan WP-SQLE-B (AST required-filter scope) is independent; EXT plan WP1/WP2 (catalog keeps RequiredFilterColumns) makes this rule meaningful.

---

## WP-M2 Metric consent cannot override PDP denials (MISC-06 HIGH). Size M-L (~400 LOC)

**Root cause (verified).** `SemanticMetricQueryService.ConsentAndEvaluation.cs:74-176`: only `IsAllowed` or a reason substring "Explicit Deny"
stops the metric branch; every other PDP denial (Casbin, ReBAC, contract, virtual filter, plugin, profile source unavailable;
`TableAccessPolicy.cs:206-460`) falls through to `ConsentResolutionService.ResolveAccess` over projected metric consents. That decision is
passed as `PreEvaluatedDecision` (`SemanticMetricQueryService.cs:294`) and returned unchecked at `GovernedAggregationService.ValidationAndExecution.cs:33-36`.

**Fix.**
1. Typed denial kind: `src/Autheris.Domain/Interfaces/TableAccessDenialKind.cs` `public enum TableAccessDenialKind { None, ConsentMissing, ConsentDeny, Policy }`;
   `TableAccessDecision` gets `public TableAccessDenialKind DenialKind { get; init; }`; `Denied(...)` defaults to `Policy`.
   Only `TableAccessPolicy.Consent.cs` (no Allow consent -> `ConsentMissing`; Deny consent -> `ConsentDeny`) sets the consent kinds.
   The decision cache (`DecisionKey`/cached decision) must round-trip `DenialKind`.
2. `TableAccessQuery` gets `IReadOnlyList<Consent>? SupplementalConsents = null`; `ResolveConsentAsync` unions them with the repository
   consents. `IUnifiedPolicyDecisionPoint.EvaluateAccessAsync` gets an overload with `IReadOnlyList<Consent>? supplementalConsents`.
3. Metric branch: proceed only when `tableDecision.DenialKind == ConsentMissing`. Re-evaluate through the PDP with the projected metric
   consents as `SupplementalConsents` (full pipeline: ReBAC, contract, Casbin incl. Restrict, hooks, virtual filters, profile). Use that result.
   Remove the "Explicit Deny" substring check and the direct `resolver.ResolveAccess` call.
4. `GovernedAggregationService`: `PreEvaluatedDecision` is accepted only when it carries a PDP provenance marker
   (`internal bool EvaluatedByPdp { get; init; }`, set by `UnifiedPolicyDecisionPoint`); otherwise ignore it and evaluate normally.

**Fail-closed.** Any `DenialKind` other than `ConsentMissing` returns the original denial. No PDP registered -> deny. A metric decision
without PDP provenance is never trusted.

**Red tests.** `tests/Autheris.Tests.Unit/Analytics/SemanticMetricConsentPdpTests.cs` (new):
`MetricConsent_CasbinDenied_StaysDenied`, `MetricConsent_RebacDenied_StaysDenied`, `MetricConsent_ContractDenied_StaysDenied`,
`MetricConsent_VirtualFilterDenied_StaysDenied`, `MetricConsent_ProfileSourceUnavailable_StaysDenied`,
`MetricConsent_ConsentMissing_AllowedWithVirtualFilterPredicateApplied` (asserts `MandatoryRowPredicateSql` present),
`MetricConsent_CasbinRestrict_RowFilterApplied`, `Aggregation_PreEvaluatedDecisionWithoutPdpProvenance_IsReevaluated`.
`TableAccessPolicyTests`: `NoConsent_DenialKindConsentMissing`, `CasbinDeny_DenialKindPolicy`.

**Breaking.** `TableAccessDecision` gains a property; metric queries that relied on consent overriding policy denials now fail (intended).
**Dependencies.** Touches the Policy/PDP area (shared with GOV area plans); coordinate merge order on `TableAccessPolicy*.cs`.

---

## WP-M3 Aggregation correctness and differencing (MISC-07, MISC-08, MISC-09 MEDIUM). Size M (~350 LOC)

**MISC-07 (verified).** `NoisyCacheKey.Build` (`NoisyCacheKey.cs:70-112`) omits `AggregationQuery.TimeBuckets`; call site
`GovernedAggregationService.cs:215`. Fix: add `IReadOnlyList<TimeBucketSpecification>? timeBuckets` to `Build`, write count then per spec
`Column`, `Interval`, `TimeZone` in request order; bump the tag to `noisy-key-v2`. Fail-closed: a null and an empty list hash differently
from any non-empty list. Red tests (`NoisyCacheKeyTests.cs`): `Build_DifferentTimeBucketInterval_DifferentKey`,
`Build_DifferentTimeZone_DifferentKey`, `Build_TimeBucketColumnChange_DifferentKey`; service test `NoisyCache_DayThenMonth_SecondIsCacheMiss`.

**MISC-08 (verified).** `MergePreAggregatedBuckets` (`GovernedAggregationService.Buckets.cs:255-297`) sums per-group COUNT_DISTINCT and
weights AVG by `b.Count`; reached from the totals path (`:391-397`) for planner buckets (`Rows = []`). Fix: for totals (`isMergedOther: false`
on pre-aggregated buckets) set COUNT_DISTINCT totals to `null` (non-mergeable, same as `NullNonMergeable` for OTHER). For AVG, the planner
emits a hidden `COUNT(col)` per AVG aggregate (`__cnt_<alias>`, stripped from the response) in `BuildBucketsFromPlannedSqlResult`; merge AVG
as `Σ(avg_i·cnt_i)/Σcnt_i`; if the hidden count is absent, total AVG is `null`. Red tests (`GovernedAggregationTotalsTests.cs`, new):
`PlannerTotals_CountDistinct_IsNull`, `PlannerTotals_Avg_WeightedByNonNullCount` (55 not 18.2 repro), `PlannerTotals_Avg_MissingCount_IsNull`,
`InMemoryAndPlannerTotals_AgreeForSumCountAvg`.

**MISC-09 (verified).** `AnalyticsOptions.RestrictNegatedFilters` (`AnalyticsOptions.cs:81`) has no reader; `ValidateWhereClause`
(`GovernedAggregationService.ValidationAndExecution.cs:230-284`) only checks column access. Fix (default taken: implement, not remove):
when the option is true and the effective min group size > 1, reject (new `AggregationFilterRejectedException`, code `AGG_NEGATED_FILTER_REJECTED`, 400)
any WHERE whose predicate on a column that is **not** a group-by column uses `<>`, `!=`, `NOT IN`, `NOT LIKE`, `NOT BETWEEN`, `IS NOT`
(other than `IS NOT NULL`), or `NOT (...)`; detect on the typed where tree when present, else on the tokenised text (TrinoSqlEngine tokenizer,
not regex). Also reject equality on a non-group-by column whose metadata marks it unique/primary key (single-row isolation). Unparseable
WHERE -> reject. Red tests (`AggregationNegatedFilterTests.cs`, new): `NotEqualsOnNonGroupColumn_Rejected` (employee_id repro),
`NotIn_Rejected`, `NotLike_Rejected`, `NotParenthesised_Rejected`, `NegationOnGroupByColumn_Allowed`, `IsNotNull_Allowed`,
`OptionFalse_NegationAllowed`, `MinGroupSizeOne_NegationAllowed`, `UnparseableWhere_Rejected`. Note in the option XML doc that
k-anonymity without noise does not fully resist differencing.

**Breaking.** Cache keys change (one-day cache churn); totals for COUNT_DISTINCT become null; some aggregation filters are now rejected.

---

## WP-M4 OLAP memory budget (MISC-05 MEDIUM). Size S-M (~200 LOC)

**Root cause (verified).** `DuckDbOlapOptions` (`DistributedCacheOptions.cs:58-95`): `MaxMemory="1GB"`, `MaxConcurrentQueries=4`,
`MaxTotalStagedBytes=256MB`; `DuckDbOlapEngine.cs:163` applies `max_memory` per query DB. Helm `values.yaml:73` limit 2048Mi,
`docker-compose.yml:24` `mem_limit: 1g`. No validator exists for `DuckDbOlapOptions`.

**Fix.**
1. Defaults: `MaxMemory="256MB"`, `MaxConcurrentQueries=2`, add `long MemoryBudgetBytes` (0 = derive).
2. `src/Autheris.Application/Olap/ContainerMemoryLimit.cs`: reads cgroup v2 `/sys/fs/cgroup/memory.max` then v1
   `memory/memory.limit_in_bytes`; `"max"`/unreadable -> null. Injectable `Func<string,string?>` for tests.
3. `DuckDbOlapOptionsValidator : IValidateOptions<DuckDbOlapOptions>` (registered with `ValidateOnStart`): budget = `MemoryBudgetBytes`
   or 50% of the cgroup limit; fail when `MaxConcurrentQueries × (parse(MaxMemory) + 3 × MaxTotalStagedBytes) > budget`
   (3× = .NET object overhead factor); fail on unparseable `MaxMemory`. No limit and no budget while `Enabled` -> validation failure in
   non-Development environments (fail closed), warning in Development.
4. Raise helm `values.yaml` memory limit only if the new defaults do not fit; record the computed figure in `values.yaml` comments.

**Red tests.** `tests/Autheris.Tests.Unit/Olap/DuckDbOlapOptionsValidatorTests.cs`: `Defaults_FitHelmLimit2GiB`, `Defaults_FitCompose1GiB`,
`OverCommit_FailsValidation`, `UnparseableMaxMemory_Fails`, `NoLimitNoBudget_Production_Fails`, `CgroupV2Max_TreatedAsNoLimit`,
`ExplicitBudget_OverridesCgroup`.
**Breaking.** Lower OLAP memory per query; operators with large staging must set `MemoryBudgetBytes`.

---

## WP-M5 GDPR Art. 15 report and PDF seal (MISC-11 HIGH, MISC-12 MEDIUM). Size L (~500 LOC)

**MISC-11 (verified).** `LineageImpactAnalyzerService.GetGdprDataDisclosureReportAsync` passes `actorSid: subjectSid` (`:461-468`) and groups
recipients by `ActorSid` (`:507`); `limit: 5000` without truncation flag. `AuditLogEntry` (`GovernanceModels.cs:71`) carries no data-subject
or row keys, so "who read rows about the subject" cannot be answered from the audit log today.

**Fix (default taken: honest table-level report now, row-level subject capture as a separate PRD).**
1. Stop using the subject as actor. Determine subject-bearing tables: the requested `table`, or all tables with PII/person classification
   whose person-key column is set. Query audit by `targetTable` only and exclude rows where `ActorSid == subjectSid`.
2. Page through `QueryAuditLogsAsync` (add `long? afterId` + stable order to `IGovernanceRepository.QueryAuditLogsAsync`) up to a hard cap
   `GdprReportOptions.MaxAuditEvents` (default 100 000); hitting the cap sets `IsComplete=false`.
3. `GdprDisclosureReport` (`GovernanceV2Models.cs:204`) adds `bool IsComplete`, `string EvidenceLevel` (`"TableLevel"`), `IReadOnlyList<string> IncompletenessReasons`;
   recipients are labelled "Potential recipients (accessed tables containing the subject's data)". Without a subject->table mapping the
   endpoint returns 422 `GDPR_SUBJECT_SCOPE_UNRESOLVED` rather than a report.
4. Follow-up PRD (not in this plan): capture data-subject keys in audit records for row-level Art. 15 evidence.

**MISC-12 (verified).** `GdprAuditReportPdfExporter.cs:21-24` unkeyed SHA-256; `:111-124` silently stops after 25 recipients; `:181-186`.
**Fix.** Inject `IAuditAnchorSigner` (`IAuditChainAnchorStore.cs:16`); seal = `Sign(UTF-8 bytes of the exact rendered text model)`, where the
rendered model is the single source for both PDF content and seal. Render all recipients with pagination (new page per N lines);
print `IsComplete`/incompleteness reasons. No signer -> no seal line, no "Tamper-Evident" text, header `X-Audit-Seal` omitted.
Rename header to `X-Audit-Seal` with value `hmac-sha256:<sig>`; remove "Verified Tamper-Evident" wording unless signed.

**Fail-closed.** Cap hit or unresolved scope -> report marked incomplete or 422; never a "complete" claim without full evidence.
**Red tests.** `tests/Autheris.Tests.Unit/Lineage/GdprDisclosureReportTests.cs` (new): `SubjectReport_DoesNotFilterBySubjectAsActor`,
`SubjectReport_ExcludesSubjectFromRecipients`, `SubjectReport_ListsOtherReaders` (HR analyst repro), `AuditCapHit_MarksIncomplete`,
`UnresolvedSubjectScope_Returns422`. `GdprAuditReportPdfExporterTests.cs`: `Seal_IsKeyedSignature_NotPlainSha256`,
`Seal_ChangesWhenRecipientRemoved`, `AllSixtyRecipientsRendered`, `NoSigner_NoTamperEvidentClaim`, `IncompleteReport_PrintsNotice`.
**Breaking.** Report contract and header name change. **Dependencies.** Audit area (repository paging signature).

---

## WP-M6 HTTP data source tenant pushdown (MISC-13 MEDIUM). Size S (~120 LOC)

**Root cause (verified).** `DeclarativeHttpDataSourceExecutor.cs:531` (query param) and `:661` (header) call `ResolveTenantClaim(principal)`
(`:599-605`: `tenant_id ?? tid ?? tenant`) and ignore `DataSourceExecutionContext.Tenant` (`IDataSourceExecutor.cs:29-40`).
**Fix.** Delete `ResolveTenantClaim`. Push down `context.Tenant.Value`. Missing `context.Tenant` -> `SecurityException`. If a raw tenant claim
exists and differs (ordinal) from `context.Tenant` and the tenant was not selected by ClusterAdmin header, still use `context.Tenant` and
emit a security audit event (no deny: realm binding is authoritative). Ensure all executor callers populate `Tenant` (grep `new DataSourceExecutionContext(`).
Add an architecture test: no `FindFirst("tenant_id"|"tid"|"tenant")` in `src/Autheris.Application/Services/*Executor*.cs`.
**Red tests.** `DeclarativeHttpTenantPushdownTests.cs` (new): `FixedRealmTenant_PushedInsteadOfTokenClaim` (partner-a repro, query + header),
`ClusterAdminSelectedTenant_Pushed`, `MissingContextTenant_FailsClosed`, `ClaimMismatch_EmitsAudit`; architecture test
`Executors_DoNotReadTenantClaimsDirectly`.
**Dependencies.** Identity area (realm TenantBinding semantics) only as consumer.

---

## WP-M7 Shadowing redaction (MISC-14 MEDIUM). Size M (~300 LOC)

**Root cause (verified).** `PiiShadowingRedactor.cs:47` numeric regex requires a preceding operator; `RedactPathAndQuery` (`:76-108`) keeps
the path; `RedactSqlOrQueryText` (`:153-163`) is regex only.
**Fix.**
1. GraphQL bodies: parse with `HotChocolate.Language.Utf8GraphQLParser` (already referenced); rewrite every `IntValueNode`, `FloatValueNode`,
   `StringValueNode`, `BooleanValueNode`, `EnumValueNode` argument/variable-default value to a typed placeholder; also redact `variables` JSON leaves.
2. SQL text: tokenize with the TrinoSqlEngine lexer; replace every numeric/string literal token. Keep `LIMIT/OFFSET` as `0`.
3. Path: replace every path segment that is not in `TrafficShadowingOptions.AllowedPathSegments` (default: route templates' static
   segments) with `_`; OData key segments `x(...)` become `x(_)`.
4. Unparseable GraphQL/SQL body -> do not shadow the request (drop and count metric `shadow_dropped_unparseable`).
**Red tests.** `ShadowingRedactionExt3Tests.cs`: `GraphQlInlineIntArgument_Redacted` (`patient(id: 4711)`), `GraphQlNestedEq_Redacted`
(insuranceNo repro), `GraphQlEnumAndBoolean_Redacted`, `SqlBetweenBounds_Redacted`, `SqlCaseThenLiteral_Redacted`, `RestPathKey_Redacted`,
`ODataKeySegment_Redacted`, `UnparseableBody_NotShadowed`, `StaticPath_Kept`.
**Breaking.** Shadow traffic loses literal fidelity (intended).

---

## WP-M8 Schema registry gate (MISC-16 MEDIUM). Size M (~300 LOC)

**Root cause (verified).** `SchemaLinter.Compare` (`SchemaLinter.cs:18-24`) only takes `ITypeDefinitionNode`; all maps use `ToDictionary`
(`:20,24,85,86,134,135,181,182,209,210`); `SchemaRegistrationRequestValidator.cs:20-33` checks syntax only.
**Fix.**
1. New `SchemaDocumentNormalizer.Normalize(DocumentNode) -> Result<NormalizedSchema, IReadOnlyList<string>>`: merges `ITypeExtensionNode`
   into their definitions (extension without definition = error), collects interfaces per type, directive definitions, schema/root operation
   types; reports duplicate types/fields/arguments/enum values/input fields/directives as errors.
2. Validator calls the normalizer; any error -> 400 at registration (first version included).
3. `SchemaLinter.Compare` works on `NormalizedSchema`; new breaking checks: removed implemented interface, removed directive definition or
   location, removed/changed root operation type, removed field from a merged extension. Duplicate-safe maps (normalizer guarantees uniqueness).
4. `SchemaRegistryService` (`:52`, `:129`): any exception from normalize/compare -> validation failure (`SCHEMA_COMPARE_FAILED`, 422), never 500.
**Red tests.** `SchemaRegistryAndLinterTests.cs`: `RemovingExtendTypeField_IsBreaking` (orders repro), `RemovingImplementedInterface_IsBreaking`,
`RemovingDirectiveDefinition_IsBreaking`, `ChangingRootQueryType_IsBreaking`, `DuplicateField_RejectedAtRegistration`,
`DuplicateType_RejectedAtRegistration`, `StoredBaselineWithDuplicates_CompareReturnsValidationFailureNot500`.

---

## WP-M9 Consumer-usage k-anonymity (MISC-17 MEDIUM). Size M (~250 LOC)

**Root cause (verified).** `ConsumerFolding.Fold` (`ConsumerFolding.cs:19-115`): `Math.Max(actorCount, distinctActorsByApp[app])` (`:42`),
`foldedDistinctActorsTotal += actorCount` (`:65`), merged bucket takes `foldedSummaries[0].Day` (`:81`). Caller passes window-wide
`actorCounts` (`LineageImpactAnalyzerService.cs:402`). `ConsumerAggregationStore.cs:238-248` emits no actor identities.
**Fix.**
1. Remove the `distinctActorsByApp` parameter; k is decided on the cell's own `DistinctActors`.
2. `ConsumerAppDailySummary` gets `[JsonIgnore] IReadOnlySet<string>? ActorKeys` (HMAC-pseudonymised actor ids, internal use only);
   `ConsumerAggregationStore` fills it. Folded bucket actor count = `|∪ ActorKeys|`; if any folded cell lacks `ActorKeys`, the folded bucket
   is suppressed (counts null, `SuppressedSmallGroups=true`).
3. Fold per day: one "Other / Aggregated" bucket per `Day`, each checked against k independently.
**Red tests.** `TableConsumersDualTierSecurityTests.cs` / new `ConsumerFoldingTests.cs`: `SingleActorDay_FoldedDespiteAppWideCount`
(CFO repro), `SameActorThreeDays_FoldedBucketCountsOneActor_Suppressed`, `FoldedBuckets_PerDay_NotMergedUnderFirstDay`,
`MissingActorKeys_FoldedBucketSuppressed`, `ActorKeys_NeverSerialized`.
**Breaking.** `Fold` signature and general-tier report shape (more suppression, per-day Other buckets).

---

## WP-M10 CI publish workflows (MISC-01, MISC-02 MEDIUM). Size S (~80 lines YAML + tests)

**MISC-01 (verified).** `.github/workflows/docker-publish.yml`: job `if` (`:22-27`) gates only `workflow_run`; dispatch accepts
`v*`/`latest` (`:127-142`) and pushes (`build-push-action push: true`) before cosign verify.
**Fix.** Job `if` adds `github.event_name != 'workflow_dispatch' || github.ref == 'refs/heads/main'`; on dispatch reject tags matching
`^v[0-9]` or `latest` (exit 1) and never add `:latest`; add `environment: release` (required reviewers) to the job; push by digest only
(`outputs: type=image,push-by-digest=true`), sign + verify, then `docker buildx imagetools create` the tags.
**MISC-02 (verified).** `release.yml`: checkout without `ref` (`:24-27`), `tag_name: inputs.tag || ref_name` (`:136`).
**Fix.** Checkout `ref: refs/tags/${{ inputs.tag || github.ref_name }}`; a first step fails unless the tag exists and (on dispatch) the
tag matches `^v[0-9]+\.[0-9]+\.[0-9]+`; fail if a release for the tag already exists (`gh release view`); `overwrite_files: false`;
`environment: release` on the publish job.
**Red tests.** `tests/Autheris.Tests.Unit/SupplyChainAndDeploymentSecurityTests.cs`: `DockerPublish_DispatchRestrictedToMain`,
`DockerPublish_DispatchRejectsReleaseTags`, `DockerPublish_TagsAppliedAfterVerify`, `DockerPublish_UsesProtectedEnvironment`,
`Release_CheckoutPinnedToTag`, `Release_FailsIfReleaseExists`, `Release_UsesProtectedEnvironment`.
**Dependencies.** Owner must create the `release` GitHub environment with reviewers (repo setting, outside code).

---

## Order and sizes

| Order | WP | Findings | Size |
|---|---|---|---|
| 1 | WP-M2 | MISC-06 (HIGH) | M-L |
| 2 | WP-M1 | MISC-03 (HIGH), MISC-04 | M |
| 3 | WP-M5 | MISC-11 (HIGH), MISC-12 | L |
| 4 | WP-M3 | MISC-07, 08, 09 | M |
| 5 | WP-M6 | MISC-13 | S |
| 6 | WP-M9 | MISC-17 | M |
| 7 | WP-M7 | MISC-14 | M |
| 8 | WP-M8 | MISC-16 | M |
| 9 | WP-M4 | MISC-05 | S-M |
| 10 | WP-M10 | MISC-01, 02 | S |

WP-M2 before WP-M3 (both touch `GovernedAggregationService.ValidationAndExecution.cs`). WP-M6, M7, M8, M10 are independent lanes.

## Security conditions for Phase 3

1. No path accepts a `TableAccessDecision` that did not pass the full PDP pipeline; metric consent can only cure `DenialKind.ConsentMissing`. No reason-string matching remains in analytics.
2. `RequiredFilterColumnRule` is the only implementation; no `CombinedRowFilterSql.Contains` remains in `src/` (architecture test). OLAP single- and cross-source deny with `REQUIRED_FILTER_COLUMN_MISSING`.
3. Every cache key that affects grouping includes all grouping inputs (TimeBuckets incl. time zone).
4. `RestrictNegatedFilters=true` is enforced by a tokenizer/typed tree, never regex; unparseable WHERE rejects.
5. GDPR report never claims completeness or tamper evidence without full evidence and a keyed signature; the subject is never treated as the recipient filter.
6. Executors push down only the gateway-resolved tenant; missing tenant fails closed.
7. Shadowing drops what it cannot parse; no literal and no non-allowlisted path segment reaches the shadow target.
8. Schema registry rejects duplicates at registration; compare failures are 4xx, never 500.
9. k-anonymity is decided per published cell on true distinct actors; missing actor identity suppresses.
10. OLAP memory configuration that over-commits the container fails `ValidateOnStart` outside Development.
11. Release tags and `latest` can only be produced from a tag ref / main under a protected environment; tags move only after signature verification.

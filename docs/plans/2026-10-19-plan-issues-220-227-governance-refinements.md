# Architectural Implementation Plan: Issues #220–#227 Governance Refinements

**Document ID:** `PLAN-GOV-220-227`  
**Date:** 2026-10-19  
**Status:** APPROVED (Phase 3 Security Reviewed)  
**Author:** Solution Architect & Security Lead (`csharp-architect`, `appsec-reviewer`)  
**Input PRD:** [`docs/plans/2026-10-19-req-issues-220-227-governance-refinements.md`](2026-10-19-req-issues-220-227-governance-refinements.md)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md)  

---

## 1. Architecture Decisions

### 1.1 Issue #220: Consumer Usage Recording Wiring
- **W-220-1**: In `SecurityContextFactory.ExtractCallerContext`, resolve `ClientId` from `context.User` using claim types `azp`, `appid`, `client_id`. Populate `CallerContext.ClientId`.
- **W-220-2**: Pass `effectiveCaller?.ClientId` to `CallerLabelNormalizer.NormalizeClientId` in `ConsumerUsageRecorder.RecordUsage`.
- **W-220-3**: Seam 1: In `GovernedQueryStages.RecordTableReadAsync`, accept optional `IConsumerUsageRecorder?`. Record `Allow` when `allowed: true`, `DenyAccess` when `allowed: false`.
- **W-220-4**: Seam 2: In `GatewayExecutionService` and `GovernedTreeQueryService`, record rows returned (`rowsReturned`, `rowsMeasured: true`) and record `DenyRateLimit` on `GatewayThrottledException`.

### 1.2 Issue #221: Required Filter Columns Enhancements
- **W-221-1 (Model & Repositories)**: In `Table` and `TableMetadata`, add `ImmutableArray<ImmutableArray<string>> RequiredFilterColumnGroups`. Persist as JSON `required_filter_column_groups_json` across SQLite, PostgreSQL, SQL Server.
- **W-221-2 (dbt Ingestion)**: In `DbtMetadataIngestionService` and `DbtSemanticManifestIngestionService`, parse `meta.required_filter_columns` or `meta.required_filters` as either array of strings or array of groups.
- **W-221-3 (API Endpoints)**: Add `PUT /api/v1/catalog/datasets/{id}/required-filters` in `CatalogApiEndpoints.cs` to update required filter columns and groups with GovernanceAdmin / TableOwner checks. Expose in `DatasetCatalogSettingsDto`.
- **W-221-4 (MCP describe_dataset)**: In `McpDatasetDescription`, add `IReadOnlyList<string>? RequiredFilterColumns` and `IReadOnlyList<IReadOnlyList<string>>? RequiredFilterColumnGroups`.
- **W-221-5 (Relation Join Enforcement)**:
  - In `AstSecurityVisitor.RequiredFilters.cs`: When checking join predicates in `CollectOnPredicates`, an equi-join on a foreign key (`a.id = b.a_id`) where `b` is joined to `a` counts as constraining `b.a_id` for table `b`.
  - In `TreeSqlCompiler.cs`: When evaluating child relation node, if the child relation joins on a column in `relation.ChildColumns`, that column is treated as satisfied by the parent relation join.

### 1.3 Issue #222: Bulk Stream Refinements
- **W-222-1 (Signed Cursors)**: `KeysetCursorEncoder` appends `.sig` where `sig = Base64Url(HMACSHA256(secret, payloadBytes))`. On decode, verify signature in constant time using `CryptographicOperations.FixedTimeEquals`. Reject invalid signature with `InvalidCursorException`.
- **W-222-2 (Body Metadata)**: In `TableStreamEndpoints.HandleBulkStreamAsync`, when NDJSON stream completes or truncates, append trailing control line:
  `{"_meta":{"nextCursor":"...","hasMore":bool,"truncatedReason":"...","rowsStreamed":long}}`.
- **W-222-3 (Dedicated Switch)**: Add `BulkStreamEnabled` to `DataOptions` (in `GatewayOptions.cs`) and `IReadPathSwitches`. In `GatewayApplicationBuilderExtensions`, map `MapTableStreamEndpoints()` only when `switches.BulkStreamEnabled` is `true`.

### 1.4 Issue #223: Cross-Source Relations Real Reader & Opt-In
- **W-223-1 (Real Row Reader)**: Implement `GovernedSqlCrossSourceRowReader : ICrossSourceRowReader`. For a given child table and keyset, constructs and executes a governed parameterized SQL query (`SELECT ... WHERE child_pk IN (...)`) using `ISqlConnectionFactory` and the target database connection, with masking and tenant filtering. Register in `DataApiServiceExtensions.cs`.
- **W-223-2 (Persisted Schema)**: Add `allow_cross_data_source INTEGER NOT NULL DEFAULT 0` column to `TABLE_RELATIONS` in `SqliteBaselineSchema.cs`, `PostgreSqlBaselineSchema.cs`, and `SqlServerBaselineSchema.cs`. Read and write in catalog repository relation methods.
- **W-223-3 (Approval Path)**: Add `SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, ClaimsPrincipal principal)` in repository and API, requiring GovernanceAdmin role or four-eyes table owner approval.

### 1.5 Issue #224: Cost Settlement Multiplicity & Recursion
- **W-224-1 (Single-Row Factor)**: In `QueryCostAnalyzerRule.CalculateSelectionSetCost`, when `isList` is true and `effectiveRows == 1`, set `multiplier = _payloadRowMultiplier` (or 1) instead of `_defaultListMultiplier`.
- **W-224-2 (Recursive CountResultRows)**: In `CostAndQuotaMiddleware.CountResultRows`, recursively traverse dictionaries, objects, and enumerables to sum all entities/rows returned across nested levels.

### 1.6 Issue #225: Query Analysis Caller Limits & Row Reporting
- **W-225-1 (Tier/Scope Limit Check)**: In `QueryAnalysisService`, when evaluating `totalEstimatedCost > maxAllowedCost`, obtain `maxAllowedCost` from `quotaCtx.Policy.MaxCostPerQuery` if available, falling back to `_analysisOptions.MaxAllowedCost`.
- **W-225-2 (Report Rows Per Request)**: Compute `estimatedRows` from AST limits or table `ExpectedRows` and populate `QueryCostEstimate.EstimatedRows`.

### 1.7 Issue #226: Configurable Request & Review Scopes
- **W-226-1 (ReadOnlyOperationMiddleware)**: In `ReadOnlyOperationMiddleware`, resolve `IOptions<GatewayOptions>` to inspect `options.Authentication.Jwt.ReadOnlySelfServiceScopes`. Check `principal.HasAnyScope(configuredScopes)` rather than hardcoding `"data.access.request"`.
- **W-226-2 (MutationTypes)**: In `ApproveConsentRequestAsync` and `RejectConsentRequestAsync`, resolve `options.Authentication.Jwt.AccessReviewScopes` (defaulting to `["data.access.review"]`). Check against configured scopes rather than hardcoding.

### 1.8 Issue #227: Consent Description on Approval
- **W-227-1**: In `MutationTypes.ApproveConsentRequestAsync`, add `string? description = null`.
- **W-227-2**: Validate and normalize description with `ConsentDescriptionText.Normalize`. Populate `Consent.Description = normalizedDescription`.

---

## 2. Binding Security & Compliance Conditions (Phase 3 Audit)

1. **SEC-BC-01 (HMAC Keyset Cursor Integrity)**: Keyset cursors MUST be cryptographically authenticated using HMAC-SHA256 with the cluster secret key. Unsigned, truncated, or tampered signatures MUST fail closed immediately with `INVALID_CURSOR` (HTTP 400), leaking no internal structure or stack trace.
2. **SEC-BC-02 (Fail-Closed Bulk Stream Switch)**: Disabling `BulkStreamEnabled` MUST immediately unmap the endpoint and reject attempts with HTTP 404/403.
3. **SEC-BC-03 (Cross-Source Four-Eyes Opt-In)**: Enabling `AllowCrossDataSource` on a relation requires authenticated authorization (GovernanceAdmin or dual table owner approval). Database schema migration must default `allow_cross_data_source` to `0` (false).
4. **SEC-BC-04 (Cross-Source Real Reader Masking Guard)**: The real `GovernedSqlCrossSourceRowReader` MUST apply column-level masking rules and tenant filters on all read batches before returning rows.
5. **SEC-BC-05 (Cost Settlement Boundedness)**: Recursive row counting in `CostAndQuotaMiddleware` MUST protect against cyclic graphs using a recursion depth cap (max depth 32) and clamp settled cost between `BaseCost` and `MaxCostPerQuery`.
6. **SEC-BC-06 (Caller Context Sanitization)**: `ClientId` extracted from claims MUST be validated and normalized via `CallerLabelNormalizer.NormalizeClientId` before telemetry buffering.
7. **SEC-BC-07 (Scope Configuration Fail-Closed)**: If configured `ReadOnlySelfServiceScopes` or `AccessReviewScopes` is empty or null, operations MUST fail closed.
8. **SEC-BC-08 (Description Sanitization)**: Consent description supplied on approval MUST be stripped of ASCII control characters and length-bounded to 500 characters.

---

## 3. Targeted Test Verification Strategy

- **Audit Telemetry Tests**: Verify `IConsumerUsageRecorder` records `Allow`, `DenyAccess`, `DenyRateLimit`, `DenyCost`, and row counts across read paths.
- **Required Filter Tests**: Verify dbt model ingestion of required filter columns, API updates, MCP discovery, column group disjunction satisfaction, and relation join satisfaction.
- **Bulk Stream Tests**: Verify NDJSON stream body includes `_meta` record with nextCursor and truncatedReason; verify HMAC cursor signing rejects tampered signatures; verify independent `BulkStreamEnabled` switch.
- **Cross-Source Relation Tests**: Verify real SQL reader queries target data source; verify persisted schema column and approval path.
- **Cost Settlement Tests**: Verify nested `first: 1` uses single-row factor; verify recursive row counting includes nested items in settled cost.
- **Query Analysis Tests**: Verify cost validation honors caller tier's `MaxCostPerQuery`; verify `EstimatedRows` reported.
- **Self-Service Scopes Tests**: Verify custom request and review scopes configured in `JwtOptions` are respected.
- **Consent Description Tests**: Verify `approveConsentRequest` sets `Consent.Description`.

---

## 4. Execution & Verification Results (Phases 4 & 5 Complete)

- **Phase 4 Implementation**:
  - Issue #220: Normalized `ClientId` extraction in `SecurityContextFactory`, added to `CallerContext`, passed through `GovernedQueryStages`, `GatewayExecutionService`, `GovernedTreeQueryService`, recorded via `ConsumerUsageRecorder`.
  - Issue #221: Added `RequiredFilterColumnGroups` to `Table`, `TableMetadata`, `McpDatasetCatalog`, `TableCatalog`, disjunction logic in `AstSecurityVisitor.RequiredFilters` and `TreeSqlCompiler`, dbt ingestion in `DbtMetadataIngestionService`, and REST endpoints `PUT /api/v1/catalog/datasets/{id}/required-filters`.
  - Issue #222: Added `BulkStreamOptions` and `BulkStreamEnabled` switch, HMAC-SHA256 signature verification in `KeysetCursorEncoder`, and `_meta` record emission in `TableStreamEndpoints`.
  - Issue #223: Added `GovernedCrossSourceRowReader` implementing `ICrossSourceRowReader`, added `allow_cross_data_source` column to SQLite, PostgreSQL, and SQL Server baseline schemas and shape files, updated `RelationRowAssembler`, added `SetRelationCrossDataSourceOptInAsync` to repositories and test doubles, and exposed `PUT /api/v1/catalog/relations/{relationId}/cross-data-source` with `CROSS_SOURCE_OPT_IN_CHANGED` audit logging.
  - Issue #224: Applied single-row multiplier in `QueryCostAnalyzerRule` when `isList` and `effectiveRows <= 1`, and implemented recursive bounded row counting (depth <= 32) in `CostAndQuotaMiddleware.CountResultRows`.
  - Issue #225: Added `EstimatedRows` to `QueryCostEstimate`, resolved caller tier `MaxCostPerQuery` via `IClientTierResolver` in `QueryAnalysisService`, and computed estimated rows across GraphQL and SQL queries.
  - Issue #226: Added `ReadOnlySelfServiceScopes` and `AccessReviewScopes` to `EntraIdAuthOptions`, checked in `ReadOnlyOperationMiddleware`, `ApproveConsentRequestAsync`, and `RejectConsentRequestAsync`.
  - Issue #227: Added `description` parameter to `ApproveConsentRequestAsync`, validated via `UntrustedText.Normalize`, assigned to `Consent.Description` and `ConsentRequestPayload.Description`.
- **Phase 5 Verification**:
  - `Issues220To227GovernanceRefinementsTests`: 11 passed (100%).
  - Regression tests (`CrossSourceRelationsSecurityTests`, `MustFixRemediationTests`, `SetConsentDescriptionTests`, `Issue113AccessRequestsSecurityTests`): 52 passed (100%).
  - Total test run: 63 passed, 0 failed.


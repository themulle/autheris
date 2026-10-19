# Requirements Specification: Issues #220–#227 Governance & Federation Refinements

**Document ID:** `REQ-GOV-220-227`  
**Date:** 2026-10-19  
**Status:** APPROVED  
**Author:** Delivery Project Manager & Product Owner (`project-manager`)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md)  
**Target Milestone:** v1.2.4  

---

## 1. Executive Summary

This specification consolidates the requirements for eight governance, federation, and FinOps refinements identified across recent production releases and GitHub issues #220 through #227.

| Issue | Feature Area | Problem Statement | Core Requirement |
|---|---|---|---|
| **#220** | Audit & Lineage (#114) | Usage is never recorded in runtime read paths, leaving consumer reports empty. | Wire `IConsumerUsageRecorder` into `GovernedQueryStages`, `GatewayExecutionService`, `GovernedSqlExecutor`, and `GovernedTreeQueryService`, resolving `ClientId` from claims. |
| **#221** | Catalog & Policy (#163) | Required filter columns cannot be declared via dbt/API or discovered via `describe_dataset`; column groups and relation joins are unsupported. | Support declaration via dbt model meta and REST API, expose in `describe_dataset`, support column groups (disjunction), and treat relation joins as valid filter constraints. |
| **#222** | Streaming Data (#178) | Continuation cursor and truncation reason are only in headers/trailers; cursor is unsigned; lacks its own read path switch. | Emit continuation cursor and truncation reason in stream response body, sign keyset cursors with HMAC-SHA256, and introduce dedicated `BulkStreamEnabled` switch. |
| **#223** | Cross-Source GraphQL (#75) | Production DI uses test `InMemoryCrossSourceRowReader`; cross-source opt-in is not persisted with approval path. | Implement real `GovernedCrossSourceRowReader` over data source connections, persist `allow_cross_data_source` in repository schemas, and provide approval path. |
| **#224** | FinOps & Cost (#177) | Nested `first: 1` lists use default list multiplier; post-execution settlement ignores nested result rows. | Use single-row factor (1) for nested `first: 1` selections, and recursively count nested result rows during cost settlement. |
| **#225** | Query Analysis (#175) | Analysis validates against static global cost limit instead of caller's tier/scope limit, and does not report rows per request. | Validate query cost against caller's tier/scope limit, and report estimated rows per request in analysis output. |
| **#226** | Self-Service Access (#113) | Configured request and review scopes in `JwtOptions` are ignored in favor of hardcoded strings. | Honor `ReadOnlySelfServiceScopes` and `AccessReviewScopes` configured in `JwtOptions`. |
| **#227** | Consents & Grants (#158) | `approveConsentRequest` mutation cannot set consent description on approval. | Add optional `description` parameter to `approveConsentRequest` and persist to `Consent.Description`. |

---

## 2. Detailed Functional Requirements

### 2.1 Issue #220: Consumers Per Calling Application Runtime Recording (#114)
- **FR-220-1**: `IConsumerUsageRecorder.RecordUsage` MUST be invoked during table read execution in:
  - `GovernedQueryStages.RecordTableReadAsync`: records `Allow` or `DenyAccess` based on the evaluation result.
  - `GatewayExecutionService`: records executed calls with measured row counts; records `DenyRateLimit` upon throttling.
  - `GovernedSqlExecutor`: records executed calls and row counts for single-table queries; records `DenyRateLimit` upon throttling.
  - `GovernedTreeQueryService`: records executed calls and row counts; records `DenyRateLimit` upon throttling.
  - `GovernedAggregationService`: records `DenyCost` when query cost exceeds budget.
- **FR-220-2**: `CallerContext` MUST support `ClientId` populated from authenticated claims (`azp`, `appid`, `client_id`). `ConsumerUsageRecorder` MUST pass `effectiveCaller?.ClientId` to `CallerLabelNormalizer.NormalizeClientId`.

### 2.2 Issue #221: Required Filter Columns Enhancements (#163)
- **FR-221-1 (dbt Declaration)**: `DbtMetadataIngestionService` and `DbtSemanticManifestIngestionService` MUST extract required filter columns and column groups from model `meta.required_filter_columns` or `meta.required_filters`.
- **FR-221-2 (API Declaration)**: Add REST API endpoints:
  - `PUT /api/v1/catalog/datasets/{id}/required-filters` to set/update required filter columns and column groups.
  - Expose required filter configuration in `DatasetCatalogSettingsDto`.
- **FR-221-3 (Discovery in describe_dataset)**: Update `McpDatasetDescription` to include `RequiredFilterColumns` and `RequiredFilterColumnGroups`.
- **FR-221-4 (Column Groups)**: Support alternative column groups (e.g. `[[tenant_id], [org_id, department_id]]`). A query satisfies required filtering if ANY group has all its columns constrained.
- **FR-221-5 (Relation Joins)**: Joining a child table to a parent table on a required filter column satisfies the requirement for the child table in both SQL AST visitor and GraphQL tree compiler.

### 2.3 Issue #222: Bulk Stream Refinements (#178)
- **FR-222-1 (Body Trailer/Metadata)**: For NDJSON streaming, emit a trailing metadata record `{"_meta":{"nextCursor":"...","hasMore":bool,"truncatedReason":"...","rowsStreamed":long}}` when streaming terminates or is truncated.
- **FR-222-2 (Signed Cursor)**: `KeysetCursorEncoder` MUST sign keyset cursors using HMAC-SHA256 with the cluster secret. Deserialization MUST verify the signature and reject tampered cursors with `InvalidCursorException` (`INVALID_CURSOR`).
- **FR-222-3 (Dedicated Switch)**: Introduce `BulkStreamEnabled` in `GatewayOptions.Data` and `IReadPathSwitches`. Bulk streaming endpoints MUST ONLY be mapped and active when `BulkStreamEnabled` is `true`.

### 2.4 Issue #223: Cross-Source Relations Real Reader and Opt-In Approval (#75)
- **FR-223-1 (Real Row Reader)**: Implement `GovernedCrossSourceRowReader` that resolves the target data source connection via `ISqlConnectionFactory` and executes governed queries. Register this as the production `ICrossSourceRowReader` in `DataApiServiceExtensions.cs`.
- **FR-223-2 (Persisted Schema)**: Add `allow_cross_data_source` column to `TABLE_RELATIONS` in SQLite, PostgreSQL, and SQL Server baseline schemas and repositories.
- **FR-223-3 (Approval Path)**: Provide an admin/owner approval API and service method `SetRelationCrossDataSourceOptInAsync` enforcing four-eyes or governance admin privileges.

### 2.5 Issue #224: Cost Settlement Refinements (#177)
- **FR-224-1 (Single-Row Factor)**: In `QueryCostAnalyzerRule.cs`, when a nested list field specifies `first: 1` (or `effectiveRows == 1`), the multiplier applied MUST be the single-row factor (`_payloadRowMultiplier`, default 1) rather than `_defaultListMultiplier` (10).
- **FR-224-2 (Nested Rows in Settled Cost)**: `CountResultRows` in `CostAndQuotaMiddleware.cs` MUST recursively traverse nested objects and lists to calculate the true total rows returned across the entire GraphQL response tree.

### 2.6 Issue #225: Query Analysis Tier Limits & Row Estimation (#175)
- **FR-225-1 (Tier/Scope Limit Check)**: In `QueryAnalysisService`, check `totalEstimatedCost` against the caller's resolved tier limit (`quotaCtx.Policy.MaxCostPerQuery`) or scope limit rather than only the static global default.
- **FR-225-2 (Report Rows Per Request)**: Include `EstimatedRows` in `QueryCostEstimate` and `QueryValidationReport` based on query limit arguments and catalog `ExpectedRows`.

### 2.7 Issue #226: Self-Service Access Request Configurable Scopes (#113)
- **FR-226-1 (Configurable Request Scopes)**: `ReadOnlyOperationMiddleware` MUST check whether the principal possesses any scope configured in `JwtOptions.ReadOnlySelfServiceScopes` (defaulting to `["data.access.request"]`), rather than checking hardcoded string `"data.access.request"`.
- **FR-226-2 (Configurable Review Scopes)**: `MutationTypes.ApproveConsentRequestAsync` and `RejectConsentRequestAsync` MUST check whether the principal possesses any scope configured in `JwtOptions.AccessReviewScopes` (defaulting to `["data.access.review"]`), rather than checking hardcoded string `"data.access.review"`.

### 2.8 Issue #227: Consent Description on Approval (#158 Part B)
- **FR-227-1**: Add `string? description = null` to GraphQL mutation `approveConsentRequest`.
- **FR-227-2**: Normalize description via `UntrustedText` / `ConsentDescriptionText` and assign it to `Consent.Description` upon creation.

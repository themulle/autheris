# Requirements Specification: Issue #240 Read-Path Switches for Metrics & Query Analysis

**Document ID:** `REQ-GOV-240`  
**Date:** 2026-10-19  
**Status:** APPROVED  
**Author:** Delivery Project Manager & Product Owner (`project-manager`)  
**Parent Plan:** [`docs/plans/00-master-plan-overview.md`](00-master-plan-overview.md)  
**Target Milestone:** v1.3.0 / v1.2.5  
**Branch:** `feat/issue-240-read-path-switches-metrics-and-analyze`  

---

## 1. Executive Summary & Problem Statement

In Autheris v1.1.16 / Issue #115 (`PLAN-SEC-115`), dedicated off-switches were introduced for read paths across REST data endpoints and MCP tool groups to give operators fine-grained control over accessible query attack surfaces.

During the v1.2.x cycle, two new governed read paths were added:
1. **GraphQL `queryMetrics` Root Field:** Evaluates metric aggregations over catalog tables. Currently, this field is registered in the GraphQL schema whenever tables exist, with no independent switch to omit the metrics surface.
2. **`POST /api/v1/governance/queries/analyze` Endpoint:** Evaluates pre-flight static query complexity and cost. Currently, this endpoint is mapped unconditionally, without an operator off-switch.

Issue #240 resolves this governance gap by introducing explicit, immutable startup switches for both read paths:
- `Gateway:GraphQL:Metrics:Enabled` (default: `true`)
- `Gateway:Governance:QueryAnalysis:Enabled` (default: `true`)

---

## 2. Functional Requirements

### FR-240-1: GraphQL `queryMetrics` Off-Switch
- **FR-240-1.1 (`GraphQLMetricsOptions`):** Add `GraphQLMetricsOptions Metrics` to `GraphQLOptions` (bound to `Gateway:GraphQL:Metrics`) with `bool Enabled { get; init; } = true`.
- **FR-240-1.2 (Schema Gating):** In `CatalogGraphQlTypeModule`, gate `MetricGraphQlTypes.Register(types)` and `MetricGraphQlTypes.AddQueryField(d)` on effective metrics enablement:
  - `metricsEnabled = options.Value.GraphQL.Metrics.Enabled && options.Value.GraphQL.Aggregation.Enabled`.
- **FR-240-1.3 (Fail-Closed Unknown Field):** When `metricsEnabled` is `false`, `queryMetrics` MUST NOT be registered in the GraphQL schema. Queries referencing `queryMetrics` will fail GraphQL validation with an unknown field error.

### FR-240-2: Governance Query Analysis Endpoint Switch
- **FR-240-2.1 (`QueryAnalysisOptions`):** Add `QueryAnalysisOptions QueryAnalysis` to `GovernanceOptions` (bound to `Gateway:Governance:QueryAnalysis`) with `bool Enabled { get; init; } = true`.
- **FR-240-2.2 (`IReadPathSwitches.QueryAnalysisEnabled`):** Extend `IReadPathSwitches` and `ReadPathSwitches` with `bool QueryAnalysisEnabled { get; }` reflecting `Gateway:Governance:QueryAnalysis:Enabled` (default `true`).
- **FR-240-2.3 (Unmapped Endpoint / HTTP 404):** In `QueryAnalysisAndProfilingEndpoints.MapQueryAnalysisAndProfilingEndpoints`, add `bool mapQueryAnalysis = true`. When `false`, the route `POST /api/v1/governance/queries/analyze` is not registered in the endpoint routing table, causing requests to return HTTP 404 Not Found naturally.
- **FR-240-2.4 (Application Builder Wiring):** In `GatewayApplicationBuilderExtensions`, pass `mapQueryAnalysis: switches.QueryAnalysisEnabled`.

---

## 3. Non-Functional & Security Requirements

- **SEC-SW240-01 (Immutable Startup Snapshot):**
  Like all read-path switches (PLAN-SEC-115), `Metrics.Enabled` and `QueryAnalysis.Enabled` are evaluated strictly once at application startup. No request-driven header, claim, or query parameter can override or toggle the switch.
- **SEC-SW240-02 (Fail-Closed Surface Reduction):**
  When a switch is disabled, the corresponding feature must be completely absent from the route/schema registry. No internal handler or endpoint logic may be invoked.
- **SEC-SW240-03 (Zero Regression on Defaults):**
  Default configuration (`Enabled = true` for both) ensures 100% backward compatibility for existing deployments.

---

## 4. Acceptance Criteria

1. **AC-240-1:** Default configuration retains both `queryMetrics` in GraphQL and `POST /api/v1/governance/queries/analyze` as operational.
2. **AC-240-2:** Setting `Gateway:GraphQL:Metrics:Enabled = false` removes `queryMetrics` from GraphQL schema, returning GraphQL validation error `Cannot query field "queryMetrics" on type "Query"`.
3. **AC-240-3:** Setting `Gateway:Governance:QueryAnalysis:Enabled = false` unmaps `POST /api/v1/governance/queries/analyze`, returning HTTP 404 Not Found.
4. **AC-240-4:** 100% test pass rate across unit and integration test suites.

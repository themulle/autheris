# Implementation Plan: Issue #240 Read-Path Switches for Metrics & Query Analysis

**Document ID:** `PLAN-GOV-240`  
**Date:** 2026-10-19  
**Status:** COMPLETED & VERIFIED ✅  
**Author:** Solution Architect (`project-manager`)  
**Parent PRD:** [`docs/plans/2026-10-19-req-issue-240-read-path-switches-metrics-and-analyze.md`](2026-10-19-req-issue-240-read-path-switches-metrics-and-analyze.md) (`REQ-GOV-240`)  
**Target Milestone:** v1.3.0 / v1.2.5  
**Branch:** `feat/issue-240-read-path-switches-metrics-and-analyze`  

---

## 1. Architectural Strategy & Design

Following the read-path switch pattern established in Issue #115 (`PLAN-SEC-115`), read-path off-switches must:
1. Be captured in an immutable snapshot at application startup (`ReadPathSwitches`).
2. Completely prevent endpoint route mapping or GraphQL schema registration when disabled.
3. Be completely un-bypassable at runtime.

### Work Packages

#### WP-240-1: GraphQL `queryMetrics` Switch (`Autheris.Domain` & `Autheris.GraphQL`)
1. **Options:**
   In `src/Autheris.Domain/Options/Protocols/GraphQlOptions.cs`:
   - Define `GraphQLMetricsOptions` with `public bool Enabled { get; init; } = true;`
   - Add `public GraphQLMetricsOptions Metrics { get; init; } = new();` to `GraphQLOptions`.
2. **Catalog GraphQL Type Module:**
   In `src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs`:
   - Add constructor parameter `bool metricsEnabled = true`.
   - Store `private readonly bool _metricsEnabled;`.
   - Wrap `MetricGraphQlTypes.Register(types);` with `if (_metricsEnabled)`.
   - Wrap `MetricGraphQlTypes.AddQueryField(d);` with `if (_metricsEnabled)`.
3. **DI Registration:**
   In `src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.cs`:
   - Pass `metricsEnabled: gatewayOptions.GraphQL.Metrics.Enabled && gatewayOptions.GraphQL.Aggregation.Enabled` to `CatalogGraphQlTypeModule`.

#### WP-240-2: Governance Query Analysis Endpoint Switch (`Autheris.Domain`, `Autheris.Application`, `Autheris.Api`)
1. **Options:**
   - Create `src/Autheris.Domain/Options/GovernanceOptions.cs` defining `GovernanceOptions` and `QueryAnalysisOptions` (`public bool Enabled { get; init; } = true;`).
   - Add `public GovernanceOptions Governance { get; init; } = new();` to `GatewayOptions`.
   - Add `GatewaySections.Governance = "Gateway:Governance";` and `GatewaySections.QueryAnalysis = "Gateway:Governance:QueryAnalysis";` in `GatewaySections.cs`.
2. **Read Path Switches Contract & Implementation:**
   - In `src/Autheris.Application/Mcp/Interfaces/IReadPathSwitches.cs`:
     - Add `bool QueryAnalysisEnabled { get; }`.
   - In `src/Autheris.Application/Mcp/Services/ReadPathSwitches.cs`:
     - Read `options.Governance?.QueryAnalysis?.Enabled ?? true`.
     - Implement `public bool QueryAnalysisEnabled { get; }`.
3. **Endpoint Routing:**
   - In `src/Autheris.Api/Endpoints/QueryAnalysisAndProfilingEndpoints.cs`:
     - Update signature to `MapQueryAnalysisAndProfilingEndpoints(this IEndpointRouteBuilder app, bool mapSampleRows = true, bool mapQueryAnalysis = true)`.
     - Guard `app.MapPost("/api/v1/governance/queries/analyze", ...)` with `if (mapQueryAnalysis)`.
   - In `src/Autheris.Api/Extensions/GatewayApplicationBuilderExtensions.cs`:
     - Pass `mapQueryAnalysis: switches.QueryAnalysisEnabled`.

#### WP-240-3: Comprehensive Verification Tests (`Autheris.Tests.Unit`)
1. Create `tests/Autheris.Tests.Unit/Governance/ReadPathSwitchesMetricsAndAnalyzeTests.cs` verifying:
   - Default options: `queryMetrics` present in schema, `/api/v1/governance/queries/analyze` mapped.
   - `Gateway:GraphQL:Metrics:Enabled = false`: `queryMetrics` omitted from schema, execution fails with unknown field error.
   - `Gateway:Governance:QueryAnalysis:Enabled = false`: `POST /api/v1/governance/queries/analyze` unmapped, returning 404 Not Found.
   - `IReadPathSwitches.QueryAnalysisEnabled` correctly reflects configuration.

---

## 2. Binding Security Conditions (SEC-SW240)

- **SEC-SW240-01 (Immutable Startup Snapshot):**
  Switches are resolved exclusively during host startup through `IReadPathSwitches`. Runtime modification is impossible.
- **SEC-SW240-02 (Zero Surface Leakage / Fail-Closed):**
  When disabled, no schema metadata, types, or endpoints exist. Probing returns 404 (for REST) or Unknown Field error (for GraphQL).
- **SEC-SW240-03 (Conservative Defaults):**
  Both switches default to `true` to avoid breaking changes on upgrade.

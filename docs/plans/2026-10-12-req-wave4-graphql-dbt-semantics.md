# Requirement / PRD: Wave 4 GraphQL Cursor Paging, dbt Semantics & ISO Temporal Types

**Document ID:** `REQ-WAVE4-GRAPHQL-DBT-SEMANTICS-2026-10-12`  
**Date:** 2026-10-12  
**Status:** APPROVED 🎯  
**Author:** Enterprise Product Manager (`product-manager`)  
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md), [2026-10-12-plan-github-issues-roadmap.md](2026-10-12-plan-github-issues-roadmap.md)  
**Target Delivery:** Wave 4 Non-AST Suite (#36, #39, #38 Stage 1)

---

## 1. Executive Summary & Problem Statement

Enterprises querying Autheris through GraphQL and AI Agent MCP gateways face three key operational challenges:
1. **Timezone Shifts & Missing Column Semantics (#36):** Dates and timestamps returned as generic strings cause ambiguity and timezone conversion errors in frontend and BI tools. Furthermore, rich semantic metadata configured in dbt (e.g. `meta.role = "measure"`, `meta.unit = "EUR"`, `meta.business_name = "Gross Revenue"`) is stripped during catalog synchronization and not available to AI agents.
2. **Result Set Exhaustion & Lack of Stable Cursor Pagination (#39):** Fetching large tabular datasets in GraphQL either hits hard query row/cost limits or causes memory bloat. Clients need standard Relay cursor-based pagination (`first`, `after`, `pageInfo { hasNextPage, endCursor }`) with an explicit machine-readable `truncated` flag when cost or tier-based row caps are reached.
3. **dbt Semantic Layer & Metric Catalog Ingestion (#38 Stage 1):** Organizations standardizing metric definitions via dbt `semantic_manifest.json` require Autheris to ingest and expose semantic metrics, dimensions, entities, and time grains through the catalog and MCP discovery tools (`list_metrics`, `describe_metric`).

---

## 2. Scope & Functional Requirements

### FR-01: ISO Temporal Scalars & Type Mapping (#36)
- Provide standard HotChocolate scalars:
  - `Date`: ISO 8601 calendar date formatted strictly as `YYYY-MM-DD`.
  - `DateTime`: ISO 8601 extended timestamp with timezone offset (`YYYY-MM-DDTHH:mm:ss.fffZ` or `+00:00`).
  - `Time`: ISO 8601 time of day formatted as `HH:mm:ss`.
- Automatic type mapping for table metadata and dynamic GraphQL schema fields based on SQL/Trino data types (`DATE`, `TIMESTAMP`, `TIME`).

### FR-02: dbt Column Semantics & Metadata Passthrough (#36)
- Ingest and preserve `meta` dictionaries from dbt models in `DbtColumnDefinition` and `TableColumn`.
- Store and expose structured properties:
  - `Role`: `measure`, `dimension`, `time_dimension`, `entity`.
  - `Unit`: e.g. `EUR`, `USD`, `pct`, `ms`.
  - `Description`: Business documentation.
- Expose these semantics in `McpDatasetDescription` (`describe_dataset`) and GraphQL column metadata.

### FR-03: Relay Cursor Paging & Truncation Signal (#39)
- Support Relay cursor pagination on dataset queries:
  - Arguments: `first: Int`, `after: String`.
  - Return types: `Connection` with `edges { node, cursor }` and `pageInfo { hasNextPage, hasPreviousPage, startCursor, endCursor }`.
- Opaque, tamper-resistant base64 cursors encoding row offsets/keys.
- Emit a GraphQL response extension `extensions.pagination { isTruncated: Boolean, maxRowsReached: Boolean, returnedRows: Int }` when results are clamped by tier cost/quota limits.

### FR-04: dbt Semantic Manifest Ingestion & Catalog Discovery (#38 Stage 1)
- Model `DbtSemanticManifest` containing `SemanticModels`, `Metrics`, `Dimensions`, `Measures`, and `Entities`.
- Ingest `semantic_manifest.json` via API and file streaming.
- Expose MCP tools:
  - `list_metrics`: Lists available metrics with description, time grains, and required dimensions.
  - `describe_metric`: Detailed definition, underlying measures, filter expressions, and owners.

---

## 3. Non-Functional & Zero-Trust Constraints

1. **Zero AST Dependency:** All components operate at the Application, Catalog, MCP, and GraphQL presentation layers without altering or coupling to the AST dialect generator compiler engine.
2. **Fail-Closed & Anti-Oracle:** Catalog queries for metrics or columns on datasets that the caller does not have permission to view are strictly filtered out without leaking schema existence.
3. **Performance & Allocations:** Zero unnecessary string allocations in cursor serialization; efficient UTF-8 streaming for `semantic_manifest.json`.

# F-GOV-24: Self-Service Access Requests, Read-Only Token Gating & Governed Telemetry

## 1. Executive Summary & Problem Statement

In zero-trust analytical and agentic data architectures, users and autonomous AI agents routinely operate with restricted read-only tokens (`TokenAccessScope.ReadOnlyScopes`, e.g., `Agent.Read`, `App.Read`). When an analyst or agent discovers a protected dataset for which they lack query permission, they need to submit an access request with operational context (such as calling application, tool, justification, and desired lease duration) and withdraw stale requests.

Historically, gateway governance suffered from significant operational friction and blind spots:
1. **Coarse-Grained Write Blocking:** Read-only tokens were unconditionally blocked from executing *all* GraphQL mutations (`READ_ONLY_TOKEN`). Users and agents were forced to obtain high-privilege write tokens simply to request data access, violating least privilege.
2. **Static Approvals Without Row Scoping:** Data owners approving consent requests could only grant full-table access or static metadata snapshots without fine-grained row-level filters or column masking overrides, leading to over-granting.
3. **Untrusted Caller Telemetry & Privacy Risks:** Telemetry headers (`X-Caller-Application`, `X-Caller-Query-Name`) supplied by upstream clients were unvalidated, creating spreadsheet formula injection risks and leaking personal user identifiers (SIDs) across application consumers without $k$-anonymity aggregation.
4. **Ungoverned Stored Procedures:** Stored procedures lacked unified catalog discovery in MCP tools, exposure to AI agents, and anti-enumeration defenses, creating existence oracles.
5. **Full-Table Scans on Massive Datasets:** Massive historical and multi-tenant tables lacked mandatory filter enforcement, allowing clients to initiate unbounded full-table scans that exhausted database resources.
6. **Untyped Aggregation in GraphQL:** GraphQL aggregation relied on untyped strings for filtering and grouping, lacking strong compile-time schema validation and predictable cost analysis.

`F-GOV-16` resolves these governance challenges through an integrated self-service, telemetry, and read-path security framework:
- **Bounded Self-Service for Read-Only Tokens:** Permits read-only tokens carrying `data.access.request` to execute allowlisted access request mutations (`requestTableAccess`, `withdrawAccessRequest`), strictly rejecting all other mutations and denying non-human app-only callers.
- **Dynamic Approver Row Filters & Atomic Activation:** Enables data owners to specify dynamic row filters (`rowFilter`) and column masks during approval, executing consent creation, approval recording, and table epoch cache invalidation in a single atomic transaction under strict Four-Eyes segregation of duties (`RequesterSid != ApproverSid`).
- **Normalized Caller Telemetry & Dual-Tier Privacy Aggregation:** Sanitizes caller headers against formula injection and aggregates daily consumer usage under dual-tier authorization: detailed views for data owners/admins, and $k$-anonymized ($k \ge 3$) folded views for general consumers.
- **Governed Stored Procedures in Catalog & MCP:** Exposes stored procedures in MCP discovery (`list_datasets`, `describe_dataset`) and execution (`call_procedure`) with anti-enumeration 404 protection and column masking.
- **Mandatory Required Filter Columns:** Enforces declared required filter columns across every query path in the gateway (WebSQL, GraphQL, REST, Arrow, OData), failing closed if any required column is unconstrained.
- **Typed Aggregations in GraphQL:** Provides strongly typed schema fields (`<table>_aggregate`) with typed filter inputs (`<table>_filter`), grouping enums (`<table>_group_by`), and typed metric objects (`count`, `sum`, `avg`, `min`, `max`).

---

## 2. Architecture & Component Wiring

```mermaid
flowchart TD
    subgraph ClientIngress ["Client Ingress & Token Scopes"]
        ReadOnlyAgent["AI Agent / User<br/>(Read-Only Token + data.access.request)"]
        OwnerClient["Data Owner<br/>(Delegated Token + data.access.review)"]
        QueryClient["Analytics Consumer<br/>(X-Caller-Application Headers)"]
    end

    subgraph SecurityPipeline ["Security & Interceptor Pipeline"]
        RoMiddleware["ReadOnlyOperationMiddleware<br/>(SelfServiceMutationClassifier)"]
        CallerNormalizer["CallerLabelNormalizer<br/>(Sanitization & Anti-Formula Injection)"]
        RequiredFilterValidator["AstSecurityVisitor & GovernedTableReadPlanner<br/>(Required Filter Columns Gate)"]
    end

    subgraph GovernanceServices ["Governance & Access Decision Engine"]
        ConsentService["ConsentRequestService<br/>(Request & Withdrawal State Machine)"]
        ApprovalHandler["ApproveConsentRequest<br/>(Four-Eyes & RowFilterAstValidator)"]
        UsageRecorder["ConsumerUsageRecorder<br/>(In-Memory Telemetry Aggregator)"]
        ConsumerFoldingEngine["ConsumerFolding<br/>(k-Anonymity k=3 Folding Engine)"]
        ProcedureRegistry["ProcedureRegistry & McpToolExecutionHandler<br/>(Governed Procedure Execution)"]
        TypedAggregateResolver["TypedAggregateResolver<br/>(Hot Chocolate GraphQL Schema)"]
    end

    subgraph StorageLayer ["Governance Storage & Caches"]
        GovRepo["IGovernanceRepository<br/>(Atomic Transaction Engine)"]
        EpochCache["POLICY_EPOCHS<br/>(Cache Invalidation Table)"]
        UsageStore["ConsumerAggregationStore<br/>(Daily Usage Aggregates)"]
    end

    ReadOnlyAgent -->|Mutation: requestTableAccess| RoMiddleware
    RoMiddleware -->|Self-Service Whitelist| ConsentService
    ConsentService --> GovRepo

    OwnerClient -->|Mutation: approveConsentRequest(rowFilter)| ApprovalHandler
    ApprovalHandler -->|Atomic Commit & Invalidate| GovRepo
    ApprovalHandler -->|Invalidate Table Epoch| EpochCache

    QueryClient -->|Read Query + Headers| CallerNormalizer
    CallerNormalizer --> UsageRecorder
    UsageRecorder --> UsageStore
    UsageStore --> ConsumerFoldingEngine

    QueryClient -->|Query Execution| RequiredFilterValidator
    QueryClient -->|GraphQL: <table>_aggregate| TypedAggregateResolver
    ReadOnlyAgent -->|MCP: call_procedure| ProcedureRegistry
```

### 2.1 Component Responsibilities

| Component | Responsibility |
|---|---|
| [`SelfServiceMutationClassifier`](file:///root/autheris/src/Autheris.GraphQL/Interceptors/SelfServiceMutationClassifier.cs) | Inspects GraphQL mutation AST root fields; permits execution for read-only tokens if and only if root fields are exclusively in `[requestTableAccess, withdrawAccessRequest]`. |
| [`ReadOnlyOperationMiddleware`](file:///root/autheris/src/Autheris.GraphQL/Interceptors/ReadOnlyOperationMiddleware.cs) | Enforces read-only token gating; validates `data.access.request` scope and human identity (`SubjectType.Person`). |
| [`ConsentRequestService`](file:///root/autheris/src/Autheris.Application/ConsentRequests/ConsentRequestService.cs) | Manages the access request lifecycle (`REQUESTED`, `APPROVED`, `REJECTED`, `WITHDRAWN`), validating justifications and lease durations. |
| [`RowFilterAstValidator`](file:///root/autheris/src/Autheris.Application/Governance/Validation/RowFilterAstValidator.cs) | Validates approver-supplied row filter AST expressions against target table column types, operators, and catalog constraints. |
| [`CallerLabelNormalizer`](file:///root/autheris/src/Autheris.Application/Security/CallerLabelNormalizer.cs) | Sanitizes `X-Caller-Application`, `X-Caller-Version`, `X-Caller-Query-Name`, and client IDs: enforces length bounds, safe character sets, and strips formula injection prefixes (`=`, `+`, `-`, `@`, `\t`, `\r`). |
| [`ConsumerUsageRecorder`](file:///root/autheris/src/Autheris.Application/Observability/ConsumerUsageRecorder.cs) & [`ConsumerAggregationStore`](file:///root/autheris/src/Autheris.Infrastructure/Persistence/ConsumerAggregationStore.cs) | Records and aggregates caller telemetry in memory, flushing daily summaries (calls, rows returned, rejections) to the governance database. |
| [`ConsumerFolding`](file:///root/autheris/src/Autheris.Application/Lineage/ConsumerFolding.cs) | Implements $k$-anonymity ($k=3$) privacy folding for non-owner consumers; folds small application caller groups into an aggregate bucket and suppresses personal SIDs. |
| [`McpDatasetCatalog`](file:///root/autheris/src/Autheris.Application/Mcp/Services/McpDatasetCatalog.cs) & [`McpToolExecutionHandler`](file:///root/autheris/src/Autheris.Application/Mcp/Services/McpToolExecutionHandler.cs) | Catalogues stored procedures alongside datasets, enforcing anti-enumeration 404 responses and executing procedures via MCP tool `call_procedure`. |
| `AstSecurityVisitor.RequiredFilters` & [`GovernedTableReadPlanner`](file:///root/autheris/src/Autheris.Application/Data/Services/GovernedTableReadPlanner.cs) | Enforces declared required filter columns across AST compiler and query planners, rejecting unconstrained queries with `REQUIRED_FILTER_COLUMN_MISSING`. |
| [`AggregateGraphQlTypes`](file:///root/autheris/src/Autheris.GraphQL/Catalog/AggregateGraphQlTypes.cs) & [`AggregateArgumentParser`](file:///root/autheris/src/Autheris.GraphQL/Catalog/AggregateArgumentParser.cs) | Exposes strongly typed `<table>_aggregate` GraphQL fields, parsing typed where filters and group-by enums with privacy-safe metric mapping. |

---

## 3. Technical Deep-Dive

### 3.1 Bounded Self-Service Mutations for Read-Only Tokens (Issue #113)

When an analyst or AI agent operating with a read-only token encounters a restricted dataset:
1. **Root-Field Inspection:** The GraphQL operation is intercepted by `ReadOnlyOperationMiddleware`. If the principal is classified as read-only via `TokenAccessScope.IsReadOnly()`, `SelfServiceMutationClassifier` inspects the AST field selections on the mutation root.
2. **Strict Whitelist:** If the mutation document contains *any* field other than `requestTableAccess` or `withdrawAccessRequest`, the request is immediately rejected with HTTP 403 `READ_ONLY_TOKEN`.
3. **Scope Authorization:** The caller must hold the explicit self-service scope configured in `Gateway:Security:ReadOnlySelfServiceScopes` (default: `["data.access.request"]`).
4. **Human Principal Verification:** The principal must resolve to `SubjectType.Person`. Application-only service principals (`idtyp=app` without delegated user context) and machine tokens are denied fail-closed with HTTP 403 `FORBIDDEN`.
5. **Withdrawal:** A requester can withdraw their own pending request via `withdrawAccessRequest`, transitioning the request status to `WITHDRAWN`.

### 3.2 Dynamic Approvals with Row Filters & Four-Eyes Segregation of Duties

When a data owner or delegate reviews an access request:
1. **Four-Eyes Segregation:** The approver must hold the `data.access.review` scope and cannot be the same individual who requested access (`RequesterSid != ApproverSid`).
2. **Dynamic Row Filters & Column Masking:** The approver can restrict the grant by providing an optional `rowFilter` (e.g. `department = 'Sales' AND region = 'EMEA'`) and `columnMasking` overrides.
3. **AST Validation:** `RowFilterAstValidator` parses the filter AST, verifying that all referenced columns exist in the target table, match data types, and use permitted operators.
4. **Atomic Transactional Activation:** The approval step (`APPROVAL_STEPS`), consent record (`CONSENTS`), column masking rules (`CONSENT_COLUMN_RULES`), and row filters (`CONSENT_ROW_FILTERS`) are inserted inside a single atomic database transaction. Upon commit, the table epoch in `POLICY_EPOCHS` is bumped, immediately invalidating distributed policy caches.

### 3.3 Untrusted Caller Telemetry & Dual-Tier Privacy Aggregation (Issue #114)

Client applications transmit telemetry via HTTP headers:
- `X-Caller-Application`: Name of the calling system (e.g., `bi-dashboard`).
- `X-Caller-Version`: Client application version (e.g., `v2.4.1`).
- `X-Caller-Query-Name`: High-level operation or report identifier (e.g., `monthly_revenue_report`).

`CallerLabelNormalizer` cleanses these inputs:
- Limits string length (64 chars for application/query name, 32 chars for version).
- Permits only alphanumeric characters, dots, underscores, dashes, and spaces.
- Strips leading formula characters (`=`, `+`, `-`, `@`, `\t`, `\r`) to eliminate CSV/Excel spreadsheet injection vulnerabilities.
- Unset or invalid headers default to safe sentinels (`"unknown"`, `"invalid"`).

**Dual-Tier Consumer Reporting:**
- **Tier 1 (Data Owners / Admins):** Full unmasked reporting including user SIDs (`ActorSids`), exact query names, call counts, rows returned, and categorised rejections (`rate_limit`, `cost`, `access`).
- **Tier 2 (General / Non-Owners):** Privacy-minimized aggregation via `ConsumerFolding` enforcing $k$-anonymity ($k=3$). Applications with fewer than 3 distinct human actors are folded into an aggregate bucket (`"Other / Aggregated"`), personal SIDs are set to `null`, and specific query names are suppressed.

### 3.4 Governed Stored Procedures in Catalog & MCP (Issue #153)

Database stored procedures are first-class governed catalog entities:
- **Discovery:** Listed alongside tables in MCP tools `list_datasets`, `describe_dataset`, and `search_catalog` when granted to the caller.
- **Anti-Enumeration 404:** Non-visible or ungranted procedures return HTTP 404 / Not Found with normalized execution latency, preventing attackers from probing procedure existence.
- **MCP Execution (`call_procedure`):** Invoked via the dedicated MCP tool `call_procedure`. Input arguments are validated against procedure schema types, checked for SQL injection patterns, and execution results are filtered through active column masking rules.

### 3.5 Universal Required Filter Columns (Issue #163)

Administrators can configure `RequiredFilterColumns` on large, partitioned, or sensitive tables (e.g. `tenant_id`, `created_at`, `region`):
- **Universal Enforcement:** Enforced across all query paths: WebSQL / AST compiler (`AstSecurityVisitor.RequiredFilters`), GraphQL / Tree SQL (`TreeSqlCompiler`), Governed Table Read Planner (`GovernedTableReadPlanner`), Federated Staging (`FederatedStagingService`, `CrossSourcePlanner`), Arrow export, and OData.
- **Fail-Closed Validation:** A query is rejected with `REQUIRED_FILTER_COLUMN_MISSING` unless every required filter column is constrained by at least one of:
  - An explicit client `WHERE` equality or range conjunct.
  - An active tenant column binding.
  - An active Row-Level Security (RLS) predicate referencing the column.
  - An active Virtual Filter or pushed filter.

### 3.6 Typed Aggregations in GraphQL (Issue #112)

Replaces loose string-based aggregation with strongly typed GraphQL schema fields:
- **Root Field:** `<table>_aggregate(where: <table>_filter, groupBy: [<table>_group_by!])`.
- **Typed Filters:** `<table>_filter` provides typed input fields (`eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `in`, `like`) per column.
- **Typed Return Object:** Exposes metric subfields:
  - `count`: Integer count of matching records.
  - `sum`: Object with numeric properties corresponding to table numeric columns.
  - `avg`: Object with floating-point properties.
  - `min` / `max`: Object with comparable column properties.
- **Read-Only Token Gating:** Permitted for read-only tokens; masked columns are rejected fail-closed (`AGGREGATION_ON_MASKED_COLUMN`).
- **Privacy Protections:** Integrated with $k$-anonymity minimum group size and differential privacy noise keys (`NoisyCacheKey`).

---

## 4. Configuration & Options

Configure self-service scopes, telemetry, and aggregation options in `appsettings.json`:

```json
{
  "Gateway": {
    "Security": {
      "ReadOnlyScopes": [ "Agent.Read", "App.Read" ],
      "ReadOnlySelfServiceScopes": [ "data.access.request" ]
    },
    "Observability": {
      "ConsumerTelemetry": {
        "Enabled": true,
        "FlushIntervalSeconds": 60,
        "MinActorsKAnonymity": 3
      }
    }
  },
  "Analytics": {
    "Aggregation": {
      "Enabled": true,
      "MaxGroupByColumns": 4,
      "MaxAggregates": 16,
      "MaxGroups": 1000
    },
    "Privacy": {
      "DefaultMinGroupSize": 5,
      "SuppressionMode": "Suppress"
    }
  }
}
```

---

## 5. Usage Examples

### 5.1 Requesting Table Access with a Read-Only Token

```graphql
mutation RequestAccessWithReadOnlyToken {
  requestTableAccess(
    domain: "finance"
    schema: "dbo"
    tableName: "general_ledger"
    justification: "Quarterly audit reconciliation for EMEA region"
    durationDays: 30
    idempotencyKey: "req-rec-2026-q4-001"
  ) {
    requestId
    status
    message
  }
}
```

**Response:**
```json
{
  "data": {
    "requestTableAccess": {
      "requestId": "e1f4b8a2-9382-4217-b76b-92864ef01822",
      "status": "REQUESTED",
      "message": "Consent request submitted successfully."
    }
  }
}
```

### 5.2 Approving Access with Dynamic Row Filters & Column Masking

```graphql
mutation ApproveWithRowFilter {
  approveConsentRequest(
    requestId: "e1f4b8a2-9382-4217-b76b-92864ef01822"
    rowFilter: {
      column: "region"
      operator: "EQUALS"
      value: "EMEA"
    }
    columnMasking: {
      masks: [
        { column: "tax_identifier", maskType: "REDACT" },
        { column: "bank_account", maskType: "PARTIAL_MASK" }
      ]
    }
  ) {
    requestId
    status
    message
  }
}
```

### 5.3 Typed GraphQL Aggregation Query

```graphql
query GetRegionalSalesSummary {
  sales_orders_aggregate(
    where: { status: { eq: "COMPLETED" }, created_date: { gte: "2026-01-01" } }
    groupBy: [REGION]
  ) {
    groupBy {
      region
    }
    count
    sum {
      order_total
    }
    avg {
      order_total
    }
  }
}
```

### 5.4 Calling a Governed Stored Procedure via MCP

```json
{
  "jsonrpc": "2.0",
  "id": "call-1",
  "method": "tools/call",
  "params": {
    "name": "call_procedure",
    "arguments": {
      "procedure": "finance.dbo.calculate_vat_summary",
      "parameters": {
        "tax_year": 2026,
        "region_code": "DE"
      }
    }
  }
}
```

---

## 6. Security, Zero-Trust & Fail-Closed Behavior

1. **Self-Service AST Isolation:** `ReadOnlyOperationMiddleware` evaluates the exact syntax tree of incoming mutations. Attempting to execute unauthorized mutations alongside `requestTableAccess` in a single document fails the entire operation with HTTP 403 `READ_ONLY_TOKEN`.
2. **Anti-Automation Human Gate:** Machine accounts, service principals (`idtyp=app`), and unauthenticated callers cannot submit or decide access requests, blocking autonomous privilege escalation loops.
3. **Four-Eyes Segregation of Duties:** An access request can never be approved by its requester (`RequesterSid != ApproverSid`), guaranteeing independent supervisory authorization.
4. **Formula Injection Neutralization:** Telemetry headers are stripped of formula-triggering characters, preventing malicious actors from weaponizing caller names or query identifiers against downstream spreadsheet consumers.
5. **Universal Required Filter Guard:** Full-table scans on massive or partitioned tables are blocked across all protocols. If a required filter column is omitted, the query fails immediately with `REQUIRED_FILTER_COLUMN_MISSING`.
6. **Anti-Enumeration Parity:** Stored procedure catalog tools return HTTP 404 for ungranted procedures with uniform response timings, preventing attackers from mapping hidden administrative procedures.

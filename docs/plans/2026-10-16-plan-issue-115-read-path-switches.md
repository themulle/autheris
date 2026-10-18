# Implementation Plan: REQ-SEC-115 - Switches for the Remaining Read Paths (REST Data Endpoint, MCP Tool Groups)

**Document ID:** `PLAN-SEC-115` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-SEC-115](2026-10-16-req-issues-111-115-catalog-access-analytics.md) section 6 (decisions D-115-1..4, FR-115-1..6, SI-115-1..4, AC 1..6, questions Q-115-1..3, verification item T-115-1) and section 8
**Branch:** `feat/issue-115-read-path-switches` (planned; base `main`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md) | **Requirement:** `REQ-SEC-115` | **Delivery tracker:** [`PLAN-DELIVERY-W23`](2026-10-16-plan-waves-2-3-delivery.md) (step P-5, implementation step I-4)
**Follow-up pointer (intake 2026-10-17):** a later MCP tool to call governed procedures ([`REQ-PROC-153`](2026-10-17-req-issues-152-154-governed-procedures.md) INC-5, wave 4) belongs to the `Query` tool group defined here.
**Status:** Phase 4 implemented (conditions C-115-1..12 satisfied); Phase 5 code review APPROVED WITH FINDINGS (findings fixed 2026-10-17)

**Binding project rules applied:** no upgrade/repair migrations (this change has no persistence); fail closed; defaults never turn a path on or off compared with today; CI stays fast (no sleeps, shared fixtures); Phase-4 TDD developer is Sonnet; the SqlGate/AST security suites are not touched; `AstSecurityVisitor.cs` is not modified.

**Owner guiding rule (applies to every decision below):** rights are checked by the backend, not by the calling agent; an agent must never widen its own reach. Consequence for this plan: a switch is read from startup configuration only, it is never influenced by a request, and a switched-off path is off on every route to it, including composition routes (`invoke_api`, resources, prompts, `describe_api`).

**Open questions Q-115-1..3 take the PRD defaults** (change-token and system virtual tables follow the REST data endpoint switch; groups plus a `DisabledTools` list; `describe_api` follows the `Api` group). Section 1.3 lists four verified gaps in the PRD that this plan closes and that the owner may overrule (section 9).

---

## 1. Verification of the PRD Against the Code (`635ac4e0`)

### 1.1 Existing read-path switches (what is already switchable and how "off" behaves)

| Path | Switch (option, default) | Off behaviour | Evidence |
|---|---|---|---|
| OData v4 | `Gateway:OData:Enabled` (`true`) | Routes **not mapped** (router 404) | `GatewayApplicationBuilderExtensions.cs:200-203`; `ODataOptions.cs:5` |
| Arrow export | `Gateway:Arrow:Enabled` (`true`) | Routes not mapped | `GatewayApplicationBuilderExtensions.cs:216-219`; `ODataOptions.cs:26` |
| Arrow Flight SQL | `Arrow:Enabled && Arrow:EnableFlightSql` | Routes not mapped | `GatewayApplicationBuilderExtensions.cs:220-223` |
| DuckDB OLAP | `Gateway:DuckDbOlap:Enabled` (`true`) | Routes not mapped (the endpoint class additionally re-checks `options.Enabled`) | `GatewayApplicationBuilderExtensions.cs:224-227`; `DuckDbOlapEndpoints.cs:45`; `DistributedCacheOptions.cs:60` |
| Iceberg REST catalog | `Gateway:Lakehouse:Enabled` | Routes not mapped | `GatewayApplicationBuilderExtensions.cs:228-231` |
| SQL endpoints | `Gateway:SqlEndpoints:Enabled` (`true`) | Loader does not run (`LoadSqlEndpoints`) | `GatewayApplicationBuilderExtensions.cs:253-257` |
| MCP (whole endpoint) | `Gateway:Mcp:Enabled` (`false`) | `MapMcpEndpoints` returns before mapping; `AddGatewayMcpServer` returns before registering the SDK | `McpEndpoints.cs:46-49`; `GatewayMcpServer.cs:39-42` |
| WebSQL and Trino protocol (`/api/v1/sql`, `/api/sql`, `/v1/statement/*`) | `Gateway:WebSql:Enabled` (`false`) | Routes are **always mapped** (`WebSqlEndpoints.cs:44-74`); the switch is a **runtime check** inside the governed executor: no data source is queryable (`GovernedSqlExecutionService.IsDataSourceQueryable`, `GovernedSqlExecutionService.cs:274-281`); the switch also removes the WebSQL OpenAPI entry (`GatewayCoreServiceExtensions.cs:41`, `DynamicOpenApiGenerator.cs:96-98`) and two hosted startup services (`GatewaySqlEngineServiceExtensions.cs:77-83`) | cited |
| GraphQL | **no switch** (always mapped) | n/a | `GatewayApplicationBuilderExtensions.cs:143` (`app.MapGraphQL`) |
| REST data endpoint | **no switch** (this issue) | n/a | `GatewayApplicationBuilderExtensions.cs:234` |

Conclusions: (a) the established "off" behaviour for a mapped-at-startup protocol is **not mapped, router 404, no body**; (b) all existing switches are **startup-only** (decided once from the bound `GatewayOptions` in `MapGatewayEndpoints`, or in the DI composition); none uses `IOptionsMonitor` (the only `IOptionsMonitor<GatewayOptions>` users are background services and `SecurityContextResolutionMiddleware`, none of them a path switch); (c) WebSQL is the one precedent for a runtime check and is **not** a 404 path; this plan follows the OData/Arrow precedent for REST, as the PRD says (D-115-4).

### 1.2 PRD claims confirmed

| PRD item | Result |
|---|---|
| REST data endpoint mapped unconditionally | **Confirmed.** `DatasetDataEndpoints.cs:31-52` maps `GET /api/v1/data/{domain}/{schema}/{table}` (`:34`), `/{id}` (`:40`), `/api/v1/data/{datasetId}` (`:46`); `MapDatasetDataEndpoints()` is called at `GatewayApplicationBuilderExtensions.cs:234`, `MapChangeDetectionEndpoints()` at `:236`, both without a condition. No other file maps a route under `/api/v1/data` (grep over `src`). |
| Change-token route is a read path in the same family | **Confirmed**: `ChangeDetectionEndpoints.cs:33` (`GET .../change-token`). |
| MCP tools registered unconditionally in the registry constructor | **Confirmed.** `McpToolRegistry.cs:23-27` (ctor), default tools `:45-155`, `RegisterTool` `:29-33`, `FindTool` `:40-44`, `GetAvailableTools` `:35-38`. |
| `tools/list` lists everything the registry returns except admin tools for non-admins | **Confirmed**, two list sites: SDK path `GatewayMcpServer.cs:61-80`, JSON-RPC path `McpProtocolHandler.cs:156-170` (`GetAvailableTools()` at `:164`, then optional pruning). |
| T-115-1: `tools/call` resolves tools only through the registry | **Confirmed.** Both call sites (`GatewayMcpServer.CallGuardedToolAsync` `:292-297`, `McpProtocolHandler.HandleToolsCallAsync` `:241`) go to `AiDataGuardrailService.ExecuteToolWithGuardrailAsync`, whose first lookup is `_toolRegistry.FindTool` (`AiDataGuardrailService.cs:116`); a miss returns `Tool '<name>' is not registered or allowed.` after a `DENY` audit entry (`:117-123`) before any argument or admin handling. Resource reads of `autheris://datasets/{dataset}` route through the same guarded call (`GatewayMcpServer.cs:147-153`). `McpToolExecutionHandler` is reached only after the registry hit. |
| `invoke_api` composes the REST data path in-process | **Confirmed and sharper than the PRD.** `ApiDispatcherService.InvokeApiAsync` does **not** issue an HTTP call: it calls `IGovernedDataQueryService.ExecuteQueryAsync` directly for any endpoint starting with `/api/v1/data` (`ApiDispatcherService.cs:78-101`). Unmapping the REST route therefore does **not** switch off `invoke_api` access; the dispatcher itself needs the switch (SI-115-2). The row-target resolution for the guardrail is in `McpDataTargetResolver.cs:18,65-85`. |
| `Mcp.AllowedOperations` is not a duplicate | **Confirmed.** It only registers extra GraphQL-operation tools (`McpToolRegistry.cs:157-174`); it is not a deny list and is untouched. |

### 1.3 PRD gaps found (closed by this plan)

| # | Gap | Evidence | Resolution |
|---|---|---|---|
| G-1 | **`POST /api/v1/data/{domain}/{schema}/{table}/changes` is a second read route** (governed rows via `changesSince`) in the same class; the PRD names only the GET change-token route. | `ChangeDetectionEndpoints.cs:36-39`, `HandlePostChangesSinceAsync` `:111+` | One switch covers both routes (they share `MapChangeDetectionEndpoints`, `ChangeDetectionEndpoints.cs:31-42`). AC and tests cover GET and POST. |
| G-2 | **The PRD tool inventory (7 names) is incomplete.** The registry holds more row- and metadata-reading tools: `query_graphql`, `query_sql`, `sample_rows`, `list_datasources`, `get_data_lineage`, `describe_api` (`McpDatasetTools.cs:14-27`), `query_metrics`, `list_metrics`, `describe_metric` (`McpSemanticTools.cs:18-20`), the demo/legacy tools `query_customers`, `query_invoices`, `query_data_catalog`, `simulate_query`, `get_golden_queries` (`McpToolRegistry.cs:52,69,87,103,119`), doc tools (`McpDocTools.cs:13-18`), admin tools (`McpAdminTools.cs:20-24`) and **dynamic tools** registered at startup from GraphQL fields (`McpSchemaDiscoveryService.cs:86`) or from `Mcp:AllowedOperations` (`McpToolRegistry.cs:157-174`). A `Query` group that only contains `query_dataset` would leave `query_sql`, `query_graphql`, `sample_rows` and every dynamic tool callable: Veto 1 of the PRD in a different form. | see above | Group membership is a closed table (section 2, A-2) that assigns **every** built-in tool; dynamic tools belong to `Query`. |
| G-3 | **Composition of `invoke_api` is wider than the REST data route.** One tool reaches data rows (`/api/v1/data*`, `/api/v1/statement`), catalog metadata (`/api/v1/catalog/*`) and identity/access (`/api/v1/governance/me/access`) (`ApiDispatcherService.cs:78,103,115,135,149,161,185`). Switching `Query`, `Catalog` or `Access` off while `Api` stays on would let an agent reach the same data through `invoke_api`. | see above | Dispatcher checks the group of each branch (A-5). |
| G-4 | **MCP resources and the documentation resources bypass tool switches.** `autheris://catalog/summary`, `.../datasources`, `.../datasets/{id}/schema`, `autheris://governance/my-access`, `autheris://api/openapi.json`, `.../docs/endpoints`, `.../docs/mcp-tools` are rendered by `McpResourceProvider` (`McpResourceProvider.cs:44-125`) and listed by `GatewayMcpServer.ListResourcesAsync` (`:90-115`) without any tool check; semantic resources come from `SemanticMcpCompiler.GetSemanticResourcesAsync` (`SemanticMcpCompiler.cs:104`) used by both server and JSON-RPC handler (`McpProtocolHandler.cs:404-475`). The documentation resources also describe `/api/v1/data` (`ApiDispatcherService.cs:219-250`, static OpenAPI `:404+`) and list **all** dataset tools regardless of registry (`:252-264` iterates `McpDatasetTools.Definitions()`). | see above | Resources, docs and prompts are gated by group (A-6). |

Smaller facts used below: `ApiDispatcherService.BuildEndpointDescription` returns a generic description for unknown endpoints (`:268` onwards), so `describe_api` already has a no-oracle fallback; the dynamic OpenAPI generator (`DynamicOpenApiGenerator.cs`) lists only OData, SQL endpoints, procedures and WebSQL, **not** `/api/v1/data` (grep), so "absent from OpenAPI" needs no change there; there is no `ProtocolInventory` type in the code (grep over `src`), so FR-115-1's "if present" clause is moot. `AccessAuditMiddleware` deliberately does not persist router-produced 404s for unrouted paths (`AccessAuditMiddleware.cs:107-117`, counter `autheris_unrouted_requests_total`).

### 1.4 Verified: where `RestApiOptions` lives

The tracker (I-4) and PRD 6.2 speak of `RestApiOptions`. **There is no `RestApiOptions` class.** `src/Autheris.Domain/Options/Protocols/RestApiOptions.cs` contains `WebSqlOptions`, `CrossSourceOptions`, `SqlEndpointsOptions` and `ProcedureEndpointsOptions` (misnamed file, `:1-167`). `GatewayOptions` has no `RestApi` property (`GatewayOptions.cs:13-60`). This plan adds a new `RestApiOptions` class **in that file** (so the file name becomes true for the first class and the tracker's "shared file" note stays correct for #115 and `PLAN-FED-01` I-9) and a new `GatewayOptions.RestApi` property. The other plans in this wave touch the file as follows: PLAN-SQL-128 adds three options to `WebSqlOptions` (`plan-issue-128` section 3), PLAN-FED-131 and `PLAN-FED-01` I-9 touch `CrossSourceOptions`; none touches the end of the file or the new class, so the merge is mechanical when #115 lands first (I-4 precedes I-5 and I-9).

---

## 2. Architecture Decisions

### A-1 Option layout (names final; PRD names were indicative)

```
Gateway:RestApi:DataEndpoint:Enabled     bool, default true      (new RestApiOptions / RestDataEndpointOptions)
Gateway:Mcp:Tools:Catalog                bool, default true      (new McpToolGroupOptions on McpOptions)
Gateway:Mcp:Tools:Query                  bool, default true
Gateway:Mcp:Tools:Api                    bool, default true
Gateway:Mcp:Tools:Access                 bool, default true
Gateway:Mcp:DisabledTools                string[], default empty (null and [] both mean "nothing disabled")
```

`Mcp:DisabledTools` is `List<string>?` on purpose: a JSON `[]` binds to `null` (documented in `GatewayEmptyListConfigurationValidator.cs:9-12`); here null and empty are equivalent (nothing disabled, equal to today), so **no** rule is added to `GatewayEmptyListConfigurationValidator`. Disabled wins over enabled: a tool is off when its group is off **or** its name is listed. A group switch never turns on a tool that `DisabledTools` names. `Mcp:Enabled=false` stays the master switch; the new options are inert then (FR-115-4).

### A-2 Single source of truth: `McpToolGroups` and `IReadPathSwitches`

New static table `McpToolGroups` (Application, next to the tool name constants) maps each **built-in** tool name to a group, case-insensitive like the registry (`McpToolRegistry.cs:20`):

| Group | Built-in tools |
|---|---|
| `Catalog` | `list_datasets`, `describe_dataset`, `search_catalog`, `list_datasources`, `get_data_lineage`, `query_data_catalog`, `list_metrics`, `describe_metric`, `get_golden_queries` |
| `Query` | `query_dataset`, `query_sql`, `query_graphql`, `sample_rows`, `query_metrics`, `simulate_query`, `query_customers`, `query_invoices`, and **every dynamic tool** (GraphQL-discovered or from `Mcp:AllowedOperations`) |
| `Api` | `invoke_api`, `describe_api` (Q-115-3 default) |
| `Access` | `get_my_permissions`, `describe_my_access` |
| none (only `DisabledTools`) | the five `admin_*` tools (they are already limited to the canonical cluster admin, `McpAdminTools.cs:34-47`), the six doc tools (`McpDocTools.cs:13-18`) |

Interface `IReadPathSwitches` (Application, singleton, built from `IOptions<GatewayOptions>` once, immutable snapshot): `bool RestDataEndpointEnabled`; `bool IsGroupEnabled(McpToolGroup)`; `bool IsToolEnabled(string name)` (false when group off or listed; unknown/dynamic names resolve to group `Query`); `string? DisabledReason(string name)` for audit text (internal only). A unit test pins the table against the live registry: **every name the registry registers by default has a mapping entry or is explicitly listed as ungrouped** (the test enumerates `McpToolRegistry` defaults; a new tool without a decision fails CI). This is the guard against the G-2 class of error recurring.

### A-3 Enforcement point for tools: the registry (D-115-3 confirmed, with one addition)

`McpToolRegistry` takes `IReadPathSwitches?` (optional last constructor parameter, like `IDemoDataSwitch`; null = all enabled, so the 48 direct `new McpToolRegistry(...)` test sites stay valid). `RegisterTool` drops a tool for which `IsToolEnabled` is false (no throw, one Debug log without payload); because `RegisterTool` is also the entry for dynamic tools (`McpSchemaDiscoveryService.cs:86`, `AllowedOperations`), the filter covers them too. Result: a disabled tool is not in `GetAvailableTools()` (both `tools/list` sites, including the pruner input), not found by `FindTool` (so `tools/call` takes the existing unknown-tool branch, `AiDataGuardrailService.cs:116-123`, identical text and result shape as a never-registered name: no oracle), and not executable through `McpToolExecutionHandler`. No second check in handlers.

Addition (audit only, never visible to the caller): in the unknown-tool branch `AiDataGuardrailService` asks `IReadPathSwitches.DisabledReason(name)`; when non-null it writes the **same** `MCP_TOOL_EXECUTION`/`DENY` audit entry with an internal `details` text `Tool disabled by configuration (<group|DisabledTools>)` and increments the disabled-path counter; the returned `McpToolCallResult` is byte-identical to today's unknown-tool result. The optional constructor parameter is appended at the end of `AiDataGuardrailService` (`:55-73`).

### A-4 Enforcement point for REST: not mapped (D-115-4 confirmed, G-1 added)

In `MapGatewayEndpoints`, `MapDatasetDataEndpoints()` (`:234`) and `MapChangeDetectionEndpoints()` (`:236`) each run only when `gatewayOptions.RestApi.DataEndpoint.Enabled`. The two calls keep their positions (the `MapAnalyticsEndpoints()` call between them stays unconditional). No new endpoint filter, no handler-level check: the route does not exist, the router answers 404 with no body, exactly like OData off. System virtual tables (`governance.system.*`) are rendered by `GovernedDataQueryService` (`VirtualSystemTables.Get`, `GovernedDataQueryService.cs:84`), which other channels also call; **only the REST routes disappear with this switch** (Q-115-1 default). The PRD sentence "system virtual tables are switched off with it" is therefore made precise: they stay readable through WebSQL and `query_dataset` and are governed there by those channels' own switches. This is stated in the feature documentation (Phase 6).

### A-5 Composition: `invoke_api` and `describe_api` honour the same switches (SI-115-2, G-3)

`ApiDispatcherService` gets `IReadPathSwitches?` (optional trailing parameter, constructor `:40-50`). A private `AllowedBranch(branch)` decides before any service call:

| Branch (`ApiDispatcherService.cs`) | Required |
|---|---|
| 1 `/api/v1/data*` (`:78`) | `RestDataEndpointEnabled` **and** group `Query` |
| 2-5 `/api/v1/catalog/*` (`:103-160`) | group `Catalog` |
| 6 `POST /api/v1/statement` (`:161`) | group `Query` (the WebSQL data-source check stays as is) |
| 7 `/api/v1/governance/me/access` (`:185`) | group `Access` |

A refused branch returns the **existing** unknown-endpoint result (`SerializeError("EndpointNotFound", 404, ...)`, the text of `:198`), not a new code, so a disabled endpoint is indistinguishable from an unknown one. `DescribeApiAsync` (`:52-60`) applies the same table: for a disabled endpoint it returns the generic description branch (`BuildEndpointDescription` fallback, `:268` onwards), so `describe_api` neither documents nor confirms a disabled route (Q-115-3: the tool itself follows the `Api` group via the registry). The documentation methods use the table too: `GetEndpointsDocumentationAsync` (`:219-250`) and `BuildStaticOpenApiJson` (`:404+`) omit the sections of disabled branches; `GetMcpToolsDocumentationAsync` (`:252-264`) lists only tools that pass `IsToolEnabled` (today it lists `McpDatasetTools.Definitions()` irrespective of the registry).

Interaction rule stated once for the developer: **`Api` on and `Query` off means `invoke_api` can still reach catalog and access endpoints if those groups are on, and nothing that returns rows.** `Api` off removes both tools; the groups never widen each other.

### A-6 Resources and prompts follow the group of the tool that renders the same data (G-4)

| Resource / prompt | Group |
|---|---|
| `autheris://catalog/summary`, `.../datasources`, `.../datasets/{id}/schema`, semantic resources, resource template `autheris://datasets/{dataset}` | `Catalog` |
| `autheris://governance/my-access` | `Access` |
| `autheris://api/openapi.json`, `.../docs/endpoints`, `.../docs/mcp-tools` | `Api` |
| prompt `explore_dataset` | `Catalog` and `Query` |
| prompt `audit_access_compliance` | `Access` and `Catalog` |

`McpResourceProvider.ReadAsync` (`:44`) throws the existing `McpResourceNotFoundException` for a disabled resource (so the wire answer is the same "Resource not found" as for an unknown URI, `GatewayMcpServer.cs:188-198`); a new method `IMcpResourceProvider.IsResourceEnabled(uri)` is used by `GatewayMcpServer.ListResourcesAsync` (`:90-115`) and `ListResourceTemplatesAsync` (`:113-133`) to omit entries, and by `ListPromptsAsync`/`GetPromptAsync` (`:174-186`, `GetAvailablePrompts` `:201`) for prompts (unlisted, and `GetPrompt` answers `Prompt not found` as today for unknown prompts). `SemanticMcpCompiler.GetSemanticResourcesAsync` returns an empty list when `Catalog` is off (this covers the JSON-RPC handler `McpProtocolHandler.cs:404-475` and the SDK handler with one change). Prompts carry no data; gating them is hygiene (no dangling instructions to uncallable tools) and is explicitly **not** a security control; the server `instructions` string (`McpDatasetTools.ServerInstructions`) stays static (known residual, section 8 R-4).

### A-7 Hot reload vs startup-only: startup-only (decided)

Rationale: (1) every existing path switch is startup-only (section 1.1); (2) the registry is a singleton filled in its constructor and the SDK handlers/endpoint mapping are fixed at host build; making only the new switches reloadable would give a mixed model where `tools/list` (reloadable) and the mapped routes (not) disagree; (3) SI-115-4: no request-time or file-watch influence on a security decision. `IReadPathSwitches` snapshots `IOptions<GatewayOptions>.Value` once. The configuration guide says "restart required".

### A-8 Startup validation: unknown names fail the start (FR-115-3, confirmed)

New `ReadPathSwitchOptionsValidator : IValidateOptions<GatewayOptions>` in `src/Autheris.Api/Configuration/Validation/` (pattern of `CrossSourceOptionsValidator.cs:20-52`; registered next to it at `GatewayServiceCollectionExtensions.cs:149`, covered by the existing `ValidateOnStart` at `:139-141`). Rules (all failures, no warnings):
1. Each `Mcp:DisabledTools` entry, trimmed, must be non-empty and equal (case-insensitive) to a **known built-in tool name** (all names of the A-2 table including ungrouped) **or** to a name that `Mcp:AllowedOperations` produces (same normalisation as `McpToolRegistry.cs:161`: lower-case, spaces to `_`). Anything else: `Gateway:Mcp:DisabledTools contains the unknown tool name '<name>'. Valid built-in names: ...` (name echoed, list of valid names, no other data). Names of GraphQL-discovered tools cannot be known at validation time (they are created by `McpSchemaDiscoveryService` after start); such a tool is disabled through the `Query` group or by removing its `@mcpTool` directive, and the documentation says so. This is a documented limit, not a silent hole: a typo still fails the start.
2. A switch value present in raw configuration but not a boolean (`Gateway:RestApi:DataEndpoint:Enabled`, `Gateway:Mcp:Tools:*`) fails the start, checked against `IConfiguration` (as the cross-source validator reads raw keys). PRD SI-115-3 ("malformed yields the documented default") is **tightened** to "malformed aborts the start": a malformed security switch silently falling back to `true` would leave a path open the operator tried to close. (The default binder already throws for an unparsable bool; the explicit check makes the message name the key and is the regression test.)
3. Duplicates are allowed (idempotent).
No rule depends on `Mcp:Enabled`; a typo is an error even while MCP is off (so enabling MCP later cannot activate a misspelt switch).

### A-9 Telemetry and audit of rejected calls (FR-115-6, PRD 6.7 adjusted)

- **MCP:** rejected disabled-tool calls are audited today through the unknown-tool branch (`MCP_TOOL_EXECUTION`, decision `DENY`, `AiDataGuardrailService.cs:117-123`; audit failure is fail-closed outside the auth bypass, `AiDataGuardrailService.Helpers.cs:54-60`). A-3 only refines the internal details text. No new `AuditEventTypes` entry: the PRD's `ACCESS_PATH_DISABLED` event is **not** added (deviation, section 9 F-2): the existing event already carries principal, tenant and tool, and a second event for the same decision would double rows.
- **REST:** the route does not exist, so no handler runs. `AccessAuditMiddleware` deliberately does not persist router 404s (`:107-117`), so a probe leaves only `autheris_unrouted_requests_total`. To give operators the "leaked token tried the closed path" signal the PRD wants without an audit-chain row per probe, a small `DisabledReadPathObserverMiddleware` is registered **only when the REST switch is off**, after authentication/claims normalisation (after `ClaimsNormalizationMiddleware` at `GatewayApplicationBuilderExtensions.cs:83`, before `UseAuthorization` at `:88`). For a request whose path is `/api/v1/data` or below it increments the new counter `autheris_disabled_path_requests_total{path="rest_data"}` and writes a **rate-limited** warning (one log per principal-hash per minute using `TimeProvider`; fields: method, authenticated flag, no path suffix, no token, no query string), then calls `next` (the router returns the normal 404). It never writes a response and never short-circuits.
- **Counter:** `GatewayDiagnostics.DisabledPathRequestCounter` (`src/Autheris.Domain/Diagnostics/GatewayDiagnostics.cs`, next to `UnroutedRequestCounter` `:31`), name `autheris_disabled_path_requests_total`, tag `path` with the closed set `rest_data | mcp_catalog | mcp_query | mcp_api | mcp_access | mcp_listed` (no tool names, no dataset ids: bounded cardinality). The meter `Autheris.Metrics` is already registered (`GatewayCoreServiceExtensions.cs:132`).
- **Startup log (FR-115-6):** `ReadPathInventory.Describe(GatewayOptions)` (pure, unit-testable) returns one Information line, e.g. `Read paths: graphql=on rest_data=off odata=on arrow=on flight_sql=off duckdb_olap=on websql=off sql_endpoints=on mcp=on(catalog=on,query=off,api=on,access=on,disabled_tools=2)`; logged once from `MapGatewayEndpoints`. No secrets, no tool names.

### A-10 Alternatives rejected

- **Per-request switch check in handlers** (REST filter, tool-call check): second enforcement point, easy to forget on a new route; rejected for the registry and the router as the only points (plus the dispatcher, which is a distinct code path, A-5).
- **Seven independent flags:** PRD already chose groups plus `DisabledTools`; the group table is closed and CI-tested (A-2).
- **Making `IOptionsMonitor` reload the registry:** mixed model, rejected (A-7).
- **Hiding tools only in `tools/list`:** PRD veto 1, structurally impossible here because the tool is never registered.
- **Gating by tool-definition metadata (a `Group` field on `McpToolDefinition`):** changes a public record used by 48 test sites and by dynamic registration; a name-keyed table with a CI completeness test is smaller and equally safe.

---

## 3. Contracts (fixed up front)

```csharp
// src/Autheris.Domain/Options/Protocols/RestApiOptions.cs (new classes at the top of the existing file)
public sealed class RestApiOptions { public RestDataEndpointOptions DataEndpoint { get; init; } = new(); }
public sealed class RestDataEndpointOptions { public bool Enabled { get; init; } = true; }
// GatewayOptions: [Required] public RestApiOptions RestApi { get; init; } = new();

// src/Autheris.Domain/Options/Protocols/McpOptions.cs
public sealed class McpToolGroupOptions
{
    public bool Catalog { get; init; } = true;
    public bool Query { get; init; } = true;
    public bool Api { get; init; } = true;
    public bool Access { get; init; } = true;
}
// McpOptions: public McpToolGroupOptions Tools { get; init; } = new();
//             public List<string>? DisabledTools { get; init; }

// src/Autheris.Application/Mcp/Services/McpToolGroups.cs (new)
public enum McpToolGroup { None = 0, Catalog, Query, Api, Access }
public static class McpToolGroups
{
    public static McpToolGroup Of(string toolName);          // built-in table; any other name -> Query (dynamic tools)
    public static bool IsKnownBuiltIn(string toolName);
    public static IReadOnlyCollection<string> BuiltInNames { get; }
}

// src/Autheris.Application/Mcp/Interfaces/IReadPathSwitches.cs (new) + Services/ReadPathSwitches.cs
public interface IReadPathSwitches
{
    bool RestDataEndpointEnabled { get; }
    bool IsGroupEnabled(McpToolGroup group);
    bool IsToolEnabled(string toolName);
    string? DisabledReason(string toolName);                 // internal audit text only; null when enabled
    bool IsResourceEnabled(string resourceUri);              // A-6 table
    bool IsPromptEnabled(string promptName);
}
// DI: services.AddSingleton<IReadPathSwitches>(sp => ReadPathSwitches.From(sp.GetRequiredService<IOptions<GatewayOptions>>().Value))
//     registered in GatewayMcpServiceExtensions.cs next to IMcpToolRegistry (:35), before the registry resolves.
```

Signature changes (all additive, trailing optional parameters; no existing call site breaks):
`McpToolRegistry(IOptions<GatewayOptions>? options = null, IDemoDataSwitch? demoData = null, IReadPathSwitches? switches = null)`; `AiDataGuardrailService(..., TrinoSqlEngine.ISqlEngine? sqlEngine = null, IReadPathSwitches? switches = null)`; `ApiDispatcherService(..., ILogger<ApiDispatcherService>? logger = null, IReadPathSwitches? switches = null)`; `McpResourceProvider(ICatalogDiscoveryService, IApiDispatcherService, IReadPathSwitches? switches = null)`; `SemanticMcpCompiler` gets the same optional parameter. `IMcpResourceProvider` gains `bool IsResourceEnabled(string uri)` (one implementation, one fake in tests).

Wire behaviour contract (frozen by tests): for a disabled tool, group, resource, prompt or endpoint the caller-visible response is **byte-identical** to the response for a name/URI/endpoint that never existed, in both transports (SDK handlers and `McpProtocolHandler`).

---

## 4. Files per Layer and Work Package

Single sequential developer; ownership is exclusive per WP and WPs are done in order.

### WP-1 - Options, group table, switches, validator (red first)
- Domain: `Options/Protocols/RestApiOptions.cs` (new classes `RestApiOptions`, `RestDataEndpointOptions`), `Options/Protocols/McpOptions.cs` (`McpToolGroupOptions`, `Tools`, `DisabledTools`), `Options/GatewayOptions.cs` (`RestApi` property), `Diagnostics/GatewayDiagnostics.cs` (counter).
- Application: `Mcp/Services/McpToolGroups.cs` (new), `Mcp/Interfaces/IReadPathSwitches.cs` (new), `Mcp/Services/ReadPathSwitches.cs` (new), `Mcp/Services/ReadPathInventory.cs` (new, pure startup line).
- Api: `Configuration/Validation/ReadPathSwitchOptionsValidator.cs` (new), `Extensions/GatewayServiceCollectionExtensions.cs` (one registration line near `:149`), `Extensions/DependencyInjection/GatewayMcpServiceExtensions.cs` (`IReadPathSwitches` registration before `:35`).
- Tests: `Tests.Unit/Mcp/ReadPathSwitchesTests.cs`, `McpToolGroupsCompletenessTests.cs`, `Tests.Unit/Configuration/ReadPathSwitchOptionsValidatorTests.cs`, `ReadPathInventoryTests.cs`.

### WP-2 - MCP registry, guardrail audit, resources, prompts
- Application: `Mcp/Services/McpToolRegistry.cs` (ctor parameter, filter in `RegisterTool`), `Mcp/Services/AiDataGuardrailService.cs` (+`.Helpers.cs` only if the audit text helper lives there; constructor tail parameter, disabled-reason in the unknown-tool branch `:116-123`), `Mcp/Services/McpResourceProvider.cs` and `Mcp/Interfaces/IMcpResourceProvider.cs` (`IsResourceEnabled`, gate in `ReadAsync`), `Mcp/Services/SemanticMcpCompiler.cs` (empty list when `Catalog` off).
- Api: `Mcp/GatewayMcpServer.cs` (filter `ListResourcesAsync`, `ListResourceTemplatesAsync`, `ListPromptsAsync`, `GetPromptAsync`; `ListToolsAsync` needs no change because the registry no longer holds disabled tools).
- Tests: `Tests.Unit/Mcp/McpToolSwitchTests.cs`, `McpResourceSwitchTests.cs`, extension of `McpServerTests.cs` (golden `tools/list` with defaults).

### WP-3 - Dispatcher composition (SI-115-2)
- Application: `Mcp/Services/ApiDispatcherService.cs` (branch table A-5 in `InvokeApiAsync`, `DescribeApiAsync`, `GetEndpointsDocumentationAsync`, `BuildStaticOpenApiJson`, `GetMcpToolsDocumentationAsync`).
- Tests: `Tests.Unit/Mcp/ApiDispatcherSwitchTests.cs`.

### WP-4 - REST mapping, observer, startup log
- Api: `Extensions/GatewayApplicationBuilderExtensions.cs` (two conditions at `:234` and `:236`, observer registration, startup log call), `Middleware/DisabledReadPathObserverMiddleware.cs` (new).
- Tests: `Tests.Unit/Api/RestDataEndpointSwitchTests.cs`, `DisabledReadPathObserverMiddlewareTests.cs`; integration check in `Tests.Integration` (WebApplicationFactory, existing fixtures).

### Not changed (stated so the reviewer can check)
`AstSecurityVisitor.cs`, SQL compiler and governance pipeline, GraphQL schema, OData/Arrow/WebSQL switches, `AuditEventTypes.cs`, persistence/DDL, `McpDatasetTools.cs` definitions (names and schemas unchanged), `McpToolExecutionHandler.cs`.

Shared-file notes for neighbouring plans: `GatewayApplicationBuilderExtensions.cs` is also edited by PLAN-SEC-OLAP-01/FED-01 INC-2c only around the `DuckDbOlap` block (`:224-227`), a different hunk; `GatewayServiceCollectionExtensions.cs:143-155` receives one added line, same pattern the cross-source validator used. `McpToolRegistry.cs` and `IReadPathSwitches` are the registry and switch surface that #111 (catalog tags: `Catalog` group tools), #113 (new access-request tools: assigned to the `Access` group in `McpToolGroups` by that plan) and #114 reuse; the completeness test forces each of them to classify its new tools.

---

## 5. Sequencing

Tracker step I-4, no dependency on other items. Order inside the PR: WP-1 (contracts, options, tests red then green) -> WP-2 -> WP-3 -> WP-4. Each WP: write the failing tests of section 6 first, minimal code, refactor, full `Autheris.Tests.Unit` green, Release build with warnings as errors before push (lead builds once at the end per project rule; the developer implements only). One PR.

---

## 6. Test Strategy (TDD list, red first; AC mapping)

Defaults must stay **byte-identical** (AC 4): the first red test of each WP is a golden/regression test of today's behaviour and must stay green throughout.

**WP-1**
1. `McpToolGroups_Every_default_registry_tool_is_classified` (registry defaults vs table; fails for an unclassified name) - guard for G-2.
2. `McpToolGroups_Mapping_matches_table` (each name -> expected group, case-insensitive; unknown name -> `Query`).
3. `ReadPathSwitches_Defaults_all_enabled`; `Group_off_disables_members`; `DisabledTools_wins_over_enabled_group`; `Group_off_never_enabled_by_list`; `Name_matching_is_case_insensitive_and_trimmed`.
4. Validator: `Unknown_tool_name_fails_start` (message names key and value, lists valid names; AC 5), `Blank_entry_fails`, `AllowedOperations_derived_name_is_accepted` (`"List Orders"` -> `list_orders`), `Valid_builtin_names_pass`, `Non_boolean_switch_in_raw_config_fails_with_key_name`, `Typo_fails_even_when_Mcp_disabled`, `Empty_list_and_null_pass`, `All_defaults_pass`.
5. `Options_binding`: JSON with `Gateway:RestApi:DataEndpoint:Enabled=false`, `Gateway:Mcp:Tools:Query=false`, `DisabledTools=["query_sql"]` binds correctly; `DisabledTools: []` binds (null) without error.
6. `ReadPathInventory_Describe` golden strings for default and hardened profile; contains no tool names or secrets.

**WP-2**
7. Regression: `ToolsList_default_is_byte_identical` for SDK `GatewayMcpServer.ListToolsAsync` and `McpProtocolHandler` JSON-RPC `tools/list` (snapshot of names/descriptions/schemas) (AC 4).
8. Per group (`Catalog`, `Query`, `Api`, `Access`) and for one `DisabledTools` name: tool absent from both `tools/list` paths (AC 2); `tools/call` through both transports returns a result **equal to** the result for a random never-registered name (same `ErrorMessage` modulo the echoed tool name, same `isError`, same JSON-RPC shape) - the no-oracle test.
9. `Disabled_tool_call_is_audited_as_DENY_with_internal_reason` (audit entry `MCP_TOOL_EXECUTION`/`DENY`, details contain the reason, response does not); counter incremented with `path=mcp_<group>`; audit repository throwing -> call aborts fail-closed (existing behaviour preserved).
10. Dynamic tools: discovery registers a GraphQL tool while `Query` is off -> not in registry; `AllowedOperations` tool likewise; both appear again with defaults.
11. Pruner input: `SemanticToolPruner` receives no disabled tool (`McpProtocolHandler` with `prompt` argument).
12. Resources: each A-6 resource listed and readable by default; with its group off it is absent from `resources/list`/templates and `resources/read` returns the same error as an unknown URI; semantic resources empty when `Catalog` off in both handlers; `autheris://datasets/{id}` read fails like unknown when `Catalog` off.
13. Prompts: both prompts listed by default; unlisted and `GetPrompt` = `Prompt not found` per A-6 rules.
14. Admin tools stay visible to the cluster admin by default and `DisabledTools: ["admin_apply_access"]` removes one; non-admin visibility rule unchanged.

**WP-3**
15. Dispatcher defaults: all seven branches behave as today (existing `McpHybridToolsTests` stay green).
16. REST off: `invoke_api` `/api/v1/data/d.s.t` and `/api/v1/data/d/s/t/change-token` return the unknown-endpoint result and **do not call** `IGovernedDataQueryService` (strict mock, AC 3); `Query` off: same, plus `POST /api/v1/statement` refused without calling `IGovernedSqlExecutionService`; `Catalog` off: branches 2-5 refused without calling the catalog service; `Access` off: branch 7 refused.
17. `Api` on, `Query` off: catalog still answers; no row-returning call possible (`Groups_never_widen_each_other`).
18. `describe_api` for a disabled endpoint equals `describe_api` for a nonexistent path (no oracle); `GetEndpointsDocumentationAsync`, `BuildStaticOpenApiJson` and `GetMcpToolsDocumentationAsync` omit disabled sections/tools and are unchanged by default (golden).

**WP-4**
19. REST off: `GET /api/v1/data/d/s/t`, `/{id}`, `/api/v1/data/{datasetId}`, `GET .../change-token` **and `POST .../changes`** return 404 with empty body, indistinguishable from `GET /api/v1/data-nonexistent` style unknown paths (AC 1, G-1); they are absent from the dynamic OpenAPI index and endpoint listing; neighbouring routes (`/api/v1/catalog/*`, `/api/v1/sql`) still resolve.
20. REST on (default): the five routes still map (`EndpointDataSource` assertion) and `MapAnalyticsEndpoints` is unaffected.
21. Observer: counts a request to `/api/v1/data/x` and `/api/v1/data` only when the switch is off; does not count `/api/v1/database`, `/api/v1/catalog`; rate limiter logs once per window (`FakeTimeProvider`, no sleeps); never writes a response; middleware not registered when the switch is on.
22. Startup log line emitted once with the expected content for default and hardened options.
23. Integration (WebApplicationFactory, MCP enabled, `Query=false`, `Api=false`): `tools/list` over HTTP shows the reduced set; `tools/call query_sql` returns the unknown-tool error; leak test: serialized `tools/list`, `resources/list`, docs resources and OpenAPI resource contain none of the disabled tool names or `/api/v1/data` when the corresponding switches are off.

Security tests carry `[Trait("Category", "Security")]`; ids `SEC-115-nn` are assigned in Phase 3.

---

## 7. Fail-Closed Behaviour

| Situation | Behaviour |
|---|---|
| Unknown or blank name in `Mcp:DisabledTools` | Start aborts (A-8). |
| Non-boolean switch value | Start aborts (A-8, tightens SI-115-3). |
| Switch absent | Documented default (`true`): no path is turned off by an upgrade (PRD veto 2). |
| Tool name not in the group table (dynamic) | Treated as `Query`: switching `Query` off removes it; never "ungrouped and always on". |
| `IReadPathSwitches` missing in a hand-built composition (direct `new`) | All enabled, identical to today; production DI always registers it (a DI composition test asserts resolution). |
| Audit write fails on a disabled-tool call | Call aborts as today (fail-closed outside the MCP auth bypass). |
| Dispatcher cannot classify an endpoint | Falls to the existing unknown-endpoint branch (no data call). |
| Group off, caller names the tool in different case or with whitespace | Registry lookup is case-insensitive and `FindTool` rejects blank; a tool disabled in one case is disabled in all. |
| Config changed at runtime | No effect until restart (A-7); documented. |

---

## 8. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R-1 | A tool added later (#111, #113, #114) is not classified and stays reachable when its group is off | CI completeness test (test 1) fails for an unclassified default tool; each follow-up plan names its group. |
| R-2 | Group assignment surprises an operator (`sample_rows`, `query_sql` under `Query`; `get_data_lineage`, `list_datasources` under `Catalog`) | Table in the configuration guide and the feature doc; `DisabledTools` gives per-tool control; owner may reassign (Q-115-4). |
| R-3 | `Api` on while `Query` off leaves `invoke_api` partially useful, which may confuse | Rule stated in A-5 and documented; test 17. |
| R-4 | `ServerInstructions` text and `explore_dataset` mention tool names that are off | Cosmetic; instructions are static and public product text; prompts are gated (A-6). Not a security issue (no data, no oracle beyond public tool names). |
| R-5 | Observer middleware adds a pipeline element | Registered only when the REST switch is off, no allocation on the on path, pure path-prefix check; test 21. |
| R-6 | Hand-built test compositions skip the switches | Optional trailing parameters keep tests valid; one DI-composition test proves production wiring (resolves `IReadPathSwitches` and gives it to registry, guardrail, dispatcher, resource provider). |
| R-7 | Dynamic tool names cannot be validated at start | Documented limit (A-8); `Query` group is the control; typos in built-in or `AllowedOperations` names still fail. |
| R-8 | Merge friction on `RestApiOptions.cs`/`GatewayOptions.cs` with I-5, I-9 | I-4 lands first; the new class sits at the file top, other plans edit later hunks (section 1.4). |

---

## 9. Owner Flags

1. **Q-115-4 (new, default applied): group membership is wider than the PRD list** (G-2): `Query` contains every row-returning tool and every dynamic tool, `Catalog` all metadata tools, `Api` both API tools, `Access` both identity tools; admin and doc tools only via `DisabledTools`. Alternative: seven independent flags or a narrower `Query`; the plan advises against narrowing because it would leave `query_sql`/`sample_rows` callable.
2. **F-2 deviation from PRD 6.7:** no new `ACCESS_PATH_DISABLED` audit event type; MCP uses the existing `MCP_TOOL_EXECUTION`/`DENY` row with an internal reason; REST gets counter plus rate-limited log only (router 404s are deliberately not audited). Override if the owner wants a chain row per REST probe.
3. **F-3 tightening of SI-115-3:** a malformed switch value aborts the start instead of defaulting.
4. **F-4:** system virtual tables stay reachable through WebSQL and `query_dataset` when only the REST switch is off (Q-115-1 default made precise).
5. **F-5:** `invoke_api` honours the groups of what it reaches (A-5); this is stricter than the PRD (which names only the REST data family).

---

## 10. Security Notes for Phase 3

Review focus, in order:
1. **Completeness of "off means off"** (SI-115-1): every route to a tool or REST function: registry (both transports), `McpToolExecutionHandler`, dispatcher branches 1-7, resources (list, templates, read), prompts, docs resources, semantic resources, dynamic tools, `AllowedOperations` tools, router mapping (GET data x3, GET change-token, POST changes). Confirm no other `/api/v1/data` mapping appears via plugins or extensibility (`GatewayExtensibilityMiddleware`, plugin loader at `GatewayApplicationBuilderExtensions.cs:135-138`).
2. **No oracle:** disabled vs unknown for tool call, resource read, prompt get, endpoint via `invoke_api`/`describe_api` (message text, JSON-RPC code, `isError`, timing is not claimed). Audit text may differ, response must not.
3. **No self-widening by the agent:** nothing in the call path lets a tool argument, header, token claim, resource URI or prompt argument select or override a switch; confirm `McpToolDefinition` of dynamic tools cannot carry a group claim (A-2: name-keyed only); confirm group `Query` default for unknown names cannot be bypassed by a name that collides with a built-in under different casing/Unicode normalisation (comparison is `OrdinalIgnoreCase`, as the registry).
4. **Composition ordering:** the dispatcher refuses before any service call and before row-target resolution results are used (the guardrail's ABAC resolution at `McpDataTargetResolver.cs` still runs first and may deny differently; confirm this ordering reveals nothing the caller could not learn for an unknown endpoint).
5. **Fail-closed validation** (A-8): unknown names, raw-config boolean check, behaviour with `Mcp:Enabled=false`.
6. **Observer middleware:** log content (no token, no principal id in clear: hashed), rate-limit state is bounded (fixed-size dictionary with eviction, or a single window counter), no per-request allocation beyond the path check, cannot be used to flood logs.
7. **Hardening profile claim:** the documented hardened profile (`GraphQL-only`: REST off, `Mcp:Enabled=false`, OData/Arrow/OLAP off, WebSQL off) must be reproducible from the startup log line (test 22).
8. **Open point for the reviewer:** whether prompts and the static `ServerInstructions` should be treated as information disclosure (plan says no, R-4).

---

## 11. LOC Estimate

| Part | Production | Tests |
|---|---:|---:|
| WP-1 options, group table, switches, validator, inventory, counter | ~190 | ~330 |
| WP-2 registry, guardrail audit, resources/prompts, semantic gate, server filters | ~110 | ~280 |
| WP-3 dispatcher branch table and docs | ~70 | ~170 |
| WP-4 mapping, observer middleware, startup log | ~70 | ~160 |
| **Total** | **~440** | **~940** |

Size S (tracker I-4); the test share is high because every "off" route needs its own no-oracle assertion. No persistence, no migrations, no new packages.

---

## 12. Changelog

- 2026-10-16: Revision 1 (Phase 2). PRD defaults taken for Q-115-1..3; gaps G-1..G-4 and the missing `RestApiOptions` class recorded; Q-115-4 opened with a default.
- 2026-10-16: Phase 3 security review (`csharp-security-expert`): verdict APPROVED WITH CONDITIONS; findings SEC-115-01..11, binding conditions C-115-1..12, mandatory security tests ST-115-01..24, owner-flag recommendations (see the review section below).
- 2026-10-17: Phase 4 implementation notes (`csharp-tdd-developer`), all within the Phase 3 conditions:
  - Registry: public `RegisterTool` is the dynamic route (group `Query` by origin; a name equal to a built-in is refused even when that built-in's group is off); built-ins use a private `RegisterBuiltIn`. `IReadPathSwitches` gained `RecordDropped` (bounded set of 4096 names, startup-only) to feed `DisabledReason` (C-115-4); registry also takes an optional `ILogger`.
  - There are seven admin tools (not five); all are in the table as ungrouped. Dispatcher gates sit inside each branch with one shared `UnknownEndpoint` producer (C-115-6); `McpDatasetTools.Definitions(switches)` swaps the `describe_api` example path when the data family is closed (ST-115-13). Prose cross-references between tool descriptions (R-4) and `ServerInstructions` are unchanged.
  - Observer (C-115-5): the code has no SID audit-hash helper, so a per-process random-key HMAC (6 bytes hex) identifies the principal; budget 20 log lines/min process-wide, 256-entry per-principal table, extra counter tag `observer_error`; the sample-rows route is part of the observed family. Startup line also reports `lakehouse`; it is unit-tested through `GatewayApplicationBuilderExtensions.LogReadPathInventory` because Serilog replaces test logger providers in the integration host.
  - Verified facts: anonymous callers get the fallback-policy 401 for mapped and unmapped paths alike (SEC-115-08(b) moot); the closed sample-rows route is answered by the `/api/v1/catalog/datasets/{*id}` catch-all exactly like any other unknown sub-path (compared against that, not against a 404); `McpProtocolHandler` serves only tools and semantic resources (no native resources/prompts), the SDK handlers are covered by the `GatewayMcpServer` filter helpers plus a real-SDK integration test; ST-115-20 result: with `WebSql:Enabled=false` an async job ends `Failed` (rewriter gate), no gap to report.
  - Known limit: raw-config key checking covers the closed key sets of the three switch nodes (C-115-2); a misspelt key directly under `Gateway:Mcp` (for example `DisabledTool`) is still ignored by the binder. ST-115-23 is example-based (crafted arguments), not a generated property test.
- 2026-10-17: Phase 5 follow-ups. The validator now also fails the start for a near-miss misspelling (edit distance <= 2 or substring) of `Tools`, `DisabledTools`, `RestApi`, `DataEndpoint` directly under `Gateway` and `Gateway:Mcp` (supersedes the "known limit" above for those names). Accepted limitation: the observer's principal hash is a per-process keyed HMAC, so it cannot be correlated across restarts. `RecordDropped` is a best-effort bound (startup-only registration). The `autheris://datasets/{id}` resource and template also follow a `DisabledTools` entry of `describe_dataset`. ST-115-19 is folded into the test `ST_115_01_and_19_*`; the startup-line unit test is named `C_115_12_*`.

---

## Phase 3 Security Review (2026-10-16)

**Reviewer:** `csharp-security-expert` (Phase 3). **Scope:** this plan against PRD section 6 (decisions D-115-1..4, SI-115-1..4) and the current code in the worktree (`src/...`, paths below are relative to `src/`). **Method:** every route/dispatcher/resource that can return governed rows or catalog/identity metadata was enumerated from the code (`MapGet/MapPost/MapGroup`, registry call sites, dispatcher branches), not from the plan. Owner guiding rule applied as the test for every finding: the backend decides, the calling agent can never widen its own reach.

### 1. Threat model

| ID | Actor / asset | Threat | Plan coverage |
|---|---|---|---|
| T-1 | Agent holding a valid token (MCP or REST) | Calls a path the operator closed (direct, or through a composing path: `invoke_api`, resources, prompts, docs, dynamic tools). | A-3..A-6; gaps SEC-115-01, -03, -07 |
| T-2 | Agent / scanner | Learns from response differences (text, status, JSON-RPC code, `isError`, tool/resource lists) which paths exist but are disabled, then targets them. | A-3, A-5, A-6; residuals SEC-115-04, -06, -08 |
| T-3 | Agent controlling tool arguments, URI, prompt arguments, headers, claims | Selects or overrides a switch (self-widening). | Switches come from `IOptions<GatewayOptions>` only. No request input reaches `IReadPathSwitches`. Confirmed: no finding. |
| T-4 | Operator error (typo, wrong type, wrong shape) | A closing switch is silently ignored and the path stays open (fail-open by misconfiguration). | A-8 covers values and tool names only; gap SEC-115-02 |
| T-5 | Schema author / future developer | Registers a tool whose name maps to a more permissive group, or adds a tool nobody classified. | A-2 + test 1; gaps SEC-115-03, -09 |
| T-6 | Attacker probing a closed path | Floods logs, audit chain or metrics via the new audit/observer additions. | A-9; gaps SEC-115-04, -05 |
| T-7 | Config reload / env override at runtime | A runtime change opens a path or makes layers disagree. | A-7 (startup-only); residual SEC-115-08 |
| T-8 | Reviewer-side: trust of the plan's inventory | An incomplete inventory makes "off" look complete. | SEC-115-01, -07 |

### 2. What was verified and holds (no change needed)

- **Single enforcement point for tools is real.** Only two code paths run a tool: `AiDataGuardrailService.ExecuteToolWithGuardrailAsync` (called from `Mcp/GatewayMcpServer.cs:296` for the SDK transport and `Application/Mcp/Services/McpProtocolHandler.cs:241` for JSON-RPC), and its first step is `_toolRegistry.FindTool` (`AiDataGuardrailService.cs:116`) with the unknown-tool return at `:117-123` before prompt guardrail, admin handling, doc tools (`:172`) or any executor. `IMcpToolExecutionHandler.ExecuteToolAsync` has exactly one caller, `GatewayMcpQueryExecutor.cs:132`, which is reached only from the guardrail with the registry's `McpToolDefinition` (grep over `src`: no other `ExecuteToolAsync` or `IMcpQueryExecutor` call site). There is no separate hybrid gateway tool path; the "hybrid tools" are `McpToolExecutionHandler` (`GatewayMcpQueryExecutor.cs:129`). `FindTool` rejects blank names (`McpToolRegistry.cs:42`) and is `OrdinalIgnoreCase` (`:20`), so case variants of a disabled name resolve to nothing.
- **Both `tools/list` sites** read `GetAvailableTools()` only (`GatewayMcpServer.cs:70`, `McpProtocolHandler.cs:164`), and the pruner receives the registry result (`:182`), so dropping at `RegisterTool` removes tools from listing, pruner input and execution at once.
- **`invoke_api` really is an in-process dispatcher** (`ApiDispatcherService.cs:78-101` calls `_dataQueryService` directly), so unmapping the REST route alone would not close it; the plan's A-5 is necessary and correct. The only callers of `IApiDispatcherService` are `McpToolExecutionHandler` and `McpResourceProvider` (grep), so gating the dispatcher plus the registry covers every in-process composition of the REST data path.
- **Route mapping.** `/api/v1/data` is mapped in exactly two files (`Endpoints/DatasetDataEndpoints.cs:34,40,46`; `Endpoints/ChangeDetectionEndpoints.cs:33,38`); the `GatewayApplicationBuilderExtensions.cs:234,236` calls are unconditional today. Router 404 for unmapped paths is non-persisted by design (`Middleware/AccessAuditMiddleware.cs:107-117`).
- **Switch input is request-independent**: no switch method takes request data. T-3 closed by construction (name-keyed table, no group claim on `McpToolDefinition`).
- **Audit for disabled-tool calls is fail-closed already** (`AiDataGuardrailService.Helpers.cs:54-60`, abort unless `IsMcpAuthBypassed`).

### 3. Findings

Severity scale: High = a closed path stays reachable or a security control can silently fail open; Medium = weakening or oracle/DoS with a bounded impact; Low = hardening/consistency.

#### SEC-115-01 (High): REST row path `GET /api/v1/catalog/datasets/{dataset}/sample-rows` is not covered by any switch
**Evidence:** `Endpoints/QueryAnalysisAndProfilingEndpoints.cs:59` maps it; `HandleSampleRowsAsync` (`:154-171`) calls `IMcpDatasetCatalog.SampleRowsAsync`, the same implementation behind the `sample_rows` tool that the plan puts in group `Query`. It is mapped by `MapQueryAnalysisAndProfilingEndpoints()` (`GatewayApplicationBuilderExtensions.cs:210`) unconditionally and lives under `/api/v1/catalog`, so neither the REST data switch (A-4) nor the observer prefix (`/api/v1/data`) touches it. With `Gateway:RestApi:DataEndpoint:Enabled=false` and `Mcp:Tools:Query=false` the same token still reads governed (or synthetic) rows over REST. This is PRD Veto 1 in a third form.
**Required change:** (a) treat the route as part of the REST read-path family: map it only when `RestApi.DataEndpoint.Enabled` (split the single `app.MapGet` out of `MapQueryAnalysisAndProfilingEndpoints` into a conditionally mapped method or pass a flag; the other analysis/consent routes stay unconditional), extend the observer prefix set accordingly (`rest_data`), and add it to AC 1 and test 19; or (b) if the owner decides the route is deliberately outside the switch, the plan must say so in section 1.1 and the feature docs must state that `Gateway:Mcp:Tools:Query=false` does not close it. Recommendation: (a). The remaining `/api/v1/catalog/*` REST routes (`Endpoints/CatalogApiEndpoints.cs:24-71`) return metadata, not rows; they are out of scope of #115 but must be listed (SEC-115-07).

#### SEC-115-02 (High): misconfiguration of a closing switch can silently fail open (key typos, wrong shape, null)
**Evidence:** A-8 rule 2 checks only that a present value is a boolean. The default configuration binder ignores (a) unknown keys (`Gateway:Mcp:Tools:Qeury=false`, `Gateway:RestApi:DataEndpoint:Enable=false`, `Gateway:Mcp:DisabledTool`), (b) a scalar where a section/array is expected (`"Tools": false`, env `GATEWAY__MCP__DISABLEDTOOLS=query_sql` binding to `List<string>`), (c) an explicit JSON `null`/empty value for a `bool` (keeps the default `true`). In all three the operator believes a path is closed and it is open. This is exactly the class of failure fail-closed validation exists for, and the plan states it only for tool names and boolean parse errors.
**Required change:** the validator must work on the raw `IConfiguration` subtree and fail the start when: (1) `Gateway:RestApi`, `Gateway:RestApi:DataEndpoint`, `Gateway:Mcp:Tools` contain a child key that is not in the closed key set (case-insensitive); (2) these nodes carry a scalar value of their own; (3) `Gateway:Mcp:DisabledTools` has a scalar value or children that are not numeric indexes, or an element that is null/blank; (4) a switch key exists with a null/empty value; (5) a switch value is anything other than a case-insensitive `true`/`false` (reject `yes`, `1`, `off`). Messages name the key path only, never a value except the already-planned unknown tool name echo. These checks run regardless of `Mcp:Enabled` (as A-8 already says for names).

#### SEC-115-03 (Medium): group of a dynamic tool is decided by name, so a dynamic tool can inherit a non-`Query` group; construction order is load-bearing
**Evidence:** A-2: "unknown/dynamic names resolve to group `Query`", but a name that equals a built-in (`McpToolRegistry.cs:41` `_tools[tool.Name] = tool` overwrites silently; GraphQL discovery registers through the same call, `GraphQL/Mcp/McpSchemaDiscoveryService.cs:86`) is classified by the built-in table. A `@mcpTool` named `list_datasources` or `get_my_permissions` that executes an arbitrary GraphQL operation (rows) would then stay available while `Query=false` (group `Catalog`/`Access`). Reaching this needs a schema author, not the agent, so Medium. Separately, `InitializeDefaultTools` runs in the constructor (`:26`) and calls `RegisterTool` (`:29`); a switch field assigned after that call silently disables the filter for all built-ins.
**Required change:** (1) classification must follow registration origin, not only name: any tool registered after the registry's own default initialisation, or carrying `TargetGraphQLOperation` that is not one of the built-in operations, is group `Query` regardless of name; additionally the registry must refuse (log + drop) a dynamic registration that collides with a built-in name instead of overwriting it. (2) `_switches` is assigned before `InitializeDefaultTools`; a unit test constructs the registry with a switch set disabling every group and asserts an empty registry (modulo ungrouped tools), which catches a wrong order. (3) A test registers a dynamic tool named like a `Catalog` built-in under `Query=false, Catalog=true` and asserts it is not callable.

#### SEC-115-04 (Medium): `DisabledReason` as planned mislabels every unknown name and inflates audit and counters
**Evidence:** A-3 asks `DisabledReason(name)` in the unknown-tool branch (`AiDataGuardrailService.cs:116-123`); A-2 resolves any unknown name to `Query`. With `Query=false` every random never-registered name is therefore recorded as "disabled by configuration (Query)" and increments `autheris_disabled_path_requests_total{path=mcp_query}`. That poisons the one signal the PRD wants (an agent probing a closed path) and lets an agent inflate the counter at will. The DENY audit row per call itself is pre-existing behaviour (unbounded per caller today, see SEC-115-05 for the rate stance).
**Required change:** `DisabledReason` returns non-null only for names that are (a) in the built-in table with their group off, (b) listed in `DisabledTools`, or (c) names the registry actually dropped at registration (bounded set, capped, e.g. 4096 entries, kept inside the registry). Unknown names return null: plain DENY text and no disabled-path counter. The `path` tag stays the closed set. Add test ST-115-09.

#### SEC-115-05 (Medium): observer middleware: unbounded state, auth ordering, hash quality, log flooding
**Evidence:** A-9 plans "one log per principal-hash per minute" in a dictionary, registered before `UseAuthorization` (`GatewayApplicationBuilderExtensions.cs:88`), i.e. also for anonymous requests, and lists "bounded cardinality" only for the counter. Risks: (a) per-principal dictionary grows with attacker-chosen distinct identities (rotating valid client certs/JWT subjects, or the "anonymous" bucket logic if keyed on claims the attacker influences); (b) an unsalted hash of a SID is a re-identification oracle in logs; (c) before authorization an anonymous scanner generates a counter increment and a log candidate per request; (d) a second counter next to the existing `autheris_unrouted_requests_total` is redundant for anonymous traffic.
**Required change:** (1) state is a fixed-size structure (single global token bucket, e.g. at most N log lines per minute process-wide, plus an optional small fixed-capacity per-principal LRU of 256 entries; no unbounded dictionary); the "dropped log lines" count is exposed through the counter only. (2) The principal is identified with the project's existing audit hashing of the SID or a keyed hash, never a bare SHA of the raw SID and never the raw SID/token/query string/path suffix. (3) Anonymous requests (no authenticated identity) only increment the counter and never log; log lines are for authenticated principals only (the leaked-token signal). (4) Registered only when the switch is off (already planned); never short-circuits, never writes a response, never throws (wrap in try/catch, fail-open for the request but count the failure). (5) Path match is segment-aware (`/api/v1/data` or `/api/v1/data/...`, so not `/api/v1/database`, as test 21 already says) and uses `PathString.StartsWithSegments` with ordinal-ignore-case, applied on the normalised path.

#### SEC-115-06 (Medium): dispatcher and resource gating must be inline and short-circuit; documentation and `describe_api` oracles need explicit golden tests
**Evidence:** A-5 describes an `AllowedBranch(branch)` table. In `ApiDispatcherService.InvokeApiAsync` the branches are chained `if` blocks keyed on `StartsWith`/`Equals` over the lowercased trimmed endpoint (`:78,103,115,135,149,161,185`); branch 1 matches the bare prefix `/api/v1/data` (so also `/api/v1/database...`), branches 4/5 match prefixes, and branches 1-5 ignore the HTTP method. A separate pre-classifier that re-parses the endpoint can drift from these predicates and let an endpoint reach a service call that the classifier mapped to another group. A-6 states `ReadAsync` throws not-found; the SDK read path checks `IsNativeResource` first (`GatewayMcpServer.cs:141-146`) and only then `ReadAsync`, so listing, template listing and read must be consistent; the `autheris://datasets/{dataset}` read (`:147-153`) maps any tool error to not-found, which is correct but also hides a DENY from the agent (fine) and writes the audit row (needed).
**Required change:** (1) put the gate as the first statement inside each existing branch (`if (predicate) { if (!allowed) return UnknownEndpointResult(...); ...}`), using the identical predicate; a refused branch returns immediately and never falls through to a later branch; the unknown-endpoint text is produced by one shared helper so refused and unknown cannot diverge. (2) `describe_api`, `GetEndpointsDocumentationAsync`, `BuildStaticOpenApiJson`, `GetMcpToolsDocumentationAsync` get a golden test that the output for a disabled endpoint equals the output for a never-existing path byte for byte, and the documentation outputs contain none of the disabled paths/tool names (substring test over the serialized string). (3) `ReadAsync` checks `IsResourceEnabled(uri)` as its first statement before any service call (no catalog/discovery call for a closed resource); `IsResourceEnabled` for an unknown URI returns true (so the unknown branch stays the single not-found producer) but the not-found text and error code must be identical for disabled and unknown URIs in both the SDK path and the JSON-RPC path (`McpProtocolHandler.cs:462-465` uses `-32004` with the same text format; keep it). (4) Verify `BuildPrompt` returns the same result for a gated prompt name and an unknown name (the plan states "Prompt not found as today"; this review did not re-verify it, the Phase 4 developer proves it with ST-115-14 before relying on the claim).

#### SEC-115-07 (Medium): read-path inventory in the plan is incomplete as a statement of scope (REST catalog family, flight/jobs/CDC, doc tools, GraphQL)
**Evidence (enumerated from the code):**

| Path | Rows / metadata | Covered by | Status in plan |
|---|---|---|---|
| `GET /api/v1/data/*`, `GET .../change-token`, `POST .../changes` | rows | REST data switch | covered (A-4, G-1) |
| `GET /api/v1/catalog/datasets/{dataset}/sample-rows` | rows | nothing | missing (SEC-115-01) |
| `/api/v1/catalog/datasets`, `/datasets/{*id}`, `/datasources` (`CatalogApiEndpoints.cs:24-66`), `/api/v1/governance/me/access` | metadata/identity | nothing (only the MCP groups mirror them) | not listed; scope of `Catalog`/`Access` groups is "MCP tools and in-process composition", not "access path" |
| `POST /api/v1/jobs/query`, `GET /api/v1/jobs/{id}/result` (`AsyncJobEndpoints.cs:23-116`) | rows (SQL) | executes via `IGovernedSqlExecutionService` (`Application/Jobs/Services/AsyncQueryJobBackgroundWorker.cs:237`); WebSql gate not verified by this review | not listed |
| `/api/v1/flight/sql/*` | rows/metadata | `Arrow:EnableFlightSql` | listed |
| `/api/v1/cdc/events`, `/subscriptions` (`StreamingCdcEndpoints.cs:35,104,107`), webhooks (`WebhookEndpoints.cs`) | ingestion/config (write paths); subscription listing is metadata | own authorization | not listed; out of scope but must be stated |
| GraphQL (`GatewayApplicationBuilderExtensions.cs:143`) | rows | no switch by design (baseline of the hardened profile) | listed |
| doc tools (`search_docs`, `lookup_error_code`, `get_procedure`, `list_doc_topics`, `list_applications`, `deposit_documentation`, `McpDocTools.cs:13-18`) | documentation content; `deposit_documentation` is a **write** tool | `DisabledTools` only | classified "none"; see Q-115-5 |
| MCP resources/prompts/semantic resources | metadata | A-6 | covered |

**Required change:** section 1.1/2 of the plan gains this inventory table so the scope is explicit: the new switches close the REST data family (including sample-rows) and MCP tools/resources/prompts/in-process composition; they do not close REST catalog/identity routes, async jobs, CDC or webhooks, and the feature documentation says "Catalog/Access group = MCP surface". For async jobs the developer verifies in a test (ST-115-20) that `POST /api/v1/jobs/query` with `WebSql:Enabled=false` cannot read a client-selected source; if it can, that is a pre-existing WebSql-switch gap and is reported to the owner as a new issue, not fixed inside #115.

#### SEC-115-08 (Low): startup snapshot consistency; anonymous existence oracle for the unmapped REST routes
**Evidence:** (a) `MapGatewayEndpoints` reads `gatewayOptions` for mapping while `IReadPathSwitches` is a lazily created singleton over `IOptions<GatewayOptions>.Value`; both are startup values, but if resolved at different times with a reload-capable configuration source (`reloadOnChange`) the two could differ for a single process. Other code uses `IOptionsMonitor<GatewayOptions>` (e.g. `SecurityContextResolutionMiddleware`), which the new class must not use. (b) With the route mapped, `.RequireAuthorization()` answers anonymous callers with 401; unmapped, the router answers 404 (the fallback policy `Security/GatewayPolicies.cs:92` needs an endpoint). An anonymous scanner can therefore tell "REST data off" from "REST data on". Same as the OData/Arrow precedent, so accepted, but it must be documented. (c) The plan's "404 no body" is an unmapped-path behaviour; verify that no exception handler/`UseStatusCodePages` adds a body.
**Required change:** `IReadPathSwitches` is created and resolved eagerly during startup validation (a `ValidateOnStart`-time touch or registration through a factory called in `MapGatewayEndpoints`), and `MapGatewayEndpoints` takes its REST decision from that same instance, not from a second read; a test asserts both agree. The feature documentation states "restart required" and the anonymous 401/404 difference. Test: response of a closed REST route equals response of `/api/v1/data-nonexistent-xyz` for authenticated and anonymous callers (status, headers that are not tracing ids, empty body).

#### SEC-115-09 (Low): the CI completeness test must be two-sided and cover every registration source
**Evidence:** test 1 enumerates registry defaults. Registry defaults depend on `IDemoDataSwitch` (`McpToolRegistry.cs:23-26,45-155`), on `AllowedOperations` (`:157-174`), and the semantic/doc/admin tools come from separate constant classes (`McpSemanticTools.cs:18-20`, `McpDocTools.cs:13-18`, `McpAdminTools.cs:20-24`).
**Required change:** the test (a) builds the registry with demo data enabled and with a non-empty `AllowedOperations` and asserts every registered name is either in the built-in table or flagged dynamic; (b) reflects over the public `const string` tool names of `McpDatasetTools`, `McpSemanticTools`, `McpDocTools`, `McpAdminTools` and asserts each is in the table or in an explicit ungrouped list; (c) the reverse direction: every table entry exists in the registry (no stale names that would be accepted by `DisabledTools` validation but never match); (d) a mutation-style test registers a fake default tool name and asserts the test helper reports it unclassified (proves the guard can fail). Unknown names stay group `Query` at runtime (fail-safe), but CI must fail first.

#### SEC-115-10 (Low): audit and prompt/instructions stance, accepted with conditions
Reuse of `MCP_TOOL_EXECUTION`/`DENY` instead of a new `ACCESS_PATH_DISABLED` event (F-2) is accepted for MCP: the row already carries principal, tenant and tool, and the rate of these rows is as unbounded as every other DENY today. For REST, counter plus rate-limited warning without a chain row is accepted because router 404s are deliberately not persisted (`AccessAuditMiddleware.cs:107-117`); the condition is SEC-115-05. R-4 (prompts and `ServerInstructions` are static public text) is accepted: no data and no state-dependent response, so no oracle; the gating of prompts is hygiene and the plan must keep calling it that.

#### SEC-115-11 (Info): items checked and found sound
`Mcp:AllowedOperations` is not a deny list and stays untouched (`McpToolRegistry.cs:157-174`); `DisabledTools` null/empty equivalence matches `GatewayEmptyListConfigurationValidator`; defaults reproduce today's behaviour (PRD Veto 2); disabled wins over enabled; case-insensitive and trimmed matching is the same `OrdinalIgnoreCase` the registry uses (Unicode look-alikes simply do not match a registry key, so they yield the unknown-tool branch, never a bypass); the dispatcher's `ToLowerInvariant` normalisation means case variants of `/api/v1/DATA` are caught by the same predicate.

### 4. Binding conditions (Phase 4 must satisfy all; deviations need a new review)

- **C-115-1** (SEC-115-01) Gate `GET /api/v1/catalog/datasets/{dataset}/sample-rows` with the REST data switch (or record an explicit owner decision to the contrary in section 1.1 and the docs).
- **C-115-2** (SEC-115-02) Raw-configuration validation of key set, node shape, null/empty values and strict `true`/`false` for all new keys; `DisabledTools` shape and blank/null elements; all regardless of `Mcp:Enabled`.
- **C-115-3** (SEC-115-03) Classification by registration origin: tools registered after registry initialisation or with a non-built-in GraphQL operation are `Query`; colliding dynamic registrations are refused, not overwritten; `_switches` assigned before default tool initialisation.
- **C-115-4** (SEC-115-04) `DisabledReason` non-null only for table members with group off, `DisabledTools` names and registry-dropped names (bounded set); unknown names get the plain DENY text and no disabled-path counter.
- **C-115-5** (SEC-115-05) Observer state is fixed-size; anonymous requests are counted only; principal identification uses the project's audit hashing; never writes, never throws.
- **C-115-6** (SEC-115-06) Dispatcher gate sits inside each existing branch with the identical predicate and returns the shared unknown-endpoint result immediately; `ReadAsync` gate is the first statement; golden no-oracle tests for describe/docs.
- **C-115-7** (SEC-115-07) The inventory table above is added to the plan's section 1 and to the feature doc with the scope statement; async-jobs behaviour is verified by test and any gap is reported, not silently absorbed.
- **C-115-8** (SEC-115-08) `IReadPathSwitches` resolved eagerly; the same instance drives route mapping and the registry; no `IOptionsMonitor`.
- **C-115-9** (SEC-115-09) Two-sided, source-complete CI completeness test as specified.
- **C-115-10** The no-oracle contract of section 3 ("byte-identical") is tested over both MCP transports for tools, resources, prompts and over `invoke_api`/`describe_api`, comparing the full serialized response modulo only the echoed name/URI/endpoint string, which the test substitutes with a placeholder on both sides.
- **C-115-11** No switch method accepts request-derived data; a reflection test asserts `IReadPathSwitches` has no member taking `HttpContext`, `ClaimsPrincipal`, `McpSessionContext` or `RequestContext`.
- **C-115-12** Defaults remain byte-identical (AC 4) and `Mcp:Enabled=false` keeps the new options inert while still validating them; the hardened-profile startup line (test 22) reflects the effective mapping (REST decision, MCP groups, `disabled_tools` count) from the same snapshot.

### 5. Mandatory security tests (`[Trait("Category", "Security")]`, in addition to the planned tests 1-23)

| ID | Test | Covers |
|---|---|---|
| ST-115-01 | `SampleRows_route_not_mapped_when_rest_data_off` (authenticated, 404, empty body, equal to unknown path) and mapped by default | SEC-115-01 |
| ST-115-02 | `Query_off_REST_off_no_row_returning_route_remains`: enumerate `EndpointDataSource` and assert no route whose handler reaches `IGovernedDataQueryService` or `IMcpDatasetCatalog.SampleRowsAsync` remains | SEC-115-01, -07 |
| ST-115-03 | Config validation fails the start for: unknown key under `Tools`, `RestApi`, `DataEndpoint`; scalar `Tools`; scalar `DisabledTools`; `Enabled: null`; `Enabled: "yes"`/`"0"`; null/blank `DisabledTools` element | SEC-115-02 |
| ST-115-04 | Same invalid inputs still fail with `Mcp:Enabled=false` | SEC-115-02, C-115-12 |
| ST-115-05 | Registry built with all groups off and `DisabledTools` full: no row-returning tool is registered or callable; proves `_switches` is set before default initialisation | SEC-115-03 |
| ST-115-06 | Dynamic tool named like a `Catalog`/`Access` built-in with `Query=false`: not callable (collision refused or classified `Query`) | SEC-115-03 |
| ST-115-07 | Dynamic tool registered via discovery and via `AllowedOperations` while `Query=false`: absent from `tools/list` (both transports) and `tools/call` returns the unknown-tool result | SEC-115-03 |
| ST-115-08 | Case/whitespace/Unicode variants of a disabled tool name (`QUERY_SQL`, ` query_sql `, fullwidth letters, trailing NUL): never executable, always the unknown-tool result | T-3 |
| ST-115-09 | Never-registered name with `Query=false`: DENY audit without the "disabled by configuration" text; disabled-path counter unchanged; disabled built-in name: audit has the reason, counter +1; 10,000 random unknown names do not grow any registry/observer state | SEC-115-04 |
| ST-115-10 | No-oracle golden over both transports for tool call: disabled built-in, `DisabledTools` name, group-disabled tool vs random unknown name; identical status, JSON-RPC code, `isError`, content shape (name substituted) | C-115-10 |
| ST-115-11 | No-oracle golden for resources (list, templates, read, semantic) and prompts (list, get) over SDK and JSON-RPC handlers; also `autheris://datasets/{id}` with `Catalog` off equals an unknown dataset | C-115-10, SEC-115-06 |
| ST-115-12 | `invoke_api` with strict mocks: each disabled branch returns the exact unknown-endpoint string for a never-existing endpoint, makes zero calls to `IGovernedDataQueryService`, `ICatalogDiscoveryService`, `IGovernedSqlExecutionService`; includes `/API/V1/Data/x`, ` /api/v1/data/x `, `/api/v1/data/d.s.t?x`, `/api/v1/database` (must not be treated as the data family beyond existing behaviour) and every HTTP method | SEC-115-06 |
| ST-115-13 | `describe_api`, `GetEndpointsDocumentationAsync`, `BuildStaticOpenApiJson`, `GetMcpToolsDocumentationAsync`, `autheris://api/*` resources: no disabled path or tool name appears in the serialized output (substring leak test); disabled `describe_api` output equals never-existing output | SEC-115-06 |
| ST-115-14 | `BuildPrompt`/`GetPrompt` for a gated prompt equals unknown prompt result in both handlers | SEC-115-06 (prompt claim) |
| ST-115-15 | `ReadAsync` on a disabled resource makes zero calls to discovery/dispatcher (strict mocks) | SEC-115-06 |
| ST-115-16 | Observer: 100,000 requests with distinct principals keep state at the fixed capacity; log lines per minute capped; anonymous requests produce no log line; log text contains no raw SID, token, query string or path suffix; observer throwing internally does not change the response | SEC-115-05 |
| ST-115-17 | Observer registered only when the switch is off, and is a no-op for non-matching paths (`/api/v1/database`, `/api/v1/catalog`) | SEC-115-05 |
| ST-115-18 | `IReadPathSwitches` and endpoint mapping agree when the configuration provider changes after startup (reload simulated); the switch value does not change after startup; no `IOptionsMonitor<GatewayOptions>` dependency in `ReadPathSwitches` (reflection on constructor parameters) | SEC-115-08, A-7 |
| ST-115-19 | Closed REST route vs `/api/v1/data-nonexistent-xyz`: identical status, headers (excluding trace ids), empty body, for authenticated and anonymous callers | SEC-115-08 |
| ST-115-20 | Async jobs: `POST /api/v1/jobs/query` with `WebSql:Enabled=false` cannot read a client-selected source (result recorded; failure reported to the owner as a separate issue) | SEC-115-07 |
| ST-115-21 | Completeness: two-sided, source-complete, with the mutation-style self-test (SEC-115-09) | SEC-115-09 |
| ST-115-22 | `IReadPathSwitches` surface has no member taking request-scoped types (reflection) | C-115-11 |
| ST-115-23 | Self-widening: a tool call, resource URI, prompt argument, header and claim set that name another tool/group/`Enabled` flag change nothing about the registry or the switches (property test over crafted arguments) | T-3 |
| ST-115-24 | Fail-closed audit: with the audit repository throwing, a disabled-tool call aborts (SecurityException) outside `IsMcpAuthBypassed` and returns the unknown result inside it | existing behaviour preserved |

### 6. Recommendations for owner flags

- **Q-115-4 (group membership wider than the PRD list):** accept the plan default. Narrowing `Query` would leave `query_sql`/`sample_rows`/dynamic tools callable, which contradicts the owner rule. Add the registration-origin rule (C-115-3) to the decision.
- **New Q-115-5 (doc tools and `deposit_documentation`):** the six doc tools are in no group. `deposit_documentation` is a write tool and `search_docs` returns documentation content. Recommendation: keep them ungrouped (`DisabledTools` only) for #115, document that `Catalog=false` does not close them, and record a follow-up if the owner wants a `Docs` group. This is a scope decision, not a security hole for the stated read-path purpose.
- **New Q-115-6 (REST catalog/identity routes):** confirm that `Catalog`/`Access` are MCP-surface switches and the REST catalog/identity routes stay always on (the plan's current behaviour). Recommendation: confirm; revisit only if an operator needs a REST-side catalog switch (separate requirement).
- **F-2 (no new audit event type):** accept with SEC-115-04/-05 conditions. If the owner later wants a chain row per REST probe, add it as a rate-limited summary event, not per request.
- **F-3 (malformed switch aborts the start):** accept and extend per SEC-115-02 (strictly stronger than the PRD).
- **F-4 (system virtual tables stay reachable through WebSQL/`query_dataset` when only REST is off):** accept; the operator doc must state that closing REST does not close WebSQL or `query_dataset`; use `DisabledTools`/`Query` for that.
- **F-5 (`invoke_api` follows the groups of what it reaches):** accept; required by the owner rule (an agent must not reach through composition what a switch closed).
- **SEC-115-01 decision:** recommend option (a), gating sample-rows with the REST switch.

### 7. Verdict

**APPROVED WITH CONDITIONS.** The architecture (one enforcement point at the registry, mapping-level unmapping for REST, gating the in-process dispatcher, startup-only switches, fail-closed names) is sound and every tool call path was verified to resolve through the registry. It must not enter Phase 4 without folding in C-115-1..12; the two High findings (an ungated REST row route and silent fail-open on malformed switch configuration) are closed only by C-115-1 and C-115-2. The plan owner updates sections 1, 2 (A-2, A-3, A-8, A-9), 6 and 9 accordingly when starting Phase 4 or hands the conditions to the TDD developer verbatim; test ids ST-115-01..24 supersede the "ids assigned in Phase 3" note in section 6.

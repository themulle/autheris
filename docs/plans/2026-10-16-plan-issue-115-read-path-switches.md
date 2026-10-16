# Implementation Plan: REQ-SEC-115 - Switches for the Remaining Read Paths (REST Data Endpoint, MCP Tool Groups)

**Document ID:** `PLAN-SEC-115` (revision 1, 2026-10-16)
**Phase:** 2 (Solution Architect, `csharp-architect`)
**Input:** [PRD REQ-SEC-115](2026-10-16-req-issues-111-115-catalog-access-analytics.md) section 6 (decisions D-115-1..4, FR-115-1..6, SI-115-1..4, AC 1..6, questions Q-115-1..3, verification item T-115-1) and section 8
**Branch:** `feat/issue-115-read-path-switches` (planned; base `main`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md) | **Requirement:** `REQ-SEC-115` | **Delivery tracker:** [`PLAN-DELIVERY-W23`](2026-10-16-plan-waves-2-3-delivery.md) (step P-5, implementation step I-4)
**Status:** PROPOSED (Phase 2 complete; next: Phase 3 security review, then Phase 4 TDD implementation)

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

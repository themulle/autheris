# Architecture Review 02 - src/Autheris.Api

## 1. Scope and key metrics
Reviewed `src/Autheris.Api` (Endpoints, Middleware, Mcp, Security, Serialization, Logging, Configuration, UI, Hosting; Program.cs only for pipeline order). Read-only.

- ~22.6k C# lines total; `Endpoints/` = 11.0k lines in 35 files; 22 middleware classes; MCP = 2 files (GatewayMcpServer 475, GatewayMcpOAuth 79).
- ~170 `RequireAuthorization`/`AllowAnonymous` call sites; only 7 `MapGroup` calls across 35 endpoint files (most files call `app.MapX("/full/path")`).
- 191 ad-hoc error bodies (`new { error = ... }` / `Results.Json(new { error`) vs 20 `Results.Problem`; 1 `WithTags/Produces*` OpenAPI annotation in all of Endpoints.
- 43 imperative `HasRole/HasAnyRole` checks inside handlers; 0 named-policy `RequireAuthorization("...")` uses in Endpoints.
- 60 `GetService/GetRequiredService` calls and 47 `RequestServices` uses in Endpoints/Middleware/Mcp (service locator).
- 9 inline `app.Use(async ...)` lambdas in `UseGatewayPipeline`.

Top 15 files by lines: WebSqlEndpoints 1018; GovernanceEndpoints 987; VirtualFilterEndpoints 919; ODataEndpoints 796; GatewayStartupValidator 769; DevPortalEndpoints 705; GatewayServiceCollectionExtensions 672; SqlEndpointRoutes 535; GatewayApplicationBuilderExtensions 501; GatewayMcpServer 475; ParquetGraphQLResponseMiddleware 438; BasicAuthenticationHandler 392; DbtEndpoints 389; WebhookEndpoints 383; ProcedureEndpointRoutes 377.

## 2. Strengths
- MCP tool execution is a single choke point: `CallGuardedToolAsync` routes tools and dataset resources through `IAiDataGuardrailService` (`Mcp/GatewayMcpServer.cs:407-430`, reused for resource reads at `:168-173`); protocol/transport is delegated to the official SDK in stateless mode (`:45-47`).
- Per-request identity for MCP is derived from the authenticated principal, no session state (`Mcp/GatewayMcpServer.cs:432-452`).
- Query endpoints delegate to Application services (`IGovernedSqlExecutionService`, `IODataHandler`, `IAsyncQueryJobManager`); e.g. `Endpoints/AsyncJobEndpoints.cs:28-46` is thin.
- WebSQL streams rows through `Utf8JsonWriter` straight onto the response body with a row-limited reader (`Endpoints/WebSqlEndpoints.cs:267-345`, `RowLimitedDataReader`).
- Bounded response buffering with pass-through on overflow and streaming-request exclusion (`Middleware/BoundedResponseBufferStream.cs`, `Middleware/GatewayExtensibilityMiddleware.cs:120-148`).
- Cancellation is broadly propagated (168 `RequestAborted` uses; MCP handlers pass `ct`); no `.Result`/`.Wait()`/`Task.Run` sync-over-async found.

## 3. Findings overview

| ID | Sev | Category | Title |
|---|---|---|---|
| ARC-A-01 | High | Layering | Governance/VirtualFilter/DuckDb/OData handlers hold authorization, tenant resolution, catalog lookup and audit writing |
| ARC-A-02 | High | Consistency | Audit writing is hand-rolled per handler with divergent fail-open vs fail-closed semantics |
| ARC-A-03 | High | Consistency | No shared error contract: 191 ad-hoc error shapes vs 20 ProblemDetails |
| ARC-A-04 | High | Maintainability | Handlers/route files are god files (WebSql 1018, Governance 987, VirtualFilter 919) |
| ARC-A-05 | Medium | Layering | MCP native resources bypass the guardrail pipeline and render markdown inside the Api layer |
| ARC-A-06 | Medium | Cohesion | MCP admin/role logic duplicated; `GatewayMcpServer` depends on `McpEndpoints` |
| ARC-A-07 | Medium | API Design | Inconsistent route conventions, versioning and duplicate aliases |
| ARC-A-08 | Medium | Consistency | Authorization is imperative (`HasRole` in handlers) and not declarative policies |
| ARC-A-09 | Medium | Testability | `UseGatewayPipeline` contains 9 inline lambdas incl. CSRF/CORS logic |
| ARC-A-10 | Medium | Consistency | Path rewrite for `/graphql/{domain}` runs after the security pipeline |
| ARC-A-11 | Medium | Performance | OData flat entity-set route loads the full catalog per request; duplicate tenant resolution |
| ARC-A-12 | Medium | Performance | Arrow export, Parquet and extensibility egress fully buffer rows/bytes |
| ARC-A-13 | Medium | DI/Lifetime | Service locator via `RequestServices.GetService` in handlers/middleware |
| ARC-A-14 | Medium | Maintainability | Hand-built OpenAPI documents and HTML/JS inside endpoint classes |
| ARC-A-15 | Low | Consistency | Query-option parsing and OData error shapes duplicated within ODataEndpoints |
| ARC-A-16 | Low | Resilience | Missing CancellationToken on 19 `WriteAsJsonAsync` calls; DuckDb handler overuses closures |
| ARC-A-17 | Low | Testability | `internal static` handlers + `InternalsVisibleTo` as the testing seam; `EndpointSecurity` is static |
| ARC-A-18 | Low | Consistency | Endpoint registration is a 34-line manual list; no discovery/convention |
| ARC-A-19 | Low | Config | MCP base path resolved in two places with different normalization |

## 4. Details

### ARC-A-01 (High, Layering) Business logic, authorization and persistence access in endpoint handlers
- Evidence: `Endpoints/GovernanceEndpoints.cs:63-137` (tenant/domain resolution, role gating, 20 MB body read, audit entry composition and write inside the lambda); `Endpoints/GovernanceEndpoints.cs:141-168` (tenant override logic); `Endpoints/VirtualFilterEndpoints.cs:222-330` (`EffectiveFiltersAsync`: role check, SID/CSV parsing, catalog paging via `ITableMetadataRepository.GetAllTablesAsync`, per-table `ExplainAsync` loop, two audit writes, response shaping); `Endpoints/DuckDbOlapEndpoints.cs:90-200` (table-name resolution against `metadataRepository.GetAllTablesAsync` at `:180`, row/table budget logic, access resolution, audit); `Endpoints/ArrowExportEndpoints.cs:165` (builds `SELECT * FROM {table}` SQL in the handler).
- Problem: Orchestration that belongs in Application services lives in static route classes. Endpoints inject `ITableMetadataRepository`, `IAuditLogRepository`, `IOwnershipRepository` directly (see `GovernanceEndpoints.cs:55-58`, `VirtualFilterEndpoints.cs:212-214`).
- Impact: Not unit-testable without HttpContext; rules (who may inspect filters, tenant override, audit events) cannot be reused by MCP/GraphQL/other transports; Api layer reaches around the Application layer to repositories.
- Recommendation: Introduce use-case services (e.g. `IOpenApiIngestionUseCase`, `IEffectiveFilterInspector`, `IOlapStagingService`) taking a `RequestContext`/`CallerSecurityContext` (already used by `DatasetDataEndpoints.cs:187`) and returning result types; handlers map result -> HTTP only. Start with VirtualFilter `EffectiveFiltersAsync` and DuckDb staging.
- Effort: L

### ARC-A-02 (High, Consistency) Audit writes are copy-pasted with contradictory failure semantics
- Evidence: Fail-open (audit skipped if repo missing): `Endpoints/GovernanceEndpoints.cs:112-114`, `:319`, `:751`, `:903`; `Endpoints/VirtualFilterEndpoints.cs:232-233`, `:284-285`, `:320-321`, `:538`; `Endpoints/DevEndpoints.cs:95`. Fail-closed with 503: `Endpoints/DuckDbOlapEndpoints.cs:107-141` (local `TryAuditAsync` closure).
- Problem: ~10 places build `AuditLogEntry` by hand with `GetService<IAuditLogRepository>()` and `if (auditRepo != null)`. A missing registration silently disables auditing on governance-config endpoints, while DuckDb treats the same condition as an error. Parallel to a declarative mechanism (`WithAudit(...)`, `MarkDomainAudited` in `AsyncJobEndpoints.cs:36`).
- Impact: Audit coverage depends on handler authors; divergence is invisible; the `OccurredAt`/`TraceId` fields are set inconsistently (`GovernanceEndpoints.cs:121` sets TraceId, `VirtualFilterEndpoints.cs:243` sets OccurredAt only).
- Recommendation: One `IDomainAuditWriter` (Application) with `RecordRequiredAsync` (fail-closed) and `RecordBestEffortAsync`, resolving actor/tenant/trace from the request context; make the dependency constructor-injected (non-optional). Replace all `GetService<IAuditLogRepository>` usages.
- Effort: M

### ARC-A-03 (High, Consistency) No single error contract
- Evidence: `Results.Problem` only in 20 places (e.g. `Endpoints/ArrowExportEndpoints.cs:151-168`); 191 sites emit `{ error = "..." }` shapes (e.g. `Endpoints/DuckDbOlapEndpoints.cs:96,106,140,176`, `Endpoints/VirtualFilterEndpoints.cs:260-275`, `Middleware`-level `Extensions/GatewayApplicationBuilderExtensions.cs:131-146`, `Endpoints/McpEndpoints.cs:60`); OData-specific envelope duplicated (`Endpoints/ODataEndpoints.cs:402-433, 608`); bare `Results.StatusCode(403)` 84 times (e.g. `GovernanceEndpoints.cs:69,147`) with no body. `GatewayExceptionHandler`/`GatewayDenialStatusMapper` exist but are not used by the handlers.
- Problem: Clients get four different error bodies (RFC 7807, `{error:string}`, OData `{error:{code,message}}`, empty 403); `WriteAsJsonAsync` + manual `StatusCode` is used in places where a typed result would do.
- Impact: Hard to document, client SDK generation breaks, `ErrorSanitizingFilter`-style leakage rules (see RR-L3-03 comment in `ArrowExportEndpoints.cs:189`) must be re-implemented per handler.
- Recommendation: Define `GatewayProblem` factory (`Forbidden()`, `BadRequest(code)`, `NotFound()` -> `Results.Problem` with stable `type`/`code` extension) and an `ODataProblem` variant limited to `/odata`; replace by search/refactor; add an architecture test banning `new { error =` in `Endpoints/`. Make production-vs-dev detail switch (`isDev ? 404 : 403` at `DuckDbOlapEndpoints.cs:186`) a single helper.
- Effort: M

### ARC-A-04 (High, Maintainability) God files and mixed responsibilities
- Evidence: `Endpoints/WebSqlEndpoints.cs` (1018 lines: routing at `:45-101`, request handling `:119-380`, Parquet branch `:380`, error mapping `:444-595`, hand-built OpenAPI `:610-755`, value writer `:755`, Trino queued-statement protocol `:807-1018`); `Endpoints/GovernanceEndpoints.cs` (987: ingestion, policy simulation, sunsetting, differential-privacy, 13+ unrelated features in one `MapGovernanceEndpoints`); `Endpoints/VirtualFilterEndpoints.cs` (919); `Endpoints/ODataEndpoints.cs` (796, includes Swagger UI HTML at `:711-796`); `Endpoints/DevPortalEndpoints.cs` (705, HTML templates `:223-703`).
- Problem: Several protocols (WebSQL, Trino REST, OpenAPI docs, Parquet export) co-located; `GovernanceEndpoints` also contains six other governance files' neighbours (`GovernanceApiEndpoints`, `GovernanceClassificationEndpoints`, `GovernancePlanEndpoints`) with overlapping prefixes.
- Impact: Merge conflicts, hard review, cannot test a protocol in isolation.
- Recommendation: Split per protocol/feature: `WebSql/` (Endpoints, TrinoStatement, OpenApiSpec, ErrorMapper, JsonRowWriter), `Governance/` one file per feature group with `MapGroup`. Move HTML/CSS/JS to embedded resources (see ARC-A-14).
- Effort: M

### ARC-A-05 (Medium, Layering) MCP native resources bypass the governance pipeline and render in the Api layer
- Evidence: `Mcp/GatewayMcpServer.cs:175-300` `ReadNativeResourceAsync` calls `ICatalogDiscoveryService`/`IApiDispatcherService` directly and builds markdown tables with StringBuilder; builds `CallerSecurityContext` by hand (`:183-193`) instead of reusing `McpProtocolHandler.BuildPrincipalFromSession` used elsewhere (`:97`, `:164`); `:134-136` lists semantic resources; tools and dataset resource go through `CallGuardedToolAsync` (`:168`) but catalog/summary, datasources, my-access, openapi, docs do not pass the guardrail (no token-budget, PII scrub, MCP audit).
- Problem: Two paths for MCP reads; presentation (markdown) in a static class of the Api project; null-service fallbacks (`discovery != null ? ... : []`) silently return empty content when a service is missing (fail-open, hides misconfiguration).
- Impact: Catalog/endpoint listings are not subject to the per-tool governance that tools enjoy; changing resource formats needs Api changes; hard to unit test (static, `IServiceProvider` locator).
- Recommendation: Move resource reading behind an Application `IMcpResourceProvider` (strategy per URI scheme) that internally uses the guardrail/audit; keep Api limited to SDK mapping. Replace the `GetService` + null fallback by required dependencies.
- Effort: M

### ARC-A-06 (Medium, Cohesion) Admin-role logic and caller resolution misplaced
- Evidence: `Mcp/GatewayMcpServer.cs:67-73` hard-codes "ClusterAdmin/GovernanceAdmin/TenantAdmin/Admin" strings for tool visibility, while enforcement lives in `Application/Mcp/Services/AiDataGuardrailService.cs:231` (`McpAdminTools.IsAdminTool`) and `Endpoints/EndpointSecurity.cs:59-73` has separate admin predicates; `GatewayMcpServer.Caller` calls `McpEndpoints.ResolveCaller` (`Mcp/GatewayMcpServer.cs:436`) - the server class depends on an endpoint-registration class. `ResolveCaller` (`Endpoints/McpEndpoints.cs:182-227`) is 45 lines of claim fallback chains.
- Problem: Visibility rules (list_tools) and enforcement (call_tool) can drift; "Admin"/"TenantAdmin" strings are not `GatewayRole` constants; the caller-resolver sits in the wrong file and is internal-static.
- Impact: A role rename updates enforcement but not discovery (or vice versa); tests need an HttpContext.
- Recommendation: Add `IMcpCallerResolver` (Api/Security) + `IMcpToolRegistry.GetAvailableTools(caller)` so the Application registry filters by `GatewayRole` constants; delete the inline role list.
- Effort: S

### ARC-A-07 (Medium, API Design) Inconsistent route conventions and aliases
- Evidence: Same domain under two prefixes: `/api/v1/governance` (`Endpoints/VirtualFilterEndpoints.cs:38`) vs unversioned `/api/governance/...` in 32 handler registrations (`GovernanceEndpoints.cs:63,141,172,186,...`); WebSQL registered four times (`/api/v1/sql`, `/api/sql`, `/v1/statement`, plus `/api/v1/queries` in `SqlEndpointRoutes.cs:33`) at `WebSqlEndpoints.cs:61-101`; also `/api/extensions` (13), `/api/webhooks`, `/api/schema-registry`, `/api/auth` unversioned. Only 7 `MapGroup`; the remaining files repeat the full path in each registration. OpenAPI docs for SQL exist at `/api/v1/sql/openapi.json` (`WebSqlEndpoints.cs:49`) and `SqlEndpointRoutes`/`ProcedureEndpointRoutes.cs:44`/OData separately.
- Problem: No versioning policy, no group-level defaults (auth/audit/tags/filters), literal route strings repeated.
- Impact: Cannot apply cross-cutting filters per group; API surface unpredictable; deprecating an alias requires touching handlers.
- Recommendation: Introduce `ApiRoutes` constants + `MapGroup("/api/v1/<area>")` per feature with `.RequireAuthorization(policy)`/`.WithTags`, keep legacy paths as explicit, deprecation-marked aliases (`.WithMetadata(new DeprecatedRouteAttribute)`), document the policy in an ADR.
- Effort: M

### ARC-A-08 (Medium, Consistency) Imperative authorization inside handlers
- Evidence: 43 `HasRole/HasAnyRole` calls in handlers, e.g. `Endpoints/GovernanceEndpoints.cs:65-69`, `:147` (via helper), `Endpoints/VirtualFilterEndpoints.cs:201-205, 222-224`; 121 plain `RequireAuthorization()` and 0 named policies in Endpoints, although `Security/GatewayPolicies.cs:94` defines `AddPolicy`. `Endpoints/DevEndpoints.cs:30` uses a group-wide `.AllowAnonymous()`.
- Problem: Endpoint metadata says "any authenticated user" while the handler returns 403 for most callers; role rules are not visible to OpenAPI/endpoint inventory or to the `DevForbiddenDiagnosticsMiddleware`.
- Impact: Policy audits/tests that enumerate endpoint metadata miss real requirements; 403 body-less responses (ARC-A-03) result from this style.
- Recommendation: Move role/tenant-scope checks into named policies / `IAuthorizationRequirement` handlers (`RequireAuthorization(GatewayPolicies.FilterSync)`) and keep only data-dependent checks (per-table ownership) in services.
- Effort: M

### ARC-A-09 (Medium, Testability) Pipeline composition contains behaviour in anonymous lambdas
- Evidence: `Extensions/GatewayApplicationBuilderExtensions.cs:26,40,55,77,93,113,126,144,369` - nine `app.Use(async ...)`; the CSRF/CORS middleware at `:126-200` (CORS allow-all header emission + OPTIONS short-circuit + WebSocket detection + preflight-header matrix) and MCP content-type guard (`:93-105`); edge-stripping at `:55-70`. `UseGatewayPipeline` is ~370 lines.
- Problem: Security-relevant behaviour is untestable in isolation and mixes CORS (already `app.UseCors()` at `:52`) with a hand-rolled CORS response (`:128-139`).
- Impact: Duplicate CORS mechanisms; regressions need full-host tests; other middleware are proper classes, so the inconsistency hides the CSRF logic.
- Recommendation: Extract `CsrfPreflightMiddleware`, `EdgeHeaderStrippingMiddleware`, `McpContentTypeGuardMiddleware`, `ForwardedHeaderSanitizerMiddleware` as classes with options; replace the custom allow-all CORS by a CORS policy.
- Effort: S

### ARC-A-10 (Medium, Consistency) Domain-scope rewrite after auth/audit
- Evidence: `Extensions/GatewayApplicationBuilderExtensions.cs:369-382` (in `MapGatewayEndpoints`) rewrites `/graphql/{domain}` -> `/graphql` and sets `Items["DomainScope"]`; the pipeline that already ran (`UseGatewayPipeline`, `:288-331`) includes `AccessAuditMiddleware`, `ReadOnlyTokenMiddleware(graphQlPath)` (`:306-309`), `SchemaContractMiddleware`, `FinOpsBudgetMiddleware` that inspect `Request.Path`.
- Problem: Middleware ordering depends on code in a different method; the audit/read-only checks see the unrewritten path, endpoint routing sees the rewritten one; a string-keyed `HttpContext.Items` bag (21 usages) carries the scope.
- Impact: Subtle drift (audit path != routed path); magic string key.
- Recommendation: Replace by a route (`MapGraphQL(endpoint + "/{domain}")`) or move the rewrite into the pipeline before `UseAuthentication`; expose scope through a typed `IFeatureCollection` feature instead of `Items["DomainScope"]`.
- Effort: S

### ARC-A-11 (Medium, Performance) Catalog loaded per request; duplicate tenant logic
- Evidence: `Endpoints/ODataEndpoints.cs:344-361` - `GetAllTablesAsync` then LINQ filter by tenant and CSDL entity name on every flat `/odata/v4/{entitySet}` call; tenant taken via `context.User.GetTenantId()` (`:345`) whereas other handlers use `EndpointSecurity.GetRequestTenant` (`AsyncJobEndpoints.cs:28`, `GovernanceEndpoints.cs:70`) or `context.Items[TenantIdItemKey]` (`GovernanceEndpoints.cs:149`); same full-scan in `DuckDbOlapEndpoints.cs:180` per unqualified table name inside a loop.
- Problem: O(tables) per request plus three tenant sources (claim, Items key, `SecurityPrincipalContext`).
- Impact: Latency growth with catalog size; risk of tenant mismatch between paths (`LegacySingleTenant`/"default" fallback at `ODataEndpoints.cs:347-350`).
- Recommendation: Add `ITableMetadataRepository.FindByEntitySetNameAsync(tenant, name)` backed by a cached index (invalidated with the existing epoch cache); have all handlers read tenant exclusively from `SecurityPrincipalContext`.
- Effort: M

### ARC-A-12 (Medium, Performance) Full buffering on egress paths
- Evidence: `Endpoints/ArrowExportEndpoints.cs:185-186` `ExecuteQueryBufferedAsync` -> `queryResult.Rows` materialized; `Endpoints/ODataEndpoints.cs:498-501` `ExtractEntitySetRows(result.Payload)` then Parquet write of in-memory rows; `Middleware/ParquetGraphQLResponseMiddleware.cs:138-168` buffers up to 64 MB (`:26`) and re-parses JSON (`TryExtractRows` `:265`) to produce Parquet; `Middleware/GatewayExtensibilityMiddleware.cs:130-174` buffers every non-streaming response up to `MaxExtensibilityBufferBytes` and copies `ToArray()` (`BoundedResponseBufferStream.cs:39`, `GatewayExtensibilityMiddleware.cs:173`).
- Problem: Double materialization (DB rows -> JSON -> buffered bytes -> parse -> rows -> Parquet); `GetBufferedBytes()` allocates a new array instead of exposing the `MemoryStream` buffer; extensibility buffering is applied to all responses regardless of whether any egress interceptor is registered (check not visible before line 120).
- Impact: LOH allocations and memory pressure under concurrent large exports; GC pauses.
- Recommendation: Skip the capture stream when no egress interceptor/Parquet request is present; expose `ReadOnlyMemory<byte>`/`ArraySegment` via `TryGetBuffer`; for Parquet, stream rows from the governed executor (the WebSQL path already streams) rather than re-parsing GraphQL JSON.
- Effort: M

### ARC-A-13 (Medium, DI/Lifetime) Service locator inside handlers/middleware
- Evidence: `Endpoints/ArrowExportEndpoints.cs:174-176, 190-192` (`GetService<IOptions<GatewayOptions>>`, `ILoggerFactory`, `IHostEnvironment` per call); `Endpoints/WebSqlEndpoints.cs:127` (`GetService<IWebSqlStatementManager>`); `Endpoints/ODataEndpoints.cs:491, 358`; `Middleware/SecurityContextResolutionMiddleware.cs:40, 90, 103, 134, 152`; `Middleware/GraphQlEnumerationShieldMiddleware.cs:51`; `Mcp/GatewayMcpServer.cs:61-62, 97, 187-188` (`context.Services!.GetRequiredService`); `Endpoints/ArrowExportEndpoints.cs:174` null-conditional on `RequestServices?`.
- Problem: Hidden dependencies, optional-service semantics (`?.`) that make security components fail-open when unregistered (see ARC-A-02, ARC-A-05); `IOptions<GatewayOptions>` re-resolved each request.
- Impact: Tests must build a full service provider; missing registrations only surface at runtime.
- Recommendation: Use parameter injection (minimal API) / constructor injection (middleware, `InvokeAsync` params) for required services; reserve `GetService` for truly optional plugin points and document them.
- Effort: M

### ARC-A-14 (Medium, Maintainability) Documents and UI generated in code
- Evidence: Hand-built OpenAPI dictionaries: `Endpoints/WebSqlEndpoints.cs:610-755`, `Endpoints/ProcedureEndpointRoutes.cs:85-180`, `Endpoints/SqlEndpointRoutes.cs` (openapi route), `Endpoints/ODataEndpoints.cs:117-300`; HTML/CSS/JS raw strings: `Endpoints/ODataEndpoints.cs:719-796`, `Endpoints/DevPortalEndpoints.cs:223-490, 510-703`; only 1 `WithOpenApi/Produces*` usage in Endpoints.
- Problem: At least four independent OpenAPI generators, none connected to the real endpoint metadata, so docs drift from routes; UI markup is interpolated into C# strings (`$$"""`).
- Impact: Duplicate maintenance; escaping/CSP-nonce bugs risk; `Assets/` folder already exists but is unused for these.
- Recommendation: Adopt `Microsoft.AspNetCore.OpenApi` document + operation transformers fed by endpoint metadata (`WithName/WithTags/Produces<T>`); move HTML to embedded resource templates with a `{{nonce}}` placeholder.
- Effort: L

### ARC-A-15 (Low, Consistency) Repeated query-option parsing and error envelopes in OData
- Evidence: `Endpoints/ODataEndpoints.cs:402-433` three near-identical `Results.Json(new{error=new{code,message}}, contentType: "application/json;odata.metadata=minimal;charset=utf-8")` although `ODataError(...)` exists at `:608`; same query parsing duplicated between `HandleEntitySetRequestAsync` (`:387`) and `HandleCountRequestAsync` (`:515`) and `headers.ToDictionary(...)` allocation at `:480` and `:532`.
- Recommendation: Introduce `ODataQueryOptions.TryParse(IQueryCollection, out options, out IResult error)` and use `ODataError` everywhere; pass `IHeaderDictionary` (or a lazy view) rather than copying headers to `Dictionary<string,string[]>` per request.
- Impact: duplication, allocations on a hot path. Effort: S

### ARC-A-16 (Low, Resilience) Cancellation gaps
- Evidence: 19 of 66 `WriteAsJsonAsync(` calls in Endpoints/Middleware omit a token, e.g. `Extensions/GatewayApplicationBuilderExtensions.cs:99-103, 131-146`; `Middleware/GatewayExtensibilityMiddleware.cs:152` (`ProcessEgressAsync(..., RequestAborted)` ok, but `await _next(context)` at `:135` without comment) and `Endpoints/GovernanceEndpoints.cs:114-136` passes the token but the `Results.Ok` is returned after audit inside try-less code; `Endpoints/DuckDbOlapEndpoints.cs:96-100` mix. Several lambdas take `HttpContext` and read `context.RequestAborted` rather than binding `CancellationToken ct` (only `AsyncJobEndpoints` uses the bound parameter).
- Recommendation: Bind `CancellationToken` parameters in minimal-API handlers and pass them on; add an analyzer (CA2016) at warning level for `Api`.
- Impact: Unnecessary work after client disconnect; Effort: S

### ARC-A-17 (Low, Testability) Static, HttpContext-coupled seams
- Evidence: `Endpoints/EndpointSecurity.cs:19` `internal static class` (218 lines, consumed by nearly all endpoints, reads `HttpContext.Items`); all handlers are `internal static`/lambdas exposed to tests through `InternalsVisibleTo` (`Autheris.Api.csproj:44-45`); e.g. `ODataEndpoints.HandleEntitySetRequestAsync` (`:387`) takes `HttpContext` and reads query/headers/Accept directly; `Security/HttpCallerContextProvider.cs:9` provides an abstraction used by few endpoints.
- Impact: Handler tests need DefaultHttpContext fabrication; no seam to replace tenant/caller resolution.
- Recommendation: Make `ICallerAccessor`/`IRequestSecurityContext` (scoped, populated by `SecurityContextResolutionMiddleware`) the single source and inject it into handlers; keep `EndpointSecurity` for pure helpers only.
- Effort: M

### ARC-A-18 (Low, Consistency) Manual endpoint registration list
- Evidence: 34 `Map...Endpoints/Routes(` calls in `Extensions/GatewayApplicationBuilderExtensions.cs` (grep count) with mixed signatures `(app)`, `(app, gatewayOptions)`, `(app, gatewayOptions, env)` (`McpEndpoints.cs:31-34`, `ODataEndpoints.cs:56`, `GovernanceEndpoints.cs:60`); inconsistent naming (`*Endpoints` vs `*EndpointRoutes` vs `Routes`).
- Recommendation: Define `IEndpointModule { void Map(IEndpointRouteBuilder, GatewayOptions) }`, auto-register via assembly scan or a source-generated list; guarantee feature-flag gating is done in one place (today `Mcp.Enabled` is checked inside `MapMcpEndpoints:38-41` while others gate at call-site).
- Impact: new endpoints can be forgotten or gated inconsistently. Effort: S

### ARC-A-19 (Low, Config) MCP path normalized twice
- Evidence: `Endpoints/McpEndpoints.cs:42-45` (`TrimEnd('/')`, no leading-slash enforcement) vs `Extensions/GatewayApplicationBuilderExtensions.cs:327-339` `ResolveMcpBasePath` (adds leading slash, fallback when empty after trimming); the former is used for `MapMcp(...)` and RFC 9728 redirect target (`McpEndpoints.cs:150`), the latter for ReadOnly/content-type middleware (`:276-309`).
- Impact: A configured `mcp/` or `/` yields a different path in routing vs the guarding middleware -> guard applied to the wrong path (config-dependent bypass of the ReadOnly token / content-type guard).
- Recommendation: Use `GatewayApplicationBuilderExtensions.ResolveMcpBasePath` in `MapMcpEndpoints`; ideally centralize in `GatewayOptions.Mcp.NormalizedPath` and validate in `GatewayStartupValidator`.
- Effort: S

## 5. Notes on non-findings
- No raw SQL/DbConnection/DbCommand in Api; the only SQL string built is the `SELECT * FROM {table}` at `ArrowExportEndpoints.cs:165` (reviewed under ARC-A-01; it is passed to the governed AST pipeline).
- No sync-over-async found. `ErrorSanitizingFilter` and `GatewayExceptionHandler` are sound central pieces but are not used consistently by handlers (ARC-A-03).

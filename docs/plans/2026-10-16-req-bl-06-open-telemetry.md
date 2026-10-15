# PRD: REQ-OPS-BL06 - Tracing, Metrics and Logging via Open Standards (OpenTelemetry)

**Document ID:** `REQ-OPS-BL06`
**Date:** 2026-10-16
**Status:** PRD READY (Phase 1 done); next: Phase 2 (solution architect)
**Author:** Enterprise Product Manager
**Source:** Owner request BL-06 ("tracing / logging via open standards"), [backlog intake](2026-10-16-backlog-intake.md) section 2.3
**Branch:** `feat/bl-06-open-telemetry` (base `main` `272ef93c`)
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)
**Related items:** BL-05 per-data-source configuration (consumes the hook contract in section 7), BL-03 scale analysis (`docs/plans/2026-10-16-analysis-scale-5000-users-governance.md` on branch `feat/scale-5000-users`; needs the measurements of section 5.3), BL-04 table cache (cache metrics).
**Target feature doc (Phase 6):** `docs/features/f-ops-03-opentelemetry-observability.md`

---

## 1. Problem Statement

Autheris already references the OpenTelemetry .NET SDK, but telemetry is fragmented, partly proprietary in shape, and not safe by construction:

- traces start late in the middleware pipeline, are not linked to the caller's W3C trace context, and stop at the gateway boundary (no spans for source databases, governance store, Redis or outbound HTTP);
- two metric APIs coexist (prometheus-net static registry and `System.Diagnostics.Metrics`), several meters are never registered with the OpenTelemetry provider, and names do not follow the OpenTelemetry semantic conventions;
- logs are written by Serilog to the console only; there is no OTLP log export and no automatic log-to-trace correlation;
- correlation uses a custom `X-Correlation-Id` header and the ASP.NET `TraceIdentifier`, which differs from the W3C trace id in audit records;
- redaction exists for query strings only; span tags, exception messages and the opt-in generated-SQL log are not governed by one policy.

The owner wants traces, metrics and logs on open standards so that any compatible backend can be used, with correlation across gateway, governance store and source queries and **no sensitive values in telemetry**.

## 2. Current State Inventory (evidence, `main` `272ef93c`)

### 2.1 OpenTelemetry SDK and exporters

| Finding | Evidence |
|---|---|
| Packages `OpenTelemetry.Exporter.OpenTelemetryProtocol` and `OpenTelemetry.Extensions.Hosting` 1.19.1; no instrumentation packages (AspNetCore, Http, SqlClient, Redis), no in-memory exporter. | `src/Autheris.Api/Autheris.Api.csproj:17-18`, `src/Autheris.Infrastructure/Autheris.Infrastructure.csproj:16` |
| Provider registers traces and metrics only (no logs), sources `Autheris.Core` and `Autheris.Governance.Classification`, meters `Autheris.Metrics` and `Autheris.Governance.Classification`; `AddOtlpExporter()` without options, so it always exports to the SDK default endpoint (`localhost:4317`) even when nothing is configured. No resource (`service.name` falls back to `unknown_service`), no sampler, no views, no exemplars. | `src/Autheris.Api/Extensions/DependencyInjection/GatewayCoreServiceExtensions.cs:122-135` |
| Not registered with the provider, so their spans/instruments are never exported via OTLP: ActivitySource and Meter `Autheris.Mcp`, ActivitySource `TrinoSqlEngine.SqlCompiler` and Meter `Autheris.SqlCompiler`, Meter `Autheris.AsyncJobs`. | `src/Autheris.Application/Mcp/Diagnostics/McpDiagnostics.cs:14-19`, `src/TrinoSqlEngine/CompilerTelemetry.cs:11-17`, `src/Autheris.Application/Jobs/Services/AsyncQueryJobManager.cs:40` |
| Only 7 `StartActivity` call sites in the whole code base (ingress middleware, classification workflow, lineage, MCP guardrail, SQL compiler). No span for governance decisions, source command execution, governance store, Redis, webhooks, async jobs or CDC. | `rg StartActivity src` |
| No OTLP settings in `appsettings*.json`, Helm chart or compose files. | `src/Autheris.Api/appsettings.json`, `deploy/helm/autheris/values.yaml`, `deploy/podman-compose.yaml` |

### 2.2 Traces and propagation

| Finding | Evidence |
|---|---|
| Custom `OpenTelemetryTracingMiddleware` starts `Ingress.HttpRequest` (Server) **after** authentication, rate limiting, CORS and budget middleware, so 401/403/429 rejections and pre-auth work are not traced. ASP.NET Core's own hosting activity (`Microsoft.AspNetCore`, which extracts `traceparent`) is not added as a source; whether the custom span gets the caller's trace as parent depends on listener side effects. | `src/Autheris.Api/Extensions/GatewayApplicationBuilderExtensions.cs:96` (after lines 74-95), `src/Autheris.Api/Middleware/OpenTelemetryTracingMiddleware.cs:15` |
| The ingress span carries `user.sid` (personal identifier) and `tenant.id`, and records the full exception (`AddException`, message + stack). | `OpenTelemetryTracingMiddleware.cs:22-25,48` |
| Span-tag allow-list exists but is used only by the ingress middleware; it allows `db.statement` and `user.sid`. | `src/Autheris.Domain/Diagnostics/GatewayDiagnostics.cs:68-85` (line 72 `db.statement`, line 70 `user.sid`) |
| MCP spans put session id and service principal id on spans. | `src/Autheris.Application/Mcp/Services/AiDataGuardrailService.cs:109-110` |
| Correlation id: custom `X-Correlation-Id` (validated: printable ASCII, max 128), falls back to the W3C trace id; echoed only when supplied. | `src/Autheris.Api/Middleware/CorrelationIdMiddleware.cs:13-75` |
| Problem details use the W3C trace id (`Activity.Current.TraceId`) but endpoints and audit records use `HttpContext.TraceIdentifier` (connection-based, not W3C); `AuditContext.TraceId` defaults to a random GUID. Audit and traces therefore cannot be joined reliably. | `src/Autheris.Api/Errors/ApiProblems.cs:132-133`; e.g. `src/Autheris.Api/Endpoints/GovernanceEndpoints.Access.cs:234`, `src/Autheris.Api/Endpoints/SqlErrorResponse.cs:25`; `src/Autheris.Domain/Audit/AuditContext.cs:12` |
| No explicit propagator configuration and no baggage handling: the .NET default propagator accepts incoming `baggage` and re-emits `traceparent`/`baggage` on every `HttpClient` call (38 typed/named clients, including webhooks and external integrations). | `rg "DistributedContextPropagator\|Baggage" src` (no hits); `src/Autheris.Application/Security/SecureOutboundHttp.cs:43` |
| Async jobs, webhook outbox, CDC poller and Redis event bus do not carry trace context. | `src/Autheris.Application/Jobs/Services/*`, `src/Autheris.Application/Webhooks/WebhookDeliveryHostedService.cs`, `src/Autheris.Extensions/Cdc/MssqlChangeTrackingPoller.cs` |

### 2.3 Logging

| Finding | Evidence |
|---|---|
| Serilog replaces the Microsoft logger factory (`Logging:LogLevel` has no effect); sink is console text only; `Enrich.FromLogContext`. No OTLP log export, no `trace_id`/`span_id` on log events. | `src/Autheris.Api/Program.cs:43-47`, `src/Autheris.Api/appsettings.json:11-22` |
| Redaction: `SensitiveLogPropertyEnricher` strips query strings from `QueryString`/`Uri`/`Url`/`RequestUri`/`Path` (DEP-8); framework categories at Warning. | `src/Autheris.Api/Logging/SensitiveLogPropertyEnricher.cs:11-51` |
| No `[LoggerMessage]` source-generated logging (0 occurrences); message templates are used, but exceptions are logged with full `Exception.Message`, which for driver exceptions can contain literal values. | `rg "\[LoggerMessage" src`; e.g. `src/Autheris.Api/Endpoints/SqlErrorResponse.cs:25`, `src/Autheris.Api/Endpoints/WebSqlEndpoints.Governance.cs:107` |
| Generated SQL logging: global `Gateway:...:Logging:LogGeneratedSql` (default false) logs the compiled secured SQL at Debug in three places. | `src/Autheris.Domain/Options/Security/AuditLoggingOptions.cs:125-128`, `src/Autheris.Application/Sql/Services/GovernedSqlRewriter.cs:642-645`, `src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs:410`, `src/Autheris.Application/Procedures/Services/SqlProcedureRowScopeResolver.cs:177` |
| Log forging: ad-hoc CRLF guards in a few places only (Envoy ext_authz, resource group, ITSM webhook, datasource test). | `src/Autheris.Application/Mesh/Services/EnvoyExtAuthzService.cs:82-88`, `src/Autheris.Api/Middleware/ResourceGroupMiddleware.cs:56`, `src/Autheris.Extensions/Itsm/ItsmWebhookHandler.cs:260` |
| Audit is separate and authoritative: HMAC-chained audit store (F-AUDIT-01), not part of operational logs. | `docs/features/f-audit-01-hardened-cryptographic-audit.md` |

### 2.4 Metrics and health

| Finding | Evidence |
|---|---|
| `/metrics` is served by prometheus-net (`MapMetrics`, protected by the `MetricsScrape` policy) with `UseHttpMetrics`; `System.Diagnostics.Metrics` instruments reach `/metrics` through the prometheus-net meter adapter. | `src/Autheris.Api/Extensions/GatewayApplicationBuilderExtensions.cs:67,126`; `docs/features/f-data-07-async-query-jobs.md:203` |
| Some counters use the prometheus-net API directly (not visible to OTLP). | `src/Autheris.Api/Middleware/RateLimitingMiddleware.cs:29`, `src/Autheris.Api/Security/BasicAuthenticationHandler.cs:32`, `src/Autheris.Infrastructure/Cache/ConsentCacheService.cs:86-89` |
| Naming is mixed: Prometheus style (`autheris_*_total`, `_ms`) in `GatewayDiagnostics`, dotted names in `Autheris.AsyncJobs`/`Autheris.SqlCompiler`, `gen_ai.*` in MCP. Units in names instead of the `unit` argument. | `GatewayDiagnostics.cs:16-65`, `CompilerTelemetry.cs:16-17`, `McpDiagnostics.cs:22-33` |
| No data-path metrics: no source query duration, no rows/bytes, no governance decision latency beyond Casbin, no pool, cache-ratio or queue-lag metrics needed by BL-03/BL-04. | `GatewayDiagnostics.cs` |
| Helm: authenticated `ServiceMonitor` (fails rendering if unauthenticated) and optional `PrometheusRule`; no OTLP endpoint values, no collector example. | `deploy/helm/autheris/templates/servicemonitor.yaml:9,32`, `deploy/helm/autheris/templates/prometheusrule.yaml:1` |
| Health endpoints `/health/live`, `/health/ready` (cached probe); they are not excluded from tracing because there is no HTTP instrumentation yet. | `src/Autheris.Api/Endpoints/HealthEndpoints.cs:33,52` |

### 2.5 Documentation

No feature document describes observability. Telemetry is mentioned in passing in F-DATA-07 (async job instruments), F-GOV-13 (classification telemetry) and F-OPS-02 (Helm monitoring). `docs/configuration-guide.md` and `docs/operations-runbook.md` have no OTLP section.

## 3. Benchmark and Best Practice

| Topic | Best practice | Consequence for Autheris |
|---|---|---|
| Signals and protocol | OpenTelemetry traces, metrics and logs exported with OTLP (gRPC 4317 or HTTP/protobuf 4318), configured through the standard `OTEL_*` environment variables; the OpenTelemetry Collector as vendor-neutral routing/processing tier. | OTLP is the only push protocol; standard env vars are honoured; the collector is recommended, never required. Prometheus pull stays as a compatibility exporter. |
| Instrumentation API | .NET built-ins: `System.Diagnostics.ActivitySource`, `System.Diagnostics.Metrics.Meter`/`IMeterFactory`, `Microsoft.Extensions.Logging` (`ILogger`, `[LoggerMessage]`); SDK only at the composition root. | One instrumentation API per signal. prometheus-net counters move to `Meter`. |
| Semantic conventions | Stable HTTP conventions (`http.request.method`, `http.route`, `http.response.status_code`, `url.scheme`, `error.type`; metric `http.server.request.duration` in seconds); database conventions (`db.system.name`, `db.namespace`, `db.operation.name`, `db.collection.name`, `db.query.summary`, `db.response.status_code`, metric `db.client.operation.duration`). `db.query.text` only for parameterized queries and still opt-in; parameter values never by default. | Gateway-owned `db.*` spans with summary, not text; text is an explicit opt-in (section 6.2). |
| Resource | `service.name`, `service.version`, `service.instance.id`, `deployment.environment.name`; Kubernetes attributes added by the collector (`k8sattributes`). | Resource builder with these attributes, overridable by `OTEL_SERVICE_NAME` / `OTEL_RESOURCE_ATTRIBUTES`. |
| Sampling | Head sampling in the SDK with a trace-id ratio sampler (deterministic, consistent across services); tail sampling (errors, latency, policy denials) in the collector. Honouring an untrusted caller's `sampled` flag lets any client force 100 % sampling. | `TraceIdRatioBased` root sampler; parent `sampled` flag honoured only for trusted callers; tail-sampling example for the collector. |
| Exemplars | Histogram exemplars linking metric points to sampled traces (stable in OpenTelemetry .NET since 1.10). | Enabled for latency histograms (trace-based filter). |
| Trace context | W3C Trace Context (`traceparent`, `tracestate`) for cross-process correlation; W3C Baggage is untrusted input, is propagated to every downstream hop and is visible to third parties. | Accept `traceparent`; drop incoming `baggage` and never derive identity, tenant or authorization from it; do not emit baggage to external parties. |
| Messaging / async | Producer stores the context; consumer starts a new trace or span with a **link** to the producer context (semantic conventions for messaging). | Async jobs, webhook outbox, CDC, Redis event bus use links. |
| Logs | OpenTelemetry log data model (timestamp, severity, body, attributes, `trace_id`, `span_id`, resource); structured templates; JSON for stdout (ECS-compatible field mapping at the collector, not in the app). | `ILogger` + OpenTelemetry logging provider; JSON console; redaction before any exporter. |
| Redaction | `Microsoft.Extensions.Compliance.Redaction` and data classification attributes, `Microsoft.Extensions.Telemetry` enrichment; defense in depth with the collector `redaction`/`transform` processors. | App-side redaction is mandatory (fail closed); collector redaction is an optional second layer. |
| Cardinality | Metric attributes bounded and enumerated; SDK cardinality limit per metric (default 2000) with overflow attribute; no user ids, query text, table ids or raw URLs as metric attributes. | Attribute allow-list per instrument, enforced by tests. |
| No vendor lock-in | No vendor SDKs or vendor-specific exporters in the product; backend choice belongs to the collector. | Only OpenTelemetry and .NET packages; dashboards are examples, not dependencies. |

### 3.1 Rejected anti-patterns

| Anti-pattern | Why rejected |
|---|---|
| Query text, literals or bound parameter values in spans, logs or metrics by default (including `db.statement`/`db.query.text` on by default and driver auto-instrumentation that captures text). | Literals are customer data and can be PII; Autheris-compiled SQL also reveals row-filter and masking policy structure. |
| User identifiers (`user.sid`, UPN, session id, service principal id) as span or metric attributes. | Personal data in a store outside governance, retention and right-to-erasure controls. The join from a trace to a person goes through the audit record (`trace_id`), which is access-controlled. |
| Tenant id, table id, user id, data source name of unbounded sets, route parameters or error messages as metric labels without limits. | Cardinality explosion (5,000 users × many tables, BL-03) and cost; label values become a side channel. |
| Identity, tenant or authorization context carried in baggage, or trusting incoming baggage. | Baggage is attacker-controlled and leaks to every downstream service. |
| `sqlcommenter`-style trace context comments in source SQL. | Changes statement text per request, which pollutes SQL Server and Oracle plan caches and defeats the query plan cache (F-PERF-11); a comment is also an injection surface. |
| Exception messages exported verbatim. | Driver exceptions (e.g. constraint or conversion errors) contain values. |
| A second metric API or vendor agent in the product. | Two sources of truth; vendor lock-in. |
| Exporting the audit chain as operational logs. | Different integrity, retention and access rules; the audit store stays authoritative. |
| Per-data-source head sampling of child spans. | Dropping child spans of a sampled trace produces broken traces; per-source verbosity is expressed through span detail and slow-query thresholds instead (section 7). |

## 4. Decisions on Open Questions (best practice, binding for Phase 2)

| # | Question | Decision |
|---|---|---|
| D-1 | Which standard? | OpenTelemetry for all three signals; OTLP gRPC and HTTP/protobuf; W3C Trace Context; standard `OTEL_*` env vars take precedence over `Gateway:Telemetry`. |
| D-2 | Exporter on by default? | No. An exporter is active only when an endpoint is configured (`OTEL_EXPORTER_OTLP_ENDPOINT` or `Gateway:Telemetry:Otlp:Endpoint`). Without it, instrumentation stays in-process (no network retries). `/metrics` keeps working. |
| D-3 | Serilog or Microsoft.Extensions.Logging? | `Microsoft.Extensions.Logging` with the OpenTelemetry logging provider and the JSON console formatter; Serilog is removed. DEP-8 redaction is re-implemented as a log processor/redactor that runs before every exporter. `Logging:LogLevel` becomes effective again (breaking change accepted: no production deployment). |
| D-4 | Metrics exposition | Instrumentation only via `System.Diagnostics.Metrics`. Export via OTLP (push) and the existing authenticated `/metrics` (pull, prometheus-net meter adapter) until the OpenTelemetry Prometheus exporter is stable. prometheus-net instrumentation APIs (`Metrics.Create*`, `UseHttpMetrics`) are removed; HTTP metrics come from the built-in `Microsoft.AspNetCore.Hosting` meter. |
| D-5 | Metric naming | OpenTelemetry style: dotted lowercase `autheris.<area>.<name>`, unit in the `unit` argument (UCUM, durations in seconds), no `_total`/unit suffix in the instrument name. Existing instruments are renamed (no production); `PrometheusRule` and docs are updated in the same increment. |
| D-6 | Who creates database spans? | Autheris creates one `Client` span per source command in the governed execution path (all engines: SQL Server, PostgreSQL, Oracle, DuckDB, HTTP data sources) and per governance store command, with `db.*` conventions and redaction applied. Driver-native instrumentation (Npgsql, SqlClient, ODP.NET, Redis) is not enabled by default because it can capture statement text; it may be enabled explicitly per source family (Phase 2 decides whether to expose that switch). |
| D-7 | Incoming trace context | `traceparent`/`tracestate` are accepted from any caller (the trace id is continued); malformed headers are ignored. The parent's `sampled` flag is honoured only when `Gateway:Telemetry:Propagation:TrustParentSampling` is true (default false); otherwise the root ratio sampler decides on the trace id. |
| D-8 | Baggage | Incoming `baggage` is removed at ingress (default). Autheris never writes baggage. An allow-list of baggage keys may be configured for internal hops (default empty). |
| D-9 | Outgoing propagation | `traceparent` (not `tracestate`, not baggage) is injected on outbound HTTP to internal integrations (OpenFGA, OPA, GraphQL subgraphs, lineage, ITSM). External egress (customer webhooks, CDC webhooks, lakehouse/object storage) propagates `traceparent` only when enabled for that integration/subscription (default off). No trace context into source SQL (rejected `sqlcommenter`). |
| D-10 | Correlation id | The W3C trace id is the canonical correlation id: problem details, logs and audit records (`AuditContext.TraceId`) use it. `X-Correlation-Id` stays as an optional caller-supplied label: validated as today, recorded as span attribute `autheris.correlation_id` and log attribute, echoed in the response; it never replaces the trace id. |
| D-11 | Identity in telemetry | No user identifier on spans, logs attributes or metrics by default. `tenant.id` (Autheris attribute `autheris.tenant.id`) is on spans and logs by default (needed for collector routing per tenant), off on metrics. An opt-in `PseudonymousUserId` mode emits `enduser.pseudo.id` = keyed HMAC of the SID (key from the secret store, rotatable); clear-text SIDs are never emitted. |
| D-12 | SQL text | Default: `db.query.summary` (operation + collection, e.g. `SELECT sales.orders`) and a statement shape hash `autheris.db.query.hash` (hash of the compiled, parameterized SQL), no text. Opt-in `StatementText = Parameterized` emits the compiled parameterized SQL (never values) as `db.query.text`, only when the global ceiling allows it (section 6.2). `LogGeneratedSql` is folded into this policy. |
| D-13 | Exceptions | Spans record `error.type` (exception type or stable Autheris error code, see `SqlCompilerErrorMapper`) and the status; `exception.message` is exported only for allow-listed Autheris exception types whose messages are value-free; `DbException` and unknown types get `[redacted]`. Stack traces are allowed. Same rule for logs. |
| D-14 | Async work | Producers persist `traceparent` with the work item (async job record, webhook outbox row, Redis event envelope, CDC batch). Consumers start a new root span with a link to the stored context (messaging conventions); long-running pollers create one span per poll cycle, not per process. |
| D-15 | GraphQL and MCP | HotChocolate execution produces one operation span plus resolver-level spans only for data-source fetches (not per field), with `graphql.operation.type` and operation name, never the document or variables. MCP spans keep `gen_ai.*` conventions but drop session id and client principal (D-11). |
| D-16 | Audit vs operational | Unchanged separation: the audit chain is not exported over OTLP. Audit records carry the W3C trace id for joins. |
| D-17 | Collector | Not bundled as a hard dependency. Helm gets values for OTLP endpoint, protocol, headers from a Secret and TLS/mTLS; the docs ship an example collector configuration (tail sampling, redaction processor, tenant routing). |

## 5. Scope

### 5.1 In scope

1. Telemetry composition root: options, resource, exporters, sampler, views, propagators, registration of all Autheris sources and meters (including `Autheris.Mcp`, `Autheris.AsyncJobs`, SQL compiler).
2. Ingress and egress HTTP tracing via ASP.NET Core and `HttpClient` instrumentation (replaces `OpenTelemetryTracingMiddleware`), health and metrics endpoints excluded.
3. Gateway-owned spans for the governed data path: parse/compile, governance decision (consent, ABAC/ReBAC, row filter, masking), source command execution, result streaming, governance store access, Redis cluster state, audit append.
4. Logs via `ILogger` with OpenTelemetry export, JSON console, trace correlation, redaction, Serilog removal.
5. Metric catalog (section 5.3), migration of prometheus-net instruments, renaming, exemplars, cardinality limits.
6. Trace context in async jobs, webhook outbox, CDC, Redis event bus, MCP, GraphQL.
7. Per-data-source telemetry policy hook (section 7) with a global-only implementation.
8. Helm values, example collector configuration, feature documentation F-OPS-03, configuration guide and runbook sections.

### 5.2 Out of scope

- Shipping or operating a collector, dashboards for a specific vendor, or alert rules beyond updating the existing `PrometheusRule` names.
- Trace context propagation into source databases (session context, `sqlcommenter`).
- Profiling signal (OpenTelemetry profiles) and continuous profiling.
- Exporting the audit chain over OTLP or SIEM integrations.
- Per-data-source configuration storage, inheritance and administration UI/API (BL-05).
- Tenant self-service access to telemetry (operators own the backend; tenants use audit and system endpoints).

### 5.3 Minimum metric catalog (needed by BL-03 and BL-04)

| Instrument | Type, unit | Allowed attributes |
|---|---|---|
| `http.server.request.duration` (built-in) | histogram, s | semconv HTTP set (`http.route` template, never raw path) |
| `autheris.request.duration` per surface | histogram, s | `autheris.surface` (odata, graphql, websql, rest, flight, mcp, trino), `error.type` |
| `autheris.governance.decision.duration` | histogram, s | `autheris.decision.stage` (consent, abac, rebac, row_filter, masking), `autheris.decision.result` (allow, deny, error) |
| `autheris.sql.compile.duration` | histogram, s | `db.system.name`, `autheris.sql.statement_class`, `error.type` |
| `db.client.operation.duration` | histogram, s | `db.system.name`, `autheris.datasource.name`, `db.operation.name`, `error.type` |
| `autheris.query.rows` / `autheris.query.bytes` | histogram, {row} / By | `autheris.surface`, `autheris.datasource.name` |
| `db.client.connection.count` / `.wait_time` | updowncounter / histogram | `db.client.connection.pool.name`, `db.client.connection.state` |
| `autheris.cache.requests` | counter | `autheris.cache.name` (consent, plan, decision, keyset (BL-02), table (BL-04)), `autheris.cache.result` (hit, miss, stale) |
| `autheris.asyncjobs.queue.depth`, `autheris.webhook.outbox.pending`, `autheris.cdc.lag` | gauge | `autheris.datasource.name` where applicable |
| `autheris.audit.append.duration` | histogram, s | `autheris.audit.tier` |
| .NET runtime and Kestrel meters (built-in) | - | as defined by .NET |

`autheris.datasource.name` is bounded by configuration and is allowed; tenant, table, user, route parameter and query hash are **not** metric attributes. An opt-in `Gateway:Telemetry:Metrics:IncludeTenant` adds `autheris.tenant.id` to the request and decision histograms only, with a cardinality limit (default 500, overflow to `otel.metric.overflow`).

## 6. Requirements

### 6.1 Functional requirements

| ID | Requirement |
|---|---|
| FR-1 | Telemetry is configured by the standard `OTEL_*` environment variables and by `Gateway:Telemetry` (env wins). Settings: `Enabled`, `Otlp:Endpoint`, `Otlp:Protocol` (`grpc`, `http/protobuf`), `Otlp:Headers` (secret reference), `Otlp:Timeout`, per-signal enable flags, `Sampling:Ratio` (default 1.0 in Development, 0.1 otherwise), `Propagation:*` (D-7 to D-9), `Redaction:*`, `Metrics:*`. Invalid settings fail at startup (`ValidateOnStart`). |
| FR-2 | Resource attributes: `service.name` (default `autheris`), `service.namespace`, `service.version` (assembly informational version), `service.instance.id` (stable per process), `deployment.environment.name`; overridable by `OTEL_RESOURCE_ATTRIBUTES`. |
| FR-3 | Every request gets one server span created by ASP.NET Core hosting at the start of the pipeline, continuing a valid incoming `traceparent`; authentication, rate-limit and authorization rejections are visible on it (`error.type`, status). `/health/*` and `/metrics` are not traced. |
| FR-4 | The governed data path emits child spans as listed in scope item 3 with the attribute sets of section 6.2; one source command = one `Client` span. |
| FR-5 | Every outbound HTTP call produces a `Client` span; propagation follows D-9. |
| FR-6 | Logs are emitted through `ILogger`, exported via OTLP when enabled and written as JSON to stdout; each record inside a span carries `trace_id` and `span_id`. Hot-path log statements use `[LoggerMessage]`. |
| FR-7 | Async work follows D-14; a job, webhook delivery or CDC batch can be found from the originating request trace through its link. |
| FR-8 | Audit records, problem details and log records use the W3C trace id (D-10). |
| FR-9 | All Autheris instruments use `System.Diagnostics.Metrics`, follow D-5, and are registered with the provider; latency histograms carry exemplars when the trace is sampled. |
| FR-10 | The data-path telemetry consults the per-data-source policy (section 7) for the effective settings of the data source being executed. |
| FR-11 | `/metrics` keeps working with the `MetricsScrape` policy and exposes the renamed instruments. |

### 6.2 Security requirements

| ID | Requirement |
|---|---|
| SR-1 Redaction by default | No literal value, bound parameter value, result value, masked or classified column value, request body, query string, GraphQL document/variables, OData `$filter` text or header value (except allow-listed headers) appears in any span, span event, log record or metric. Enforced by a central attribute allow-list per span type (successor of `GatewayDiagnostics.SetSafeTag`) and a redaction processor that runs before every exporter. Unknown attribute keys from Autheris sources are dropped (fail closed). |
| SR-2 SQL text opt-in | `db.query.text` is emitted only if **both** the global ceiling `Gateway:Telemetry:Redaction:AllowStatementText` is true **and** the effective data-source policy requests `StatementText = Parameterized`. The emitted text is the compiled, parameterized SQL (placeholders only); values are never emitted. The same rule governs the former `LogGeneratedSql` debug log. Enabling it logs a startup warning and is visible in the system configuration endpoint. |
| SR-3 Identity | D-11: no clear-text user identifiers; pseudonymous mode only with a keyed HMAC whose key comes from the secret store; session ids, tokens, API keys, cookies and `Authorization` values are never emitted. |
| SR-4 Tenant isolation of telemetry | Every span and log produced in a tenant context carries `autheris.tenant.id` taken from the resolved, authenticated tenant (never from headers or baggage), so the collector can route per tenant. Telemetry is never exposed to tenants through Autheris APIs. Cross-tenant work (cluster jobs) uses the reserved value `cluster`. Metrics follow section 5.3. |
| SR-5 Untrusted propagation input | Malformed or oversized `traceparent`/`tracestate` is ignored (W3C limits: `tracestate` max 32 members, 512 bytes); incoming baggage is dropped (D-8); propagation headers never influence authorization, tenant resolution, rate-limit keys or audit identity. The sampled flag follows D-7, so a client cannot force full sampling. |
| SR-6 Exceptions | D-13: no unredacted exception messages from drivers or unknown types in spans or logs. |
| SR-7 Log forging | User-controlled values only as structured attributes, never interpolated into the message body (analyzer CA2254 as error in `src/`); JSON output escapes control characters; attribute values are length-limited (`OTEL_ATTRIBUTE_VALUE_LENGTH_LIMIT`, default 1024). |
| SR-8 Export channel | OTLP supports TLS and mTLS; credentials (headers, client certificates) come from secrets, never from `appsettings.json`; the exporter never follows redirects to other hosts. Export failures never fail or slow a request (bounded batch queues, drop on overflow, counter `otel.sdk.*` or `autheris.telemetry.dropped`). |
| SR-9 Fail closed vs fail open | Redaction and allow-list failures drop the attribute (fail closed). Telemetry pipeline failures (exporter down, queue full) degrade telemetry only, never the data path (fail open for availability, by design). |
| SR-10 Per-source policy ceiling | A data-source policy can make telemetry less verbose without limit but cannot exceed the global ceiling (statement text, log level below the global minimum for sensitive categories, identity mode). |

### 6.3 Horizontal scaling (several replicas in Kubernetes)

Owner note 2026-10-16: Autheris runs as several instances behind a Kubernetes Service (HPA, BL-03). Telemetry must support this:

| ID | Requirement |
|---|---|
| HR-1 | Every replica has a unique `service.instance.id` (pod UID or generated per process) and the same `service.name`; the collector adds `k8s.pod.name`, `k8s.node.name`, `k8s.deployment.name`. Metrics are aggregated across replicas in the backend, never inside Autheris. |
| HR-2 | Counters and histograms are per process (delta-safe for OTLP push, cumulative for `/metrics` pull per pod via the `ServiceMonitor`); no instrument assumes it is the only instance. Cluster-wide values (async job queue depth, webhook outbox pending, CDC lag, policy epoch) are reported only by the instance that holds the corresponding cluster lease (`ClusterScheduledService`), or carry an attribute stating it is a cluster-scope observation, so that summing over replicas does not multiply them. |
| HR-3 | A trace that crosses replicas (request on pod A, async job on pod B, Redis event handled on pod C) stays connected through stored trace context and links (D-14). |
| HR-4 | Per-replica signals needed for scaling decisions are present: in-flight requests, request duration, thread-pool queue length and GC (built-in runtime meters), connection-pool usage per data source, rate-limit rejections, and Redis/governance store latency; these are the inputs for HPA custom metrics evaluated in BL-03. |
| HR-5 | Export load scales linearly: the recommended topology is an OTel Collector agent (DaemonSet) or sidecar per node forwarding to a gateway collector; tail sampling runs at the gateway collector with trace-id-aware load balancing (`loadbalancing` exporter), documented in the example configuration. |

## 7. Per-Data-Source Hook (contract that BL-05 must provide)

BL-06 defines the contract and ships a global-only implementation; BL-05 adds storage, inheritance (data source overrides global) and administration.

```csharp
// Application layer (abstraction owned by BL-06)
public interface IDataSourceTelemetryPolicyProvider
{
    // Never throws, never returns null. Unknown or unresolvable data source -> global defaults.
    DataSourceTelemetryPolicy GetPolicy(string dataSourceName);
}

public sealed record DataSourceTelemetryPolicy(
    LogLevel MinimumLogLevel,           // category Autheris.DataSource.<name>; clamped to the global ceiling
    SpanDetail SpanDetail,              // None | Basic (one client span) | Detailed (+ fetch/stream events)
    StatementTextMode StatementText,    // None | Summary (default) | Parameterized (needs global AllowStatementText)
    TimeSpan? SlowQueryThreshold,       // span event + Warning log + autheris.query.slow counter above threshold
    bool RecordRowCounts);              // rows/bytes histograms and span attributes
```

Contract obligations for BL-05:

1. **Effective value resolution:** global `Gateway:Telemetry:DataSourceDefaults` is overridden by the data source's `Telemetry` section; every field is independently inheritable.
2. **Ceiling enforcement:** values more verbose than the global ceiling are clamped (SR-10) and the clamp is reported once as a warning and in the configuration view.
3. **Hot path:** `GetPolicy` is O(1), allocation-free on a hit (frozen snapshot), and reflects configuration changes within one reload cycle without restart.
4. **Validation:** invalid per-source values fail validation at load/save time; the previous valid snapshot stays active.
5. **Audit:** changing a per-source telemetry policy (especially `StatementText`) is an audited administrative action.
6. **No sampling field:** per-source head sampling is deliberately not part of the contract (section 3.1); tail sampling by `autheris.datasource.name` is done at the collector.

## 8. Acceptance Criteria

| ID | Criterion |
|---|---|
| AC-1 | With no endpoint configured, no OTLP connection is attempted and the app starts without telemetry warnings; with `OTEL_EXPORTER_OTLP_ENDPOINT` set, traces, metrics and logs arrive at a collector over gRPC and over HTTP/protobuf. |
| AC-2 | A request with a valid `traceparent` produces a server span with the same trace id; the response problem details `traceId`, all log records of the request and the audit record carry that trace id. |
| AC-3 | A request rejected by authentication (401) or rate limiting (429) produces a server span with `http.response.status_code` and `error.type`. |
| AC-4 | A WebSQL, OData and GraphQL query against a governed table produces, in one trace: server span, compile span, governance decision span(s), one `db` client span per source command (`db.system.name`, `db.namespace`, `db.operation.name`, `db.query.summary`, `autheris.datasource.name`), and governance store/Redis spans where used. |
| AC-5 Canary | Queries containing canary literals (in filters, parameters, GraphQL variables, OData `$filter`, request bodies, and values that trigger driver errors) on tables with row filters and masked/classified columns produce **zero** occurrences of any canary in all exported spans, events, logs and metrics (in-memory exporters), under default settings. |
| AC-6 | With `AllowStatementText=true` and a data source set to `Parameterized`, `db.query.text` contains placeholders and no canary value; with either switch off, no `db.query.text` is present. |
| AC-7 | Incoming `baggage` is not visible in `Activity.Baggage` nor forwarded on outbound calls; a `traceparent` with `sampled=1` from an untrusted caller does not change the sampling decision when `Sampling:Ratio=0`. Malformed `traceparent` starts a new trace. |
| AC-8 | Outbound HTTP to internal integrations carries `traceparent`; customer webhooks do not unless enabled for that subscription. |
| AC-9 | An async job, webhook delivery and CDC batch each produce a span with a link to the originating trace context. |
| AC-10 | No Autheris span, log or metric contains a user SID, UPN, session id or token; with pseudonymous mode on, `enduser.pseudo.id` is a stable HMAC that changes when the key rotates. |
| AC-11 | A test enumerates all Autheris instruments (via `MeterListener`) and asserts names follow D-5 and attribute keys are within the per-instrument allow-list; no instrument uses tenant, user, table or query hash as attribute unless `IncludeTenant` is on, and then the cardinality limit applies. |
| AC-12 | No `prometheus-net` instrumentation APIs remain in `src/` (architecture test); `/metrics` still serves the catalog behind `MetricsScrape`; `PrometheusRule` expressions match the new names (Helm template test). |
| AC-13 | `IDataSourceTelemetryPolicyProvider` global implementation returns defaults for any name; a fake per-source provider changes span detail, slow-query threshold and log level for that source only, and cannot exceed the ceiling. |
| AC-14 | Exporter outage (collector down) does not change request latency beyond noise in the benchmark smoke test and does not produce request failures. |
| AC-15 | Telemetry overhead with sampling ratio 0.1 stays within 3 % p50 / 5 % p99 latency and adds no allocations on the non-sampled hot path for the governed query benchmark (BenchmarkDotNet baseline vs. change). |
| AC-16 | Feature doc F-OPS-03, configuration guide, operations runbook and Helm values are updated; example collector configuration validates with `otelcol validate` in CI or a documented manual step. |

## 9. Test Strategy

- **Fast CI (unit):** `OpenTelemetry.Exporter.InMemory` for traces, metrics and logs inside a minimal host; `Microsoft.Extensions.Diagnostics.Testing` (`MetricCollector<T>`, `FakeLogger`) for component tests; `ActivityListener`-based assertions for spans; propagation tests with crafted `traceparent`/`baggage` headers via `WebApplicationFactory` in-memory server.
- **Leak tests (AC-5):** one shared canary harness that runs each query surface against SQLite/in-memory or the existing fast source fakes, collects all signals, and greps serialized exports for canaries; also includes exception paths (forced driver error with literal value).
- **Contract/arch tests:** attribute allow-list and naming via `MeterListener`; no prometheus-net instrumentation API; no `Activity.SetTag` with keys outside the telemetry key catalog in `src/` (ArchUnitNET/Roslyn rule); CA2254 as error.
- **Integration (not in the fast gate):** Testcontainers OpenTelemetry Collector with the `file` exporter verifying OTLP gRPC and HTTP end to end; database spans against real SQL Server/PostgreSQL containers (existing shared DB servers, WP-TS-01).
- **Helm:** `helm template` tests for OTLP values, Secret references and `PrometheusRule` names.
- **Performance:** benchmark in `benchmarks/Autheris.Benchmarks` with telemetry off / on ratio 0.1 / on ratio 1.0 (AC-15).

## 10. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Leak of values through a new or third-party span source (driver, HotChocolate, MCP SDK). | Sensitive data in an ungoverned store. | Only allow-listed sources are added; redaction processor drops unknown attribute keys from Autheris sources; canary tests per surface; driver instrumentation off by default. |
| Overhead on the hot path (spans per command, histograms). | Latency regressions (BL-03 scale). | Ratio sampling, `Activity` created only when sampled/listened, `[LoggerMessage]`, benchmark gate AC-15. |
| Metric renaming breaks dashboards/alerts. | Lost monitoring. | No production yet; rename once in I3 with `PrometheusRule` update and a mapping table in the feature doc. |
| Serilog removal changes log output format and configuration keys. | Operator confusion. | Breaking change accepted (no production); documented mapping `Serilog:*` → `Logging:*`; startup fails if a `Serilog` section is still present (like `LegacySwitchGuard`). |
| Trace id adoption in audit changes audit canonicalization input. | Audit chain verification of older entries. | Only new entries use the W3C id; canonicalization format unchanged (string field); no repair migration needed. |
| prometheus-net meter adapter and OTel views disagree (names, histogram buckets). | Different numbers on `/metrics` and OTLP. | Phase 2 verifies the adapter mapping; explicit bucket boundaries via views; long term switch to OTel Prometheus exporter when stable. |
| Tenant id on spans may itself be sensitive for some operators. | Disclosure to telemetry operators. | Switch `Gateway:Telemetry:IncludeTenant` (default true for spans/logs); operators are trusted cluster staff. |
| Collector tail sampling misconfigured. | Missing traces for errors. | Example config tested; errors and denials always kept by policy in the example. |
| BL-05 not ready when I5 lands. | Hook unused. | Global-only provider is complete and production-ready; BL-05 only swaps the implementation. |

## 11. Increments

| Inc. | Content | Done when |
|---|---|---|
| I1 Foundation | `Gateway:Telemetry` options + validation, resource, conditional OTLP exporters (gRPC/HTTP), ratio sampler with D-7, propagator config (D-8), registration of all sources/meters, ASP.NET Core + `HttpClient` instrumentation replacing `OpenTelemetryTracingMiddleware`, redaction processor and span-attribute catalog (SR-1, SR-6), W3C trace id as correlation id in problem details/audit/logs (D-10), in-memory test harness. | AC-1, AC-2, AC-3, AC-7, AC-10 |
| I2 Logs | Serilog removal, MEL + OTel logs + JSON console, DEP-8 redaction as processor, `[LoggerMessage]` for hot paths, CA2254, `LogGeneratedSql` folded into SR-2 global ceiling. | AC-1 (logs), AC-5 (logs part), AC-6 (logs part) |
| I3 Data path and metrics | Spans for compile, governance decisions, source commands (D-6, D-12), governance store, Redis, audit append, GraphQL/MCP (D-15); metric catalog 5.3, prometheus-net migration, renaming (D-5), exemplars, cardinality limits, `PrometheusRule` update. | AC-4, AC-5, AC-6, AC-11, AC-12, AC-15 |
| I4 Async propagation | Trace context persisted and linked for async jobs, webhook outbox, CDC, Redis event bus (D-14); outbound propagation policy per integration (D-9). | AC-8, AC-9 |
| I5 Per-source hook | `IDataSourceTelemetryPolicyProvider` + global implementation wired into the data path; slow-query threshold. | AC-13 |
| I6 Deployment and docs | Helm OTLP values (endpoint, protocol, Secret headers, TLS), example collector config (tail sampling, redaction, tenant routing), F-OPS-03, configuration guide, runbook, master plan. | AC-14, AC-16 |

I1 to I3 are sequential; I4 and I5 can run in parallel after I3; I6 closes the item. BL-03 can use I1 + I3 measurements as soon as I3 is merged.

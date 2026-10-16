# Implementation Plan: REQ-OPS-BL06 - Tracing, Metrics and Logging via OpenTelemetry

**Document ID:** `PLAN-OPS-BL06`
**Date:** 2026-10-16
**Status:** Phase 2 (architecture) and Phase 3 (security review) done; next: Phase 4 (TDD developers, Sonnet)
**Authors:** Solution Architect (`csharp-architect`), Security Expert (`csharp-security-expert`)
**Input PRD:** [`2026-10-16-req-bl-06-open-telemetry.md`](2026-10-16-req-bl-06-open-telemetry.md) (`REQ-OPS-BL06`)
**Branch:** `feat/bl-06-open-telemetry`
**Parent Plan:** [00-master-plan-overview.md](00-master-plan-overview.md), backlog item BL-06 in [`2026-10-16-backlog-intake.md`](2026-10-16-backlog-intake.md)
**Target feature doc (Phase 6):** `docs/features/f-ops-03-opentelemetry-observability.md`

---

## 0. Binding Rules for this Item

| Rule | Consequence in this plan |
|---|---|
| Fail closed for sensitive data | Unknown span attribute keys from Autheris sources are dropped; exception messages are redacted unless allow-listed; SQL text is scrubbed of literals or not emitted at all; canary tests (AC-5) are part of the fast gate. |
| Fast CI | All unit tests use in-memory exporters / `ActivityListener` / `MeterListener`; no `Task.Delay`, no sleeps, no real collector in the unit gate. Collector and database container tests run in the integration suite only. The overhead gate (AC-15) is a BenchmarkDotNet job outside the PR gate plus a deterministic zero-allocation unit test. |
| TDD developers are Sonnet | Work packages are sized for three Sonnet developers with disjoint file ownership (section 5). |
| No upgrade migrations | The one governance schema change (`WEBHOOK_OUTBOX.trace_parent`) extends the `0001` baseline in place on SQLite, PostgreSQL and SQL Server plus the three shape snapshots. |
| Do not touch `src/TrinoSqlEngine/Ast/Visitors/AstSecurityVisitor.cs` | No work package lists it. The compiler span lives in `GovernedSqlCompiler.cs`/`CompilerTelemetry.cs` only. |
| Minimize churn in shared DI/options files | All telemetry registration moves into one new file `GatewayTelemetryServiceExtensions.cs`; `Gateway:Telemetry` is bound on its own and is **not** added to `GatewayOptions`/`GatewaySections` (section 7 lists the remaining hotspots). |

## 1. Verification of the PRD Against the Code (`main` `272ef93c` + PRD commit)

| PRD statement | Verified result | Consequence |
|---|---|---|
| 2.1 Provider registers 2 sources / 2 meters, `AddOtlpExporter()` unconditional | Confirmed (`GatewayCoreServiceExtensions.cs:122-135`). | Block is removed and replaced by `AddGatewayTelemetry` (D-1). |
| 2.1 `Autheris.Mcp`, `TrinoSqlEngine.SqlCompiler`, `Autheris.SqlCompiler`, `Autheris.AsyncJobs` not registered | Confirmed (`McpDiagnostics.cs:14-19`, `CompilerTelemetry.cs:11-17`, `AsyncQueryJobManager.cs:40`). | Central source/meter registry with an architecture test (D-5). |
| 2.2 Ingress span carries `user.sid` | Confirmed, **and more**: `CasbinEnforcementService.Columns.cs:441-444` writes `user.sid` and `tenant.id` onto **`Activity.Current`** (whatever span is current, normally the server span) via `SetSafeTag("Governance.CasbinAbacEnforcement", ...)`; `ClassificationWorkflowStateMachine.cs:96-99` sets `proposal.id`, `table.id`, `reviewer.role` with raw `SetTag`; `LineageImpactAnalyzerService.cs:185` uses span name `Lineage.GetTableConsumers` that is not in the allow-list (tag silently dropped). | All existing tag call sites are migrated to the new catalog in WP-B2; `user.sid` is removed from the catalog entirely. |
| 2.3 Serilog | Confirmed; Serilog is used only in `Program.cs:43-47`, `Api/Logging/SensitiveLogPropertyEnricher.cs`, `Api.csproj:20`, `appsettings.json` and one test (`tests/Autheris.Tests.Unit/Security/LoggingRedactionDep8Tests.cs`). | Removal is small and local to the Api project (WP-A2). |
| 2.3 Exceptions logged with message | 962 `Log*` call sites in `src/`, 284 of them pass an exception; 0 use `$"..."` interpolation (CA2254 has no existing violations). | Exception redaction must be central (log processor), not per call site. CA2254 can be raised to error without code changes. |
| 2.3 `LogGeneratedSql` in three places | Confirmed (`GovernedSqlRewriter.cs:642`, `GovernedTreeQueryService.cs:410`, `SqlProcedureRowScopeResolver.cs:177`). | Folded into the statement-text policy (D-9). |
| 2.4 prometheus-net direct counters in 3 files | **Incomplete.** Eight files use `Prometheus.Metrics.CreateCounter`: `RateLimitingMiddleware.cs`, `BasicAuthenticationHandler.cs`, `ConsentCacheService.cs`, `RedisIdempotencyStore.cs`, `RedisRateLimiterService.cs`, `RedisTokenRevocationService.cs`, `RedisClusterStateProvider.cs` (4 counters), `GovernanceDbMssqlWatermarkStore.cs`. `Autheris.Infrastructure.csproj:17` references `prometheus-net`. | WP-C1 migrates all eight; the Infrastructure project drops the package (AC-12 architecture test). |
| 2.2 Audit uses `TraceIdentifier` | Partly. `AccessAuditMiddleware.cs:59` already uses `Activity.Current?.TraceId`; 46 other sites in `src/Autheris.Api` read `HttpContext.TraceIdentifier` (endpoint audit records, problem details fallback, MCP). `AuditContext.TraceId` defaults to `Guid.NewGuid().ToString("N")` (32 hex chars, format-compatible with a W3C trace id). | One early middleware sets `HttpContext.TraceIdentifier` to the W3C trace id, so all 46 sites become correct without edits (D-11). |
| 6.2 SR-2 "compiled, parameterized SQL (placeholders only)" | **False assumption.** Compiled SQL can contain inlined literals: masking replacements (`SqlDataMaskingProvider.cs:99`), the **tenant id** in structured virtual filters (`StructuredFilterSqlBuilder.cs:79`), virtual filter values (`StructuredFilterSqlBuilder.cs:278`), tree compiler literals (`TreeSqlCompiler.cs:450`). | Statement text and the query hash are produced from a **scrubbed** text (all literals replaced by `?`) by a fail-closed dialect lexer (D-9, SEC-OT06-04). |
| D-6 "one client span per source command" | Command execution is spread over executors (`GovernedSqlExecutor`, `GovernedTreeQueryService`, `SqlProcedureRowScopeResolver`, `MssqlProcedureInvoker`, `SqlVirtualFilterKeySetProvider`, `DuckDbOlapEngine`, `CheckedDmlExecutor`, `DatabricksDbCommand`, HTTP/Lakehouse executors). A `DbConnection`/`DbCommand` wrapper in `SqlConnectionFactory` is **not possible**: Oracle `BindByName` is enabled by reflection on the concrete command type (`OracleBindByName.cs:15`, `MssqlProcedureInvoker.cs:316`), which a wrapper would break (fail-closed `SecurityException`). | Explicit, allocation-free `SourceCommandTelemetry` scope at each execution site plus an architecture test that every `Execute*Async` call site in the data path is inside a scope (D-7). |
| D-6 governance store spans | Three repositories (`PostgreSqlGovernanceRepository`, `SqlServerGovernanceRepository`, `SqliteGovernanceRepository` via `SqliteGovernanceConnectionProvider`) open connections through one private `OpenConnectionAsync` each (~140 call sites). | Span per connection rental, started in the three `OpenConnectionAsync` methods with `[CallerMemberName]` and ended on connection dispose (D-8). |
| D-9 outbound propagation per integration | `SecureOutboundHttp.CreatePrimaryHandler` returns a `SocketsHttpHandler` per integration name. | Per-integration propagation via `SocketsHttpHandler.ActivityHeadersPropagator` (D-4). |
| D-14 webhook outbox carries trace context | `WEBHOOK_OUTBOX` has no trace column; `WebhookSubscription` (configured) is an options class; CloudEvent (CDC) subscriptions are a governance table that BL-01 is reworking. | Baseline extension: `WEBHOOK_OUTBOX.trace_parent` only. Per-subscription propagation flag for configured subscriptions (options); CloudEvent subscriptions never propagate in BL-06 (fail closed, flag added by BL-01 later). |
| 5.3 `autheris.datasource.name` bounded by configuration | Data sources can also be registered at runtime (`POST /api/v1/catalog/datasources`, `CatalogApiEndpoints.cs:66`). | The attribute is normalized and protected by a per-instrument cardinality limit (SEC-OT06-08). |
| `CA1848` (LoggerMessage) | Suppressed in `Directory.Build.props`. | Stays suppressed globally; `[LoggerMessage]` is required for the new hot-path log statements only (FR-6). |
| Lock files | `RestorePackagesWithLockFile` + locked mode in CI. | Every package change updates `packages.lock.json` of all affected projects (Api, Infrastructure, Tests.Unit, Tests.Architecture, Tests.Integration, Benchmarks). |

No PRD decision is reversed. Two are sharpened: D-12 (statement text is scrubbed, not assumed parameterized) and D-6 (explicit scopes instead of a driver or connection wrapper).

## 2. Architecture Decisions

### D-1 One telemetry composition root

- New file `src/Autheris.Api/Extensions/DependencyInjection/GatewayTelemetryServiceExtensions.cs` with `IServiceCollection AddGatewayTelemetry(IConfiguration, IHostEnvironment)` and `ILoggingBuilder AddGatewayLogging(IConfiguration, IHostEnvironment)`. `Program.cs` calls both (two lines replace the Serilog block); the old block in `GatewayCoreServiceExtensions.cs:121-135` is deleted.
- Options: `TelemetryOptions` (`Gateway:Telemetry`) in `src/Autheris.Domain/Options/Observability/TelemetryOptions.cs`, bound with `AddOptions<TelemetryOptions>().BindConfiguration(...)`, validated by `TelemetryOptionsValidator : IValidateOptions<TelemetryOptions>` and `ValidateOnStart()`. Not added to `GatewayOptions` (hotspot avoidance).
- The tracer, meter and logger providers are **always** registered (in-process), even with no endpoint: trace ids exist for correlation (D-11), in-memory exporters can be attached in tests, `/metrics` keeps working. `Telemetry:Enabled=false` removes all exporters and the stdout log exporter falls back to errors only; it never removes the redaction processors.

### D-2 Resource

`ResourceBuilder.CreateEmpty().AddService(serviceName: "autheris" (option ServiceName), serviceNamespace: option, serviceVersion: AssemblyInformationalVersion (commit suffix stripped), autoGenerateServiceInstanceId: true).AddAttributes([deployment.environment.name = option ?? IHostEnvironment.EnvironmentName]).AddEnvironmentVariableDetector()` so `OTEL_SERVICE_NAME`/`OTEL_RESOURCE_ATTRIBUTES` win (FR-2). Helm sets `OTEL_RESOURCE_ATTRIBUTES=service.instance.id=$(POD_UID),k8s.pod.uid=$(POD_UID)` from the downward API (HR-1). No host name, no user, no IP in the resource.

### D-3 Sampler

`AutherisSampler : Sampler` (Infrastructure):

| Parent | Decision |
|---|---|
| none (root) | `TraceIdRatioBasedSampler(Ratio)` |
| local parent (in-process) | follow the parent's `Recorded` flag (never break an in-process trace) |
| remote parent, `TrustParentSampling=false` (default) | ratio on the trace id; the parent `sampled` flag is ignored (D-7, SR-5) |
| remote parent, `TrustParentSampling=true` | follow the parent flag |

Default ratio 1.0 in Development, 0.1 otherwise. `OTEL_TRACES_SAMPLER`/`_ARG` are **not** honoured (they would bypass the trust rule; documented). Non-recorded children return `Drop`, so `StartActivity` returns `null` and the hot path allocates nothing (AC-15).

### D-4 Propagation and baggage

- OpenTelemetry API: `Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator())` (no `BaggagePropagator`).
- .NET runtime: `AutherisDistributedContextPropagator : DistributedContextPropagator` registered as the DI singleton `DistributedContextPropagator` (used by ASP.NET Core hosting) and assigned to `DistributedContextPropagator.Current` (used by `HttpClient`). Extract: `traceparent` via the W3C propagator after validation (length 55, lowercase hex, version `00`, non-zero ids; otherwise ignored), `tracestate` only if ≤ 512 bytes and ≤ 32 members; `ExtractBaggage` always returns `null`. Inject: `traceparent` only (no `tracestate`, no baggage).
- The OpenTelemetry ASP.NET Core instrumentation must not set `Baggage.Current` from the request; a test proves `Baggage.Current` and `Activity.Baggage` are empty (AC-7).
- Per integration (D-9 of the PRD): `SecureOutboundHttp.CreatePrimaryHandler` sets `handler.ActivityHeadersPropagator` from `OutboundPropagationPolicy.For(integrationName)`: internal integrations (catalog in `OutboundIntegrations.Internal`: OpenFGA, OPA, GraphQL subgraphs, lineage, ITSM) get the Autheris propagator; everything else (customer webhooks, CDC webhooks, lakehouse, object storage, unknown names) gets `DistributedContextPropagator.CreateNoOutputPropagator()` (fail closed). Webhook delivery writes `traceparent` explicitly per request when the configured subscription has `PropagateTraceContext=true` (default false).

### D-5 Source and meter registry, naming

`AutherisTelemetrySources` (Api, composition root) lists every source and meter; the provider registers exactly this list plus `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`, `System.Net.Http`, `System.Runtime` meters and the ASP.NET Core/HttpClient instrumentation.

| Kind | Name (after BL-06) | Owner file |
|---|---|---|
| ActivitySource + Meter | `Autheris.Core` (Meter renamed from `Autheris.Metrics`) | `Domain/Diagnostics/GatewayDiagnostics.cs` |
| ActivitySource + Meter | `Autheris.SqlCompiler` (ActivitySource renamed from `TrinoSqlEngine.SqlCompiler`) | `TrinoSqlEngine/CompilerTelemetry.cs` |
| ActivitySource + Meter | `Autheris.Mcp` | `Application/Mcp/Diagnostics/McpDiagnostics.cs` |
| Meter | `Autheris.AsyncJobs` | `Application/Jobs/Services/AsyncQueryJobManager.cs` (moved to a static `AsyncJobDiagnostics` class) |
| ActivitySource + Meter | `Autheris.Governance.Classification` | `Application/Governance/Diagnostics/ClassificationDiagnostics.cs` |

Static `ActivitySource`/`Meter` instances stay (no `IMeterFactory` churn through DI in 3 parallel branches); tests that assert on metrics run in one xUnit collection `TelemetryCollection` (no parallelism among themselves) and filter by unique attribute values. Architecture test: reflection over all `Autheris.*`/`TrinoSqlEngine` assemblies finds every static field of type `ActivitySource`/`Meter` and asserts its name is in the registry.

Naming (PRD D-5): `autheris.<area>.<name>`, lowercase, dots, UCUM unit in the `unit` argument, durations in seconds as `double`. Rename table for the existing `GatewayDiagnostics` instruments is fixed in contract C-5.

### D-6 Span attribute catalog and redaction processor (traces)

- `TelemetrySpanCatalog` (Domain/Diagnostics, frozen): span name → allowed attribute keys; also the global key list `TelemetryAttributes` (constants). Replaces `GatewayDiagnostics.SetSafeTag` and its allow-list. `user.sid`, `db.statement`, `table.id`, `proposal.id`, `root.node_id`, `latency.ms` are not in the catalog.
- `SpanRedactionProcessor : BaseProcessor<Activity>` (Infrastructure), registered **first** in the tracer pipeline, runs `OnEnd`:
  1. For activities from Autheris sources: drop every tag whose key is not allowed for that span name (unknown span name: only the global base set `autheris.tenant.id`, `error.type`, `autheris.correlation_id`).
  2. For all sources: remove `url.query`, strip the query from `url.full`, remove `http.request.header.*`/`http.response.header.*` except the allow-list (`content-type`), remove `db.statement`/`db.query.text` unless the span carries the internal marker set by `SourceCommandTelemetry` when the policy allowed text (marker removed before export).
  3. Exception events: `exception.message` replaced per D-10; `exception.stacktrace` kept; `exception.type` kept.
  4. Truncate every string value to `AttributeValueLengthLimit` (default 1024).
- Status descriptions are replaced by the stable error code or removed (status descriptions are free text).

### D-7 Data-path spans (`SourceCommandTelemetry`)

`src/Autheris.Application/Telemetry/SourceCommandTelemetry.cs`:

```csharp
public static class SourceCommandTelemetry
{
    // Returns default (no Activity) when not sampled; never throws.
    public static SourceCommandScope Start(in SourceCommandDescriptor descriptor);
}

public readonly record struct SourceCommandDescriptor(
    string DataSourceName,          // normalized via TelemetryNames.DataSource (charset, length 64)
    string DbSystemName,            // semconv values: mssql, postgresql, oracle, duckdb, sqlite, databricks, http
    string? Namespace,              // database name from options, never from the connection string
    string OperationName,           // SELECT | INSERT | UPDATE | DELETE | MERGE | EXECUTE | FETCH
    string? CollectionName,         // primary governed table (qualified name from metadata)
    string? CompiledSql,            // only read if the span is recorded; scrubbed before use
    DatabaseDialect? Dialect);

public struct SourceCommandScope : IDisposable
{
    public void SetRows(long rows);                  // only if policy.RecordRowCounts
    public void SetBytes(long bytes);
    public void Fail(Exception exception);           // error.type via TelemetryErrorType.From(ex)
    public void Dispose();                           // ends span, records db.client.operation.duration (always, sampled or not)
}
```

- Span: name `"{db.operation.name} {db.collection.name}"` (semconv), kind `Client`, source `Autheris.Core`. Attributes: `db.system.name`, `db.namespace`, `db.operation.name`, `db.collection.name`, `db.query.summary`, `autheris.datasource.name`, `autheris.db.query.hash`, `db.response.status_code` (driver error number only, never the message), `error.type`, optional `db.query.text` (D-9), `autheris.db.rows`.
- Metrics are recorded with a `TagList` (≤ 8 tags, stack only) whether or not the span is sampled.
- The policy (`IDataSourceTelemetryPolicyProvider`, D-13) is read once per scope; `SpanDetail.None` suppresses the span but keeps the metric; `Detailed` adds `autheris.db.first_row` and `autheris.db.stream_end` events (timestamps only).
- Call sites (WP-B3): `GovernedSqlExecutor`, `GovernedTreeQueryService`, `SqlProcedureRowScopeResolver`, `MssqlProcedureInvoker`, `SqlVirtualFilterKeySetProvider`, `DuckDbOlapEngine`, `CheckedDmlExecutor` (TrinoSqlEngine has no Application reference: it exposes an `Action`-free hook `ICheckedDmlTelemetry` set by the caller, or the scope is opened around the call in `GovernedSqlExecutor`; the developer picks the latter if the call is 1:1), `DeclarativeHttpDataSourceExecutor`, `PluginHttpDataSourceExecutor`, `DeltaLakeDataSourceExecutor`, `DatabricksDbCommand` (span opened by the caller, not inside the facade). `DbSessionContextInitializer` and session pinning statements are part of the rental, not separate spans.
- Further gateway-owned spans (names fixed in C-3): `autheris.sql.compile` (existing compiler span renamed), `autheris.governance.decide` with `autheris.decision.stage`/`autheris.decision.result`, `autheris.audit.append`, `autheris.cluster_state {operation}` (Redis lease/epoch/event bus), `autheris.governance_store {operation}`, `graphql.execute` (one per operation, D-15 of the PRD), MCP `gen_ai.tool {name}` without session id / principal.
- Driver-native instrumentation (Npgsql, SqlClient, ODP.NET, StackExchange.Redis) is **not** added and there is **no** switch in BL-06 (decision on the PRD's open point): its statement capture cannot be scoped to the policy of D-9. Revisit only if a driver offers parameter-free, summary-only spans.

### D-8 Governance store spans

`GovernanceStoreTelemetry.Track(DbConnection connection, string dbSystem, [CallerMemberName] string operation = "")` called in the three private `OpenConnectionAsync` methods (and `SqliteGovernanceConnectionProvider`). It starts a `Client` span `autheris.governance_store {operation}` (operation = repository method name, a bounded code identifier), **restores `Activity.Current` to the previous value immediately** (the span must not become the parent of unrelated caller work), and ends it from the connection's `Disposed` event. Attributes: `db.system.name`, `db.operation.name` (= method name), `autheris.store=governance`. No SQL text, no parameters. Migration runners are not instrumented (startup only).

### D-9 Statement text, query hash, `LogGeneratedSql`

- `SqlTextScrubber.TryScrub(string sql, DatabaseDialect dialect, out string scrubbed)` (Domain/Diagnostics): a dialect-aware lexer that replaces every string literal (`'...'` with `''` escapes, `N'...'`, PostgreSQL `E'...'`), numeric literal, and hex/binary literal with `?`, removes comments, and keeps identifiers (quoted identifiers `"..."`, `[...]`, `` `...` ``) and parameter markers (`@p0`, `:p0`, `$1`). Returns `false` (fail closed: no text, no hash) on any construct it does not fully understand: PostgreSQL dollar quoting, Oracle `q'...'`, unterminated quotes or comments, input longer than 64 KiB.
- `autheris.db.query.hash` = lowercase hex of the first 8 bytes of SHA-256 over the **scrubbed** text, computed only when the span is recorded. Literals (including tenant ids) therefore never influence the hash, and the hash is stable across tenants and replicas.
- `db.query.text` = scrubbed text, only when `Redaction:AllowStatementText=true` **and** the effective policy says `StatementText=Parameterized` **and** scrubbing succeeded (SR-2, AC-6).
- `db.query.summary` = `"{operation} {collection}"` built from metadata, never from SQL text.
- `Gateway:...:Logging:LogGeneratedSql` is removed: the three call sites log the scrubbed text through the same policy (`Debug`, category `Autheris.DataSource.<name>`). `LegacySwitchGuard.Replacements` gets the entry pointing to `Telemetry:Redaction:AllowStatementText` (startup fails if the old key is `true`).

### D-10 Exception redaction (spans and logs)

`TelemetryExceptionPolicy` (Domain/Diagnostics): `string ErrorType(Exception)` = stable Autheris error code if the exception exposes one (`SqlCompilerErrorMapper`, `SqlSourceUnavailableException.ErrorCode`, `IHasErrorCode`), else the exception type's full name. `string? SafeMessage(Exception)` = the message only for types in the frozen allow-list `ValueFreeExceptionTypes` (Autheris types whose messages are constant strings, verified by a test that constructs them), otherwise `"[redacted]"`. `DbException`, `JsonException`, `FormatException`, `InvalidCastException`, `ArgumentException` and all unknown types are redacted. Inner exceptions are walked with the same rule (depth ≤ 5).

### D-11 Correlation id = W3C trace id

- `TraceIdentifierMiddleware` (Api) is registered **first** (before `UseExceptionHandler`): `context.TraceIdentifier = Activity.Current?.TraceId.ToHexString()` when an activity exists (always, D-1). All 46 `TraceIdentifier` reads, problem details and audit records thus carry the W3C trace id with no further edits.
- `AuditContext.TraceId` default becomes `Activity.Current?.TraceId.ToHexString() ?? ActivityTraceId.CreateRandom().ToHexString()` (same 32-hex format; canonicalization unchanged; no migration).
- `CorrelationIdMiddleware` keeps its validation; the value is set as span attribute `autheris.correlation_id` and log scope attribute and is echoed. It never replaces the trace id.
- The audit record additionally stores `trace_remote_parent` (bool) in `details_json` (no schema change) so that investigators know whether the trace id was chosen by the caller (SEC-OT06-10).

### D-12 Logging provider swap

- Remove `Serilog.AspNetCore`, `UseSerilog`, `SensitiveLogPropertyEnricher`. `builder.Logging.ClearProviders()`; only the OpenTelemetry logging provider is added (`IncludeScopes=true`, `ParseStateValues=true`, `IncludeFormattedMessage=false`).
- **One pipeline, one redaction point:** `LogRedactionProcessor : BaseProcessor<LogRecord>` → `BatchLogRecordExportProcessor(JsonStdoutLogExporter)` and, when configured, `BatchLogRecordExportProcessor(OtlpLogExporter)`. The JSON stdout exporter (Infrastructure, `Utf8JsonWriter`, default escaping: control characters escaped) writes the OpenTelemetry log data model fields (`timestamp`, `severity_text`, `severity_number`, `body`, `attributes`, `trace_id`, `span_id`, `resource.service.name`, `resource.service.instance.id`). There is no separate console provider that could bypass redaction.
- `body` is the **message template** (`{OriginalFormat}`), not the formatted message, so values appear only as attributes, which the processor governs.
- `LogRedactionProcessor`: (1) attribute keys matching the sensitive-key catalog (`QueryString`, `Uri`, `Url`, `RequestUri`, `Path` → query removed; keys containing `sql`, `query`, `filter`, `body`, `payload`, `token`, `password`, `secret`, `authorization`, `cookie`, `header`, `variables`, `document`, `literal`, `value`, `parameter`, `sid`, `upn`, `email` (case-insensitive) → `[redacted]`, except the explicit allow-list `autheris.*`, `ErrorCode`, `TraceId`, `DataSource`, `Table`); (2) `LogRecord.Exception` replaced by `RedactedException` (type name, D-10 message, original stack trace via `StackTrace` override); (3) string values truncated (1024) ; (4) adds `autheris.tenant.id` from `TelemetryTenantContext`.
- `Logging:LogLevel` is effective again; defaults: `Default=Information`, `Microsoft.AspNetCore=Warning`, `System.Net.Http.HttpClient=Warning`. A remaining `Serilog` configuration section fails startup with a mapping message (`LegacySwitchGuard`-style `SerilogSectionGuard`).
- Bootstrap: exceptions before the host is built are written by `Program.cs` as a single JSON line with type and `[redacted]` message (no Serilog bootstrap logger).

### D-13 Per-data-source telemetry policy (I5, contract for BL-05)

Contract exactly as PRD section 7 (C-6). `GlobalDataSourceTelemetryPolicyProvider` (Application) builds one frozen `DataSourceTelemetryPolicy` from `Telemetry:DataSourceDefaults` clamped by `Telemetry:Redaction` (ceiling) on every `IOptionsMonitor` change and returns it for any name (O(1), no allocation). `DataSourceTelemetryPolicy.ClampTo(TelemetryCeiling)` is a pure function owned by BL-06 and reused by BL-05. Per-source log level: `DataSourceLoggers.Get(name)` returns a cached `ILogger` for category `Autheris.DataSource.<normalized name>`; data-path log statements check `policy.MinimumLogLevel` before logging. Slow-query threshold: span event `autheris.db.slow_query`, `Warning` log (`[LoggerMessage]`, no SQL), counter `autheris.query.slow`.

### D-14 Metrics export and catalog

- Instrumentation only via `System.Diagnostics.Metrics`. OTLP via `PeriodicExportingMetricReader` (60 s, delta temporality off: cumulative default; `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE` honoured).
- `/metrics`: prometheus-net stays in `Autheris.Api` for exposition only. `Metrics.ConfigureMeterAdapter(o => { o.InstrumentFilterPredicate = AutherisTelemetrySources.IsExposedMeter; o.ResolveHistogramBuckets = TelemetryViews.BucketsFor; })` and `Metrics.SuppressDefaultMetrics(...)` for event-counter duplicates; `UseHttpMetrics()` is removed. WP-C1 records the actual exposed names in a golden test (`/metrics` output names) because the adapter maps dots to underscores and may append unit suffixes; `PrometheusRule` uses the golden names.
- Views: explicit second-based buckets `[0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30]` for all duration histograms; `CardinalityLimit` per instrument (default 2000; 500 for instruments with `autheris.tenant.id`; 200 for `autheris.datasource.name` instruments); exemplars `ExemplarFilterType.TraceBased`.
- Cluster-scope gauges (`autheris.asyncjobs.queue.depth` cluster part, `autheris.webhook.outbox.pending`, `autheris.cdc.lag`, policy epoch) are observed only while the instance holds the `ClusterScheduledService` lease for that job; otherwise the callback returns no measurement (HR-2).
- Catalog: PRD section 5.3 plus the renamed existing instruments (C-5).

### D-15 Tenant and identity in telemetry

- `TelemetryTenantContext` (Domain/Diagnostics): `AsyncLocal<string?>` with `IDisposable Enter(TenantId tenant)` (restores the previous value). Set by `SecurityContextResolutionMiddleware` from the **resolved, authenticated** tenant, which also tags the server span via `IHttpActivityFeature`. Background loops call `Enter` per work item (async job owner tenant, outbox row owner tenant, `cluster` for cluster jobs) and dispose after the item.
- `TenantEnrichmentProcessor : BaseProcessor<Activity>` (`OnStart`) and the log processor read it. `Telemetry:IncludeTenant` (default true) controls spans/logs; `Metrics:IncludeTenant` (default false) controls the two histograms of PRD 5.3.
- Pseudonymous user id: `Telemetry:Identity:Mode = None | Pseudonymous` (default `None`); `Pseudonymous` requires `Identity:PseudonymKeyRef` (Key Vault reference resolved by `IKeyVaultSecretProvider`, ≥ 32 bytes, else startup fails). `enduser.pseudo.id = "{keyId}:" + hex(HMAC-SHA256(key, canonical SID))[0..32]`. Never on metrics.

### D-16 Async propagation and span links (I4)

| Producer | Stored context | Consumer span |
|---|---|---|
| Async job submit (`AsyncQueryJobManager`) | `ActivityContext? Origin` field on `AsyncJobExecutionItem` (in-process channel; job runs on the accepting node) | `autheris.asyncjob.execute` root span (new trace) with link to `Origin`, kind `Consumer` |
| Webhook dispatcher (`WebhookDispatcherService` → `IWebhookOutbox.EnqueueAsync`) | new column `WEBHOOK_OUTBOX.trace_parent` (`traceparent` string, ≤ 55 chars, validated on read) and `WebhookOutboxMessage.TraceParent` | `autheris.webhook.deliver` root span per delivery attempt with link; kind `Producer` for the HTTP send |
| Redis event bus (`RedisEventBus.PublishAsync`) | envelope `{"tp":"<traceparent>","m":<payload>}`; reader accepts legacy raw payloads (rolling upgrade) | `autheris.event.process {message type}` span with link |
| CDC (`MssqlChangeTrackingPoller`, `RedisCdcEventChannel`) | one root span `autheris.cdc.poll` per poll cycle; its context travels in `RedisCdcEnvelope.TraceParent` and into the outbox row of the resulting webhook | consumer spans with link to the poll-cycle span |

Links carry no attributes except `autheris.link.kind` (`origin`). A malformed stored `traceparent` is ignored (no link), never fails the work item.

### D-17 Exporter activation and channel security

- An OTLP exporter for a signal is added only when an endpoint resolves: `OTEL_EXPORTER_OTLP_<SIGNAL>_ENDPOINT` → `OTEL_EXPORTER_OTLP_ENDPOINT` → `Telemetry:Otlp:Endpoint`; and the signal flag (`Otlp:Traces|Metrics|Logs`, default true) is on. Otherwise no exporter object exists (AC-1).
- Every exporter gets `HttpClientFactory = OtlpHttpClientFactory.Create(options)`: `SocketsHttpHandler` with `AllowAutoRedirect=false`, `UseProxy` only if `Otlp:UseSystemProxy`, TLS ≥ 1.2, optional client certificate (mTLS) loaded from `Otlp:ClientCertificatePath`/`ClientKeyPath` (mounted Secret files) or the standard `OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE`/`_CLIENT_KEY`, optional CA bundle `OTEL_EXPORTER_OTLP_CERTIFICATE`.
- Headers (API keys) only from `OTEL_EXPORTER_OTLP_HEADERS` (Helm: `valueFrom.secretKeyRef`). There is **no** `Telemetry:Otlp:Headers` key; the validator fails startup if one is present.
- Endpoint validation: absolute `http`/`https` URI, no user info, no query, no fragment; `http` only with `Otlp:AllowInsecureTransport=true` (startup warning) or in Development.
- Batch processors: bounded queue (2048 spans / 2048 log records), export timeout `Otlp:Timeout` (default 10 s), drop on overflow; dropped counts are exposed through `autheris.telemetry.dropped` (`signal` attribute) from the SDK's internal counters (WP-A1 picks the SDK hook available in the pinned version; fallback: a wrapping processor that counts `OnEnd` when the queue is full).

### D-18 Ingress and egress HTTP

- `AddAspNetCoreInstrumentation(o => { o.Filter = ctx => !IsExcluded(ctx.Request.Path); o.RecordException = false; o.EnrichWithHttpResponse = SetRejectionErrorType; })`. Excluded: `/health/*`, `/metrics`. `SetRejectionErrorType` sets `error.type` to the status code string for 401, 403 and 429 (AC-3). `OpenTelemetryTracingMiddleware` is deleted.
- `AddHttpClientInstrumentation(o => { o.RecordException = false; o.FilterHttpRequestMessage = r => r.RequestUri is not null; })`; `url.full` query stripped by D-6.
- Exceptions on the server span are recorded by `GatewayExceptionHandler` through `TelemetryExceptionPolicy` (type + code + stack, redacted message).

## 3. Fixed Contracts (created first, in WP-0)

WP-0 is a single commit by developer B before the parallel work starts (contract-only, compiles, no behaviour). Developers A and C rebase on it.

| ID | Contract | File (new unless stated) |
|---|---|---|
| C-1 | `TelemetryOptions` (+ nested `OtlpOptions`, `SamplingOptions`, `PropagationOptions`, `RedactionOptions`, `MetricsOptions`, `IdentityOptions`, `DataSourceTelemetryDefaults`), section `Gateway:Telemetry`; enums `OtlpProtocol { Grpc, HttpProtobuf }`, `TelemetryIdentityMode { None, Pseudonymous }` | `src/Autheris.Domain/Options/Observability/TelemetryOptions.cs` |
| C-2 | `TelemetryAttributes` (all attribute key constants), `TelemetrySpanNames`, `TelemetrySpanCatalog.IsAllowed(spanName, key)` | `src/Autheris.Domain/Diagnostics/TelemetryAttributes.cs`, `TelemetrySpanCatalog.cs` |
| C-3 | Span names: `autheris.sql.compile`, `autheris.governance.decide`, `autheris.audit.append`, `autheris.governance_store {op}`, `autheris.cluster_state {op}`, `{db.operation.name} {db.collection.name}`, `graphql.execute`, `gen_ai.tool {name}`, `autheris.asyncjob.execute`, `autheris.webhook.deliver`, `autheris.event.process {type}`, `autheris.cdc.poll` | in `TelemetrySpanNames` |
| C-4 | `TelemetryExceptionPolicy` (`ErrorType`, `SafeMessage`), `SqlTextScrubber.TryScrub`, `TelemetryNames.DataSource(string)` (normalize: `[A-Za-z0-9_.-]`, max 64, else `invalid`), `TelemetryTenantContext` | `src/Autheris.Domain/Diagnostics/*.cs` (WP-0 ships the real implementations of these small pure types with their unit tests, not stubs, so A and C can rely on them) |
| C-5 | Metric rename table (old → new): `autheris_forbidden_requests_total` → `autheris.http.forbidden`; `autheris_event_bus_publish_failed_total` → `autheris.eventbus.publish.failures`; `autheris_event_bus_handler_failed_total` → `autheris.eventbus.handler.failures`; `autheris_policy_epoch_propagation_failed_total` → `autheris.policy.epoch.propagation.failures`; `autheris_unrouted_requests_total` → `autheris.http.unrouted`; `autheris_query_too_complex_total` → `autheris.query.too_complex`; `autheris_cross_tenant_mismatch_total` → `autheris.tenant.mismatch`; `authz_dependency_failure_total` → `autheris.authz.dependency.failures`; `autheris_audit_write_failed_total` → `autheris.audit.write.failures`; `autheris_audit_dead_letter_total` → `autheris.audit.dead_letter`; `autheris_dml_audit_failed_total` → `autheris.audit.dml.failures`; `autheris_eventbus_dropped_total` + `autheris_event_bus_dropped_total` → `autheris.eventbus.dropped` (`autheris.eventbus.kind` = `internal`/`gateway`); `autheris_event_bus_handler_timeout_total` → `autheris.eventbus.handler.timeouts`; `autheris_policy_evaluation_duration_ms` → `autheris.governance.decision.duration` (s, stage `abac`); `autheris_lineage_traversal_duration_ms` → `autheris.lineage.traversal.duration` (s); prometheus-net counters: `autheris_ratelimit_rejected_total` → `autheris.ratelimit.rejections`, basic-auth hash rejections → `autheris.auth.password_hash.rejections`, consent cache hits/misses → `autheris.cache.requests` (`autheris.cache.name=consent`, `autheris.cache.result`), Redis idempotency/rate limiter/revocation/cluster state errors → `autheris.redis.failures` (`autheris.redis.component`, `autheris.redis.operation` from fixed enums), lease lost/renew failures → `autheris.cluster.lease.events` (`reason`), watermark fenced → `autheris.cdc.watermark.fenced`. Per-instrument attribute allow-list: `TelemetryMetricCatalog` | `src/Autheris.Domain/Diagnostics/TelemetryMetricCatalog.cs` |
| C-6 | `IDataSourceTelemetryPolicyProvider`, `DataSourceTelemetryPolicy` (+ `ClampTo`), `SpanDetail { None, Basic, Detailed }`, `StatementTextMode { None, Summary, Parameterized }`, `TelemetryCeiling` | `src/Autheris.Application/Telemetry/IDataSourceTelemetryPolicyProvider.cs` (enums in `src/Autheris.Domain/Diagnostics/TelemetryPolicyEnums.cs` because options use them) |
| C-7 | `SourceCommandTelemetry`, `SourceCommandDescriptor`, `SourceCommandScope` signatures (D-7) | `src/Autheris.Application/Telemetry/SourceCommandTelemetry.cs` (WP-0: signatures with a no-op body that only disposes; WP-B3 fills it) |
| C-8 | Governance schema: `WEBHOOK_OUTBOX.trace_parent` `TEXT NULL` (SQLite) / `VARCHAR(55) NULL` (PostgreSQL) / `NVARCHAR(55) NULL` (SQL Server), appended as the last column; `WebhookOutboxMessage.TraceParent` (`string?`, last positional parameter with default `null`) | baseline files + shape JSON (WP-C3) |
| C-9 | `AsyncJobExecutionItem` gets `ActivityContext Origin` (last parameter, default `default`) | WP-C3 |

## 4. Files per Layer

| Layer | New | Changed |
|---|---|---|
| Domain | `Options/Observability/TelemetryOptions.cs`, `Diagnostics/TelemetryAttributes.cs`, `TelemetrySpanCatalog.cs`, `TelemetryMetricCatalog.cs`, `TelemetryExceptionPolicy.cs`, `SqlTextScrubber.cs`, `TelemetryNames.cs`, `TelemetryTenantContext.cs`, `TelemetryPolicyEnums.cs` | `Diagnostics/GatewayDiagnostics.cs` (rename instruments, delete `SetSafeTag`), `Audit/AuditContext.cs` (default trace id), `Model/WebhookOutboxMessage.cs` (C-8), `Options/WebhooksOptions.cs` (`PropagateTraceContext`), `Options/Security/AuditLoggingOptions.cs` (remove `LogGeneratedSql`) |
| Application | `Telemetry/IDataSourceTelemetryPolicyProvider.cs`, `GlobalDataSourceTelemetryPolicyProvider.cs`, `SourceCommandTelemetry.cs`, `DataSourceLoggers.cs`, `GovernanceDecisionTelemetry.cs`, `OutboundPropagationPolicy.cs`, `Jobs/Diagnostics/AsyncJobDiagnostics.cs` | executors listed in D-7, `CasbinEnforcementService.Columns.cs`, `ClassificationWorkflowStateMachine.cs`, `LineageImpactAnalyzerService.cs`, `AiDataGuardrailService.cs`, `McpDiagnostics.cs`, `GovernedSqlRewriter.cs`, `GovernedTreeQueryService.cs`, `SqlProcedureRowScopeResolver.cs`, `Security/SecureOutboundHttp.cs`, `Webhooks/WebhookDispatcherService.cs`, `WebhookDeliveryHostedService.cs`, `WebhookDeliveryClient.cs`, `Jobs/Services/AsyncQueryJobManager.cs`, `AsyncQueryJobBackgroundWorker.cs`, `AsyncJobExecutionItem.cs` |
| TrinoSqlEngine | - | `CompilerTelemetry.cs` (source name), `GovernedSqlCompiler.cs` (span name/attributes) |
| Infrastructure | `Telemetry/AutherisSampler.cs`, `AutherisDistributedContextPropagator.cs`, `SpanRedactionProcessor.cs`, `TenantEnrichmentProcessor.cs`, `LogRedactionProcessor.cs`, `RedactedException.cs`, `JsonStdoutLogExporter.cs`, `OtlpHttpClientFactory.cs`, `GovernanceStoreTelemetry.cs`, `TelemetryViews.cs` | 8 prometheus-net files (C-5), 3 governance repositories + `SqliteGovernanceConnectionProvider.cs`, `*GovernanceRepository.WebhookOutbox.cs` (3), 3 baseline schemas + 3 shape JSON, `Messaging/RedisEventBus.cs`, `Streaming/RedisCdcEventChannel.cs`, `Persistence/AuditChainEngine.cs` caller (append span), `Autheris.Infrastructure.csproj` (drop prometheus-net, add `OpenTelemetry` SDK if not transitive) |
| Extensions | - | `Cdc/MssqlChangeTrackingPoller.cs` (poll-cycle span, source command scope) |
| Api | `Extensions/DependencyInjection/GatewayTelemetryServiceExtensions.cs`, `Telemetry/AutherisTelemetrySources.cs`, `Middleware/TraceIdentifierMiddleware.cs`, `Configuration/SerilogSectionGuard.cs`, `Telemetry/GraphQlTelemetryListener.cs` | `Program.cs`, `GatewayCoreServiceExtensions.cs` (delete OTel block), `GatewayApplicationBuilderExtensions.cs` (middleware order, remove `UseHttpMetrics`, meter adapter), `SecurityContextResolutionMiddleware.cs` (tenant tag), `CorrelationIdMiddleware.cs` (span attribute), `GatewayExceptionHandler.cs`, `Middleware/RateLimitingMiddleware.cs`, `Security/BasicAuthenticationHandler.cs`, `Configuration/LegacySwitchGuard.cs`, `appsettings*.json`, `Autheris.Api.csproj`; deleted: `Middleware/OpenTelemetryTracingMiddleware.cs`, `Logging/SensitiveLogPropertyEnricher.cs` |
| Deploy | `deploy/otel-collector/collector-example.yaml` | `deploy/helm/autheris/values.yaml`, `templates/deployment.yaml`, `templates/prometheusrule.yaml`, `templates/configmap.yaml`, `deploy/podman-compose.yaml` |
| Tests | `tests/Autheris.Tests.Unit/Telemetry/*` (harness + tests), `tests/Autheris.Tests.Architecture/TelemetryArchitectureTests.cs`, `tests/Autheris.Tests.Integration/Telemetry/OtlpCollectorTests.cs`, `benchmarks/Autheris.Benchmarks/TelemetryOverheadBenchmark.cs` | `tests/Autheris.Tests.Unit/Security/LoggingRedactionDep8Tests.cs` (rewritten against the new processor) |

Packages (pin exact, latest stable of the 1.19.x line or the matching instrumentation release, at least two weeks old; update lock files): `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime` (Api); `OpenTelemetry.Exporter.InMemory`, `Microsoft.Extensions.Diagnostics.Testing` (Tests.Unit); `Testcontainers` collector image `otel/opentelemetry-collector-contrib` pinned by digest (Tests.Integration). Removed: `Serilog.AspNetCore` (Api), `prometheus-net` (Infrastructure). `prometheus-net.AspNetCore` stays in Api.

## 5. Work Packages, Ownership and Sequencing (3 Sonnet developers)

File ownership is exclusive; a developer who needs a change in a file owned by another asks in the stream channel and the owner makes it.

| WP | Owner | Content | Files (exclusive) | Depends on | ACs |
|---|---|---|---|---|---|
| **WP-0** Contracts | Dev B | C-1 to C-7 incl. real implementations + tests of `SqlTextScrubber`, `TelemetryExceptionPolicy`, `TelemetryNames`, `TelemetryTenantContext`, `TelemetrySpanCatalog`, `TelemetryMetricCatalog`; options validator | Domain `Diagnostics/Telemetry*`, `SqlTextScrubber.cs`, `Options/Observability/*`, Application `Telemetry/IDataSourceTelemetryPolicyProvider.cs`, `SourceCommandTelemetry.cs` (stub) | - | AC-6 (scrubber part) |
| **WP-A1** Composition root, ingress/egress, correlation | Dev A | D-1 to D-4, D-17, D-18, D-11; sampler, propagator, processors for spans (`SpanRedactionProcessor`, `TenantEnrichmentProcessor`), `OtlpHttpClientFactory`, registry + architecture test, delete `OpenTelemetryTracingMiddleware`, `TraceIdentifierMiddleware`, tenant tag in `SecurityContextResolutionMiddleware`, `SecureOutboundHttp` propagator per integration, `AuditContext` default, test harness `TelemetryTestHost` (in-memory exporters for all signals) | Api composition root files, `GatewayApplicationBuilderExtensions.cs`, `Program.cs`, Infrastructure `Telemetry/AutherisSampler.cs`, `AutherisDistributedContextPropagator.cs`, `SpanRedactionProcessor.cs`, `TenantEnrichmentProcessor.cs`, `OtlpHttpClientFactory.cs`, Application `Security/SecureOutboundHttp.cs`, `Telemetry/OutboundPropagationPolicy.cs`, Domain `Audit/AuditContext.cs`, `tests/.../Telemetry/Harness/*` | WP-0 | AC-1 (traces/metrics), AC-2, AC-3, AC-7, AC-8 (internal part) |
| **WP-A2** Logs | Dev A | D-12, D-9 log part (`LegacySwitchGuard`, remove `LogGeneratedSql` option), `JsonStdoutLogExporter`, `LogRedactionProcessor`, `RedactedException`, `SerilogSectionGuard`, CA2254 as error (`.editorconfig` `dotnet_diagnostic.CA2254.severity = error` for `src/`), rewrite DEP-8 test | Api `Logging/*`, `Configuration/SerilogSectionGuard.cs`, `LegacySwitchGuard.cs`, `appsettings*.json`, `Autheris.Api.csproj`, Infrastructure `Telemetry/LogRedactionProcessor.cs`, `JsonStdoutLogExporter.cs`, `RedactedException.cs`, Domain `AuditLoggingOptions.cs`, `.editorconfig` | WP-A1 | AC-1 (logs), AC-5 (logs), AC-6 (logs), AC-10 (logs) |
| **WP-A3** Deployment | Dev A | Helm values (endpoint, protocol, Secret headers, TLS files, downward API `POD_UID`), example collector config (agent + gateway, tail sampling, `redaction` processor, tenant routing via `routing` connector, `loadbalancing` exporter), `otelcol validate` CI step or documented manual step, podman-compose collector profile | `deploy/**` except `prometheusrule.yaml` | WP-A2, WP-C1 names | AC-14 (doc part), AC-16 (Helm/collector part) |
| **WP-B1** Per-source hook | Dev B | D-13: `GlobalDataSourceTelemetryPolicyProvider`, `ClampTo`, `DataSourceLoggers`, DI registration (one line in `GatewayTelemetryServiceExtensions` via Dev A: Dev B provides `services.AddDataSourceTelemetryPolicy()` extension in Application) | Application `Telemetry/GlobalDataSourceTelemetryPolicyProvider.cs`, `DataSourceLoggers.cs`, `TelemetryServiceCollectionExtensions.cs` | WP-0 | AC-13 |
| **WP-B2** Existing spans cleanup | Dev B | Migrate all `SetSafeTag`/`SetTag` call sites (Casbin, classification workflow, lineage, MCP guardrail, compiler) to the catalog; MCP drops session id and principal; compiler span rename | `CasbinEnforcementService.Columns.cs`, `ClassificationWorkflowStateMachine.cs`, `LineageImpactAnalyzerService.cs`, `AiDataGuardrailService.cs`, `McpDiagnostics.cs`, `CompilerTelemetry.cs`, `GovernedSqlCompiler.cs` | WP-0 | AC-10 (spans) |
| **WP-B3** Data-path spans | Dev B | D-7, D-8, D-9 span part, `GovernanceDecisionTelemetry`, audit append span, GraphQL listener (`GraphQlTelemetryListener` registered by Dev B through an `AddGraphQlTelemetry` extension called from Dev A's root), `LogGeneratedSql` call sites switched to the policy | executors of D-7, `GovernedSqlRewriter.cs`, `GovernedTreeQueryService.cs`, `SqlProcedureRowScopeResolver.cs`, Infrastructure `Telemetry/GovernanceStoreTelemetry.cs`, the 3 repositories' `OpenConnectionAsync` (main partial files only), `SqliteGovernanceConnectionProvider.cs`, `AuditChainEngine` caller, Api `Telemetry/GraphQlTelemetryListener.cs` | WP-B1, WP-A1 harness | AC-4, AC-5 (spans), AC-6, AC-15 |
| **WP-C1** Metrics | Dev C | D-14, C-5: rename `GatewayDiagnostics` instruments and delete `SetSafeTag` (after WP-B2 merged), migrate 8 prometheus-net files, new catalog instruments (request duration per surface, decision duration, compile duration, rows/bytes, cache requests, audit append, pool gauges where the driver exposes them, cluster-scope gauges with lease rule), views/buckets/cardinality/exemplars (`TelemetryViews`), meter adapter config, golden `/metrics` names, `PrometheusRule` + Helm template test, `AsyncJobDiagnostics` | `GatewayDiagnostics.cs`, 8 prometheus-net files, Infrastructure `Telemetry/TelemetryViews.cs`, `Autheris.Infrastructure.csproj`, Application `Jobs/Diagnostics/AsyncJobDiagnostics.cs`, `deploy/helm/autheris/templates/prometheusrule.yaml` | WP-0 (WP-B2 for the `SetSafeTag` deletion step only) | AC-11, AC-12, AC-15 (metrics part) |
| **WP-C2** Exporter resilience | Dev C | dropped-telemetry counter, blocked-exporter test (AC-14), overhead benchmark `TelemetryOverheadBenchmark` (off / 0.1 / 1.0) | `benchmarks/Autheris.Benchmarks/TelemetryOverheadBenchmark.cs`, `tests/.../Telemetry/ExporterResilienceTests.cs` | WP-A1 | AC-14, AC-15 |
| **WP-C3** Async propagation | Dev C | D-16: job item origin + worker span + tenant scope per item; webhook outbox column (3 baselines + 3 shapes + 3 repository partials `*.WebhookOutbox.cs`), dispatcher stores `traceparent`, delivery span with link, `PropagateTraceContext` for configured subscriptions; Redis event bus envelope; CDC poll-cycle span and `RedisCdcEnvelope.TraceParent` | `Jobs/Services/AsyncQueryJobManager.cs`, `AsyncQueryJobBackgroundWorker.cs`, `AsyncJobExecutionItem.cs`, `Webhooks/*`, `WebhookOutboxMessage.cs`, `WebhooksOptions.cs`, baselines + shapes, `*GovernanceRepository.WebhookOutbox.cs`, `RedisEventBus.cs`, `RedisCdcEventChannel.cs`, `MssqlChangeTrackingPoller.cs` | WP-A1; CDC part after BL-01 is merged to `main` (rebase), else deferred to a follow-up commit | AC-8 (webhooks), AC-9 |
| **WP-INT** Canary + end-to-end | Dev B (lead), all | Shared canary harness (AC-5) over WebSQL, OData, GraphQL (variables), REST body, MCP, async job, webhook; driver-error canary; integration tests with the collector container (gRPC + HTTP/protobuf) | `tests/.../Telemetry/Canary/*`, `tests/Autheris.Tests.Integration/Telemetry/*` | WP-A2, WP-B3, WP-C1, WP-C3 | AC-1 (e2e), AC-5, AC-10 |

Sequencing (maps to PRD increments; I1-I3 stay logically sequential but run as three streams):

```
Day 0      WP-0 (B) ─────────────┐
Stream A   WP-A1 ──► WP-A2 ──────────────► WP-A3
Stream B   WP-B1 ─► WP-B2 ─► WP-B3 ────────► WP-INT
Stream C   WP-C1 ───────────► WP-C2 ─► WP-C3 ─► (WP-INT)
```

Phase 6 (documentation expert) after WP-INT: `docs/features/f-ops-03-opentelemetry-observability.md` (with metric rename mapping and `Serilog:*` → `Logging:*` mapping), `docs/features/README.md`, configuration guide, operations runbook, F-OPS-02 Helm section, F-DATA-07 instrument names.

## 6. Test Strategy (AC mapping)

All unit tests: `tests/Autheris.Tests.Unit/Telemetry/`, xUnit collection `TelemetryCollection` for tests that use process-global listeners; no sleeps; deterministic `TimeProvider` (`FakeTimeProvider`) for slow-query thresholds; `ForceFlush()` on providers instead of waiting.

| AC | Test (type) | Approach |
|---|---|---|
| AC-1 | `TelemetryActivationTests` (unit), `OtlpCollectorTests` (integration) | Unit: build the host with no endpoint, assert no `OtlpTraceExporter`/`OtlpMetricExporter`/`OtlpLogExporter` in the provider's processors (reflection on `TracerProviderSdk` is fragile: instead resolve `IOptionsMonitor<OtlpExporterOptions>` registrations count and a test hook `TelemetryRegistrationReport` produced by the composition root). Integration: collector container with `file` exporter, gRPC and HTTP/protobuf. |
| AC-2 | `TraceCorrelationTests` (unit, `WebApplicationFactory`) | Send `traceparent`; assert server span trace id, problem-details `traceId`, every log record's `trace_id` (in-memory log exporter), audit record `TraceId` (fake audit sink), `HttpContext.TraceIdentifier`. |
| AC-3 | `RejectionSpanTests` | 401 (no credentials), 429 (limiter forced), 403: server span has `http.response.status_code` and `error.type`. |
| AC-4 | `DataPathTraceTests` | WebSQL, OData, GraphQL query on a governed SQLite table: one trace with server, compile, decision, `db` client span (attributes), governance store span. |
| AC-5 | `CanaryLeakTests` (unit, shared harness) | Canary tokens `CANARY_<guid>` in filter literal, bound parameter, GraphQL variable, OData `$filter`, REST body, MCP tool arguments, async job SQL, webhook payload, a value that triggers a driver error (SQLite constraint violation with the canary value), masked and classified column values, header `X-Custom`, query string, `baggage`, `tracestate`; serialize all exported spans (tags, events, links, status), logs (body, attributes, exception) and metric points (attributes) and assert zero occurrences. Also run with `Sampling:Ratio=1` and `SpanDetail=Detailed`. |
| AC-6 | `StatementTextPolicyTests`, `SqlTextScrubberTests` (incl. FsCheck property: for random literals embedded in generated SQL, the scrubbed text never contains the literal; unsupported constructs return false) | 4-way matrix of ceiling × source policy; masking replacement literal and tenant literal never in text or hash input. |
| AC-7 | `PropagationSecurityTests` | Baggage dropped (`Baggage.Current`, `Activity.Baggage`, outbound headers via a stub handler); `sampled=1` with ratio 0 → not recorded; malformed/oversized `traceparent`/`tracestate` → new trace. |
| AC-8 | `OutboundPropagationTests` | Stub primary handler per integration: internal carries `traceparent` (no `tracestate`, no baggage); webhook client none unless `PropagateTraceContext`; unknown integration name none. |
| AC-9 | `AsyncLinkTests` | Job, webhook delivery (outbox SQLite), Redis event (fake bus), CDC poll (fake source) produce spans with a link to the origin context. |
| AC-10 | `IdentityLeakTests` | SID/UPN/session id/token canaries; pseudonymous mode stable per key, changes on key rotation, absent on metrics. |
| AC-11 | `MetricCatalogTests` (unit) | `MeterListener` enumerates every instrument of the registry's meters after touching all instruments via a registry `EnsureCreated()`; names match `^[a-z][a-z0-9_]*(\.[a-z0-9_]+)+$`, no `_total`/`_ms` suffix, unit set; recorded attribute keys ⊆ `TelemetryMetricCatalog`. |
| AC-12 | `TelemetryArchitectureTests` (architecture), `MetricsEndpointTests` (unit), Helm template test | No type in `src` references `Prometheus.Counter`/`Prometheus.Metrics.Create*`/`UseHttpMetrics`; Infrastructure has no prometheus-net reference; `/metrics` golden names behind `MetricsScrape`; `helm template` + grep of `prometheusrule.yaml` expressions against the golden list. |
| AC-13 | `DataSourceTelemetryPolicyTests` | Global provider returns defaults for any name (incl. null/empty/unknown); fake provider changes span detail, threshold and log level for one source only; `ClampTo` never exceeds the ceiling (FsCheck). |
| AC-14 | `ExporterResilienceTests` | A blocking test exporter (`ManualResetEventSlim` never set) behind the batch processor; 50 requests complete with status 200 while the exporter is blocked (no timing assertion); dropped counter increases once the queue is full. Exporter pointed at a closed localhost port: requests still succeed (no wait on export). |
| AC-15 | `HotPathAllocationTests` (unit, deterministic), `TelemetryOverheadBenchmark` (BenchmarkDotNet, not in the PR gate) | Unit: `GC.GetAllocatedBytesForCurrentThread()` delta is 0 for `SourceCommandTelemetry.Start/Dispose` + histogram record with a non-sampled parent (after warm-up). Benchmark: governed query pipeline (existing `CompilePathBenchmark`/`GatewayLoadBenchmark` setup) with telemetry off / ratio 0.1 / 1.0; run via the existing benchmark workflow (`benchmarks/baselines`) on demand and before merge; result recorded in the PR. |
| AC-16 | Helm template tests, collector config validation | `helm template` with OTLP values renders env vars, Secret references, downward API; collector example validated with `otelcol validate` (CI job using the pinned collector image, or documented manual step if the CI runner has no container runtime). |

## 7. Merge Hotspots with Branches in Flight

| File | Other branch | Nature | Mitigation |
|---|---|---|---|
| `Persistence/Migrations/{Sqlite,PostgreSql,SqlServer}BaselineSchema.cs`, `Shape/governance-shape.*.json` | `feat/issue-99-cdc-fanout` (adds CDC stream tables) | Both extend the baseline; JSON snapshots are generated | WP-C3 touches only the `WEBHOOK_OUTBOX` block; regenerate the shape snapshots after rebasing; whoever merges second regenerates. |
| `MssqlChangeTrackingPoller.cs`, `RedisCdcEventChannel.cs`, `Webhooks/*` | `feat/issue-99-cdc-fanout` | CDC fan-out rewrites the poller/stream path | CDC part of WP-C3 waits for BL-01 on `main`; otherwise ship job/webhook/event-bus links first and CDC in a follow-up commit. |
| `GatewayServiceCollectionExtensions.cs`, `GatewayOptions.cs`, `GatewaySections.cs` | `feat/issue-99-cdc-fanout`, `feat/issue-107-rowfilter-keyset-cache` | Options and DI registration | BL-06 does **not** edit these files (own section binding, own extension file). |
| `GatewayCoreServiceExtensions.cs` | possibly `feat/scale-5000-users` | BL-06 deletes lines 121-135 only | Small, isolated hunk. |
| `GatewayDiagnostics.cs` | `feat/issue-107-rowfilter-keyset-cache` (likely adds a key-set cache counter) | Instrument renames | BL-02 uses `autheris.cache.requests` with `autheris.cache.name=keyset` (C-5) instead of a new counter; whoever merges second adapts. |
| `GovernedSqlRewriter.cs`, `GovernedSqlExecutor.cs`, policy factory | `feat/issue-107-rowfilter-keyset-cache` | Row-filter rewriting near the `LogGeneratedSql` site and execution | WP-B3 keeps its edits to the logging block and the scope around `Execute*Async`. |
| `Program.cs`, `GatewayApplicationBuilderExtensions.cs` | any | Middleware order | Changes limited to the Serilog block, first middleware, `UseHttpMetrics` and the tracing middleware line. |

## 8. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| prometheus-net meter adapter renames (unit suffixes) differ from expectations. | `PrometheusRule` mismatch. | Golden `/metrics` names test (WP-C1); rules generated from it. |
| ASP.NET Core instrumentation version still re-extracts context with `Propagators.DefaultTextMapPropagator` and sets `Baggage.Current`. | Baggage leak. | Default text map propagator without baggage (D-4) plus AC-7 test against the pinned version. |
| `SqlTextScrubber` misses a dialect construct. | Literal in `db.query.text`. | Fail closed on unknown constructs; text is double opt-in; property tests; collector `redaction` processor as second layer in the example. |
| Some execution site is not wrapped by `SourceCommandTelemetry`. | Missing span (not a leak). | Architecture test listing allowed `Execute*Async` sites. |
| Static meters and parallel tests. | Flaky metric assertions. | `TelemetryCollection`, attribute-filtered `MetricCollector`. |
| `Disposed` event not raised by a provider's connection type. | Governance store span never ends (memory). | Unit test per engine (SQLite in unit, PostgreSQL/SQL Server in integration); fallback: end on `StateChange` to `Closed`. |
| BL-01 lands late. | CDC links missing. | CDC part split out (section 7). |
| JSON stdout batch exporter loses logs on hard crash. | Last log lines lost. | `ForceFlush` on `IHostApplicationLifetime.ApplicationStopping` and unhandled-exception handler; queue small (2048). |

## 9. Owner Questions

None blocking. Non-blocking defaults taken (owner may override later): driver-native DB instrumentation stays unavailable (D-7); CloudEvent (CDC) subscriptions never propagate `traceparent` until BL-01 adds a stored per-subscription flag; `OTEL_TRACES_SAMPLER` is ignored in favour of the trust-aware sampler (D-3).

---

## 10. Security Review (Phase 3)

**Reviewer:** Security Expert (`csharp-security-expert`), 2026-10-16. Scope: PRD `REQ-OPS-BL06` sections 4-8 and this plan sections 1-7.

### 10.1 Threat analysis

| # | Threat | Vector | Assessment |
|---|---|---|---|
| T-1 | Data leakage via span attributes | Literal values, tenant literals in compiled SQL, table data in tags, new third-party sources | High without controls. Verified that compiled SQL contains inlined literals (section 1). Covered by D-6 processor, D-9 scrubber, SEC-OT06-01..04. |
| T-2 | Leakage via logs | 962 log sites, formatted messages, framework logs with paths and query strings, `LogGeneratedSql` | High. Covered by D-12 (template body, single redaction point), SEC-OT06-05. |
| T-3 | Leakage via exceptions | 284 exception log sites, driver messages with values, `Activity.AddException` | High. D-10, SEC-OT06-06. |
| T-4 | Leakage / side channel via metric labels | tenant, table, user, datasource name, `error.type` from messages | Medium. SEC-OT06-07/08. |
| T-5 | Cardinality DoS | runtime-registered data sources, `http.route` fallback to raw path, error types, operation names from callers | Medium. SEC-OT06-08. |
| T-6 | Trace-context injection / forced sampling | `sampled=1` from untrusted callers, oversized `tracestate`, crafted trace ids colliding with other requests | Medium. SEC-OT06-09/10. |
| T-7 | Baggage abuse | identity/tenant smuggling, propagation to third parties, memory | Medium. SEC-OT06-11. |
| T-8 | Cross-tenant telemetry mix-up | `AsyncLocal` tenant leaking across pooled work items or background loops; tenant from headers | Medium. SEC-OT06-12. |
| T-9 | Exporter SSRF / TLS / credentials | endpoint from config pointing at internal services; redirects; headers in `appsettings.json`; plaintext | Medium (operator config, not user input). SEC-OT06-13/14. |
| T-10 | Log forging | CRLF / JSON injection via attribute values and message templates | Low after JSON exporter. SEC-OT06-15. |
| T-11 | Audit correlation integrity | caller-chosen trace id in audit, duplicate trace ids across requests, trace id used as key | Medium. SEC-OT06-10. |
| T-12 | Availability via telemetry | blocking exporter, unbounded queues, slow stdout | Medium. SEC-OT06-16. |
| T-13 | Policy bypass via per-source hook | BL-05 provider returning more verbose values than the ceiling | Medium. SEC-OT06-17. |
| T-14 | Pseudonym reversal | unkeyed or short hash of SIDs, key in config | Medium. SEC-OT06-18. |

### 10.2 Binding conditions

| ID | Condition |
|---|---|
| SEC-OT06-01 | **Fail-closed span allow-list.** `SpanRedactionProcessor` is the first processor of the tracer pipeline and drops every attribute of an Autheris-source span whose key is not allowed for that span name; unknown span names get only the base set. It also processes third-party spans (ASP.NET Core, HttpClient): remove `url.query`, strip query from `url.full`, drop all request/response header attributes except `content-type`, drop `db.statement`/`db.query.text` without the internal allow marker. Span status descriptions are replaced by stable codes. `user.sid`, `enduser.id`, session ids, principal ids, `table.id` raw values from requests, `db.statement` are not in any catalog entry. |
| SEC-OT06-02 | **Existing tag sites migrated.** No `Activity.SetTag`/`AddTag`/`SetBaggage`/`AddBaggage`/`AddException` in `src/` outside the telemetry helpers (`SourceCommandTelemetry`, `GovernanceStoreTelemetry`, `GovernanceDecisionTelemetry`, `TelemetrySpan.Set`, processors). Architecture test enforces it. Casbin no longer writes to `Activity.Current`; it tags only its own decision span. |
| SEC-OT06-03 | **No values in summaries and names.** Span names and `db.query.summary` are built from metadata (operation, qualified governed table name), never from request text. GraphQL operation names are emitted only if they match `^[_A-Za-z][_0-9A-Za-z]{0,63}$`, else `[invalid]`; GraphQL documents and variables, OData query options, request bodies and MCP tool arguments are never emitted. MCP tool names are emitted only if registered. |
| SEC-OT06-04 | **Statement text and hash from scrubbed SQL only.** `db.query.text` requires global ceiling **and** per-source `Parameterized` **and** successful `SqlTextScrubber.TryScrub`; the scrubber fails closed on dollar quoting, Oracle q-quotes, unterminated constructs and input > 64 KiB. `autheris.db.query.hash` is computed over the scrubbed text only and omitted when scrubbing fails. The same rule governs the former `LogGeneratedSql` output. Enabling `AllowStatementText` logs a startup warning and is shown in the system configuration endpoint. |
| SEC-OT06-05 | **Single log redaction point.** The OpenTelemetry logging provider is the only logging provider; every exporter (stdout JSON, OTLP) sits behind `LogRedactionProcessor`. Log body is the message template; `IncludeFormattedMessage=false`. Sensitive-key catalog redaction and query-string stripping apply to attributes and scope values. A test asserts `ILoggerFactory` has exactly the expected provider set (no console/debug/event-log provider added by defaults). |
| SEC-OT06-06 | **Exception redaction.** Spans and logs carry `exception.type`, stable `error.type`, stack trace and only allow-listed messages; `DbException` and unknown types are `[redacted]`, including inner exceptions. `RecordException` is off for the ASP.NET Core and HttpClient instrumentation; `GatewayExceptionHandler` records through `TelemetryExceptionPolicy`. The allow-list test constructs each allow-listed exception and proves the message is value-free (constant). |
| SEC-OT06-07 | **Metric attribute allow-list.** Every instrument has a fixed attribute key set in `TelemetryMetricCatalog`; attribute values are enums/stable codes (never exception messages, never tenant/user/table/query hash/route parameter). `autheris.tenant.id` only with `Metrics:IncludeTenant` on the two documented histograms. `error.type` on metrics uses `TelemetryExceptionPolicy.ErrorType`. |
| SEC-OT06-08 | **Cardinality bound.** Views set `CardinalityLimit` per instrument (default 2000, tenant 500, datasource 200) with overflow attribute; `autheris.datasource.name` is normalized by `TelemetryNames.DataSource` (charset, length 64). `http.route` is the endpoint template; requests without a matched route use the fixed value `unmatched` (no raw path). Span link counts per span ≤ 1 (origin). |
| SEC-OT06-09 | **No forced sampling.** The parent `sampled` flag of a remote parent is ignored unless `Propagation:TrustParentSampling=true`; `OTEL_TRACES_SAMPLER` is not honoured. Malformed or oversized `traceparent`/`tracestate` (> 512 bytes or > 32 members) are dropped before extraction. Propagation headers never influence authentication, tenant resolution, rate-limit keys, authorization or audit identity (test: same request with and without headers yields identical decisions and limiter keys). |
| SEC-OT06-10 | **Audit correlation integrity.** The audit record's unique key stays the audit entry id; `trace_id` is a correlation attribute and is never used for de-duplication, idempotency, lookup-by-trace authorization or chain verification. `details_json` records `trace_remote_parent=true` when the trace id was continued from a caller. The trace id format is validated (32 lowercase hex, not all zero) before it is written. |
| SEC-OT06-11 | **Baggage is dropped.** Incoming `baggage` is never extracted (custom propagator returns none; OpenTelemetry default propagator has no baggage component); Autheris never calls `SetBaggage`/`Baggage.SetBaggage`; outbound `HttpClient` calls carry no `baggage` and no `tracestate`. The configurable baggage allow-list of PRD D-8 is **not** implemented in BL-06 (default empty, no internal consumer); any future allow-list must be keys only, internal hops only, and never identity/tenant/authorization data. |
| SEC-OT06-12 | **Tenant from the authenticated context only.** `autheris.tenant.id` comes from `TelemetryTenantContext`, set only by `SecurityContextResolutionMiddleware` (resolved tenant) and by background loops per work item through a disposable scope; it is never derived from headers, baggage or `tracestate`. Background loops enter and dispose the scope per item (no leakage to the next item). Telemetry is never readable through any Autheris API. |
| SEC-OT06-13 | **Exporter channel hardening.** Exporters exist only when an endpoint is configured; the endpoint must be an absolute `http(s)` URI without user info, query or fragment; plain `http` only with `Otlp:AllowInsecureTransport=true` (startup warning) or in Development. All exporters use `OtlpHttpClientFactory` (`AllowAutoRedirect=false`, TLS ≥ 1.2, no proxy unless configured, optional mTLS client certificate from mounted Secret files). Certificate validation is never disabled. |
| SEC-OT06-14 | **Exporter credentials from secrets.** OTLP headers only via `OTEL_EXPORTER_OTLP_HEADERS` sourced from a Kubernetes Secret (Helm `secretKeyRef`); a `Gateway:Telemetry:Otlp:Headers` key fails startup; header values are never logged (the options validator and the system configuration endpoint print only header names). Private keys are read from files, never from configuration values. |
| SEC-OT06-15 | **Log forging.** CA2254 is an error in `src/`; user-controlled values are attributes only; the JSON stdout exporter uses `Utf8JsonWriter` default escaping (control characters, `<`, `>`, `&`, non-ASCII escaped) and truncates values to 1024 characters; attribute keys are length-limited (128) and keys from scopes that are not identifiers are dropped. |
| SEC-OT06-16 | **Telemetry never degrades the data path.** Batch processors only (bounded queues, drop on overflow, export timeout); no synchronous export on request threads; processors catch and swallow their own exceptions (fail open for availability) but a redaction exception drops the whole attribute set of the item (fail closed for data). Dropped items are counted. |
| SEC-OT06-17 | **Per-source ceiling.** `DataSourceTelemetryPolicy.ClampTo(TelemetryCeiling)` is applied by the consumer (`SourceCommandTelemetry`, `DataSourceLoggers`) on every read, not only by the provider, so a faulty BL-05 provider cannot exceed the ceiling; a provider exception or `null` result yields the global defaults (never more verbose). |
| SEC-OT06-18 | **Pseudonymous identity.** Only HMAC-SHA256 with a key ≥ 32 bytes from the secret store, emitted with a key id, output truncated to 128 bits; never on metrics; mode `None` is the default; clear-text SID/UPN/e-mail/session id/token/API key/cookie/`Authorization` values are never emitted in any mode. |
| SEC-OT06-19 | **Outbound propagation fail closed.** Only integration names in `OutboundIntegrations.Internal` get a propagating handler; all others (including unknown and newly added clients) get the no-output propagator. Customer webhooks propagate only `traceparent` and only when the configured subscription sets `PropagateTraceContext=true`; CloudEvent subscriptions never propagate in BL-06. No trace context is written into source SQL or session context. |
| SEC-OT06-20 | **Stored trace context is untrusted on read.** `WEBHOOK_OUTBOX.trace_parent`, Redis envelopes and CDC envelopes are parsed with the same validator as ingress; invalid values are ignored (no link) and never fail or alter the work item; the column has a length limit (55) and stores no `tracestate`/baggage. |
| SEC-OT06-21 | **Audit stays separate.** The audit chain is not exported over OTLP; the `autheris.audit.append` span carries only tier, outcome and duration (no entry id, no chain hash, no actor). |

### 10.3 Mandatory security tests

| Test | Covers |
|---|---|
| `CanaryLeakTests` (AC-5 matrix of section 6, incl. driver error, masked/classified columns, tenant literal in virtual filter, masking replacement literal, `Sampling:Ratio=1`, `SpanDetail=Detailed`, `AllowStatementText=true` + `Parameterized`) | SEC-OT06-01..06 |
| `SpanRedactionProcessorTests`: unknown key dropped; third-party `url.query`/headers/`db.statement` removed; status description replaced | SEC-OT06-01 |
| `TelemetryArchitectureTests`: no raw `SetTag`/`AddException`/`SetBaggage` outside helpers; every static `ActivitySource`/`Meter` registered; no prometheus-net instrumentation API; every data-path `Execute*Async` site inside a scope | SEC-OT06-02, AC-12 |
| `GraphQlOperationNameTests` (invalid names → `[invalid]`; document/variables never present) | SEC-OT06-03 |
| `SqlTextScrubberTests` + FsCheck property; fail-closed constructs per dialect | SEC-OT06-04 |
| `LoggingProviderSetTests` (exactly the OpenTelemetry provider), `LogRedactionProcessorTests` (query strings, sensitive keys, scopes, exceptions, truncation, tenant attribute) | SEC-OT06-05, -06, -15 |
| `ExceptionPolicyTests`: `DbException`/inner exceptions redacted; allow-listed messages constant | SEC-OT06-06 |
| `MetricCatalogTests` (AC-11) + `CardinalityLimitTests` (2,001 distinct datasource names → overflow series; unmatched route → `unmatched`) | SEC-OT06-07, -08 |
| `PropagationSecurityTests` (AC-7) + `PropagationDoesNotAffectSecurityTests` (decisions, limiter keys, tenant, audit actor identical with/without `traceparent`/`baggage`) | SEC-OT06-09, -11 |
| `AuditTraceCorrelationTests`: `trace_remote_parent` flag; invalid trace id rejected; two requests with the same caller trace id produce two audit entries with distinct entry ids | SEC-OT06-10 |
| `TenantTelemetryIsolationTests`: two async jobs / outbox rows of different tenants processed back to back on one worker produce spans and logs with their own tenant; header `X-Tenant-Id`/baggage `tenant` never appears as `autheris.tenant.id` | SEC-OT06-12 |
| `OtlpChannelSecurityTests`: insecure `http` rejected outside Development without the flag; user info/query in endpoint rejected; `Otlp:Headers` key fails startup; redirect not followed (stub server returning 307 to another host, asserted via handler, no network); header values absent from logs and config endpoint | SEC-OT06-13, -14 |
| `ExporterResilienceTests` (AC-14) | SEC-OT06-16 |
| `DataSourcePolicyCeilingTests`: faulty provider (throws, returns `null`, returns more verbose values) never exceeds the ceiling | SEC-OT06-17 |
| `IdentityLeakTests` (AC-10) incl. key length validation and key rotation | SEC-OT06-18 |
| `OutboundPropagationTests` (AC-8) incl. unknown integration name and CloudEvent subscription | SEC-OT06-19 |
| `StoredTraceContextTests`: malformed/oversized `trace_parent` in outbox, Redis envelope and CDC envelope → no link, item processed | SEC-OT06-20 |
| `AuditAppendSpanTests`: span attributes ⊆ {tier, outcome}; no OTLP export of audit entries | SEC-OT06-21 |

### 10.4 Changes to the PRD imposed by this review

1. PRD D-8 "configurable baggage allow-list": not implemented in BL-06 (SEC-OT06-11).
2. PRD D-12 / SR-2 "parameterized compiled SQL": replaced by scrubbed SQL with fail-closed scrubber (SEC-OT06-04).
3. PRD D-6 driver-native instrumentation switch: not offered (D-7).
4. PRD D-9 per-subscription external propagation: configured subscriptions only; CloudEvent subscriptions never in BL-06 (SEC-OT06-19).

### 10.5 Verdict

**APPROVED WITH CONDITIONS.** The architecture is fail closed for data (allow-list processors, template-only log bodies, scrubbed SQL, redacted exceptions, baggage dropped) and fail open only for telemetry availability. Phase 4 may start with WP-0. SEC-OT06-01 to SEC-OT06-21 and the tests of 10.3 are mandatory; the Phase-5 reviewer verifies each condition against the code and the Phase-6 feature document lists them as operator-visible guarantees.

### 10.6 Gate 3 checklist

- [x] Threats T-1..T-14 analysed, each mapped to a binding condition.
- [x] Fail-closed behaviour defined for every redaction path; fail-open defined only for exporter availability.
- [x] No governance schema change beyond the `0001` baseline extension (`WEBHOOK_OUTBOX.trace_parent`), on 3 engines + shapes.
- [x] `AstSecurityVisitor.cs` not touched.
- [x] Fast-CI test strategy (in-memory exporters, no sleeps); benchmarks outside the PR gate.

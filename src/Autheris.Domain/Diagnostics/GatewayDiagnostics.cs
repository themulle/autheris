namespace Autheris.Domain.Diagnostics;

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>
/// Gateway-wide diagnostics, activity sources, and metric instruments.
/// </summary>
public static class GatewayDiagnostics
{
    public const string ActivitySourceName = "Autheris.Core";
    public const string MeterName = "Autheris.Metrics";

    public static readonly ActivitySource Source = new(ActivitySourceName, "2.0.0");
    public static readonly Meter Meter = new(MeterName, "2.0.0");

    public static readonly Counter<long> ForbiddenRequestsCounter =
        Meter.CreateCounter<long>("autheris_forbidden_requests_total", description: "Total number of rejected requests (403/Forbidden)");

    /// <summary>R-3: a Redis event-bus publish failed (tag <c>message_type</c> only: channel names can carry tenant ids).</summary>
    public static readonly Counter<long> EventBusPublishFailedCounter =
        Meter.CreateCounter<long>("autheris_event_bus_publish_failed_total", description: "Redis event bus publishes that failed and were rethrown");

    /// <summary>R-3: a Redis event-bus subscriber handler (or message deserialization) failed.</summary>
    public static readonly Counter<long> EventBusHandlerFailedCounter =
        Meter.CreateCounter<long>("autheris_event_bus_handler_failed_total", description: "Redis event bus subscriber handlers that failed");

    /// <summary>A-14: a local policy change could not be propagated to the cluster policy epoch (other nodes converge via cache TTL).</summary>
    public static readonly Counter<long> PolicyEpochPropagationFailedCounter =
        Meter.CreateCounter<long>("autheris_policy_epoch_propagation_failed_total", description: "Casbin policy epoch increments that failed or found the cluster state unavailable");

    public static readonly Counter<long> UnroutedRequestCounter =
        Meter.CreateCounter<long>("autheris_unrouted_requests_total", description: "Requests answered by the router without a matching endpoint (404/405/400); not written to the audit chain");

    /// <summary>PLAN-SEC-115: calls to a read path the operator switched off. Tag <c>path</c> is a closed set (no tool names, no ids).</summary>
    public static readonly Counter<long> DisabledPathRequestCounter =
        Meter.CreateCounter<long>("autheris_disabled_path_requests_total", description: "Requests or tool calls that reached a read path switched off by configuration");

    /// <summary>Issue #111 (A-6/A-9): a persisted catalog tag or tier could not be read or was malformed and degraded to Hidden without tags. No table names or ids as tags.</summary>
    public static readonly Counter<long> CatalogVisibilityDegradedCounter =
        Meter.CreateCounter<long>("autheris_catalog_visibility_degraded_total", description: "Catalog tier or tag reads that degraded to Hidden because the stored settings were unreadable or malformed");

    public static readonly Counter<long> QueryTooComplexCounter =
        Meter.CreateCounter<long>("autheris_query_too_complex_total", description: "Number of requests rejected due to AST complexity");

    public static readonly Counter<long> CrossTenantMismatchCounter =
        Meter.CreateCounter<long>("autheris_cross_tenant_mismatch_total", description: "Cross-tenant webhook or access mismatches");

    public static readonly Counter<long> AuthzDependencyFailureCounter =
        Meter.CreateCounter<long>("authz_dependency_failure_total", description: "Authorization dependency failures that resulted in a fail-closed denial");

    public static readonly Counter<long> RoleResolutionDegradedCounter =
        Meter.CreateCounter<long>("autheris_role_resolution_degraded_total", description: "Requests whose group and role resolution was unavailable (data access is denied)");

    public static readonly Counter<long> AuditWriteFailedCounter =
        Meter.CreateCounter<long>("autheris_audit_write_failed_total", description: "Audit writes that failed, by tier (required or best_effort)");

    public static readonly Counter<long> AuditDeadLetterCounter =
        Meter.CreateCounter<long>("autheris_audit_dead_letter_total", description: "Total number of audit events diverted to dead-letter storage");

    public static readonly Counter<long> DmlAuditFailedCounter =
        Meter.CreateCounter<long>("autheris_dml_audit_failed_total", description: "Number of committed WebSQL DML statements whose audit record could not be written");

    public static readonly Counter<long> EventBusDroppedCounter =
        Meter.CreateCounter<long>("autheris_eventbus_dropped_total", description: "Total number of event bus messages dropped due to channel saturation");

    public static readonly Counter<long> GatewayEventBusDroppedCounter =
        Meter.CreateCounter<long>("autheris_event_bus_dropped_total", description: "Gateway events dropped (oldest first) because a subscriber queue was full");

    public static readonly Counter<long> GatewayEventBusHandlerTimeoutCounter =
        Meter.CreateCounter<long>("autheris_event_bus_handler_timeout_total", description: "Gateway event handler invocations that exceeded the per-handler timeout");

    public static readonly Histogram<double> PolicyEvaluationDuration =
        Meter.CreateHistogram<double>("autheris_policy_evaluation_duration_ms", "ms", description: "Duration of Casbin ABAC policy evaluation");

    public static readonly Histogram<double> LineageTraversalDuration =
        Meter.CreateHistogram<double>("autheris_lineage_traversal_duration_ms", "ms", description: "Duration of cycle-safe lineage traversal");

    public static readonly Counter<long> RowFilterKeySetHits =
        Meter.CreateCounter<long>("autheris.rowfilter.keyset.hits", description: "Correlated row-filter sites served from a cached key set");

    public static readonly Counter<long> RowFilterKeySetMisses =
        Meter.CreateCounter<long>("autheris.rowfilter.keyset.misses", description: "Correlated row-filter sites whose key set was loaded for the statement");

    public static readonly Counter<long> RowFilterKeySetFallbacks =
        Meter.CreateCounter<long>("autheris.rowfilter.keyset.fallbacks", description: "Correlated row-filter sites that kept the exact EXISTS form, by reason");

    public static readonly Histogram<double> RowFilterKeyQueryDuration =
        Meter.CreateHistogram<double>("autheris.rowfilter.keyset.key_query.duration", "ms", description: "Duration of a row-filter key query");

    public static readonly Counter<long> CallerAttributeResolutions =
        Meter.CreateCounter<long>("autheris.callerattr.resolutions", description: "Caller attribute resolutions, by provider kind and outcome");

    public static readonly Counter<long> CallerAttributeCacheHits =
        Meter.CreateCounter<long>("autheris.callerattr.cache.hits", description: "Caller attribute value cache hits");

    public static readonly Counter<long> CallerAttributeCacheMisses =
        Meter.CreateCounter<long>("autheris.callerattr.cache.misses", description: "Caller attribute value cache misses");

    public static readonly Histogram<double> CallerAttributeProviderDuration =
        Meter.CreateHistogram<double>("autheris.callerattr.provider.duration", "ms", description: "Duration of a caller attribute provider call");

    public static readonly Counter<long> CallerAttributeCircuitOpen =
        Meter.CreateCounter<long>("autheris.callerattr.circuit.open", description: "Caller attribute provider calls refused by an open circuit");

    // Versioned PII allow-list per span type (Frozen for zero-allocation thread-safe lookups)
    private static readonly FrozenDictionary<string, FrozenSet<string>> SpanTagAllowList = new Dictionary<string, FrozenSet<string>>
    {
        ["Governance.EvaluateConsent"] = (FrozenSet<string>)new[] { "user.sid", "tenant.id", "decision", "table.id", "policy.epoch" }.ToFrozenSet(),
        ["Governance.CasbinAbacEnforcement"] = (FrozenSet<string>)new[] { "user.sid", "tenant.id", "policy.match", "rule.count", "latency.ms" }.ToFrozenSet(),
        ["SqlExecution.Pushdown"] = (FrozenSet<string>)new[] { "db.system", "db.statement", "tenant.id", "rows.affected" }.ToFrozenSet(),
        ["AuditLog.AppendHmacEntry"] = (FrozenSet<string>)new[] { "audit.entry_id", "tenant.id", "chain.height" }.ToFrozenSet(),
        ["Lineage.Traverse"] = (FrozenSet<string>)new[] { "tenant.id", "root.node_id", "nodes.visited", "cycle.detected" }.ToFrozenSet(),
        ["Itsm.Webhook"] = (FrozenSet<string>)new[] { "itsm.system", "itsm.ticket_id", "tenant.id", "action" }.ToFrozenSet()
    }.ToFrozenDictionary();

    /// <summary>
    /// Tags an activity with safety checks against <see cref="TelemetrySpanCatalog"/> and legacy <see cref="SpanTagAllowList"/>.
    /// </summary>
    public static void SetSafeTag(Activity? activity, string spanName, string key, object? value)
    {
        if (activity == null || value == null) return;
        if (TelemetrySpanCatalog.IsAllowed(spanName, key) ||
            (SpanTagAllowList.TryGetValue(spanName, out var allowedKeys) && allowedKeys.Contains(key)))
        {
            activity.SetTag(key, value);
        }
    }
}

namespace Autheris.Domain.Diagnostics;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Frozen;
using System.Collections.Generic;

public static class GatewayDiagnostics
{
    public const string ActivitySourceName = "Autheris.Core";
    public const string MeterName = "Autheris.Metrics";

    public static readonly ActivitySource Source = new(ActivitySourceName, "2.0.0");
    public static readonly Meter Meter = new(MeterName, "2.0.0");

    public static readonly Counter<long> ForbiddenRequestsCounter =
        Meter.CreateCounter<long>("autheris_forbidden_requests_total", description: "Anzahl abgewiesener Anfragen (403/Forbidden)");

    public static readonly Counter<long> UnroutedRequestCounter =
        Meter.CreateCounter<long>("autheris_unrouted_requests_total", description: "Requests answered by the router without a matching endpoint (404/405/400); not written to the audit chain");

    public static readonly Counter<long> QueryTooComplexCounter =
        Meter.CreateCounter<long>("autheris_query_too_complex_total", description: "Number of requests rejected due to AST complexity");

    public static readonly Counter<long> CrossTenantMismatchCounter =
        Meter.CreateCounter<long>("autheris_cross_tenant_mismatch_total", description: "Cross-tenant webhook or access mismatches");

    public static readonly Counter<long> AuthzDependencyFailureCounter =
        Meter.CreateCounter<long>("authz_dependency_failure_total", description: "Authorization dependency failures that resulted in a fail-closed denial");

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
        Meter.CreateHistogram<double>("autheris_policy_evaluation_duration_ms", "ms", description: "Dauer der Casbin ABAC Evaluierung");

    public static readonly Histogram<double> LineageTraversalDuration =
        Meter.CreateHistogram<double>("autheris_lineage_traversal_duration_ms", "ms", description: "Dauer der zyklensicheren Lineage-Traversierung");

    // Versionierte PII-Allow-List je Span-Typ (Frozen für Zero-Allocation Thread-Safe Lookups)
    private static readonly FrozenDictionary<string, FrozenSet<string>> SpanTagAllowList = new Dictionary<string, FrozenSet<string>>
    {
        ["Governance.EvaluateConsent"] = (FrozenSet<string>)new[] { "user.sid", "tenant.id", "decision", "table.id", "policy.epoch" }.ToFrozenSet(),
        ["Governance.CasbinAbacEnforcement"] = (FrozenSet<string>)new[] { "user.sid", "tenant.id", "policy.match", "rule.count", "latency.ms" }.ToFrozenSet(),
        ["SqlExecution.Pushdown"] = (FrozenSet<string>)new[] { "db.system", "db.statement", "tenant.id", "rows.affected" }.ToFrozenSet(),
        ["AuditLog.AppendHmacEntry"] = (FrozenSet<string>)new[] { "audit.entry_id", "tenant.id", "chain.height" }.ToFrozenSet(),
        ["Lineage.Traverse"] = (FrozenSet<string>)new[] { "tenant.id", "root.node_id", "nodes.visited", "cycle.detected" }.ToFrozenSet(),
        ["Itsm.Webhook"] = (FrozenSet<string>)new[] { "itsm.system", "itsm.ticket_id", "tenant.id", "action" }.ToFrozenSet()
    }.ToFrozenDictionary();

    public static void SetSafeTag(Activity? activity, string spanName, string key, object? value)
    {
        if (activity == null || value == null) return;
        if (SpanTagAllowList.TryGetValue(spanName, out var allowedKeys) && allowedKeys.Contains(key))
        {
            activity.SetTag(key, value);
        }
    }
}

namespace Autheris.Domain.Events;

using System;
using System.Collections.Generic;

/// <summary>
/// Root interface for all domain and operational events emitted by the Autheris gateway.
/// </summary>
public interface IGatewayEvent
{
    string EventId { get; }
    string EventType { get; }
    DateTimeOffset Timestamp { get; }
    string? TenantId { get; }
    string? CorrelationId { get; }
}

/// <summary>
/// Standard CloudEvents v1.0 specification compliant envelope.
/// </summary>
public sealed record GatewayCloudEvent(
    string Id,
    string Source,
    string Type,
    DateTimeOffset Time,
    string? Subject,
    string? DataContentType,
    object? Data,
    string? TenantId = null,
    string? CorrelationId = null
);

// ----------------------------------------------------------------------
// Governance & Consent Domain Events
// ----------------------------------------------------------------------

public sealed record AccessPlanCreatedEvent(
    string EventId,
    string PlanId,
    string DatasetId,
    string RequesterSid,
    int GrantCount,
    DateTimeOffset Timestamp,
    string? TenantId = null,
    string? CorrelationId = null
) : IGatewayEvent
{
    public string EventType => "governance.access_plan.created";
}

public sealed record AccessPlanAppliedEvent(
    string EventId,
    string PlanId,
    string DatasetId,
    string ApproverSid,
    int AppliedGrantCount,
    DateTimeOffset Timestamp,
    string? TenantId = null,
    string? CorrelationId = null
) : IGatewayEvent
{
    public string EventType => "governance.access_plan.applied";
}

public sealed record ConsentDecidedEvent(
    string EventId,
    string ConsentId,
    string SubjectSid,
    string DatasetId,
    string Decision, // "APPROVED" | "DENIED" | "REVOKED"
    string DecidedBySid,
    DateTimeOffset Timestamp,
    string? TenantId = null,
    string? CorrelationId = null
) : IGatewayEvent
{
    public string EventType => "governance.consent.decided";
}

// ----------------------------------------------------------------------
// Catalog & Schema Domain Events
// ----------------------------------------------------------------------

public sealed record DatasetStateChangedEvent(
    string EventId,
    string DatasetId,
    bool OldState,
    bool NewState,
    string ChangedBySid,
    string? Reason,
    DateTimeOffset Timestamp,
    string? TenantId = null,
    string? CorrelationId = null
) : IGatewayEvent
{
    public string EventType => "catalog.dataset.state_changed";
}

public sealed record SchemaDriftDetectedEvent(
    string EventId,
    string DatasetId,
    string DriftSummary,
    DateTimeOffset Timestamp,
    string? TenantId = null,
    string? CorrelationId = null
) : IGatewayEvent
{
    public string EventType => "catalog.schema.drift_detected";
}

// ----------------------------------------------------------------------
// Security & Policy Domain Events
// ----------------------------------------------------------------------

public sealed record PolicyDeniedEvent(
    string EventId,
    string SubjectSid,
    string Action,
    string Resource,
    string Reason,
    DateTimeOffset Timestamp,
    string? TenantId = null,
    string? CorrelationId = null,
    string ReasonCode = PolicyDeniedEvent.DefaultReasonCode,
    string? PolicyId = null
) : IGatewayEvent
{
    /// <summary>
    /// SR-L-15: stable machine-readable denial code. Events never carry verbatim ABAC denial text
    /// (full reasons are written to the audit log only); <see cref="Reason"/> carries the same code.
    /// </summary>
    public const string DefaultReasonCode = "POLICY_DENIED";

    public string EventType => "security.policy.denied";
}

public sealed record SecurityAlertEvent(
    string EventId,
    string AlertType, // e.g. "PROMPT_INJECTION", "JAILBREAK", "BRUTE_FORCE"
    string Severity,  // "LOW", "MEDIUM", "HIGH", "CRITICAL"
    string Details,
    string? SourceIp,
    string? UserSid,
    DateTimeOffset Timestamp,
    string? TenantId = null,
    string? CorrelationId = null
) : IGatewayEvent
{
    public string EventType => "security.alert";
}

// ----------------------------------------------------------------------
// Documentation & Execution Domain Events
// ----------------------------------------------------------------------

public sealed record DocumentDepositedEvent(
    string EventId,
    string AppId,
    string Category,
    string DocId,
    string Title,
    string RelativePath,
    DateTimeOffset Timestamp,
    string? TenantId = null,
    string? CorrelationId = null
) : IGatewayEvent
{
    public string EventType => "docs.document.deposited";

    /// <summary>
    /// Deprecated (SR-L-15): always equals <see cref="RelativePath"/>, never an absolute filesystem path.
    /// Kept for one release so existing plugin binaries and webhook consumers still bind; use <see cref="RelativePath"/>.
    /// </summary>
    [Obsolete("Use RelativePath. This property is populated with the redacted relative path and will be removed.")]
    public string SourceFilePath => RelativePath;

    /// <summary>Builds the doc-lake relative path <c>{appId}/{category}/{docId}</c> from request values.</summary>
    public static string BuildRelativePath(string appId, string category, string docId) => $"{appId}/{category}/{docId}";
}

public sealed record QueryExecutedEvent(
    string EventId,
    string QueryType, // "GRAPHQL" | "SQL" | "MCP"
    string OperationName,
    long ExecutionDurationMs,
    bool WasMasked,
    string? UserSid,
    DateTimeOffset Timestamp,
    string? TenantId = null,
    string? CorrelationId = null
) : IGatewayEvent
{
    public string EventType => "execution.query.completed";
}

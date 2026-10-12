using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

namespace Autheris.Domain.Audit;

/// <summary>
/// Scoped audit context tracking request metadata, security decisions, and metrics across the pipeline.
/// </summary>
public sealed class AuditContext
{
    public string TraceId { get; set; } = Guid.NewGuid().ToString("N");
    public string Channel { get; set; } = "REST";
    public string? SourceIp { get; set; }
    public Sid ActorSid { get; set; } = new Sid("S-1-0-0");
    public TenantId TenantId { get; set; } = TenantId.LegacySingleTenant;
    public string Target { get; set; } = string.Empty;
    public string Decision { get; set; } = "ALLOW";
    public string? EventType { get; set; }
    public string? ReasonCode { get; set; }
    public long? Rows { get; set; }
    public long? Bytes { get; set; }
    public bool IsHandled { get; private set; }

    public Dictionary<string, object?> AdditionalDetails { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void MarkHandled()
    {
        IsHandled = true;
    }

    public void Deny(string reasonCode, string? eventType = null)
    {
        Decision = "DENY";
        ReasonCode = reasonCode;
        if (!string.IsNullOrEmpty(eventType))
        {
            EventType = eventType;
        }
    }

    public void Allow(string? eventType = null)
    {
        Decision = "ALLOW";
        if (!string.IsNullOrEmpty(eventType))
        {
            EventType = eventType;
        }
    }

    public void Error(string reasonCode, string? eventType = null)
    {
        Decision = "ERROR";
        ReasonCode = reasonCode;
        if (!string.IsNullOrEmpty(eventType))
        {
            EventType = eventType;
        }
    }

    public void Describe(string? target = null, long? rows = null, long? bytes = null)
    {
        if (!string.IsNullOrEmpty(target))
        {
            Target = target;
        }
        if (rows.HasValue)
        {
            Rows = rows.Value;
        }
        if (bytes.HasValue)
        {
            Bytes = bytes.Value;
        }
    }

    public void RecordMetrics(long? rows = null, long? bytes = null)
    {
        Describe(target: null, rows: rows, bytes: bytes);
    }
}

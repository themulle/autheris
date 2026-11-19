namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;

public enum CdcOperation
{
    Insert = 1,
    Update = 2,
    Delete = 3,
    Snapshot = 4
}

public sealed record CdcEvent(
    string EventId,
    TableIdentifier Table,
    CdcOperation Operation,
    string? TenantId,
    IReadOnlyDictionary<string, object?>? Before,
    IReadOnlyDictionary<string, object?>? After,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string>? Metadata = null
);

public sealed record StreamSecurityDecision(
    bool IsAllowed,
    IReadOnlyDictionary<string, object?>? MaskedPayload,
    string? FilterReason = null
)
{
    /// <summary>Filter reason of an event refused by the data source transport policy.</summary>
    public const string TransportDeniedReason = "Transport not allowed";

    public static StreamSecurityDecision Denied(string reason) => new(false, null, reason);
    public static StreamSecurityDecision TransportDenied() => Denied(TransportDeniedReason);

    /// <summary>True when the denial came from the data source transport policy.</summary>
    public bool IsTransportDenied => !IsAllowed && string.Equals(FilterReason, TransportDeniedReason, StringComparison.Ordinal);
    public static StreamSecurityDecision Allowed(IReadOnlyDictionary<string, object?> payload) => new(true, payload);
}

public sealed record StreamCdcEvent(
    string EventId,
    string Table,
    string Operation,
    string? TenantId,
    string PayloadJson,
    DateTimeOffset Timestamp
)
{
    public TableIdentifier? TableIdentifier => Autheris.Domain.Common.TableIdentifier.TryParse(Table, out var tid) ? tid : null;

    public StreamCdcEvent(
        string eventId,
        TableIdentifier table,
        string operation,
        string? tenantId,
        string payloadJson,
        DateTimeOffset timestamp)
        : this(eventId, table.ToString(), operation, tenantId, payloadJson, timestamp)
    {
    }
}

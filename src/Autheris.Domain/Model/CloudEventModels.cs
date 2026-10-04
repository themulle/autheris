namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// F-EVT-01: Standardized CloudEvents v1.0 specification envelope.
/// </summary>
public sealed record CloudEventEnvelope(
    [property: JsonPropertyName("specversion")] string SpecVersion,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("datacontenttype")] string DataContentType,
    [property: JsonPropertyName("tenantid")] string? TenantId,
    [property: JsonPropertyName("data")] object? Data,
    [property: JsonPropertyName("subject")] string? Subject = null
);

/// <summary>
/// F-EVT-01: Outbound webhook subscription configuration.
/// </summary>
public sealed record CloudEventWebhookSubscription(
    string Id,
    string TenantId,
    string TargetUrl,
    string FilterTable,
    IReadOnlyList<CdcOperation> FilterOperations,
    string HmacSecret,
    bool IsEnabled = true,
    IReadOnlyDictionary<string, string>? CustomHeaders = null
);

/// <summary>
/// Status result of a webhook delivery attempt.
/// </summary>
public sealed record CloudEventDeliveryResult(
    string DeliveryId,
    string SubscriptionId,
    string EventId,
    bool Success,
    int StatusCode,
    TimeSpan Elapsed,
    string? ErrorMessage = null
);

namespace Autheris.Domain.Model;

using System;
using System.Text.Json.Serialization;

public sealed record DbtCloudWebhookEvent(
    [property: JsonPropertyName("eventId")] string? EventId,
    [property: JsonPropertyName("eventType")] string? EventType,
    [property: JsonPropertyName("timestamp")] DateTimeOffset? Timestamp,
    [property: JsonPropertyName("accountId")] long? AccountId,
    [property: JsonPropertyName("data")] DbtCloudWebhookData? Data
);

public sealed record DbtCloudWebhookData(
    [property: JsonPropertyName("jobId")] long? JobId,
    [property: JsonPropertyName("jobName")] string? JobName,
    [property: JsonPropertyName("runId")] long? RunId,
    [property: JsonPropertyName("runStatus")] string? RunStatus, // "Success", "Error", "Cancelled"
    [property: JsonPropertyName("runReason")] string? RunReason,
    [property: JsonPropertyName("environmentId")] long? EnvironmentId,
    [property: JsonPropertyName("dbtVersion")] string? DbtVersion
);

public sealed record DbtWebhookProcessingResult(
    bool Success,
    string Message,
    string? EventType = null,
    long? RunId = null
);

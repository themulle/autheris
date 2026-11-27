namespace Autheris.Application.ConsentRequests.Events;

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autheris.Domain.Model;

/// <summary>
/// CloudEvents 1.0 event type identifiers and thin non-sensitive payload builders for the consent outbox.
/// Payloads deliberately omit justification text, column rules/values, and approver identities.
/// </summary>
public static class ConsentEventTypes
{
    public const string TypePrefix = "com.autheris.consent.";
    public const string EventSource = "/autheris/consent";

    public const string RequestCreated = TypePrefix + "request.created";
    public const string RequestUpdated = TypePrefix + "request.updated";
    public const string RequestDecided = TypePrefix + "request.decided";
    public const string RequestExpired = TypePrefix + "request.expired";
    public const string GrantRevoked = TypePrefix + "grant.revoked";
    public const string GrantExpiring = TypePrefix + "grant.expiring";
    public const string GrantRenewed = TypePrefix + "grant.renewed";
    public const string GrantExpired = TypePrefix + "grant.expired";

    /// <summary>Issue #1122: the grant's Unity Catalog privileges were applied and confirmed by a read.</summary>
    public const string GrantProvisioned = TypePrefix + "grant.provisioned";

    /// <summary>Issue #1122: the grant's Unity Catalog privileges could not be applied (failed attempts or missing catalog access).</summary>
    public const string GrantProvisioningFailed = TypePrefix + "grant.provisioning_failed";
}

public sealed record ConsentEventLinks(string Self, string? Events = null);

public sealed record ConsentThinEventPayload(
    string TenantId,
    Guid? RequestId,
    Guid? GrantId,
    string ObjectId,
    string Status,
    int Version,
    string WorkflowMode,
    DateTimeOffset OccurredAt,
    ConsentEventLinks Links,
    int? ReminderStage = null,
    Guid? PreviousGrantId = null);

public static class ConsentEventPayloads
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ConsentThinEventPayload ForRequest(ConsentRequest request, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ConsentThinEventPayload(
            request.TenantId.Value,
            request.Id,
            null,
            request.TableIdentifier.ToString(),
            request.Status,
            request.Version <= 0 ? 1 : request.Version,
            request.WorkflowMode.ToSqlString(),
            occurredAt,
            new ConsentEventLinks($"/api/v1/consent/requests/{request.Id}", "/api/v1/consent/events"));
    }

    public static ConsentThinEventPayload ForGrant(
        Consent consent,
        ConsentWorkflowMode workflowMode,
        int version,
        string status,
        DateTimeOffset occurredAt,
        int? reminderStage = null)
    {
        ArgumentNullException.ThrowIfNull(consent);
        var objectId = !string.IsNullOrWhiteSpace(consent.TableIdentifier.TableName)
            ? consent.TableIdentifier.ToString()
            : consent.TableId.ToString("D");
        return new ConsentThinEventPayload(
            consent.TenantId.Value,
            consent.ConsentRequestId,
            consent.Id,
            objectId,
            status,
            version <= 0 ? 1 : version,
            workflowMode.ToSqlString(),
            occurredAt,
            new ConsentEventLinks($"/api/v1/consent/grants/{consent.Id}", "/api/v1/consent/events"),
            reminderStage);
    }

    public static string Serialize(ConsentThinEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}

public sealed record ConsentEventWrite(
    string TenantId,
    Guid EventId,
    string EventType,
    string SubjectId,
    string PayloadJson,
    DateTimeOffset CreatedAt,
    Guid? ExternalSystemId = null);

public sealed record ConsentEventRecord(
    string TenantId,
    long Seq,
    Guid Id,
    string Type,
    string SubjectId,
    string PayloadJson,
    DateTimeOffset CreatedAt);

public sealed record PendingConsentEventDelivery(
    Guid EventId,
    Guid ExternalSystemId,
    string TenantId,
    string EventType,
    string SubjectId,
    string PayloadJson,
    DateTimeOffset CreatedAt,
    int Attempts,
    DateTimeOffset NextAttemptAt);

public sealed record ConsentEventFeedPage(
    IReadOnlyList<ConsentEventRecord> Events,
    long NextSince,
    bool CursorExpired);

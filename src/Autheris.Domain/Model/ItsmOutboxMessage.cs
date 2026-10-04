namespace Autheris.Domain.Model;

using System;

/// <summary>
/// Status of an outbox message in the persistent transactional outbox queue.
/// </summary>
public enum ItsmOutboxStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    DeadLetter = 3
}

/// <summary>
/// Represents a persistent transactional outbox record for asynchronous ITSM ticket creation and webhook dispatching.
/// Prevents loss of external governance tickets during network partitions or external system outages.
/// </summary>
public sealed record ItsmOutboxMessage(
    string Id,
    string RequestId,
    string TenantId,
    string EventType,
    string PayloadJson,
    string PreferredSystem,
    ItsmOutboxStatus Status,
    int RetryCount,
    int MaxRetries,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextRetryAt = null,
    string? LastError = null);

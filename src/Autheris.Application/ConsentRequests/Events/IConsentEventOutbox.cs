namespace Autheris.Application.ConsentRequests.Events;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Transactional consent event outbox, lease-based webhook delivery store, and cursor-fed event log.
/// </summary>
public interface IConsentEventOutbox
{
    Task<long> AppendAsync(
        ConsentEventWrite entry,
        DbTransaction? transaction = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<PendingConsentEventDelivery>> ClaimDeliveriesAsync(
        int limit,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        string leaseOwner,
        CancellationToken ct = default);

    Task MarkDeliveredAsync(
        Guid eventId,
        Guid externalSystemId,
        string leaseOwner,
        DateTimeOffset deliveredAt,
        CancellationToken ct = default);

    Task RescheduleDeliveryAsync(
        Guid eventId,
        Guid externalSystemId,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        CancellationToken ct = default);

    Task MarkDeadLetteredAsync(
        Guid eventId,
        Guid externalSystemId,
        string leaseOwner,
        DateTimeOffset deadLetteredAt,
        CancellationToken ct = default);

    Task<ConsentEventFeedPage> ReadFeedAsync(
        TenantId tenantId,
        long sinceSeq,
        int pageSize,
        Guid? externalSystemId = null,
        DateTimeOffset? retentionCutoff = null,
        CancellationToken ct = default);

    Task<int> PurgeExpiredEventsAsync(
        DateTimeOffset olderThan,
        CancellationToken ct = default);
}

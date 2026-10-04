namespace Autheris.Application.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Persistent outbox repository managing transactional ITSM ticket requests and webhook operations
/// with exponential backoff and dead-letter queue (DLQ) support.
/// </summary>
public interface IItsmOutboxRepository
{
    Task EnqueueAsync(ItsmOutboxMessage message, CancellationToken ct = default);

    Task<IReadOnlyList<ItsmOutboxMessage>> GetPendingMessagesAsync(int batchSize = 20, CancellationToken ct = default);

    Task MarkCompletedAsync(string id, string? ticketId = null, CancellationToken ct = default);

    Task MarkFailedAsync(string id, string errorMessage, TimeSpan nextRetryDelay, bool deadLetter = false, CancellationToken ct = default);

    Task<IReadOnlyList<ItsmOutboxMessage>> GetDeadLetterMessagesAsync(int limit = 50, CancellationToken ct = default);
}

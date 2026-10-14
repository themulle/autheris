namespace Autheris.Application.Webhooks;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Durable webhook outbox (WP-ARC-A-02). Time is always passed in (from a <see cref="TimeProvider"/>); the database clock is never
/// used, so multi-node deployments rely on NTP-synchronised nodes. Completion calls are fenced by the lease owner so a stale worker
/// cannot overwrite a row another worker reclaimed.
/// </summary>
public interface IWebhookOutbox
{
    /// <summary>Stores a row. Returns <c>false</c> when (source, subscription, event) already exists (dedupe).</summary>
    Task<bool> EnqueueAsync(WebhookOutboxMessage message, CancellationToken ct = default);

    /// <summary>
    /// Atomically leases up to <paramref name="limit"/> due rows (Pending and due, or InFlight with an expired lease) and increments
    /// their attempt counter, so a crash during delivery still counts as an attempt.
    /// </summary>
    Task<IReadOnlyList<WebhookOutboxMessage>> ClaimBatchAsync(int limit, DateTimeOffset now, TimeSpan lease, string leaseOwner, CancellationToken ct = default);

    /// <summary>Marks the row delivered. <c>false</c> when the caller no longer holds the lease.</summary>
    Task<bool> CompleteAsync(string id, string leaseOwner, int statusCode, CancellationToken ct = default);

    /// <summary>Returns the row to Pending at <paramref name="nextAttemptAt"/>. <c>false</c> when the caller no longer holds the lease.</summary>
    Task<bool> RescheduleAsync(string id, string leaseOwner, DateTimeOffset nextAttemptAt, int? statusCode, string errorCode, CancellationToken ct = default);

    /// <summary>Marks the row dead. <c>false</c> when the caller no longer holds the lease.</summary>
    Task<bool> DeadLetterAsync(string id, string leaseOwner, int? statusCode, string errorCode, CancellationToken ct = default);

    /// <summary>Circuit breaker: pushes the next attempt of all Pending rows of a subscription to <paramref name="until"/> (cluster-wide, no attempt is consumed).</summary>
    Task<int> DeferSubscriptionAsync(WebhookSubscriptionSource source, string subscriptionId, DateTimeOffset until, CancellationToken ct = default);

    /// <summary>Dead letters of exactly one owner scope (a tenant, or cluster-wide subscriptions); never all tenants. No payloads.</summary>
    Task<IReadOnlyList<WebhookDeadLetter>> ListDeadLettersAsync(WebhookOwnerScope scope, int limit, CancellationToken ct = default);

    /// <summary>Number of undelivered rows (Pending and InFlight).</summary>
    Task<long> CountPendingAsync(CancellationToken ct = default);

    /// <summary>Removes delivered rows last touched before <paramref name="deliveredBefore"/> and dead rows before <paramref name="deadBefore"/>.</summary>
    Task<int> PurgeAsync(DateTimeOffset deliveredBefore, DateTimeOffset deadBefore, CancellationToken ct = default);
}

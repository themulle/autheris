namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;

/// <summary>
/// SQLite <see cref="IWebhookOutbox"/> (WP-ARC-A-02). Every statement runs on a per-operation connection: claim, complete, reschedule,
/// dead-letter, defer, enqueue and purge are writes under the single write gate; list and count are reads.
/// </summary>
public partial class SqliteGovernanceRepository : IWebhookOutbox
{
    /// <summary>Dialect SQL of the webhook outbox (ARCH-MNT-22). Literals are moved verbatim; binding and mapping live in <see cref="WebhookOutboxSqlCore"/>.</summary>
    internal static class WebhookOutboxSql
    {
        // ON CONFLICT DO NOTHING (not INSERT OR IGNORE, which would also swallow NOT NULL violations).
        public const string Enqueue = $@"
                INSERT INTO WEBHOOK_OUTBOX ({WebhookOutboxMapping.Columns}, updated_ms)
                VALUES (@id, @source, @sub, @owner, @evTenant, @evId, @evType, @evSource, @evTime, @payload,
                        @attempt, @maxAttempts, @state, @next, NULL, NULL, NULL, NULL, @created, @created)
                ON CONFLICT (source, subscription_id, event_id) DO NOTHING;";

        public static string ClaimBatch() => $@"
                UPDATE WEBHOOK_OUTBOX
                   SET state = 1, lease_until_ms = @leaseUntil, lease_owner = @owner, attempt = attempt + 1, updated_ms = @now
                 WHERE id IN (
                        SELECT id FROM WEBHOOK_OUTBOX
                         WHERE (state = 0 AND next_attempt_ms <= @now) OR (state = 1 AND lease_until_ms <= @now)
                         ORDER BY next_attempt_ms
                         LIMIT @limit)
                RETURNING {WebhookOutboxMapping.Columns};";

        public static string FencedUpdate(string setClause) => $"UPDATE WEBHOOK_OUTBOX SET {setClause} WHERE id = @id AND state = 1 AND lease_owner = @owner;";

        public const string DeferSubscription = @"
                UPDATE WEBHOOK_OUTBOX SET next_attempt_ms = @until
                 WHERE state = 0 AND source = @source AND subscription_id = @sub AND next_attempt_ms < @until;";

        public static string ListDeadLetters(WebhookOwnerScope scope) => $@"
                SELECT {WebhookOutboxMapping.DeadLetterColumns} FROM WEBHOOK_OUTBOX
                 WHERE state = 3 AND {(scope.ClusterScope ? "owner_tenant_id IS NULL" : "owner_tenant_id = @tenant")}
                 ORDER BY created_ms DESC, id LIMIT @limit;";

        public const string CountPending = "SELECT COUNT(*) FROM WEBHOOK_OUTBOX WHERE state IN (0, 1);";

        public const string Purge = "DELETE FROM WEBHOOK_OUTBOX WHERE (state = 2 AND updated_ms < @delivered) OR (state = 3 AND updated_ms < @dead);";
    }

    public Task<bool> EnqueueAsync(WebhookOutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return WriteAsync(conn => WebhookOutboxSqlCore.EnqueueAsync(conn, WebhookOutboxSql.Enqueue, message, ct), ct);
    }

    public Task<IReadOnlyList<WebhookOutboxMessage>> ClaimBatchAsync(int limit, DateTimeOffset now, TimeSpan lease, string leaseOwner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        return WriteAsync(conn => WebhookOutboxSqlCore.ClaimBatchAsync(conn, WebhookOutboxSql.ClaimBatch(), limit, now, lease, leaseOwner, ct), ct);
    }

    public Task<bool> CompleteAsync(string id, string leaseOwner, int statusCode, DateTimeOffset now, CancellationToken ct = default) =>
        FencedUpdateAsync(
            "state = 2, lease_until_ms = NULL, last_status_code = @status, last_error_code = NULL, updated_ms = @now",
            id, leaseOwner, statusCode, null, null, now, ct);

    public Task<bool> RescheduleAsync(string id, string leaseOwner, DateTimeOffset nextAttemptAt, int? statusCode, string errorCode, DateTimeOffset now, CancellationToken ct = default) =>
        FencedUpdateAsync(
            "state = 0, lease_until_ms = NULL, lease_owner = NULL, next_attempt_ms = @next, last_status_code = @status, last_error_code = @error, updated_ms = @now",
            id, leaseOwner, statusCode, errorCode, WebhookOutboxMapping.Ms(nextAttemptAt), now, ct);

    public Task<bool> DeadLetterAsync(string id, string leaseOwner, int? statusCode, string errorCode, DateTimeOffset now, CancellationToken ct = default) =>
        FencedUpdateAsync(
            "state = 3, lease_until_ms = NULL, last_status_code = @status, last_error_code = @error, updated_ms = @now",
            id, leaseOwner, statusCode, errorCode, null, now, ct);

    private Task<bool> FencedUpdateAsync(string setClause, string id, string leaseOwner, int? statusCode, string? errorCode, long? next, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        return WriteAsync(
            conn => WebhookOutboxSqlCore.FencedUpdateAsync(conn, WebhookOutboxSql.FencedUpdate(setClause), id, leaseOwner, new WebhookFencedValues(setClause, statusCode, errorCode, next, now), BindFencedUpdate, ct),
            ct);
    }

    // D5 (ARCH-MNT-22): SQLite binds @status, @error and @next unconditionally; PostgreSQL and SQL Server bind only what the set clause uses.
    internal static void BindFencedUpdate(DbCommand cmd, WebhookFencedValues v)
    {
        cmd.AddBound("@status", (object?)v.StatusCode ?? DBNull.Value);
        cmd.AddBound("@error", (object?)v.ErrorCode ?? DBNull.Value);
        cmd.AddBound("@next", (object?)v.Next ?? DBNull.Value);
        cmd.AddBound("@now", WebhookOutboxMapping.Ms(v.Now));
    }

    public Task<int> DeferSubscriptionAsync(WebhookSubscriptionSource source, string subscriptionId, DateTimeOffset until, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        return WriteAsync(conn => WebhookOutboxSqlCore.DeferSubscriptionAsync(conn, WebhookOutboxSql.DeferSubscription, source, subscriptionId, until, ct), ct);
    }

    public Task<IReadOnlyList<WebhookDeadLetter>> ListDeadLettersAsync(WebhookOwnerScope scope, int limit, CancellationToken ct = default)
    {
        WebhookOutboxMapping.ValidateScope(scope);
        return ReadAsync(conn => WebhookOutboxSqlCore.ListDeadLettersAsync(conn, WebhookOutboxSql.ListDeadLetters(scope), scope, limit, ct), ct);
    }

    public Task<long> CountPendingAsync(CancellationToken ct = default) =>
        ReadAsync(conn => WebhookOutboxSqlCore.CountPendingAsync(conn, WebhookOutboxSql.CountPending, ct), ct);

    public Task<int> PurgeAsync(DateTimeOffset deliveredBefore, DateTimeOffset deadBefore, CancellationToken ct = default) =>
        WriteAsync(conn => WebhookOutboxSqlCore.PurgeAsync(conn, WebhookOutboxSql.Purge, deliveredBefore, deadBefore, ct), ct);
}

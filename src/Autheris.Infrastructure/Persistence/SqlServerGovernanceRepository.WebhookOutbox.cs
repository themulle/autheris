namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Microsoft.Data.SqlClient;

/// <summary>SQL Server <see cref="IWebhookOutbox"/> (WP-ARC-A-02): claims with <c>UPDLOCK, READPAST</c>, so workers on any node get disjoint batches.</summary>
public partial class SqlServerGovernanceRepository : IWebhookOutbox
{
    /// <summary>Dialect SQL of the webhook outbox (ARCH-MNT-22). Literals are moved verbatim; binding and mapping live in <see cref="WebhookOutboxSqlCore"/>.</summary>
    internal static class WebhookOutboxSql
    {
        // UPDLOCK + HOLDLOCK serialises concurrent inserts of the same key; the unique index is the final guard.
        public const string Enqueue = $@"
            INSERT INTO dbo.WEBHOOK_OUTBOX ({WebhookOutboxMapping.Columns}, updated_ms)
            SELECT @id, @source, @sub, @owner, @evTenant, @evId, @evType, @evSource, @evTime, @payload,
                   @attempt, @maxAttempts, @state, @next, NULL, NULL, NULL, NULL, @created, @created
             WHERE NOT EXISTS (SELECT 1 FROM dbo.WEBHOOK_OUTBOX WITH (UPDLOCK, HOLDLOCK)
                                WHERE source = @source AND subscription_id = @sub AND event_id = @evId);";

        public static string ClaimBatch() => $@"
            WITH picked AS (
                SELECT TOP (@limit) * FROM dbo.WEBHOOK_OUTBOX WITH (UPDLOCK, READPAST, ROWLOCK)
                 WHERE (state = 0 AND next_attempt_ms <= @now) OR (state = 1 AND lease_until_ms <= @now)
                 ORDER BY next_attempt_ms)
            UPDATE picked
               SET state = 1, lease_until_ms = @leaseUntil, lease_owner = @owner, attempt = attempt + 1, updated_ms = @now
            OUTPUT {WebhookOutboxMapping.OutputInserted};";

        public static string FencedUpdate(string setClause) => $"UPDATE dbo.WEBHOOK_OUTBOX SET {setClause} WHERE id = @id AND state = 1 AND lease_owner = @owner;";

        public const string DeferSubscription = @"
            UPDATE dbo.WEBHOOK_OUTBOX SET next_attempt_ms = @until
             WHERE state = 0 AND source = @source AND subscription_id = @sub AND next_attempt_ms < @until;";

        public static string ListDeadLetters(WebhookOwnerScope scope) => $@"
            SELECT {WebhookOutboxMapping.DeadLetterColumns} FROM dbo.WEBHOOK_OUTBOX
             WHERE state = 3 AND {(scope.ClusterScope ? "owner_tenant_id IS NULL" : "owner_tenant_id = @tenant")}
             ORDER BY created_ms DESC, id OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY;";

        public const string CountPending = "SELECT COUNT(*) FROM dbo.WEBHOOK_OUTBOX WHERE state IN (0, 1);";

        public const string Purge = "DELETE FROM dbo.WEBHOOK_OUTBOX WHERE (state = 2 AND updated_ms < @delivered) OR (state = 3 AND updated_ms < @dead);";
    }

    public async Task<bool> EnqueueAsync(WebhookOutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            return await WebhookOutboxSqlCore.EnqueueAsync(conn, WebhookOutboxSql.Enqueue, message, ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            return false; // lost the race against a concurrent identical enqueue
        }
    }

    public async Task<IReadOnlyList<WebhookOutboxMessage>> ClaimBatchAsync(int limit, DateTimeOffset now, TimeSpan lease, string leaseOwner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.ClaimBatchAsync(conn, WebhookOutboxSql.ClaimBatch(), limit, now, lease, leaseOwner, ct).ConfigureAwait(false);
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

    private async Task<bool> FencedUpdateAsync(string setClause, string id, string leaseOwner, int? statusCode, string? errorCode, long? next, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.FencedUpdateAsync(
            conn, WebhookOutboxSql.FencedUpdate(setClause), id, leaseOwner, new WebhookFencedValues(setClause, statusCode, errorCode, next, now), BindFencedUpdate, ct).ConfigureAwait(false);
    }

    // D5 (ARCH-MNT-22): SQL Server binds only the parameters the set clause uses (SQLite binds all unconditionally).
    internal static void BindFencedUpdate(DbCommand cmd, WebhookFencedValues v)
    {
        if (v.SetClause.Contains("@status", StringComparison.Ordinal))
        {
            cmd.AddBound("@status", (object?)v.StatusCode ?? DBNull.Value);
        }

        if (v.SetClause.Contains("@error", StringComparison.Ordinal))
        {
            cmd.AddBound("@error", (object?)v.ErrorCode ?? DBNull.Value);
        }

        cmd.AddBound("@now", WebhookOutboxMapping.Ms(v.Now));
        if (v.SetClause.Contains("@next", StringComparison.Ordinal))
        {
            cmd.AddBound("@next", v.Next!.Value);
        }
    }

    public async Task<int> DeferSubscriptionAsync(WebhookSubscriptionSource source, string subscriptionId, DateTimeOffset until, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.DeferSubscriptionAsync(conn, WebhookOutboxSql.DeferSubscription, source, subscriptionId, until, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WebhookDeadLetter>> ListDeadLettersAsync(WebhookOwnerScope scope, int limit, CancellationToken ct = default)
    {
        WebhookOutboxMapping.ValidateScope(scope);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.ListDeadLettersAsync(conn, WebhookOutboxSql.ListDeadLetters(scope), scope, limit, ct).ConfigureAwait(false);
    }

    public async Task<long> CountPendingAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.CountPendingAsync(conn, WebhookOutboxSql.CountPending, ct).ConfigureAwait(false);
    }

    public async Task<int> PurgeAsync(DateTimeOffset deliveredBefore, DateTimeOffset deadBefore, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.PurgeAsync(conn, WebhookOutboxSql.Purge, deliveredBefore, deadBefore, ct).ConfigureAwait(false);
    }
}

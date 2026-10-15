namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Npgsql;

/// <summary>PostgreSQL <see cref="IWebhookOutbox"/> (WP-ARC-A-02): claims with <c>FOR UPDATE SKIP LOCKED</c>, so workers on any node get disjoint batches.</summary>
public partial class PostgreSqlGovernanceRepository : IWebhookOutbox
{
    public async Task<bool> EnqueueAsync(WebhookOutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO WEBHOOK_OUTBOX ({WebhookOutboxMapping.Columns}, updated_ms)
            VALUES (@id, @source, @sub, @owner, @evTenant, @evId, @evType, @evSource, @evTime, @payload,
                    @attempt, @maxAttempts, @state, @next, NULL, NULL, NULL, NULL, @created, @created)
            ON CONFLICT (source, subscription_id, event_id) DO NOTHING;";
        cmd.Parameters.AddWithValue("@id", message.Id);
        cmd.Parameters.AddWithValue("@source", (int)message.Source);
        cmd.Parameters.AddWithValue("@sub", message.SubscriptionId);
        cmd.Parameters.AddWithValue("@owner", (object?)message.OwnerTenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@evTenant", (object?)message.EventTenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@evId", message.EventId);
        cmd.Parameters.AddWithValue("@evType", message.EventType);
        cmd.Parameters.AddWithValue("@evSource", message.EventSource);
        cmd.Parameters.AddWithValue("@evTime", WebhookOutboxMapping.Ms(message.EventTime));
        cmd.Parameters.AddWithValue("@payload", message.PayloadJson);
        cmd.Parameters.AddWithValue("@attempt", message.Attempt);
        cmd.Parameters.AddWithValue("@maxAttempts", message.MaxAttempts);
        cmd.Parameters.AddWithValue("@state", (int)WebhookOutboxState.Pending);
        cmd.Parameters.AddWithValue("@next", WebhookOutboxMapping.Ms(message.NextAttemptAt));
        cmd.Parameters.AddWithValue("@created", WebhookOutboxMapping.Ms(message.CreatedAt));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<WebhookOutboxMessage>> ClaimBatchAsync(int limit, DateTimeOffset now, TimeSpan lease, string leaseOwner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            WITH picked AS (
                SELECT id FROM WEBHOOK_OUTBOX
                 WHERE (state = 0 AND next_attempt_ms <= @now) OR (state = 1 AND lease_until_ms <= @now)
                 ORDER BY next_attempt_ms
                 LIMIT @limit
                 FOR UPDATE SKIP LOCKED)
            UPDATE WEBHOOK_OUTBOX o
               SET state = 1, lease_until_ms = @leaseUntil, lease_owner = @owner, attempt = o.attempt + 1, updated_ms = @now
              FROM picked
             WHERE o.id = picked.id
            RETURNING {WebhookOutboxMapping.ReturningAliasO};";
        cmd.Parameters.AddWithValue("@now", WebhookOutboxMapping.Ms(now));
        cmd.Parameters.AddWithValue("@leaseUntil", WebhookOutboxMapping.Ms(now + lease));
        cmd.Parameters.AddWithValue("@owner", leaseOwner);
        cmd.Parameters.AddWithValue("@limit", WebhookOutboxMapping.ClampLimit(limit));

        var rows = new List<WebhookOutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(WebhookOutboxMapping.ReadMessage(reader));
        }

        return WebhookOutboxMapping.SortByNextAttempt(rows);
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
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE WEBHOOK_OUTBOX SET {setClause} WHERE id = @id AND state = 1 AND lease_owner = @owner;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@owner", leaseOwner);
        if (setClause.Contains("@status", StringComparison.Ordinal))
        {
            cmd.Parameters.Add(new NpgsqlParameter("@status", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)statusCode ?? DBNull.Value });
        }

        if (setClause.Contains("@error", StringComparison.Ordinal))
        {
            cmd.Parameters.AddWithValue("@error", (object?)errorCode ?? DBNull.Value);
        }

        cmd.Parameters.AddWithValue("@now", WebhookOutboxMapping.Ms(now));
        if (setClause.Contains("@next", StringComparison.Ordinal))
        {
            cmd.Parameters.AddWithValue("@next", next!.Value);
        }

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<int> DeferSubscriptionAsync(WebhookSubscriptionSource source, string subscriptionId, DateTimeOffset until, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE WEBHOOK_OUTBOX SET next_attempt_ms = @until
             WHERE state = 0 AND source = @source AND subscription_id = @sub AND next_attempt_ms < @until;";
        cmd.Parameters.AddWithValue("@until", WebhookOutboxMapping.Ms(until));
        cmd.Parameters.AddWithValue("@source", (int)source);
        cmd.Parameters.AddWithValue("@sub", subscriptionId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WebhookDeadLetter>> ListDeadLettersAsync(WebhookOwnerScope scope, int limit, CancellationToken ct = default)
    {
        WebhookOutboxMapping.ValidateScope(scope);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {WebhookOutboxMapping.DeadLetterColumns} FROM WEBHOOK_OUTBOX
             WHERE state = 3 AND {(scope.ClusterScope ? "owner_tenant_id IS NULL" : "owner_tenant_id = @tenant")}
             ORDER BY created_ms DESC, id LIMIT @limit;";
        if (!scope.ClusterScope)
        {
            cmd.Parameters.AddWithValue("@tenant", scope.TenantId!);
        }

        cmd.Parameters.AddWithValue("@limit", WebhookOutboxMapping.ClampLimit(limit));
        var rows = new List<WebhookDeadLetter>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(WebhookOutboxMapping.ReadDeadLetter(reader));
        }

        return rows;
    }

    public async Task<long> CountPendingAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM WEBHOOK_OUTBOX WHERE state IN (0, 1);";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<int> PurgeAsync(DateTimeOffset deliveredBefore, DateTimeOffset deadBefore, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM WEBHOOK_OUTBOX WHERE (state = 2 AND updated_ms < @delivered) OR (state = 3 AND updated_ms < @dead);";
        cmd.Parameters.AddWithValue("@delivered", WebhookOutboxMapping.Ms(deliveredBefore));
        cmd.Parameters.AddWithValue("@dead", WebhookOutboxMapping.Ms(deadBefore));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

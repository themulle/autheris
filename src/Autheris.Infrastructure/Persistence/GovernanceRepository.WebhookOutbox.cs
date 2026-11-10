namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Data.SqlClient;

public partial class GovernanceRepository : IWebhookOutbox
{
    internal static class WebhookOutboxPostgreSql
    {
        public static string Enqueue => $@"
            INSERT INTO WEBHOOK_OUTBOX ({WebhookOutboxMapping.Columns}, updated_ms)
            VALUES (@id, @source, @sub, @owner, @evTenant, @evId, @evType, @evSource, @evTime, @payload,
                    @attempt, @maxAttempts, @state, @next, NULL, NULL, NULL, NULL, @created, @created)
            ON CONFLICT (source, subscription_id, event_id) DO NOTHING;";

        public static string ClaimBatch() => $@"
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

        public static string FencedUpdate(string setClause) => $"UPDATE WEBHOOK_OUTBOX SET {setClause} WHERE id = @id AND state = 1 AND lease_owner = @owner;";

        public static string DeferSubscription => @"
            UPDATE WEBHOOK_OUTBOX SET next_attempt_ms = @until
             WHERE state = 0 AND source = @source AND subscription_id = @sub AND next_attempt_ms < @until;";

        public static string ListDeadLetters(WebhookOwnerScope scope) => $@"
            SELECT {WebhookOutboxMapping.DeadLetterColumns} FROM WEBHOOK_OUTBOX
             WHERE state = 3 AND {(scope.ClusterScope ? "owner_tenant_id IS NULL" : "owner_tenant_id = @tenant")}
             ORDER BY created_ms DESC, id LIMIT @limit;";

        public static string CountPending => "SELECT COUNT(*) FROM WEBHOOK_OUTBOX WHERE state IN (0, 1);";

        public static string Purge => "DELETE FROM WEBHOOK_OUTBOX WHERE (state = 2 AND updated_ms < @delivered) OR (state = 3 AND updated_ms < @dead);";

        public static void BindFencedUpdate(DbCommand cmd, WebhookFencedValues v)
        {
            if (v.SetClause.Contains("@status", StringComparison.Ordinal))
            {
                cmd.Parameters.Add(new Npgsql.NpgsqlParameter("@status", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)v.StatusCode ?? DBNull.Value });
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
    }

    internal static class WebhookOutboxSqlServer
    {
        public static string Enqueue => $@"
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

        public static string DeferSubscription => @"
            UPDATE dbo.WEBHOOK_OUTBOX SET next_attempt_ms = @until
             WHERE state = 0 AND source = @source AND subscription_id = @sub AND next_attempt_ms < @until;";

        public static string ListDeadLetters(WebhookOwnerScope scope) => $@"
            SELECT {WebhookOutboxMapping.DeadLetterColumns} FROM dbo.WEBHOOK_OUTBOX
             WHERE state = 3 AND {(scope.ClusterScope ? "owner_tenant_id IS NULL" : "owner_tenant_id = @tenant")}
             ORDER BY created_ms DESC, id OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY;";

        public static string CountPending => "SELECT COUNT(*) FROM dbo.WEBHOOK_OUTBOX WHERE state IN (0, 1);";

        public static string Purge => "DELETE FROM dbo.WEBHOOK_OUTBOX WHERE (state = 2 AND updated_ms < @delivered) OR (state = 3 AND updated_ms < @dead);";

        public static void BindFencedUpdate(DbCommand cmd, WebhookFencedValues v)
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
    }

    public async Task<bool> EnqueueAsync(WebhookOutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var isPg = _dialect is PostgreSqlGovernanceDialect;
        var sql = isPg ? WebhookOutboxPostgreSql.Enqueue : WebhookOutboxSqlServer.Enqueue;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        if (isPg)
        {
            return await WebhookOutboxSqlCore.EnqueueAsync(conn, sql, message, ct).ConfigureAwait(false);
        }

        try
        {
            return await WebhookOutboxSqlCore.EnqueueAsync(conn, sql, message, ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<WebhookOutboxMessage>> ClaimBatchAsync(int limit, DateTimeOffset now, TimeSpan lease, string leaseOwner, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);

        var sql = _dialect is PostgreSqlGovernanceDialect ? WebhookOutboxPostgreSql.ClaimBatch() : WebhookOutboxSqlServer.ClaimBatch();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.ClaimBatchAsync(conn, sql, limit, now, lease, leaseOwner, ct).ConfigureAwait(false);
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

        var isPg = _dialect is PostgreSqlGovernanceDialect;
        var sql = isPg ? WebhookOutboxPostgreSql.FencedUpdate(setClause) : WebhookOutboxSqlServer.FencedUpdate(setClause);
        var binder = isPg ? (Action<DbCommand, WebhookFencedValues>)WebhookOutboxPostgreSql.BindFencedUpdate : WebhookOutboxSqlServer.BindFencedUpdate;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.FencedUpdateAsync(
            conn, sql, id, leaseOwner, new WebhookFencedValues(setClause, statusCode, errorCode, next, now), binder, ct).ConfigureAwait(false);
    }

    public async Task<int> DeferSubscriptionAsync(WebhookSubscriptionSource source, string subscriptionId, DateTimeOffset until, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var sql = _dialect is PostgreSqlGovernanceDialect ? WebhookOutboxPostgreSql.DeferSubscription : WebhookOutboxSqlServer.DeferSubscription;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.DeferSubscriptionAsync(conn, sql, source, subscriptionId, until, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WebhookDeadLetter>> ListDeadLettersAsync(WebhookOwnerScope scope, int limit, CancellationToken ct = default)
    {
        WebhookOutboxMapping.ValidateScope(scope);

        var sql = _dialect is PostgreSqlGovernanceDialect ? WebhookOutboxPostgreSql.ListDeadLetters(scope) : WebhookOutboxSqlServer.ListDeadLetters(scope);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.ListDeadLettersAsync(conn, sql, scope, limit, ct).ConfigureAwait(false);
    }

    public async Task<long> CountPendingAsync(CancellationToken ct = default)
    {
        var sql = _dialect is PostgreSqlGovernanceDialect ? WebhookOutboxPostgreSql.CountPending : WebhookOutboxSqlServer.CountPending;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.CountPendingAsync(conn, sql, ct).ConfigureAwait(false);
    }

    public async Task<int> PurgeAsync(DateTimeOffset deliveredBefore, DateTimeOffset deadBefore, CancellationToken ct = default)
    {
        var sql = _dialect is PostgreSqlGovernanceDialect ? WebhookOutboxPostgreSql.Purge : WebhookOutboxSqlServer.Purge;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await WebhookOutboxSqlCore.PurgeAsync(conn, sql, deliveredBefore, deadBefore, ct).ConfigureAwait(false);
    }
}

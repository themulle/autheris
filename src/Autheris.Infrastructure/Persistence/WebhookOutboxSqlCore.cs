namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Webhooks;
using Autheris.Domain.Model;

/// <summary>Values of one fenced webhook outbox update; the dialect binder decides which of them reach the command (divergence D5).</summary>
internal readonly record struct WebhookFencedValues(string SetClause, int? StatusCode, string? ErrorCode, long? Next, DateTimeOffset Now);

/// <summary>
/// Engine-neutral binding, execution and row mapping of the webhook outbox (ARCH-MNT-22). The dialect repositories own the SQL text, the
/// connection or write-gate acquisition, the SQL Server unique-violation catch and the dialect-specific fenced-update binding; this class
/// receives an open connection and never opens, commits, rolls back or disposes it. Tenant scoping stays in the SQL the dialect hands in:
/// the dead-letter list keeps <c>owner_tenant_id = @tenant</c> (or <c>owner_tenant_id IS NULL</c> for the cluster scope), the claim and
/// fence statements are system-queue statements keyed by id and lease owner, exactly as before. Every read is bounded by the clamped limit.
/// </summary>
internal static class WebhookOutboxSqlCore
{
    public static async Task<bool> EnqueueAsync(DbConnection conn, string sql, WebhookOutboxMessage message, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@id", message.Id);
        cmd.AddBound("@source", (int)message.Source);
        cmd.AddBound("@sub", message.SubscriptionId);
        cmd.AddBound("@owner", (object?)message.OwnerTenantId ?? DBNull.Value);
        cmd.AddBound("@evTenant", (object?)message.EventTenantId ?? DBNull.Value);
        cmd.AddBound("@evId", message.EventId);
        cmd.AddBound("@evType", message.EventType);
        cmd.AddBound("@evSource", message.EventSource);
        cmd.AddBound("@evTime", WebhookOutboxMapping.Ms(message.EventTime));
        cmd.AddBound("@payload", message.PayloadJson);
        cmd.AddBound("@attempt", message.Attempt);
        cmd.AddBound("@maxAttempts", message.MaxAttempts);
        cmd.AddBound("@state", (int)WebhookOutboxState.Pending);
        cmd.AddBound("@next", WebhookOutboxMapping.Ms(message.NextAttemptAt));
        cmd.AddBound("@created", WebhookOutboxMapping.Ms(message.CreatedAt));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public static async Task<IReadOnlyList<WebhookOutboxMessage>> ClaimBatchAsync(
        DbConnection conn, string sql, int limit, DateTimeOffset now, TimeSpan lease, string leaseOwner, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@now", WebhookOutboxMapping.Ms(now));
        cmd.AddBound("@leaseUntil", WebhookOutboxMapping.Ms(now + lease));
        cmd.AddBound("@owner", leaseOwner);
        cmd.AddBound("@limit", WebhookOutboxMapping.ClampLimit(limit));

        var rows = new List<WebhookOutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(WebhookOutboxMapping.ReadMessage(reader));
        }

        return WebhookOutboxMapping.SortByNextAttempt(rows);
    }

    /// <summary>Runs the fenced update: binds <c>@id</c> and <c>@owner</c>, then lets the dialect <paramref name="bindRest"/> bind the remaining values.</summary>
    public static async Task<bool> FencedUpdateAsync(
        DbConnection conn, string sql, string id, string leaseOwner, WebhookFencedValues values, Action<DbCommand, WebhookFencedValues> bindRest, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@id", id);
        cmd.AddBound("@owner", leaseOwner);
        bindRest(cmd, values);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public static async Task<int> DeferSubscriptionAsync(
        DbConnection conn, string sql, WebhookSubscriptionSource source, string subscriptionId, DateTimeOffset until, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@until", WebhookOutboxMapping.Ms(until));
        cmd.AddBound("@source", (int)source);
        cmd.AddBound("@sub", subscriptionId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<WebhookDeadLetter>> ListDeadLettersAsync(
        DbConnection conn, string sql, WebhookOwnerScope scope, int limit, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (!scope.ClusterScope)
        {
            cmd.AddBound("@tenant", scope.TenantId!);
        }

        cmd.AddBound("@limit", WebhookOutboxMapping.ClampLimit(limit));
        var rows = new List<WebhookDeadLetter>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(WebhookOutboxMapping.ReadDeadLetter(reader));
        }

        return rows;
    }

    public static async Task<long> CountPendingAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public static async Task<int> PurgeAsync(DbConnection conn, string sql, DateTimeOffset deliveredBefore, DateTimeOffset deadBefore, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@delivered", WebhookOutboxMapping.Ms(deliveredBefore));
        cmd.AddBound("@dead", WebhookOutboxMapping.Ms(deadBefore));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

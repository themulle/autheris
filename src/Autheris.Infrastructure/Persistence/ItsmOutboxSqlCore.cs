namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Engine-neutral binding, execution and row mapping of the ITSM outbox (ARCH-MNT-22). The dialect repositories own the SQL text, the
/// connection or write-gate acquisition and every decision; this class receives an open connection and never opens, commits, rolls back or
/// disposes it. Tenant filter exemption (ARCH-MNT-22-SC-04 b): <c>ITSM_OUTBOX</c> is a system queue drained by the dispatcher across all
/// tenants, so none of its statements carries a tenant predicate. Reads stay bounded by the caller-supplied, clamped <c>LIMIT/TOP</c>.
/// </summary>
internal static class ItsmOutboxSqlCore
{
    public static async Task EnqueueAsync(DbConnection conn, string sql, ItsmOutboxMessage message, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        cmd.AddBound("@id", message.Id);
        cmd.AddBound("@requestId", message.RequestId);
        cmd.AddBound("@tenantId", message.TenantId);
        cmd.AddBound("@eventType", message.EventType);
        cmd.AddBound("@payloadJson", message.PayloadJson);
        cmd.AddBound("@preferredSystem", message.PreferredSystem);
        cmd.AddBound("@status", (int)message.Status);
        cmd.AddBound("@retryCount", message.RetryCount);
        cmd.AddBound("@maxRetries", message.MaxRetries);
        cmd.AddBound("@createdAt", message.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.AddBound("@nextRetryAt", message.NextRetryAt.HasValue
            ? message.NextRetryAt.Value.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
        cmd.AddBound("@lastError", (object?)message.LastError ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<ItsmOutboxMessage>> GetPendingAsync(DbConnection conn, string sql, int batchSize, TimeProvider clock, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var nowStr = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

        cmd.CommandText = sql;

        cmd.AddBound("@now", nowStr);
        cmd.AddBound("@batchSize", Math.Max(1, batchSize));

        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    public static async Task MarkCompletedAsync(DbConnection conn, string sql, string id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        cmd.AddBound("@status", (int)ItsmOutboxStatus.Completed);
        cmd.AddBound("@id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task MarkFailedAsync(
        DbConnection conn, string sql, string id, string errorMessage, TimeSpan nextRetryDelay, bool deadLetter, TimeProvider clock, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var nextRetry = clock.GetUtcNow().Add(nextRetryDelay).ToString("O", CultureInfo.InvariantCulture);

        cmd.CommandText = sql;

        cmd.AddBound("@status", deadLetter ? (int)ItsmOutboxStatus.DeadLetter : (int)ItsmOutboxStatus.Pending);
        cmd.AddBound("@nextRetryAt", deadLetter ? DBNull.Value : nextRetry);
        cmd.AddBound("@lastError", errorMessage ?? "Unknown error");
        cmd.AddBound("@id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<ItsmOutboxMessage>> GetDeadLettersAsync(DbConnection conn, string sql, int limit, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        cmd.AddBound("@status", (int)ItsmOutboxStatus.DeadLetter);
        cmd.AddBound("@limit", Math.Max(1, limit));

        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ItsmOutboxMessage>> ReadAllAsync(DbCommand cmd, CancellationToken ct)
    {
        var list = new List<ItsmOutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadMessage(reader));
        }

        return list;
    }

    internal static ItsmOutboxMessage ReadMessage(DbDataReader reader)
    {
        var id = reader.GetString(0);
        var reqId = reader.GetString(1);
        var tenantId = reader.GetString(2);
        var eventType = reader.GetString(3);
        var payloadJson = reader.GetString(4);
        var prefSystem = reader.GetString(5);
        var status = (ItsmOutboxStatus)reader.GetInt32(6);
        var retryCount = reader.GetInt32(7);
        var maxRetries = reader.GetInt32(8);
        var createdAt = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        DateTimeOffset? nextRetryAt = reader.IsDBNull(10)
            ? null
            : DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var lastError = reader.IsDBNull(11) ? null : reader.GetString(11);

        return new ItsmOutboxMessage(
            id,
            reqId,
            tenantId,
            eventType,
            payloadJson,
            prefSystem,
            status,
            retryCount,
            maxRetries,
            createdAt,
            nextRetryAt,
            lastError);
    }
}

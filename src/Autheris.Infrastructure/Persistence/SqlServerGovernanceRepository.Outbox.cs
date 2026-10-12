using System.Globalization;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Data.SqlClient;

namespace Autheris.Infrastructure.Persistence;

public partial class SqlServerGovernanceRepository : IItsmOutboxRepository
{
    public async Task EnqueueAsync(ItsmOutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ITSM_OUTBOX (
                id, request_id, tenant_id, event_type, payload_json, preferred_system,
                status, retry_count, max_retries, created_at, next_retry_at, last_error
            ) VALUES (
                @id, @requestId, @tenantId, @eventType, @payloadJson, @preferredSystem,
                @status, @retryCount, @maxRetries, @createdAt, @nextRetryAt, @lastError
            );";

        cmd.Parameters.AddWithValue("@id", message.Id);
        cmd.Parameters.AddWithValue("@requestId", message.RequestId);
        cmd.Parameters.AddWithValue("@tenantId", message.TenantId);
        cmd.Parameters.AddWithValue("@eventType", message.EventType);
        cmd.Parameters.AddWithValue("@payloadJson", message.PayloadJson);
        cmd.Parameters.AddWithValue("@preferredSystem", message.PreferredSystem);
        cmd.Parameters.AddWithValue("@status", (int)message.Status);
        cmd.Parameters.AddWithValue("@retryCount", message.RetryCount);
        cmd.Parameters.AddWithValue("@maxRetries", message.MaxRetries);
        cmd.Parameters.AddWithValue("@createdAt", message.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@nextRetryAt", message.NextRetryAt.HasValue
            ? (object)message.NextRetryAt.Value.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value);
        cmd.Parameters.AddWithValue("@lastError", (object?)message.LastError ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ItsmOutboxMessage>> GetPendingMessagesAsync(int batchSize = 20, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        var nowStr = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        // Plain read (no row locks), same as the PostgreSQL provider: SELECT TOP (@batchSize) instead of LIMIT.
        cmd.CommandText = @"
            SELECT TOP (@batchSize) id, request_id, tenant_id, event_type, payload_json, preferred_system,
                   status, retry_count, max_retries, created_at, next_retry_at, last_error
            FROM ITSM_OUTBOX
            WHERE status IN (0, 1)
              AND (next_retry_at IS NULL OR next_retry_at <= @now)
            ORDER BY created_at ASC;";

        cmd.Parameters.AddWithValue("@now", nowStr);
        cmd.Parameters.AddWithValue("@batchSize", Math.Max(1, batchSize));

        var list = new List<ItsmOutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadOutboxMessage(reader));
        }
        return list;
    }

    public async Task MarkCompletedAsync(string id, string? ticketId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE ITSM_OUTBOX
            SET status = @status,
                last_error = NULL
            WHERE id = @id;";

        cmd.Parameters.AddWithValue("@status", (int)ItsmOutboxStatus.Completed);
        cmd.Parameters.AddWithValue("@id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(string id, string errorMessage, TimeSpan nextRetryDelay, bool deadLetter = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        var nextRetry = DateTimeOffset.UtcNow.Add(nextRetryDelay).ToString("O", CultureInfo.InvariantCulture);

        cmd.CommandText = @"
            UPDATE ITSM_OUTBOX
            SET status = @status,
                retry_count = retry_count + 1,
                next_retry_at = @nextRetryAt,
                last_error = @lastError
            WHERE id = @id;";

        cmd.Parameters.AddWithValue("@status", deadLetter ? (int)ItsmOutboxStatus.DeadLetter : (int)ItsmOutboxStatus.Pending);
        cmd.Parameters.AddWithValue("@nextRetryAt", deadLetter ? DBNull.Value : (object)nextRetry);
        cmd.Parameters.AddWithValue("@lastError", errorMessage ?? "Unknown error");
        cmd.Parameters.AddWithValue("@id", id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ItsmOutboxMessage>> GetDeadLetterMessagesAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP (@limit) id, request_id, tenant_id, event_type, payload_json, preferred_system,
                   status, retry_count, max_retries, created_at, next_retry_at, last_error
            FROM ITSM_OUTBOX
            WHERE status = @status
            ORDER BY created_at DESC;";

        cmd.Parameters.AddWithValue("@status", (int)ItsmOutboxStatus.DeadLetter);
        cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        var list = new List<ItsmOutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadOutboxMessage(reader));
        }
        return list;
    }

    private static ItsmOutboxMessage ReadOutboxMessage(SqlDataReader reader)
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

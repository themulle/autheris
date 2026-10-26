using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository : IItsmOutboxRepository
{
    /// <summary>Dialect SQL of the ITSM outbox (ARCH-MNT-22). Literals are moved verbatim; the shared binding lives in <see cref="ItsmOutboxSqlCore"/>.</summary>
    internal static class ItsmOutboxSql
    {
        public const string Insert = @"
            INSERT INTO ITSM_OUTBOX (
                id, request_id, tenant_id, event_type, payload_json, preferred_system,
                status, retry_count, max_retries, created_at, next_retry_at, last_error
            ) VALUES (
                @id, @requestId, @tenantId, @eventType, @payloadJson, @preferredSystem,
                @status, @retryCount, @maxRetries, @createdAt, @nextRetryAt, @lastError
            );";

        public const string GetPending = @"
            SELECT id, request_id, tenant_id, event_type, payload_json, preferred_system,
                   status, retry_count, max_retries, created_at, next_retry_at, last_error
            FROM ITSM_OUTBOX
            WHERE status IN (0, 1)
              AND (next_retry_at IS NULL OR next_retry_at <= @now)
            ORDER BY created_at ASC
            LIMIT @batchSize;";

        public const string MarkCompleted = @"
            UPDATE ITSM_OUTBOX
            SET status = @status,
                last_error = NULL
            WHERE id = @id;";

        public const string MarkFailed = @"
            UPDATE ITSM_OUTBOX
            SET status = @status,
                retry_count = retry_count + 1,
                next_retry_at = @nextRetryAt,
                last_error = @lastError
            WHERE id = @id;";

        public const string GetDeadLetters = @"
            SELECT id, request_id, tenant_id, event_type, payload_json, preferred_system,
                   status, retry_count, max_retries, created_at, next_retry_at, last_error
            FROM ITSM_OUTBOX
            WHERE status = @status
            ORDER BY created_at DESC
            LIMIT @limit;";
    }

    public async Task EnqueueAsync(ItsmOutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ItsmOutboxSqlCore.EnqueueAsync(conn, ItsmOutboxSql.Insert, message, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ItsmOutboxMessage>> GetPendingMessagesAsync(int batchSize = 20, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ItsmOutboxSqlCore.GetPendingAsync(conn, ItsmOutboxSql.GetPending, batchSize, TimeProvider.System, ct).ConfigureAwait(false);
    }

    public async Task MarkCompletedAsync(string id, string? ticketId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ItsmOutboxSqlCore.MarkCompletedAsync(conn, ItsmOutboxSql.MarkCompleted, id, ct).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(string id, string errorMessage, TimeSpan nextRetryDelay, bool deadLetter = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ItsmOutboxSqlCore.MarkFailedAsync(conn, ItsmOutboxSql.MarkFailed, id, errorMessage, nextRetryDelay, deadLetter, TimeProvider.System, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ItsmOutboxMessage>> GetDeadLetterMessagesAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ItsmOutboxSqlCore.GetDeadLettersAsync(conn, ItsmOutboxSql.GetDeadLetters, limit, ct).ConfigureAwait(false);
    }
}

using Autheris.Domain.Model;

namespace Autheris.Domain.Audit;

/// <summary>
/// AU-10: Provider-neutral audit core for consistent escaping (\n, \r, \t, |)
/// and consistent HMAC-SHA256 entry hashing across PostgreSQL and SQL Server.
/// </summary>
public static class AuditCanonicalizer
{
    /// <summary>
    /// Escapes newlines (\n), carriage returns (\r), tabs (\t), backslashes (\) and delimiter pipes (|)
    /// to guarantee field separation and tamper resistance across database providers.
    /// </summary>
    public static string CanonicalizeField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal)
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\t", "\\t", StringComparison.Ordinal)
                    .Replace("|", "\\p", StringComparison.Ordinal);
    }

    /// <summary>
    /// Computes the HMAC-SHA256 audit entry hash for an AuditLogEntry.
    /// </summary>
    public static string ComputeEntryHash(byte[] hmacKey, long? sequence, string prevHash, AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(hmacKey);
        ArgumentNullException.ThrowIfNull(entry);

        var tenantId = StoredTenant.RequireWritable(entry.TenantId);
        return ComputeEntryHash(
            hmacKey,
            sequence,
            entry.Id.ToString(),
            prevHash,
            entry.OccurredAt,
            entry.EventType,
            entry.ActorSid.Value,
            entry.TargetTable,
            entry.TargetColumn,
            entry.Decision,
            entry.TraceId,
            entry.DetailsJson,
            tenantId);
    }

    /// <summary>
    /// Builds the canonical v1 (unsequenced) or v2 (sequenced) audit payload string.
    /// </summary>
    public static string BuildCanonicalPayload(
        long? sequence,
        string id,
        string prevHash,
        DateTimeOffset occurredAt,
        string? eventType,
        string? actorSid,
        string? targetTable,
        string? targetColumn,
        string? decision,
        string? traceId,
        string? detailsJson,
        string? tenantId = null)
    {
        var payload = $"{id}|{prevHash}|{occurredAt:O}|{CanonicalizeField(eventType)}|{CanonicalizeField(actorSid)}|{CanonicalizeField(targetTable)}|{CanonicalizeField(targetColumn)}|{CanonicalizeField(decision)}|{CanonicalizeField(traceId)}|{CanonicalizeField(detailsJson)}|{CanonicalizeField(tenantId)}";
        if (sequence.HasValue)
        {
            payload = $"v2|{sequence.Value}|{payload}";
        }

        return payload;
    }

    /// <summary>
    /// Computes the HMAC-SHA256 entry hash over individual canonicalized fields.
    /// Supports v1 (unsequenced) and v2 (sequenced) payload layouts.
    /// </summary>
    public static string ComputeEntryHash(
        byte[] hmacKey,
        long? sequence,
        string id,
        string prevHash,
        DateTimeOffset occurredAt,
        string? eventType,
        string? actorSid,
        string? targetTable,
        string? targetColumn,
        string? decision,
        string? traceId,
        string? detailsJson,
        string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(hmacKey);

        return HashCanonicalPayload(hmacKey, sequence, id, prevHash, occurredAt, eventType, actorSid, targetTable, targetColumn, decision, traceId, detailsJson, tenantId);
    }

    /// <summary>
    /// Hashes the canonical payload (see <see cref="BuildCanonicalPayload"/>) without building the payload string: SHA-256 when
    /// <paramref name="hmacKey"/> is null, HMAC-SHA-256 otherwise (an empty key is still a key). Upper-case hex; the digest bytes are identical to hashing the UTF-8
    /// bytes of <see cref="BuildCanonicalPayload"/>.
    /// </summary>
    internal static string HashCanonicalPayload(
        byte[]? hmacKey,
        long? sequence,
        string id,
        string prevHash,
        DateTimeOffset occurredAt,
        string? eventType,
        string? actorSid,
        string? targetTable,
        string? targetColumn,
        string? decision,
        string? traceId,
        string? detailsJson,
        string? tenantId)
    {
        int estimate = 96 + id.Length + prevHash.Length
            + 2 * ((eventType?.Length ?? 0) + (actorSid?.Length ?? 0) + (targetTable?.Length ?? 0) + (targetColumn?.Length ?? 0)
                + (decision?.Length ?? 0) + (traceId?.Length ?? 0) + (detailsJson?.Length ?? 0) + (tenantId?.Length ?? 0));
        var writer = new CanonicalPayloadWriter(estimate);
        try
        {
            if (sequence.HasValue)
            {
                writer.Append("v2|");
                writer.AppendLong(sequence.Value);
                writer.Append('|');
            }

            writer.Append(id);
            writer.Append('|');
            writer.Append(prevHash);
            writer.Append('|');
            writer.AppendRoundTrip(occurredAt);
            writer.Append('|');
            writer.AppendCanonical(eventType);
            writer.Append('|');
            writer.AppendCanonical(actorSid);
            writer.Append('|');
            writer.AppendCanonical(targetTable);
            writer.Append('|');
            writer.AppendCanonical(targetColumn);
            writer.Append('|');
            writer.AppendCanonical(decision);
            writer.Append('|');
            writer.AppendCanonical(traceId);
            writer.Append('|');
            writer.AppendCanonical(detailsJson);
            writer.Append('|');
            writer.AppendCanonical(tenantId);
            return writer.HashToHex(hmacKey);
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// Returns lowercase representation of the entry hash.
    /// </summary>
    public static string ComputeEntryHashLower(
        byte[] hmacKey,
        long? sequence,
        string id,
        string prevHash,
        DateTimeOffset occurredAt,
        string? eventType,
        string? actorSid,
        string? targetTable,
        string? targetColumn,
        string? decision,
        string? traceId,
        string? detailsJson,
        string? tenantId) =>
        ComputeEntryHash(hmacKey, sequence, id, prevHash, occurredAt, eventType, actorSid, targetTable, targetColumn, decision, traceId, detailsJson, tenantId).ToLowerInvariant();
}

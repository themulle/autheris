using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Model;

namespace Autheris.Domain.Audit;

/// <summary>
/// AU-10: Anbieterneutraler Audit-Kern für einheitliches Escaping (\n, \r, \t, |)
/// und konsistentes HMAC-SHA256 Entry-Hashing über SQLite, PostgreSQL und SQL Server.
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

        return value.Replace("\\", "\\\\")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t")
                    .Replace("|", "\\p");
    }

    /// <summary>
    /// Computes the HMAC-SHA256 audit entry hash for an AuditLogEntry.
    /// </summary>
    public static string ComputeEntryHash(byte[] hmacKey, long? sequence, string prevHash, AuditLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(hmacKey);
        ArgumentNullException.ThrowIfNull(entry);

        var tenantId = string.IsNullOrWhiteSpace(entry.TenantId.Value) ? "legacy-single-tenant" : entry.TenantId.Value;
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

        var payload = $"{id}|{prevHash}|{occurredAt:O}|{CanonicalizeField(eventType)}|{CanonicalizeField(actorSid)}|{CanonicalizeField(targetTable)}|{CanonicalizeField(targetColumn)}|{CanonicalizeField(decision)}|{CanonicalizeField(traceId)}|{CanonicalizeField(detailsJson)}|{CanonicalizeField(tenantId)}";
        if (sequence.HasValue)
        {
            payload = $"v2|{sequence.Value}|{payload}";
        }

        Span<byte> hashBytes = stackalloc byte[32];
        HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes(payload), hashBytes);
        return Convert.ToHexString(hashBytes);
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

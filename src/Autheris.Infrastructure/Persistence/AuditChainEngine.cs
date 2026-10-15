using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Shared cryptographic hash-chain engine for WORM audit log persistence across SQLite, PostgreSQL, and SQL Server.
/// Centralizes HMAC-SHA256 / SHA-256 canonical digest computation and batch chain linking.
/// </summary>
public static class AuditChainEngine
{
    public const string AuditGenesisHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Computes the canonical entry hash for a single audit log entry using HMAC-SHA256 when an HMAC key is configured,
    /// or SHA-256 over the canonical payload when no HMAC key is provided.
    /// </summary>
    public static string ComputeEntryHash(
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
        string? tenantId = null)
    {
        if (hmacKey is { Length: > 0 })
        {
            return AuditCanonicalizer.ComputeEntryHash(
                hmacKey,
                sequence,
                id,
                prevHash,
                occurredAt,
                eventType,
                actorSid,
                targetTable,
                targetColumn,
                decision,
                traceId,
                detailsJson,
                tenantId);
        }

        string canonical = AuditCanonicalizer.BuildCanonicalPayload(
            sequence,
            id,
            prevHash,
            occurredAt,
            eventType,
            actorSid,
            targetTable,
            targetColumn,
            decision,
            traceId,
            detailsJson,
            tenantId);

        byte[] payloadBytes = Encoding.UTF8.GetBytes(canonical);
        byte[] hash = SHA256.HashData(payloadBytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Computes the cryptographic hash chain across an ordered batch of <see cref="AuditLogEntry"/> items in memory,
    /// mutating each entry's <see cref="AuditLogEntry.PrevHash"/> and <see cref="AuditLogEntry.EntryHash"/> in place.
    /// Returns the final entry hash in the batch (or the normalized <paramref name="previousHash"/> when empty).
    /// </summary>
    public static string ComputeBatchChain(
        IReadOnlyList<AuditLogEntry> entries,
        string? previousHash,
        byte[]? hmacKey,
        long? startSequence = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        string currentPrevHash = string.IsNullOrWhiteSpace(previousHash)
            ? AuditGenesisHash
            : previousHash;

        for (int i = 0; i < entries.Count; i++)
        {
            AuditLogEntry entry = entries[i];
            long? sequence = startSequence.HasValue ? startSequence.Value + i : entry.ChainSequence;
            entry.ChainSequence = sequence;
            entry.PrevHash = currentPrevHash;
            string effectiveTenantId = string.IsNullOrWhiteSpace(entry.TenantId.Value)
                ? TenantId.LegacySingleTenant.Value
                : entry.TenantId.Value;
            entry.EntryHash = ComputeEntryHash(
                hmacKey,
                sequence,
                entry.Id.ToString(),
                currentPrevHash,
                entry.OccurredAt,
                entry.EventType,
                entry.ActorSid.Value,
                entry.TargetTable,
                entry.TargetColumn,
                entry.Decision,
                entry.TraceId,
                entry.DetailsJson,
                effectiveTenantId);

            currentPrevHash = entry.EntryHash;
        }

        return currentPrevHash;
    }
}

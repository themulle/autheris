using System;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Domain.Audit;

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
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Links an in-memory batch of audit log entries into a continuous hash chain, setting Sequence, PrevHash,
    /// and EntryHash on each entry sequentially.
    /// </summary>
    public static (long LastSeq, string LastHash) LinkBatch(
        byte[]? hmacKey,
        IReadOnlyList<AuditLogEntry> batch,
        long startingSequence,
        string startingPrevHash)
    {
        ArgumentNullException.ThrowIfNull(batch);
        long currentSeq = startingSequence;
        string currentPrevHash = startingPrevHash ?? AuditGenesisHash;

        for (int i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            currentSeq++;
            entry.PrevHash = currentPrevHash;
            var effectiveTenantId = string.IsNullOrWhiteSpace(entry.TenantId.Value) ? TenantId.LegacySingleTenant.Value : entry.TenantId.Value;

            entry.EntryHash = ComputeEntryHash(
                hmacKey,
                currentSeq,
                entry.Id.ToString(),
                entry.PrevHash,
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

        return (currentSeq, currentPrevHash);
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

    /// <summary>
    /// SEC-AUD161-02: Builds the canonical payload string for a purge checkpoint anchor.
    /// Format: PURGE_CHECKPOINT_V1|firstSurvivingSeq|survivingPrevHash|purgedCount|purgedThroughUtc:O|createdAtUtc:O
    /// </summary>
    public static string BuildPurgeCheckpointPayload(
        long firstSurvivingSeq,
        string survivingPrevHash,
        long purgedCount,
        DateTimeOffset purgedThroughUtc,
        DateTimeOffset createdAtUtc)
    {
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"PURGE_CHECKPOINT_V1|{firstSurvivingSeq}|{survivingPrevHash}|{purgedCount}|{purgedThroughUtc.ToUniversalTime():O}|{createdAtUtc.ToUniversalTime():O}");
    }

    public static string BuildPurgeCheckpointPayload(AuditPurgeCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return BuildPurgeCheckpointPayload(
            checkpoint.FirstSurvivingSequence,
            checkpoint.SurvivingPrevHash,
            checkpoint.PurgedCount,
            checkpoint.PurgedThroughUtc,
            checkpoint.CreatedAtUtc);
    }

    /// <summary>
    /// SEC-AUD161-02: Computes the HMAC-SHA256 signature for a purge checkpoint anchor (or SHA-256 fallback if no key).
    /// </summary>
    public static string ComputePurgeCheckpointSignature(
        byte[]? hmacKey,
        long firstSurvivingSeq,
        string survivingPrevHash,
        long purgedCount,
        DateTimeOffset purgedThroughUtc,
        DateTimeOffset createdAtUtc)
    {
        string canonical = BuildPurgeCheckpointPayload(
            firstSurvivingSeq,
            survivingPrevHash,
            purgedCount,
            purgedThroughUtc,
            createdAtUtc);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(canonical);

        if (hmacKey is { Length: > 0 })
        {
            byte[] hash = HMACSHA256.HashData(hmacKey, payloadBytes);
            return Convert.ToHexString(hash);
        }

        byte[] sha = SHA256.HashData(payloadBytes);
        return Convert.ToHexString(sha);
    }

    public static string ComputePurgeCheckpointSignature(
        byte[]? hmacKey,
        AuditPurgeCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return ComputePurgeCheckpointSignature(
            hmacKey,
            checkpoint.FirstSurvivingSequence,
            checkpoint.SurvivingPrevHash,
            checkpoint.PurgedCount,
            checkpoint.PurgedThroughUtc,
            checkpoint.CreatedAtUtc);
    }

    /// <summary>
    /// SEC-AUD161-02: Verifies the HMAC-SHA256 signature of a purge checkpoint using constant-time comparison.
    /// </summary>
    public static bool VerifyPurgeCheckpointSignature(
        byte[]? hmacKey,
        AuditPurgeCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (string.IsNullOrWhiteSpace(checkpoint.Signature))
        {
            return false;
        }

        string expectedSig = ComputePurgeCheckpointSignature(
            hmacKey,
            checkpoint.FirstSurvivingSequence,
            checkpoint.SurvivingPrevHash,
            checkpoint.PurgedCount,
            checkpoint.PurgedThroughUtc,
            checkpoint.CreatedAtUtc);

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedSig);
        byte[] actualBytes = Encoding.UTF8.GetBytes(checkpoint.Signature);

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}

namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Common;

public sealed record SemanticCacheKey(
    TenantId TenantId,
    Sid UserSid,
    string SecurityContextHash,
    TableIdentifier Collection,
    string NormalizedPrompt
)
{
    public string ComputePartitionKey()
    {
        var raw = $"{TenantId.Value}:{UserSid.Value}:{SecurityContextHash}:{Collection.ToQualifiedName()}";
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

public sealed record SemanticCacheEntry(
    SemanticCacheKey Key,
    IReadOnlyList<float> PromptEmbedding,
    IReadOnlyList<VectorDocumentChunk> CachedResult,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    long PolicyEpochSnapshot
)
{
    public bool IsExpired(DateTimeOffset now, long currentEpoch)
    {
        if (now >= ExpiresAt)
        {
            return true;
        }

        // Fail-closed epoch check: If entry was snapshotted with a policy epoch > 0,
        // caller must provide a valid epoch > 0, and if the live epoch has advanced,
        // the entry is expired.
        if (PolicyEpochSnapshot > 0)
        {
            if (currentEpoch <= 0 || currentEpoch > PolicyEpochSnapshot)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class SemanticCacheOptions
{
    public int MaxPartitions { get; set; } = 1000;
    public int MaxEntriesPerPartition { get; set; } = 100;
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
    public bool EnableBackgroundCleanup { get; set; } = true;
}

public sealed record SemanticMatchResult(
    bool IsHit,
    float Similarity,
    IReadOnlyList<VectorDocumentChunk>? Result = null
);

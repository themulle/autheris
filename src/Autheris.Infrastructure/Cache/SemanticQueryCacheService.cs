namespace Autheris.Infrastructure.Cache;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class SemanticQueryCacheService : ISemanticQueryCache, IDisposable
{
    private readonly ConcurrentDictionary<string, List<SemanticCacheEntry>> _partitions = new();
    private readonly SemanticCacheOptions _options;
    private readonly IConsentCacheService? _consentCacheService;
    private readonly ILogger<SemanticQueryCacheService>? _logger;
    private readonly Timer? _cleanupTimer;
    private bool _disposed;

    public SemanticQueryCacheService(
        IOptions<SemanticCacheOptions>? options = null,
        IConsentCacheService? consentCacheService = null,
        ILogger<SemanticQueryCacheService>? logger = null)
    {
        _options = options?.Value ?? new SemanticCacheOptions();
        _consentCacheService = consentCacheService;
        _logger = logger;

        if (_options.EnableBackgroundCleanup && _options.CleanupInterval > TimeSpan.Zero)
        {
            _cleanupTimer = new Timer(
                _ => CleanupExpiredEntries(),
                null,
                _options.CleanupInterval,
                _options.CleanupInterval);
        }
    }

    public async Task<SemanticMatchResult> TryGetAsync(
        SemanticCacheKey key,
        float[] queryEmbedding,
        float similarityThreshold = 0.95f,
        long currentPolicyEpoch = 0,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(queryEmbedding);

        // If caller did not provide epoch but consent cache service is available, resolve live epoch snapshot
        if (currentPolicyEpoch <= 0 && _consentCacheService != null)
        {
            var liveEpoch = await _consentCacheService.GetEpochSnapshotAsync(key.Collection, ct).ConfigureAwait(false);
            if (liveEpoch.HasValue)
            {
                currentPolicyEpoch = liveEpoch.Value;
            }
        }

        var partitionKey = key.ComputePartitionKey();
        if (!_partitions.TryGetValue(partitionKey, out var entries))
        {
            return new SemanticMatchResult(IsHit: false, Similarity: 0f);
        }

        var now = DateTimeOffset.UtcNow;
        float maxSimilarity = 0f;
        lock (entries)
        {
            // Prune expired or invalidated by newer policy epoch (fail-closed if currentPolicyEpoch <= 0 on epoch-governed entries)
            entries.RemoveAll(e => e.IsExpired(now, currentPolicyEpoch));

            SemanticCacheEntry? bestMatch = null;

            foreach (var entry in entries)
            {
                if (entry.PromptEmbedding == null || entry.PromptEmbedding.Count != queryEmbedding.Length)
                {
                    continue;
                }

                var sim = ComputeCosineSimilarity(queryEmbedding, entry.PromptEmbedding);
                if (sim > maxSimilarity)
                {
                    maxSimilarity = sim;
                    bestMatch = entry;
                }
            }

            if (bestMatch != null && maxSimilarity >= similarityThreshold)
            {
                // PII Protection: Never log the raw prompt text; log truncated partition and prompt hash instead
                if (_logger?.IsEnabled(LogLevel.Debug) == true)
                {
                    var promptHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.NormalizedPrompt)))[..Math.Min(8, 64)];
                    _logger.LogDebug(
                        "Semantic Cache HIT for collection '{Collection}' (partition: {PartitionKey}, promptHash: {PromptHash}) with similarity {Similarity:F4}",
                        key.Collection.ToQualifiedName(),
                        partitionKey[..Math.Min(8, partitionKey.Length)],
                        promptHash,
                        maxSimilarity);
                }

                return new SemanticMatchResult(
                    IsHit: true,
                    Similarity: maxSimilarity,
                    Result: bestMatch.CachedResult);
            }
        }

        return new SemanticMatchResult(IsHit: false, Similarity: maxSimilarity);
    }

    public Task SetAsync(
        SemanticCacheEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var now = DateTimeOffset.UtcNow;
        var partitionKey = entry.Key.ComputePartitionKey();

        // 1. Unbounded Memory Protection: Cap partition count
        if (!_partitions.ContainsKey(partitionKey) && _partitions.Count >= _options.MaxPartitions)
        {
            CleanupExpiredEntries();

            if (_partitions.Count >= _options.MaxPartitions)
            {
                // Evict oldest or first available partition to prevent memory exhaustion DoS
                var keyToEvict = _partitions.Keys.FirstOrDefault();
                if (keyToEvict != null && _partitions.TryRemove(keyToEvict, out _))
                {
                    _logger?.LogWarning(
                        "Semantic cache partition capacity reached ({MaxPartitions}). Evicted partition '{PartitionKey}'.",
                        _options.MaxPartitions,
                        keyToEvict[..Math.Min(8, keyToEvict.Length)]);
                }
            }
        }

        var list = _partitions.GetOrAdd(partitionKey, _ => new List<SemanticCacheEntry>());

        lock (list)
        {
            // 2. Active clean up on write: remove expired entries immediately
            list.RemoveAll(e => now >= e.ExpiresAt);

            // 3. Remove exact prompt match if exists, then add
            list.RemoveAll(e => string.Equals(e.Key.NormalizedPrompt, entry.Key.NormalizedPrompt, StringComparison.OrdinalIgnoreCase));

            // 4. Enforce MaxEntriesPerPartition (FIFO eviction)
            while (list.Count >= _options.MaxEntriesPerPartition)
            {
                list.RemoveAt(0);
            }

            list.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task InvalidateCollectionAsync(TableIdentifier collection, CancellationToken ct = default)
    {
        var targetQualified = collection.ToQualifiedName();
        var keysToRemove = new List<string>();

        foreach (var (k, list) in _partitions)
        {
            lock (list)
            {
                list.RemoveAll(e => e.Key.Collection.Equals(collection) ||
                                    string.Equals(e.Key.Collection.ToQualifiedName(), targetQualified, StringComparison.OrdinalIgnoreCase));
                if (list.Count == 0)
                {
                    keysToRemove.Add(k);
                }
            }
        }

        foreach (var k in keysToRemove)
        {
            _partitions.TryRemove(k, out _);
        }

        _logger?.LogInformation("Invalidated semantic cache for collection '{Collection}'", targetQualified);
        return Task.CompletedTask;
    }

    public void CleanupExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var emptyPartitions = new List<string>();

        foreach (var (k, list) in _partitions)
        {
            lock (list)
            {
                list.RemoveAll(e => now >= e.ExpiresAt);
                if (list.Count == 0)
                {
                    emptyPartitions.Add(k);
                }
            }
        }

        foreach (var k in emptyPartitions)
        {
            _partitions.TryRemove(k, out _);
        }
    }

    public int PartitionCount => _partitions.Count;

    public int GetPartitionEntryCount(SemanticCacheKey key)
    {
        var partitionKey = key.ComputePartitionKey();
        if (_partitions.TryGetValue(partitionKey, out var list))
        {
            lock (list)
            {
                return list.Count;
            }
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer?.Dispose();
    }

    private static float ComputeCosineSimilarity(IReadOnlyList<float> vecA, IReadOnlyList<float> vecB)
    {
        if (vecA.Count != vecB.Count || vecA.Count == 0) return 0f;

        double dot = 0.0;
        double magA = 0.0;
        double magB = 0.0;

        for (int i = 0; i < vecA.Count; i++)
        {
            var a = vecA[i];
            var b = vecB[i];
            dot += a * b;
            magA += a * a;
            magB += b * b;
        }

        if (magA <= 0.0 || magB <= 0.0) return 0f;
        return (float)(dot / (Math.Sqrt(magA) * Math.Sqrt(magB)));
    }
}

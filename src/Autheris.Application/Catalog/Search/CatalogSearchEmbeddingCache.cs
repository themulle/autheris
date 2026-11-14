namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Autheris.Application.Security;

/// <summary>
/// Thread-safe bounded LRU cache for catalog document embeddings.
/// Keys strictly by provider, model identifier, embedding dimension, and the SHA-256 hex digest of
/// already-scrubbed document text, and retains vectors only (never raw or scrubbed source text).
/// </summary>
public sealed class CatalogSearchEmbeddingCache
{
    public const int DefaultMaxEntries = 100_000;
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    public readonly record struct ModelIdentity(
        string ProviderName,
        string ModelId,
        int Dimension);

    public readonly record struct CacheKey(
        string ProviderName,
        string ModelId,
        int Dimension,
        string ContentSha256Hex);

    private sealed class CacheEntry(CacheKey key, float[] vector, long byteSize)
    {
        public CacheKey Key { get; } = key;
        public float[] Vector { get; } = vector;
        public long ByteSize { get; } = byteSize;
    }

    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly Lock _lock = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _entries = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _currentBytes;
    private ModelIdentity? _activeModel;

    public CatalogSearchEmbeddingCache(
        int maxEntries = DefaultMaxEntries,
        long maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEntries, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBytes, 0L);
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public long CurrentBytes
    {
        get
        {
            lock (_lock)
            {
                return _currentBytes;
            }
        }
    }

    /// <summary>
    /// Computes the cache key digest over scrubbed document text.
    /// Redacts secrets defensively in case a caller invokes the helper directly.
    /// </summary>
    public static string ComputeContentSha256Hex(string scrubbedDocumentText)
    {
        var sanitized = SecretScrubber.Redact(scrubbedDocumentText);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sanitized)));
    }

    public static CacheKey CreateKey(
        string providerName,
        string modelId,
        int dimension,
        string contentSha256Hex) =>
        new(
            providerName ?? string.Empty,
            modelId ?? string.Empty,
            dimension,
            contentSha256Hex ?? string.Empty);

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _lru.Clear();
            _currentBytes = 0;
            _activeModel = null;
        }
    }

    /// <summary>
    /// Clears cached vectors when the active provider, model identifier, or vector dimension changes.
    /// </summary>
    public bool ClearIfModelChanged(string providerName, string modelId, int dimension)
    {
        var next = new ModelIdentity(providerName ?? string.Empty, modelId ?? string.Empty, dimension);
        lock (_lock)
        {
            if (_activeModel is { } current && current != next)
            {
                _entries.Clear();
                _lru.Clear();
                _currentBytes = 0;
                _activeModel = next;
                return true;
            }

            _activeModel ??= next;
            return false;
        }
    }

    public bool TryGet(
        string providerName,
        string modelId,
        int dimension,
        string contentSha256Hex,
        out ReadOnlyMemory<float> vector)
    {
        var key = CreateKey(providerName, modelId, dimension, contentSha256Hex);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                vector = node.Value.Vector;
                return true;
            }
        }

        vector = ReadOnlyMemory<float>.Empty;
        return false;
    }

    public void Set(
        string providerName,
        string modelId,
        int dimension,
        string contentSha256Hex,
        ReadOnlyMemory<float> vector)
    {
        if (vector.IsEmpty || string.IsNullOrEmpty(contentSha256Hex))
        {
            return;
        }

        var copy = vector.ToArray();
        var entryBytes = (long)copy.Length * sizeof(float);
        if (entryBytes > _maxBytes)
        {
            return;
        }

        var key = CreateKey(providerName, modelId, dimension, contentSha256Hex);
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Value.ByteSize;
                _lru.Remove(existing);
                _entries.Remove(key);
            }

            while (_entries.Count >= _maxEntries || _currentBytes + entryBytes > _maxBytes)
            {
                var tail = _lru.Last;
                if (tail is null)
                {
                    break;
                }

                _lru.RemoveLast();
                _entries.Remove(tail.Value.Key);
                _currentBytes -= tail.Value.ByteSize;
            }

            var entry = new CacheEntry(key, copy, entryBytes);
            var node = _lru.AddFirst(entry);
            _entries[key] = node;
            _currentBytes += entryBytes;
        }
    }
}

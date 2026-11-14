namespace Autheris.Api.Middleware;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HotChocolate.Language;

/// <summary>
/// Bounded LRU cache mapping exact UTF-8 document bytes to the normalized SHA-256 hex digest.
/// Key comparison is full exact byte-sequence equality (never a 32-bit hash).
/// </summary>
internal sealed class TrustedDocumentNormalizedHashCache
{
    internal const int MaxEntries = 1_024;
    internal const int MaxTotalBytes = 4 * 1024 * 1024;

    private sealed record CacheEntry(byte[] Key, string Hash);

    private sealed class ExactBytesComparer :
        IEqualityComparer<byte[]>,
        IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static readonly ExactBytesComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] obj) => GetHashCode(obj.AsSpan());

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<byte> alternate)
        {
            var hc = new HashCode();
            hc.AddBytes(alternate);
            return hc.ToHashCode();
        }

        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
    }

    private readonly object _gate = new();
    private readonly Dictionary<byte[], LinkedListNode<CacheEntry>> _map = new(ExactBytesComparer.Instance);
    private readonly Dictionary<byte[], LinkedListNode<CacheEntry>>.AlternateLookup<ReadOnlySpan<byte>> _lookup;
    private readonly LinkedList<CacheEntry> _lru = new();
    private int _totalBytes;
    private long _parseCount;

    public TrustedDocumentNormalizedHashCache()
    {
        _lookup = _map.GetAlternateLookup<ReadOnlySpan<byte>>();
    }

    internal long ParseCount => Interlocked.Read(ref _parseCount);

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    internal int TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    public bool TryGet(ReadOnlySpan<byte> utf8Bytes, out string hash)
    {
        lock (_gate)
        {
            if (_lookup.TryGetValue(utf8Bytes, out var node))
            {
                if (!ReferenceEquals(node, _lru.First))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                }

                hash = node.Value.Hash;
                return true;
            }
        }

        hash = string.Empty;
        return false;
    }

    public string GetOrComputeNormalizedHash(ReadOnlySpan<byte> utf8Bytes)
    {
        if (TryGet(utf8Bytes, out var cached))
        {
            return cached;
        }

        Interlocked.Increment(ref _parseCount);
        var document = Utf8GraphQLParser.Parse(utf8Bytes);
        var hash = ComputeNormalizedHash(document);

        Set(utf8Bytes, hash);
        return hash;
    }

    public static string ComputeNormalizedHash(DocumentNode document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var normalized = document.ToString();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private void Set(ReadOnlySpan<byte> utf8Bytes, string hash)
    {
        if (utf8Bytes.Length > MaxTotalBytes)
        {
            return;
        }

        lock (_gate)
        {
            if (_lookup.TryGetValue(utf8Bytes, out var existing))
            {
                if (!ReferenceEquals(existing, _lru.First))
                {
                    _lru.Remove(existing);
                    _lru.AddFirst(existing);
                }

                return;
            }

            while ((_map.Count >= MaxEntries || _totalBytes + utf8Bytes.Length > MaxTotalBytes) && _lru.Last is { } tail)
            {
                _lru.RemoveLast();
                _map.Remove(tail.Value.Key);
                _totalBytes -= tail.Value.Key.Length;
            }

            if (_map.Count < MaxEntries && _totalBytes + utf8Bytes.Length <= MaxTotalBytes)
            {
                var key = utf8Bytes.ToArray();
                var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, hash));
                _lru.AddFirst(node);
                _map[key] = node;
                _totalBytes += key.Length;
            }
        }
    }
}

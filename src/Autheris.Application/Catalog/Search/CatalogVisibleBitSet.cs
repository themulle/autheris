using System.Numerics;

namespace Autheris.Application.Catalog.Search;

/// <summary>
/// Dense bit set used for pre-filtering catalog search candidates before BM25/vector scoring.
/// Bit positions correspond to ordinal indices in a <see cref="CatalogSnapshot"/>.
/// </summary>
public sealed class CatalogVisibleBitSet
{
    private readonly ulong[] _words;
    private readonly int _capacity;
    private int _setCount = -1;

    public int Capacity => _capacity;

    public CatalogVisibleBitSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
        _words = new ulong[(capacity + 63) / 64];
    }

    public void Set(int ordinal)
    {
        if ((uint)ordinal >= (uint)_capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        int wordIndex = ordinal >> 6;
        ulong mask = 1UL << (ordinal & 63);
        if ((_words[wordIndex] & mask) == 0)
        {
            _words[wordIndex] |= mask;
            if (_setCount >= 0)
            {
                _setCount++;
            }
        }
    }

    public bool IsSet(int ordinal)
    {
        if ((uint)ordinal >= (uint)_capacity)
        {
            return false;
        }

        int wordIndex = ordinal >> 6;
        ulong mask = 1UL << (ordinal & 63);
        return (_words[wordIndex] & mask) != 0;
    }

    public int SetCount
    {
        get
        {
            if (_setCount < 0)
            {
                int count = 0;
                for (int i = 0; i < _words.Length; i++)
                {
                    count += BitOperations.PopCount(_words[i]);
                }
                _setCount = count;
            }
            return _setCount;
        }
    }

    public static CatalogVisibleBitSet AllSet(int capacity)
    {
        var bitset = new CatalogVisibleBitSet(capacity);
        for (int i = 0; i < bitset._words.Length; i++)
        {
            bitset._words[i] = ~0UL;
        }
        // Mask out excess bits in the last word
        int remainder = capacity & 63;
        if (remainder != 0 && bitset._words.Length > 0)
        {
            bitset._words[^1] = (1UL << remainder) - 1UL;
        }
        bitset._setCount = capacity;
        return bitset;
    }

    /// <summary>
    /// Computes full 64-character lowercase SHA-256 hex digest of the bit set's underlying bit words.
    /// </summary>
    public string ComputeSha256()
    {
        byte[]? rented = null;
        int byteCount = _words.Length * sizeof(ulong);
        Span<byte> wordBytes = byteCount <= 512
            ? stackalloc byte[byteCount]
            : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        try
        {
            for (int i = 0; i < _words.Length; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                    wordBytes.Slice(i * sizeof(ulong), sizeof(ulong)),
                    _words[i]);
            }
            Span<byte> hashBytes = stackalloc byte[32];
            System.Security.Cryptography.SHA256.HashData(wordBytes, hashBytes);
            return Convert.ToHexStringLower(hashBytes);
        }
        finally
        {
            if (rented != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// SEC-PERF254-01: Computes full 64-character lowercase SHA-256 hex digest using canonical length-prefixed encoding
    /// of sorted table names ({length}:{qualifiedName};) to eliminate delimiter injection collisions.
    /// </summary>
    public static string ComputeSha256(IEnumerable<Autheris.Domain.Common.TableIdentifier> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var sortedNames = tables
            .Select(t => t.ToQualifiedName().ToLowerInvariant())
            .OrderBy(n => n, StringComparer.Ordinal);

        var sb = new System.Text.StringBuilder();
        foreach (var name in sortedNames)
        {
            sb.Append(name.Length).Append(':').Append(name).Append(';');
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }
}

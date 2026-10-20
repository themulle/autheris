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
}

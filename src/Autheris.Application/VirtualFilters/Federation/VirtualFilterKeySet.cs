namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// F-GOV-14 (M2, M4): the authorized, de-duplicated key tuples of one Virtual Filter for one set of key types.
/// Keys that cannot be canonicalized are dropped and counted (AC-4.5); a NULL component never authorizes (AC-4.6).
/// </summary>
public sealed record VirtualFilterKeySet
{
    private VirtualFilterKeySet(IReadOnlyList<VirtualFilterKeyType> keyTypes, FrozenSet<VirtualFilterKey> keys, int droppedKeyCount)
    {
        KeyTypes = keyTypes;
        Keys = keys;
        DroppedKeyCount = droppedKeyCount;
        if (keyTypes.Count == 1)
        {
            // Single-column fast path: typed sets probed without boxing or allocating a key per row (CR-08).
            switch (keyTypes[0])
            {
                case VirtualFilterKeyType.IntegerNumber: _integers = keys.Select(k => (long)k[0]).ToFrozenSet(); break;
                case VirtualFilterKeyType.DecimalNumber: _decimals = keys.Select(k => (decimal)k[0]).ToFrozenSet(); break;
                case VirtualFilterKeyType.Uuid: _uuids = keys.Select(k => (Guid)k[0]).ToFrozenSet(); break;
                case VirtualFilterKeyType.Text: _texts = keys.Select(k => (string)k[0]).ToFrozenSet(StringComparer.Ordinal); break;
            }
        }
    }

    private readonly FrozenSet<long>? _integers;
    private readonly FrozenSet<decimal>? _decimals;
    private readonly FrozenSet<Guid>? _uuids;
    private readonly FrozenSet<string>? _texts;

    /// <summary>
    /// True for a one-column set: <paramref name="value"/> is the raw upstream value, which is canonicalized and probed
    /// without allocating. False (not authorized) for a value that cannot be converted exactly or a set of another arity.
    /// </summary>
    internal bool ContainsRawSingle(object? value) => KeyTypes[0] switch
    {
        VirtualFilterKeyType.IntegerNumber => _integers != null && VirtualFilterKeyCanonicalizer.TryGetInteger(value, out var l) && _integers.Contains(l),
        VirtualFilterKeyType.DecimalNumber => _decimals != null && VirtualFilterKeyCanonicalizer.TryGetDecimal(value, out var d) && _decimals.Contains(d),
        VirtualFilterKeyType.Uuid => _uuids != null && VirtualFilterKeyCanonicalizer.TryGetUuid(value, out var g) && _uuids.Contains(g),
        VirtualFilterKeyType.Text => _texts != null && VirtualFilterKeyCanonicalizer.TryGetText(value, out var t) && _texts.Contains(t),
        _ => false
    };

    /// <summary>True when the key set has exactly one component (the fast path of <see cref="ContainsRawSingle"/> applies).</summary>
    internal bool IsSingleColumn => KeyTypes.Count == 1;

    /// <summary>Canonical type of each key component, in key column order.</summary>
    public IReadOnlyList<VirtualFilterKeyType> KeyTypes { get; }

    /// <summary>The authorized key tuples.</summary>
    public FrozenSet<VirtualFilterKey> Keys { get; }

    /// <summary>Key tuples of the source that could not be canonicalized (never part of <see cref="Keys"/>).</summary>
    public int DroppedKeyCount { get; }

    /// <summary>True when the set was served from the key set cache (audit only).</summary>
    public bool CacheHit { get; init; }

    public int Count => Keys.Count;

    public bool Contains(VirtualFilterKey key) => Keys.Contains(key);

    /// <summary>Canonicalizes and de-duplicates raw key tuples of the key source.</summary>
    public static VirtualFilterKeySet Create(IReadOnlyList<VirtualFilterKeyType> keyTypes, IEnumerable<IReadOnlyList<object?>?> tuples)
    {
        ArgumentNullException.ThrowIfNull(keyTypes);
        ArgumentNullException.ThrowIfNull(tuples);
        if (keyTypes.Count == 0)
        {
            throw new ArgumentException("A key set needs at least one key type.", nameof(keyTypes));
        }

        var keys = new HashSet<VirtualFilterKey>();
        int dropped = 0;
        foreach (var tuple in tuples)
        {
            if (tuple != null && VirtualFilterKey.TryCreate(keyTypes, tuple, out var key))
            {
                keys.Add(key);
            }
            else
            {
                dropped++;
            }
        }

        return new VirtualFilterKeySet(keyTypes.ToArray(), keys.ToFrozenSet(), dropped);
    }

    /// <summary>Approximate memory footprint in bytes (cache size accounting, SEC-10).</summary>
    public long EstimateBytes()
    {
        long bytes = 64;
        foreach (var key in Keys)
        {
            bytes += 48;
            for (int i = 0; i < key.Count; i++)
            {
                bytes += key[i] is string s ? 24 + (2L * s.Length) : 24;
            }
        }

        return bytes;
    }

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Count = ").Append(Count).Append(", DroppedKeyCount = ").Append(DroppedKeyCount).Append(", CacheHit = ").Append(CacheHit);
        return true;
    }
}

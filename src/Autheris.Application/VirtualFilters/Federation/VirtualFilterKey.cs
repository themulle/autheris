namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Generic;

/// <summary>
/// F-GOV-14 (M2): one authorized key tuple of canonical values (<see cref="VirtualFilterKeyCanonicalizer"/>), with a
/// precomputed hash and ordinal, type-exact equality. A tuple with a NULL or unconvertible component cannot be built,
/// so it never matches (AC-4.6). <see cref="ToString"/> never prints the values.
/// </summary>
public readonly struct VirtualFilterKey : IEquatable<VirtualFilterKey>
{
    private readonly object[] _values;
    private readonly int _hash;

    private VirtualFilterKey(object[] values)
    {
        _values = values;
        var hash = new HashCode();
        foreach (var value in values)
        {
            hash.Add(value);
        }

        _hash = hash.ToHashCode();
    }

    /// <summary>Number of components.</summary>
    public int Count => _values?.Length ?? 0;

    /// <summary>Canonical value of component <paramref name="index"/>.</summary>
    public object this[int index] => _values[index];

    /// <summary>Builds the key from raw values; false when the arity differs or a component is NULL or not convertible.</summary>
    public static bool TryCreate(IReadOnlyList<VirtualFilterKeyType> types, IReadOnlyList<object?> values, out VirtualFilterKey key)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(values);
        key = default;
        if (types.Count == 0 || values.Count != types.Count)
        {
            return false;
        }

        var canonical = new object[types.Count];
        for (int i = 0; i < canonical.Length; i++)
        {
            if (!VirtualFilterKeyCanonicalizer.TryCanonicalize(types[i], values[i], out canonical[i]))
            {
                return false;
            }
        }

        key = new VirtualFilterKey(canonical);
        return true;
    }

    public bool Equals(VirtualFilterKey other)
    {
        if (_hash != other._hash || Count != other.Count)
        {
            return false;
        }

        for (int i = 0; i < Count; i++)
        {
            var left = _values[i];
            var right = other._values[i];
            if (left.GetType() != right.GetType() || !left.Equals(right))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is VirtualFilterKey other && Equals(other);

    public override int GetHashCode() => _hash;

    public override string ToString() => $"VirtualFilterKey({Count} components)";

    public static bool operator ==(VirtualFilterKey left, VirtualFilterKey right) => left.Equals(right);

    public static bool operator !=(VirtualFilterKey left, VirtualFilterKey right) => !left.Equals(right);
}

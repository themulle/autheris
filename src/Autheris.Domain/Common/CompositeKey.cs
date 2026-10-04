using System;
using System.Collections.Generic;
using System.Linq;

namespace Autheris.Domain.Common;

/// <summary>
/// Repräsentiert einen zusammengesetzten Primär- oder Fremdschlüssel (Composite Key)
/// mit struktureller Wertgleichheit und typsicherem Hashing.
/// </summary>
public readonly struct CompositeKey : IEquatable<CompositeKey>, IComparable<CompositeKey>
{
    private static readonly object?[] EmptyValues = Array.Empty<object?>();
    private readonly object?[] _values;

    public IReadOnlyList<object?> Values => _values ?? EmptyValues;
    public int Count => Values.Count;

    public CompositeKey(params object?[] values)
    {
        _values = values ?? EmptyValues;
    }

    public CompositeKey(IEnumerable<object?> values)
    {
        _values = values?.ToArray() ?? EmptyValues;
    }

    public bool Equals(CompositeKey other)
    {
        var v1 = Values;
        var v2 = other.Values;

        if (v1.Count != v2.Count) return false;

        for (int i = 0; i < v1.Count; i++)
        {
            if (!AreValuesEqual(v1[i], v2[i])) return false;
        }

        return true;
    }

    private static bool TryConvertToDecimal(object? val, out decimal result)
    {
        switch (val)
        {
            case decimal d:
                result = d;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case short s:
                result = s;
                return true;
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = sb;
                return true;
            case ushort us:
                result = us;
                return true;
            case uint ui:
                result = ui;
                return true;
            case ulong ul:
                result = ul;
                return true;
            case float f:
                if (!float.IsNaN(f) && !float.IsInfinity(f) && f >= (float)decimal.MinValue && f <= (float)decimal.MaxValue)
                {
                    result = (decimal)f;
                    return true;
                }
                result = 0;
                return false;
            case double db:
                if (!double.IsNaN(db) && !double.IsInfinity(db) && db >= (double)decimal.MinValue && db <= (double)decimal.MaxValue)
                {
                    result = (decimal)db;
                    return true;
                }
                result = 0;
                return false;
            default:
                result = 0;
                return false;
        }
    }

    private static bool AreValuesEqual(object? val1, object? val2)
    {
        if (ReferenceEquals(val1, val2)) return true;
        if (val1 is null || val2 is null) return false;
        if (val1.Equals(val2)) return true;

        if (TryConvertToDecimal(val1, out var d1) && TryConvertToDecimal(val2, out var d2))
        {
            return d1 == d2;
        }

        if (IsNumeric(val1) && IsNumeric(val2))
        {
            return string.Equals(val1.ToString(), val2.ToString(), StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsNumeric(object val) =>
        val is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    public override bool Equals(object? obj) =>
        obj is CompositeKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var val in Values)
        {
            if (val != null)
            {
                if (TryConvertToDecimal(val, out var d))
                {
                    hash.Add(d);
                    continue;
                }
                if (IsNumeric(val))
                {
                    hash.Add(val.ToString(), StringComparer.Ordinal);
                    continue;
                }
            }
            hash.Add(val);
        }
        return hash.ToHashCode();
    }

    public int CompareTo(CompositeKey other)
    {
        var v1 = Values;
        var v2 = other.Values;
        int len = Math.Min(v1.Count, v2.Count);

        for (int i = 0; i < len; i++)
        {
            var val1 = v1[i];
            var val2 = v2[i];

            if (val1 is null && val2 is null) continue;
            if (val1 is null) return -1;
            if (val2 is null) return 1;

            if (TryConvertToDecimal(val1, out var d1) && TryConvertToDecimal(val2, out var d2))
            {
                int cmp = d1.CompareTo(d2);
                if (cmp != 0) return cmp;
                continue;
            }

            if (IsNumeric(val1) && IsNumeric(val2))
            {
                int numCmp = string.CompareOrdinal(val1.ToString(), val2.ToString());
                if (numCmp != 0) return numCmp;
                continue;
            }

            if (val1 is IComparable comp && val1.GetType() == val2.GetType())
            {
                int cmp = comp.CompareTo(val2);
                if (cmp != 0) return cmp;
            }
            else
            {
                int strCmp = string.Compare(val1?.ToString(), val2?.ToString(), StringComparison.Ordinal);
                if (strCmp != 0) return strCmp;
            }
        }

        return v1.Count.CompareTo(v2.Count);
    }

    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is not CompositeKey other)
            throw new ArgumentException("Object must be of type CompositeKey", nameof(obj));
        return CompareTo(other);
    }

    public override string ToString() =>
        $"({string.Join(", ", Values.Select(v => v?.ToString() ?? "NULL"))})";

    public static bool operator ==(CompositeKey left, CompositeKey right) => left.Equals(right);
    public static bool operator !=(CompositeKey left, CompositeKey right) => !left.Equals(right);
    public static bool operator <(CompositeKey left, CompositeKey right) => left.CompareTo(right) < 0;
    public static bool operator <=(CompositeKey left, CompositeKey right) => left.CompareTo(right) <= 0;
    public static bool operator >(CompositeKey left, CompositeKey right) => left.CompareTo(right) > 0;
    public static bool operator >=(CompositeKey left, CompositeKey right) => left.CompareTo(right) >= 0;
}

namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autheris.Domain.Options;

/// <summary>
/// Immutable multi-dimensional watermark vector state (PLAN-CDC-101 D-2, WP-101-2).
/// Tracks independent monotonic high-water marks for each dimension column.
/// </summary>
public sealed record MultiWatermarkVector
{
    public static readonly MultiWatermarkVector Empty = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, string> Dimensions { get; init; }

    public MultiWatermarkVector()
    {
        Dimensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public MultiWatermarkVector(IReadOnlyDictionary<string, string> dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        Dimensions = new Dictionary<string, string>(dimensions, StringComparer.OrdinalIgnoreCase);
    }

    public string? GetValue(string dimensionName) =>
        Dimensions.TryGetValue(dimensionName, out var value) ? value : null;

    public MultiWatermarkVector AdvanceDimension(string dimensionName, string newValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimensionName);
        ArgumentNullException.ThrowIfNull(newValue);

        var updated = new Dictionary<string, string>(Dimensions, StringComparer.OrdinalIgnoreCase)
        {
            [dimensionName] = newValue
        };
        return new MultiWatermarkVector(updated);
    }

    /// <summary>
    /// Monotonically advances the vector dimension by computing element-wise max.
    /// </summary>
    public MultiWatermarkVector Advance(string dimensionName, string candidateValue, WatermarkDimensionType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimensionName);
        if (string.IsNullOrWhiteSpace(candidateValue)) return this;

        if (!Dimensions.TryGetValue(dimensionName, out var currentValue) || string.IsNullOrWhiteSpace(currentValue))
        {
            return AdvanceDimension(dimensionName, candidateValue);
        }

        var maxVal = MaxOf(currentValue, candidateValue, type);
        return AdvanceDimension(dimensionName, maxVal);
    }

    /// <summary>
    /// Computes the monotonic maximum of two values based on the declared dimension type.
    /// </summary>
    public static string MaxOf(string current, string candidate, WatermarkDimensionType type)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            return type switch
            {
                WatermarkDimensionType.DateTimeOffset =>
                    DateTimeOffset.Parse(candidate, CultureInfo.InvariantCulture) > DateTimeOffset.Parse(current, CultureInfo.InvariantCulture)
                        ? candidate : current,
                WatermarkDimensionType.Int64 =>
                    long.Parse(candidate, CultureInfo.InvariantCulture) > long.Parse(current, CultureInfo.InvariantCulture)
                        ? candidate : current,
                WatermarkDimensionType.Int32 =>
                    int.Parse(candidate, CultureInfo.InvariantCulture) > int.Parse(current, CultureInfo.InvariantCulture)
                        ? candidate : current,
                _ => string.CompareOrdinal(candidate, current) > 0 ? candidate : current
            };
        }
        catch
        {
            return string.CompareOrdinal(candidate, current) > 0 ? candidate : current;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(Dimensions);

    public static MultiWatermarkVector FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return dict is not null ? new MultiWatermarkVector(dict) : Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public bool Equals(MultiWatermarkVector? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Dimensions.Count != other.Dimensions.Count) return false;

        foreach (var (k, v) in Dimensions)
        {
            if (!other.Dimensions.TryGetValue(k, out var otherVal) ||
                !string.Equals(v, otherVal, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in Dimensions.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            hash.Add(key, StringComparer.OrdinalIgnoreCase);
            hash.Add(Dimensions[key], StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}

using System.Globalization;
using Autheris.Application.Analytics;

namespace Autheris.GraphQL.Catalog;

/// <summary>One aggregate of a typed call after validation: its alias, function, column and static value kind.</summary>
internal sealed record ResolvedAggregate(string Alias, AggregateFunction Function, CatalogColumnField? Column, AggregateValueKind Kind);

/// <summary>Exactly one value field is set (the one named by <see cref="Kind"/>); all are null for a NULL aggregate.</summary>
internal sealed record AggregateValueDto(
    string Alias,
    AggregateValueKind Kind,
    long? IntValue = null,
    decimal? DecimalValue = null,
    double? FloatValue = null,
    string? StringValue = null,
    DateTimeOffset? DateTimeValue = null,
    bool? BooleanValue = null);

/// <summary>WP-112-3: Typed group row containing key fields, row count and aggregate results.</summary>
internal record AutherisAggregationRow(
    IReadOnlyDictionary<string, object?> Key,
    long RowCount,
    bool IsMergedOther,
    IReadOnlyList<AggregateValueDto> Aggregates);

internal sealed record AggregateGroupDto(
    IReadOnlyDictionary<string, object?> Key,
    long RowCount,
    bool IsMergedOther,
    IReadOnlyList<AggregateValueDto> Aggregates) : AutherisAggregationRow(Key, RowCount, IsMergedOther, Aggregates);

/// <summary>WP-112-3: The typed result. It deliberately has no suppressed-row count (#112 O-4, SEC-112-20).</summary>
internal record AutherisTypedAggregationResult(
    IReadOnlyList<AggregateGroupDto> Groups,
    long Count,
    bool Suppressed,
    int SuppressedGroupsCount,
    int MinimumGroupSize,
    bool MergedIntoOther,
    bool Truncated,
    bool NoiseApplied);

internal sealed record AggregateResultDto(
    IReadOnlyList<AggregateGroupDto> Groups,
    long Count,
    bool Suppressed,
    int SuppressedGroupsCount,
    int MinimumGroupSize,
    bool MergedIntoOther,
    bool Truncated,
    bool NoiseApplied) : AutherisTypedAggregationResult(Groups, Count, Suppressed, SuppressedGroupsCount, MinimumGroupSize, MergedIntoOther, Truncated, NoiseApplied);

/// <summary>
/// #112 A-4: pure mapping of an engine result to the typed GraphQL shape. Kinds are static (function and column type), conversion is
/// strict and invariant-culture; a value that does not fit its declared kind raises AGGREGATION_RESULT_TYPE_MISMATCH.
/// </summary>
internal static class AggregateResultMapper
{
    /// <summary>The value kind of a function over a column type, or null when the combination is not allowed (A-4 matrix).</summary>
    public static AggregateValueKind? KindFor(AggregateFunction function, CatalogFieldType? column)
    {
        switch (function)
        {
            case AggregateFunction.Count:
                return AggregateValueKind.Int;
            case AggregateFunction.CountDistinct:
                return column is null ? null : AggregateValueKind.Int;
        }

        if (column is null)
        {
            return null;
        }

        return function switch
        {
            AggregateFunction.Sum => column switch
            {
                CatalogFieldType.Int or CatalogFieldType.Long => AggregateValueKind.Int,
                CatalogFieldType.Decimal => AggregateValueKind.Decimal,
                CatalogFieldType.Float => AggregateValueKind.Float,
                _ => null
            },
            AggregateFunction.Avg => column switch
            {
                CatalogFieldType.Int or CatalogFieldType.Long or CatalogFieldType.Decimal => AggregateValueKind.Decimal,
                CatalogFieldType.Float => AggregateValueKind.Float,
                _ => null
            },
            AggregateFunction.Min or AggregateFunction.Max => column switch
            {
                CatalogFieldType.Int or CatalogFieldType.Long => AggregateValueKind.Int,
                CatalogFieldType.Decimal => AggregateValueKind.Decimal,
                CatalogFieldType.Float => AggregateValueKind.Float,
                CatalogFieldType.DateTime => AggregateValueKind.DateTime,
                CatalogFieldType.String => AggregateValueKind.String,
                _ => null
            },
            _ => null
        };
    }

    public static AggregateResultDto Map(
        AggregationResult result,
        CatalogTableType table,
        IReadOnlyList<CatalogColumnField> groupColumns,
        IReadOnlyList<ResolvedAggregate> aggregates)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(table);

        var groups = new List<AggregateGroupDto>(result.Groups.Count);
        foreach (var row in result.Groups)
        {
            var key = new Dictionary<string, object?>(groupColumns.Count, StringComparer.Ordinal);
            foreach (var column in groupColumns)
            {
                // The merged OTHER row stands for many groups: its key is never filled (SEC-112-07).
                key[column.FieldName] = row.IsMergedOther ? null : ConvertKey(Lookup(row.GroupKey, column.ColumnName), column.FieldType);
            }

            var values = new List<AggregateValueDto>(aggregates.Count);
            foreach (var aggregate in aggregates)
            {
                var raw = Lookup(row.Aggregates, aggregate.Alias);
                if (row.IsMergedOther && aggregate.Function is AggregateFunction.CountDistinct or AggregateFunction.Avg or AggregateFunction.Min or AggregateFunction.Max)
                {
                    raw = null;
                }

                values.Add(AggregateValueConverter.Convert(aggregate.Alias, aggregate.Kind, raw));
            }

            groups.Add(new AggregateGroupDto(key, row.Count, row.IsMergedOther, values));
        }

        return new AggregateResultDto(
            groups,
            result.Totals?.Count ?? 0L,
            result.Suppressed,
            result.SuppressedGroupsCount,
            result.MinimumGroupSize,
            result.MergedIntoOther,
            result.Truncated,
            result.NoiseApplied);
    }

    private static object? Lookup(IReadOnlyDictionary<string, object?> values, string name)
    {
        if (values.TryGetValue(name, out var direct))
        {
            return direct;
        }

        foreach (var (k, v) in values)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }

    private static object? ConvertKey(object? raw, CatalogFieldType type)
    {
        if (raw is null)
        {
            return null;
        }

        return type switch
        {
            CatalogFieldType.Int => AggregateValueConverter.TryLong(raw, out var i) && i is >= int.MinValue and <= int.MaxValue
                ? (int)i : throw AggregateErrors.ResultTypeMismatch(),
            CatalogFieldType.Long => AggregateValueConverter.TryLong(raw, out var l) ? l : throw AggregateErrors.ResultTypeMismatch(),
            CatalogFieldType.Float => AggregateValueConverter.TryDouble(raw, out var d) ? d : throw AggregateErrors.ResultTypeMismatch(),
            CatalogFieldType.Decimal => AggregateValueConverter.TryDecimal(raw, out var m) ? m : throw AggregateErrors.ResultTypeMismatch(),
            CatalogFieldType.Boolean => AggregateValueConverter.TryBoolean(raw, out var b) ? b : throw AggregateErrors.ResultTypeMismatch(),
            CatalogFieldType.DateTime => AggregateValueConverter.TryDateTime(raw, out var dt)
                ? dt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                : throw AggregateErrors.ResultTypeMismatch(),
            _ => raw as string ?? (raw is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : raw.ToString())
        };
    }
}

/// <summary>Strict, culture-invariant conversion of driver values to the typed value shape (shared by aggregates and metrics).</summary>
internal static class AggregateValueConverter
{
    /// <param name="lenientString">A STRING value that is not a string (a GUID, say) is formatted culture-invariantly instead of rejected (dimension values).</param>
    public static AggregateValueDto Convert(string alias, AggregateValueKind kind, object? raw, bool lenientString = false)
    {
        var empty = new AggregateValueDto(alias, kind);
        if (raw is null)
        {
            return empty;
        }

        switch (kind)
        {
            case AggregateValueKind.Int:
                return TryLong(raw, out var l) ? empty with { IntValue = l } : throw AggregateErrors.ResultTypeMismatch();
            case AggregateValueKind.Decimal:
                return TryDecimal(raw, out var m) ? empty with { DecimalValue = m } : throw AggregateErrors.ResultTypeMismatch();
            case AggregateValueKind.Float:
                return TryDouble(raw, out var d) ? empty with { FloatValue = d } : throw AggregateErrors.ResultTypeMismatch();
            case AggregateValueKind.String:
                return raw is string s
                    ? empty with { StringValue = s }
                    : lenientString && raw is IFormattable f
                        ? empty with { StringValue = f.ToString(null, CultureInfo.InvariantCulture) }
                        : throw AggregateErrors.ResultTypeMismatch();
            case AggregateValueKind.Boolean:
                return TryBoolean(raw, out var b) ? empty with { BooleanValue = b } : throw AggregateErrors.ResultTypeMismatch();
            case AggregateValueKind.DateTime:
                return TryDateTime(raw, out var dt) ? empty with { DateTimeValue = dt } : throw AggregateErrors.ResultTypeMismatch();
            default:
                throw AggregateErrors.ResultTypeMismatch();
        }
    }

    public static bool TryLong(object raw, out long value)
    {
        switch (raw)
        {
            case long l: value = l; return true;
            case int i: value = i; return true;
            case short s: value = s; return true;
            case sbyte sb: value = sb; return true;
            case byte b: value = b; return true;
            case ushort us: value = us; return true;
            case uint ui: value = ui; return true;
            case ulong ul when ul <= long.MaxValue: value = (long)ul; return true;
            case decimal m when decimal.Truncate(m) == m && m >= long.MinValue && m <= long.MaxValue: value = (long)m; return true;
            case double d when double.IsFinite(d) && Math.Truncate(d) == d && d >= -9.2233720368547758E18 && d < 9.2233720368547758E18:
                value = (long)d;
                return true;
            case float f when float.IsFinite(f) && MathF.Truncate(f) == f && f >= -9.2233720368547758E18f && f < 9.2233720368547758E18f:
                value = (long)f;
                return true;
            default: value = 0; return false;
        }
    }

    public static bool TryDecimal(object raw, out decimal value)
    {
        switch (raw)
        {
            case decimal m: value = m; return true;
            case double d when double.IsFinite(d) && Math.Abs(d) < 7.9E28: value = (decimal)d; return true;
            case float f when float.IsFinite(f) && Math.Abs(f) < 7.9E28f: value = (decimal)f; return true;
            default:
                if (TryLong(raw, out var l))
                {
                    value = l;
                    return true;
                }

                value = 0;
                return false;
        }
    }

    public static bool TryDouble(object raw, out double value)
    {
        switch (raw)
        {
            case double d when double.IsFinite(d): value = d; return true;
            case float f when float.IsFinite(f): value = f; return true;
            case decimal m: value = (double)m; return true;
            default:
                if (TryLong(raw, out var l))
                {
                    value = l;
                    return true;
                }

                value = 0;
                return false;
        }
    }

    public static bool TryBoolean(object raw, out bool value)
    {
        switch (raw)
        {
            case bool b: value = b; return true;
            default:
                if (TryLong(raw, out var l) && l is 0 or 1)
                {
                    value = l == 1;
                    return true;
                }

                value = false;
                return false;
        }
    }

    public static bool TryDateTime(object raw, out DateTimeOffset value)
    {
        switch (raw)
        {
            case DateTimeOffset dto: value = dto.ToUniversalTime(); return true;
            case DateTime dt:
                value = new DateTimeOffset(dt.Kind switch
                {
                    DateTimeKind.Utc => dt,
                    DateTimeKind.Local => dt.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                });
                return true;
            case DateOnly date:
                // Npgsql yields DateOnly for a PostgreSQL DATE: midnight UTC, the same value as the ISO date text of SQLite.
                value = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
                return true;
            case string s:
                return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
            default: value = default; return false;
        }
    }
}

namespace Autheris.Application.Sql;

using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// A Trino column type: the type string (<c>decimal(18,2)</c>) plus the parts of the client type signature
/// (<c>rawType</c> and its numeric arguments).
/// </summary>
public sealed record TrinoColumnType(string Name, string RawType, IReadOnlyList<long> Arguments)
{
    /// <summary>The unbounded <c>varchar</c>, also the type of every column that cannot be mapped.</summary>
    public static readonly TrinoColumnType Varchar = new("varchar", "varchar", [UnboundedVarcharLength]);

    internal const long UnboundedVarcharLength = 2147483647;
}

/// <summary>
/// WebSQL findings 2.2: maps the CLR types of a result to Trino types and encodes the values the way the Trino client
/// protocol does (numbers and booleans as JSON values; decimal, date, time and timestamp as strings in Trino format;
/// varbinary as Base64). A column whose values do not all fit the mapped type is announced as <c>varchar</c>.
/// </summary>
public static class TrinoColumnTypes
{
    private const int TimestampPrecision = 3;
    private const int MaxDecimalPrecision = 38;

    private static readonly TrinoColumnType Boolean = Simple("boolean");
    private static readonly TrinoColumnType TinyInt = Simple("tinyint");
    private static readonly TrinoColumnType SmallInt = Simple("smallint");
    private static readonly TrinoColumnType Integer = Simple("integer");
    private static readonly TrinoColumnType BigInt = Simple("bigint");
    private static readonly TrinoColumnType Real = Simple("real");
    private static readonly TrinoColumnType Double = Simple("double");
    private static readonly TrinoColumnType Date = Simple("date");
    private static readonly TrinoColumnType Uuid = Simple("uuid");
    private static readonly TrinoColumnType Varbinary = Simple("varbinary");
    private static readonly TrinoColumnType Time = new($"time({TimestampPrecision})", "time", [TimestampPrecision]);
    private static readonly TrinoColumnType Timestamp = new($"timestamp({TimestampPrecision})", "timestamp", [TimestampPrecision]);
    private static readonly TrinoColumnType TimestampWithTimeZone =
        new($"timestamp({TimestampPrecision}) with time zone", "timestamp with time zone", [TimestampPrecision]);

    /// <summary>Trino type of <paramref name="column"/>; <paramref name="values"/> are the column's values in the result.</summary>
    public static TrinoColumnType Map(SqlResultColumn column, IEnumerable<object?> values)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(values);

        Type? clrType = column.ClrType;
        if (clrType == null)
        {
            return TrinoColumnType.Varchar;
        }

        // Every value must have the reported type (SQLite types per value, not per column).
        int maxScale = 0;
        foreach (var value in values)
        {
            if (value is null or DBNull)
            {
                continue;
            }

            if (value.GetType() != clrType)
            {
                return TrinoColumnType.Varchar;
            }

            if (value is decimal d)
            {
                maxScale = Math.Max(maxScale, d.Scale);
            }
        }

        string dataTypeName = column.DataTypeName ?? string.Empty;
        return clrType switch
        {
            _ when clrType == typeof(bool) => Boolean,
            _ when clrType == typeof(sbyte) => TinyInt,
            _ when clrType == typeof(byte) || clrType == typeof(short) => SmallInt,
            _ when clrType == typeof(ushort) || clrType == typeof(int) => Integer,
            _ when clrType == typeof(uint) || clrType == typeof(long) => BigInt,
            _ when clrType == typeof(float) => Real,
            _ when clrType == typeof(double) => Double,
            _ when clrType == typeof(decimal) => Decimal(column.NumericPrecision, column.NumericScale, maxScale),
            _ when clrType == typeof(string) || clrType == typeof(char) => TrinoColumnType.Varchar,
            _ when clrType == typeof(DateOnly) => Date,
            _ when clrType == typeof(DateTime) && dataTypeName.Equals("date", StringComparison.OrdinalIgnoreCase) => Date,
            _ when clrType == typeof(DateTime) && dataTypeName.Contains("with time zone", StringComparison.OrdinalIgnoreCase) => TimestampWithTimeZone,
            _ when clrType == typeof(DateTime) => Timestamp,
            _ when clrType == typeof(DateTimeOffset) => TimestampWithTimeZone,
            _ when clrType == typeof(TimeSpan) || clrType == typeof(TimeOnly) => Time,
            _ when clrType == typeof(Guid) => Uuid,
            _ when clrType == typeof(byte[]) => Varbinary,
            _ => TrinoColumnType.Varchar
        };
    }

    /// <summary>The value of a column of type <paramref name="type"/> in Trino's JSON encoding.</summary>
    public static object? Encode(object? value, TrinoColumnType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return value switch
        {
            null or DBNull => null,
            double d when !double.IsFinite(d) => d.ToString(CultureInfo.InvariantCulture),
            float f when !float.IsFinite(f) => f.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            DateTime dt when type.RawType == "date" => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt when type.RawType == "timestamp with time zone" => FormatWithZone(new DateTimeOffset(
                dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime())),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            DateTimeOffset dto => FormatWithZone(dto),
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeSpan ts => ts.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Guid g => g.ToString("D"),
            byte[] bytes => Convert.ToBase64String(bytes),
            IFormattable formattable when type.RawType == "varchar" => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private static string FormatWithZone(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    private static TrinoColumnType Decimal(int? precision, int? scale, int observedScale)
    {
        // Providers report 255 or 0 when the precision is unknown (PostgreSQL numeric without precision).
        bool known = precision is > 0 and <= MaxDecimalPrecision && scale is >= 0 && scale <= precision;
        long p = known ? precision!.Value : MaxDecimalPrecision;
        long s = known ? scale!.Value : Math.Min(observedScale, MaxDecimalPrecision);
        return new TrinoColumnType(
            string.Create(CultureInfo.InvariantCulture, $"decimal({p},{s})"), "decimal", [p, s]);
    }

    private static TrinoColumnType Simple(string name) => new(name, name, []);
}

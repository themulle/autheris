namespace Autheris.Infrastructure.Databricks;

using System.Globalization;

/// <summary>
/// Maps Databricks Statement Execution <c>type_name</c> metadata to CLR column types and parses
/// <c>JSON_ARRAY</c> cell values using <see cref="CultureInfo.InvariantCulture"/>.
/// </summary>
internal static class DatabricksTypeMap
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss"];

    public static Type ResolveClrType(string? typeName, int? precision = null) => Normalize(typeName) switch
    {
        "BOOLEAN" => typeof(bool),
        "BYTE" => typeof(sbyte),
        "SHORT" => typeof(short),
        "INT" => typeof(int),
        "LONG" => typeof(long),
        "FLOAT" => typeof(float),
        "DOUBLE" => typeof(double),
        "DECIMAL" => precision is > 28 ? typeof(string) : typeof(decimal),
        "DATE" => typeof(DateTime),
        "TIMESTAMP" => typeof(DateTimeOffset),
        "TIMESTAMP_NTZ" => typeof(DateTime),
        _ => typeof(string)
    };

    public static object ParseCell(string? raw, string? typeName, int? precision = null)
    {
        if (raw is null)
        {
            return DBNull.Value;
        }

        return Normalize(typeName) switch
        {
            "BOOLEAN" => bool.TryParse(raw, out var b) ? b : throw Invalid(typeName),
            "BYTE" => sbyte.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sb) ? sb : throw Invalid(typeName),
            "SHORT" => short.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : throw Invalid(typeName),
            "INT" => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : throw Invalid(typeName),
            "LONG" => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : throw Invalid(typeName),
            "FLOAT" => float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : throw Invalid(typeName),
            "DOUBLE" => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw Invalid(typeName),
            "DECIMAL" when precision is > 28 => raw,
            "DECIMAL" => decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : throw Invalid(typeName),
            "DATE" => ParseDate(raw, typeName),
            "TIMESTAMP" => ParseTimestamp(raw, typeName),
            "TIMESTAMP_NTZ" => ParseTimestampNtz(raw, typeName),
            _ => raw
        };
    }

    private static DateTime ParseDate(string raw, string? typeName)
    {
        if (DateTime.TryParseExact(raw.AsSpan().Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return DateTime.SpecifyKind(dt.Date, DateTimeKind.Unspecified);
        }

        throw Invalid(typeName);
    }

    private static DateTimeOffset ParseTimestamp(string raw, string? typeName)
    {
        var trimmed = raw.AsSpan().Trim();
        if (HasIsoYearPrefix(trimmed) &&
            DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            return dto;
        }

        throw Invalid(typeName);
    }

    private static DateTime ParseTimestampNtz(string raw, string? typeName)
    {
        var trimmed = raw.AsSpan().Trim();
        if (HasIsoYearPrefix(trimmed) &&
            DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        }

        throw Invalid(typeName);
    }

    private static bool HasIsoYearPrefix(ReadOnlySpan<char> value) =>
        value.Length >= 10 &&
        char.IsAsciiDigit(value[0]) &&
        char.IsAsciiDigit(value[1]) &&
        char.IsAsciiDigit(value[2]) &&
        char.IsAsciiDigit(value[3]) &&
        value[4] == '-';

    private static string Normalize(string? typeName) =>
        string.IsNullOrWhiteSpace(typeName) ? string.Empty : typeName.Trim().ToUpperInvariant();

    private static DatabricksProviderException Invalid(string? typeName) =>
        new(DatabricksErrorCodes.ProtocolViolation, $"Databricks result value could not be parsed as '{typeName ?? "UNKNOWN"}'.");
}

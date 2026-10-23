namespace Autheris.Application.Analytics;

/// <summary>
/// Value kind of a catalog column, as far as typed filters and aggregation results are concerned. The Application-level twin of the
/// GraphQL field type (the member order is the same).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Represents catalog value kinds.")]
public enum ColumnValueKind
{
    String,
    Int,
    Long,
    Float,
    Decimal,
    Boolean,
    DateTime
}

/// <summary>Mapping of database type names to <see cref="ColumnValueKind"/>.</summary>
public static class ColumnValueKinds
{
    /// <summary>Maps a database type name (any dialect spelling) to a value kind. Unknown, spatial and complex types are <see cref="ColumnValueKind.String"/>.</summary>
    public static ColumnValueKind FromDataType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return ColumnValueKind.String;
        }

        var dt = dataType.Trim().ToLowerInvariant();

        // 1. Spatial / complex types that might contain words like 'point' or 'interval'
        if (dt.Contains("interval", StringComparison.Ordinal) || dt.Contains("point", StringComparison.Ordinal) ||
            dt.Contains("polygon", StringComparison.Ordinal) || dt.Contains("geometry", StringComparison.Ordinal) ||
            dt.Contains("geography", StringComparison.Ordinal) || dt.Contains("line", StringComparison.Ordinal) ||
            dt.Contains("json", StringComparison.Ordinal) || dt.Contains("xml", StringComparison.Ordinal) ||
            dt.Contains("uuid", StringComparison.Ordinal) || dt.Contains("guid", StringComparison.Ordinal))
        {
            return ColumnValueKind.String;
        }

        var baseType = dt.Split('(', '[', ' ')[0].Trim();

        if (baseType.StartsWith("bit", StringComparison.Ordinal) || baseType.StartsWith("bool", StringComparison.Ordinal))
        {
            return ColumnValueKind.Boolean;
        }

        if (baseType is "bigint" or "int8" or "long")
        {
            return ColumnValueKind.Long;
        }

        if (baseType is "int" or "integer" or "int4" or "smallint" or "int2" or "tinyint")
        {
            return ColumnValueKind.Int;
        }

        if (baseType is "decimal" or "numeric" or "money" or "smallmoney")
        {
            return ColumnValueKind.Decimal;
        }

        if (baseType is "float" or "double" or "real" or "float4" or "float8")
        {
            return ColumnValueKind.Float;
        }

        if (dt.Contains("date", StringComparison.Ordinal) || dt.Contains("time", StringComparison.Ordinal) ||
            dt.Contains("timestamp", StringComparison.Ordinal))
        {
            return ColumnValueKind.DateTime;
        }

        return ColumnValueKind.String;
    }

    /// <summary>True when the database type is a calendar date without a time part (<c>date</c>); filter values then compare as dates.</summary>
    public static bool IsDateOnly(string? dataType) =>
        !string.IsNullOrWhiteSpace(dataType) && string.Equals(dataType.Trim().Split('(', '[', ' ')[0], "date", StringComparison.OrdinalIgnoreCase);
}

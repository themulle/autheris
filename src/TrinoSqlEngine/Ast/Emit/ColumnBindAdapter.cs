namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>Coarse class of a cataloged column type (provider-neutral).</summary>
public enum ColumnTypeClass
{
    Unknown,
    /// <summary>nvarchar, nchar, ntext, nvarchar2, nclob.</summary>
    UnicodeText,
    /// <summary>varchar, char, text, varchar2, clob.</summary>
    AnsiText,
    /// <summary>int, bigint, decimal, number, float ...</summary>
    Numeric,
    Other
}

/// <summary>How a binder follows the catalog column type of a compared column (SEC-ADG-16 item 2, CR-ADG-09).</summary>
public enum ColumnBindStyle
{
    /// <summary>The bind type follows the value only.</summary>
    None,
    /// <summary>SQL Server: a string compared with a <c>varchar</c> column is bound as <c>varchar</c> (an <c>nvarchar</c> bind forces CONVERT_IMPLICIT on the column and turns the seek into a scan).</summary>
    SqlServer,
    /// <summary>Oracle: the bind follows the column (a numeric value against a VARCHAR2 column is bound as text, a numeric-looking string against a NUMBER column as a number), so the column is never converted (no ORA-01722 side channel).</summary>
    Oracle
}

/// <summary>The bind shape of one parameter after the column type was taken into account.</summary>
public readonly record struct AdaptedBind(SqlParameterType Type, object? Value, bool AnsiString, int DeclaredLength);

/// <summary>
/// Maps the catalog data type of the column a gateway-bound value is compared with to a provider bind type (CR-ADG-09). Applies to
/// tenant and policy binds, whose column is known from the catalog. Query literals follow their literal type: taking the type of
/// the compared column for user literals is a tracked precondition of X1.
/// </summary>
public static class ColumnBindAdapter
{
    private static readonly Regex TypeRegex = new(
        @"\A\s*(?<name>[A-Za-z][A-Za-z0-9_ ]*?)\s*(?:\(\s*(?<arg>max|[0-9]{1,5})\s*(?:,\s*[0-9]{1,3})?\s*\))?\s*\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    public static ColumnTypeClass Classify(string? dataType) => Parse(dataType).Class;

    public static AdaptedBind Adapt(ColumnBindStyle style, SqlParameterType type, object? value, string? columnType)
    {
        var unchanged = new AdaptedBind(type, value, false, 0);
        if (style == ColumnBindStyle.None || columnType is null || value is null or DBNull)
        {
            return unchanged;
        }

        var (cls, length) = Parse(columnType);
        switch (style)
        {
            case ColumnBindStyle.SqlServer:
                return type == SqlParameterType.String && cls == ColumnTypeClass.AnsiText
                    ? new AdaptedBind(type, value, AnsiString: true, length)
                    : unchanged;

            case ColumnBindStyle.Oracle:
                if (type == SqlParameterType.String && cls == ColumnTypeClass.Numeric &&
                    value is string text && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                {
                    return new AdaptedBind(SqlParameterType.Decimal, number, false, 0);
                }

                if ((type is SqlParameterType.Int32 or SqlParameterType.Int64 or SqlParameterType.Decimal) && (cls is ColumnTypeClass.AnsiText or ColumnTypeClass.UnicodeText))
                {
                    return new AdaptedBind(SqlParameterType.String, Convert.ToString(value, CultureInfo.InvariantCulture), false, 0);
                }

                return unchanged;

            default:
                return unchanged;
        }
    }

    private static (ColumnTypeClass Class, int Length) Parse(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return (ColumnTypeClass.Unknown, 0);
        var match = TypeRegex.Match(dataType);
        if (!match.Success) return (ColumnTypeClass.Unknown, 0);

        string name = match.Groups["name"].Value.Trim().ToLowerInvariant();
        string arg = match.Groups["arg"].Value;
        int length = arg.Length == 0 || arg.Equals("max", StringComparison.OrdinalIgnoreCase) ? 0 : int.Parse(arg, CultureInfo.InvariantCulture);

        var cls = name switch
        {
            "nvarchar" or "nchar" or "ntext" or "nvarchar2" or "nclob" or "national character varying" or "national char" => ColumnTypeClass.UnicodeText,
            "varchar" or "char" or "text" or "varchar2" or "clob" or "character varying" or "character" => ColumnTypeClass.AnsiText,
            "int" or "integer" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or "number" or "float" or "real"
                or "double" or "double precision" or "money" or "smallmoney" or "binary_float" or "binary_double" => ColumnTypeClass.Numeric,
            _ => ColumnTypeClass.Other
        };
        return (cls, length);
    }
}

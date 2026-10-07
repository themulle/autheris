namespace TrinoSqlEngine.Ast;

using System;
using System.Collections.Frozen;
using System.Security;
using System.Text.RegularExpressions;

/// <summary>
/// SQL-3: parts of the AST that generators emit verbatim (cast target types, EXTRACT fields, binary literals) are
/// restricted to plain tokens, so no quoted identifier or other dialect-specific lexeme reaches the target database.
/// </summary>
public static partial class SqlSafeTokens
{
    private static readonly FrozenSet<string> ExtractFields = new[]
    {
        "YEAR", "QUARTER", "MONTH", "WEEK", "DAY", "DAY_OF_MONTH", "DAY_OF_WEEK", "DOW", "DAY_OF_YEAR", "DOY",
        "YEAR_OF_WEEK", "YOW", "HOUR", "MINUTE", "SECOND", "MILLISECOND", "MICROSECOND", "TIMEZONE_HOUR",
        "TIMEZONE_MINUTE", "EPOCH", "ISODOW", "ISOYEAR", "DECADE", "CENTURY"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Words separated by single spaces, optional numeric parameters, ARRAY brackets; no quotes or symbols.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(?: [A-Za-z][A-Za-z0-9_]*)*(?:\(\d{1,4}(?:, ?\d{1,4})?\))?(?: [A-Za-z][A-Za-z0-9_]*)*(?:\[\d{1,6}\])?$")]
    private static partial Regex TypeNameRegex();

    [GeneratedRegex(@"^X'(?:[0-9A-Fa-f]{2}| )*'$")]
    private static partial Regex BinaryLiteralRegex();

    public static string EnsureTypeName(string typeName)
    {
        var normalized = string.Join(' ', (typeName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        normalized = normalized.Replace(" (", "(", StringComparison.Ordinal).Replace("( ", "(", StringComparison.Ordinal)
            .Replace(" )", ")", StringComparison.Ordinal).Replace(" ,", ",", StringComparison.Ordinal);
        if (!TypeNameRegex().IsMatch(normalized))
        {
            throw new SecurityException("The CAST target type is not permitted.");
        }

        return normalized;
    }

    public static string EnsureExtractField(string field)
    {
        if (string.IsNullOrWhiteSpace(field) || !ExtractFields.Contains(field))
        {
            throw new SecurityException("The EXTRACT field is not permitted.");
        }

        return field.ToUpperInvariant();
    }

    public static string EnsureBinaryLiteral(string literal)
    {
        if (string.IsNullOrEmpty(literal) || !BinaryLiteralRegex().IsMatch(literal))
        {
            throw new SecurityException("The binary literal is not a valid hexadecimal literal.");
        }

        return literal;
    }
}

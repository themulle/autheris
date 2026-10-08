namespace Autheris.Application.Services;

using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// SQL2-12: one text representation of a value before it is masked (HMAC pseudonyms in particular), shared by the REST
/// paths (ADO.NET values) and the GraphQL tree path (JSON values produced by the database), so the same value yields the
/// same pseudonym on every API.
/// </summary>
public static partial class MaskingInputCanonicalizer
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:?\d{2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTimestampRegex();

    public static string ToCanonicalString(object? value) => value switch
    {
        null => string.Empty,
        DateTime dt => FormatUtc(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime()),
        DateTimeOffset dto => FormatUtc(dto.UtcDateTime),
        DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        string s => CanonicalizeText(s),
        bool b => b ? "True" : "False",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>
    /// A JSON value as the tree path receives it from the database: strings as text, numbers with the database's own
    /// formatting (equal to the invariant ADO.NET representation), booleans like <see cref="bool.ToString()"/>.
    /// </summary>
    public static string FromJson(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.GetValueKind() switch
        {
            JsonValueKind.String => CanonicalizeText(node.GetValue<string>()),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => node.ToJsonString()
        };
    }

    /// <summary>Timestamps written as text (JSON, text columns) are normalized like DateTime values (UTC, ISO-8601 "O").</summary>
    private static string CanonicalizeText(string s)
    {
        if (s.Length >= 19 && IsoTimestampRegex().IsMatch(s) &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return FormatUtc(parsed.UtcDateTime);
        }

        return s;
    }

    private static string FormatUtc(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);
}

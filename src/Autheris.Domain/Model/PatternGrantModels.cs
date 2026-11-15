namespace Autheris.Domain.Model;

/// <summary>Determines how columns are resolved when a consent covers columns dynamically.</summary>
public enum ColumnSetMode
{
    /// <summary>Only columns explicitly listed in <see cref="Consent.ColumnRules"/> (or default rule behavior).</summary>
    Explicit = 0,

    /// <summary>All non-hidden non-sensitive columns of each matching object (sensitive columns stay masked unless explicitly cleared).</summary>
    AllNonHidden = 1
}

/// <summary>SQL serialization and parsing helpers for <see cref="ColumnSetMode"/>.</summary>
public static class ColumnSetModeExtensions
{
    public static string ToSqlString(this ColumnSetMode mode) => mode switch
    {
        ColumnSetMode.Explicit => "EXPLICIT",
        ColumnSetMode.AllNonHidden => "ALL_NON_HIDDEN",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown column set mode.")
    };

    public static bool TryParseColumnSetMode(string? value, out ColumnSetMode mode)
    {
        if (string.Equals(value, "EXPLICIT", StringComparison.OrdinalIgnoreCase))
        {
            mode = ColumnSetMode.Explicit;
            return true;
        }

        if (string.Equals(value, "ALL_NON_HIDDEN", StringComparison.OrdinalIgnoreCase))
        {
            mode = ColumnSetMode.AllNonHidden;
            return true;
        }

        mode = ColumnSetMode.Explicit;
        return false;
    }

    public static ColumnSetMode ParseColumnSetModeOrDefault(
        string? value,
        ColumnSetMode fallback = ColumnSetMode.Explicit) =>
        TryParseColumnSetMode(value, out var parsed) ? parsed : fallback;
}

/// <summary>
/// Structural validator and glob matcher for three-segment governance pattern grants (<c>source.schema.object</c>).
/// Requires a literal first segment (<c>data_source</c>) and permits <c>*</c> wildcards only in schema and object segments.
/// </summary>
public sealed class ObjectPatternSpec
{
    public const int MaxPatternLength = 512;
    public const int MaxSegmentLength = 256;
    private static readonly char[] ForbiddenChars = ['?', '(', ')', '[', ']', '{', '}', '\\', '|', '+', '^', '$'];

    private ObjectPatternSpec(string normalizedPattern, string dataSource, string schemaPattern, string objectNamePattern)
    {
        NormalizedPattern = normalizedPattern;
        DataSource = dataSource;
        SchemaPattern = schemaPattern;
        ObjectNamePattern = objectNamePattern;
    }

    public string NormalizedPattern { get; }
    public string DataSource { get; }
    public string SchemaPattern { get; }
    public string ObjectNamePattern { get; }

    public static bool TryParse(string? pattern, out ObjectPatternSpec? spec, out string error)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Object pattern must not be empty.";
            return false;
        }

        if (pattern.Length > MaxPatternLength)
        {
            error = $"Object pattern length ({pattern.Length}) exceeds maximum of {MaxPatternLength} characters.";
            return false;
        }

        if (pattern.IndexOfAny(ForbiddenChars) >= 0)
        {
            error = "Object pattern contains forbidden wildcard or regex metacharacters; only '*' is supported.";
            return false;
        }

        foreach (char ch in pattern)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                error = "Object pattern must not contain whitespace or control characters.";
                return false;
            }
        }

        var parts = pattern.Split('.');
        if (parts.Length != 3)
        {
            error = "Object pattern must have exactly three dot-separated segments: data_source.schema.object.";
            return false;
        }

        var source = parts[0];
        var schema = parts[1];
        var obj = parts[2];

        if (source.Length == 0 || schema.Length == 0 || obj.Length == 0)
        {
            error = "Each segment in object pattern must be non-empty.";
            return false;
        }

        if (source.Length > MaxSegmentLength || schema.Length > MaxSegmentLength || obj.Length > MaxSegmentLength)
        {
            error = $"Each object pattern segment must be at most {MaxSegmentLength} characters.";
            return false;
        }

        if (source.Contains('*', StringComparison.Ordinal))
        {
            error = "Object pattern must fix data_source as a literal identifier without '*' wildcards.";
            return false;
        }

        spec = new ObjectPatternSpec(pattern, source, schema, obj);
        error = string.Empty;
        return true;
    }

    public static ObjectPatternSpec Parse(string pattern) =>
        TryParse(pattern, out var spec, out var error)
            ? spec!
            : throw new InvalidOperationException(error);

    public bool Matches(TableIdentifier table) =>
        string.Equals(DataSource, table.Domain, StringComparison.OrdinalIgnoreCase) &&
        MatchesGlobIgnoreCase(SchemaPattern, table.Schema) &&
        MatchesGlobIgnoreCase(ObjectNamePattern, table.TableName);

    public bool Matches(string dataSource, string schemaName, string objectName) =>
        string.Equals(DataSource, dataSource, StringComparison.OrdinalIgnoreCase) &&
        MatchesGlobIgnoreCase(SchemaPattern, schemaName) &&
        MatchesGlobIgnoreCase(ObjectNamePattern, objectName);

    /// <summary>Linear-time wildcard matching for <c>*</c> only (zero regex/backtracking).</summary>
    public static bool MatchesGlobIgnoreCase(string pattern, string input)
    {
        if (string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return true;
        }

        int p = 0;
        int i = 0;
        int starIdx = -1;
        int matchIdx = 0;

        while (i < input.Length)
        {
            if (p < pattern.Length && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(input[i]))
            {
                p++;
                i++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                matchIdx = i;
                p++;
            }
            else if (starIdx != -1)
            {
                p = starIdx + 1;
                matchIdx++;
                i = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}

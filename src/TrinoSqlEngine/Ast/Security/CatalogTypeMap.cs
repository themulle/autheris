namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Number and form of the arguments a native type accepts.</summary>
internal enum NativeTypeArguments
{
    None,
    /// <summary>Zero or one numeric argument (precision, fractional seconds, length).</summary>
    OptionalOne,
    /// <summary>Exactly one argument, a length (a number or <c>max</c> where the dialect has it).</summary>
    RequiredLength,
    /// <summary>Zero, one or two numeric arguments (precision and scale).</summary>
    OptionalPrecisionScale
}

/// <summary>
/// CR-ADG-42: the closed set of provider-native column types the INSERT check option can cast to, per dialect. The check casts every
/// written value to the catalog type of its column, so the policy is evaluated on what the database will store (decimal rounding,
/// date and time truncation, length). A type that is unknown, not listed here, or listed for no dialect has no cast and the INSERT
/// is rejected (fail closed). The emitter and the coverage verifier both resolve through this one table, so the emitted type name
/// can never be request text.
/// </summary>
internal static class CatalogTypeMap
{
    private static readonly Regex TypeRegex = new(
        @"\A\s*(?<name>[A-Za-z][A-Za-z0-9_]*(?: [A-Za-z][A-Za-z0-9_]*)*?)\s*(?:\(\s*(?<a>max|[0-9]{1,5})\s*(?:,\s*(?<b>[0-9]{1,3}))?\s*\))?(?<tz>\s+with(?:out)?\s+time\s+zone)?\s*\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    private readonly record struct Entry(NativeTypeArguments Arguments, bool IsText = false, bool AllowsMax = false, bool AllowsTimeZone = false);

    private static readonly FrozenDictionary<string, Entry> SqlServer = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        ["bit"] = new(NativeTypeArguments.None), ["tinyint"] = new(NativeTypeArguments.None), ["smallint"] = new(NativeTypeArguments.None),
        ["int"] = new(NativeTypeArguments.None), ["bigint"] = new(NativeTypeArguments.None), ["real"] = new(NativeTypeArguments.None),
        ["money"] = new(NativeTypeArguments.None), ["smallmoney"] = new(NativeTypeArguments.None), ["date"] = new(NativeTypeArguments.None),
        ["smalldatetime"] = new(NativeTypeArguments.None), ["datetime"] = new(NativeTypeArguments.None), ["uniqueidentifier"] = new(NativeTypeArguments.None),
        ["float"] = new(NativeTypeArguments.OptionalOne), ["time"] = new(NativeTypeArguments.OptionalOne), ["datetime2"] = new(NativeTypeArguments.OptionalOne),
        ["datetimeoffset"] = new(NativeTypeArguments.OptionalOne),
        ["decimal"] = new(NativeTypeArguments.OptionalPrecisionScale), ["numeric"] = new(NativeTypeArguments.OptionalPrecisionScale),
        ["char"] = new(NativeTypeArguments.RequiredLength, IsText: true), ["nchar"] = new(NativeTypeArguments.RequiredLength, IsText: true),
        ["varchar"] = new(NativeTypeArguments.RequiredLength, IsText: true, AllowsMax: true),
        ["nvarchar"] = new(NativeTypeArguments.RequiredLength, IsText: true, AllowsMax: true),
        ["binary"] = new(NativeTypeArguments.RequiredLength), ["varbinary"] = new(NativeTypeArguments.RequiredLength, AllowsMax: true)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, Entry> PostgreSql = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        ["smallint"] = new(NativeTypeArguments.None), ["integer"] = new(NativeTypeArguments.None), ["int"] = new(NativeTypeArguments.None),
        ["bigint"] = new(NativeTypeArguments.None), ["real"] = new(NativeTypeArguments.None), ["double precision"] = new(NativeTypeArguments.None),
        ["boolean"] = new(NativeTypeArguments.None), ["date"] = new(NativeTypeArguments.None), ["uuid"] = new(NativeTypeArguments.None),
        ["bytea"] = new(NativeTypeArguments.None), ["text"] = new(NativeTypeArguments.None, IsText: true),
        ["numeric"] = new(NativeTypeArguments.OptionalPrecisionScale), ["decimal"] = new(NativeTypeArguments.OptionalPrecisionScale),
        ["varchar"] = new(NativeTypeArguments.OptionalOne, IsText: true), ["character varying"] = new(NativeTypeArguments.OptionalOne, IsText: true),
        ["char"] = new(NativeTypeArguments.OptionalOne, IsText: true), ["character"] = new(NativeTypeArguments.OptionalOne, IsText: true),
        ["time"] = new(NativeTypeArguments.OptionalOne, AllowsTimeZone: true), ["timestamp"] = new(NativeTypeArguments.OptionalOne, AllowsTimeZone: true)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, Entry> Oracle = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        ["number"] = new(NativeTypeArguments.OptionalPrecisionScale), ["float"] = new(NativeTypeArguments.OptionalOne),
        ["binary_float"] = new(NativeTypeArguments.None), ["binary_double"] = new(NativeTypeArguments.None), ["date"] = new(NativeTypeArguments.None),
        ["varchar2"] = new(NativeTypeArguments.RequiredLength, IsText: true), ["nvarchar2"] = new(NativeTypeArguments.RequiredLength, IsText: true),
        ["char"] = new(NativeTypeArguments.OptionalOne, IsText: true), ["nchar"] = new(NativeTypeArguments.OptionalOne, IsText: true),
        ["timestamp"] = new(NativeTypeArguments.OptionalOne, AllowsTimeZone: true), ["raw"] = new(NativeTypeArguments.RequiredLength)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, Entry> DuckDb = new Dictionary<string, Entry>(StringComparer.Ordinal)
    {
        ["boolean"] = new(NativeTypeArguments.None), ["tinyint"] = new(NativeTypeArguments.None), ["smallint"] = new(NativeTypeArguments.None),
        ["integer"] = new(NativeTypeArguments.None), ["int"] = new(NativeTypeArguments.None), ["bigint"] = new(NativeTypeArguments.None),
        ["real"] = new(NativeTypeArguments.None), ["float"] = new(NativeTypeArguments.None), ["double"] = new(NativeTypeArguments.None),
        ["date"] = new(NativeTypeArguments.None), ["time"] = new(NativeTypeArguments.None), ["timestamp"] = new(NativeTypeArguments.None),
        ["uuid"] = new(NativeTypeArguments.None), ["blob"] = new(NativeTypeArguments.None), ["text"] = new(NativeTypeArguments.None, IsText: true),
        ["varchar"] = new(NativeTypeArguments.OptionalOne, IsText: true),
        ["decimal"] = new(NativeTypeArguments.OptionalPrecisionScale), ["numeric"] = new(NativeTypeArguments.OptionalPrecisionScale)
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static FrozenDictionary<string, Entry>? TableOf(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => SqlServer,
        TargetSqlDialect.PostgreSql => PostgreSql,
        TargetSqlDialect.Oracle => Oracle,
        TargetSqlDialect.DuckDb => DuckDb,
        _ => null   // Databricks and the dialects without a governed DML path have no cast: the check option is rejected
    };

    /// <summary>
    /// Resolves a catalog data type to the native cast target of <paramref name="dialect"/>. Returns false for a null, empty,
    /// unknown or unlisted type (fail closed).
    /// </summary>
    public static bool TryResolve(TargetSqlDialect dialect, string? catalogType, out string native, out bool isText)
    {
        native = string.Empty;
        isText = false;
        var table = TableOf(dialect);
        if (table is null || string.IsNullOrWhiteSpace(catalogType)) return false;

        Match match;
        try
        {
            match = TypeRegex.Match(catalogType);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!match.Success) return false;
        string name = match.Groups["name"].Value.ToLowerInvariant();
        if (!table.TryGetValue(name, out var entry)) return false;

        bool hasA = match.Groups["a"].Success, hasB = match.Groups["b"].Success;
        string a = match.Groups["a"].Value.ToLowerInvariant();
        bool max = a == "max";
        string tz = match.Groups["tz"].Success ? Regex.Replace(match.Groups["tz"].Value.Trim().ToLowerInvariant(), @"\s+", " ") : string.Empty;

        if (tz.Length > 0 && !entry.AllowsTimeZone) return false;
        switch (entry.Arguments)
        {
            case NativeTypeArguments.None when hasA:
                return false;
            case NativeTypeArguments.RequiredLength when !hasA || hasB:
            case NativeTypeArguments.OptionalOne when hasB:
                return false;
        }

        if (max && !entry.AllowsMax) return false;
        if (hasB && entry.Arguments != NativeTypeArguments.OptionalPrecisionScale) return false;
        if (!max && hasA && (!int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out int an) || (an == 0 && entry.IsText))) return false;

        var sb = new StringBuilder();
        sb.Append(dialect == TargetSqlDialect.Oracle ? name.ToUpperInvariant() : name);
        if (hasA)
        {
            sb.Append('(').Append(a);
            if (hasB) sb.Append(',').Append(match.Groups["b"].Value);
            sb.Append(')');
        }

        if (tz.Length > 0) sb.Append(' ').Append(dialect == TargetSqlDialect.Oracle ? tz.ToUpperInvariant() : tz);
        native = sb.ToString();
        isText = entry.IsText;
        return true;
    }
}

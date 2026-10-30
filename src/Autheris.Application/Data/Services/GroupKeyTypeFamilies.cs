namespace Autheris.Application.Data.Services;

using Autheris.Application.Analytics;
using Autheris.Domain.Model;

/// <summary>
/// #405: closed vocabulary for the family of a refused aggregation group-key type. Only the literal of a family ever reaches the audit
/// entry; neither the raw type text nor a column name does.
/// </summary>
public enum GroupKeyTypeFamily
{
    Json,
    Xml,
    Spatial,
    Binary,
    Lob,
    Structured,
    Unknown
}

/// <summary>Classifies a refused group-key type into a <see cref="GroupKeyTypeFamily"/> and guards the group-key type.</summary>
public static class GroupKeyTypeFamilies
{
    /// <summary>Longest type text that is parsed; anything longer is <see cref="GroupKeyTypeFamily.Unknown"/> (I405-SC-05).</summary>
    public const int MaxTypeTextLength = 128;

    /// <summary>The fixed audit literal of a family (a closed set; no input text).</summary>
    public static string ToAuditLiteral(GroupKeyTypeFamily family) => family switch
    {
        GroupKeyTypeFamily.Json => "json",
        GroupKeyTypeFamily.Xml => "xml",
        GroupKeyTypeFamily.Spatial => "spatial",
        GroupKeyTypeFamily.Binary => "binary",
        GroupKeyTypeFamily.Lob => "lob",
        GroupKeyTypeFamily.Structured => "structured",
        _ => "unknown"
    };

    /// <summary>
    /// Total and fail-closed: null, blank, unknown, malformed or oversized input maps to <see cref="GroupKeyTypeFamily.Unknown"/>.
    /// The classifier only labels; it never decides whether a key is refused (see <see cref="DefaultReadOrdering.IsGroupableType"/>).
    /// </summary>
    public static GroupKeyTypeFamily Classify(string? dataType, DatabaseDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(dataType) || dataType.Length > MaxTypeTextLength)
        {
            return GroupKeyTypeFamily.Unknown;
        }

        var t = dataType.Trim().ToLowerInvariant();
        if (t.EndsWith("[]", StringComparison.Ordinal))
        {
            return GroupKeyTypeFamily.Structured;
        }

        var end = t.IndexOfAny(['(', ' ', '<']);
        var baseType = end < 0 ? t : t[..end];

        return dialect switch
        {
            DatabaseDialect.SqlServer => baseType switch
            {
                "xml" => GroupKeyTypeFamily.Xml,
                "geography" or "geometry" => GroupKeyTypeFamily.Spatial,
                "varbinary" or "binary" => GroupKeyTypeFamily.Binary,
                "text" or "ntext" or "image" => GroupKeyTypeFamily.Lob,
                "sql_variant" or "hierarchyid" => GroupKeyTypeFamily.Structured,
                _ => GroupKeyTypeFamily.Unknown
            },
            DatabaseDialect.PostgreSql => baseType switch
            {
                "json" or "jsonb" => GroupKeyTypeFamily.Json,
                "xml" => GroupKeyTypeFamily.Xml,
                "bytea" => GroupKeyTypeFamily.Binary,
                "geometry" or "geography" or "point" or "line" or "lseg" or "box" or "path" or "polygon" or "circle" => GroupKeyTypeFamily.Spatial,
                "hstore" or "tsvector" => GroupKeyTypeFamily.Structured,
                _ when baseType.EndsWith("range", StringComparison.Ordinal) => GroupKeyTypeFamily.Structured,
                _ => GroupKeyTypeFamily.Unknown
            },
            DatabaseDialect.Oracle => baseType switch
            {
                "clob" or "nclob" or "blob" or "long" or "bfile" => GroupKeyTypeFamily.Lob,
                "raw" => GroupKeyTypeFamily.Binary,
                "xmltype" => GroupKeyTypeFamily.Xml,
                "json" => GroupKeyTypeFamily.Json,
                "sdo_geometry" => GroupKeyTypeFamily.Spatial,
                _ => GroupKeyTypeFamily.Unknown
            },
            DatabaseDialect.Databricks => baseType switch
            {
                "map" or "struct" or "array" or "variant" => GroupKeyTypeFamily.Structured,
                "binary" => GroupKeyTypeFamily.Binary,
                _ => GroupKeyTypeFamily.Unknown
            },
            _ => GroupKeyTypeFamily.Unknown
        };
    }

    /// <summary>
    /// Throws <see cref="AggregationGroupKeyNotSupportedException"/> when the type is not groupable on the dialect. The one place that
    /// refuses a group-key type, shared by the audited validation and the planner.
    /// </summary>
    public static void EnsureGroupable(string? dataType, DatabaseDialect dialect)
    {
        if (!DefaultReadOrdering.IsGroupableType(dataType, dialect))
        {
            throw new AggregationGroupKeyNotSupportedException(Classify(dataType, dialect));
        }
    }
}

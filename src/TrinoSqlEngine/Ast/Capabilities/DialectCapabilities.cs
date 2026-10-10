namespace TrinoSqlEngine.Ast.Capabilities;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;

public enum DialectSupportTier { Production, Experimental, Internal }

public enum ParameterMarkerStyle { AtNamedOrdinal, DollarOrdinal, QuestionOrdinal, ColonNamedOrdinal, ColonOrdinal }

public enum PaginationStyle { LimitOffset, OffsetFetch, TopOrOffsetFetch }

public enum BooleanRepresentation { Native, Integer01, Number1 }

public enum IdentifierLengthUnit { Characters, Bytes }

/// <summary>
/// How the tenant column is compared (SEC-ADG-04, INV-15). Tenant comparison is always binary-exact, independent of the
/// column collation or session settings.
/// </summary>
public enum TenantComparisonStyle
{
    /// <summary>
    /// SQL Server: <c>col = @t AND CAST(CAST(col AS nvarchar(256)) AS varbinary(512)) = CAST(CAST(@t AS nvarchar(256)) AS varbinary(512))</c>.
    /// The plain equality keeps an index seek, the binary conjunct removes case, accent and trailing-space insensitivity.
    /// </summary>
    Utf16BinaryCast,

    /// <summary>
    /// DuckDB: <c>col = t AND encode(CAST(col AS varchar)) = encode(CAST(t AS varchar))</c>. <c>encode</c> yields a BLOB, whose
    /// comparison is exact regardless of any collation on the column.
    /// </summary>
    EncodedBlob,

    /// <summary>
    /// PostgreSQL: <c>col = t AND textsend(CAST(col AS text)) = textsend(CAST(t AS text))</c>. <c>textsend</c> yields the raw bytes,
    /// so the comparison is exact even when the column has a non-deterministic (case- or accent-insensitive) ICU collation or
    /// the type is <c>citext</c>.
    /// </summary>
    TextSendBytea,

    /// <summary>
    /// Oracle: <c>col = t AND UTL_RAW.CAST_TO_RAW(CAST(col AS VARCHAR2(4000))) = UTL_RAW.CAST_TO_RAW(CAST(t AS VARCHAR2(4000)))</c>.
    /// RAW comparison is byte-exact whatever <c>NLS_COMP</c>, <c>NLS_SORT</c> or the column collation (for example
    /// <c>BINARY_CI</c>) say. The binder rejects an empty tenant string because Oracle treats it as NULL.
    /// </summary>
    RawCast,

    /// <summary>
    /// Databricks: <c>CAST(col AS BINARY) = CAST(t AS BINARY)</c>. Casting STRING to BINARY yields the UTF-8 bytes, whose comparison
    /// is exact regardless of any column collation (for example <c>UTF8_LCASE</c>). There is deliberately no plain <c>col = t</c>
    /// conjunct: Spark's constant propagation would turn the pair into a tautology on collated columns.
    /// </summary>
    CastBinary
}

/// <summary>Declarative Trino-function to dialect rule. No matching rule means the function is rejected (INV-1).</summary>
public sealed record FunctionRewriteRule(string TrinoName, ImmutableArray<string> ArgumentTypeClasses, string TargetTemplate);

public sealed record DialectFunctionMap(FrozenDictionary<string, FunctionRewriteRule> Rules)
{
    public static DialectFunctionMap Empty { get; } = new(FrozenDictionary<string, FunctionRewriteRule>.Empty);

    /// <summary>
    /// Builds the map of <paramref name="dialect"/> from the reviewed allowlist (CR-ADG-02): identity rules, except where the
    /// dialect generator rewrites the function (the template then names the target form).
    /// </summary>
    public static DialectFunctionMap ForDialect(TargetSqlDialect dialect, IReadOnlyDictionary<string, string>? rewrites = null)
    {
        var rules = new Dictionary<string, FunctionRewriteRule>(StringComparer.Ordinal);
        foreach (var name in SqlFunctionAllowlists.CompilerNames(dialect))
        {
            string lower = name.ToLowerInvariant();
            string template = rewrites is not null && rewrites.TryGetValue(lower, out var target) ? target : name.ToUpperInvariant();
            rules[lower] = new FunctionRewriteRule(lower, ImmutableArray<string>.Empty, template);
        }

        return new DialectFunctionMap(rules.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>The Trino function names that have a rule; the compiler's default allowlist (null AllowedFunctions).</summary>
    public IReadOnlySet<string> Names { get; } = Rules.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool TryGetRule(string trinoName, out FunctionRewriteRule? rule)
    {
        ArgumentNullException.ThrowIfNull(trinoName);
        return Rules.TryGetValue(trinoName.ToLowerInvariant(), out rule);
    }
}

/// <summary>
/// Per-dialect limits and feature flags. Data, not code (plan section 4.5 and 5). <c>DmlStatements</c> lists the DML classes the
/// governed compiler may emit for the dialect (WP-A7); a class that is not listed is rejected fail closed.
/// </summary>
public sealed record DialectCapabilities(
    TargetSqlDialect Dialect,
    DialectSupportTier Tier,
    int MaxBindParameters,
    int? MaxInListItems,
    int MaxIdentifierLength,
    IdentifierLengthUnit IdentifierLengthUnit,
    char IdentifierOpenQuote,
    char IdentifierCloseQuote,
    ParameterMarkerStyle MarkerStyle,
    bool SupportsMarkerReuse,
    PaginationStyle Pagination,
    bool SupportsWithTies,
    bool SupportsNullsFirstLast,
    BooleanRepresentation Booleans,
    bool SupportsMerge,
    bool SupportsLateral,
    bool SupportsGroupingSets,
    bool SupportsFilterClause,
    bool SupportsTryCast,
    bool LimitGuaranteed,
    bool InDbHmac,
    TenantComparisonStyle TenantComparison,
    FrozenDictionary<SqlParameterType, string> BindExpressionTemplates,
    DialectFunctionMap Functions,
    IReadOnlySet<string> AllowedTableFunctions,
    string LimitSource,
    StatementPermissions DmlStatements = StatementPermissions.ReadOnly)
{
    /// <summary>Version of the capability data. Part of the compile cache key (SEC-ADG-01).</summary>
    public const string TableVersion = "cap-3";
}

public interface IDialectCapabilityProvider
{
    /// <summary>Capabilities of <paramref name="dialect"/>; an unknown or not yet supported dialect throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    DialectCapabilities Get(TargetSqlDialect dialect);
}

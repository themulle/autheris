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
    Utf16BinaryCast
}

/// <summary>Declarative Trino-function to dialect rule. No matching rule means the function is rejected (INV-1).</summary>
public sealed record FunctionRewriteRule(string TrinoName, ImmutableArray<string> ArgumentTypeClasses, string TargetTemplate);

public sealed record DialectFunctionMap(FrozenDictionary<string, FunctionRewriteRule> Rules)
{
    public static DialectFunctionMap Empty { get; } = new(FrozenDictionary<string, FunctionRewriteRule>.Empty);

    public bool TryGetRule(string trinoName, out FunctionRewriteRule? rule)
    {
        ArgumentNullException.ThrowIfNull(trinoName);
        return Rules.TryGetValue(trinoName.ToLowerInvariant(), out rule);
    }
}

/// <summary>Per-dialect limits and feature flags. Data, not code (plan section 4.5 and 5).</summary>
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
    string LimitSource)
{
    /// <summary>Version of the capability data. Part of the compile cache key (SEC-ADG-01).</summary>
    public const string TableVersion = "cap-1";
}

public interface IDialectCapabilityProvider
{
    /// <summary>Capabilities of <paramref name="dialect"/>; an unknown or not yet supported dialect throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    DialectCapabilities Get(TargetSqlDialect dialect);
}

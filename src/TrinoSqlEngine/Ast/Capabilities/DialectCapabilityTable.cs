namespace TrinoSqlEngine.Ast.Capabilities;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// The capability data. Only dialects with a production-ready governed SELECT path are listed; everything else fails
/// closed with <see cref="ArgumentOutOfRangeException"/> (INV-1). Other dialects are added by their own branches.
/// </summary>
public sealed class DialectCapabilityTable : IDialectCapabilityProvider
{
    // Bind expression templates are compile-time constants with exactly one placeholder (plan 16.4 item 2). T-SQL types the
    // parameter through the provider (SqlParameter.SqlDbType), so no wrapper is needed.
    private static readonly FrozenDictionary<SqlParameterType, string> SqlServerBindTemplates = BuildIdentityTemplates();

    private static readonly DialectCapabilities SqlServer = new(
        Dialect: TargetSqlDialect.SqlServer,
        Tier: DialectSupportTier.Production,
        MaxBindParameters: 2100,                                   // [MSSQL-CAP] 2,100 parameters per RPC
        MaxInListItems: null,
        MaxIdentifierLength: 128,
        IdentifierLengthUnit: IdentifierLengthUnit.Characters,
        IdentifierOpenQuote: '[',
        IdentifierCloseQuote: ']',
        MarkerStyle: ParameterMarkerStyle.AtNamedOrdinal,
        SupportsMarkerReuse: true,
        Pagination: PaginationStyle.OffsetFetch,
        SupportsWithTies: true,
        SupportsNullsFirstLast: false,                             // emulated with CASE by the generator
        Booleans: BooleanRepresentation.Integer01,
        SupportsMerge: true,
        SupportsLateral: false,                                    // T-SQL uses APPLY; LATERAL is rejected
        SupportsGroupingSets: true,
        SupportsFilterClause: false,
        SupportsTryCast: true,
        LimitGuaranteed: true,
        InDbHmac: true,
        TenantComparison: TenantComparisonStyle.Utf16BinaryCast,
        BindExpressionTemplates: SqlServerBindTemplates,
        Functions: DialectFunctionMap.Empty,
        AllowedTableFunctions: FrozenSet<string>.Empty,
        LimitSource: "MSSQL-CAP");

    private static readonly DialectCapabilities DuckDb = new(
        Dialect: TargetSqlDialect.DuckDb,
        Tier: DialectSupportTier.Production,
        MaxBindParameters: 65535,                                  // conservative; no published limit [DUCK-PREP], probed in tests
        MaxInListItems: null,
        MaxIdentifierLength: int.MaxValue,
        IdentifierLengthUnit: IdentifierLengthUnit.Characters,
        IdentifierOpenQuote: '"',
        IdentifierCloseQuote: '"',
        MarkerStyle: ParameterMarkerStyle.DollarOrdinal,
        SupportsMarkerReuse: true,
        Pagination: PaginationStyle.LimitOffset,
        SupportsWithTies: false,
        SupportsNullsFirstLast: true,
        Booleans: BooleanRepresentation.Native,
        SupportsMerge: true,
        SupportsLateral: true,
        SupportsGroupingSets: true,
        SupportsFilterClause: true,
        SupportsTryCast: true,
        LimitGuaranteed: true,
        InDbHmac: false,                                           // decision B-2: HMAC degrades to Redact
        TenantComparison: TenantComparisonStyle.EncodedBlob,
        BindExpressionTemplates: SqlServerBindTemplates,
        Functions: DialectFunctionMap.Empty,
        AllowedTableFunctions: FrozenSet<string>.Empty,
        LimitSource: "DUCK-PREP");

    private static readonly DialectCapabilities PostgreSql = new(
        Dialect: TargetSqlDialect.PostgreSql,
        Tier: DialectSupportTier.Production,
        MaxBindParameters: 65535,                                  // Int16 count in the Bind message [PG-PROTO]
        MaxInListItems: null,
        MaxIdentifierLength: 63,                                   // NAMEDATALEN - 1; silently truncated, so rejected [PG-LEX]
        IdentifierLengthUnit: IdentifierLengthUnit.Bytes,
        IdentifierOpenQuote: '"',
        IdentifierCloseQuote: '"',
        MarkerStyle: ParameterMarkerStyle.DollarOrdinal,
        SupportsMarkerReuse: true,
        Pagination: PaginationStyle.LimitOffset,
        SupportsWithTies: true,
        SupportsNullsFirstLast: true,
        Booleans: BooleanRepresentation.Native,
        SupportsMerge: true,
        SupportsLateral: true,
        SupportsGroupingSets: true,
        SupportsFilterClause: true,
        SupportsTryCast: false,
        LimitGuaranteed: true,
        InDbHmac: true,                                            // pgcrypto hmac(); a missing extension fails at execution
        TenantComparison: TenantComparisonStyle.TextSendBytea,
        BindExpressionTemplates: SqlServerBindTemplates,
        Functions: DialectFunctionMap.Empty,
        AllowedTableFunctions: FrozenSet<string>.Empty,
        LimitSource: "PG-PROTO");

    // Declared after the static capability entries: static initializers run in textual order.
    public static DialectCapabilityTable Default { get; } = new();

    private readonly FrozenDictionary<TargetSqlDialect, DialectCapabilities> _table;

    public DialectCapabilityTable()
    {
        _table = new Dictionary<TargetSqlDialect, DialectCapabilities>
        {
            [TargetSqlDialect.SqlServer] = SqlServer,
            [TargetSqlDialect.DuckDb] = DuckDb,
            [TargetSqlDialect.PostgreSql] = PostgreSql
        }.ToFrozenDictionary();
    }

    public DialectCapabilities Get(TargetSqlDialect dialect)
    {
        if (_table.TryGetValue(dialect, out var caps))
        {
            return caps;
        }

        throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "No capability entry exists for this dialect.");
    }

    private static FrozenDictionary<SqlParameterType, string> BuildIdentityTemplates()
    {
        var map = new Dictionary<SqlParameterType, string>();
        foreach (var type in Enum.GetValues<SqlParameterType>())
        {
            map[type] = "{0}";
        }

        return map.ToFrozenDictionary();
    }
}

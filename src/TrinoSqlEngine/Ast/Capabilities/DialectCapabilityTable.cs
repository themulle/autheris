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
    private static readonly FrozenDictionary<SqlParameterType, string> IdentityBindTemplates = BuildIdentityTemplates();

    // Trino functions that the dialect generator rewrites (documentation of the rule; the generator performs the rewrite).
    private static readonly IReadOnlyDictionary<string, string> SqlServerRewrites = new Dictionary<string, string>
    {
        ["length"] = "LEN", ["char_length"] = "LEN", ["character_length"] = "LEN", ["ceil"] = "CEILING", ["strpos"] = "CHARINDEX"
    };

    private static readonly IReadOnlyDictionary<string, string> OracleRewrites = new Dictionary<string, string> { ["strpos"] = "INSTR" };

    private static readonly IReadOnlyDictionary<string, string> DatabricksRewrites = new Dictionary<string, string>
    {
        ["strpos"] = "INSTR", ["approx_distinct"] = "APPROX_COUNT_DISTINCT", ["arbitrary"] = "ANY_VALUE"
    };

    // INSERT, UPDATE, DELETE and MERGE, compiled with the typed DML security of WP-A7. A dialect lists a class only when its
    // generator and its execution evidence exist; the default (ReadOnly) rejects DML fail closed.
    private const StatementPermissions AllDml =
        StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge;

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
        BindExpressionTemplates: IdentityBindTemplates,
        Functions: DialectFunctionMap.ForDialect(TargetSqlDialect.SqlServer, SqlServerRewrites),
        AllowedTableFunctions: FrozenSet<string>.Empty,
        LimitSource: "MSSQL-CAP",
        DmlStatements: AllDml);

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
        BindExpressionTemplates: IdentityBindTemplates,
        Functions: DialectFunctionMap.ForDialect(TargetSqlDialect.DuckDb),
        AllowedTableFunctions: FrozenSet<string>.Empty,
        LimitSource: "DUCK-PREP",
        DmlStatements: AllDml);

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
        BindExpressionTemplates: IdentityBindTemplates,
        Functions: DialectFunctionMap.ForDialect(TargetSqlDialect.PostgreSql),
        AllowedTableFunctions: FrozenSet<string>.Empty,
        LimitSource: "PG-PROTO",
        DmlStatements: AllDml);

    private static readonly DialectCapabilities Oracle = new(
        Dialect: TargetSqlDialect.Oracle,
        Tier: DialectSupportTier.Production,
        MaxBindParameters: 32767,                                  // follows jOOQ; confirmed by the Oracle Free probe test
        MaxInListItems: 1000,                                      // ORA-01795, kept separate from the bind limit
        MaxIdentifierLength: 128,                                  // bytes, Oracle 12.2+
        IdentifierLengthUnit: IdentifierLengthUnit.Bytes,
        IdentifierOpenQuote: '"',
        IdentifierCloseQuote: '"',
        MarkerStyle: ParameterMarkerStyle.ColonNamedOrdinal,
        SupportsMarkerReuse: true,
        Pagination: PaginationStyle.OffsetFetch,
        SupportsWithTies: true,
        SupportsNullsFirstLast: true,
        Booleans: BooleanRepresentation.Number1,
        SupportsMerge: true,
        SupportsLateral: true,
        SupportsGroupingSets: true,
        SupportsFilterClause: false,
        SupportsTryCast: false,
        LimitGuaranteed: true,
        InDbHmac: false,                                           // true only after a DBMS_CRYPTO grant probe (WP-F6)
        TenantComparison: TenantComparisonStyle.RawCast,
        BindExpressionTemplates: IdentityBindTemplates,
        Functions: DialectFunctionMap.ForDialect(TargetSqlDialect.Oracle, OracleRewrites),
        AllowedTableFunctions: FrozenSet<string>.Empty,
        LimitSource: "JOOQ",
        DmlStatements: AllDml,
        MergeShape: MergeClauseShape.OracleSingleClause);

    private static readonly DialectCapabilities Databricks = new(
        Dialect: TargetSqlDialect.Databricks,
        Tier: DialectSupportTier.Experimental,
        MaxBindParameters: 1000,                                   // provisional fail-closed budget until the live probe (WP-C5)
        MaxInListItems: null,
        MaxIdentifierLength: 255,
        IdentifierLengthUnit: IdentifierLengthUnit.Characters,
        IdentifierOpenQuote: '`',
        IdentifierCloseQuote: '`',
        MarkerStyle: ParameterMarkerStyle.ColonNamedOrdinal,
        SupportsMarkerReuse: true,
        Pagination: PaginationStyle.LimitOffset,
        SupportsWithTies: false,
        SupportsNullsFirstLast: true,
        Booleans: BooleanRepresentation.Native,
        SupportsMerge: true,
        SupportsLateral: false,                                    // accepted only after the Spark proxy confirms it
        SupportsGroupingSets: true,
        SupportsFilterClause: true,
        SupportsTryCast: true,
        LimitGuaranteed: true,
        InDbHmac: false,                                           // decision B-2: HMAC degrades to Redact
        TenantComparison: TenantComparisonStyle.CastBinary,
        BindExpressionTemplates: IdentityBindTemplates,
        Functions: DialectFunctionMap.ForDialect(TargetSqlDialect.Databricks, DatabricksRewrites),
        AllowedTableFunctions: FrozenSet<string>.Empty,
        LimitSource: "DBX-PARAM",
        DmlStatements: AllDml,
        SupportsSubqueryInDmlCondition: false,
        ReportsInsertRowCount: false);                              // Delta INSERT returns an empty result: no check-option row count (CR-ADG-35)                    // Delta: DELTA_UNSUPPORTED_SUBQUERY at analysis (CR-ADG-39)

    // Declared after the static capability entries: static initializers run in textual order.
    public static DialectCapabilityTable Default { get; } = new();

    private readonly FrozenDictionary<TargetSqlDialect, DialectCapabilities> _table;

    public DialectCapabilityTable()
    {
        _table = new Dictionary<TargetSqlDialect, DialectCapabilities>
        {
            [TargetSqlDialect.SqlServer] = SqlServer,
            [TargetSqlDialect.DuckDb] = DuckDb,
            [TargetSqlDialect.PostgreSql] = PostgreSql,
            [TargetSqlDialect.Oracle] = Oracle,
            [TargetSqlDialect.Databricks] = Databricks
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

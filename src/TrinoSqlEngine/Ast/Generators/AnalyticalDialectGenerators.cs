namespace TrinoSqlEngine.Ast.Generators;

using System;
using System.Collections.Frozen;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// ANSI standard SQL code generator.
/// </summary>
public sealed class AnsiDialectGenerator : SqlDialectGeneratorBase
{
    internal static readonly DialectCapabilities MaskRenderCapabilities = new(
        Dialect: TargetSqlDialect.Ansi,
        Tier: DialectSupportTier.Internal,
        MaxBindParameters: 65535,
        MaxInListItems: null,
        MaxIdentifierLength: 128,
        IdentifierLengthUnit: IdentifierLengthUnit.Characters,
        IdentifierOpenQuote: '"',
        IdentifierCloseQuote: '"',
        MarkerStyle: ParameterMarkerStyle.DollarOrdinal,
        SupportsMarkerReuse: true,
        Pagination: PaginationStyle.OffsetFetch,
        SupportsWithTies: true,
        SupportsNullsFirstLast: true,
        Booleans: BooleanRepresentation.Native,
        SupportsMerge: false,
        SupportsLateral: false,
        SupportsGroupingSets: true,
        SupportsFilterClause: true,
        SupportsTryCast: false,
        LimitGuaranteed: false,
        InDbHmac: false,
        TenantComparison: TenantComparisonStyle.TextSendBytea,
        BindExpressionTemplates: FrozenDictionary<SqlParameterType, string>.Empty,
        Functions: DialectFunctionMap.Empty,
        AllowedTableFunctions: FrozenSet<string>.Empty,
        LimitSource: "ansi-mask-render");

    public override TargetSqlDialect TargetDialect => TargetSqlDialect.Ansi;
    protected override bool SupportsAggregateFilter => true;
    protected override bool BindLiterals => true;

    private static readonly System.Text.RegularExpressions.Regex NativeTypeRegex = new(
        @"\A(?<name>[a-z][a-z0-9_]*(?: [a-z][a-z0-9_]*)*?)(?:\((?<args>[0-9]{1,4}(?:, ?[0-9]{1,4})?)\))?\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly FrozenSet<string> NumericOrTemporalTypes = new[]
    {
        "integer", "int", "bigint", "smallint", "tinyint", "numeric", "decimal", "real", "float", "double", "double precision",
        "boolean", "date", "time", "timestamp", "timestamp with time zone", "timestamp without time zone"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> TextTypes = new[]
    {
        "varchar", "character varying", "char", "character", "text", "clob", "nvarchar", "nchar"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static (string Text, bool NumericOrTemporal) NativeType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            throw new System.Security.SecurityException("A typed column mask requires the catalog data type of the column.");
        }

        var match = NativeTypeRegex.Match(dataType.Trim());
        if (!match.Success)
        {
            throw new System.Security.SecurityException("The catalog data type of a masked column is not permitted.");
        }

        string name = match.Groups["name"].Value.ToLowerInvariant();
        bool numeric = NumericOrTemporalTypes.Contains(name);
        if (!numeric && !TextTypes.Contains(name))
        {
            throw new System.Security.SecurityException("The catalog data type of a masked column is not permitted.");
        }

        string args = match.Groups["args"].Success ? "(" + match.Groups["args"].Value.Replace(" ", string.Empty, StringComparison.Ordinal) + ")" : string.Empty;
        return (name.ToUpperInvariant() + args, numeric);
    }

    protected override void FormatMask(ref ValueStringBuilder builder, MaskExpression mask, SqlEmitterContext context)
    {
        var (type, numeric) = NativeType(mask.DataType);
        string column = "\"" + mask.Column.Name.SimpleName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        string text = "CAST(" + column + " AS VARCHAR)";
        var args = mask.Arguments;

        switch (mask.Kind)
        {
            case Governance.MaskKind.Nullify:
                AppendStructural(ref builder, context, "CAST(NULL AS " + type + ")");
                break;

            case Governance.MaskKind.Redact:
                if (numeric)
                {
                    AppendStructural(ref builder, context, "CAST(NULL AS " + type + ")");
                }
                else
                {
                    builder.Append("CAST(");
                    builder.Append(context.BindPolicy(args.Constant ?? throw new System.Security.SecurityException($"The {mask.Kind} mask requires the Constant argument.")));
                    builder.Append(" AS VARCHAR)");
                }

                break;

            case Governance.MaskKind.Constant:
                builder.Append("CAST(");
                builder.Append(context.BindPolicy(args.Constant ?? throw new System.Security.SecurityException($"The {mask.Kind} mask requires the Constant argument.")));
                AppendStructural(ref builder, context, " AS " + type + ")");
                break;

            case Governance.MaskKind.PartialMask:
            {
                string prefix = "CASE WHEN CAST(" + context.BindPolicy(args.KeepPrefix ?? throw new System.Security.SecurityException($"The {mask.Kind} mask requires the KeepPrefix argument.")) + " AS INTEGER) < ";
                string prefixVal = "CAST(" + context.BindPolicy(args.KeepPrefix!) + " AS INTEGER)";
                string suffix = "CASE WHEN CAST(" + context.BindPolicy(args.KeepSuffix ?? throw new System.Security.SecurityException($"The {mask.Kind} mask requires the KeepSuffix argument.")) + " AS INTEGER) < ";
                string suffixVal = "CAST(" + context.BindPolicy(args.KeepSuffix!) + " AS INTEGER)";
                string maskChar = "CAST(" + context.BindPolicy(args.MaskChar ?? throw new System.Security.SecurityException($"The {mask.Kind} mask requires the MaskChar argument.")) + " AS VARCHAR)";

                void Clamped(ref ValueStringBuilder b, string cond, string val)
                {
                    b.Append('(');
                    b.Append(cond);
                    AppendInlineInteger(ref b, 0, context);
                    b.Append(" THEN ");
                    AppendInlineInteger(ref b, 0, context);
                    b.Append(" ELSE ");
                    b.Append(val);
                    b.Append(" END)");
                }

                builder.Append("CASE WHEN ");
                builder.Append(column);
                builder.Append(" IS NULL THEN NULL WHEN CHAR_LENGTH(");
                builder.Append(text);
                builder.Append(") <= (");
                Clamped(ref builder, prefix, prefixVal);
                builder.Append(" + ");
                Clamped(ref builder, suffix, suffixVal);
                builder.Append(") THEN ");
                builder.Append(maskChar);
                builder.Append(" ELSE SUBSTRING(");
                builder.Append(text);
                builder.Append(" FROM ");
                AppendInlineInteger(ref builder, 1, context);
                builder.Append(" FOR ");
                Clamped(ref builder, prefix, prefixVal);
                builder.Append(") || ");
                builder.Append(maskChar);
                builder.Append(" || SUBSTRING(");
                builder.Append(text);
                builder.Append(" FROM CHAR_LENGTH(");
                builder.Append(text);
                builder.Append(") - ");
                Clamped(ref builder, suffix, suffixVal);
                builder.Append(" + ");
                AppendInlineInteger(ref builder, 1, context);
                builder.Append(") END");
                break;
            }

            case Governance.MaskKind.GeoJitter:
            {
                int decimals = Math.Clamp(args.Decimals ?? 2, 0, 6);
                builder.Append("CASE WHEN ");
                builder.Append(column);
                builder.Append(" IS NULL OR ");
                builder.Append(column);
                builder.Append(" = ");
                AppendStructural(ref builder, context, "0.0");
                builder.Append(" THEN ");
                builder.Append(column);
                builder.Append(" ELSE ROUND(");
                builder.Append(column);
                builder.Append(", ");
                AppendInlineInteger(ref builder, decimals, context);
                builder.Append(") END");
                break;
            }

            case Governance.MaskKind.TruncateDate:
            {
                string unitFrag = (args.TruncateUnit ?? throw new System.Security.SecurityException($"The {mask.Kind} mask requires the TruncateUnit argument.")) switch
                {
                    Governance.MaskDateTruncateUnit.Year => "'year'",
                    Governance.MaskDateTruncateUnit.Month => "'month'",
                    Governance.MaskDateTruncateUnit.Day => "'day'",
                    _ => throw UnsupportedConstruct($"column mask {mask.Kind}", TargetDialect)
                };
                builder.Append("CAST(DATE_TRUNC(");
                AppendConstantFragment(ref builder, context, unitFrag);
                builder.Append(", ");
                builder.Append(column);
                builder.Append(')');
                AppendStructural(ref builder, context, " AS " + type + ")");
                break;
            }

            case Governance.MaskKind.BucketNumber:
            {
                string width = context.BindPolicy(args.BucketWidth ?? throw new System.Security.SecurityException($"The {mask.Kind} mask requires the BucketWidth argument."));
                string calc = type.StartsWith("DOUBLE", StringComparison.Ordinal) || type.StartsWith("FLOAT", StringComparison.Ordinal) || type.StartsWith("REAL", StringComparison.Ordinal)
                    ? "DOUBLE PRECISION"
                    : "DECIMAL(38, 10)";
                builder.Append("CAST(FLOOR(CAST(");
                builder.Append(column);
                builder.Append(" AS ");
                AppendStructural(ref builder, context, calc);
                builder.Append(") / CAST(");
                builder.Append(width);
                builder.Append(" AS ");
                AppendStructural(ref builder, context, calc);
                builder.Append(")) * CAST(");
                builder.Append(width);
                builder.Append(" AS ");
                AppendStructural(ref builder, context, calc);
                AppendStructural(ref builder, context, ") AS " + type + ")");
                break;
            }

            case Governance.MaskKind.RoundNumber:
            {
                string digits = context.BindPolicy(args.RoundDigits ?? throw new System.Security.SecurityException($"The {mask.Kind} mask requires the RoundDigits argument."));
                builder.Append("CAST(ROUND(");
                builder.Append(column);
                builder.Append(", CAST(");
                builder.Append(digits);
                AppendStructural(ref builder, context, " AS INTEGER)) AS " + type + ")");
                break;
            }

            case Governance.MaskKind.EmailDomain:
            {
                builder.Append("CASE WHEN ");
                builder.Append(column);
                builder.Append(" IS NULL THEN ");
                AppendStructural(ref builder, context, "CAST(NULL AS VARCHAR)");
                builder.Append(" WHEN POSITION(");
                AppendConstantFragment(ref builder, context, "'@'");
                builder.Append(" IN ");
                builder.Append(text);
                builder.Append(") > ");
                AppendInlineInteger(ref builder, 1, context);
                builder.Append(" AND POSITION(");
                AppendConstantFragment(ref builder, context, "'@'");
                builder.Append(" IN ");
                builder.Append(text);
                builder.Append(") < CHAR_LENGTH(");
                builder.Append(text);
                builder.Append(") THEN ");
                AppendConstantFragment(ref builder, context, "'***@'");
                builder.Append(" || SUBSTRING(");
                builder.Append(text);
                builder.Append(" FROM POSITION(");
                AppendConstantFragment(ref builder, context, "'@'");
                builder.Append(" IN ");
                builder.Append(text);
                builder.Append(") + ");
                AppendInlineInteger(ref builder, 1, context);
                builder.Append(") ELSE ");
                AppendConstantFragment(ref builder, context, "'***@***'");
                builder.Append(" END");
                break;
            }

            default:
                throw UnsupportedConstruct($"column mask {mask.Kind}", TargetDialect);
        }
    }

    public override void FormatIdentifier(ref ValueStringBuilder builder, SqlIdentifier identifier, SqlEmitterContext context)
    {
        builder.Append('"');
        builder.Append(identifier.Value.Replace("\"", "\"\"", StringComparison.Ordinal));
        builder.Append('"');
    }

    public override void FormatStringLiteral(ref ValueStringBuilder builder, string value, SqlEmitterContext context)
    {
        builder.Append('\'');
        builder.Append(value.Replace("'", "''", StringComparison.Ordinal));
        builder.Append('\'');
    }

    public override void FormatBoolean(ref ValueStringBuilder builder, bool value, SqlEmitterContext context)
    {
        builder.Append(value ? "TRUE" : "FALSE");
    }

    protected override void GeneratePagination(PaginationClause pagination, OrderByClause? orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (pagination.WithTies)
        {
            if (orderBy == null)
            {
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException("FETCH … WITH TIES requires an ORDER BY clause.");
            }

            if (pagination.Offset != null)
            {
                builder.Append("OFFSET ");
                GenerateExpression(pagination.Offset, ref builder, context);
                builder.Append(" ROWS ");
            }

            if (pagination.Limit != null)
            {
                builder.Append("FETCH FIRST ");
                GenerateExpression(pagination.Limit, ref builder, context);
                builder.Append(" ROWS WITH TIES");
            }
            return;
        }

        if (pagination.Limit != null)
        {
            builder.Append("LIMIT ");
            GenerateExpression(pagination.Limit, ref builder, context);
        }

        if (pagination.Offset != null)
        {
            if (pagination.Limit != null) builder.Append(' ');
            builder.Append("OFFSET ");
            GenerateExpression(pagination.Offset, ref builder, context);
        }
    }
}

/// <summary>
/// DuckDB Lakehouse analytical dialect generator.
/// </summary>
public sealed class DuckDbDialectGenerator : SqlDialectGeneratorBase
{
    public override TargetSqlDialect TargetDialect => TargetSqlDialect.DuckDb;
    protected override bool SupportsTryCast => true;
    protected override bool SupportsAggregateFilter => true;
    protected override bool SupportsOrderedAggregates => true;
    protected override bool SupportsGroupByDistinct => true;
    protected override bool SupportsGroupByAll => true;

    // The inline structural positions of this generator are registered with the emitter context ( for DuckDB).
    protected override bool BindLiterals => true;

    protected override bool EmitsTimeZoneConversion => true;

    /// <summary>
    /// <c>TIMEZONE(to, TIMEZONE(from, ts))</c>: <c>timezone(zone, TIMESTAMP)</c> reads the plain value as local time of <c>zone</c> (a <c>TIMESTAMPTZ</c> instant),
    /// <c>timezone(zone, TIMESTAMPTZ)</c> returns the local plain <c>TIMESTAMP</c>. The ICU build of the library does the work; the gateway never installs an extension.
    /// </summary>
    protected override void FormatConvertTimeZone(ref ValueStringBuilder builder, FunctionCallExpression fn, SqlEmitterContext context)
    {
        builder.Append("TIMEZONE(");
        AppendZoneArgument(ref builder, fn.Arguments[1], context);
        builder.Append(", TIMEZONE(");
        AppendZoneArgument(ref builder, fn.Arguments[0], context);
        builder.Append(", ");
        GenerateExpression(fn.Arguments[2], ref builder, context);
        builder.Append("))");
    }

    /// <summary>Virtual filters (phase 7b): <c>(x + INTERVAL '±n unit')</c>; on the governed path the amount is bound.</summary>
    protected override void FormatDateAdd(ref ValueStringBuilder builder, DateUnit unit, long amount, Expression source, SqlEmitterContext context)
    {
        if (context.IsBound)
        {
            builder.Append('(');
            GenerateExpression(source, ref builder, context);
            builder.Append(" + INTERVAL (CAST(");
            builder.Append(BindInteger(amount, context));
            builder.Append(" AS BIGINT)) ");
            builder.Append(DateUnitName(unit).ToUpperInvariant());
            builder.Append(')');
            return;
        }

        builder.Append('(');
        GenerateExpression(source, ref builder, context);
        builder.Append(" + INTERVAL '");
        builder.Append(amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(DateUnitName(unit));
        builder.Append("')");
    }

    /// <summary><c>DATE_TRUNC('unit', x)</c>: the unit keyword comes from a closed enum and is a reviewed constant fragment.</summary>
    protected override void FormatDateTrunc(ref ValueStringBuilder builder, DateUnit unit, Expression source, SqlEmitterContext context)
    {
        builder.Append("DATE_TRUNC(");
        AppendConstantFragment(ref builder, context, "'" + DateUnitName(unit) + "'");
        builder.Append(", ");
        GenerateExpression(source, ref builder, context);
        builder.Append(')');
    }

    protected override void FormatTypedLiteral(ref ValueStringBuilder builder, TypedLiteralExpression literal, SqlEmitterContext context)
    {
        if (!context.IsBound)
        {
            base.FormatTypedLiteral(ref builder, literal, context);
            return;
        }

        var (value, type) = ParseTypedLiteralValue(literal);
        builder.Append("CAST(");
        builder.Append(context.BindValue(value, type, ParameterOrigin.QueryLiteral));
        builder.Append(literal.Kind switch
        {
            TypedLiteralKind.Date => " AS DATE)",
            TypedLiteralKind.Time => " AS TIME)",
            _ => " AS TIMESTAMP)"
        });
    }

    protected override void FormatIntervalLiteral(ref ValueStringBuilder builder, IntervalLiteralExpression interval, SqlEmitterContext context)
    {
        if (!context.IsBound)
        {
            base.FormatIntervalLiteral(ref builder, interval, context);
            return;
        }

        // INTERVAL (n) FIELD: the amount is a bound integer, the field a validated keyword.
        builder.Append("INTERVAL (CAST(");
        builder.Append(BindInteger(long.Parse(interval.Value, System.Globalization.CultureInfo.InvariantCulture), context));
        builder.Append(" AS BIGINT)) ");
        builder.Append(interval.Field);
    }

    // ---- typed column masks ----

    private static readonly System.Text.RegularExpressions.Regex NativeTypeRegex = new(
        @"\A(?<name>[a-z][a-z0-9]*)(?:\((?<args>[0-9]{1,4}(?:, ?[0-9]{1,4})?)\))?\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly System.Collections.Frozen.FrozenSet<string> NumericOrTemporalTypes = new[]
    {
        "tinyint", "smallint", "integer", "int", "bigint", "hugeint", "utinyint", "usmallint", "uinteger", "ubigint", "decimal",
        "numeric", "double", "float", "real", "boolean", "bool", "date", "time", "timestamp", "timestamptz", "uuid"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly System.Collections.Frozen.FrozenSet<string> TextTypes = new[]
    {
        "varchar", "char", "bpchar", "text", "string", "blob"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static (string Text, bool NumericOrTemporal) NativeType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            throw new System.Security.SecurityException("A typed column mask requires the catalog data type of the column.");
        }

        var match = NativeTypeRegex.Match(dataType.Trim());
        if (!match.Success)
        {
            throw new System.Security.SecurityException("The catalog data type of a masked column is not permitted.");
        }

        string name = match.Groups["name"].Value.ToLowerInvariant();
        bool numeric = NumericOrTemporalTypes.Contains(name);
        if (!numeric && !TextTypes.Contains(name))
        {
            throw new System.Security.SecurityException("The catalog data type of a masked column is not permitted.");
        }

        string args = match.Groups["args"].Success ? "(" + match.Groups["args"].Value.Replace(" ", string.Empty, StringComparison.Ordinal) + ")" : string.Empty;
        return (name.ToUpperInvariant() + args, numeric);
    }

    private static void Put(ref ValueStringBuilder builder, params ReadOnlySpan<string> parts)
    {
        foreach (var part in parts) builder.Append(part);
    }

    protected override void FormatMask(ref ValueStringBuilder builder, MaskExpression mask, SqlEmitterContext context)
    {
        var (type, numeric) = NativeType(mask.DataType);
        string column = "\"" + mask.Column.Name.SimpleName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var args = mask.Arguments;

        switch (mask.Kind)
        {
            case Governance.MaskKind.Nullify:
                AppendStructural(ref builder, context, "CAST(NULL AS " + type + ")");
                break;

            case Governance.MaskKind.Redact:
                if (numeric)
                {
                    AppendStructural(ref builder, context, "CAST(NULL AS " + type + ")");
                }
                else
                {
                    builder.Append("CAST(");
                    builder.Append(context.BindPolicy(args.Constant ?? throw MissingArgument(mask, "Constant")));
                    builder.Append(" AS VARCHAR)");
                }

                break;

            case Governance.MaskKind.Constant:
                builder.Append("CAST(");
                builder.Append(context.BindPolicy(args.Constant ?? throw MissingArgument(mask, "Constant")));
                AppendStructural(ref builder, context, " AS " + type + ")");
                break;

            case Governance.MaskKind.PartialMask:
            {
                // greatest(n, 0): a negative count would make left()/right() count from the other end and expose the value.
                string prefix = "greatest(CAST(" + context.BindPolicy(args.KeepPrefix ?? throw MissingArgument(mask, "KeepPrefix")) + " AS BIGINT), ";
                string suffix = "greatest(CAST(" + context.BindPolicy(args.KeepSuffix ?? throw MissingArgument(mask, "KeepSuffix")) + " AS BIGINT), ";
                string maskChar = "CAST(" + context.BindPolicy(args.MaskChar ?? throw MissingArgument(mask, "MaskChar")) + " AS VARCHAR)";

                void Clamped(ref ValueStringBuilder b, string head)
                {
                    b.Append(head);
                    AppendInlineInteger(ref b, 0, context);
                    b.Append(')');
                }

                void Keep(ref ValueStringBuilder b)
                {
                    b.Append('(');
                    Clamped(ref b, prefix);
                    b.Append(" + ");
                    Clamped(ref b, suffix);
                    b.Append(')');
                }

                Put(ref builder, "CASE WHEN ", column, " IS NULL THEN NULL WHEN length(", column, ") <= ");
                Keep(ref builder);
                Put(ref builder, " THEN repeat(", maskChar, ", ");
                AppendInlineInteger(ref builder, 5, context);
                Put(ref builder, ") ELSE concat(left(", column, ", ");
                Clamped(ref builder, prefix);
                Put(ref builder, "), repeat(", maskChar, ", CASE WHEN length(", column, ") > ");
                Keep(ref builder);
                Put(ref builder, " THEN length(", column, ") - ");
                Keep(ref builder);
                Put(ref builder, " ELSE ");
                AppendInlineInteger(ref builder, 5, context);
                Put(ref builder, " END), right(", column, ", ");
                Clamped(ref builder, suffix);
                builder.Append(")) END");
                break;
            }

            case Governance.MaskKind.GeoJitter:
            {
                int decimals = Math.Clamp(args.Decimals ?? 2, 0, 6);
                Put(ref builder, "CASE WHEN ", column, " IS NULL OR ", column, " = ");
                AppendStructural(ref builder, context, "0.0");
                Put(ref builder, " THEN ", column, " ELSE round(", column, ", ");
                AppendInlineInteger(ref builder, decimals, context);
                builder.Append(") END");
                break;
            }

            case Governance.MaskKind.TruncateDate:
            {
                string unitFrag = (args.TruncateUnit ?? throw MissingArgument(mask, "TruncateUnit")) switch
                {
                    Governance.MaskDateTruncateUnit.Year => "'year'",
                    Governance.MaskDateTruncateUnit.Month => "'month'",
                    Governance.MaskDateTruncateUnit.Day => "'day'",
                    _ => throw UnsupportedConstruct($"column mask {mask.Kind}", TargetDialect)
                };
                builder.Append("CAST(DATE_TRUNC(");
                AppendConstantFragment(ref builder, context, unitFrag);
                Put(ref builder, ", ", column, ")");
                AppendStructural(ref builder, context, " AS " + type + ")");
                break;
            }

            case Governance.MaskKind.BucketNumber:
            {
                string width = context.BindPolicy(args.BucketWidth ?? throw MissingArgument(mask, "BucketWidth"));
                string calc = type.StartsWith("DOUBLE", StringComparison.Ordinal) || type.StartsWith("FLOAT", StringComparison.Ordinal) || type.StartsWith("REAL", StringComparison.Ordinal)
                    ? "DOUBLE PRECISION"
                    : "DECIMAL(38, 10)";
                Put(ref builder, "CAST(FLOOR(CAST(", column, " AS ");
                AppendStructural(ref builder, context, calc);
                Put(ref builder, ") / CAST(", width, " AS ");
                AppendStructural(ref builder, context, calc);
                Put(ref builder, ")) * CAST(", width, " AS ");
                AppendStructural(ref builder, context, calc);
                AppendStructural(ref builder, context, ") AS " + type + ")");
                break;
            }

            case Governance.MaskKind.RoundNumber:
            {
                string digits = context.BindPolicy(args.RoundDigits ?? throw MissingArgument(mask, "RoundDigits"));
                Put(ref builder, "CAST(round(", column, ", CAST(", digits, " AS INTEGER))");
                AppendStructural(ref builder, context, " AS " + type + ")");
                break;
            }

            case Governance.MaskKind.EmailDomain:
            {
                string text = "CAST(" + column + " AS VARCHAR)";
                Put(ref builder, "CASE WHEN ", column, " IS NULL THEN ");
                AppendStructural(ref builder, context, "CAST(NULL AS VARCHAR)");
                builder.Append(" WHEN POSITION(");
                AppendConstantFragment(ref builder, context, "'@'");
                Put(ref builder, " IN ", text, ") > ");
                AppendInlineInteger(ref builder, 1, context);
                builder.Append(" AND POSITION(");
                AppendConstantFragment(ref builder, context, "'@'");
                Put(ref builder, " IN ", text, ") < length(", text, ") THEN concat(");
                AppendConstantFragment(ref builder, context, "'***@'");
                Put(ref builder, ", substring(", text, " FROM POSITION(");
                AppendConstantFragment(ref builder, context, "'@'");
                Put(ref builder, " IN ", text, ") + ");
                AppendInlineInteger(ref builder, 1, context);
                builder.Append(")) ELSE ");
                AppendConstantFragment(ref builder, context, "'***@***'");
                builder.Append(" END");
                break;
            }

            default:
                // HMAC and Tokenize are not computed inside DuckDB (InDbHmac = false): the compiler degrades them to Redact before emission.
                throw UnsupportedConstruct($"column mask {mask.Kind}", TargetDialect);
        }
    }

    private static System.Security.SecurityException MissingArgument(MaskExpression mask, string argument) =>
        new($"The {mask.Kind} mask requires the {argument} argument.");

    public override void FormatIdentifier(ref ValueStringBuilder builder, SqlIdentifier identifier, SqlEmitterContext context)
    {
        builder.Append('"');
        string val = identifier.IsQuoted ? identifier.Value : identifier.Value.ToLowerInvariant();
        builder.Append(val.Replace("\"", "\"\"", StringComparison.Ordinal));
        builder.Append('"');
    }

    public override void FormatStringLiteral(ref ValueStringBuilder builder, string value, SqlEmitterContext context)
    {
        builder.Append('\'');
        builder.Append(value.Replace("'", "''", StringComparison.Ordinal));
        builder.Append('\'');
    }

    public override void FormatBoolean(ref ValueStringBuilder builder, bool value, SqlEmitterContext context)
    {
        builder.Append(value ? "TRUE" : "FALSE");
    }

    protected override void GeneratePagination(PaginationClause pagination, OrderByClause? orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (pagination.WithTies)
        {
            throw UnsupportedConstruct("FETCH … WITH TIES", TargetDialect, TrinoSqlEngine.Ast.Builder.SqlConstructCatalog.FetchWithTies);
        }

        if (pagination.Limit != null)
        {
            builder.Append("LIMIT ");
            GenerateStructuralInteger(pagination.Limit, ref builder, context);
        }

        if (pagination.Offset != null)
        {
            if (pagination.Limit != null) builder.Append(' ');
            builder.Append("OFFSET ");
            GenerateStructuralInteger(pagination.Offset, ref builder, context);
        }
    }
}


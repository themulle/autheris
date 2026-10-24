namespace TrinoSqlEngine.Ast.Generators;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;

/// <summary>
/// SQLite code generator.
/// Handles "identifier" ANSI double-quote quoting with "" escaping, ?1 parameter placeholders (budget 999),
/// integer booleans (1/0), standard strings, and LIMIT ... OFFSET ... pagination.
/// </summary>
public sealed class SqliteDialectGenerator : SqlDialectGeneratorBase
{
    public override TargetSqlDialect TargetDialect => TargetSqlDialect.Sqlite;
    protected override bool BindLiterals => true;   // WP-X0h: every value is bound on the governed path (SD-3), like the other production dialects
    protected override bool SupportsAggregateFilter => true;
    protected override bool SupportsGroupingSets => false;

    protected override string SubstringFunctionName => "SUBSTR";

    /// <summary>
    /// Issue #339: in SQLite CROSS JOIN is a join-order hint (the left operand is always the outer loop, the planner never reorders it),
    /// which makes wide comma joins of derived tables near-cartesian. A plain JOIN without ON is the same cartesian inner join, but the
    /// planner may reorder it.
    /// </summary>
    protected override string CrossJoinKeyword => "JOIN";

    /// <summary>
    /// <c>blob</c> is the tenant comparison cast; <c>varbinary</c> maps to it, because SQLite would give the spelling "varbinary" a
    /// NUMERIC affinity (the name contains neither BLOB nor a text keyword).
    /// </summary>
    protected override string FormatTypeName(TrinoType type) =>
        type.Name is "blob" or "varbinary" ? "BLOB" : StandardTypeName(type);

    /// <summary>Wunsch 4: SQLite's null-safe comparison is IS / IS NOT.</summary>
    protected override void FormatIsDistinctFrom(ref ValueStringBuilder builder, IsDistinctFromExpression dist, SqlEmitterContext context)
    {
        GeneratePredicateOperand(dist.Left, ref builder, context);
        builder.Append(dist.IsNotDistinctFrom ? " IS " : " IS NOT ");
        GeneratePredicateOperand(dist.Right, ref builder, context);
    }

    protected override void FormatCurrentDateTime(ref ValueStringBuilder builder, CurrentDateTimeKind kind, SqlEmitterContext context)
    {
        if (context.IsBound && kind is CurrentDateTimeKind.LocalTime or CurrentDateTimeKind.LocalTimestamp)
        {
            throw NoBoundForm("LOCALTIME and LOCALTIMESTAMP", TargetDialect);
        }

        builder.Append(kind switch
        {
            CurrentDateTimeKind.CurrentDate => "CURRENT_DATE",
            CurrentDateTimeKind.CurrentTime => "CURRENT_TIME",
            CurrentDateTimeKind.CurrentTimestamp => "CURRENT_TIMESTAMP",
            CurrentDateTimeKind.LocalTime => "time('now', 'localtime')",
            _ => "datetime('now', 'localtime')"
        });
    }

    /// <summary>SQLite: TRIM/LTRIM/RTRIM(x[, chars]).</summary>
    protected override void FormatTrim(ref ValueStringBuilder builder, TrimExpression trim, SqlEmitterContext context)
    {
        builder.Append(trim.Specification switch
        {
            TrimSpecification.Leading => "LTRIM(",
            TrimSpecification.Trailing => "RTRIM(",
            _ => "TRIM("
        });
        GenerateExpression(trim.Source, ref builder, context);
        if (trim.Characters != null)
        {
            builder.Append(", ");
            GenerateExpression(trim.Characters, ref builder, context);
        }
        builder.Append(')');
    }

    protected override void FormatPosition(ref ValueStringBuilder builder, PositionExpression position, SqlEmitterContext context) =>
        FormatInstr(ref builder, position, context);

    /// <summary>
    /// Appends a generator-owned text token as an argument. SEC-PG-01/-02: on the governed path the token (strftime format,
    /// datetime modifier) is a bound string parameter, so the emitted SQL contains no string literal; it never comes from client text.
    /// </summary>
    private static void AppendToken(ref ValueStringBuilder builder, string token, SqlEmitterContext context)
    {
        if (context.IsBound)
        {
            builder.Append(context.BindValue(token, SqlParameterType.String, ParameterOrigin.QueryLiteral));
            return;
        }

        builder.Append('\'');
        builder.Append(token);
        builder.Append('\'');
    }

    /// <summary>Wunsch 4: SQLite has no EXTRACT; strftime on ISO text, with the ISO day of week (%w counts Sunday = 0).</summary>
    protected override void FormatExtract(ref ValueStringBuilder builder, string field, Expression source, SqlEmitterContext context)
    {
        (string format, string prefix, string suffix) = field switch
        {
            "YEAR" => ("%Y", "", ""),
            "MONTH" => ("%m", "", ""),
            "DAY" => ("%d", "", ""),
            "HOUR" => ("%H", "", ""),
            "MINUTE" => ("%M", "", ""),
            "SECOND" => ("%S", "", ""),
            "DAY_OF_YEAR" => ("%j", "", ""),
            "DAY_OF_WEEK" => ("%w", "((", " + 6) % 7 + 1)"),
            "QUARTER" => ("%m", "((", " + 2) / 3)"),
            _ => throw UnsupportedExtract(field, TargetDialect)
        };
        AppendStructural(ref builder, context, prefix);
        builder.Append("CAST(strftime(");
        AppendToken(ref builder, format, context);
        builder.Append(", ");
        GenerateExpression(source, ref builder, context);
        builder.Append(") AS INTEGER)");
        AppendStructural(ref builder, context, suffix);
    }

    /// <summary>
    /// GAP-007: SQLite stores dates as ISO text; <c>CAST(x AS date)</c> would give the numeric affinity (<c>'2024-02-29'</c> becomes 2024),
    /// so DATE, TIMESTAMP and TIME map to <c>date(x)</c>, <c>datetime(x)</c> and <c>time(x)</c> (function names are generator tokens).
    /// </summary>
    protected override bool TryFormatCast(ref ValueStringBuilder builder, CastExpression cast, SqlEmitterContext context)
    {
        if (cast.IsTryCast || cast.IsNativeType)
        {
            return false;
        }

        var type = ParseTypeName(cast.TargetType);
        if (type.WithTimeZone)
        {
            return false;
        }

        string? function = type.Name switch
        {
            "date" => "date(",
            "timestamp" => "datetime(",
            "time" => "time(",
            _ => null
        };
        if (function is null)
        {
            return false;
        }

        builder.Append(function);
        GenerateExpression(cast.Operand, ref builder, context);
        builder.Append(')');
        return true;
    }

    /// <summary>Wunsch 4: SQLite stores dates as ISO text and has no typed literals; the ISO string compares correctly.</summary>
    protected override void FormatTypedLiteral(ref ValueStringBuilder builder, TypedLiteralExpression literal, SqlEmitterContext context)
    {
        if (context.IsBound)
        {
            // SEC-PG-04: the validated value is bound in its canonical ISO text form (the stored form of SQLite dates).
            var (value, _) = ParseTypedLiteralValue(literal);
            string text = value switch
            {
                DateTime moment when literal.Kind == TypedLiteralKind.Date => moment.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                DateTime moment => moment.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture),
                TimeSpan time => time.ToString(time.Ticks % TimeSpan.TicksPerSecond == 0 ? @"hh\:mm\:ss" : @"hh\:mm\:ss\.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"The {literal.Kind} literal value is not a valid {literal.Kind} value.")
            };
            builder.Append(context.BindValue(text, SqlParameterType.String, ParameterOrigin.QueryLiteral));
            return;
        }

        FormatStringLiteral(ref builder, literal.Value, context);
    }

    protected override void FormatIntervalLiteral(ref ValueStringBuilder builder, IntervalLiteralExpression interval, SqlEmitterContext context) =>
        throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"SQL construct INTERVAL literal is not supported for {TargetDialect} (no interval type).");


    /// <summary>Virtual filters (phase 7b): <c>datetime(x, '±n units')</c>; a week is seven days.</summary>
    protected override void FormatDateAdd(ref ValueStringBuilder builder, DateUnit unit, long amount, Expression source, SqlEmitterContext context)
    {
        (long value, string name) = unit == DateUnit.Week ? (checked(amount * 7), "day") : (amount, DateUnitName(unit));
        if (context.IsBound)
        {
            // SEC-PG-01: the amount is a bound number, the unit a bound generator token; the modifier is their concatenation ('5' || ' days').
            builder.Append("datetime(");
            GenerateExpression(source, ref builder, context);
            builder.Append(", ");
            builder.Append(BindInteger(value, context));
            builder.Append(" || ");
            builder.Append(context.BindValue(" " + name + "s", SqlParameterType.String, ParameterOrigin.QueryLiteral));
            builder.Append(')');
            return;
        }

        builder.Append("datetime(");
        GenerateExpression(source, ref builder, context);
        builder.Append(", '");
        builder.Append(value < 0 ? "-" : "+");
        builder.Append(Math.Abs(value).ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(name);
        builder.Append(Math.Abs(value) == 1 && unit != DateUnit.Week ? "'" : "s'");
        builder.Append(')');
    }

    /// <summary>
    /// Virtual filters (phase 7b): <c>datetime(x, 'start of day|month|year')</c>, <c>strftime</c> for hour, minute and
    /// second. SQLite has no week start modifier, so week is rejected.
    /// </summary>
    protected override void FormatDateTrunc(ref ValueStringBuilder builder, DateUnit unit, Expression source, SqlEmitterContext context)
    {
        string? start = unit switch
        {
            DateUnit.Day => "start of day",
            DateUnit.Month => "start of month",
            DateUnit.Year => "start of year",
            _ => null
        };
        if (start != null)
        {
            builder.Append("datetime(");
            GenerateExpression(source, ref builder, context);
            builder.Append(", ");
            AppendToken(ref builder, start, context);
            builder.Append(')');
            return;
        }

        string format = unit switch
        {
            DateUnit.Hour => "%Y-%m-%d %H:00:00",
            DateUnit.Minute => "%Y-%m-%d %H:%M:00",
            DateUnit.Second => "%Y-%m-%d %H:%M:%S",
            _ => throw UnsupportedDateFunction("date_trunc", unit, TargetDialect)
        };
        builder.Append("strftime(");
        AppendToken(ref builder, format, context);
        builder.Append(", ");
        GenerateExpression(source, ref builder, context);
        builder.Append(')');
    }

    // ---- typed column masks ----

    private static readonly System.Text.RegularExpressions.Regex DeclaredTypeRegex = new(
        @"\A[a-z][a-z0-9_]*(?: [a-z][a-z0-9_]*)*(?:\([0-9]{1,4}(?:, ?[0-9]{1,4})?\))?\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static void Put(ref ValueStringBuilder builder, params ReadOnlySpan<string> parts)
    {
        foreach (var part in parts) builder.Append(part);
    }

    private static System.Security.SecurityException MissingArgument(MaskExpression mask, string argument) =>
        new($"The {mask.Kind} mask requires the {argument} argument.");

    /// <summary>SQLite declared types are free text; only the affinity (a closed keyword set) is ever emitted.</summary>
    private static SqliteTypeAffinity.Kind MaskAffinity(string? declaredType)
    {
        if (string.IsNullOrWhiteSpace(declaredType) || !DeclaredTypeRegex.IsMatch(declaredType.Trim()))
        {
            throw new System.Security.SecurityException("The catalog data type of a masked column is not permitted.");
        }

        return SqliteTypeAffinity.Of(declaredType);
    }

    protected override void FormatMask(ref ValueStringBuilder builder, MaskExpression mask, SqlEmitterContext context)
    {
        var affinity = MaskAffinity(mask.DataType);
        string type = SqliteTypeAffinity.Keyword(affinity);
        bool text = affinity == SqliteTypeAffinity.Kind.Text;
        string column = "\"" + mask.Column.Name.SimpleName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        string asText = "CAST(" + column + " AS TEXT)";
        var args = mask.Arguments;

        switch (mask.Kind)
        {
            case Governance.MaskKind.Nullify:
                AppendStructural(ref builder, context, "CAST(NULL AS " + type + ")");
                break;

            case Governance.MaskKind.Redact:
                if (text)
                {
                    builder.Append("CAST(");
                    builder.Append(context.BindPolicy(args.Constant ?? throw MissingArgument(mask, "Constant")));
                    builder.Append(" AS TEXT)");
                }
                else
                {
                    AppendStructural(ref builder, context, "CAST(NULL AS " + type + ")");
                }

                break;

            case Governance.MaskKind.Constant:
                builder.Append("CAST(");
                builder.Append(context.BindPolicy(args.Constant ?? throw MissingArgument(mask, "Constant")));
                AppendStructural(ref builder, context, " AS " + type + ")");
                break;

            case Governance.MaskKind.PartialMask:
            {
                // MAX(n, 0): a negative count would make SUBSTR count from the other end and expose the value.
                string prefix = "MAX(CAST(" + context.BindPolicy(args.KeepPrefix ?? throw MissingArgument(mask, "KeepPrefix")) + " AS INTEGER), ";
                string suffix = "MAX(CAST(" + context.BindPolicy(args.KeepSuffix ?? throw MissingArgument(mask, "KeepSuffix")) + " AS INTEGER), ";
                string maskChar = "CAST(" + context.BindPolicy(args.MaskChar ?? throw MissingArgument(mask, "MaskChar")) + " AS TEXT)";

                void Clamped(ref ValueStringBuilder b, string head)
                {
                    b.Append(head);
                    int zeroStart = b.Length;
                    b.Append('0');
                    context.RegisterInlineNumericPosition(zeroStart, 1);
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

                // SQLite has no REPEAT: REPLACE(HEX(ZEROBLOB(n)), '00', c) yields n copies of c.
                Put(ref builder, "CASE WHEN ", column, " IS NULL THEN NULL WHEN LENGTH(", asText, ") <= ");
                Keep(ref builder);
                Put(ref builder, " THEN REPLACE(HEX(ZEROBLOB(");
                AppendInlineInteger(ref builder, 5, context);
                builder.Append(")), ");
                AppendConstantFragment(ref builder, context, "'00'");
                Put(ref builder, ", ", maskChar, ") ELSE SUBSTR(", asText, ", ");
                AppendInlineInteger(ref builder, 1, context);
                builder.Append(", ");
                Clamped(ref builder, prefix);
                Put(ref builder, ") || REPLACE(HEX(ZEROBLOB(LENGTH(", asText, ") - ");
                Keep(ref builder);
                builder.Append(")), ");
                AppendConstantFragment(ref builder, context, "'00'");
                Put(ref builder, ", ", maskChar, ") || SUBSTR(", asText, ", -");
                Clamped(ref builder, suffix);
                builder.Append(", ");
                Clamped(ref builder, suffix);
                builder.Append(") END");
                break;
            }

            case Governance.MaskKind.Hmac:
            {
                // The gateway UDF registered by SqlConnectionFactory; the name is a reviewed constant fragment (user SQL cannot call it).
                string key = context.BindPolicy(args.HmacKey ?? throw MissingArgument(mask, "HmacKey"));
                AppendConstantFragment(ref builder, context, "gateway_hmac_sha256(");
                Put(ref builder, asText, ", ", key, ")");
                break;
            }

            case Governance.MaskKind.GeoJitter:
            {
                int decimals = Math.Clamp(args.Decimals ?? 2, 0, 6);
                Put(ref builder, "CASE WHEN ", column, " IS NULL OR ", column, " = ");
                AppendStructural(ref builder, context, "0.0");
                Put(ref builder, " THEN ", column, " ELSE ROUND(", column, ", ");
                AppendInlineInteger(ref builder, decimals, context);
                builder.Append(") END");
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
        builder.Append(value ? '1' : '0');
    }

    protected override void GeneratePagination(PaginationClause pagination, OrderByClause? orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (pagination.WithTies)
        {
            throw UnsupportedConstruct("FETCH … WITH TIES", TargetDialect);
        }

        if (pagination.Limit != null)
        {
            builder.Append("LIMIT ");
            GenerateExpression(pagination.Limit, ref builder, context);
        }
        else if (pagination.Offset != null)
        {
            if (context.IsBound)
            {
                // SQLite needs LIMIT -1 for an OFFSET without LIMIT; the -1 is an inline numeric token.
                throw NoBoundForm("OFFSET without LIMIT", TargetDialect);
            }

            builder.Append("LIMIT -1");
        }

        if (pagination.Offset != null)
        {
            builder.Append(" OFFSET ");
            GenerateExpression(pagination.Offset, ref builder, context);
        }
    }
}

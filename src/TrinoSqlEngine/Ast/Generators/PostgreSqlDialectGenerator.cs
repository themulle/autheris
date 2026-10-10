namespace TrinoSqlEngine.Ast.Generators;

using System;
using TrinoSqlEngine;
using System.Collections.Frozen;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// PostgreSQL code generator.
/// Handles "identifier" double-quote quoting with "" escaping, $1 parameter placeholders (budget 65,535),
/// native TRUE/FALSE booleans, standard-conforming string literals, and LIMIT ... OFFSET ... pagination.
/// </summary>
public sealed class PostgreSqlDialectGenerator : SqlDialectGeneratorBase
{
    public override TargetSqlDialect TargetDialect => TargetSqlDialect.PostgreSql;
    protected override bool SupportsAggregateFilter => true;
    protected override bool SupportsOrderedAggregates => true;
    protected override bool SupportsGroupByDistinct => true;

    /// <summary>Wunsch 4: PostgreSQL has no double, tinyint or varbinary.</summary>
    protected override string FormatTypeName(TrinoType type) => type.Name switch
    {
        "double" => "double precision",
        "tinyint" => "smallint",
        "varbinary" => "bytea",
        _ => StandardTypeName(type)
    };
    public override int MaxParameterBudget => 65535;

    // The inline structural positions of this generator are registered with the emitter context.
    protected override bool BindLiterals => true;

    /// <summary>Virtual filters (phase 7b): <c>(x + (n) * INTERVAL '1 unit')</c>; on the governed path the amount is bound.</summary>
    protected override void FormatDateAdd(ref ValueStringBuilder builder, DateUnit unit, long amount, Expression source, SqlEmitterContext context)
    {
        if (context.IsBound)
        {
            builder.Append('(');
            GenerateExpression(source, ref builder, context);
            builder.Append(" + CAST(");
            builder.Append(BindInteger(amount, context));
            builder.Append(" AS bigint) * ");
            AppendConstantFragment(ref builder, context, "INTERVAL '1 " + DateUnitName(unit) + "'");
            builder.Append(')');
            return;
        }

        builder.Append('(');
        GenerateExpression(source, ref builder, context);
        builder.Append(" + (");
        builder.Append(amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(") * INTERVAL '1 ");
        builder.Append(DateUnitName(unit));
        builder.Append("')");
    }

    /// <summary><c>DATE_TRUNC('unit', x)</c>: the unit comes from a closed enum and is a reviewed constant fragment.</summary>
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
        builder.Append(context.BindValue(value, type, TrinoSqlEngine.Ast.Emit.ParameterOrigin.QueryLiteral));
        builder.Append(literal.Kind switch
        {
            TypedLiteralKind.Date => " AS date)",
            TypedLiteralKind.Time => " AS time)",
            _ => " AS timestamp)"
        });
    }

    protected override void FormatIntervalLiteral(ref ValueStringBuilder builder, IntervalLiteralExpression interval, SqlEmitterContext context)
    {
        if (!context.IsBound)
        {
            base.FormatIntervalLiteral(ref builder, interval, context);
            return;
        }

        builder.Append("(CAST(");
        builder.Append(BindInteger(long.Parse(interval.Value, System.Globalization.CultureInfo.InvariantCulture), context));
        builder.Append(" AS bigint) * ");
        AppendConstantFragment(ref builder, context, "INTERVAL '1 " + interval.Field.ToLowerInvariant() + "'");
        builder.Append(')');
    }

    // ---- typed column masks ----

    private static readonly System.Text.RegularExpressions.Regex NativeTypeRegex = new(
        @"\A(?<name>[a-z][a-z0-9_]*(?: [a-z][a-z0-9_]*)*?)(?:\((?<args>[0-9]{1,4}(?:, ?[0-9]{1,4})?)\))?\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly System.Collections.Frozen.FrozenSet<string> NumericOrTemporalTypes = new[]
    {
        "integer", "int", "int4", "bigint", "int8", "smallint", "int2", "numeric", "decimal", "real", "float4", "double precision",
        "float8", "boolean", "bool", "date", "time", "time without time zone", "time with time zone", "timestamp",
        "timestamp without time zone", "timestamp with time zone", "timestamptz", "uuid"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly System.Collections.Frozen.FrozenSet<string> TextTypes = new[]
    {
        "text", "varchar", "character varying", "char", "character", "bpchar", "name", "citext", "bytea"
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
        return (name + args, numeric);
    }

    private static void Put(ref ValueStringBuilder builder, params ReadOnlySpan<string> parts)
    {
        foreach (var part in parts) builder.Append(part);
    }

    protected override void FormatMask(ref ValueStringBuilder builder, MaskExpression mask, SqlEmitterContext context)
    {
        var (type, numeric) = NativeType(mask.DataType);
        string column = "\"" + mask.Column.Name.SimpleName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        string text = "CAST(" + column + " AS text)";
        var args = mask.Arguments;

        switch (mask.Kind)
        {
            case TrinoSqlEngine.Governance.MaskKind.Nullify:
                AppendStructural(ref builder, context, "CAST(NULL AS " + type + ")");
                break;

            case TrinoSqlEngine.Governance.MaskKind.Redact:
                if (numeric)
                {
                    AppendStructural(ref builder, context, "CAST(NULL AS " + type + ")");
                }
                else
                {
                    builder.Append("CAST(");
                    builder.Append(context.BindPolicy(args.Constant ?? throw MissingArgument(mask, "Constant")));
                    builder.Append(" AS text)");
                }

                break;

            case TrinoSqlEngine.Governance.MaskKind.Constant:
                builder.Append("CAST(");
                builder.Append(context.BindPolicy(args.Constant ?? throw MissingArgument(mask, "Constant")));
                AppendStructural(ref builder, context, " AS " + type + ")");
                break;

            case TrinoSqlEngine.Governance.MaskKind.PartialMask:
            {
                // GREATEST(n, 0): a negative count would make left()/right() count from the other end and expose the value.
                string prefix = "GREATEST(CAST(" + context.BindPolicy(args.KeepPrefix ?? throw MissingArgument(mask, "KeepPrefix")) + " AS integer), ";
                string suffix = "GREATEST(CAST(" + context.BindPolicy(args.KeepSuffix ?? throw MissingArgument(mask, "KeepSuffix")) + " AS integer), ";
                string maskChar = "CAST(" + context.BindPolicy(args.MaskChar ?? throw MissingArgument(mask, "MaskChar")) + " AS text)";
                // prefix/suffix expressions are emitted several times; build them once as text with the registered zero.
                int zeroStart;
                string P(ref ValueStringBuilder b)
                {
                    b.Append(prefix);
                    zeroStart = b.Length;
                    b.Append('0');
                    context.RegisterInlineNumericPosition(zeroStart, 1);
                    b.Append(')');
                    return string.Empty;
                }

                string S(ref ValueStringBuilder b)
                {
                    b.Append(suffix);
                    zeroStart = b.Length;
                    b.Append('0');
                    context.RegisterInlineNumericPosition(zeroStart, 1);
                    b.Append(')');
                    return string.Empty;
                }

                void Keep(ref ValueStringBuilder b)
                {
                    b.Append('(');
                    P(ref b);
                    b.Append(" + ");
                    S(ref b);
                    b.Append(')');
                }

                Put(ref builder, "CASE WHEN ", column, " IS NULL THEN NULL WHEN length(", text, ") <= ");
                Keep(ref builder);
                Put(ref builder, " THEN repeat(", maskChar, ", ");
                AppendInlineInteger(ref builder, 5, context);
                Put(ref builder, ") ELSE concat(left(", text, ", ");
                P(ref builder);
                Put(ref builder, "), repeat(", maskChar, ", CASE WHEN length(", text, ") > ");
                Keep(ref builder);
                Put(ref builder, " THEN length(", text, ") - ");
                Keep(ref builder);
                Put(ref builder, " ELSE ");
                AppendInlineInteger(ref builder, 5, context);
                Put(ref builder, " END), right(", text, ", ");
                S(ref builder);
                builder.Append(")) END");
                break;
            }

            case TrinoSqlEngine.Governance.MaskKind.Hmac:
            {
                // HMAC-SHA256 through pgcrypto; the algorithm and encoding names are reviewed constant fragments.
                string key = context.BindPolicy(args.HmacKey ?? throw MissingArgument(mask, "HmacKey"));
                Put(ref builder, "ENCODE(HMAC(", text, ", CAST(", key, " AS text), ");
                AppendConstantFragment(ref builder, context, "'sha256'");
                builder.Append("), ");
                AppendConstantFragment(ref builder, context, "'hex'");
                builder.Append(')');
                break;
            }

            case TrinoSqlEngine.Governance.MaskKind.GeoJitter:
            {
                int decimals = Math.Clamp(args.Decimals ?? 2, 0, 6);
                Put(ref builder, "CASE WHEN ", column, " IS NULL OR ", column, " = ");
                AppendStructural(ref builder, context, "0.0");
                Put(ref builder, " THEN ", column, " ELSE CAST(ROUND(CAST(", column, " AS numeric), ");
                AppendInlineInteger(ref builder, decimals, context);
                builder.Append(") AS double precision) END");
                break;
            }

            default:
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
            if (orderBy == null)
            {
                throw new TrinoSqlEngine.Ast.Builder.AstBuildException("FETCH … WITH TIES requires an ORDER BY clause.");
            }

            if (pagination.Offset != null)
            {
                builder.Append("OFFSET ");
                GenerateStructuralInteger(pagination.Offset, ref builder, context);
                builder.Append(" ROWS ");
            }

            if (pagination.Limit != null)
            {
                builder.Append("FETCH FIRST ");
                GenerateStructuralInteger(pagination.Limit, ref builder, context);
                builder.Append(" ROWS WITH TIES");
            }
            return;
        }

        bool hasLimit = pagination.Limit != null;
        if (hasLimit)
        {
            builder.Append("LIMIT ");
            GenerateStructuralInteger(pagination.Limit!, ref builder, context);
        }

        if (pagination.Offset != null)
        {
            if (hasLimit) builder.Append(' ');
            builder.Append("OFFSET ");
            GenerateStructuralInteger(pagination.Offset, ref builder, context);
        }
    }
}

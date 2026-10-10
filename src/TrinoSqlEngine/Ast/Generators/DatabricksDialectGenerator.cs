namespace TrinoSqlEngine.Ast.Generators;

using System;
using System.Collections.Frozen;
using System.Globalization;
using System.Security;
using System.Text.RegularExpressions;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Databricks SQL (Spark SQL) generator (plan section 6.1): backtick identifiers, named <c>:pN</c> markers, Unity Catalog
/// three-part names and no string literals at all. Spark interprets backslash escapes in <c>'...'</c> literals and may
/// substitute <c>${...}</c> before parsing, so every value is bound and identifiers containing <c>$</c>, <c>{</c> or <c>}</c>
/// are rejected (SEC-ADG-10).
/// </summary>
public sealed class DatabricksDialectGenerator : SqlDialectGeneratorBase
{
    public override TargetSqlDialect TargetDialect => TargetSqlDialect.Databricks;
    public override int MaxParameterBudget => 1000;

    protected override bool BindLiterals => true;
    protected override bool SupportsTryCast => true;
    protected override bool SupportsAggregateFilter => true;

    // ---- identifiers and literals ----

    public override void FormatIdentifier(ref ValueStringBuilder builder, SqlIdentifier identifier, SqlEmitterContext context)
    {
        string value = identifier.Value;
        if (value.AsSpan().IndexOfAny('$', '{', '}') >= 0)
        {
            // SEC-ADG-10: variable substitution (${...}) could change the statement after the gateway checked it.
            throw new SecurityException("A Databricks identifier must not contain '$', '{' or '}'.");
        }

        builder.Append('`');
        builder.Append(value.Replace("`", "``", StringComparison.Ordinal));
        builder.Append('`');
    }

    /// <summary>Unity Catalog names are catalog.schema.table; every part is delimited separately.</summary>
    public override void FormatTableName(ref ValueStringBuilder builder, SqlQualifiedName name, SqlEmitterContext context)
    {
        if (name.Parts.Count > 3)
        {
            throw UnsupportedConstruct("a table name with more than three parts", TargetDialect);
        }

        for (int i = 0; i < name.Parts.Count; i++)
        {
            if (i > 0) builder.Append('.');
            FormatIdentifier(ref builder, name.Parts[i], context);
        }
    }

    public override void FormatStringLiteral(ref ValueStringBuilder builder, string value, SqlEmitterContext context) =>
        throw new InvalidOperationException("Databricks string literals are never emitted: every value is a bound parameter.");

    public override void FormatBoolean(ref ValueStringBuilder builder, bool value, SqlEmitterContext context) =>
        builder.Append(value ? "TRUE" : "FALSE");

    // ---- pagination ----

    protected override void GeneratePagination(PaginationClause pagination, OrderByClause? orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (pagination.WithTies)
        {
            throw UnsupportedConstruct("FETCH ... WITH TIES", TargetDialect);
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

    // ---- types ----

    protected override string FormatTypeName(TrinoType type) => type.Name switch
    {
        "varchar" or "char" or "string" => "STRING",
        "double" => "DOUBLE",
        "real" => "FLOAT",
        "decimal" or "numeric" => "DECIMAL" + type.Arguments,
        "timestamp" when type.WithTimeZone => "TIMESTAMP",
        "timestamp" => "TIMESTAMP_NTZ",
        "varbinary" => "BINARY",
        "integer" or "int" => "INT",
        "bigint" => "BIGINT",
        "smallint" => "SMALLINT",
        "tinyint" => "TINYINT",
        "boolean" => "BOOLEAN",
        "date" when !type.WithTimeZone => "DATE",
        _ => throw UnsupportedConstruct($"CAST(... AS {type.Normalized})", TargetDialect)
    };

    // ---- date and time ----

    protected override void FormatCurrentDateTime(ref ValueStringBuilder builder, CurrentDateTimeKind kind, SqlEmitterContext context)
    {
        builder.Append(kind switch
        {
            CurrentDateTimeKind.CurrentDate => "CURRENT_DATE()",
            CurrentDateTimeKind.CurrentTimestamp => "CURRENT_TIMESTAMP()",
            CurrentDateTimeKind.LocalTimestamp => "LOCALTIMESTAMP()",
            _ => throw UnsupportedConstruct("CURRENT_TIME / LOCALTIME", TargetDialect)
        });
    }

    /// <summary><c>timestampadd(UNIT, n, x)</c>; the amount is bound.</summary>
    protected override void FormatDateAdd(ref ValueStringBuilder builder, DateUnit unit, long amount, Expression source, SqlEmitterContext context)
    {
        builder.Append("TIMESTAMPADD(");
        builder.Append(DateUnitName(unit).ToUpperInvariant());
        builder.Append(", ");
        builder.Append(context.IsBound ? BindInteger(amount, context) : amount.ToString(CultureInfo.InvariantCulture));
        builder.Append(", ");
        GenerateExpression(source, ref builder, context);
        builder.Append(')');
    }

    /// <summary><c>DATE_TRUNC('UNIT', x)</c>: the unit comes from a closed enum and is a reviewed constant fragment.</summary>
    protected override void FormatDateTrunc(ref ValueStringBuilder builder, DateUnit unit, Expression source, SqlEmitterContext context)
    {
        builder.Append("DATE_TRUNC(");
        AppendConstantFragment(ref builder, context, "'" + DateUnitName(unit).ToUpperInvariant() + "'");
        builder.Append(", ");
        GenerateExpression(source, ref builder, context);
        builder.Append(')');
    }

    protected override void FormatTypedLiteral(ref ValueStringBuilder builder, TypedLiteralExpression literal, SqlEmitterContext context)
    {
        var (value, type) = ParseTypedLiteralValue(literal);
        builder.Append("CAST(");
        builder.Append(context.BindValue(value, type, ParameterOrigin.QueryLiteral));
        builder.Append(literal.Kind switch
        {
            TypedLiteralKind.Date => " AS DATE)",
            TypedLiteralKind.Time => throw UnsupportedConstruct("TIME literal", TargetDialect),
            _ => " AS TIMESTAMP_NTZ)"
        });
    }

    protected override void FormatIntervalLiteral(ref ValueStringBuilder builder, IntervalLiteralExpression interval, SqlEmitterContext context)
    {
        builder.Append("(CAST(");
        builder.Append(BindInteger(long.Parse(interval.Value, CultureInfo.InvariantCulture), context));
        builder.Append(" AS INT) * ");
        AppendConstantFragment(ref builder, context, "INTERVAL '1' " + interval.Field.ToUpperInvariant());
        builder.Append(')');
    }

    protected override void FormatExtract(ref ValueStringBuilder builder, string field, Expression source, SqlEmitterContext context)
    {
        string spark = field switch
        {
            "YEAR" or "QUARTER" or "MONTH" or "WEEK" or "DAY" or "HOUR" or "MINUTE" or "SECOND" => field,
            "DAY_OF_WEEK" => "DAYOFWEEK_ISO",
            "DAY_OF_YEAR" => "DOY",
            "YEAR_OF_WEEK" => "YEAROFWEEK",
            _ => throw UnsupportedExtract(field, TargetDialect)
        };
        builder.Append("EXTRACT(");
        builder.Append(spark);
        builder.Append(" FROM ");
        GenerateExpression(source, ref builder, context);
        builder.Append(')');
    }

    // ---- functions and unsupported constructs ----

    protected override void GenerateFunctionCall(FunctionCallExpression fn, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (fn.Name.Parts.Count == 1 && !fn.Name.Parts[0].IsQuoted && fn.Window == null)
        {
            string? mapped = fn.Name.Parts[0].Value.ToLowerInvariant() switch
            {
                "strpos" when fn.Arguments.Count == 2 => "INSTR",
                "approx_distinct" => "APPROX_COUNT_DISTINCT",
                "arbitrary" => "ANY_VALUE",
                _ => null
            };
            if (mapped != null)
            {
                base.GenerateFunctionCall(fn with { Name = new SqlQualifiedName(mapped) }, ref builder, context);
                return;
            }
        }

        base.GenerateFunctionCall(fn, ref builder, context);
    }

    public override void GenerateExpression(Expression expression, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        // Spark arrays are zero-based and constructed with array(...); Trino arrays are one-based. Rejected rather than guessed.
        if (expression is SubscriptExpression or ArrayConstructorExpression)
        {
            throw UnsupportedConstruct("array constructors and subscripts", TargetDialect);
        }

        base.GenerateExpression(expression, ref builder, context);
    }

    // ---- typed column masks ----

    private static readonly Regex NativeTypeRegex = new(
        @"\A(?<name>[a-z][a-z0-9_]*)(?:\((?<args>[0-9]{1,4}(?:, ?[0-9]{1,4})?)\))?\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    private static readonly FrozenSet<string> NumericOrTemporalTypes = new[]
    {
        "tinyint", "smallint", "int", "integer", "bigint", "decimal", "numeric", "double", "float", "boolean", "date", "timestamp", "timestamp_ntz"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> TextTypes = new[] { "string", "varchar", "char", "binary" }.ToFrozenSet(StringComparer.Ordinal);

    private static (string Text, bool NumericOrTemporal) NativeType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            throw new SecurityException("A typed column mask requires the catalog data type of the column.");
        }

        var match = NativeTypeRegex.Match(dataType.Trim());
        if (!match.Success)
        {
            throw new SecurityException("The catalog data type of a masked column is not permitted.");
        }

        string name = match.Groups["name"].Value.ToLowerInvariant();
        bool numeric = NumericOrTemporalTypes.Contains(name);
        if (!numeric && !TextTypes.Contains(name))
        {
            throw new SecurityException("The catalog data type of a masked column is not permitted.");
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
        string column = "`" + mask.Column.Name.SimpleName.Replace("`", "``", StringComparison.Ordinal) + "`";
        if (mask.Column.Name.SimpleName.AsSpan().IndexOfAny('$', '{', '}') >= 0)
        {
            throw new SecurityException("A Databricks identifier must not contain '$', '{' or '}'.");
        }

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
                    builder.Append(" AS STRING)");
                }

                break;

            case Governance.MaskKind.Constant:
                builder.Append("CAST(");
                builder.Append(context.BindPolicy(args.Constant ?? throw MissingArgument(mask, "Constant")));
                AppendStructural(ref builder, context, " AS " + type + ")");
                break;

            case Governance.MaskKind.PartialMask:
            {
                // GREATEST(n, 0): a negative count must not make left()/right() count from the other end.
                string prefix = "GREATEST(CAST(" + context.BindPolicy(args.KeepPrefix ?? throw MissingArgument(mask, "KeepPrefix")) + " AS INT), ";
                string suffix = "GREATEST(CAST(" + context.BindPolicy(args.KeepSuffix ?? throw MissingArgument(mask, "KeepSuffix")) + " AS INT), ";
                string maskChar = "CAST(" + context.BindPolicy(args.MaskChar ?? throw MissingArgument(mask, "MaskChar")) + " AS STRING)";
                string text = "CAST(" + column + " AS STRING)";

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

                Put(ref builder, "CASE WHEN ", column, " IS NULL THEN NULL WHEN LENGTH(", text, ") <= ");
                Keep(ref builder);
                Put(ref builder, " THEN REPEAT(", maskChar, ", ");
                AppendInlineInteger(ref builder, 5, context);
                Put(ref builder, ") ELSE CONCAT(LEFT(", text, ", ");
                Clamped(ref builder, prefix);
                Put(ref builder, "), REPEAT(", maskChar, ", CASE WHEN LENGTH(", text, ") > ");
                Keep(ref builder);
                Put(ref builder, " THEN LENGTH(", text, ") - ");
                Keep(ref builder);
                Put(ref builder, " ELSE ");
                AppendInlineInteger(ref builder, 5, context);
                Put(ref builder, " END), RIGHT(", text, ", ");
                Clamped(ref builder, suffix);
                builder.Append(")) END");
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
                // Databricks has no keyed HMAC (InDbHmac = false): the compiler degrades HMAC to Redact before emission.
                throw UnsupportedConstruct($"column mask {mask.Kind}", TargetDialect);
        }
    }

    private static SecurityException MissingArgument(MaskExpression mask, string argument) =>
        new($"The {mask.Kind} mask requires the {argument} argument.");
}

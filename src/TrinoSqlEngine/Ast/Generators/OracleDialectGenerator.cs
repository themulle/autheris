namespace TrinoSqlEngine.Ast.Generators;

using System;
using TrinoSqlEngine;
using System.Collections.Frozen;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Oracle Database code generator (Oracle 12c+ / 23c).
/// Handles double-quoted identifiers with uppercase folding for unquoted identifiers,
/// parameter placeholders (:p1, budget 1000), omission of AS keyword in FROM clause table aliases,
/// 1/0 and (1=1)/(1=0) booleans, and OFFSET ... ROWS FETCH NEXT ... ROWS ONLY pagination.
/// </summary>
public sealed class OracleDialectGenerator : SqlDialectGeneratorBase
{
    public override TargetSqlDialect TargetDialect => TargetSqlDialect.Oracle;

    protected override string SubstringFunctionName => "SUBSTR";

    // The inline structural positions of this generator are registered with the emitter context.
    protected override bool BindLiterals => true;

    protected override string? FromlessSource => "DUAL";

    /// <summary>Trino <c>strpos(s, t)</c> is Oracle <c>INSTR(s, t)</c> (same argument order); other functions pass through.</summary>
    protected override void GenerateFunctionCall(FunctionCallExpression fn, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (fn.Name.Parts.Count == 1 && !fn.Name.Parts[0].IsQuoted && fn.Window == null && fn.Arguments.Count == 2 &&
            fn.Name.Parts[0].Value.Equals("strpos", StringComparison.OrdinalIgnoreCase))
        {
            base.GenerateFunctionCall(fn with { Name = new SqlQualifiedName("INSTR") }, ref builder, context);
            return;
        }

        base.GenerateFunctionCall(fn, ref builder, context);
    }

    /// <summary>Wunsch 4: Oracle has no IS DISTINCT FROM; DECODE treats two NULLs as equal.</summary>
    protected override void FormatIsDistinctFrom(ref ValueStringBuilder builder, IsDistinctFromExpression dist, SqlEmitterContext context)
    {
        builder.Append("DECODE(");
        GenerateExpression(dist.Left, ref builder, context);
        builder.Append(", ");
        GenerateExpression(dist.Right, ref builder, context);
        AppendStructural(ref builder, context, dist.IsNotDistinctFrom ? ", 0, 1) = 0" : ", 0, 1) = 1");
    }

    /// <summary>Wunsch 4: Oracle spellings (VARCHAR2, NUMBER, BINARY_DOUBLE); no BOOLEAN or TIME before 23ai.</summary>
    protected override string FormatTypeName(TrinoType type) => type.Name switch
    {
        "double" or "double precision" => "BINARY_DOUBLE",
        "real" => "BINARY_FLOAT",
        "varchar" => "VARCHAR2" + (type.Arguments ?? "(4000)"),
        "char" => "CHAR" + type.Arguments,
        "tinyint" => "NUMBER(3)",
        "smallint" => "NUMBER(5)",
        "integer" or "int" => "NUMBER(10)",
        "bigint" => "NUMBER(19)",
        "decimal" or "numeric" => "NUMBER" + type.Arguments,
        "date" => "DATE",
        "timestamp" => "TIMESTAMP" + type.Arguments + (type.WithTimeZone ? " WITH TIME ZONE" : string.Empty),
        _ => throw UnsupportedConstruct($"CAST(… AS {type.Normalized})", TargetDialect)
    };

    /// <summary>Oracle's CURRENT_DATE carries a time of day; Oracle has no TIME type.</summary>
    protected override void FormatCurrentDateTime(ref ValueStringBuilder builder, CurrentDateTimeKind kind, SqlEmitterContext context)
    {
        builder.Append(kind switch
        {
            CurrentDateTimeKind.CurrentDate => "TRUNC(CURRENT_DATE)",
            CurrentDateTimeKind.CurrentTimestamp => "CURRENT_TIMESTAMP",
            CurrentDateTimeKind.LocalTimestamp => "LOCALTIMESTAMP",
            _ => throw UnsupportedConstruct("current_time/localtime (no TIME type)", TargetDialect)
        });
    }

    protected override void FormatPosition(ref ValueStringBuilder builder, PositionExpression position, SqlEmitterContext context) =>
        FormatInstr(ref builder, position, context);

    /// <summary>
    /// Wunsch 4: Oracle EXTRACT knows YEAR…SECOND (time fields only from TIMESTAMP); quarter, ISO week and day of year via
    /// TO_CHAR. The day of week depends on NLS settings and is rejected.
    /// </summary>
    protected override void FormatExtract(ref ValueStringBuilder builder, string field, Expression source, SqlEmitterContext context)
    {
        switch (field)
        {
            case "YEAR" or "MONTH" or "DAY":
                builder.Append("EXTRACT(");
                builder.Append(field);
                builder.Append(" FROM ");
                GenerateExpression(source, ref builder, context);
                builder.Append(')');
                return;
            case "HOUR" or "MINUTE" or "SECOND":
                builder.Append("EXTRACT(");
                builder.Append(field);
                builder.Append(" FROM CAST(");
                GenerateExpression(source, ref builder, context);
                builder.Append(" AS TIMESTAMP))");
                return;
            case "QUARTER" or "WEEK" or "DAY_OF_YEAR":
                builder.Append("TO_NUMBER(TO_CHAR(");
                GenerateExpression(source, ref builder, context);
                builder.Append(", ");
                AppendConstantFragment(ref builder, context, field switch { "QUARTER" => "'Q'", "WEEK" => "'IW'", _ => "'DDD'" });
                builder.Append("))");
                return;
            default:
                throw UnsupportedExtract(field, TargetDialect);
        }
    }

    /// <summary>Wunsch 4: Oracle has DATE and TIMESTAMP literals but no TIME type.</summary>
    protected override void FormatTypedLiteral(ref ValueStringBuilder builder, TypedLiteralExpression literal, SqlEmitterContext context)
    {
        if (literal.Kind == TypedLiteralKind.Time)
        {
            throw new TrinoSqlEngine.Ast.Builder.AstBuildException("SQL construct TIME literal is not supported for Oracle (no TIME type).");
        }

        if (!context.IsBound)
        {
            base.FormatTypedLiteral(ref builder, literal, context);
            return;
        }

        var (value, type) = ParseTypedLiteralValue(literal);
        builder.Append("CAST(");
        builder.Append(context.BindValue(value, type, ParameterOrigin.QueryLiteral));
        builder.Append(literal.Kind == TypedLiteralKind.Date ? " AS DATE)" : " AS TIMESTAMP)");
    }

    protected override void FormatIntervalLiteral(ref ValueStringBuilder builder, IntervalLiteralExpression interval, SqlEmitterContext context)
    {
        if (!context.IsBound)
        {
            base.FormatIntervalLiteral(ref builder, interval, context);
            return;
        }

        builder.Append("NUMTODSINTERVAL(CAST(");
        builder.Append(BindInteger(long.Parse(interval.Value, System.Globalization.CultureInfo.InvariantCulture), context));
        builder.Append(" AS NUMBER(19)), ");
        AppendConstantFragment(ref builder, context, "'" + interval.Field.ToUpperInvariant() + "'");
        builder.Append(')');
    }

    public override int MaxParameterBudget => 1000;

    /// <summary>
    /// Virtual filters (phase 7b): <c>ADD_MONTHS</c> for month and year (calendar months), otherwise
    /// <c>(x + NUMTODSINTERVAL(n, 'UNIT'))</c>; a week is seven days.
    /// </summary>
    protected override void FormatDateAdd(ref ValueStringBuilder builder, DateUnit unit, long amount, Expression source, SqlEmitterContext context)
    {
        string Amount(long n) => context.IsBound ? BindInteger(n, context) : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (unit is DateUnit.Month or DateUnit.Year)
        {
            builder.Append("ADD_MONTHS(");
            GenerateExpression(source, ref builder, context);
            builder.Append(", ");
            builder.Append(Amount(unit == DateUnit.Year ? amount * 12 : amount));
            builder.Append(')');
            return;
        }

        (long value, string field) = unit == DateUnit.Week ? (amount * 7, "DAY") : (amount, DateUnitName(unit).ToUpperInvariant());
        builder.Append('(');
        GenerateExpression(source, ref builder, context);
        builder.Append(" + NUMTODSINTERVAL(");
        builder.Append(Amount(value));
        builder.Append(", ");
        AppendConstantFragment(ref builder, context, "'" + field + "'");
        builder.Append("))");
    }

    /// <summary>Virtual filters (phase 7b): <c>TRUNC(x, 'format')</c>; Oracle cannot truncate to the second.</summary>
    protected override void FormatDateTrunc(ref ValueStringBuilder builder, DateUnit unit, Expression source, SqlEmitterContext context)
    {
        string format = unit switch
        {
            DateUnit.Minute => "MI",
            DateUnit.Hour => "HH24",
            DateUnit.Day => "DD",
            DateUnit.Week => "IW",
            DateUnit.Month => "MM",
            DateUnit.Year => "YYYY",
            _ => throw UnsupportedDateFunction("date_trunc", unit, TargetDialect)
        };
        builder.Append("TRUNC(");
        GenerateExpression(source, ref builder, context);
        builder.Append(", ");
        AppendConstantFragment(ref builder, context, "'" + format + "'");
        builder.Append(')');
    }

    protected override string TableAliasKeyword => " ";

    // ---- typed column masks ----

    private static readonly System.Text.RegularExpressions.Regex NativeTypeRegex = new(
        @"\A(?<name>[a-z][a-z0-9_]*(?: [a-z][a-z0-9_]*)*?)(?:\((?<args>[0-9]{1,4}(?:, ?[0-9]{1,4})?)\))?\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static readonly System.Collections.Frozen.FrozenSet<string> NumericOrTemporalTypes = new[]
    {
        "number", "integer", "int", "smallint", "float", "binary_double", "binary_float", "date", "timestamp",
        "timestamp with time zone", "timestamp with local time zone", "raw"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly System.Collections.Frozen.FrozenSet<string> TextTypes = new[]
    {
        "varchar2", "nvarchar2", "char", "nchar", "varchar", "clob", "nclob"
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

    // Mask templates contain reviewed constants (types with digits) and markers; they are registered as structural text.
    private static void Put(ref ValueStringBuilder builder, SqlEmitterContext context, params ReadOnlySpan<string> parts)
    {
        int start = builder.Length;
        foreach (var part in parts) builder.Append(part);
        context.RegisterInlineNumericPosition(start, builder.Length - start);
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
                    AppendStructural(ref builder, context, " AS VARCHAR2(4000))");
                }

                break;

            case Governance.MaskKind.Constant:
                builder.Append("CAST(");
                builder.Append(context.BindPolicy(args.Constant ?? throw MissingArgument(mask, "Constant")));
                AppendStructural(ref builder, context, " AS " + type + ")");
                break;

            case Governance.MaskKind.PartialMask:
            {
                // GREATEST(n, 0): a negative count must not make SUBSTR count from the other end.
                string prefix = "GREATEST(CAST(" + context.BindPolicy(args.KeepPrefix ?? throw MissingArgument(mask, "KeepPrefix")) + " AS NUMBER(10)), ";
                string suffix = "GREATEST(CAST(" + context.BindPolicy(args.KeepSuffix ?? throw MissingArgument(mask, "KeepSuffix")) + " AS NUMBER(10)), ";
                string maskChar = "CAST(" + context.BindPolicy(args.MaskChar ?? throw MissingArgument(mask, "MaskChar")) + " AS VARCHAR2(4000))";

                void Clamped(ref ValueStringBuilder b, string head)
                {
                    AppendStructural(ref b, context, head);
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

                // LENGTH/SUBSTR/RPAD: RPAD(c, n, c) repeats the mask character; SUBSTR(x, len - s + 1, s) is the last s characters
                // (and NULL for s = 0, which || treats as empty).
                Put(ref builder, context, "CASE WHEN ", column, " IS NULL THEN NULL WHEN LENGTH(", column, ") <= ");
                Keep(ref builder);
                Put(ref builder, context, " THEN RPAD(", maskChar, ", ");
                AppendInlineInteger(ref builder, 5, context);
                Put(ref builder, context, ", ", maskChar, ") ELSE SUBSTR(", column, ", ");
                AppendInlineInteger(ref builder, 1, context);
                Put(ref builder, context, ", ");
                Clamped(ref builder, prefix);
                Put(ref builder, context, ") || RPAD(", maskChar, ", LENGTH(", column, ") - ");
                Keep(ref builder);
                Put(ref builder, context, ", ", maskChar, ") || SUBSTR(", column, ", LENGTH(", column, ") - ");
                Clamped(ref builder, suffix);
                Put(ref builder, context, " + ");
                AppendInlineInteger(ref builder, 1, context);
                Put(ref builder, context, ", ");
                Clamped(ref builder, suffix);
                builder.Append(") END");
                break;
            }

            case Governance.MaskKind.GeoJitter:
            {
                int decimals = Math.Clamp(args.Decimals ?? 2, 0, 6);
                Put(ref builder, context, "CASE WHEN ", column, " IS NULL OR ", column, " = ");
                AppendStructural(ref builder, context, "0.0");
                Put(ref builder, context, " THEN ", column, " ELSE ROUND(", column, ", ");
                AppendInlineInteger(ref builder, decimals, context);
                builder.Append(") END");
                break;
            }

            default:
                // HMAC needs DBMS_CRYPTO (InDbHmac = false by default): the compiler degrades HMAC to Redact before emission.
                throw UnsupportedConstruct($"column mask {mask.Kind}", TargetDialect);
        }
    }

    private static System.Security.SecurityException MissingArgument(MaskExpression mask, string argument) =>
        new($"The {mask.Kind} mask requires the {argument} argument.");

    public override void FormatIdentifier(ref ValueStringBuilder builder, SqlIdentifier identifier, SqlEmitterContext context)
    {
        builder.Append('"');
        string val = identifier.IsQuoted ? identifier.Value : identifier.Value.ToUpperInvariant();
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
        if (context.InPredicateContext)
        {
            builder.Append(value ? "(1 = 1)" : "(1 = 0)");
        }
        else
        {
            builder.Append(value ? '1' : '0');
        }
    }

    public override void GenerateExpression(Expression expression, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (context.InProjectionContext && IsPredicateExpression(expression))
        {
            builder.Append("CASE WHEN ");
            bool prevProj = context.InProjectionContext;
            bool prevPred = context.InPredicateContext;
            context.InProjectionContext = false;
            context.InPredicateContext = true;
            base.GenerateExpression(expression, ref builder, context);
            context.InProjectionContext = prevProj;
            context.InPredicateContext = prevPred;
            AppendStructural(ref builder, context, " THEN 1 ELSE 0 END");
            return;
        }

        base.GenerateExpression(expression, ref builder, context);
    }

    private static bool IsPredicateExpression(Expression expr) => expr switch
    {
        BinaryExpression b => b.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual or
                                           BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or
                                           BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual or
                                           BinaryOperator.And or BinaryOperator.Or,
        UnaryExpression u => u.Operator is UnaryOperator.Not or UnaryOperator.IsNull or UnaryOperator.IsNotNull,
        LikeExpression => true,
        InListExpression => true,
        InSubqueryExpression => true,
        BetweenExpression => true,
        ExistsExpression => true,
        IsDistinctFromExpression => true,
        _ => false
    };

    protected override void GenerateWithClause(WithClause with, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append("WITH ");
        // Oracle does not support the RECURSIVE keyword on CTE definitions
        for (int i = 0; i < with.Ctes.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            var cte = with.Ctes[i];
            FormatIdentifier(ref builder, cte.Name, context);
            if (cte.ColumnAliases != null && cte.ColumnAliases.Count > 0)
            {
                builder.Append(" (");
                for (int j = 0; j < cte.ColumnAliases.Count; j++)
                {
                    if (j > 0) builder.Append(", ");
                    FormatIdentifier(ref builder, cte.ColumnAliases[j], context);
                }
                builder.Append(')');
            }
            builder.Append(" AS (");
            GenerateSelect(cte.Query, ref builder, context);
            builder.Append(')');
        }
    }

    protected override void GeneratePagination(PaginationClause pagination, OrderByClause? orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (pagination.WithTies && orderBy == null)
        {
            throw new TrinoSqlEngine.Ast.Builder.AstBuildException("FETCH … WITH TIES requires an ORDER BY clause.");
        }

        string tiesOrOnly = pagination.WithTies ? " ROWS WITH TIES" : " ROWS ONLY";

        if (pagination.Offset != null)
        {
            builder.Append("OFFSET ");
            GenerateStructuralInteger(pagination.Offset, ref builder, context);
            builder.Append(" ROWS");
            if (pagination.Limit != null)
            {
                builder.Append(" FETCH NEXT ");
                GenerateStructuralInteger(pagination.Limit, ref builder, context);
                builder.Append(tiesOrOnly);
            }
        }
        else if (pagination.Limit != null)
        {
            builder.Append("FETCH FIRST ");
            GenerateStructuralInteger(pagination.Limit, ref builder, context);
            builder.Append(tiesOrOnly);
        }
    }
}

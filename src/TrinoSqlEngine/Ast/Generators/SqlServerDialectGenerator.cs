namespace TrinoSqlEngine.Ast.Generators;

using System;
using System.Globalization;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Microsoft SQL Server (T-SQL) code generator.
/// Handles [identifier] bracket quoting with ]] escaping, @p0 parameters (budget 2,100),
/// N'...' Unicode strings, synthetic ORDER BY (SELECT NULL) for OFFSET/FETCH, and boolean representations.
/// </summary>
public sealed class SqlServerDialectGenerator : SqlDialectGeneratorBase
{
    public override TargetSqlDialect TargetDialect => TargetSqlDialect.SqlServer;

    protected override bool SupportsTryCast => true;
    protected override bool SupportsJoinUsing => false;

    /// <summary>Wunsch 4: IS [NOT] DISTINCT FROM needs SQL Server 2022; INTERSECT compares NULLs as equal on every version.</summary>
    protected override void FormatIsDistinctFrom(ref ValueStringBuilder builder, IsDistinctFromExpression dist, SqlEmitterContext context)
    {
        builder.Append(dist.IsNotDistinctFrom ? "EXISTS (SELECT " : "NOT EXISTS (SELECT ");
        GenerateExpression(dist.Left, ref builder, context);
        builder.Append(" INTERSECT SELECT ");
        GenerateExpression(dist.Right, ref builder, context);
        builder.Append(')');
    }

    /// <summary>Wunsch 4: T-SQL names; <c>timestamp</c> would be rowversion, so it maps to datetime2.</summary>
    protected override string FormatTypeName(TrinoType type) => type.Name switch
    {
        "double" or "double precision" => "float",
        "boolean" => "bit",
        "timestamp" when type.WithTimeZone => "datetimeoffset" + type.Arguments,
        "timestamp" => "datetime2" + type.Arguments,
        "varchar" => "nvarchar" + (type.Arguments ?? "(max)"),
        "char" => "nchar" + (type.Arguments ?? "(1)"),
        "varbinary" => "varbinary" + (type.Arguments ?? "(max)"),
        "integer" => "int",
        "uuid" => "uniqueidentifier",
        "json" => "nvarchar(max)",
        "tinyint" or "smallint" or "int" or "bigint" or "real" or "decimal" or "numeric" or "date" or "time" when !type.WithTimeZone
            => type.Name + type.Arguments,
        _ => throw UnsupportedConstruct($"CAST(… AS {type.Normalized})", TargetDialect)
    };

    protected override void FormatCurrentDateTime(ref ValueStringBuilder builder, CurrentDateTimeKind kind, SqlEmitterContext context)
    {
        builder.Append(kind switch
        {
            CurrentDateTimeKind.CurrentDate => "CAST(SYSDATETIME() AS date)",
            CurrentDateTimeKind.CurrentTimestamp => "SYSDATETIMEOFFSET()",
            CurrentDateTimeKind.LocalTimestamp => "SYSDATETIME()",
            _ => "CAST(SYSDATETIME() AS time)"
        });
    }

    /// <summary>T-SQL SUBSTRING requires a length.</summary>
    protected override void FormatSubstring(ref ValueStringBuilder builder, SubstringExpression substring, SqlEmitterContext context) =>
        base.FormatSubstring(ref builder, substring.Length != null ? substring : substring with { Length = new LiteralExpression(2147483647L, LiteralType.Integer) }, context);

    /// <summary>
    /// TRIM(x) and TRIM(chars FROM x) exist from SQL Server 2017; LTRIM/RTRIM with characters only from 2022, so leading or
    /// trailing trims of characters other than spaces are rejected.
    /// </summary>
    protected override void FormatTrim(ref ValueStringBuilder builder, TrimExpression trim, SqlEmitterContext context)
    {
        if (trim.Specification != TrimSpecification.Both && trim.Characters != null)
        {
            throw UnsupportedConstruct("TRIM(LEADING|TRAILING chars FROM …)", TargetDialect);
        }

        builder.Append(trim.Specification switch
        {
            TrimSpecification.Leading => "LTRIM(",
            TrimSpecification.Trailing => "RTRIM(",
            _ => "TRIM("
        });
        if (trim.Characters != null)
        {
            GenerateExpression(trim.Characters, ref builder, context);
            builder.Append(" FROM ");
        }
        GenerateExpression(trim.Source, ref builder, context);
        builder.Append(')');
    }

    protected override void FormatPosition(ref ValueStringBuilder builder, PositionExpression position, SqlEmitterContext context)
    {
        builder.Append("CHARINDEX(");
        GenerateExpression(position.Needle, ref builder, context);
        builder.Append(", ");
        GenerateExpression(position.Haystack, ref builder, context);
        builder.Append(')');
    }

    /// <summary>Wunsch 4: T-SQL has no EXTRACT; DATEPART, with the ISO day of week independent of @@DATEFIRST.</summary>
    protected override void FormatExtract(ref ValueStringBuilder builder, string field, Expression source, SqlEmitterContext context)
    {
        if (field == "DAY_OF_WEEK")
        {
            builder.Append("((DATEPART(weekday, ");
            GenerateExpression(source, ref builder, context);
            builder.Append(") + @@DATEFIRST + 5) % 7 + 1)");
            return;
        }

        string datepart = field switch
        {
            "YEAR" => "year",
            "QUARTER" => "quarter",
            "MONTH" => "month",
            "WEEK" => "iso_week",
            "DAY" => "day",
            "DAY_OF_YEAR" => "dayofyear",
            "HOUR" => "hour",
            "MINUTE" => "minute",
            "SECOND" => "second",
            "MILLISECOND" => "millisecond",
            "MICROSECOND" => "microsecond",
            _ => throw UnsupportedExtract(field, TargetDialect)
        };
        builder.Append("DATEPART(");
        builder.Append(datepart);
        builder.Append(", ");
        GenerateExpression(source, ref builder, context);
        builder.Append(')');
    }

    /// <summary>Wunsch 4: T-SQL GROUPING() takes one column; the multi-column bitmask is GROUPING_ID().</summary>
    protected override void FormatGroupingOperation(ref ValueStringBuilder builder, GroupingOperationExpression grouping, SqlEmitterContext context)
    {
        builder.Append(grouping.Columns.Count == 1 ? "GROUPING(" : "GROUPING_ID(");
        for (int i = 0; i < grouping.Columns.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            GenerateExpression(grouping.Columns[i], ref builder, context);
        }
        builder.Append(')');
    }

    /// <summary>Wunsch 4: T-SQL has no DATE/TIMESTAMP literal syntax; <c>timestamp</c> would even mean rowversion.</summary>
    protected override void FormatTypedLiteral(ref ValueStringBuilder builder, TypedLiteralExpression literal, SqlEmitterContext context)
    {
        builder.Append("CAST(");
        FormatStringLiteral(ref builder, literal.Value, context);
        builder.Append(literal.Kind switch
        {
            TypedLiteralKind.Date => " AS date)",
            TypedLiteralKind.Time => " AS time)",
            _ => " AS datetime2)"
        });
    }

    protected override void FormatIntervalLiteral(ref ValueStringBuilder builder, IntervalLiteralExpression interval, SqlEmitterContext context) =>
        throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"SQL construct INTERVAL literal is not supported for {TargetDialect} (no interval type).");

    public override int MaxParameterBudget => 2100;

    /// <summary>Virtual filters (phase 7b): <c>DATEADD(unit, n, x)</c>.</summary>
    protected override void FormatDateAdd(ref ValueStringBuilder builder, DateUnit unit, long amount, Expression source, SqlEmitterContext context)
    {
        builder.Append("DATEADD(");
        builder.Append(DateUnitName(unit));
        builder.Append(", ");
        builder.Append(amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(", ");
        GenerateExpression(source, ref builder, context);
        builder.Append(')');
    }

    /// <summary>
    /// Virtual filters (phase 7b): SQL Server before 2022 has no DATETRUNC. Day, month and year are rebuilt from the date
    /// parts as <c>datetimeoffset</c> (comparable with the <c>datetimeoffset</c> columns and SYSDATETIMEOFFSET()); other
    /// units are rejected.
    /// </summary>
    protected override void FormatDateTrunc(ref ValueStringBuilder builder, DateUnit unit, Expression source, SqlEmitterContext context)
    {
        switch (unit)
        {
            case DateUnit.Day:
                builder.Append("CAST(CAST(");
                GenerateExpression(source, ref builder, context);
                builder.Append(" AS date) AS datetimeoffset)");
                return;
            case DateUnit.Month:
                builder.Append("CAST(DATEFROMPARTS(YEAR(");
                GenerateExpression(source, ref builder, context);
                builder.Append("), MONTH(");
                GenerateExpression(source, ref builder, context);
                builder.Append("), 1) AS datetimeoffset)");
                return;
            case DateUnit.Year:
                builder.Append("CAST(DATEFROMPARTS(YEAR(");
                GenerateExpression(source, ref builder, context);
                builder.Append("), 1, 1) AS datetimeoffset)");
                return;
            default:
                throw UnsupportedDateFunction("date_trunc", unit, TargetDialect);
        }
    }

    public override void FormatIdentifier(ref ValueStringBuilder builder, SqlIdentifier identifier, SqlEmitterContext context)
    {
        builder.Append('[');
        builder.Append(identifier.Value.Replace("]", "]]", StringComparison.Ordinal));
        builder.Append(']');
    }

    public override void FormatStringLiteral(ref ValueStringBuilder builder, string value, SqlEmitterContext context)
    {
        builder.Append("N'");
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

    protected override string GetBinaryOperatorString(BinaryOperator op)
    {
        if (op == BinaryOperator.Concat) return "+";
        return base.GetBinaryOperatorString(op);
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
            builder.Append(" THEN 1 ELSE 0 END");
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
        // T-SQL does not support the RECURSIVE keyword on CTE definitions
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

    protected override void GenerateOrderBy(OrderByClause orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        builder.Append("ORDER BY ");
        for (int i = 0; i < orderBy.Elements.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            var el = orderBy.Elements[i];

            if (el.NullOrder == NullOrdering.First && el.Direction == SortDirection.Descending)
            {
                builder.Append("CASE WHEN ");
                GenerateExpression(el.Expression, ref builder, context);
                builder.Append(" IS NULL THEN 0 ELSE 1 END, ");
            }
            else if (el.NullOrder == NullOrdering.Last && el.Direction == SortDirection.Ascending)
            {
                builder.Append("CASE WHEN ");
                GenerateExpression(el.Expression, ref builder, context);
                builder.Append(" IS NULL THEN 1 ELSE 0 END, ");
            }

            GenerateExpression(el.Expression, ref builder, context);
            builder.Append(el.Direction == SortDirection.Descending ? " DESC" : " ASC");
        }
    }

    protected override void GeneratePagination(PaginationClause pagination, OrderByClause? orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (orderBy == null)
        {
            builder.Append("ORDER BY (SELECT NULL) ");
        }

        builder.Append("OFFSET ");
        if (pagination.Offset != null)
        {
            GenerateExpression(pagination.Offset, ref builder, context);
        }
        else
        {
            builder.Append('0');
        }
        builder.Append(" ROWS");

        if (pagination.Limit != null)
        {
            builder.Append(" FETCH NEXT ");
            GenerateExpression(pagination.Limit, ref builder, context);
            builder.Append(" ROWS ONLY");
        }
    }
}

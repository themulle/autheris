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

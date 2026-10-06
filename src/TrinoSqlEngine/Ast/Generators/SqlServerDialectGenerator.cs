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

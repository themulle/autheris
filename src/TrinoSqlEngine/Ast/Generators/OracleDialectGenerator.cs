namespace TrinoSqlEngine.Ast.Generators;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
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
                builder.Append(field switch { "QUARTER" => ", 'Q'))", "WEEK" => ", 'IW'))", _ => ", 'DDD'))" });
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

        base.FormatTypedLiteral(ref builder, literal, context);
    }
    public override int MaxParameterBudget => 1000;

    protected override string TableAliasKeyword => " ";

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
        if (pagination.Offset != null)
        {
            builder.Append("OFFSET ");
            GenerateExpression(pagination.Offset, ref builder, context);
            builder.Append(" ROWS");
            if (pagination.Limit != null)
            {
                builder.Append(" FETCH NEXT ");
                GenerateExpression(pagination.Limit, ref builder, context);
                builder.Append(" ROWS ONLY");
            }
        }
        else if (pagination.Limit != null)
        {
            builder.Append("FETCH FIRST ");
            GenerateExpression(pagination.Limit, ref builder, context);
            builder.Append(" ROWS ONLY");
        }
    }
}

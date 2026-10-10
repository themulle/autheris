namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Extracts point lookup equality predicates (e.g. col = '123') from parsed SQL AST expressions.
/// </summary>
public static class SqlPointLookupExtractor
{
    public static Dictionary<string, string> ExtractPointLookups(SqlStatement statement)
    {
        var lookups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (statement is SelectStatement select && select.Body is QuerySpecification querySpec && querySpec.Where != null)
        {
            ExtractFromExpression(querySpec.Where, lookups);
        }
        return lookups;
    }

    public static Dictionary<string, string> ExtractPointLookups(Expression? expression)
    {
        var lookups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (expression != null)
        {
            ExtractFromExpression(expression, lookups);
        }
        return lookups;
    }

    private static void ExtractFromExpression(Expression expr, Dictionary<string, string> lookups)
    {
        if (expr is ParenthesizedExpression paren)
        {
            ExtractFromExpression(paren.Expression, lookups);
            return;
        }

        if (expr is BinaryExpression binary)
        {
            if (binary.Operator == BinaryOperator.And)
            {
                ExtractFromExpression(binary.Left, lookups);
                ExtractFromExpression(binary.Right, lookups);
                return;
            }

            if (binary.Operator == BinaryOperator.Equal)
            {
                if (binary.Left is ColumnReference colLeft && binary.Right is LiteralExpression litRight && litRight.Value != null)
                {
                    var colName = colLeft.Name.Parts[^1].Value;
                    lookups[colName] = litRight.Value.ToString() ?? string.Empty;
                }
                else if (binary.Right is ColumnReference colRight && binary.Left is LiteralExpression litLeft && litLeft.Value != null)
                {
                    var colName = colRight.Name.Parts[^1].Value;
                    lookups[colName] = litLeft.Value.ToString() ?? string.Empty;
                }
            }
        }
    }
}

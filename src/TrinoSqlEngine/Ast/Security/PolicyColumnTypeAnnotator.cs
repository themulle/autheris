namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// CR-ADG-09 / SEC-ADG-16 item 2: records the catalog data type of the target table's column on every policy parameter that is
/// compared with it (<c>col = :p</c>, <c>:p &lt;&gt; col</c>, <c>col IN (:p, ...)</c>), so the binder can follow the column type
/// (varchar versus nvarchar on SQL Server, NUMBER versus VARCHAR2 on Oracle). Subqueries are not entered: their columns belong
/// to other tables, whose types the target entry does not know (those binds keep the value type). It only annotates; it never
/// changes structure or values.
/// </summary>
internal sealed class PolicyColumnTypeAnnotator(TableCatalogEntry entry) : SqlAstRewriter
{
    private string? TypeOf(Expression expression)
    {
        if (expression is not ColumnReference column) return null;
        var parts = column.Name.Parts;
        bool target = parts.Count == 1 ||
            (parts.Count == 2 && string.Equals(parts[0].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase));
        if (!target) return null;

        string name = parts[^1].Value;
        return entry.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.DataType;
    }

    private static Expression Annotate(Expression expression, string? type) =>
        type is not null && expression is PolicyParameterExpression { Origin: ParameterOrigin.Policy, ColumnType: null } parameter
            ? parameter with { ColumnType = type }
            : expression;

    public override SqlNode VisitBinaryExpression(BinaryExpression node)
    {
        if (node.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual)
        {
            var left = Annotate(node.Left, TypeOf(node.Right));
            var right = Annotate(node.Right, TypeOf(node.Left));
            if (!ReferenceEquals(left, node.Left) || !ReferenceEquals(right, node.Right))
            {
                return node with { Left = left, Right = right };
            }

            return node;
        }

        return base.VisitBinaryExpression(node);
    }

    public override SqlNode VisitInListExpression(InListExpression node)
    {
        string? type = TypeOf(node.Operand);
        if (type is null) return base.VisitInListExpression(node);

        var items = new List<Expression>(node.Items.Count);
        bool changed = false;
        foreach (var item in node.Items)
        {
            var annotated = Annotate(item, type);
            changed |= !ReferenceEquals(annotated, item);
            items.Add(annotated);
        }

        return changed ? node with { Items = items } : node;
    }

    public override SqlNode VisitInSubqueryExpression(InSubqueryExpression node) => node;

    public override SqlNode VisitExistsExpression(ExistsExpression node) => node;

    public override SqlNode VisitScalarSubqueryExpression(ScalarSubqueryExpression node) => node;

    public override SqlNode VisitQuantifiedComparisonExpression(QuantifiedComparisonExpression node) => node;
}

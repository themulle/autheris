namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// WP-X0q / SEC-ADG-16 item 2: records the catalog data type of the column a user literal is compared with
/// (<c>col = 5</c>, <c>5 &lt; col</c>, <c>col IN (...)</c>, <c>col BETWEEN ... AND ...</c>), so the generator can reject a
/// cross-family comparison and the binder can follow the column type. It runs on the user tree before the security injection and
/// resolves a column only when that is unambiguous: through the alias or table name of a catalog table of the same query block, or
/// unqualified when every source of the block is a catalog table and exactly one has the column. CTE and derived-table columns,
/// correlated references and ambiguous names stay unannotated (the literal keeps its type). It only annotates; it never changes
/// structure or values.
/// </summary>
internal sealed class LiteralColumnTypeAnnotator(ITableCatalog catalog) : ColumnScopeTracker(catalog)
{
    private (bool Resolved, string? DataType) TypeOf(Expression expression) =>
        Resolve(expression) is { } column ? (true, column.Column.DataType) : (false, null);

    /// <summary>The literal under an optional unary minus, or null when <paramref name="expression"/> is another expression.</summary>
    private static LiteralExpression? LiteralOf(Expression expression) => expression switch
    {
        LiteralExpression { Type: LiteralType.Integer or LiteralType.Decimal or LiteralType.String } literal => literal,
        UnaryExpression { Operator: UnaryOperator.Negate, Operand: LiteralExpression { Type: LiteralType.Integer or LiteralType.Decimal } literal } => literal,
        _ => null
    };

    private Expression Annotate(Expression expression, (bool Resolved, string? DataType) column)
    {
        if (!column.Resolved || LiteralOf(expression) is not { ColumnType: null, ColumnTypeUnknown: false } literal) return expression;

        var annotated = ColumnBindAdapter.Classify(column.DataType) == ColumnTypeClass.Unknown
            ? literal with { ColumnTypeUnknown = true }
            : literal with { ColumnType = column.DataType };
        return expression is UnaryExpression negate ? negate with { Operand = annotated } : annotated;
    }

    public override SqlNode VisitBinaryExpression(BinaryExpression node)
    {
        var visited = (BinaryExpression)base.VisitBinaryExpression(node);
        if (visited.Operator is not (BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual))
        {
            return visited;
        }

        var left = Annotate(visited.Left, TypeOf(visited.Right));
        var right = Annotate(visited.Right, TypeOf(visited.Left));
        return ReferenceEquals(left, visited.Left) && ReferenceEquals(right, visited.Right) ? visited : visited with { Left = left, Right = right };
    }

    public override SqlNode VisitIsDistinctFromExpression(IsDistinctFromExpression node)
    {
        var visited = (IsDistinctFromExpression)base.VisitIsDistinctFromExpression(node);
        var left = Annotate(visited.Left, TypeOf(visited.Right));
        var right = Annotate(visited.Right, TypeOf(visited.Left));
        return ReferenceEquals(left, visited.Left) && ReferenceEquals(right, visited.Right) ? visited : visited with { Left = left, Right = right };
    }

    public override SqlNode VisitInListExpression(InListExpression node)
    {
        var visited = (InListExpression)base.VisitInListExpression(node);
        var column = TypeOf(visited.Operand);
        if (!column.Resolved) return visited;

        var items = new List<Expression>(visited.Items.Count);
        bool changed = false;
        foreach (var item in visited.Items)
        {
            var annotated = Annotate(item, column);
            changed |= !ReferenceEquals(annotated, item);
            items.Add(annotated);
        }

        return changed ? visited with { Items = items } : visited;
    }

    public override SqlNode VisitBetweenExpression(BetweenExpression node)
    {
        var visited = (BetweenExpression)base.VisitBetweenExpression(node);
        var column = TypeOf(visited.Operand);
        var lower = Annotate(visited.Lower, column);
        var upper = Annotate(visited.Upper, column);
        return ReferenceEquals(lower, visited.Lower) && ReferenceEquals(upper, visited.Upper) ? visited : visited with { Lower = lower, Upper = upper };
    }
}

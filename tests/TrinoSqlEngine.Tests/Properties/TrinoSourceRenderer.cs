using System.Globalization;
using System.Text;
using TrinoSqlEngine.Ast.Nodes;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>Raised for a node the test-only renderer does not print. The G3 property counts these cases and bounds their share.</summary>
internal sealed class RendererGapException(string node) : Exception($"TrinoSourceRenderer does not print {node}.");

/// <summary>
/// Test-only printer of the read-query AST back to Trino SQL (plan 9.3 G3, sqlglot's <c>validate_identity</c>). It adds no
/// parentheses of its own: the AST keeps explicit parentheses as <see cref="ParenthesizedExpression"/>, so printing the tree flat
/// must parse back to the same tree. A difference proves that the builder lost or invented structure.
/// </summary>
internal static class TrinoSourceRenderer
{
    public static string Render(SqlStatement statement) => statement switch
    {
        SelectStatement select => Select(select),
        _ => throw new RendererGapException(statement.GetType().Name)
    };

    private static string Select(SelectStatement s)
    {
        var sb = new StringBuilder();
        if (s.With is { } with)
        {
            sb.Append("WITH ").Append(with.IsRecursive ? "RECURSIVE " : string.Empty);
            sb.Append(string.Join(", ", with.Ctes.Select(c =>
                Identifier(c.Name) + (c.ColumnAliases is { Count: > 0 } cols ? " (" + string.Join(", ", cols.Select(Identifier)) + ")" : string.Empty) + " AS (" + Select(c.Query) + ")")));
            sb.Append(' ');
        }

        sb.Append(Body(s.Body));
        if (s.OrderBy is { } order)
        {
            sb.Append(' ').Append(OrderBy(order));
        }

        if (s.Pagination is { } page)
        {
            if (page.Offset is { } offset)
            {
                sb.Append(" OFFSET ").Append(Expr(offset)).Append(" ROWS");
            }

            if (page.Limit is { } limit)
            {
                sb.Append(page.WithTies ? " FETCH FIRST " + Expr(limit) + " ROWS WITH TIES" : " LIMIT " + Expr(limit));
            }
        }

        return sb.ToString();
    }

    private static string Body(QueryBody body) => body switch
    {
        QuerySpecification q => Spec(q),
        SetOperationQuery set => Body(set.Left) + " " + set.Operator.ToString().ToUpperInvariant() + (set.Distinct ? string.Empty : " ALL") + " " + Body(set.Right),
        ValuesQueryBody v => "VALUES " + string.Join(", ", v.Rows.Select(r => "(" + string.Join(", ", r.Elements.Select(Expr)) + ")")),
        _ => throw new RendererGapException(body.GetType().Name)
    };

    private static string Spec(QuerySpecification q)
    {
        var sb = new StringBuilder("SELECT ");
        if (q.Distinct)
        {
            sb.Append("DISTINCT ");
        }

        sb.Append(string.Join(", ", q.Projections.Select(Item)));
        if (q.From is { } from)
        {
            sb.Append(" FROM ").Append(Source(from));
        }

        if (q.Where is { } where)
        {
            sb.Append(" WHERE ").Append(Expr(where));
        }

        if (q.GroupBy is not null || q.Having is not null)
        {
            throw new RendererGapException("GROUP BY / HAVING");
        }

        return sb.ToString();
    }

    private static string Item(SelectItem item) => item switch
    {
        ColumnSelectItem c => Expr(c.Expression) + (c.Alias is { } a ? " AS " + Identifier(a) : string.Empty),
        WildcardSelectItem w => (w.Qualifier is { } q ? Name(q) + "." : string.Empty) + "*",
        _ => throw new RendererGapException(item.GetType().Name)
    };

    private static string Source(TableSource source) => source switch
    {
        NamedTableSource n => Name(n.Name) + (n.Alias is { } a ? " AS " + Identifier(a) : string.Empty),
        SubqueryTableSource s => "(" + Select(s.Subquery) + ") AS " + Identifier(s.Alias) + Columns(s.ColumnAliases),
        JoinedTableSource j => Source(j.Left) + " " + Join(j.Type) + " " + Source(j.Right) + (j.Condition is OnJoinCondition on ? " ON " + Expr(on.Predicate) : string.Empty),
        _ => throw new RendererGapException(source.GetType().Name)
    };

    private static string Columns(IReadOnlyList<SqlIdentifier>? columns) =>
        columns is { Count: > 0 } ? " (" + string.Join(", ", columns.Select(Identifier)) + ")" : string.Empty;

    private static string Join(JoinType type) => type switch
    {
        JoinType.Inner => "INNER JOIN",
        JoinType.LeftOuter => "LEFT JOIN",
        JoinType.Cross => "CROSS JOIN",
        _ => throw new RendererGapException("join " + type)
    };

    private static string OrderBy(OrderByClause order) => "ORDER BY " + string.Join(", ", order.Elements.Select(e =>
        Expr(e.Expression) + (e.Direction == SortDirection.Descending ? " DESC" : string.Empty) +
        e.NullOrder switch { NullOrdering.First => " NULLS FIRST", NullOrdering.Last => " NULLS LAST", _ => string.Empty }));

    private static string Expr(Expression e) => e switch
    {
        ColumnReference c => Name(c.Name),
        ParenthesizedExpression p => "(" + Expr(p.Expression) + ")",
        LiteralExpression l => Literal(l),
        BinaryExpression b => Operand(b.Left, Precedence(b), false) + " " + Operator(b.Operator) + " " + Operand(b.Right, Precedence(b), true),
        UnaryExpression u => u.Operator switch
        {
            UnaryOperator.Not => "NOT " + Operand(u.Operand, 3, false),
            UnaryOperator.Negate => "- " + Operand(u.Operand, 8, false),
            UnaryOperator.IsNull => Operand(u.Operand, 5, false) + " IS NULL",
            _ => Operand(u.Operand, 5, false) + " IS NOT NULL"
        },
        LikeExpression l => Operand(l.Operand, 5, false) + (l.IsNotLike ? " NOT LIKE " : " LIKE ") + Operand(l.Pattern, 5, false) + (l.Escape is { } esc ? " ESCAPE " + Operand(esc, 5, false) : string.Empty),
        InListExpression i => Operand(i.Operand, 5, false) + (i.IsNotIn ? " NOT IN (" : " IN (") + string.Join(", ", i.Items.Select(Expr)) + ")",
        InSubqueryExpression i => Operand(i.Operand, 5, false) + (i.IsNotIn ? " NOT IN (" : " IN (") + Select(i.Subquery) + ")",
        ExistsExpression x => "EXISTS (" + Select(x.Subquery) + ")",
        ScalarSubqueryExpression s => "(" + Select(s.Subquery) + ")",
        BetweenExpression b => Operand(b.Operand, 5, false) + (b.IsNotBetween ? " NOT BETWEEN " : " BETWEEN ") + Operand(b.Lower, 5, false) + " AND " + Operand(b.Upper, 5, false),
        CaseExpression c => "CASE" + (c.Operand is { } op ? " " + Expr(op) : string.Empty) +
            string.Concat(c.WhenClauses.Select(w => " WHEN " + Expr(w.Condition) + " THEN " + Expr(w.Result))) +
            (c.ElseResult is { } el ? " ELSE " + Expr(el) : string.Empty) + " END",
        FunctionCallExpression f => Function(f),
        RowValueExpression r => "(" + string.Join(", ", r.Elements.Select(Expr)) + ")",
        _ => throw new RendererGapException(e.GetType().Name)
    };

    /// <summary>Prints a child, adding parentheses when it binds looser than its parent (the builder drops redundant parentheses in some positions).</summary>
    private static string Operand(Expression child, int parentPrecedence, bool strict) =>
        Precedence(child) < parentPrecedence || strict && Precedence(child) == parentPrecedence ? "(" + Expr(child) + ")" : Expr(child);

    private static int Precedence(Expression e) => e switch
    {
        BinaryExpression b => b.Operator switch
        {
            BinaryOperator.Or => 1,
            BinaryOperator.And => 2,
            BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
                or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual => 4,
            BinaryOperator.Concat => 5,
            BinaryOperator.Add or BinaryOperator.Subtract => 6,
            _ => 7
        },
        UnaryExpression { Operator: UnaryOperator.Not } => 3,
        UnaryExpression { Operator: UnaryOperator.Negate } => 8,
        UnaryExpression or LikeExpression or InListExpression or InSubqueryExpression or BetweenExpression => 4,
        _ => 10
    };

    private static string Function(FunctionCallExpression f)
    {
        if (f.Filter is not null || f.OrderWithin is not null)
        {
            throw new RendererGapException("aggregate FILTER / ORDER BY");
        }

        var sb = new StringBuilder(Name(f.Name)).Append('(');
        sb.Append(f.IsStar ? "*" : (f.Distinct ? "DISTINCT " : string.Empty) + string.Join(", ", f.Arguments.Select(Expr))).Append(')');
        if (f.Window is { } w)
        {
            if (w.Frame is not null)
            {
                throw new RendererGapException("window frame");
            }

            sb.Append(" OVER (");
            if (w.PartitionBy is { Count: > 0 } partition)
            {
                sb.Append("PARTITION BY ").Append(string.Join(", ", partition.Select(Expr)));
                sb.Append(w.OrderBy is null ? string.Empty : " ");
            }

            if (w.OrderBy is { } order)
            {
                sb.Append(OrderBy(order));
            }

            sb.Append(')');
        }

        return sb.ToString();
    }

    private static string Literal(LiteralExpression l) => l.Type switch
    {
        LiteralType.Null => "NULL",
        LiteralType.Boolean => Convert.ToBoolean(l.Value, CultureInfo.InvariantCulture) ? "TRUE" : "FALSE",
        LiteralType.Integer or LiteralType.Decimal => Convert.ToString(l.Value, CultureInfo.InvariantCulture)!,
        LiteralType.String => QueryGen.Quote((string)l.Value!),
        _ => throw new RendererGapException("literal " + l.Type)
    };

    private static string Operator(BinaryOperator op) => op switch
    {
        BinaryOperator.And => "AND",
        BinaryOperator.Or => "OR",
        BinaryOperator.Equal => "=",
        BinaryOperator.NotEqual => "<>",
        BinaryOperator.LessThan => "<",
        BinaryOperator.LessThanOrEqual => "<=",
        BinaryOperator.GreaterThan => ">",
        BinaryOperator.GreaterThanOrEqual => ">=",
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Modulo => "%",
        _ => "||"
    };

    private static string Name(SqlQualifiedName name) => string.Join(".", name.Parts.Select(Identifier));

    private static string Identifier(SqlIdentifier id) => id.IsQuoted ? "\"" + id.Value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : id.Value;
}

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
internal sealed class LiteralColumnTypeAnnotator(ITableCatalog catalog) : SqlAstRewriter
{
    /// <summary>A table source of a query block: a catalog table (<see cref="Entry"/> set) or an opaque source (CTE, derived table).</summary>
    private sealed record Source(string Key, TableCatalogEntry? Entry);

    private readonly Stack<IReadOnlyList<Source>> _scopes = new();
    private readonly Stack<HashSet<string>> _cteNames = new();

    public override SqlNode VisitSelectStatement(SelectStatement node)
    {
        if (node.With is null) return base.VisitSelectStatement(node);

        var names = new HashSet<string>(_cteNames.Count > 0 ? _cteNames.Peek() : Enumerable.Empty<string>(), StringComparer.Ordinal);
        _cteNames.Push(names);
        try
        {
            // A CTE name shadows a catalog table of the same name from its definition onwards; recursive CTEs see their own name.
            foreach (var cte in node.With.Ctes)
            {
                names.Add(SqlIdentifierHelper.FoldIdentifierForScope(cte.Name));
            }

            return base.VisitSelectStatement(node);
        }
        finally
        {
            _cteNames.Pop();
        }
    }

    public override SqlNode VisitQuerySpecification(QuerySpecification node)
    {
        var sources = new List<Source>();
        if (node.From is not null) CollectSources(node.From, sources);
        _scopes.Push(sources);
        try
        {
            return base.VisitQuerySpecification(node);
        }
        finally
        {
            _scopes.Pop();
        }
    }

    public override SqlNode VisitUpdateStatement(UpdateStatement node) => WithTarget(node.TargetTable, () => base.VisitUpdateStatement(node));

    public override SqlNode VisitDeleteStatement(DeleteStatement node) => WithTarget(node.TargetTable, () => base.VisitDeleteStatement(node));

    private SqlNode WithTarget(NamedTableSource target, Func<SqlNode> visit)
    {
        var sources = new List<Source>();
        CollectSources(target, sources);
        _scopes.Push(sources);
        try
        {
            return visit();
        }
        finally
        {
            _scopes.Pop();
        }
    }

    private void CollectSources(TableSource source, List<Source> sources)
    {
        switch (source)
        {
            case NamedTableSource named:
                bool isCte = named.Name.IsSimple && _cteNames.Count > 0 && _cteNames.Peek().Contains(SqlIdentifierHelper.FoldIdentifierForScope(named.Name.Parts[0]));
                var entry = isCte ? null : catalog.Resolve(named.Name);
                sources.Add(new Source(named.Alias?.Value ?? named.Name.SimpleName, entry));
                break;
            case JoinedTableSource joined:
                CollectSources(joined.Left, sources);
                CollectSources(joined.Right, sources);
                break;
            case SubqueryTableSource subquery:
                sources.Add(new Source(subquery.Alias.Value, null));
                break;
            case LateralTableSource lateral:
                sources.Add(new Source(lateral.Alias.Value, null));
                break;
            default:
                sources.Add(new Source(string.Empty, null));
                break;
        }
    }

    /// <summary>The catalog type of <paramref name="expression"/> when it is a resolvable column: (true, type) or (false, null) when it is not.</summary>
    private (bool Resolved, string? DataType) TypeOf(Expression expression)
    {
        if (expression is not ColumnReference column || _scopes.Count == 0) return (false, null);
        var parts = column.Name.Parts;
        string name = parts[^1].Value;
        var sources = _scopes.Peek();

        if (parts.Count >= 2)
        {
            var matches = sources.Where(s => string.Equals(s.Key, parts[^2].Value, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            return matches.Count == 1 && matches[0].Entry is { } qualified ? Lookup(qualified, name) : (false, null);
        }

        // Unqualified: only decidable when every source is a catalog table and exactly one of them has the column.
        if (sources.Any(s => s.Entry is null)) return (false, null);
        var owners = sources.Where(s => HasColumn(s.Entry!, name)).Take(2).ToList();
        return owners.Count == 1 ? Lookup(owners[0].Entry!, name) : (false, null);
    }

    private static bool HasColumn(TableCatalogEntry entry, string name) =>
        entry.Columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static (bool Resolved, string? DataType) Lookup(TableCatalogEntry entry, string name)
    {
        var column = entry.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return column is null ? (false, null) : (true, column.DataType);
    }

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

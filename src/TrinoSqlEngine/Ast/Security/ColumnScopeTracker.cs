namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>A column reference resolved to a catalog table and column.</summary>
internal sealed record ResolvedColumn(TableCatalogEntry Table, CatalogColumn Column);

/// <summary>
/// The scope rules of a query block, shared by the passes that must know which catalog column an expression refers to (the literal type
/// annotator and the time zone call validator). It resolves a column only when that is unambiguous: through the alias or table name of a catalog
/// table of the same query block, or unqualified when every source of the block is a catalog table and exactly one has the column. CTE and
/// derived-table columns, correlated references and ambiguous names stay unresolved. It never changes the tree; a derived class overrides the
/// visit methods it needs and calls the base.
/// </summary>
internal abstract class ColumnScopeTracker(ITableCatalog catalog) : SqlAstRewriter
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
            _lastBlock = sources;
            _scopes.Pop();
        }
    }

    private IReadOnlyList<Source>? _lastBlock;

    /// <summary>
    /// Re-enters the sources of the query block that was visited last, for the ORDER BY of its select statement (the order by clause is a
    /// sibling of the query specification, so it is visited outside the block). Returns false when there is no block to re-enter.
    /// </summary>
    protected bool PushLastBlock()
    {
        if (_lastBlock is null) return false;
        _scopes.Push(_lastBlock);
        return true;
    }

    protected void PopBlock() => _scopes.Pop();

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

    /// <summary>The catalog column of <paramref name="expression"/> when it is a resolvable column of the current query block, or null when it is not.</summary>
    protected ResolvedColumn? Resolve(Expression expression)
    {
        if (expression is not ColumnReference column || _scopes.Count == 0) return null;
        var parts = column.Name.Parts;
        string name = parts[^1].Value;
        var sources = _scopes.Peek();

        if (parts.Count >= 2)
        {
            // Exactly one source with this alias, and it is a catalog table.
            string qualifier = parts[^2].Value;
            TableCatalogEntry? only = null;
            int found = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                if (!string.Equals(source.Key, qualifier, StringComparison.OrdinalIgnoreCase)) continue;
                if (++found > 1) break;
                only = source.Entry;
            }

            return found == 1 && only is { } qualified ? Lookup(qualified, name) : null;
        }

        // Unqualified: only decidable when every source is a catalog table and exactly one of them has the column.
        TableCatalogEntry? owner = null;
        int owners = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (source.Entry is null) return null;
            if (!HasColumn(source.Entry, name)) continue;
            if (++owners == 1) owner = source.Entry;
        }

        return owners == 1 ? Lookup(owner!, name) : null;
    }

    private static bool HasColumn(TableCatalogEntry entry, string name) =>
        entry.Columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static ResolvedColumn? Lookup(TableCatalogEntry entry, string name)
    {
        var column = entry.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return column is null ? null : new ResolvedColumn(entry, column);
    }
}

namespace Autheris.Application.Products;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

/// <summary>One output column of a product query with the catalog columns it is derived from; <see cref="Unresolved"/> when any input could not be resolved.</summary>
public sealed record ProductOutputColumn(string Name, IReadOnlyList<ColumnLineage> Lineage, bool Unresolved, string? DataType = null);

/// <summary>A column of a catalog object as stored in the catalog (never read from the source).</summary>
public sealed record CatalogColumnInfo(string Name, string DataType, bool IsPii);

/// <summary>
/// Derives the base objects and the output columns with their lineage of a product SELECT from the AST and stored catalog metadata only.
/// Objects are addressed by catalog id (<c>domain.schema.table</c>); anything else makes the definition invalid (null result).
/// </summary>
public static class ProductLineageExtractor
{
    private const int MaxDepth = 16;

    /// <summary>The distinct catalog objects named by the statement, or null when a table is not addressed by a three-part catalog id.</summary>
    public static IReadOnlyList<TableIdentifier>? CollectBaseObjects(SqlStatement statement)
    {
        if (statement is not SelectStatement select)
        {
            return null;
        }

        var found = new List<TableIdentifier>();
        return CollectSelect(select, [], found, 0) ? found.Distinct().ToList() : null;
    }

    /// <summary>The output columns, or null when the statement is not a resolvable SELECT over the supplied catalog columns.</summary>
    public static IReadOnlyList<ProductOutputColumn>? Extract(
        SqlStatement statement,
        IReadOnlyDictionary<TableIdentifier, IReadOnlyList<CatalogColumnInfo>> catalog)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(catalog);
        if (statement is not SelectStatement select)
        {
            return null;
        }

        try
        {
            return ExtractSelect(select, catalog, new Dictionary<string, IReadOnlyList<ProductOutputColumn>>(StringComparer.OrdinalIgnoreCase), 0);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool CollectSelect(SelectStatement select, HashSet<string> cteNames, List<TableIdentifier> found, int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        var names = new HashSet<string>(cteNames, StringComparer.OrdinalIgnoreCase);
        if (select.With is { } with)
        {
            foreach (var cte in with.Ctes)
            {
                if (!CollectSelect(cte.Query, names, found, depth + 1))
                {
                    return false;
                }

                names.Add(cte.Name.Value);
            }
        }

        return CollectBody(select.Body, names, found, depth);
    }

    private static bool CollectBody(QueryBody body, HashSet<string> cteNames, List<TableIdentifier> found, int depth) => body switch
    {
        QuerySpecification spec => spec.From is null || CollectSource(spec.From, cteNames, found, depth),
        SetOperationQuery set => CollectBody(set.Left, cteNames, found, depth) && CollectBody(set.Right, cteNames, found, depth),
        _ => false
    };

    private static bool CollectSource(TableSource source, HashSet<string> cteNames, List<TableIdentifier> found, int depth)
    {
        switch (source)
        {
            case NamedTableSource named when named.Name.Parts.Count == 1 && cteNames.Contains(named.Name.SimpleName):
                return true;
            case NamedTableSource named when TryCatalogId(named.Name, out var id):
                found.Add(id);
                return true;
            case SubqueryTableSource sub:
                return CollectSelect(sub.Subquery, cteNames, found, depth + 1);
            case JoinedTableSource join:
                return CollectSource(join.Left, cteNames, found, depth) && CollectSource(join.Right, cteNames, found, depth);
            default:
                return false;
        }
    }

    private static bool TryCatalogId(SqlQualifiedName name, out TableIdentifier id)
    {
        id = default;
        return name.Parts.Count == 3 && TableIdentifier.TryCreate(name.Parts[0].Value, name.Parts[1].Value, name.Parts[2].Value, out id);
    }

    private sealed record SourceScope(string Alias, IReadOnlyList<ProductOutputColumn> Columns);

    private static IReadOnlyList<ProductOutputColumn> ExtractSelect(
        SelectStatement select,
        IReadOnlyDictionary<TableIdentifier, IReadOnlyList<CatalogColumnInfo>> catalog,
        Dictionary<string, IReadOnlyList<ProductOutputColumn>> ctes,
        int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException("The query is nested too deeply.");
        }

        var scoped = new Dictionary<string, IReadOnlyList<ProductOutputColumn>>(ctes, StringComparer.OrdinalIgnoreCase);
        if (select.With is { } with)
        {
            foreach (var cte in with.Ctes)
            {
                var columns = ExtractSelect(cte.Query, catalog, scoped, depth + 1);
                scoped[cte.Name.Value] = Rename(columns, cte.ColumnAliases);
            }
        }

        return ExtractBody(select.Body, catalog, scoped, depth);
    }

    private static IReadOnlyList<ProductOutputColumn> ExtractBody(
        QueryBody body,
        IReadOnlyDictionary<TableIdentifier, IReadOnlyList<CatalogColumnInfo>> catalog,
        Dictionary<string, IReadOnlyList<ProductOutputColumn>> ctes,
        int depth)
    {
        switch (body)
        {
            case SetOperationQuery set:
            {
                var left = ExtractBody(set.Left, catalog, ctes, depth + 1);
                var right = ExtractBody(set.Right, catalog, ctes, depth + 1);
                if (left.Count != right.Count)
                {
                    throw new InvalidOperationException("The set operation branches differ in column count.");
                }

                return left.Select((l, i) => new ProductOutputColumn(
                    l.Name,
                    l.Lineage.Concat(right[i].Lineage).Distinct().ToList(),
                    l.Unresolved || right[i].Unresolved,
                    string.Equals(l.DataType, right[i].DataType, StringComparison.OrdinalIgnoreCase) ? l.DataType : null)).ToList();
            }
            case QuerySpecification spec:
                return ExtractSpec(spec, catalog, ctes, depth);
            default:
                throw new InvalidOperationException("Unsupported query body.");
        }
    }

    private static IReadOnlyList<ProductOutputColumn> ExtractSpec(
        QuerySpecification spec,
        IReadOnlyDictionary<TableIdentifier, IReadOnlyList<CatalogColumnInfo>> catalog,
        Dictionary<string, IReadOnlyList<ProductOutputColumn>> ctes,
        int depth)
    {
        var sources = new List<SourceScope>();
        if (spec.From is not null)
        {
            AddSources(spec.From, catalog, ctes, sources, depth);
        }

        var output = new List<ProductOutputColumn>();
        foreach (var item in spec.Projections)
        {
            switch (item)
            {
                case WildcardSelectItem wildcard:
                {
                    var selected = wildcard.Qualifier is null
                        ? sources
                        : sources.Where(s => string.Equals(s.Alias, wildcard.Qualifier.SimpleName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (selected.Count == 0)
                    {
                        throw new InvalidOperationException("The wildcard matches no source.");
                    }

                    foreach (var source in selected)
                    {
                        output.AddRange(source.Columns);
                    }

                    break;
                }
                case ColumnSelectItem column:
                {
                    var name = column.Alias?.Value ?? (column.Expression is ColumnReference reference ? reference.Name.SimpleName : $"_col{output.Count}");
                    output.Add(Derive(name, column.Expression, sources));
                    break;
                }
                default:
                    throw new InvalidOperationException("Unsupported projection.");
            }
        }

        var duplicate = output.GroupBy(static c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(static g => g.Count() > 1);
        if (duplicate is not null || output.Count == 0)
        {
            throw new InvalidOperationException("The output column names must be unique.");
        }

        return output;
    }

    private static void AddSources(
        TableSource source,
        IReadOnlyDictionary<TableIdentifier, IReadOnlyList<CatalogColumnInfo>> catalog,
        Dictionary<string, IReadOnlyList<ProductOutputColumn>> ctes,
        List<SourceScope> sources,
        int depth)
    {
        switch (source)
        {
            case NamedTableSource named when named.Name.Parts.Count == 1 && ctes.TryGetValue(named.Name.SimpleName, out var cteColumns):
                sources.Add(new SourceScope(named.Alias?.Value ?? named.Name.SimpleName, cteColumns));
                break;
            case NamedTableSource named when TryCatalogId(named.Name, out var id) && catalog.TryGetValue(id, out var infos):
                sources.Add(new SourceScope(
                    named.Alias?.Value ?? id.TableName,
                    infos.Select(i => new ProductOutputColumn(i.Name, [new ColumnLineage(id, i.Name)], false, i.DataType)).ToList()));
                break;
            case SubqueryTableSource sub:
                sources.Add(new SourceScope(sub.Alias.Value, Rename(ExtractSelect(sub.Subquery, catalog, ctes, depth + 1), sub.ColumnAliases)));
                break;
            case JoinedTableSource join:
                AddSources(join.Left, catalog, ctes, sources, depth);
                AddSources(join.Right, catalog, ctes, sources, depth);
                break;
            default:
                throw new InvalidOperationException("The table source is not a resolvable catalog object.");
        }
    }

    private static IReadOnlyList<ProductOutputColumn> Rename(IReadOnlyList<ProductOutputColumn> columns, IReadOnlyList<SqlIdentifier>? aliases)
    {
        if (aliases is null)
        {
            return columns;
        }

        if (aliases.Count != columns.Count)
        {
            throw new InvalidOperationException("The column alias list does not match the column count.");
        }

        return columns.Select((c, i) => c with { Name = aliases[i].Value }).ToList();
    }

    private static ProductOutputColumn Derive(string name, Expression expression, List<SourceScope> sources)
    {
        var collector = new ReferenceCollector();
        collector.Visit(expression);
        var lineage = new List<ColumnLineage>();
        var unresolved = collector.HasSubquery;
        string? dataType = null;
        foreach (var reference in collector.References)
        {
            var parts = reference.Name.Parts;
            var column = parts[^1].Value;
            var candidates = parts.Count >= 2
                ? sources.Where(s => string.Equals(s.Alias, parts[^2].Value, StringComparison.OrdinalIgnoreCase)).ToList()
                : sources;
            var matches = candidates
                .SelectMany(s => s.Columns.Where(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matches.Count != 1)
            {
                unresolved = true;
                continue;
            }

            lineage.AddRange(matches[0].Lineage);
            unresolved |= matches[0].Unresolved;
            dataType = expression is ColumnReference ? matches[0].DataType : null;
        }

        return new ProductOutputColumn(name, lineage.Distinct().ToList(), unresolved, dataType);
    }

    private sealed class ReferenceCollector : SqlAstRewriter
    {
        public List<ColumnReference> References { get; } = [];

        public bool HasSubquery { get; private set; }

        public override SqlNode VisitColumnReference(ColumnReference node)
        {
            References.Add(node);
            return node;
        }

        public override SqlNode VisitSelectStatement(SelectStatement node)
        {
            HasSubquery = true;
            return node;
        }
    }
}

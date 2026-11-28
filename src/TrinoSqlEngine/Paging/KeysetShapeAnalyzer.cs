namespace TrinoSqlEngine;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>The AST-level outcome of the keyset shape analysis. <see cref="Reason"/> is null when the statement is eligible.</summary>
internal readonly record struct KeysetShape(
    ImmutableArray<string> Columns,
    ImmutableArray<bool> Descending,
    ImmutableArray<SqlIdentifier> ColumnIdentifiers,
    NamedTableSource? Table,
    string? Reason);

/// <summary>
/// Decides, on the user tree, whether a root SELECT is a single-table read ordered exactly by the declared primary key
/// (every key column once, plain column references), so that "rows after the last row" is a well-defined seek predicate.
/// </summary>
internal static class KeysetShapeAnalyzer
{
    public static KeysetShape Analyze(SqlStatement userTree, KeysetCandidate candidate)
    {
        if (candidate.KeyColumns.IsDefaultOrEmpty)
        {
            return Reject(KeysetUnavailableReasons.Shape);
        }

        if (candidate.KeyColumns.Length > KeysetLimits.MaxKeyColumns)
        {
            return Reject(KeysetUnavailableReasons.KeyTooWide);
        }

        if (userTree is not SelectStatement { With: null, Pagination: null, OrderBy: { } orderBy, Body: QuerySpecification spec }
            || spec.Distinct || spec.GroupBy is not null || spec.Having is not null
            || spec.From is not NamedTableSource table)
        {
            return Reject(KeysetUnavailableReasons.Shape);
        }

        foreach (var projection in spec.Projections)
        {
            if (ContainsWindow(projection))
            {
                return Reject(KeysetUnavailableReasons.Window);
            }
        }

        var columns = ImmutableArray.CreateBuilder<string>(orderBy.Elements.Count);
        var descending = ImmutableArray.CreateBuilder<bool>(orderBy.Elements.Count);
        var identifiers = ImmutableArray.CreateBuilder<SqlIdentifier>(orderBy.Elements.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in orderBy.Elements)
        {
            if (element.Expression is not ColumnReference reference || !QualifierMatches(reference.Name, table))
            {
                return Reject(KeysetUnavailableReasons.NotPrimaryKey);
            }

            var identifier = reference.Name.Parts[^1];
            string? key = null;
            foreach (var name in candidate.KeyColumns)
            {
                if (string.Equals(name, identifier.Value, StringComparison.OrdinalIgnoreCase))
                {
                    key = name;
                    break;
                }
            }

            if (key is null || !seen.Add(key))
            {
                return Reject(KeysetUnavailableReasons.NotPrimaryKey);
            }

            columns.Add(key);
            descending.Add(element.Direction == SortDirection.Descending);
            identifiers.Add(identifier);
        }

        if (columns.Count != candidate.KeyColumns.Length)
        {
            return Reject(KeysetUnavailableReasons.NotPrimaryKey);
        }

        foreach (var projection in spec.Projections)
        {
            // ORDER BY binds output aliases first: an alias that spells a key column must be that very column.
            if (projection is ColumnSelectItem { Alias: { } alias } item && seen.Contains(alias.Value)
                && !(item.Expression is ColumnReference same && string.Equals(same.Name.Parts[^1].Value, alias.Value, StringComparison.OrdinalIgnoreCase)))
            {
                return Reject(KeysetUnavailableReasons.AliasShadow);
            }
        }

        return new KeysetShape(columns.ToImmutable(), descending.ToImmutable(), identifiers.ToImmutable(), table, null);
    }

    /// <summary>The identifier a key column reference of the root block is qualified with: the alias, else the simple table name.</summary>
    public static SqlIdentifier QualifierOf(NamedTableSource table) => table.Alias ?? table.Name.Parts[^1];

    private static KeysetShape Reject(string reason) => new(default, default, default, null, reason);

    private static bool QualifierMatches(SqlQualifiedName name, NamedTableSource table)
    {
        if (name.Parts.Count == 1)
        {
            return true;
        }

        if (table.Alias is { } alias)
        {
            return name.Parts.Count == 2 && string.Equals(name.Parts[0].Value, alias.Value, StringComparison.OrdinalIgnoreCase);
        }

        // Unaliased: the column may be qualified with a trailing part sequence of the table name (t.id or schema.t.id).
        int qualifierLength = name.Parts.Count - 1;
        if (qualifierLength > table.Name.Parts.Count)
        {
            return false;
        }

        for (int i = 0; i < qualifierLength; i++)
        {
            var written = name.Parts[i].Value;
            var declared = table.Name.Parts[table.Name.Parts.Count - qualifierLength + i].Value;
            if (!string.Equals(written, declared, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsWindow(SqlNode root)
    {
        var stack = new Stack<SqlNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is FunctionCallExpression { Window: not null })
            {
                return true;
            }

            foreach (var child in node.EnumerateChildren())
            {
                stack.Push(child);
            }
        }

        return false;
    }
}

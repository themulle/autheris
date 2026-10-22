namespace Autheris.Application.Sql.Federation.Conjuncts;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// SQLA-11 / SQLA-16: which leaf sources of a FROM clause are preserved (all their rows reach the result) or null-supplying (their rows may
/// be replaced by NULLs) under outer joins. A source on either side of a FULL join is both. Shared by the key-set planner and the subtree
/// partitioner so that no pushdown restricts a preserved side or pushes a WHERE filter into a null-supplying side.
/// </summary>
public sealed class OuterJoinSides
{
    private OuterJoinSides(IReadOnlySet<TableSource> preserved, IReadOnlySet<TableSource> nullSupplying, IReadOnlyList<TableSource> leaves)
    {
        Preserved = preserved;
        NullSupplying = nullSupplying;
        Leaves = leaves;
    }

    public IReadOnlySet<TableSource> Preserved { get; }

    public IReadOnlySet<TableSource> NullSupplying { get; }

    /// <summary>The leaf sources (named tables, subqueries, lateral sources) of the FROM clause in order.</summary>
    public IReadOnlyList<TableSource> Leaves { get; }

    public static OuterJoinSides Compute(QuerySpecification querySpec)
    {
        ArgumentNullException.ThrowIfNull(querySpec);
        var preserved = new HashSet<TableSource>();
        var nullSupplying = new HashSet<TableSource>();
        var leaves = new List<TableSource>();
        if (querySpec.From != null)
        {
            Walk(querySpec.From, preserved, nullSupplying, leaves);
        }

        return new OuterJoinSides(preserved, nullSupplying, leaves);
    }

    public bool IsPreserved(TableSource leaf) => Preserved.Contains(leaf);

    public bool IsNullSupplying(TableSource leaf) => NullSupplying.Contains(leaf);

    /// <summary>
    /// Finds the leaf of <paramref name="target"/>: the named table with that table name and alias. Returns null when there is no such leaf or
    /// more than one (callers fail closed).
    /// </summary>
    public NamedTableSource? FindLeaf(TableAccessTarget target)
    {
        var matches = Leaves.OfType<NamedTableSource>().Where(l =>
            string.Equals(l.Name.Parts[^1].Value, target.TableName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Alias?.Value ?? string.Empty, target.Alias ?? string.Empty, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// True when the subtree rooted at <paramref name="root"/> is the null-supplying side of an outer join with a source outside it (any side of a FULL
    /// join counts). A WHERE conjunct pushed into such a subtree would filter rows before the null extension. Fail closed: a root that is not part
    /// of the FROM clause counts as null-supplying.
    /// </summary>
    public static bool IsNullSuppliedFromOutside(TableSource? from, TableSource root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return from is null || Locate(from, root, nullSupplyingAbove: false) is not false;
    }

    private static bool? Locate(TableSource node, TableSource root, bool nullSupplyingAbove)
    {
        if (ReferenceEquals(node, root))
        {
            return nullSupplyingAbove;
        }

        if (node is not JoinedTableSource join)
        {
            return null;
        }

        bool leftSupplied = nullSupplyingAbove || join.Type is JoinType.RightOuter or JoinType.FullOuter;
        bool rightSupplied = nullSupplyingAbove || join.Type is JoinType.LeftOuter or JoinType.FullOuter;
        return Locate(join.Left, root, leftSupplied) ?? Locate(join.Right, root, rightSupplied);
    }

    private static void Walk(TableSource source, HashSet<TableSource> preserved, HashSet<TableSource> nullSupplying, List<TableSource> leaves)
    {
        if (source is not JoinedTableSource join)
        {
            leaves.Add(source);
            return;
        }

        var left = new List<TableSource>();
        var right = new List<TableSource>();
        Walk(join.Left, preserved, nullSupplying, left);
        Walk(join.Right, preserved, nullSupplying, right);
        leaves.AddRange(left);
        leaves.AddRange(right);

        switch (join.Type)
        {
            case JoinType.LeftOuter:
                preserved.UnionWith(left);
                nullSupplying.UnionWith(right);
                break;
            case JoinType.RightOuter:
                preserved.UnionWith(right);
                nullSupplying.UnionWith(left);
                break;
            case JoinType.FullOuter:
                preserved.UnionWith(left);
                preserved.UnionWith(right);
                nullSupplying.UnionWith(left);
                nullSupplying.UnionWith(right);
                break;
        }
    }
}

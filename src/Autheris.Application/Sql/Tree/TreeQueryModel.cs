using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Sql.Tree;

/// <summary>Comparison operators of a GraphQL <c>where</c> argument (G4).</summary>
public enum TreeFilterOperator
{
    Eq,
    Neq,
    Gt,
    Gte,
    Lt,
    Lte,
    In,
    NotIn,
    Contains,
    StartsWith,
    EndsWith,
    /// <summary>Value is a bool: true = IS NULL, false = IS NOT NULL.</summary>
    IsNull
}

/// <summary>Filter tree of a <c>where</c> argument. Values are CLR values and always bound as parameters.</summary>
public abstract record TreeFilter;

public sealed record TreeAndFilter(IReadOnlyList<TreeFilter> Items) : TreeFilter;

public sealed record TreeOrFilter(IReadOnlyList<TreeFilter> Items) : TreeFilter;

public sealed record TreeNotFilter(TreeFilter Item) : TreeFilter;

/// <summary>For <see cref="TreeFilterOperator.In"/>/<see cref="TreeFilterOperator.NotIn"/> the value is a list.</summary>
public sealed record TreeComparison(string Column, TreeFilterOperator Operator, object? Value) : TreeFilter;

public sealed record TreeOrder(string Column, bool Descending = false);

/// <summary>
/// One table level of a GraphQL selection: selected columns, nested relations, filter, order and paging.
/// <see cref="Limit"/> is per parent row for nested levels.
/// </summary>
public sealed record TreeQueryNode(TableIdentifier Table, IReadOnlyList<string> Columns)
{
    public IReadOnlyList<TreeRelationNode> Relations { get; init; } = [];
    public TreeFilter? Where { get; init; }
    public IReadOnlyList<TreeOrder> OrderBy { get; init; } = [];
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}

/// <summary>
/// A navigation from a parent level to a child level. <see cref="ResponseKey"/> is the GraphQL response name and the key
/// of the nested value in the JSON. <see cref="ParentColumns"/>[i] joins <see cref="ChildColumns"/>[i].
/// </summary>
public sealed record TreeRelationNode(
    string ResponseKey,
    IReadOnlyList<string> ParentColumns,
    IReadOnlyList<string> ChildColumns,
    bool IsList,
    TreeQueryNode Child);

/// <summary>Governance input for one table: catalog metadata, access decision and the enforced tenant filter.</summary>
public sealed record TreeTableAccess(
    TableMetadata Metadata,
    TableAccessDecision Decision,
    string? TenantColumn = null,
    string? TenantValue = null);

/// <summary>An HMAC-masked column that the gateway pseudonymizes after reading (SEC H-13: no key in SQL).</summary>
public sealed record TreeHmacColumn(IReadOnlyList<string> Path, string Column, MaskingRule Rule);

/// <summary>One statement that returns the whole result as a JSON array in a single text value.</summary>
public sealed record CompiledTreeQuery(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<TreeHmacColumn> HmacColumns);

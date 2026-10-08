namespace TrinoSqlEngine.Ast.Nodes;

using System.Collections.Generic;

public sealed record OrderByClause(IReadOnlyList<OrderByElement> Elements) : SqlNode;

public sealed record OrderByElement(
    Expression Expression,
    SortDirection Direction = SortDirection.Ascending,
    NullOrdering NullOrder = NullOrdering.Default) : SqlNode;

public enum SortDirection 
{ 
    Ascending, 
    Descending 
}

public enum NullOrdering 
{ 
    Default, 
    First, 
    Last 
}

public sealed record PaginationClause(
    Expression? Offset,
    Expression? Limit) : SqlNode;

/// <param name="GroupingExpressions">Plain grouping expressions.</param>
/// <param name="AdvancedElements">Wunsch 4: ROLLUP, CUBE and GROUPING SETS, emitted after the plain expressions (the
/// grouping sets form a cross product, so the order does not change the result).</param>
/// <param name="Distinct">Wunsch 4: <c>GROUP BY DISTINCT</c>.</param>
public sealed record GroupByClause(
    IReadOnlyList<Expression> GroupingExpressions,
    IReadOnlyList<GroupingElement>? AdvancedElements = null,
    bool Distinct = false) : SqlNode;

/// <summary>Wunsch 4: one ROLLUP/CUBE/GROUPING SETS element; each set is a list of expressions (may be empty in GROUPING SETS).</summary>
public sealed record GroupingElement(
    GroupingElementKind Kind,
    IReadOnlyList<IReadOnlyList<Expression>> Sets) : SqlNode;

public enum GroupingElementKind
{
    Rollup,
    Cube,
    GroupingSets
}

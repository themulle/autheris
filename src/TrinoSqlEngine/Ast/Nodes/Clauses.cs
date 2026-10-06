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

public sealed record GroupByClause(
    IReadOnlyList<Expression> GroupingExpressions) : SqlNode;

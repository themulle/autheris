namespace TrinoSqlEngine.Ast.Nodes;

using System.Collections.Generic;

public abstract record TableSource : SqlNode;

public sealed record NamedTableSource(
    SqlQualifiedName Name,
    SqlIdentifier? Alias) : TableSource;

public sealed record SubqueryTableSource(
    SelectStatement Subquery,
    SqlIdentifier Alias,
    IReadOnlyList<SqlIdentifier>? ColumnAliases = null) : TableSource;

public sealed record JoinedTableSource(
    TableSource Left,
    JoinType Type,
    TableSource Right,
    JoinCondition? Condition) : TableSource;

public sealed record LateralTableSource(
    SelectStatement Subquery,
    SqlIdentifier Alias,
    IReadOnlyList<SqlIdentifier>? ColumnAliases = null) : TableSource;

public enum JoinType
{
    Inner,
    LeftOuter,
    RightOuter,
    FullOuter,
    Cross,
    Natural
}

public abstract record JoinCondition : SqlNode;

public sealed record OnJoinCondition(Expression Predicate) : JoinCondition;

public sealed record UsingJoinCondition(IReadOnlyList<SqlIdentifier> Columns) : JoinCondition;

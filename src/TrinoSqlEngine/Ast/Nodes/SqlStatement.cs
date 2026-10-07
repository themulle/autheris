namespace TrinoSqlEngine.Ast.Nodes;

using System.Collections.Generic;

/// <summary>
/// Root node of a SQL statement.
/// </summary>
public abstract record SqlStatement : SqlNode;

/// <summary>
/// SELECT query statement with optional CTEs, set operations, and pagination.
/// </summary>
public sealed record SelectStatement(
    WithClause? With,
    QueryBody Body,
    OrderByClause? OrderBy,
    PaginationClause? Pagination) : SqlStatement;

/// <summary>
/// Abstract body of a read query: query specification, set operation, VALUES clause, or TABLE reference.
/// </summary>
public abstract record QueryBody : SqlNode;

/// <summary>
/// Standard SELECT specification (projections, FROM, WHERE, GROUP BY, HAVING).
/// </summary>
public sealed record QuerySpecification(
    bool Distinct,
    IReadOnlyList<SelectItem> Projections,
    TableSource? From,
    Expression? Where,
    GroupByClause? GroupBy,
    Expression? Having) : QueryBody;

/// <summary>
/// Set operation between two queries (UNION, INTERSECT, EXCEPT).
/// </summary>
public sealed record SetOperationQuery(
    QueryBody Left,
    SetOperator Operator,
    bool Distinct,
    QueryBody Right) : QueryBody;

public enum SetOperator
{
    Union,
    Intersect,
    Except
}

/// <summary>
/// Inline VALUES table as query body (e.g. VALUES (1, 'a'), (2, 'b')).
/// </summary>
public sealed record ValuesQueryBody(
    IReadOnlyList<RowValueExpression> Rows) : QueryBody;

/// <summary>
/// Direct TABLE specification (e.g. TABLE my_table).
/// </summary>
public sealed record TableQueryBody(
    SqlQualifiedName TableName) : QueryBody;

/// <summary>
/// DELETE statement targeting a physical table with an optional WHERE clause.
/// </summary>
public sealed record DeleteStatement(
    NamedTableSource TargetTable,
    Expression? Where) : SqlStatement;

/// <summary>
/// UPDATE statement targeting a physical table with assignments and an optional WHERE clause.
/// </summary>
public sealed record UpdateStatement(
    NamedTableSource TargetTable,
    IReadOnlyList<UpdateAssignment> Assignments,
    Expression? Where) : SqlStatement;

/// <summary>
/// Column assignment in an UPDATE statement (col = expr).
/// </summary>
public sealed record UpdateAssignment(
    SqlIdentifier Column,
    Expression Value) : SqlNode;

/// <summary>
/// INSERT statement targeting a physical table with optional column list and query source.
/// </summary>
public sealed record InsertStatement(
    NamedTableSource TargetTable,
    IReadOnlyList<SqlIdentifier>? Columns,
    QueryBody Source) : SqlStatement;

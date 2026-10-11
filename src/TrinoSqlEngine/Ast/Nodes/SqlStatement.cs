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

/// <summary>
/// MERGE (grammar: SqlBase.g4 #merge / mergeCase). The grammar has no <c>WHEN NOT MATCHED BY SOURCE</c> and no <c>BY TARGET</c>
/// form, and the clause types below are closed (SEC-ADG-08 a): a new clause type needs a security review.
/// </summary>
public sealed record MergeStatement(
    NamedTableSource Target,
    TableSource Source,
    Expression On,
    IReadOnlyList<MergeClause> Clauses) : SqlStatement;

/// <summary>One WHEN clause of a MERGE. The set of subtypes is closed.</summary>
public abstract record MergeClause(Expression? Condition) : SqlNode;

/// <summary>WHEN MATCHED [AND condition] THEN UPDATE SET column = value, ...</summary>
public sealed record MergeUpdateClause(Expression? Condition, IReadOnlyList<UpdateAssignment> Assignments) : MergeClause(Condition);

/// <summary>WHEN MATCHED [AND condition] THEN DELETE</summary>
public sealed record MergeDeleteClause(Expression? Condition) : MergeClause(Condition);

/// <summary>WHEN NOT MATCHED [AND condition] THEN INSERT [(columns)] VALUES (values)</summary>
public sealed record MergeInsertClause(Expression? Condition, IReadOnlyList<SqlIdentifier>? Columns, IReadOnlyList<Expression> Values) : MergeClause(Condition);

namespace TrinoSqlEngine.Analysis;

using System;
using System.Collections.Generic;

public enum SqlStatementType
{
    Select,
    Insert,
    Update,
    Delete,
    Ddl,
    Other
}

public readonly record struct TableAccessTarget(
    string? Catalog,
    string? Schema,
    string TableName,
    string? Alias,
    string FullName,
    bool SchemaQuoted = false,
    bool TableNameQuoted = false);

public readonly record struct JoinColumnReference(string? TableOrAlias, string ColumnName);
public readonly record struct FilterColumnReference(string? TableOrAlias, string ColumnName);
public readonly record struct CrossJoinTarget(
    string? LeftTableOrAlias,
    string? RightTableOrAlias,
    bool IsUnknownCardinality = false);

public sealed record SqlQueryMetadata(
    SqlStatementType StatementType,
    IReadOnlyList<TableAccessTarget> ReferencedTables,
    IReadOnlyList<string> ProjectedColumns,
    int JoinCount,
    int MaxSubqueryDepth,
    bool HasExplicitLimit,
    long? ExplicitLimitValue,
    IReadOnlyList<string>? FunctionCalls = null,
    IReadOnlyList<string>? TableFunctionCalls = null,
    bool HasSessionProperties = false,
    bool HasInlineFunctionDefinitions = false,
    IReadOnlyList<JoinColumnReference>? JoinColumnReferences = null,
    IReadOnlyList<FilterColumnReference>? FilterColumnReferences = null)
{
    /// <summary>
    /// #322: the columns of window<c>PARTITION BY</c> and <c>ORDER BY</c> (OVER clauses and named windows). Kept apart from
    /// <see cref="FilterColumnReferences"/> so a masked or denied column in a window answers with its own code; every consumer that
    /// needs "all referenced columns" must read both lists.
    /// </summary>
    public IReadOnlyList<FilterColumnReference>? WindowColumnReferences { get; init; }

    /// <summary>Issue #277: the statement contains a <c>WITH RECURSIVE</c> (any nesting level).</summary>
    public bool HasRecursiveCte { get; init; }

    /// <summary>
    /// Issue #277: a CTE of a WITH without RECURSIVE names itself in its own body. The name is still reported in
    /// <see cref="ReferencedTables"/> (unchanged for other consumers); callers reject the statement before any existence-dependent answer.
    /// </summary>
    public bool HasNonRecursiveSelfReference { get; init; }

    public int CrossJoinCount { get; init; }
    public int SetOperationCount { get; init; }
    public bool HasRootOrderBy { get; init; }
    public bool HasRootOffset { get; init; }
    public IReadOnlyList<CrossJoinTarget>? CrossJoins { get; init; }
}

public interface ISqlQueryAnalyzer
{
    SqlQueryMetadata Analyze(SqlBaseParser.SingleStatementContext statementContext);
}

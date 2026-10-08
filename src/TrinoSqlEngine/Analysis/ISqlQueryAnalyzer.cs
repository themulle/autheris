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

public sealed record SqlQueryMetadata(
    SqlStatementType StatementType,
    IReadOnlyList<TableAccessTarget> ReferencedTables,
    IReadOnlyList<string> ProjectedColumns,
    int JoinCount,
    int MaxSubqueryDepth,
    bool HasExplicitLimit,
    long? ExplicitLimitValue,
    IReadOnlySet<string>? JoinConditionColumns = null,
    IReadOnlyList<string>? FunctionCalls = null,
    IReadOnlyList<string>? TableFunctionCalls = null,
    bool HasSessionProperties = false,
    bool HasInlineFunctionDefinitions = false,
    IReadOnlyList<JoinColumnReference>? JoinColumnReferences = null);

public interface ISqlQueryAnalyzer
{
    SqlQueryMetadata Analyze(SqlBaseParser.SingleStatementContext statementContext);
}

namespace TrinoSqlEngine.Ast.Nodes;

using System.Collections.Generic;

public sealed record WithClause(
    bool IsRecursive,
    IReadOnlyList<CommonTableExpression> Ctes) : SqlNode;

public sealed record CommonTableExpression(
    SqlIdentifier Name,
    IReadOnlyList<SqlIdentifier>? ColumnAliases,
    SelectStatement Query) : SqlNode;

namespace TrinoSqlEngine.Ast.Visitors;

using System;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Public facade for expanding list parameters in AST statements for DuckDB and cross-source queries (REQ-SQL-128 / Issue #131).
/// </summary>
public static class ClientListExpansion
{
    public static SqlStatement Apply(SqlStatement statement, ClientListShapes shapes, DialectCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(shapes);

        if (shapes.IsEmpty)
        {
            return statement;
        }

        capabilities ??= DialectCapabilityTable.Default.Get(TargetSqlDialect.DuckDb);
        var expander = new ClientListExpander(shapes, capabilities);
        return (SqlStatement)expander.Visit(statement);
    }
}

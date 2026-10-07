namespace TrinoSqlEngine.Ast.Generators;

using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Target SQL dialect code emitter interface.
/// </summary>
public interface ISqlDialectGenerator
{
    TargetSqlDialect TargetDialect { get; }
    int MaxParameterBudget { get; }
    void GenerateSql(SqlStatement statement, ref ValueStringBuilder builder, SqlEmitterContext context);
    string GenerateSql(SqlStatement statement);
}
